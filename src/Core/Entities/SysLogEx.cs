using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统异常日志(框架级审计实体,5.1 异常档):Web.Core/Middlewares/ExceptionMiddleware 捕获未处理异常时
    /// 发布 "ex_log" 事件,由 ExLogSubscriber 经事件总线异步落库(与操作档同范式)。
    /// </summary>
    /// <remarks>
    /// 范围口径(批 I 裁决,docs/研究/g3_refer_research.md §9):仅记未处理异常——AppException 业务异常属预期分支
    /// (5.17 口径 Warning 无堆栈),不进异常档。
    /// 大字段口径:Exception/RequestParam/ReturnResult 为 text 大字段——落库全量,列表查询不载(参照 IgnoreColumns
    /// 口径),详情端点全字段。
    /// [LogTable]:多库通用选库——日志库连接配置时落日志库,未配置回落主库(对齐 baseadmin,SysLogOp 同口径)。
    /// </remarks>
    [SugarTable("sys_log_ex", "系统异常日志")]
    // WHY 索引:高频查询列——CleanSysLogJob 保留期按 CreateTime 物理清理＋异常记录分页时间过滤排序
    [SugarIndex("i_sys_log_ex_createtime", nameof(CreateTime), OrderByType.Desc)]
    [IncreTable]
    [LogTable]
    public class SysLogEx : EntityBase
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

        /// <summary>请求地址(完整路径,含查询串;大字段)。</summary>
        [SugarColumn(ColumnDescription = "请求地址", ColumnDataType = "text", IsNullable = true)]
        public string? RequestUrl { get; set; }

        /// <summary>请求参数(查询串全量+路由值;不缓冲请求 body——参照口径亦不记 body;大字段)。</summary>
        [SugarColumn(ColumnDescription = "请求参数", ColumnDataType = "text", IsNullable = true)]
        public string? RequestParam { get; set; }

        /// <summary>返回结果(中间件实际写出的统一响应 JSON;大字段)。</summary>
        [SugarColumn(ColumnDescription = "返回结果", ColumnDataType = "text", IsNullable = true)]
        public string? ReturnResult { get; set; }

        /// <summary>来源 IP(请求远程地址,按原样记录,同 SysLogOp 口径)。</summary>
        [SugarColumn(ColumnDescription = "来源IP", Length = 64, IsNullable = true)]
        public string? RemoteIp { get; set; }

        /// <summary>请求跟踪 Id(HttpContext.TraceIdentifier 原生)。</summary>
        [SugarColumn(ColumnDescription = "请求跟踪Id", Length = 128, IsNullable = true)]
        public string? TraceId { get; set; }

        /// <summary>线程 Id(Environment.CurrentManagedThreadId)。</summary>
        [SugarColumn(ColumnDescription = "线程Id")]
        public int? ThreadId { get; set; }

        /// <summary>操作账号(JWT Claim Account,经 UserManager 门面;匿名为空)。</summary>
        [SugarColumn(ColumnDescription = "操作账号", Length = 64, IsNullable = true)]
        public string? Account { get; set; }

        /// <summary>操作人姓名(JWT Claim RealName;匿名为空)。</summary>
        [SugarColumn(ColumnDescription = "操作人姓名", Length = 64, IsNullable = true)]
        public string? RealName { get; set; }

        /// <summary>异常全文(异常信息+来源+堆栈三段拼接;大字段)。</summary>
        [SugarColumn(ColumnDescription = "异常信息", ColumnDataType = "text", IsNullable = true)]
        public string? Exception { get; set; }

        /// <summary>日志级别(异常档恒 Error;保留对齐参照,未来 Warning 级入档可扩展)。</summary>
        [SugarColumn(ColumnDescription = "日志级别", Length = 32, IsNullable = true)]
        public string? LogLevel { get; set; }
    }
}
