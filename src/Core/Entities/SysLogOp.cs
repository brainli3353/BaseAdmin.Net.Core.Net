using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 操作日志(框架级审计实体,Web.Core/Auth/OpLogFilter 异步落库;继承 EntityBase 获得审计/软删字段)。
    /// </summary>
    /// <remarks>
    /// WHY 落 Core 而非 Business:操作日志是框架能力(非业务域),Web.Core 的 OpLogFilter 需要直接引用本实体——
    /// Web.Core 只引用 Core(层级纪律,禁止反向引用 Business)。
    /// [LogTable]:多库通用选库——日志库连接配置时落日志库,未配置回落主库(对齐 baseadmin)。
    /// 继承 EntityBase 而非 EntityTenant(契约口径):操作日志全租户可见(运维审计视角),不参与租户过滤。
    /// </remarks>
    [SugarTable("sys_log_op", "操作日志")]
    // WHY 索引:高频查询列——CleanSysLogJob 保留期按 CreateTime 物理清理＋日志分页时间过滤排序(存量仅主键,降序索引)
    [SugarIndex("i_sys_log_op_createtime", nameof(CreateTime), OrderByType.Desc)]
    [IncreTable]
    [LogTable]
    public class SysLogOp : EntityBase
    {
        /// <summary>控制器名(路由层定位,如 SysUser)。</summary>
        [SugarColumn(ColumnDescription = "控制器名", Length = 64, IsNullable = true)]
        public string? ControllerName { get; set; }

        /// <summary>动作名(如 Page/Add,与控制器方法一致)。</summary>
        [SugarColumn(ColumnDescription = "动作名", Length = 64, IsNullable = true)]
        public string? ActionName { get; set; }

        /// <summary>HTTP 方法(GET/POST)。</summary>
        [SugarColumn(ColumnDescription = "HTTP方法", Length = 16, IsNullable = true)]
        public string? HttpMethod { get; set; }

        /// <summary>来源 IP(请求远程地址,IPv6 本机可能带端口前缀,按原样记录)。</summary>
        [SugarColumn(ColumnDescription = "来源IP", Length = 64, IsNullable = true)]
        public string? RemoteIp { get; set; }

        /// <summary>耗时(毫秒,动作执行起点到响应结果确定)。</summary>
        [SugarColumn(ColumnDescription = "耗时毫秒")]
        public int ElapsedMs { get; set; }

        /// <summary>操作账号(JWT Claim Account;匿名放行端点如登录一阶段为空)。</summary>
        [SugarColumn(ColumnDescription = "操作账号", Length = 64, IsNullable = true)]
        public string? Account { get; set; }

        /// <summary>是否成功(动作抛出异常/返回非 2xx → false)。</summary>
        [SugarColumn(ColumnDescription = "是否成功")]
        public bool Success { get; set; } = true;

        /// <summary>请求参数(动作方法参数 JSON;批 AZ 参照口径——参照 DatabaseLoggingWriter 记方法参数值,不缓冲原始 body;鉴权端点 Auth/Login|LoginOtp 豁免不记;大字段)。</summary>
        [SugarColumn(ColumnDescription = "请求参数", ColumnDataType = "text", IsNullable = true)]
        public string? RequestParam { get; set; }

        /// <summary>返回结果(动作返回对象 ObjectResult.Value JSON,即统一响应 ApiResult 载体;大字段)。</summary>
        [SugarColumn(ColumnDescription = "返回结果", ColumnDataType = "text", IsNullable = true)]
        public string? ReturnResult { get; set; }

        /// <summary>链路 ID(HttpContext.TraceIdentifier,与响应头 X-Trace-Id/异常档 SysLogEx.TraceId 同源,
        /// 500 排障可跨 操作日志/异常档/响应头 三面串联;批 S-G B4a 增量加列,存量行走 null)。</summary>
        [SugarColumn(ColumnName = "TraceId", ColumnDescription = "链路ID", Length = 64, IsNullable = true)]
        public string? TraceId { get; set; }
    }
}
