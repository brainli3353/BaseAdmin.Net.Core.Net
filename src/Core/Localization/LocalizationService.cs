using System.Text.Json;
using AdminCore.Core.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Localization
{
    /// <summary>
    /// 本地化服务(批 AL 6.6):资源装载 + 当前语言解析 + 逐级回落。
    /// 语言解析优先级(批 BP D2:注释与实现/测试对齐——BatchAL 用例断言 ① 压过 ②):
    /// ①`?lang=` query(调试/测试显式指定,最高优先) ②`Accept-Language`(主标签前缀匹配,取首个受支持者) ③配置缺省语言。
    /// 回落链:请求语言 → 缺省语言 → **key 本身**(可检出漏配,不抛不吞)。非请求上下文(后台任务/启动期)一律用缺省语言。
    /// </summary>
    public interface ILocalizationService
    {
        /// <summary>缺省语言(配置 Localization:DefaultCulture,缺省 zh-CN)。</summary>
        string DefaultCulture { get; }

        /// <summary>受支持语言清单(配置 Localization:SupportedCultures,缺省 ["zh-CN","en-US"])。</summary>
        IReadOnlyList<string> SupportedCultures { get; }

        /// <summary>当前请求语言(按优先级解析;非请求上下文=缺省语言)。</summary>
        string CurrentCulture { get; }

        /// <summary>取文案:按当前语言 → 缺省语言 → key 本身 逐级回落。</summary>
        string Get(string key);

        /// <summary>取指定语言文案(供测试/后台按语言取;未知语言回落缺省)。</summary>
        string Get(string key, string culture);

        /// <summary>取文案并按**命名占位**替换(批 AQ):<c>{name}</c> → args["name"];未提供的占位原样保留。</summary>
        string Get(string key, IReadOnlyDictionary<string, object?>? args);
    }

    /// <inheritdoc />
    // 批 BP D1:不贴 [Service]——本服务经 AddAdminLocalization 显式注册为 Singleton(资源装载一次,
    // 头注口径);若再贴 [Service](缺省 Scoped)则同接口双注册,.NET 按「最后注册者生效」取 Scoped
    // ⇒ 每请求重解析 Lang.*.json + 生命周期随注册顺序漂移(批 AL 时代 G23 即踩此形态)。
    public class LocalizationService : ILocalizationService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<LocalizationService> _logger;

        /// <summary>语言 → (key → 文案)。装载一次,进程内只读。</summary>
        private readonly Dictionary<string, Dictionary<string, string>> _resources = new(StringComparer.OrdinalIgnoreCase);

        public LocalizationService(IHttpContextAccessor httpContextAccessor, IConfiguration configuration,
            ILogger<LocalizationService> logger)
        {
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;

            DefaultCulture = configuration["Localization:DefaultCulture"] is { Length: > 0 } dc ? dc : "zh-CN";
            var supported = configuration.GetSection("Localization:SupportedCultures").Get<string[]>();
            SupportedCultures = supported is { Length: > 0 } ? supported : new[] { "zh-CN", "en-US" };

            LoadResources();

            // 装载静态解析器(供 new AppException("资源键") 在任意位置翻译;单例构造一次,幂等覆盖)
            L.Resolver = Get;
        }

        /// <inheritdoc />
        public string DefaultCulture { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> SupportedCultures { get; }

        /// <inheritdoc />
        public string CurrentCulture => ResolveCurrentCulture();

        /// <inheritdoc />
        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }

            return Lookup(key, ResolveCurrentCulture()) ?? key;
        }

        /// <inheritdoc />
        public string Get(string key, string culture)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }

            return Lookup(key, NormalizeCulture(culture) ?? DefaultCulture) ?? key;
        }

        /// <inheritdoc />
        public string Get(string key, IReadOnlyDictionary<string, object?>? args)
            => Format(Get(key), args);

        /// <summary>命名占位替换(批 AQ):仅替换 args 中出现的键;缺失键原样保留(便于检出漏传参数)。</summary>
        public static string Format(string text, IReadOnlyDictionary<string, object?>? args)
        {
            if (string.IsNullOrEmpty(text) || args is null || args.Count == 0)
            {
                return text;
            }

            foreach (var pair in args)
            {
                text = text.Replace("{" + pair.Key + "}", pair.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }

            return text;
        }

        // 逐级回落:请求语言 → 缺省语言 → null(调用方返回 key)
        private string? Lookup(string key, string culture)
        {
            if (_resources.TryGetValue(culture, out var map) && map.TryGetValue(key, out var text))
            {
                return text;
            }

            if (!string.Equals(culture, DefaultCulture, StringComparison.OrdinalIgnoreCase)
                && _resources.TryGetValue(DefaultCulture, out var fallback)
                && fallback.TryGetValue(key, out var fallbackText))
            {
                return fallbackText;
            }

            return null;
        }

        // 优先级:?lang= query(显式,便于测试/调试) > Accept-Language > 缺省
        private string ResolveCurrentCulture()
        {
            var context = _httpContextAccessor.HttpContext;
            if (context is null)
            {
                return DefaultCulture;
            }

            var queryLang = context.Request.Query["lang"].ToString();
            if (NormalizeCulture(queryLang) is { } fromQuery)
            {
                return fromQuery;
            }

            var header = context.Request.Headers.AcceptLanguage.ToString();
            if (!string.IsNullOrWhiteSpace(header))
            {
                // Accept-Language 形如 "en-US,en;q=0.9":逐个取主标签,前缀匹配受支持语言
                foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var tag = part.Split(';', 2)[0].Trim();
                    if (NormalizeCulture(tag) is { } matched)
                    {
                        return matched;
                    }
                }
            }

            return DefaultCulture;
        }

        // 归一:精确匹配受支持语言优先,其次主标签前缀匹配(en → en-US);不支持返回 null
        private string? NormalizeCulture(string? culture)
        {
            if (string.IsNullOrWhiteSpace(culture))
            {
                return null;
            }

            var value = culture.Trim();
            foreach (var supported in SupportedCultures)
            {
                if (string.Equals(supported, value, StringComparison.OrdinalIgnoreCase))
                {
                    return supported;
                }
            }

            var primary = value.Split('-')[0];
            foreach (var supported in SupportedCultures)
            {
                if (string.Equals(supported.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase))
                {
                    return supported;
                }
            }

            return null;
        }

        // 资源装载:Resources/Lang.<culture>.json。文件缺失/解析失败 = 响亮告警(不静默吞)
        private void LoadResources()
        {
            // 装载来源:①内置默认(保底可用,不依赖构建传递) ②嵌入资源 ③输出目录/工作目录 Resources 文件(覆盖)
            var dirs = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Resources"),
                Path.Combine(Directory.GetCurrentDirectory(), "Resources")
            };

            // ① 内置默认:先铺底,保证任何宿主都能翻译(测试/最小宿主/资源未传递)
            if (_resources.TryAdd("zh-CN", new Dictionary<string, string>(LocalizationDefaults.ZhCn, StringComparer.OrdinalIgnoreCase)))
            {
            }

            if (_resources.TryAdd("en-US", new Dictionary<string, string>(LocalizationDefaults.EnUs, StringComparer.OrdinalIgnoreCase)))
            {
            }

            foreach (var culture in SupportedCultures)
            {
                var loaded = false;

                foreach (var dir in dirs)
                {
                    var file = Path.Combine(dir, $"Lang.{culture}.json");
                    if (!File.Exists(file) || loaded)
                    {
                        continue;
                    }

                    try
                    {
                        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                        if (map is { Count: > 0 })
                        {
                            _resources[culture] = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
                            loaded = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "本地化资源解析失败:{File}(该语言文案将回落 key)", file);
                        break;
                    }
                }

                if (!loaded)
                {
                    _logger.LogWarning("本地化资源未找到:{Culture}(查找目录 {Dirs})——该语言文案将回落 key 本身",
                        culture, string.Join(" / ", dirs));
                }
            }

            _logger.LogInformation("本地化就绪:缺省 {Default},受支持 [{Supported}],已装载 {Loaded} 种",
                DefaultCulture, string.Join(",", SupportedCultures), string.Join(",", _resources.Keys));
        }
    }
}