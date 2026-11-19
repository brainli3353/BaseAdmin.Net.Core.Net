using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统租户实体(批 C 多租户业务模块 C1):租户登记表,以 Id 为租户标识
    /// (= 用户 TenantId Claim / SysUser.TenantId 的取值)。状态/过期时间驱动登录校验(1.4)
    /// 与 JwtAuthHandler 回查校验(租户停用/过期即拒绝)。
    /// </summary>
    /// <remarks>
    /// WHY : EntityBase(非 EntityTenant):租户登记表是平台级表,不受行级租户过滤
    /// (租户管理是平台/超管职能;若被过滤,非超管将无法管理租户,且"默认租户"行无"所属租户"概念)。
    /// 与 baseadmin SysTenant : EntityBase 同口径。库级隔离(独立库连接串/库名)自批 AB 起由本实体承载
    /// (<see cref="Connection"/>/<see cref="DbName"/>),连接解析/懒加载见 TenantDbRegistry。
    /// </remarks>
    [SugarTable("sys_tenant", "系统租户")]
    [IncreTable]
    [SysTable]
    public class SysTenant : EntityBase
    {
        /// <summary>租户名称(展示名;ORM 读回无空值保证,按项目实体惯例用可空引用类型)。</summary>
        [SugarColumn(ColumnDescription = "租户名称", Length = 64)]
        public string? Name { get; set; }

        /// <summary>套餐(计划/订阅名称,自由文本;批 C C1 从简,不建套餐体系——按需后补)。</summary>
        [SugarColumn(ColumnDescription = "套餐", Length = 64, IsNullable = true)]
        public string? Plan { get; set; }

        /// <summary>状态(0=启用 1=停用;与 SysUser.Status 同口径)。停用后登录拒绝 + 该租户全员 token 回查拒绝。</summary>
        [SugarColumn(ColumnDescription = "状态(0=启用 1=停用)", DefaultValue = "0")]
        public int Status { get; set; } = 0;

        /// <summary>过期时间(null=永久有效;过期后登录拒绝 + JwtAuthHandler 回查拒绝)。</summary>
        [SugarColumn(ColumnDescription = "过期时间", IsNullable = true)]
        public DateTime? ExpirationTime { get; set; }

        /// <summary>排序(列表展示序)。</summary>
        [SugarColumn(ColumnDescription = "排序", DefaultValue = "100")]
        public int OrderNo { get; set; } = 100;

        /// <summary>备注(用途说明)。</summary>
        [SugarColumn(ColumnDescription = "备注", Length = 128, IsNullable = true)]
        public string? Remark { get; set; }

        /// <summary>
        /// 租户独立库连接串(批 AB 库级隔离 C2):非空 = 该租户数据落独立库(ConfigId = 租户 Id 字符串);
        /// 空 = 落主库(基座期行为,零变化)。<c>DbSettings:EnableConnEncrypt=true</c> 时本列为密文
        /// (本仓 AES 口径,CryptogramHelper 加密;参照仓为 SM2,差异见批 AB 裁决留痕)。
        /// 安全口径:本列**永不进对外输出**(TenantOutput 仅回 hasConnection 布尔),日志亦不回显(解析失败文案不含密文)。
        /// </summary>
        [SugarColumn(ColumnDescription = "独立库连接串(空=落主库;加密开关开启时为密文)", Length = 512, IsNullable = true)]
        public string? Connection { get; set; }

        /// <summary>
        /// 租户独立库库名(展示/运维用)。**不参与连接解析**——连接串是唯一事实源,
        /// 本列仅用于列表展示与人工对账(防两处配置漂移,同 #11 单点口径)。
        /// </summary>
        [SugarColumn(ColumnDescription = "独立库库名(展示用)", Length = 64, IsNullable = true)]
        public string? DbName { get; set; }
    }
}
