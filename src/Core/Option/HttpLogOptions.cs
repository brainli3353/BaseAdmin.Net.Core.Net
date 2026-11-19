namespace AdminCore.Core.Option
{
    /// <summary>
    /// Http 出站日志选项(5.3):配置节 <c>Logging:Http</c>(与 5.2 文件通道的 <c>Logging:File</c> 同族,
    /// 故不随 AddProjectOptions 的"节名=类名"约定,由 AddHttpOutboundLogging 显式绑定该段)。
    /// </summary>
    public class HttpLogOptions
    {
        /// <summary>
        /// 是否启用(http_log 记录总开关)。缺省 <b>false</b>——出站日志含请求/响应体,属"按需开启"的
        /// 可观测性能力(避免默认把内部接口载荷写库);开启后 Handler 才记录并发布事件。
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// 请求/响应体最大记录长度(字符),超出截断并追加 "(truncated)" 标记。缺省 2048;
        /// &lt;=0 表示不记录请求/响应体(仅记方法/URL/头/状态码/耗时)。
        /// </summary>
        public int MaxBodyLength { get; set; } = 2048;

        /// <summary>
        /// 排除的目标主机(逗号分隔或数组;按主机名**后缀**匹配,如 "internal.svc" 命中 "a.internal.svc")。
        /// 用于跳过高频/敏感目标(如监控上报)。缺省空=不排除。
        /// </summary>
        public string[] ExcludeHosts { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 需要掩码的请求头名(缺省已含 Authorization/Cookie/X-API-KEY/Proxy-Authorization/Set-Cookie 等;
        /// 本项为**追加**)——命中则值记为 "***"(大小写不敏感)。库内不落凭据明文。
        /// </summary>
        public string[] MaskHeaders { get; set; } = Array.Empty<string>();
    }
}