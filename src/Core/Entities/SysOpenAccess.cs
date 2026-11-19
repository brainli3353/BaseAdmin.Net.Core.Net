using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 开放接口身份表(批 E 1.6/1.7/6.10):外部系统对接凭证——
    /// 一条身份 = 一对 AccessKey/AccessSecret + 绑定的租户与用户(调用开放接口时以绑定用户身份运行,
    /// ClaimsPrincipal 与 JWT 同构:UserId/TenantId/Account/RealName/AccountType/TokenVersion/OrgId,
    /// AOP 审计/租户行级过滤/机构数据范围对开放接口请求同样生效)。
    /// 两种认证方式共享同一身份:Signature(HMAC-SHA256 签名,Header accessKey/timestamp/nonce/sign)、
    /// ApiKey(Header X-API-KEY 值即 AccessSecret)。
    /// </summary>
    /// <remarks>
    /// 移植参照 AdminNetPro SysOpenAccess(零 Furion 适配,详见 docs/研究/e1_refer_research.md):
    /// - 补 [IncreTable](参照缺,本仓 CodeFirst 建表按本特性分流);显式表名 sys_open_access;
    /// - 不建导航属性(参照 BindUser/BindTenant 导航):参照 BindUser 漏加 JsonIgnore 会序列化出 Password 哈希,
    ///   且目标 SysUser 无 SysOrg 导航/OrgName/OrgType 无法移植(R2/R3)——分页显式 LeftJoin 映射,
    ///   Handler 经 SysOpenAccessService.GetIdentityAsync 取绑定用户行(带缓存)。
    /// - AccessSecret 为 HMAC 密钥/API Key 本体,输出侧仅建号/重置时一次性返回,分页不回显。
    /// - t846 批 2 纳管(2026-10-27,DDL 与实体同批):改继承 <see cref="EntityTenant"/>。
    ///   WHY 行归属租户 = 绑定租户(BindTenantId),两者同源——管理面按当前租户隔离;
    ///   但认证路径(AccessKey/AccessSecret 全局定位,调用方此时未认证、不知道租户)必须 IgnoreTenant,
    ///   否则认证会被租户过滤判成"不存在"(见 SysOpenAccessService.GetIdentityAsync/GetIdentityBySecretAsync)。
    ///   AccessKey 唯一键按裁定保持**全局**,是唯一键口径的显式例外(理由见 SchemaIntegrityInitializer 注释)。
    /// </remarks>
    [IncreTable]
    [SysTable]
    [SugarTable("sys_open_access", "开放接口身份表")]
    [SugarIndex("i_sys_open_access_a", nameof(AccessKey), OrderByType.Asc)]
    public class SysOpenAccess : EntityTenant
    {
        /// <summary>身份标识(签名 Header accessKey;唯一,建号后不可改)。</summary>
        [SugarColumn(ColumnDescription = "身份标识", Length = 128)]
        public string AccessKey { get; set; } = string.Empty;

        /// <summary>密钥(HMAC-SHA256 签名密钥;API Key 模式下 X-API-KEY 头值即本字段;建号/重置时自动生成,仅一次性返回)。</summary>
        [SugarColumn(ColumnDescription = "密钥", Length = 256)]
        public string AccessSecret { get; set; } = string.Empty;

        /// <summary>绑定租户 Id(开放接口请求的租户行级过滤取本值,写入 TenantId Claim)。</summary>
        [SugarColumn(ColumnDescription = "绑定租户Id")]
        public long BindTenantId { get; set; }

        /// <summary>绑定用户 Id(开放接口请求以该用户身份运行;写入 UserId/Account/RealName/AccountType/TokenVersion/OrgId Claims)。</summary>
        [SugarColumn(ColumnDescription = "绑定用户Id")]
        public long BindUserId { get; set; }
    }
}
