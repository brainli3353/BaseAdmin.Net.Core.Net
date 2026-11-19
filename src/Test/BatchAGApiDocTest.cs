using System.Reflection;
using System.Text.Json;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Utils.ApiDoc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AG 6.7「接口文档(零依赖自建 OpenAPI)」测试:被测主体 = <see cref="ApiDocGenerator"/>
/// (反射扫描控制器程序集 → OpenAPI 3.0 对象图)与 ApiDocController 的 <c>ApiDoc:Enabled</c> 开关语义。
/// </summary>
/// <remarks>
/// WHY 全部用例纯内存、不起宿主:生成器是**纯反射函数**(输入程序集 → 对象图),开关判定只读 IConfiguration——
/// 真起 Kestrel 只能把"断言同一份输出"变成慢且脆的 HTTP 冒烟,证明力不增(与 BatchZEnumTest 的纯函数口径同族)。
/// <para>
/// ⚠ 程序集定位(本次测试的**核心约束**,见本批报告):Test 项目**未**引用 Web.Entry(AdminCore.Test.csproj 只引用
/// Core/Web.Core/Business,deps.json 无 AdminCore.Web.Entry),故写不出 <c>typeof(ApiDocController)</c> 的强类型引用。
/// 定位顺序:① 强类型(Type.GetType by name,将来 Test 补上 ProjectReference 即自动命中)② 回落定位 Web.Entry 的
/// <b>构建产物</b>(Assembly.LoadFrom)。②是"钉在既有 csproj 上"的妥协:产物早于源码即 Assert.Fail——
/// 否则会拿过期 DLL 扫出过期路由,得到"看着绿其实测的是旧代码"的假绿。
/// </para>
/// </remarks>
public class BatchAGApiDocTest
{
    // 已知端点(路由先用 grep 控制器核实:[Route] + [HttpXxx] 拼出的真实路径)
    private const string SysUserPage = "/api/sysUser/page";      // SysUserController: [Route("api/sysUser")] + [HttpGet("page")] + [MenuPerm("sysUser/page")]
    private const string DemoSuccess = "/api/demo/success";      // DemoController: 无 [MenuPerm] 的对照端点
    private const string DemoValidate = "/api/demo/validate";    // DemoController: ActionResult<ApiResult<DemoValidateInput>> ⇒ $ref 可解析到属性非空的 schema
    private const string SysLogExPage = "/api/sysLogEx/page";    // SysLogExController: [FromQuery] PageSysLogExInput(record) ⇒ 参数展平
    private const string SysUserAdd = "/api/sysUser/add";        // SysUserController: [FromBody] AddUserInput ⇒ in=body

    private readonly ITestOutputHelper _output;

    public BatchAGApiDocTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Assembly? _webEntryAssembly;

    // ─────────────────────────────────────────────────────────────
    // 1. 已知端点 + 权限键口径([MenuPerm] ⇒ security + x-perm;未标注 ⇒ 两者皆无)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "生成_含已知端点与权限键")]
    public void 生成_含已知端点与权限键()
    {
        var doc = Generate();
        var paths = Paths(doc);

        Assert.True(paths.ContainsKey(SysUserPage), $"paths 应含已知真实路由 {SysUserPage}");

        var page = Op(paths, SysUserPage, "get");
        // operationId/summary 含控制器名与动作名(生成器口径:tag.Method / tag_Method)
        Assert.Equal("SysUser_Page", page["operationId"]);
        Assert.Equal("SysUser.Page", page["summary"]);
        Assert.Contains("SysUser", (string)page["summary"]!);
        Assert.Contains("Page", (string)page["summary"]!);
        // [MenuPerm("sysUser/page")] ⇒ 权限键字符串 + Bearer 安全方案
        Assert.Equal("sysUser/page", page["x-perm"]);
        var security = Assert.IsType<object[]>(page["security"]);
        Assert.Single(security);
        var scheme = Assert.IsType<Dictionary<string, object?>>(security[0]);
        Assert.True(scheme.ContainsKey("Bearer"), "security 应声明 Bearer 方案");

        // 未标注 [MenuPerm] 的端点:没有 x-perm、也没有 security(逐项断言,防"只要写了 security 就算过")
        var success = Op(paths, DemoSuccess, "get");
        Assert.False(success.ContainsKey("x-perm"), $"{DemoSuccess} 未标注 [MenuPerm],不应有 x-perm");
        Assert.False(success.ContainsKey("security"), $"{DemoSuccess} 未标注 [MenuPerm],不应有 security");

        // 全量结构一致性:带 x-perm 的操作必须同时带 security,且权限键非空白(单点口径,防两处漂移)
        var withPerm = 0;
        foreach (var operation in AllOperations(paths))
        {
            if (!operation.ContainsKey("x-perm"))
            {
                continue;
            }

            withPerm++;
            Assert.False(string.IsNullOrWhiteSpace(operation["x-perm"] as string), "x-perm 不应为空白");
            Assert.True(operation.ContainsKey("security"), "带 x-perm 的操作应同时声明 security(Bearer)");
        }

        Assert.True(withPerm > 0, "应至少有一个标注 [MenuPerm] 的操作(否则本用例的权限键断言为空转)");
        _output.WriteLine($"判定:{SysUserPage} ⇒ operationId={page["operationId"]} x-perm={page["x-perm"]};" +
                          $"{DemoSuccess} 无 x-perm;全量带 x-perm 的操作 {withPerm} 个(均同时带 security)");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 参数展平:[FromQuery] DTO → 逐属性 query 参数;[FromBody] → in=body
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "生成_参数展平")]
    public void 生成_参数展平()
    {
        var paths = Paths(Generate());

        // [FromQuery] PageSysLogExInput(record 主构造参数)⇒ 展平成 account/controllerName/.../pageIndex
        var parameters = Parameters(Op(paths, SysLogExPage, "get"));
        var names = parameters.Select(p => (string)p["name"]!).ToList();
        Assert.Contains("account", names);
        Assert.Contains("pageIndex", names);
        Assert.Contains("controllerName", names);
        Assert.Contains("endTime", names);

        // 每项参数三件套齐备:name / in / schema.type
        foreach (var p in parameters)
        {
            Assert.False(string.IsNullOrWhiteSpace(p["name"] as string), "参数 name 不应为空");
            Assert.Equal("query", p["in"]);
            var schema = Assert.IsType<Dictionary<string, object?>>(p["schema"]);
            Assert.False(string.IsNullOrWhiteSpace(schema["type"] as string), $"参数 {p["name"]} 的 schema.type 不应为空");
        }

        var account = parameters.Single(p => (string)p["name"]! == "account");
        Assert.Equal("string", ((Dictionary<string, object?>)account["schema"]!)["type"]);
        var pageIndex = parameters.Single(p => (string)p["name"]! == "pageIndex");
        Assert.Equal("integer", ((Dictionary<string, object?>)pageIndex["schema"]!)["type"]);

        // 简单类型(非 DTO)参数不展平:name 即参数名
        var detail = Parameters(Op(paths, "/api/sysLogEx/detail", "get"));
        var id = Assert.Single(detail, p => (string)p["name"]! == "id");
        Assert.Equal("query", id["in"]);
        Assert.Equal("integer", ((Dictionary<string, object?>)id["schema"]!)["type"]);

        // [FromBody] AddUserInput ⇒ in=body 的单个参数(不展平成 query)
        var bodyParams = Parameters(Op(paths, SysUserAdd, "post"));
        var body = Assert.Single(bodyParams, p => (string)p["in"]! == "body");
        Assert.Equal("input", body["name"]);
        var bodySchema = Assert.IsType<Dictionary<string, object?>>(body["schema"]);
        Assert.Equal("object", bodySchema["type"]);
        Assert.Equal("AddUserInput", bodySchema["x-dotnet-type"]);
        Assert.DoesNotContain(bodyParams, p => (string)p["in"]! == "query");

        _output.WriteLine($"判定:{SysLogExPage} 展平 {names.Count} 个 query 参数({string.Join(",", names)});" +
                          $"{SysUserAdd} body 参数 x-dotnet-type={bodySchema["x-dotnet-type"]}");
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 响应 schema 与 components:$ref 必须能在 components.schemas 里解析,属性非空且每项有 type
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "生成_响应schema与components")]
    public void 生成_响应schema与components()
    {
        var doc = Generate();
        var paths = Paths(doc);
        var schemas = Schemas(doc);
        Assert.NotEmpty(schemas);

        // 带 ApiResult<T> 返回的端点(DemoController.Validate: ActionResult<ApiResult<DemoValidateInput>>)
        var validateRef = ResponseRef(Op(paths, DemoValidate, "post"));
        Assert.StartsWith("#/components/schemas/", validateRef);
        var schemaName = validateRef["#/components/schemas/".Length..];
        Assert.True(schemas.ContainsKey(schemaName), $"{validateRef} 指向的 schema 必须存在于 components.schemas");
        Assert.Equal("DemoValidateInput", schemaName);

        var schema = Assert.IsType<Dictionary<string, object?>>(schemas[schemaName]);
        Assert.Equal("object", schema["type"]);
        var props = Assert.IsType<SortedDictionary<string, object?>>(schema["properties"]);
        Assert.NotEmpty(props);
        Assert.Contains("name", props.Keys);
        Assert.Contains("age", props.Keys);
        foreach (var prop in props)
        {
            var propSchema = Assert.IsType<Dictionary<string, object?>>(prop.Value);
            Assert.False(string.IsNullOrWhiteSpace(propSchema["type"] as string), $"schema 属性 {prop.Key} 应有 type");
        }

        // 另一个 $ref 端点(原始载荷为 string):$ref 同样必须可解析(载荷是基元时 schema 属性为空,见本批报告)
        var successRef = ResponseRef(Op(paths, DemoSuccess, "get"));
        Assert.StartsWith("#/components/schemas/", successRef);
        Assert.True(schemas.ContainsKey(successRef["#/components/schemas/".Length..]),
            $"{successRef} 指向的 schema 必须存在于 components.schemas");

        // 全量:凡 200 响应带 $ref 的操作,$ref 目标必在 components.schemas(防"悬空引用"这类文档级硬伤)
        var refCount = 0;
        foreach (var operation in AllOperations(paths))
        {
            var schemaNode = ResponseSchema(operation);
            if (schemaNode.TryGetValue("$ref", out var r) && r is string reference)
            {
                refCount++;
                Assert.StartsWith("#/components/schemas/", reference);
                Assert.True(schemas.ContainsKey(reference["#/components/schemas/".Length..]),
                    $"悬空 $ref:{reference}");
            }
        }

        Assert.True(refCount > 0, "应有带 $ref 的操作(否则悬空引用断言为空转)");
        _output.WriteLine($"判定:{DemoValidate} ⇒ {validateRef}(属性 {props.Count} 个);全量 $ref 操作 {refCount} 个全部可解析");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 全量路径非空 + JSON 可序列化(文档不是"生成即崩"的空壳)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "生成_全量路径非空且JSON可序列化")]
    public void 生成_全量路径非空且JSON可序列化()
    {
        var doc = Generate();
        var paths = Paths(doc);

        // 本仓实测 133 条路径(见 init 探针与报告);要求下限 ≥30,此处再加实测余量门防"扫描退化"
        Assert.True(paths.Count >= 30, $"paths 数量应 ≥30,实际 {paths.Count}");
        Assert.True(paths.Count >= 100, $"paths 数量应 ≥100(本仓实测 133),实际 {paths.Count} —— 疑似程序集扫描退化");

        foreach (var path in paths)
        {
            Assert.StartsWith("/", path.Key);
            var byVerb = Assert.IsAssignableFrom<IDictionary<string, object?>>(path.Value);
            Assert.NotEmpty(byVerb);
            foreach (var op in byVerb.Values)
            {
                var operation = Assert.IsAssignableFrom<IDictionary<string, object?>>(op);
                Assert.False(string.IsNullOrWhiteSpace(operation["operationId"] as string));
                Assert.True(operation.ContainsKey("responses"), $"{path.Key} 的操作应有 responses");
            }
        }

        var json = ApiDocGenerator.ToJson(doc);
        Assert.True(json.Length > 2000, $"ToJson 输出长度应 >2000,实际 {json.Length}");
        // 可序列化 ≠ 只是"没抛":反解回来核对文档骨架(序列化口径漂移即红)
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("3.0.1", parsed.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("BaseAdminCore API", parsed.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("1.0", parsed.RootElement.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal(paths.Count, parsed.RootElement.GetProperty("paths").EnumerateObject().Count());
        Assert.False(string.IsNullOrWhiteSpace(parsed.RootElement.GetProperty("info").GetProperty("description").GetString()));

        _output.WriteLine($"判定:paths={paths.Count} 条;ToJson 长度={json.Length};反解 openapi={parsed.RootElement.GetProperty("openapi").GetString()}");
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 开关语义(ApiDoc:Enabled:false ⇒ 业务异常;缺省/true ⇒ OkObjectResult + 非空文档)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "开关语义")]
    public void 开关语义()
    {
        // false:照 ApiDocController 实现抛 AppException(403);经反射调用还原"控制器真实分支",不启宿主
        var off = InvokeOpenapi(new Dictionary<string, string?> { ["ApiDoc:Enabled"] = "false" });
        var offEx = Assert.IsType<AppException>(off.Error);
        Assert.Equal(ErrorCodes.CommonApiDocDisabled, offEx.ErrorCode); // 批 AR-5 口径
        // 批 AR-5:键名已由资源文案承载(zh「接口文档已关闭(ApiDoc:Enabled=false)」),此处只断稳定错误码

        // 未配置该键:实现用 GetValue("ApiDoc:Enabled", true) ⇒ 缺省 true ⇒ 正常出文档
        var @default = InvokeOpenapi(new Dictionary<string, string?>());
        Assert.Null(@default.Error);
        var ok = Assert.IsType<OkObjectResult>(@default.Ok);
        var payload = Assert.IsType<ApiResult<Dictionary<string, object?>>>(ok.Value);
        Assert.Equal(0, payload.Code);
        Assert.NotNull(payload.Data);
        Assert.True(Paths(payload.Data!).Count > 0, "缺省开启时返回的文档 paths 不应为空");

        // 显式 true:与缺省同结果(证明判定的是"值"而非"键存在与否")
        var on = InvokeOpenapi(new Dictionary<string, string?> { ["ApiDoc:Enabled"] = "true" });
        Assert.Null(on.Error);
        var onOk = Assert.IsType<OkObjectResult>(on.Ok);
        var onPayload = Assert.IsType<ApiResult<Dictionary<string, object?>>>(onOk.Value);
        Assert.Equal(Paths(payload.Data!).Count, Paths(onPayload.Data!).Count);

        _output.WriteLine($"判定:false ⇒ AppException(\"{offEx.Message}\");缺省/true ⇒ OkObjectResult 且 paths={Paths(payload.Data!).Count} 条");
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Generate()
        => ApiDocGenerator.Generate(new[] { WebEntry() }, "BaseAdminCore API", "1.0");

    private static IDictionary<string, object?> Paths(Dictionary<string, object?> doc)
        => Assert.IsAssignableFrom<IDictionary<string, object?>>(doc["paths"]);

    private static IDictionary<string, object?> Schemas(Dictionary<string, object?> doc)
    {
        var components = Assert.IsAssignableFrom<IDictionary<string, object?>>(doc["components"]);
        return Assert.IsAssignableFrom<IDictionary<string, object?>>(components["schemas"]);
    }

    private static IDictionary<string, object?> Op(IDictionary<string, object?> paths, string path, string verb)
    {
        Assert.True(paths.ContainsKey(path), $"paths 缺少 {path}");
        var byVerb = Assert.IsAssignableFrom<IDictionary<string, object?>>(paths[path]);
        Assert.True(byVerb.ContainsKey(verb), $"{path} 缺少 {verb} 操作");
        return Assert.IsAssignableFrom<IDictionary<string, object?>>(byVerb[verb]);
    }

    /// <summary>展平某操作的全部参数(name ∈ parameters 的字典项)。</summary>
    private static List<IDictionary<string, object?>> Parameters(IDictionary<string, object?> operation)
    {
        var raw = Assert.IsAssignableFrom<IEnumerable<object?>>(operation["parameters"]);
        return raw.Select(p => Assert.IsAssignableFrom<IDictionary<string, object?>>(p)).ToList();
    }

    /// <summary>取操作 200 响应的 application/json schema 节点。</summary>
    private static IDictionary<string, object?> ResponseSchema(IDictionary<string, object?> operation)
    {
        var responses = Assert.IsAssignableFrom<IDictionary<string, object?>>(operation["responses"]);
        var ok = Assert.IsAssignableFrom<IDictionary<string, object?>>(responses["200"]);
        var content = Assert.IsAssignableFrom<IDictionary<string, object?>>(ok["content"]);
        var json = Assert.IsAssignableFrom<IDictionary<string, object?>>(content["application/json"]);
        return Assert.IsAssignableFrom<IDictionary<string, object?>>(json["schema"]);
    }

    /// <summary>取名为 $ref 的引用串(不存在即失败——本用例只对"应为 $ref"的端点调用)。</summary>
    private static string ResponseRef(IDictionary<string, object?> operation)
    {
        var schema = ResponseSchema(operation);
        Assert.True(schema.ContainsKey("$ref"), "该操作的 200 响应应为 $ref 形态");
        return Assert.IsType<string>(schema["$ref"]);
    }

    /// <summary>遍历全量操作(paths → 各 verb → operation)。</summary>
    private static IEnumerable<IDictionary<string, object?>> AllOperations(IDictionary<string, object?> paths)
    {
        foreach (var byVerb in paths.Values)
        {
            foreach (var op in Assert.IsAssignableFrom<IDictionary<string, object?>>(byVerb).Values)
            {
                yield return Assert.IsAssignableFrom<IDictionary<string, object?>>(op);
            }
        }
    }

    /// <summary>
    /// 按"内存配置 → new ApiDocController → Openapi()"复现开关分支(反射,因 Test 项目无法强类型引用 Web.Entry)。
    /// 反射调用把控制器内抛出的异常包进 TargetInvocationException,此处拆回内层原异常交给用例断言真实类型。
    /// </summary>
    private static (IActionResult? Ok, Exception? Error) InvokeOpenapi(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var type = WebEntry().GetType("AdminCore.Web.Entry.Controllers.ApiDocController", throwOnError: true)!;
        var controller = Activator.CreateInstance(type, config);
        var action = type.GetMethod("Openapi", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(action);

        try
        {
            return (Assert.IsAssignableFrom<IActionResult>(action!.Invoke(controller, null)), null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return (null, ex.InnerException);
        }
    }

    /// <summary>
    /// 定位 Web.Entry 控制器程序集(调用点在用例内,Assert.Fail 直接作为测试失败抛出,不被静态初始化包装)。
    /// </summary>
    private static Assembly WebEntry() => _webEntryAssembly ??= ResolveWebEntryAssembly();

    private static Assembly ResolveWebEntryAssembly()
    {
        // ① 强类型路径:Test 项目一旦补上对 Web.Entry 的 ProjectReference,此处即命中(无需改本文件)
        var direct = Type.GetType("AdminCore.Web.Entry.Controllers.ApiDocController, AdminCore.Web.Entry", throwOnError: false);
        if (direct is not null)
        {
            return direct.Assembly;
        }

        // ② 兜底:按源码树定位 Web.Entry 构建产物(Test 项目当前未引用 Web.Entry)
        var srcRoot = FindSrcRoot();
        Assert.True(srcRoot is not null, "无法定位源码根(未找到 AdminCore.slnx)——无法定位 Web.Entry 程序集");
        var binDir = Path.Combine(srcRoot!, "Web.Entry", "bin");
        var dll = Directory.Exists(binDir)
            ? Directory.EnumerateFiles(binDir, "AdminCore.Web.Entry.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        Assert.False(dll is null,
            "未找到 Web.Entry 构建产物(src/Web.Entry/bin/**/AdminCore.Web.Entry.dll)。" +
            "根因:Test 项目未引用 Web.Entry ⇒ 本门需要既有产物。请先 dotnet build src/Web.Entry/AdminCore.Web.Entry.csproj");

        // 防"扫过期 DLL 得假绿":产物必须不早于 Web.Entry 最新源码
        var newestSource = Directory.EnumerateFiles(Path.Combine(srcRoot!, "Web.Entry"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Max(File.GetLastWriteTimeUtc);
        Assert.True(File.GetLastWriteTimeUtc(dll!) >= newestSource,
            $"Web.Entry 构建产物({File.GetLastWriteTimeUtc(dll!):O})早于最新源码({newestSource:O})——扫到的是过期控制器。" +
            "请先 dotnet build src/Web.Entry/AdminCore.Web.Entry.csproj");

        return Assembly.LoadFrom(dll!);
    }

    /// <summary>从测试输出目录向上找含 AdminCore.slnx 的目录(= src/,其下即 Web.Entry 等工程目录)。</summary>
    private static string? FindSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AdminCore.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
