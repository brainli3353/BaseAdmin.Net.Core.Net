namespace AdminCore.Core
{
    /// <summary>
    /// SqlSugar 常量约定(写死,后补多租户不重构的前提;对齐 baseadmin 取值)。
    /// </summary>
    public static class SqlSugarConst
    {
        /// <summary>主库 ConfigId(系统表/无特性实体落库)。</summary>
        public const string MainConfigId = "main";

        /// <summary>日志库 ConfigId([LogTable] 实体落库;连接未配置时回落主库)。</summary>
        public const string LogConfigId = "log";

        /// <summary>默认租户 Id(无租户 Claim 时审计/租户字段缺省值;对齐 baseadmin 1300000000001)。</summary>
        public const long DefaultTenantId = 1300000000001;
    }
}
