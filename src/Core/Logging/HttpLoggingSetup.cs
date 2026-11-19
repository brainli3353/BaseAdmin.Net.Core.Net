using AdminCore.Core.Option;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdminCore.Core.Logging
{
    /// <summary>
    /// Http 出站日志装配(5.3):绑定 <c>Logging:Http</c> 配置段并注册 <see cref="HttpLoggingHandler"/>。
    /// </summary>
    /// <remarks>
    /// 装配点:Program.cs <c>builder.Services.AddHttpOutboundLogging(builder.Configuration)</c>(幂等,只注册不启用)。
    /// WHY 注册与启用分离:handler 本身"未挂到 HttpClient 就不产生任何日志"——
    /// 即业务侧 opt-in(<c>AddHttpClient(...).AddHttpMessageHandler&lt;HttpLoggingHandler&gt;()</c>),
    /// 开关 <see cref="HttpLogOptions.Enabled"/>(缺省 false)再决定挂上的管道是否真记录;
    /// 两级控制使"框架默认零副作用"与"业务按需开启"同时成立(同批 R 二级缓存 opt-in 口径)。
    /// WHY handler 必须 Transient:AddHttpClient 的 handler 生命周期契约要求瞬时实例
    /// (HttpMessageHandler 有状态:每次请求链复用/池化由工厂管理,单例会导致并发请求互相干扰)。
    /// </remarks>
    public static class HttpLoggingSetup
    {
        /// <summary>
        /// 注册出站日志选项与 handler(不启用记录;由 <c>Logging:Http:Enabled</c> 控制是否真记录、由业务挂载控制是否生效)。
        /// </summary>
        public static IServiceCollection AddHttpOutboundLogging(this IServiceCollection services, IConfiguration configuration)
        {
            // 节名 Logging:Http(与 5.2 文件通道 Logging:File 同族,非"节名=类名"约定;故不并入 AddProjectOptions)
            services.Configure<HttpLogOptions>(configuration.GetSection("Logging:Http"));
            services.AddTransient<HttpLoggingHandler>();
            return services;
        }
    }
}