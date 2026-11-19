using Microsoft.Extensions.DependencyInjection;

namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 服务自动发现标记(task_024):被标注类经 <c>AddAutoServices</c> 反射扫描后自动注册进 DI,
    /// 新增模块只需贴本特性,无需再改 Program.cs(根除散注册热点文件)。
    /// </summary>
    /// <remarks>
    /// 注册语义(零破坏迁移口径,与迁移前 Program.cs 散注册逐条对齐):
    /// - <see cref="ServiceType"/> 显式指定 → 按「服务类型→实现类」注册(优先级最高,用于必须自注册者,如 MVC 过滤器);
    /// - 省略 → 自动匹配:类恰好实现一个公共接口则按「接口→实现」注册,无接口则「具体类自注册」,
    ///   实现多个公共接口时启动即抛错(拒绝静默误注册,见 ServiceCollectionExtensions.ResolveServiceType);
    /// - <see cref="Lifetime"/> 缺省 Scoped。
    /// Inherited=false:注册语义逐类显式标注,不随继承传播(派生类须自行贴特性)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class ServiceAttribute : Attribute
    {
        /// <summary>
        /// 注册所用的服务类型(可选)。省略时自动匹配第一个公共接口;类无接口则按具体类自注册。
        /// 典型用途:对「实现了接口但须以具体类自注册」的类(如 MVC ActionFilter)显式锁定具体类,避免被自动匹配改道。
        /// 须为可写属性:C# 具名属性参数([Service(ServiceType = ...)])只能绑定到可写字段/非静态读写属性,
        /// 只读属性作具名参数报 CS0617。
        /// </summary>
        public Type? ServiceType { get; set; }

        /// <summary>生命周期,缺省 <see cref="ServiceLifetime.Scoped"/>。可写属性:作具名参数([Service(Lifetime = ...)])。</summary>
        public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;
    }
}
