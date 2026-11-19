using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// Http 出站日志(框架级审计实体,5.3):HttpLoggingHandler 记录本进程**对外发起**的 HTTP 调用
    /// (方法/URL/请求头/请求体/状态码/响应体/耗时/异常),发布 "http_log" 事件由 HttpLogSubscriber
    /// 经事件总线异步落库(与操作档/异常档同范式)。
    /// </summary>
    /// <remarks>
    /// 范围口径:仅记**出站**(本服务作为客户端调用外部/内部接口)——入站请求日志属操作档(SysLogOp)。
    /// 大字段口径:RequestHeaders/RequestBody/ResponseBody/Exception 为 text 大字段——落库全量(受
    /// Logging:Http:MaxBodyLength 截断)但**列表查询不载**(PageAsync 投影排除),详情端点全字段。
    /// 脱敏:请求头中的敏感头(Authorization/Cookie/X-API-KEY 等)在 Handler 侧已掩码,库内不落明文凭据。
    /// [LogTable]:多库通用选库——日志库连接配置时落日志库,未配置回落主库(与 SysLogOp/SysLogEx 同口径)。
    /// </remarks>
    [SugarTable("sys_log_http", "Http出站日志")]
    // WHY 索引:高频查询列——CleanSysLogJob 保留期按 CreateTime 物理清理＋出站日志分页时间过滤排序
    [SugarIndex("i_sys_log_http_createtime", nameof(CreateTime), OrderByType.Desc)]
    [IncreTable]
    [LogTable]
    public class SysLogHttp : EntityBase
    {
        /// <summary>HTTP 方法(GET/POST/PUT/DELETE…,原样记录)。</summary>
        [SugarColumn(ColumnDescription = "HTTP方法", Length = 16, IsNullable = true)]
        public string? HttpMethod { get; set; }

        /// <summary>请求地址(绝对 URL,含查询串;超长截断由 Length 承担)。</summary>
        [SugarColumn(ColumnDescription = "请求地址", Length = 1024, IsNullable = true)]
        public string? RequestUrl { get; set; }

        /// <summary>请求头(JSON 对象;敏感头已掩码;大字段)。</summary>
        [SugarColumn(ColumnDescription = "请求头", ColumnDataType = "text", IsNullable = true)]
        public string? RequestHeaders { get; set; }

        /// <summary>请求体(按 MaxBodyLength 截断;非文本内容记其类型占位;大字段)。</summary>
        [SugarColumn(ColumnDescription = "请求体", ColumnDataType = "text", IsNullable = true)]
        public string? RequestBody { get; set; }

        /// <summary>响应状态码(null=请求未拿到响应,如连接失败/超时——此时 HasException=true)。</summary>
        [SugarColumn(ColumnDescription = "响应状态码", IsNullable = true)]
        public int? StatusCode { get; set; }

        /// <summary>响应体(按 MaxBodyLength 截断;大字段)。</summary>
        [SugarColumn(ColumnDescription = "响应体", ColumnDataType = "text", IsNullable = true)]
        public string? ResponseBody { get; set; }

        /// <summary>耗时毫秒(发起至收到响应/异常结束)。</summary>
        [SugarColumn(ColumnDescription = "耗时毫秒", IsNullable = true)]
        public long? ElapsedMs { get; set; }

        /// <summary>
        /// 是否发生异常(独立列而非"Exception 非空"推导):列表查询**不载 Exception 大字段**,
        /// 若在投影里判空则须载入该列(逐行读大字段);独立标记列使列表零大字段读取(同 SysLogOp.Success 口径)。
        /// </summary>
        [SugarColumn(ColumnDescription = "是否异常", DefaultValue = "false")]
        public bool HasException { get; set; }

        /// <summary>异常全文(异常信息+来源+堆栈三段拼接;仅 HasException=true 时有值;大字段)。</summary>
        /// <remarks>
        /// 截断口径与请求/响应体**不同**:异常固定 4096 字符上限、**不随 Logging:Http:MaxBodyLength 伸缩**
        /// (异常诊断价值高,须保留堆栈首部;MaxBodyLength 只是载荷记录的体积旋钮)。
        /// </remarks>
        [SugarColumn(ColumnDescription = "异常信息", ColumnDataType = "text", IsNullable = true)]
        public string? Exception { get; set; }

        /// <summary>发起方账号(当前请求 JWT Claim Account,经 UserManager 门面;后台任务/匿名为空)。</summary>
        [SugarColumn(ColumnDescription = "操作账号", Length = 64, IsNullable = true)]
        public string? Account { get; set; }

        /// <summary>发起方姓名(JWT Claim RealName;匿名为空)。</summary>
        [SugarColumn(ColumnDescription = "操作人姓名", Length = 64, IsNullable = true)]
        public string? RealName { get; set; }

        /// <summary>请求跟踪 Id(取当前 HttpContext.TraceIdentifier;无请求上下文为空)——用于把一次出站调用关联回入站请求。</summary>
        [SugarColumn(ColumnDescription = "请求跟踪Id", Length = 128, IsNullable = true)]
        public string? TraceId { get; set; }
    }
}