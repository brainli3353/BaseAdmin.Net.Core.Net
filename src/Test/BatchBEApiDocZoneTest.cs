using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Utils.ApiDoc;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Test;

/// <summary>
/// 批 BE「接口文档框架/业务分区」测试:被测主体 = <see cref="ApiDocGenerator"/> 的
/// <c>x-doc-zone</c> 判定——控制器所在程序集与调用方传入的 business 程序集**引用相等** ⇒ <c>business</c>,
/// 否则(含 businessAssembly=null)⇒ <c>framework</c>。
/// </summary>
/// <remarks>
/// WHY 用 Test 程序集自身的控制器 fixture 而非 Web.Entry:Test 项目不引用 Web.Entry(见 BatchAGApiDocTest 备注),
/// 写不出 <c>typeof(ApiDocController)</c>;而分区判定是**纯函数**(输入程序集 + businessAssembly → 每个操作的 zone),
/// 用 Test 程序集即可完整覆盖两个分支(null ⇒ 全 framework;businessAssembly==Test程序集 ⇒ 全 business),
/// 与生成器"按程序集引用相等判定"的口径一一对应,无需真起宿主。
/// <para>
/// 真实部署中 business 程序集＝AdminCore.Business(Program.cs 既有锚点 typeof(Class1).Assembly):
/// 该程序集当前无控制器 ⇒ 业务区暂为空(前端显示语义化空态);下游业务控制器落 Business 后自动归 business 区。
/// </para>
/// </remarks>
public class BatchBEApiDocZoneTest
{
    // 程序集引用相等是本用例的核心:businessAssembly 与 fixture 所在程序集必须是同一实例
    private static readonly Assembly FixtureAssembly = typeof(BeDocProbeController).Assembly;

    private static Dictionary<string, object?> Generate(Assembly? businessAssembly)
        => ApiDocGenerator.Generate(new[] { FixtureAssembly }, "BaseAdminCore API", "1.0", businessAssembly);

    private static List<IDictionary<string, object?>> AllOperations(Dictionary<string, object?> doc)
    {
        var paths = Assert.IsAssignableFrom<IDictionary<string, object?>>(doc["paths"]);
        var result = new List<IDictionary<string, object?>>();
        foreach (var byVerb in paths.Values)
        {
            foreach (var op in Assert.IsAssignableFrom<IDictionary<string, object?>>(byVerb).Values)
            {
                result.Add(Assert.IsAssignableFrom<IDictionary<string, object?>>(op));
            }
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────
    // 1. 前置:fixture 确实被扫到(证明本用例在测"有内容"的分区,而非空 paths 的假绿)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "fixture_被扫描到且路径存在")]
    public void fixture_被扫描到且路径存在()
    {
        var doc = Generate(businessAssembly: null);
        var paths = Assert.IsAssignableFrom<IDictionary<string, object?>>(doc["paths"]);

        Assert.True(paths.ContainsKey("/api/beDoc/probe"), "fixture 路由 /api/beDoc/probe 应被扫到");
        Assert.True(paths.ContainsKey("/api/beDoc/secure"), "fixture 路由 /api/beDoc/secure 应被扫到");
        Assert.True(AllOperations(doc).Count >= 2, "至少应扫到 fixture 的两个操作");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. businessAssembly=null ⇒ 全 framework(向后兼容:3 参调用即此分支)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "businessAssembly为null_全framework")]
    public void businessAssembly为null_全framework()
    {
        var ops = AllOperations(Generate(businessAssembly: null));
        Assert.NotEmpty(ops);

        foreach (var op in ops)
        {
            Assert.True(op.ContainsKey("x-doc-zone"), "每个操作都应带 x-doc-zone");
            Assert.Equal("framework", op["x-doc-zone"]);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. businessAssembly==fixture程序集 ⇒ 全 business(引用相等分支)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "businessAssembly等于fixture程序集_全business")]
    public void businessAssembly等于fixture程序集_全business()
    {
        var ops = AllOperations(Generate(businessAssembly: FixtureAssembly));
        Assert.NotEmpty(ops);

        foreach (var op in ops)
        {
            Assert.Equal("business", op["x-doc-zone"]);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. businessAssembly 为另一程序集(非 fixture 程序集)⇒ 仍全 framework(引用不相等)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "businessAssembly为其他程序集_仍全framework")]
    public void businessAssembly为其他程序集_仍全framework()
    {
        // typeof(object).Assembly = System.Runtime,与 fixture 程序集引用不相等
        var ops = AllOperations(Generate(businessAssembly: typeof(object).Assembly));
        Assert.NotEmpty(ops);

        foreach (var op in ops)
        {
            Assert.Equal("framework", op["x-doc-zone"]);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 词表一致性:x-doc-zone 只取 framework/business 两值(防拼写漂移)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "x-doc-zone_取值只在词表内")]
    public void x_doc_zone_取值只在词表内()
    {
        var vocabulary = new HashSet<string> { "framework", "business" };

        // 两个分支各跑一遍,zone 值都必须落在词表内
        foreach (var businessAssembly in new[] { (Assembly?)null, FixtureAssembly })
        {
            var ops = AllOperations(Generate(businessAssembly));
            Assert.NotEmpty(ops);
            foreach (var op in ops)
            {
                var zone = Assert.IsType<string>(op["x-doc-zone"]);
                Assert.Contains(zone, vocabulary);
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────
// fixture:两个只读端点(一个带 [MenuPerm]、一个不带),仅用于被生成器扫描;
// 不注册进任何 DI/路由,纯反射对象。命名避开 BCL/既有类型。
// ─────────────────────────────────────────────────────────────
[ApiController]
[Route("api/beDoc")]
public class BeDocProbeController : ControllerBase
{
    [HttpGet("probe")]
    public IActionResult Probe() => Ok("ok");

    [MenuPerm("beDoc/secure")]
    [HttpGet("secure")]
    public IActionResult Secure() => Ok("ok");
}
