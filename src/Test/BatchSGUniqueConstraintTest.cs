// 批 S-G B2b(真实 PG,直连 dev 库;PG 不可达 Assert.Fail 明示禁跳过——本批是约束落地验证,跳过=假绿):
// ① 幂等:EnsureIndexesAsync 连跑两遍 ⇒ 第二遍全部"已存在"、不报错;pg_indexes 七索引齐且 indexdef 含 UNIQUE 与 WHERE;
// ② 并发唯一性实证:sys_config(实读最安全:Code NOT NULL、无 TenantId 列、表小)同 Code 两行未删,
//    第二行 insert 抛 23505(穿透包装层取 PostgresException.SqlState),映射器对真实库异常同样命中 common.conflict;
// ③ 零库:DbConflictMapper.TryMapUniqueViolation 对构造的 PostgresException("23505") 命中,其余不命中。
// 探针纪律:finally 物理删探针行(按探针 Code,不触碰存量数据);
// WHY 直用 SqlSugarClient(不经 AddSqlSugar 全家桶):本批只测 Ado 面的 DDL/insert/约束,最小装置依赖面最小。
using AdminCore.Core.Exceptions;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGUniqueConstraintTest
{
    // 开发库连接串(批 S-G B2b 指定;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DowngradeForbidden = "PostgreSQL 不可达——本批是唯一约束落地验证,禁止冒充通过,须在可达环境实跑";

    // 七个部分唯一索引名(与 SchemaIntegrityInitializer.Specs 同源;断言 pg_indexes 用)
    private static readonly string[] IndexNames =
    [
        "u_sys_user_account",
        "u_sys_role_code",
        "u_sys_pos_code",
        "u_sys_dict_type_code",
        "u_sys_config_code",
        "u_sys_open_access_accesskey",
    ];

    // ─────────────────────────────────────────────────────────────
    // 用例1:EnsureIndexesAsync 两遍幂等 + pg_indexes 形状断言
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例1_EnsureIndexes两遍幂等_pg_indexes七索引含UNIQUE与WHERE()
    {
        var db = ReachableDbOrFail();
        try
        {
            // 第一遍:七索引全部落地(新建或已存在;dev 库审计重复计数 0,不应有跳过)
            var first = await SchemaIntegrityInitializer.EnsureIndexesAsync(db, NullLogger.Instance);
            Assert.Empty(first.SkippedIndexes);
            Assert.Equal(6, first.CreatedIndexes.Count + first.AlreadyExistingIndexes.Count);

            // 第二遍:全部"已存在"、零新建 ⇒ 幂等
            var second = await SchemaIntegrityInitializer.EnsureIndexesAsync(db, NullLogger.Instance);
            Assert.Empty(second.CreatedIndexes);
            Assert.Equal(6, second.AlreadyExistingIndexes.Count);
            Assert.Empty(second.SkippedIndexes);

            // pg_indexes 实读:七索引名齐,且 indexdef 含 UNIQUE(唯一索引)与 WHERE(部分索引=软删语义)
            var nameList = string.Join(",", IndexNames.Select(n => $"'{n}'"));
            var defs = await db.Ado.SqlQueryAsync<string>(
                $"SELECT indexdef FROM pg_indexes WHERE schemaname = current_schema() AND indexname IN ({nameList})");
            Assert.Equal(6, defs.Count);
            foreach (var def in defs)
            {
                Assert.Contains("UNIQUE", def);
                // PG 会把谓词规范化成 WHERE ("IsDelete" = false)(带空格/括号)⇒ 断言按语义片段而非字面量
                Assert.Contains("WHERE", def);
                Assert.Contains("IsDelete", def);
            }
        }
        finally
        {
            db.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 用例2:并发唯一性实证——同 Code 两行未删 ⇒ 第二行抛 23505
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例2_同Code两行未删_第二行抛23505并命中冲突码()
    {
        var db = ReachableDbOrFail();
        // 探针 Code(Guid 串,不撞存量);探针 id 自造唯一(long 正值,PG bigint 范围内)
        var probeCode = $"sgb2b{Guid.NewGuid():N}"[..24];
        var id1 = NewProbeId();
        var id2 = NewProbeId();
        try
        {
            // 确保约束就位(与用例1 顺序无关:EnsureIndexes 幂等,已存在即跳过)
            var ensure = await SchemaIntegrityInitializer.EnsureIndexesAsync(db, NullLogger.Instance);
            Assert.Contains("u_sys_config_code",
                ensure.CreatedIndexes.Concat(ensure.AlreadyExistingIndexes));

            // 第一行:插入成功
            await db.Ado.ExecuteCommandAsync(ProbeInsert(id1, probeCode, "first"));

            // 第二行:同 Code 未删 ⇒ 撞部分唯一索引 u_sys_config_code
            var conflict = await Assert.ThrowsAnyAsync<Exception>(
                () => db.Ado.ExecuteCommandAsync(ProbeInsert(id2, probeCode, "second")));

            // 实证 SqlState=23505(穿透 SqlSugar 包装层找 PostgresException)
            var pg = FindPostgresException(conflict);
            Assert.NotNull(pg);
            Assert.Equal("23505", pg!.SqlState);

            // 映射器对真实库抛出的异常同样命中业务冲突码(与用例3 的构造路径互补)
            Assert.True(DbConflictMapper.TryMapUniqueViolation(conflict, out var errCode));
            Assert.Equal(ErrorCodes.Conflict, errCode);
        }
        finally
        {
            // 探针纪律:物理删本探针两行(按探针 Code 精确删,不触碰存量数据)
            await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_config WHERE \"Code\"='{probeCode}'");
            db.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 用例3(零库):冲突映射——23505 命中 common.conflict,其余不命中
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void 用例3_冲突映射_23505命中其余不命中()
    {
        // 直接构造 PostgresException(message, severity, invariantSeverity, sqlState)
        var pg23505 = new PostgresException(
            "duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");
        Assert.True(DbConflictMapper.TryMapUniqueViolation(pg23505, out var hit));
        Assert.Equal(ErrorCodes.Conflict, hit); // common.conflict
        Assert.Equal("common.conflict", hit);

        // 包装一层(真实管道形态:封装层包着 PostgresException)仍命中
        var wrapped = new InvalidOperationException("SqlSugar 执行失败", pg23505);
        Assert.True(DbConflictMapper.TryMapUniqueViolation(wrapped, out var hit2));
        Assert.Equal(ErrorCodes.Conflict, hit2);

        // 非 23505 的 PostgresException ⇒ 不命中
        var pg42P01 = new PostgresException("relation does not exist", "ERROR", "ERROR", "42P01");
        Assert.False(DbConflictMapper.TryMapUniqueViolation(pg42P01, out var miss1));
        Assert.Equal(string.Empty, miss1);

        // 与 DB 无关的异常 ⇒ 不命中
        Assert.False(DbConflictMapper.TryMapUniqueViolation(
            new InvalidOperationException("无关异常"), out var miss2));
        Assert.Equal(string.Empty, miss2);
    }

    // ─────────────────────────────────────────────────────────────
    // 装置与探针工具
    // ─────────────────────────────────────────────────────────────

    /// <summary>创建直连 dev 库的 SqlSugar 客户端(最小装置:IsAutoCloseConnection,每命令独立连接)。</summary>
    private static ISqlSugarClient ReachableDbOrFail()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail(DowngradeForbidden);
        }

        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = DbConnectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });
    }

    /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchSGConsistencyTest 口径)。</summary>
    private static bool IsPostgresReachable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            client.ConnectAsync("127.0.0.1", 5432, timeout.Token).AsTask().GetAwaiter().GetResult();
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>探针配置行 INSERT(Code/Value/GroupCode/IsDelete/CreateTime/SysFlag 为该表 NOT NULL 面;id 无默认值显式给)。</summary>
    private static string ProbeInsert(long id, string code, string name) =>
        $"INSERT INTO sys_config (id, \"Name\", \"Code\", \"Value\", \"GroupCode\", \"IsDelete\", \"CreateTime\", \"SysFlag\") " +
        $"VALUES ({id}, '{name}', '{code}', 'probe', 'SG_B2B_PROBE', false, now(), false)";

    /// <summary>自造探针主键(毫秒高位 + 进程内递增低位,跨进程重跑亦不撞)。</summary>
    private static long NewProbeId() =>
        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Interlocked.Increment(ref _probeSeq) & 0xFFFFF;

    private static long _probeSeq;

    /// <summary>沿 InnerException 链找 PostgresException(穿透 SqlSugar 封装层)。</summary>
    private static PostgresException? FindPostgresException(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg)
            {
                return pg;
            }
        }

        return null;
    }
}
