using System.Reflection;
using AdminCore.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdminCore.Core.Extensions
{
    /// <summary>
    /// 服务自动发现注册扩展(task_024):反射扫描指定程序集中所有标 <c>[Service]</c> 的类,按特性配置注册进 DI。
    /// 目的:消除 Program.cs 逐服务手写散注册的热点文件——新增模块贴 <c>[Service]</c> 即自动装配,零改 Program.cs。
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 扫描指定程序集中全部 <c>[Service]</c> 标注的公共非抽象类并注册。可传多个程序集覆盖不同程序集(如 Core + Web.Core)。
        /// </summary>
        /// <param name="services">DI 服务集合。</param>
        /// <param name="assemblies">待扫描程序集(至少一个)。</param>
        public static IServiceCollection AddAutoServices(this IServiceCollection services, params Assembly[] assemblies)
        {
            foreach (var assembly in assemblies)
            {
                RegisterAssemblyServices(services, assembly);
            }
            return services;
        }

        // 单程序集扫描:遍历公共非抽象类,命中 [Service](仅本类标注,不继承)即按其配置注册。
        // 排除抽象类/接口/泛型定义(无可实例化的具体语义,注册无意义)。
        private static void RegisterAssemblyServices(IServiceCollection services, Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 个别类型因依赖缺失加载失败不阻断启动:取已加载成功的部分继续扫描。
                // ex.Types 为 Type?[](元素可空),Where 不收窄元素可空性,故 Select(t => t!) 显式转非空 Type。
                types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
            }

            foreach (var type in types)
            {
                if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || !type.IsPublic)
                {
                    continue;
                }

                var attr = type.GetCustomAttribute<ServiceAttribute>(inherit: false);
                if (attr is null)
                {
                    continue;
                }

                RegisterService(services, type, attr);
            }
        }

        // 单类注册:解析 [Service] 语义确定服务类型,按其生命周期入容器(ServiceDescriptor 精确表达 服务类型→实现类型→生命周期)。
        private static void RegisterService(IServiceCollection services, Type implementationType, ServiceAttribute attr)
        {
            var serviceType = ResolveServiceType(implementationType, attr);
            services.Add(new ServiceDescriptor(serviceType, implementationType, attr.Lifetime));

            // HostedService 分支(task_014):[Service] 类实现 IHostedService(如 BackgroundService)时,
            // 补注册 IHostedService(经工厂解析主服务类型,见下),让宿主自动发现并启动后台消费者——零改 Program.cs 散注册(对齐「开发与注册分离」)。
            // WHY 须 Singleton:后台消费者常驻,非 Singleton(Scoped/Transient)会随作用域反复建/销毁消费者循环,语义错误;
            // 快速失败优于静默误注册(与 ResolveServiceType 的多接口歧义口径一致)。
            // 对存量 [Service] 无影响:存量无 IHostedService 实现类,本分支不触发。
            if (typeof(IHostedService).IsAssignableFrom(implementationType)
                && !typeof(IHostedService).Equals(serviceType))
            {
                if (attr.Lifetime != ServiceLifetime.Singleton)
                {
                    throw new InvalidOperationException(
                        $"[Service] IHostedService 实现类 {implementationType.FullName} 生命周期须为 Singleton(当前 {attr.Lifetime})," +
                        $"宿主常驻消费者要求单例");
                }

                // WHY 用工厂经「主服务类型」(serviceType)解析,而非直接 IHostedService→实现:
                // .NET DI 按 (ServiceType, ImplementationType) 缓存单例——(serviceType→实现) 与 (IHostedService→实现)
                // 两条描述符分别缓存为两个不同实例:发布者(解析 serviceType)入队实例 A 的 Channel,宿主消费者
                // (解析 IHostedService)却读实例 B 的空 Channel,事件永不消费(经 /tmp/evcheck 实证两实例 hash 不同)。
                // 工厂经主服务类型解析使两者命中同一单例缓存条目→同一实例→发布者与消费者共享同一 Channel 不分裂。
                services.Add(new ServiceDescriptor(
                    typeof(IHostedService),
                    sp => sp.GetRequiredService(serviceType),
                    ServiceLifetime.Singleton));
            }
        }

        // 服务类型解析(零破坏核心):
        // - 显式指定 ServiceType → 直接采用(覆盖自动匹配,用于必须自注册的过滤器);
        // - 省略 → 自动匹配:恰好一个公共接口→接口(接口→实现);无接口→具体类自注册;多个→启动即抛(拒绝静默误注册)。
        private static Type ResolveServiceType(Type implementationType, ServiceAttribute attr)
        {
            if (attr.ServiceType is not null)
            {
                return attr.ServiceType;
            }

            var interfaces = implementationType.GetInterfaces()
                .Where(i => i.IsPublic)
                .Distinct()
                .ToArray();

            return interfaces switch
            {
                [] => implementationType,        // 无公共接口:具体类自注册(等价 AddScoped<Concrete>())
                [var only] => only,               // 恰好一个公共接口:接口→实现(等价 AddScoped<I, Concrete>())
                _ => throw new InvalidOperationException(
                    $"[Service] 自动匹配歧义:{implementationType.FullName} 实现了多个公共接口" +
                    $"({string.Join(", ", interfaces.Select(i => i.Name))});请在特性上显式指定 ServiceType 消除歧义")
            };
        }
    }
}
