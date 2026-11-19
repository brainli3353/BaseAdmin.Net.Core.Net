using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Logging
{
    /// <summary>
    /// 文件日志通道装配(5.2 多通道):在宿主内建 Console 通道之外追加文件 provider(按天切分)。
    /// 配置源:"Logging:File" 段(Logging.json 模块配置;环境覆盖 Logging.{Environment}.json)。
    /// 装配点:Program.cs builder.Logging.AddFileLogger(builder.Configuration)——
    /// WebApplication.CreateBuilder 已 AddLogging(内建 Console),此处向同一 ILoggingBuilder 追加 provider。
    /// </summary>
    public static class LoggingSetup
    {
        /// <summary>
        /// 追加文件日志 provider:"Logging:File:Enabled"=false(显式)时不挂(缺省段缺失=挂,Enabled 缺省 true)。
        /// </summary>
        public static ILoggingBuilder AddFileLogger(this ILoggingBuilder loggingBuilder, IConfiguration configuration)
        {
            var options = new FileLoggerOptions();
            configuration.GetSection("Logging:File").Bind(options);

            if (!options.Enabled)
            {
                return loggingBuilder;
            }

            // 路径解析:相对路径挂到 content root(WebApplication 的 IHostEnvironment);绝对路径原样
            var contentRoot = ResolveContentRoot();
            if (!Path.IsPathRooted(options.Path) && contentRoot is not null)
            {
                options.Path = Path.Combine(contentRoot, options.Path);
            }

            // 纵深期 W6 修正：改**工厂注册**才能在 provider 内拿到 IHttpContextAccessor——请求关联 id 必须与
            // 响应头 X-Trace-Id 同源（`HttpContext.TraceIdentifier`），而实例注册（AddProvider）拿不到 DI。
            // AddHttpContextAccessor 内部 TryAddSingleton ⇒ 幂等、不覆盖宿主已有注册；provider 改由容器 Dispose
            // （FileLoggerProvider.Dispose 已 `_disposed` 幂等，容器与 LoggerFactory 双 Dispose 安全）。
            loggingBuilder.Services.AddHttpContextAccessor();
            loggingBuilder.Services.AddSingleton<ILoggerProvider>(sp =>
                new FileLoggerProvider(options, sp.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor));
            return loggingBuilder;
        }

        private static string? ResolveContentRoot()
        {
            // 不经 DI 取 IHostEnvironment(AddFileLogger 只拿 IConfiguration 的轻量签名):
            // WebApplication 宿主下 content root = AppContext.BaseDirectory 的上层不可靠,
            // 取宿主环境属性(ASPNETCORE_CONTENTROOT / 工作目录)——dotnet run/生产 dll 两种布局均落工作目录
            return Directory.GetCurrentDirectory();
        }
    }
}
