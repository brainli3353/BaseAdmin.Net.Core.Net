using Microsoft.Extensions.DependencyInjection;

namespace AdminCore.Core.Localization
{
    /// <summary>
    /// 本地化装配(批 AL 6.6):注册单例服务。静态解析器 <see cref="L.Resolver"/> 由
    /// <see cref="LocalizationService"/> 构造期装载 ⇒ 宿主应**启动时显式解析一次**该服务
    /// (见 Program.cs 启动 I/O 段),使 AppException 的资源键翻译在首请求前就绪。
    /// </summary>
    public static class LocalizationSetup
    {
        /// <summary>注册本地化:资源装载一次(单例)。</summary>
        public static IServiceCollection AddAdminLocalization(this IServiceCollection services)
        {
            services.AddSingleton<ILocalizationService, LocalizationService>();
            return services;
        }
    }
}