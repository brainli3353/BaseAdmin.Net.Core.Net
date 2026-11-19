using System.Collections.Concurrent;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 租户库注册表(批 AB 多租户库级隔离 C2):把"租户 Id → 独立库连接"的解析、解密、
    /// 懒加载注册与失效收敛为单点,供仓储选库(<see cref="SqlSugarSetup.SelectConfigIdFor"/>)、
    /// 请求装配(<c>PermissionMiddleware</c>)与租户库初始化(建表/种子)共用。
    /// </summary>
    /// <remarks>
    /// 机制口径(对齐 AdminNetPro 租户库,零 Furion):
    /// ① ConfigId 恒为租户 Id 的字符串形式(约定见 <see cref="SqlSugarConst"/>),默认租户/无租户上下文 → 主库 main;
    /// ② 租户库连接串存 <see cref="SysTenant.Connection"/>,<c>DbSettings:EnableConnEncrypt=true</c> 时为密文
    ///    (本仓 AES 口径 <see cref="CryptogramHelper"/>;参照仓为 SM2 解密,算法差异见批 AB 裁决留痕);
    /// ③ 懒加载:首次用到某租户库时经注册委托建"逐连接独立单连接 scope"(与 main/log 同口径:AOP/软删过滤/
    ///    二级缓存/审计填充全一致,见 <see cref="SqlSugarSetup.ConnectionClients"/> WHY),注册后常驻;
    /// ④ fail-loud:租户**配置了**连接串但解析失败(密文非法/密钥不匹配)或库级场景要求独立库而未配置 → 抛业务异常;
    ///    <b>绝不静默回落主库</b>(静默回落 = 租户数据落错库且无感知,属数据隔离失效)。反向的"未配置连接串"
    ///    在**请求路径**视为该租户不分库(opt-in 未开启)→ 落主库(基座期行为零变化),两种语义见 EnsureConnectionAsync。
    /// WHY 静态注册表而非 Scoped 服务:选库消费点含静态单点(建表/种子/仓储构造),与
    /// SqlSugarSetup.ConnectionClients 同生命周期(进程级);容器相关依赖(配置/解密器/日志/注册委托)
    /// 由 AddSqlSugar 的工厂闭包在首次解析时注入,零静态初始化顺序依赖(同批 P/R 闭包捕获口径)。
    /// 多容器(单测并发)下注册表指向最后构建的容器,与 ConnectionClients 既有口径一致。
    /// </remarks>
    public static class TenantDbRegistry
    {
        /// <summary>租户库连接串是否加密(AddSqlSugar 从 DbSettings:EnableConnEncrypt 读取,缺省 false=明文)。</summary>
        internal static bool EnableConnEncrypt { get; private set; }

        /// <summary>解密器(AddSqlSugar 工厂闭包注入;容器未注册 CryptogramHelper 时为 null)。</summary>
        internal static CryptogramHelper? Cryptogram { get; private set; }

        /// <summary>日志(懒加载/失效留痕;可为 null——最小宿主未注册日志时静默)。</summary>
        internal static ILogger? Logger { get; private set; }

        /// <summary>
        /// 租户连接注册委托(configId, connectionString) → 已配置 AOP 的独立单连接 scope;
        /// 由 AddSqlSugar 工厂闭包赋值(null=容器未装配 SqlSugar,此时任何租户库解析都应 fail-loud)。
        /// </summary>
        internal static Func<string, string, ISqlSugarClient>? ConnectionRegistrar { get; private set; }

        /// <summary>懒加载并发闸(同一租户并发解析只建一次连接;不同租户互不阻塞)。</summary>
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> Gates = new();

        /// <summary>
        /// 「不分库租户」负面登记(批 AB 修订):<see cref="EnsureConnectionAsync"/> 在 required=false 下判定
        /// "该租户未配置连接串 / 租户行缺失 → 落主库"时登记本表;
        /// <see cref="SqlSugarSetup.SelectConfigIdFor"/> 据此把该租户的 [Tenant] 实体路由主库。
        /// </summary>
        /// <remarks>
        /// WHY 必须有这层:请求路径的"未配置=落主库"判定发生在中间件(需异步查库),
        /// 而**选库**发生在仓储构造(同步静态方法,无法查库)——后者只能读判定结果。
        /// 没有负面登记时,存量未分库租户(Connection 恒 null)一旦出现 [Tenant] 业务实体,
        /// 仓储构造会按"ConfigId=租户 Id 未注册"fail-loud → 所有非默认租户请求 500
        /// (批 AB 测试实测钉出:用例 2/3 正是该矛盾的两侧)。
        /// 失效:<see cref="Invalidate"/> 清除登记(连接串变更/租户删除后下次 Ensure 重新判定)。
        /// 进程级静态(与 ConnectionClients 同生命周期);多容器(单测)口径同既有约定。
        /// </remarks>
        private static readonly ConcurrentDictionary<long, byte> MainDatabaseTenants = new();

        /// <summary>该租户是否已判定为「不分库(落主库)」:供选库单点判定(见 MainDatabaseTenants WHY)。</summary>
        public static bool IsMainDatabaseTenant(long tenantId) => MainDatabaseTenants.ContainsKey(tenantId);

        /// <summary>
        /// 装配入口(仅 SqlSugarSetup.AddSqlSugar 工厂调用):注入容器侧依赖与注册委托。
        /// </summary>
        internal static void Init(
            bool enableConnEncrypt,
            CryptogramHelper? cryptogram,
            ILogger? logger,
            Func<string, string, ISqlSugarClient> registrar)
        {
            EnableConnEncrypt = enableConnEncrypt;
            Cryptogram = cryptogram;
            Logger = logger;
            ConnectionRegistrar = registrar;
        }

        /// <summary>租户 Id → 目标库 ConfigId(恒为租户 Id 字符串;默认租户 = 主库)。</summary>
        public static string ConfigIdForTenant(long tenantId)
            => tenantId == SqlSugarConst.DefaultTenantId ? SqlSugarConst.MainConfigId : tenantId.ToString();

        /// <summary>该租户库连接是否已注册(默认租户恒 true——主库)。</summary>
        public static bool IsRegistered(long tenantId)
            => ConfigIdForTenant(tenantId) is var configId
               && SqlSugarSetup.ConnectionClients.ContainsKey(configId);

        /// <summary>
        /// 确保租户库连接已注册(懒加载,幂等):默认租户直接返回 main;
        /// 否则:已注册 → 返回 ConfigId;未注册 → 查 <see cref="SysTenant"/> 取连接串(按开关解密)→ 注册。
        /// 语义口径(批 AB C5):库级隔离是**按租户 opt-in**——租户未配置连接串 = 该租户落主库(基座期行为,
        /// 既有租户零变化);而"配置了连接串却解析失败"(密文非法/密钥不匹配/租户行缺失)属配置错误 → fail-loud,
        /// 绝不静默回落主库(否则租户数据落错库且无感知)。
        /// </summary>
        /// <param name="tenantId">租户 Id(= TenantId Claim 值)。</param>
        /// <param name="required">
        /// true = 该租户**必须**具备独立库(初始化租户库/建表场景,未配置即异常);
        /// false = 请求路径口径(未配置/租户行不存在 → 返回主库)。
        /// </param>
        /// <returns>目标库 ConfigId(租户库 Id 字符串或 main)。</returns>
        public static async Task<string> EnsureConnectionAsync(long tenantId, bool required = false)
        {
            var configId = ConfigIdForTenant(tenantId);
            if (configId == SqlSugarConst.MainConfigId || SqlSugarSetup.ConnectionClients.ContainsKey(configId))
            {
                return configId;
            }

            var gate = Gates.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                // 双检:等锁期间可能已由并发请求注册(锁内不再查库,零额外开销)
                if (SqlSugarSetup.ConnectionClients.ContainsKey(configId))
                {
                    return configId;
                }

                var mainClient = SqlSugarSetup.ConnectionClients.TryGetValue(SqlSugarConst.MainConfigId, out var main)
                    ? main
                    : throw new InvalidOperationException("主库连接未注册:无法解析租户库配置");

                // 租户登记行不受行级租户过滤(SysTenant 为平台级 EntityBase,见实体 remarks),主库直查即可
                var tenant = await mainClient.Queryable<SysTenant>().Where(t => t.Id == tenantId).FirstAsync();
                if (tenant is null)
                {
                    if (!required)
                    {
                        // 租户行缺失(登录链已拒):请求路径回落主库,不阻断业务;登记负面判定供选库单点(见 MainDatabaseTenants)
                        MainDatabaseTenants[tenantId] = 1;
                        return SqlSugarConst.MainConfigId;
                    }

                    throw AppException.With(ErrorCodes.TenantDbTenantNotFound, "tenantId", tenantId);
                }

                if (string.IsNullOrWhiteSpace(tenant.Connection))
                {
                    if (!required)
                    {
                        // 未配置连接串 = 该租户不分库(opt-in 未开启):落主库,行级租户隔离照旧生效;
                        // 登记负面判定——否则仓储构造(同步选库)会按"租户 Id 未注册"fail-loud 抛异常
                        MainDatabaseTenants[tenantId] = 1;
                        return SqlSugarConst.MainConfigId;
                    }

                    throw AppException.With(ErrorCodes.TenantDbConnectionMissing, "name", tenant.Name ?? tenantId.ToString());
                }

                var registrar = ConnectionRegistrar
                    ?? throw new InvalidOperationException("SqlSugar 未装配:无法注册租户库连接(AddSqlSugar 未调用)");

                var connectionString = DecryptConnection(tenant.Connection!);
                registrar(configId, connectionString);
                Logger?.LogInformation("租户库连接已注册: 租户={TenantId} 库标识={ConfigId} 库名={DbName}",
                    tenantId, configId, tenant.DbName ?? "(未登记)");
                return configId;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 失效租户库连接(连接串变更/租户删除时调用):移除注册表条目,下次解析重新建连接。
        /// 已注册的旧 scope 由 GC 回收(独立单连接 scope 无外部持有)。
        /// </summary>
        public static void Invalidate(long tenantId)
        {
            var configId = ConfigIdForTenant(tenantId);
            if (configId == SqlSugarConst.MainConfigId)
            {
                return; // 默认租户恒主库,无可失效
            }

            // 负面登记一并清除:连接串变更/新配置后,下次 Ensure 重新判定该租户是否分库
            MainDatabaseTenants.TryRemove(tenantId, out _);

            if (SqlSugarSetup.ConnectionClients.TryRemove(configId, out var client))
            {
                (client as IDisposable)?.Dispose();
                Logger?.LogInformation("租户库连接已失效: 租户={TenantId} 库标识={ConfigId}", tenantId, configId);
            }
        }

        /// <summary>
        /// 解析租户的连接串(按开关解密,校验非空):供库级隔离的**前置步骤**(建库/连通性判定)与
        /// 懒加载注册共用同一解密口径(约定 #11 单点,防两处解密漂移)。
        /// </summary>
        public static string ResolveConnectionString(SysTenant tenant)
        {
            if (string.IsNullOrWhiteSpace(tenant.Connection))
            {
                throw AppException.With(ErrorCodes.TenantDbConnectionMissing, "name", tenant.Name ?? tenant.Id.ToString());
            }

            return DecryptConnection(tenant.Connection!);
        }

        /// <summary>
        /// 连接串 → 库名(解析不出返回 null):识别 <c>Database=</c>/<c>Initial Catalog=</c>(忽略大小写与空格)。
        /// </summary>
        public static string? DatabaseNameOf(string connectionString)
        {
            foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = segment.Split('=', 2);
                if (kv.Length != 2)
                {
                    continue;
                }

                var key = kv[0].Trim();
                if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                {
                    var value = kv[1].Trim();
                    return value.Length == 0 ? null : value;
                }
            }

            return null;
        }

        /// <summary>
        /// 目标库是否已存在(经主库连接查 pg_database;库名走参数,无标识符拼接)。
        /// </summary>
        public static async Task<bool> DatabaseExistsAsync(ISqlSugarClient mainClient, string databaseName)
        {
            var names = await mainClient.Ado.SqlQueryAsync<string>(
                "SELECT datname FROM pg_database WHERE datname = @name",
                new SugarParameter("@name", databaseName));
            return names.Count > 0;
        }

        /// <summary>
        /// 需要时创建租户库(幂等):库已存在 → 返回 false 不动作;不存在 → CREATE DATABASE 并返回 true。
        /// 库名白名单校验(仅字母/数字/下划线,首字符非数字)防标识符注入——CREATE DATABASE 不支持参数化;
        /// 权限不足等失败 → fail-loud(AppException,提示 DBA 预建),不静默继续(否则后续建表必然失败且原因难查)。
        /// </summary>
        public static async Task<bool> EnsureDatabaseAsync(ISqlSugarClient mainClient, string databaseName)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                throw AppException.With(ErrorCodes.TenantDbNameInvalid, "dbName", databaseName);
            }

            if (await DatabaseExistsAsync(mainClient, databaseName))
            {
                return false;
            }

            try
            {
                await mainClient.Ado.ExecuteCommandAsync($"CREATE DATABASE \"{databaseName}\"");
                Logger?.LogInformation("租户库已创建: {Database}", databaseName);
                return true;
            }
            catch (Exception ex)
            {
                throw AppException.Of(ErrorCodes.TenantDbCreateFailed, new Dictionary<string, object?> { ["dbName"] = databaseName, ["reason"] = ex.Message });
            }
        }

        /// <summary>
        /// 连接串解密(开关口径):EnableConnEncrypt=true → AES 解密(密文非法/密钥不匹配 → 明确异常);
        /// false → 原样返回明文(开发/测试友好,生产建议开启)。
        /// </summary>
        private static string DecryptConnection(string raw)
        {
            if (!EnableConnEncrypt)
            {
                return raw;
            }

            var cryptogram = Cryptogram
                ?? throw AppException.Of(ErrorCodes.TenantDbEncryptHelperMissing);

            try
            {
                return cryptogram.Decrypt(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                // 不泄露密文与密钥材料;失败原因对运维可检索(密文格式非法 / 密钥不匹配)
                throw AppException.Of(ErrorCodes.TenantDbDecryptFailed);
            }
        }
    }
}