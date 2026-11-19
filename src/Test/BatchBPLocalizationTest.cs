// 批 BP sysLocalization 多语言逐页复审测试:
// ① 批 BP D1 注册面守卫:ILocalizationService 须**单一注册且 Singleton**——
//    批 AL 时代同接口双注册(显式 AddSingleton + [Service] 缺省 Scoped,.NET 按最后注册者生效取 Scoped)
//    ⇒ 每请求重解析 Lang.*.json 且生命周期随注册顺序漂移(G23 失败模式同源)。
//    本用例以 Program.cs 同款装配序列(AddAdminLocalization → AddAutoServices(Core 程序集))断言注册面,
//    防未来再贴 [Service] 或调整注册序复发。
using AdminCore.Core;
using AdminCore.Core.Extensions;
using AdminCore.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdminCore.Test;

public class BatchBPLocalizationTest
{
    [Fact]
    public void 注册面_本地化服务单一注册_单例()
    {
        var services = new ServiceCollection();
        // 与 Program.cs 同口径:显式注册在前、自动发现在后(顺序敏感——最后注册者生效)
        services.AddAdminLocalization();
        services.AddAutoServices(typeof(SqlSugarSetup).Assembly);

        var descriptors = services.Where(d => d.ServiceType == typeof(ILocalizationService)).ToList();
        // 参数化断言:Assert.Single 仅收 IEnumerable;自定义消息走 Assert.True 同义式
        Assert.True(descriptors.Count == 1,
            $"ILocalizationService 须单一注册(实得 {descriptors.Count})——双注册=生命周期随注册顺序漂移(批 BP D1/G23 同源)");
        Assert.True(descriptors[0].Lifetime == ServiceLifetime.Singleton,
            $"ILocalizationService 须 Singleton(实得 {descriptors[0].Lifetime})——否则资源随作用域重复装载(头注「装载一次」口径被破坏)");
    }
}
