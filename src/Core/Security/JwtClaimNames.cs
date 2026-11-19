namespace AdminCore.Core.Security
{
    /// <summary>
    /// JWT Claim 名约定(task_005 定稿):签发(AuthService)、读取(AuthController /me)、
    /// AOP 审计填充(SqlSugarSetup)三方共用,单点定义防字符串字面量漂移。
    /// </summary>
    public static class JwtClaimNames
    {
        /// <summary>用户雪花主键(十进制字符串;AOP 审计 CreateUserId/UpdateUserId 读取)。</summary>
        public const string UserId = "UserId";

        /// <summary>
        /// 登录账号(唯一且不可变 → 作审计"用户"字段;姓名 RealName 可重复/可变更,不适合作审计身份)。
        /// AOP 审计 CreateUserName/UpdateUserName 读取本 Claim。
        /// </summary>
        public const string Account = "Account";

        /// <summary>姓名(展示名;/me 端点返回,审计不读取)。</summary>
        public const string RealName = "RealName";

        /// <summary>账号类型:999 超级管理员 / 777 普通(task_007 扩展;M2 权限体系据此区分超管穿透)。</summary>
        public const string AccountType = "AccountType";

        /// <summary>Token 版本(task_007 扩展;JwtAuthHandler 与 Redis/DB 当前版本比对,不一致即 token 作废)。</summary>
        public const string TokenVersion = "TokenVersion";

        /// <summary>
        /// 租户 Id(task_008 租户基座扩展):登录签发(用户行 TenantId,缺省 DefaultTenantId),
        /// PermissionMiddleware 租户过滤与 AOP 租户写入自动填读取;超管请求本 Claim 存在但豁免过滤。
        /// </summary>
        public const string TenantId = "TenantId";

        /// <summary>
        /// 机构 Id(批 E 1.7 开放接口扩展):绑定用户 SysUser.OrgId 写入(可空,缺省不写 Claim);
        /// 与 JWT 同构保证开放接口请求经机构数据范围过滤同口径(PermissionMiddleware SetOrgFilterAsync)。
        /// </summary>
        public const string OrgId = "OrgId";
    }
}
