using Microsoft.Extensions.DependencyInjection;

namespace AdminCore.Core.Logging
{
    /// <summary>
    /// Http 出站日志的 HttpClient 管道接线扩展(批 AE E3,5.3 便利化):把 <see cref="HttpLoggingHandler"/>
    /// 挂到命名/类型化 HttpClient 的管道上,业务侧一行完成接线。
    /// </summary>
    /// <remarks>
    /// 用法(两种等价形态):
    /// <code>
    /// // ① 命名客户端(推荐,管道复用位置语义清晰)
    /// builder.Services.AddHttpOutboundLogging(builder.Configuration);  // Program.cs 已调,绑定 Logging:Http + 注册 handler(Transient)
    /// builder.Services.AddHttpClient("payment")
    ///        .AddHttpOutboundLogging();
    ///
    /// // ② 类型化客户端
    /// builder.Services.AddHttpClient&lt;PaymentClient&gt;()
    ///        .AddHttpOutboundLogging();
    /// </code>
    /// 前提:handler 已注册(由 <c>AddHttpOutboundLogging(IServiceCollection, IConfiguration)</c> 完成,Program.cs 恒调);
    /// 未注册时容器解析会抛明确异常(不做"自动补注册"——重复 AddTransient 会累积 handler 注册,难排查)。
    /// 是否真记录仍受 <c>Logging:Http:Enabled</c> 控制(接线 ≠ 启用,两级控制见 HttpLogOptions)。
    /// 幂等口径:同一 HttpClientBuilder 只需挂一次;重复调用会挂多个 handler(日志重复发布),故本扩展只做挂载,不承担去重。
    /// </remarks>
    public static class HttpClientBuilderExtensions
    {
        /// <summary>
        /// 把出站日志 handler 挂到该 HttpClient 的管道上(业务侧一行接线;是否记录由 Logging:Http:Enabled 决定)。
        /// </summary>
        public static IHttpClientBuilder AddHttpOutboundLogging(this IHttpClientBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddHttpMessageHandler<HttpLoggingHandler>();
        }
    }
}