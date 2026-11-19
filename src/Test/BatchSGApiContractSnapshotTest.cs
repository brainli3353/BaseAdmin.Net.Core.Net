// 批 S-G 纵深期 D4.3·API 契约快照 + 破坏性变更检测（红线⑤门控，用户 2026-10-26 批准入门，
// 口径＝**宽松锁：路由 + HTTP 方法 + DTO 属性名集合**，不锁类型细节/默认值/描述，见纵深期计划 §四）。
// WHY 门控：约定 7 本仓＝框架权威上游，下游（IntelCore 等）只 `git pull upstream` 同步；
//   纵深期 §一 架构轴列的最大洞＝"无 API 契约快照/破坏性检测（下游只能人读 CHANGELOG）"。
//   路由或 DTO 字段改名/删除若无声进 main，下游要么编译断要么**静默改行为**，定位成本全落下游。
// 判定形态：反射控制器 ⇒ 每端点一行紧凑签名 ⇒ 与入仓基线逐行比对；不一致即红并打印差异行。
// 再基线（有意变更时）：`SG_REGEN_CONTRACT=1 dotnet test --filter ~BatchSGApiContractSnapshotTest`
//   重写基线文件后，**必须同批在 CHANGELOG 记 Breaking 段**（D6.2 口径）。
// 确定性：条目内部属性名排序、条目整体按序排序（口径同 ApiDocGenerator 的 SortedDictionary）。
// 零库：只反射已加载程序集，不连 PG/Redis ⇒ 已纳入 CLAUDE.md「测试分域三档·零库档」预设。
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace AdminCore.Test;

/// <summary>D4.3 API 契约快照门：路由/方法/DTO 属性名变更必须同批再基线。</summary>
public class BatchSGApiContractSnapshotTest
{
    /// <summary>基线文件（相对仓根）。</summary>
    private const string SnapshotRelPath = "src/Test/Contracts/api-contract.snapshot.txt";

    /// <summary>再基线开关（有意变更时显式置 1，平时不得设置）。</summary>
    private const string RegenEnvVar = "SG_REGEN_CONTRACT";

    [Fact]
    public void 契约快照与代码一致()
    {
        var current = BuildSnapshotLines();
        Assert.True(current.Count >= 120, $"契约扫描面异常(仅 {current.Count} 端点)——控制器程序集装载或解析口径失效？");

        var snapshotPath = Path.Combine(FindRepoRoot(), SnapshotRelPath);
        var regen = string.Equals(Environment.GetEnvironmentVariable(RegenEnvVar), "1", StringComparison.Ordinal);

        if (regen)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, string.Join(Environment.NewLine, current) + Environment.NewLine);
            Assert.True(current.Count > 0);
            return;
        }

        Assert.True(File.Exists(snapshotPath),
            $"契约基线缺失：{SnapshotRelPath}。生成＝`{RegenEnvVar}=1 dotnet test --filter FullyQualifiedName~BatchSGApiContractSnapshotTest`");

        var baseline = File.ReadAllLines(snapshotPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        var missing = baseline.Except(current, StringComparer.Ordinal).ToList();
        var added = current.Except(baseline, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && added.Count == 0,
            $"API 契约发生变化(纵深期 D4.3 契约快照门)：\n"
            + $"消失/变更 {missing.Count} 条：{Join(missing)}\n"
            + $"新增 {added.Count} 条：{Join(added)}\n"
            + (missing.Count > 0
                ? "⇒ 对下游属破坏性变更：确认是否本意；确属有意则同批再基线并在 CHANGELOG 记 Breaking 段。"
                : "⇒ 有意新增端点：同批再基线（SG_REGEN_CONTRACT=1），并按约定 14 核对前端/菜单/权限键是否齐。"));
    }

    [Fact]
    public void 快照生成具备确定性()
    {
        var a = BuildSnapshotLines();
        var b = BuildSnapshotLines();
        Assert.Equal(a, b);
    }

    /// <summary>反射全部控制器，产出"方法|路由|请求面|响应面"紧凑签名（已排序）。</summary>
    private static List<string> BuildSnapshotLines()
    {
        var assembly = LoadEntryAssembly();
        var lines = new List<string>();

        var controllers = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

        foreach (var controller in controllers)
        {
            var baseRoutes = controller.GetCustomAttributes<RouteAttribute>(inherit: false)
                .Select(r => r.Template)
                .DefaultIfEmpty(string.Empty)
                .ToList();

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var httpAttrs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).ToList();
                if (httpAttrs.Count == 0)
                {
                    continue;
                }

                var request = string.Join("&", method.GetParameters()
                    .Select(DescribeParameter)
                    .Where(s => s.Length > 0)
                    .OrderBy(s => s, StringComparer.Ordinal));

                var response = DescribeType(GetPayloadType(method.ReturnType));

                foreach (var attr in httpAttrs)
                {
                    foreach (var verb in attr.HttpMethods.OrderBy(v => v, StringComparer.Ordinal))
                    {
                        foreach (var baseRoute in baseRoutes)
                        {
                            var route = CombineRoute(baseRoute, attr.Template ?? string.Empty);
                            lines.Add($"{verb.ToUpperInvariant()}|{route}|{request}|{response}");
                        }
                    }
                }
            }
        }

        return lines.Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
    }

    /// <summary>请求参数签名：`body:类型{属性名…}` / `query:名` / `path:类型{属性名…}`（宽松＝只锁属性名，不锁类型）。</summary>
    private static string DescribeParameter(ParameterInfo p)
    {
        if (p.ParameterType == typeof(CancellationToken))
        {
            return string.Empty;
        }

        var kind = p.GetCustomAttribute<FromBodyAttribute>() is not null ? "body"
            : p.GetCustomAttribute<FromRouteAttribute>() is not null ? "path"
            : p.GetCustomAttribute<FromFormAttribute>() is not null ? "form"
            : p.GetCustomAttribute<FromHeaderAttribute>() is not null ? "header"
            : "query";

        // 简单参数记「位置:参数名」(参数名是契约的一部分)；复杂参数记「位置:类型{属性名}」(宽松锁＝只锁属性名)
        return IsSimple(p.ParameterType)
            ? $"{kind}:{p.Name}"
            : $"{kind}:{DescribeType(p.ParameterType)}";
    }

    /// <summary>类型签名：简单类型＝类型名；复杂类型＝`名{排序后的公共属性名}`（一层，不递归展开）。</summary>
    private static string DescribeType(Type? type)
    {
        if (type is null)
        {
            return "void";
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (IsSimple(underlying))
        {
            return underlying.Name;
        }

        var props = underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && !IsIgnored(p))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return props.Count == 0 ? underlying.Name : $"{underlying.Name}{{{string.Join(",", props)}}}";
    }

    /// <summary>响应载荷：`Task&lt;T&gt;`→T、`ActionResult&lt;T&gt;`→T、`ApiResult&lt;T&gt;`→T（口径同 ApiDocGenerator）。</summary>
    private static Type? GetPayloadType(Type returnType)
    {
        var type = returnType;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            type = type.GetGenericArguments()[0];
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            type = type.GetGenericArguments()[0];
        }

        if (type.IsGenericType && type.Name.StartsWith("ApiResult", StringComparison.Ordinal))
        {
            type = type.GetGenericArguments()[0];
        }

        return type == typeof(void) || type == typeof(IActionResult) || type == typeof(object)
            ? null
            : type;
    }

    private static bool IsSimple(Type type)
        => IsSimpleCore(Nullable.GetUnderlyingType(type) ?? type);

    private static bool IsSimpleCore(Type type)
        => type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type == typeof(object)
            || type.Name.StartsWith("FormFile", StringComparison.Ordinal)
            || typeof(Stream).IsAssignableFrom(type)
            || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            || type.Name.StartsWith("IFormFile", StringComparison.Ordinal);

    private static bool IsIgnored(PropertyInfo p)
        => p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null;

    private static string CombineRoute(string baseRoute, string template)
    {
        var combined = $"{baseRoute.Trim('/')}/{template.Trim('/')}";
        return "/" + combined.Trim('/');
    }

    private static string Join(List<string> items)
        => items.Count == 0 ? "（无）" : string.Join("\n  ", items.Take(20));

    private static Assembly LoadEntryAssembly()
    {
        try
        {
            return Assembly.Load("AdminCore.Web.Entry");
        }
        catch (Exception ex)
        {
            Assert.Fail($"控制器程序集 AdminCore.Web.Entry 装载失败（契约快照门无法执行）：{ex.GetType().Name}: {ex.Message}");
            throw; // 不可达
        }
    }

    /// <summary>从测试 bin 目录向上定位仓根（以 src/Test 存在为判据）；找不到即 Fail（fail-loud 不静默跳过）。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Test")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        Assert.Fail($"未从 {AppContext.BaseDirectory} 向上定位到含 src/Test 的仓根（契约快照用例无法执行）");
        return string.Empty; // 不可达:Assert.Fail 总抛
    }
}
