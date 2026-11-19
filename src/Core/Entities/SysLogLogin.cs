using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 登录日志(task_021:实体 + 服务 + 分页查询)。继承 EntityTenant 获得雪花主键/审计/软删/租户字段,
    /// 登录流程关键时刻(登录成功/失败、登出)各写一条;主键/CreateTime/CreateUser/Tenant 由 AOP 自动填充,业务侧只写业务字段。
    /// </summary>
    /// <remarks>
    /// WHY 继承 EntityTenant(契约口径,与 SysLogOp 的 EntityBase 不同):登录日志是租户维度数据——
    /// 多租户落地后各租户只见本租户用户的登录记录(PermissionMiddleware 自动追加 TenantId 过滤);
    /// 操作日志是运维审计视角(全租户可见)才用 EntityBase,两者口径勿混。
    /// Browser/Os 存 UA 解析后的展示文本(解析在调用方完成):日志表不引 UA 解析依赖,
    /// 且解析结果随库留存,历史行不受解析规则演进影响。
    /// 表名/列名大小写约定与全项目一致:[SugarTable] 显式小写表名、列名取属性名 PascalCase 原样引用
    /// (PG 精确大小写,见 SqlSugarSetup.PgExactCaseSettings)。
    /// </remarks>
    [SugarTable("sys_log_login", "登录日志")]
    // WHY 索引:高频查询列——CleanSysLogJob 保留期按 CreateTime 物理清理＋登录记录分页时间过滤排序
    [SugarIndex("i_sys_log_login_createtime", nameof(CreateTime), OrderByType.Desc)]
    [IncreTable]
    public class SysLogLogin : EntityTenant
    {
        /// <summary>登录账号(登录名,与 SysUser.Account 同口径;未过账号初验的失败行可能为空)。</summary>
        [SugarColumn(ColumnDescription = "登录账号", Length = 64, IsNullable = true)]
        public string? Account { get; set; }

        /// <summary>来源 IP(请求远程地址按原样记录;IPv6 本机可能带端口前缀,与 SysLogOp.RemoteIp 同口径)。</summary>
        [SugarColumn(ColumnDescription = "来源IP", Length = 64, IsNullable = true)]
        public string? RemoteAddr { get; set; }

        /// <summary>浏览器(UA 解析结果展示文本,解析失败为空)。</summary>
        [SugarColumn(ColumnDescription = "浏览器", Length = 64, IsNullable = true)]
        public string? Browser { get; set; }

        /// <summary>操作系统(UA 解析结果展示文本,解析失败为空)。</summary>
        [SugarColumn(ColumnDescription = "操作系统", Length = 64, IsNullable = true)]
        public string? Os { get; set; }

        /// <summary>登录结果:true=成功,false=失败(失败原因见 Message,如密码错误/OTP 错误/账号锁定)。</summary>
        [SugarColumn(ColumnDescription = "登录结果")]
        public bool Status { get; set; }

        /// <summary>结果描述(成功/密码错误/OTP 错误/账号锁定/账号停用等,由调用方与 Status 一并写入)。</summary>
        [SugarColumn(ColumnDescription = "结果描述", Length = 255, IsNullable = true)]
        public string? Message { get; set; }

        /// <summary>登录类型:login=登录/logout=登出(区分生命周期两个写入时机,登出行 Status 恒为 true)。</summary>
        [SugarColumn(ColumnDescription = "登录类型", Length = 16, IsNullable = true)]
        public string? LoginType { get; set; }
    }
}
