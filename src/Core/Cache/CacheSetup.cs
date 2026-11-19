using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace AdminCore.Core.Cache
{
    /// <summary>
    /// 缓存注册(task_007 契约):按配置 <c>Cache:Type</c> 选择实现,Redis 优先、内存兜底,业务代码只引用 ICacheService 无感。
    /// - "Memory"(忽略大小写) → MemoryCacheService
    /// - 其余/缺省 → Redis;但 <c>ConnectionStrings:Redis</c> 缺失时退回 Memory(显式配置决定实现,
    ///   不做运行期自动切换——生产"以为是 Redis 实际全在内存"会让黑名单/计数跨实例失效,宁可启动即错)。
    /// </summary>
    public static class CacheSetup
    {
        /// <summary>
        /// 生效缓存实现描述(task_010 启动自检日志用):与 AddCache 同一判定口径——
        /// 显式 Memory / Redis 有连接串 → 对应实现;Redis 无连接串 → 回退 Memory(部署缺口,日志可见)。
        /// </summary>
        public static string DescribeEffectiveImpl(IConfiguration config)
        {
            var type = (config["Cache:Type"] ?? string.Empty).Trim();
            var useMemory = string.Equals(type, "Memory", StringComparison.OrdinalIgnoreCase);
            var conn = config.GetConnectionString("Redis");
            if (!useMemory && string.IsNullOrWhiteSpace(conn))
            {
                return "Redis 已配置但连接串缺失 → 回退 Memory(需注入 ConnectionStrings:Redis)";
            }

            return useMemory ? "Memory(显式配置)" : "Redis";
        }

        public static IServiceCollection AddCache(this IServiceCollection services, IConfiguration config)
        {
            var type = (config["Cache:Type"] ?? string.Empty).Trim();
            var useMemory = string.Equals(type, "Memory", StringComparison.OrdinalIgnoreCase);
            var conn = config.GetConnectionString("Redis");

            // Redis 优先但连接串缺失 → 内存兜底(缺配置属部署问题,启动仍可跑,日志由调用方告警)
            if (!useMemory && string.IsNullOrWhiteSpace(conn))
            {
                useMemory = true;
            }

            if (useMemory)
            {
                services.AddMemoryCache();
                services.AddSingleton<ICacheService, MemoryCacheService>();
            }
            else
            {
                // 启动期同步连接:Redis 不可达时快速失败,连接实例进程级单例(官方推荐用法)
                // conn 此处必非空:空值分支已在上面退回 Memory(编译期不可由 IsNullOrWhiteSpace 收窄,显式断言)
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(conn!));
                services.AddSingleton<ICacheService, RedisCacheService>();
            }

            return services;
        }
    }
}
