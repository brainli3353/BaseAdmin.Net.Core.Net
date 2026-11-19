// 批 S-G 纵深期·数据底座/二级索引(落码臂 B2a):
// 本轮给 13 个实体加 [SugarIndex] 非唯一二级索引(17 条:RBAC 关联列 + 日志族 CreateTime 降序),
// 本测试提供两层实证:
// ① 机制证明(探针表,不碰存量表):裸 Ado 建无索引探针表 → CodeFirst.InitTables(带 [SugarIndex] 的探针实体)
//    → 查 pg_indexes 断言索引已建——证明当前 SqlSugar 版本 InitTables 会按 SugarIndex 为"表"建索引;
// ② 真实表覆盖:InitTables 幂等重建 13 个本轮实体(dev 库表结构即由实体 CodeFirst 建出,理论上只会补索引),
//    断言本轮 17 个索引名全部存在于 pg_indexes(缺失名逐个列全,便于排障)——实证"既有表补索引是否真生效"。
// PG 不可达或 InitTables 失败 → Assert.Fail 明示,禁止静默跳过(沙箱 PG 黑洞同口径)。
// 裸客户端口径与 SqlSugarSetup.BuildPgConnectionConfig 一致(PG 标识符精确大小写三开关关闭自动小写,
// InitKeyType.Attribute)——否则 InitTables 对已存在 Pascal 列名表误判列名;只读对齐装配层,不改装配层。
using AdminCore.Core.Entities;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchSGIndexCoverageTest
{
    private readonly ITestOutputHelper _output;

    public BatchSGIndexCoverageTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 BatchUXDateRangeTest/BatchSGPageSizeClampTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    // 本轮 17 个索引名(断言基线;与 src/Core/Entities 各实体 [SugarIndex] 一一对应)
    private static readonly string[] ExpectedIndexes =
    {
        "i_sys_user_account", "i_sys_user_orgid", "i_sys_user_status",
        "i_sys_user_role_userid", "i_sys_user_role_roleid",
        "i_sys_role_menu_roleid", "i_sys_role_menu_menuid",
        "i_sys_menu_pid", "i_sys_org_pid", "i_sys_notice_user_userid",
        "i_sys_log_op_createtime", "i_sys_log_login_createtime", "i_sys_log_ex_createtime",
        "i_sys_log_http_createtime", "i_sys_log_msg_createtime", "i_sys_log_diff_createtime",
        "i_sys_job_log_createtime",
    };

    // 裸 Ado 客户端(不经仓储/服务):MoreSettings 复刻 SqlSugarSetup.PgExactCaseSettings 口径(装配层 internal 不可见)
    private static SqlSugarClient NewDb()
        => new(new ConnectionConfig
        {
            ConnectionString = DbConnectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                PgSqlIsAutoToLower = false,
                PgSqlIsAutoToLowerCodeFirst = false,
                PgSqlIsAutoToLowerSchema = false,
            },
        });

    // ─────────────────────────────────────────────────────────────
    // 用例1 机制证明:探针表(不碰存量表)——InitTables 按 [SugarIndex] 建索引
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例1_探针表_InitTables按SugarIndex建索引()
    {
        try
        {
            using var db = NewDb();

            // 裸 Ado 造无索引探针表(列形态与探针实体一致;先 DROP 防上轮残留)
            await db.Ado.ExecuteCommandAsync("DROP TABLE IF EXISTS s_g_index_probe");
            await db.Ado.ExecuteCommandAsync(
                "CREATE TABLE s_g_index_probe(id bigint primary key, \"CreateTime\" timestamp)");

            // CodeFirst 建索引:探针实体带 [SugarIndex](非唯一,CreateTime 降序,同日志族形态)
            db.CodeFirst.InitTables(typeof(SGIndexProbe));

            var names = await db.Ado.SqlQueryAsync<string>(
                "select indexname from pg_indexes where schemaname='public' and tablename='s_g_index_probe'");
            _output.WriteLine($"[探针] s_g_index_probe 实际索引: {string.Join(", ", names)}");
            Assert.Contains("i_s_g_index_probe_createtime", names);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // 排除断言异常(XunitException)被吞——只把环境/机制失败转成明示 Fail
            Assert.Fail($"用例1 失败:PG 不可达或 InitTables 未按 [SugarIndex] 建索引: {ex}");
        }
        finally
        {
            try
            {
                using var clean = NewDb();
                await clean.Ado.ExecuteCommandAsync("DROP TABLE IF EXISTS s_g_index_probe");
            }
            catch (Exception ex)
            {
                // 清理失败不掩主断言结果;残留表由下轮开头 DROP IF EXISTS 兜底
                _output.WriteLine($"[探针清理失败(下轮 DROP IF EXISTS 兜底)]: {ex.Message}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 用例2 真实表覆盖:13 个实体幂等 InitTables → 17 个索引名全部存在
    // (断言在 try 内:Assert.Fail 不带 [DoesNotReturn],catch 流出会引 CS0165;XunitException 穿透 catch 保留断言语义)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例2_真实表覆盖_本轮17索引全部存在()
    {
        try
        {
            using var db = NewDb();

            var before = (await db.Ado.SqlQueryAsync<long>(
                "select count(*) from pg_indexes where schemaname='public'"))[0];
            _output.WriteLine($"[前] pg_indexes(public) 总数 = {before}");

            // 幂等:dev 库表结构即由实体 CodeFirst 建出,重跑只会补缺列/缺索引,不动数据
            db.CodeFirst.InitTables(
                typeof(SysUser), typeof(SysUserRole), typeof(SysRoleMenu), typeof(SysMenu),
                typeof(SysOrg), typeof(SysNoticeUser),
                typeof(SysLogOp), typeof(SysLogLogin), typeof(SysLogEx), typeof(SysLogHttp),
                typeof(SysLogMsg), typeof(SysLogDiff), typeof(SysJobLog));

            var names = await db.Ado.SqlQueryAsync<string>(
                "select indexname from pg_indexes where schemaname='public'");
            _output.WriteLine($"[后] pg_indexes(public) 总数 = {names.Count}");
            _output.WriteLine($"[增量] {names.Count - before}");

            var missing = ExpectedIndexes.Where(e => !names.Contains(e)).ToList();
            Assert.True(
                missing.Count == 0,
                $"[SugarIndex] 真实表覆盖缺 {missing.Count} 个索引: {string.Join(", ", missing)}" +
                $"(既有表补索引是否生效见读数;若 InitTables 只建表不补存量表索引,须父层裁决改变本批形态)");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // 排除断言异常(XunitException)被吞——只把环境/机制失败转成明示 Fail
            Assert.Fail($"用例2 失败:PG 不可达或 InitTables(13 实体)失败: {ex}");
        }
    }

    /// <summary>探针实体(映射 s_g_index_probe;类定义放本测试文件,不进实体目录):两列与裸 Ado 建表一致。</summary>
    [SugarTable("s_g_index_probe")]
    [SugarIndex("i_s_g_index_probe_createtime", nameof(CreateTime), OrderByType.Desc)]
    private sealed class SGIndexProbe
    {
        [SugarColumn(IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? CreateTime { get; set; }
    }
}
