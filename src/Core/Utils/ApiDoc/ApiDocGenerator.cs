using System.Reflection;
using System.Text.Json;
using AdminCore.Core.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace AdminCore.Core.Utils.ApiDoc
{
    /// <summary>
    /// 接口文档生成器(批 AG 6.7,**零新依赖自建**):反射扫描控制器程序集,产出**OpenAPI 3.0 最小可用文档**。
    /// </summary>
    /// <remarks>
    /// WHY 自建而非引 Swashbuckle/Scalar/.NET 内建 OpenApi:
    /// ① 实测 .NET 10 共享框架**不含** `AddOpenApi`/`MapOpenApi`(探测工程 CS1061)⇒ 用内建也要引 NuGet 包;
    /// ② 本仓纪律＝最小依赖(SqlSugar/StackExchange.Redis 之外不新增包);
    /// ③ 手册 6.7 的硬性要求是"生产关闭",自建版把开关握在自己手里(`ApiDoc:Enabled`)。
    /// 覆盖范围(有意裁剪,已登记):paths(路径/方法/operationId/tags/summary)、parameters([FromQuery] DTO 展平为 query 参数、
    /// 简单类型参数)、security(ApiResult 包装与 `[MenuPerm]` ⇒ Bearer + `x-perm` 权限键)、responses(200 + `$ref`)、
    /// components.schemas(DTO/输出类型属性 → 简化 JSON 类型)。**不做**:请求体 JSON Schema 的完整约束、example、try-it-out、
    /// 多态/泛型深层展开(泛型只取一层)、XML 注释摘要(需生成 XML 文档文件)。
    /// 批 BE 新增:每操作 `x-doc-zone`(`framework`/`business`)——按控制器程序集是否等于调用方传入的 business 程序集
    /// (引用相等)判定;前端 apiDoc 页据此把清单拆成「框架文档」「业务接口」两区,Swagger JSON 下载原样携带该扩展字段。
    /// 输出形状:纯 `Dictionary&lt;string, object?&gt;` 组合,不定义影子模型类——避免"文档模型与 OpenAI 规范二次漂移"。
    /// </remarks>
    public static class ApiDocGenerator
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        /// <summary>生成 OpenAPI 文档(对象图;调用方负责序列化)。</summary>
        /// <param name="assemblies">要扫描的程序集(通常含 Web.Entry 控制器程序集 + Business 业务程序集)。</param>
        /// <param name="title">文档标题。</param>
        /// <param name="version">文档版本。</param>
        /// <param name="businessAssembly">
        /// 业务程序集(批 BE):控制器所在程序集与之引用相等 ⇒ 该操作 <c>x-doc-zone</c> 标 <c>business</c>,
        /// 其余标 <c>framework</c>;null ⇒ 全部 <c>framework</c>(向后兼容)。
        /// </param>
        public static Dictionary<string, object?> Generate(Assembly[] assemblies, string title, string version,
            Assembly? businessAssembly = null)
        {
            var paths = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            var schemas = new SortedDictionary<string, object?>(StringComparer.Ordinal);

            foreach (var controller in assemblies
                         .SelectMany(a => SafeGetTypes(a))
                         .Where(IsController)
                         .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var baseRoute = ResolveRoute(controller.GetCustomAttribute<RouteAttribute>(), controller.Name) ?? string.Empty;
                var tag = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                    ? controller.Name[..^"Controller".Length]
                    : controller.Name;

                // 批 BE:框架/业务分区——控制器程序集 == businessAssembly ⇒ business,否则 framework
                var zone = businessAssembly is not null && ReferenceEquals(controller.Assembly, businessAssembly)
                    ? "business"
                    : "framework";

                foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var http = ResolveHttpMethod(method);
                    if (http is null)
                    {
                        continue;
                    }

                    var (verb, template) = http.Value;
                    var fullPath = CombinePath(baseRoute, template);
                    var operation = BuildOperation(tag, controller, method, schemas, zone);

                    if (!paths.TryGetValue(fullPath, out var byVerb) || byVerb is not Dictionary<string, object?> verbMap)
                    {
                        verbMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        paths[fullPath] = verbMap;
                    }

                    ((Dictionary<string, object?>)verbMap)[verb] = operation;
                }
            }

            return new Dictionary<string, object?>
            {
                ["openapi"] = "3.0.1",
                ["info"] = new Dictionary<string, object?>
                {
                    ["title"] = title,
                    ["version"] = version,
                    ["description"] = "由 ApiDocGenerator 反射自建(零第三方依赖);权限键见各操作的 x-perm(未标注 [MenuPerm] 的端点无该字段＝登录即可)"
                },
                ["paths"] = paths,
                ["components"] = new Dictionary<string, object?> { ["schemas"] = schemas }
            };
        }

        /// <summary>序列化为 JSON(口径与全仓一致:camelCase + 中文不转义)。</summary>
        public static string ToJson(Dictionary<string, object?> document)
            => JsonSerializer.Serialize(document, JsonOpts);

        private static Dictionary<string, object?> BuildOperation(string tag, Type controller, MethodInfo method,
            SortedDictionary<string, object?> schemas, string zone)
        {
            var operation = new Dictionary<string, object?>
            {
                ["tags"] = new[] { tag },
                ["summary"] = $"{tag}.{method.Name}",
                ["operationId"] = $"{tag}_{method.Name}",
                ["parameters"] = BuildParameters(method),
                // 批 BE:框架/业务分区标记(前端据此把接口清单拆成「框架文档」「业务接口」两区)
                ["x-doc-zone"] = zone
            };

            var perm = method.GetCustomAttribute<MenuPermAttribute>()?.Permission;
            if (!string.IsNullOrWhiteSpace(perm))
            {
                // 与 PermissionMiddleware 口径一致:标注 [MenuPerm] 的端点需登录 + 对应权限键
                operation["security"] = new object[] { new Dictionary<string, object?> { ["Bearer"] = Array.Empty<string>() } };
                operation["x-perm"] = perm;
            }

            var responseSchema = BuildResponseSchema(method, schemas);
            operation["responses"] = new Dictionary<string, object?>
            {
                ["200"] = new Dictionary<string, object?>
                {
                    ["description"] = "统一响应体(ApiResult:HTTP 恒 200 + 体业务码)",
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["application/json"] = new Dictionary<string, object?> { ["schema"] = responseSchema }
                    }
                }
            };

            return operation;
        }

        /// <summary>参数:简单类型参数 + `[FromQuery]` DTO 展平(每个可写属性一个 query 参数);`[FromBody]` 记为 body 参数。</summary>
        private static List<object?> BuildParameters(MethodInfo method)
        {
            var parameters = new List<object?>();
            foreach (var p in method.GetParameters())
            {
                var fromBody = p.GetCustomAttribute<FromBodyAttribute>() is not null;
                var fromQuery = p.GetCustomAttribute<FromQueryAttribute>() is not null;
                var fromRoute = p.GetCustomAttribute<FromRouteAttribute>() is not null;

                if (fromBody)
                {
                    parameters.Add(new Dictionary<string, object?>
                    {
                        ["name"] = p.Name,
                        ["in"] = "body",
                        ["required"] = !IsNullable(p),
                        ["schema"] = new Dictionary<string, object?> { ["type"] = "object", ["x-dotnet-type"] = p.ParameterType.Name }
                    });
                    continue;
                }

                var location = fromRoute ? "path" : "query";
                if (IsSimple(p.ParameterType) || fromQuery || fromRoute)
                {
                    // DTO 展平:record 的构造参数或 class 的可写属性
                    var members = DescribeMembers(p.ParameterType);
                    if (members.Count == 0)
                    {
                        parameters.Add(new Dictionary<string, object?>
                        {
                            ["name"] = p.Name,
                            ["in"] = location,
                            ["required"] = !IsNullable(p),
                            ["schema"] = new Dictionary<string, object?> { ["type"] = MapType(p.ParameterType) }
                        });
                    }
                    else
                    {
                        foreach (var m in members)
                        {
                            parameters.Add(new Dictionary<string, object?>
                            {
                                ["name"] = m.Name,
                                ["in"] = location,
                                ["required"] = false,
                                ["schema"] = new Dictionary<string, object?> { ["type"] = m.Type }
                            });
                        }
                    }
                }
            }

            return parameters;
        }

        /// <summary>响应 schema:`ApiResult&lt;T&gt;` → `$ref` 到 T(并把 T 的属性写进 components.schemas);非泛型 → 通用 object。</summary>
        private static Dictionary<string, object?> BuildResponseSchema(MethodInfo method, SortedDictionary<string, object?> schemas)
        {
            var returnType = method.ReturnType;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                returnType = returnType.GetGenericArguments()[0];
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ActionResult<>))
            {
                returnType = returnType.GetGenericArguments()[0];
            }

            if (returnType.IsGenericType && returnType.Name.StartsWith("ApiResult", StringComparison.Ordinal))
            {
                var payload = returnType.GetGenericArguments()[0];
                var schemaName = SchemaName(payload);
                EnsureSchema(payload, schemaName, schemas);
                return new Dictionary<string, object?> { ["$ref"] = $"#/components/schemas/{schemaName}" };
            }

            return new Dictionary<string, object?> { ["type"] = "object" };
        }

        /// <summary>登记(或补齐)某类型的 schema(属性名 → 简化 JSON 类型);泛型只展开一层。</summary>
        private static void EnsureSchema(Type type, string schemaName, SortedDictionary<string, object?> schemas)
        {
            if (schemas.ContainsKey(schemaName))
            {
                return;
            }

            // 基元/简单载荷(如 ApiResult<string>)不退化成空 properties 的 object——直接给其标量类型
            // (批 AG 测试实证:此前 ApiResult<string> ⇒ {type:object,properties:{}},语义错误)
            if (IsSimple(type))
            {
                schemas[schemaName] = new Dictionary<string, object?> { ["type"] = MapType(type) };
                return;
            }

            var props = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            schemas[schemaName] = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = props };

            foreach (var m in DescribeMembers(type))
            {
                props[m.Name] = new Dictionary<string, object?> { ["type"] = m.Type };
            }
        }

        /// <summary>描述类型成员:优先 record 构造参数(位置参数),否则 public 可写属性(排除 object/基类噪声属性的空名)。</summary>
        private static List<(string Name, string Type)> DescribeMembers(Type type)
        {
            var result = new List<(string, string)>();
            if (type == typeof(string) || IsSimple(type) || type.IsEnum)
            {
                return result;
            }

            var ctors = type.GetConstructors();
            var primary = ctors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (primary is not null && primary.GetParameters().Length > 0 && type.GetProperties().Length >= primary.GetParameters().Length)
            {
                foreach (var p in primary.GetParameters())
                {
                    result.Add((ToCamel(p.Name ?? "arg"), MapType(p.ParameterType)));
                }

                return result;
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                result.Add((ToCamel(prop.Name), MapType(prop.PropertyType)));
            }

            return result;
        }

        /// <summary>类型 → 简化 JSON 类型(可空/枚举/集合一律降级到基础类型名)。</summary>
        private static string MapType(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;

            if (t == typeof(string) || t == typeof(char) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan))
            {
                return "string";
            }

            if (t == typeof(bool))
            {
                return "boolean";
            }

            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
            {
                return "integer";
            }

            if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            {
                return "number";
            }

            if (t.IsEnum)
            {
                return "integer"; // 本仓枚举按数值序列化(见 NoticeOutput 等 DTO 注释)
            }

            if (t.IsArray || (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t)))
            {
                return "array";
            }

            return "object";
        }

        private static bool IsSimple(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
                   || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan);
        }

        private static bool IsNullable(ParameterInfo p)
        {
            if (p.HasDefaultValue)
            {
                return true;
            }

            var t = p.ParameterType;
            return Nullable.GetUnderlyingType(t) is not null || !t.IsValueType;
        }

        private static string SchemaName(Type type)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name[..tick] + "_" + string.Join("_", type.GetGenericArguments().Select(a => a.Name));
            }

            return name;
        }

        private static string ToCamel(string name)
            => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

        private static bool IsController(Type type)
            => type.IsClass && !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type)
               && type.GetCustomAttribute<ApiControllerAttribute>() is not null;

        private static (string Verb, string Template)? ResolveHttpMethod(MethodInfo method)
        {
            foreach (var attr in method.GetCustomAttributes<HttpMethodAttribute>(inherit: false))
            {
                var verb = attr.HttpMethods.FirstOrDefault()?.ToLowerInvariant();
                if (string.IsNullOrEmpty(verb))
                {
                    continue;
                }

                var template = attr.Template ?? string.Empty;
                return (verb, template);
            }

            return null;
        }

        private static string? ResolveRoute(RouteAttribute? route, string controllerName)
            => route?.Template;

        private static string CombinePath(string baseRoute, string template)
        {
            var left = (baseRoute ?? string.Empty).Trim('/');
            var right = (template ?? string.Empty).Trim('/');
            var combined = string.IsNullOrEmpty(right) ? left : string.IsNullOrEmpty(left) ? right : $"{left}/{right}";
            return "/" + combined;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t is not null).Select(t => t!);
            }
        }
    }
}