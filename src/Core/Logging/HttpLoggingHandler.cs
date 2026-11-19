using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Option;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminCore.Core.Logging
{
    /// <summary>
    /// Http 出站日志(5.3):<see cref="DelegatingHandler"/> 挂在 HttpClient 管道上,记录本进程对外发起的
    /// HTTP 调用(方法/URL/请求头/请求体/状态码/响应体/耗时/异常)→ 发布 "http_log" 事件,
    /// 由 HttpLogSubscriber 经事件总线异步落库 <see cref="SysLogHttp"/>。
    /// </summary>
    /// <remarks>
    /// 接入方式(业务 opt-in,框架层零默认启用——同批 R 二级缓存/批 P 数据执行钩子口径):
    /// <c>services.AddHttpClient("x").AddHttpMessageHandler&lt;HttpLoggingHandler&gt;()</c>
    /// (或 AddHttpOutboundLogging 后按名/类型挂);未挂的 HttpClient 不受影响。
    /// 记录纪律:
    /// ① <b>不影响业务</b>——发布事件失败只记 Warning;异常请求**记录后原样重抛**(不吞业务异常);
    /// ② <b>截断</b>——请求/响应体按 <see cref="HttpLogOptions.MaxBodyLength"/> 截断(MaxBodyLength&lt;=0 记体占位);
    /// ③ <b>脱敏</b>——敏感请求头(Authorization/Cookie/X-API-KEY 等 + 配置追加)值一律 "***",库内不落凭据;
    /// ④ <b>只记文本体</b>——非文本 Content-Type(二进制/流)只记类型占位;文本体在 Content-Length 已知且超
    /// <see cref="MaxReadableRequestBodyBytes"/> 时直接跳过读取——**注意**:Content-Length 未知(chunked)或低于该阈值时
    /// 仍会先整body读入内存(ReadAsStringAsync 语义)再按 MaxBodyLength 截断,即<b>截断只约束落库体积、不约束瞬时内存</b>
    /// (批 AD 测试实证;故上限设在 1MB 级,防"日志能力把大请求打爆内存"只对有声明长度者成立);
    /// ⑤ <b>可排除</b>——<see cref="HttpLogOptions.ExcludeHosts"/> 命中主机(后缀匹配)直接放行不记录。
    /// 耗时口径:<c>ElapsedMs</c> = 发起至 base.SendAsync 返回——默认完成模式为 ResponseContentRead,
    /// 即返回时**响应内容已读入缓冲**,故耗时含响应体读取时间(不是"仅到响应头");异常路径则不设 StatusCode 并记 Exception。
    /// 生命周期:DelegatingHandler 必须 Transient(AddHttpClient 的 handler 生命周期契约),见 AddHttpOutboundLogging。
    /// </remarks>
    public class HttpLoggingHandler : DelegatingHandler
    {
        /// <summary>默认掩码请求头(大小写不敏感;配置 MaskHeaders 为追加项)。</summary>
        private static readonly HashSet<string> DefaultMaskHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Set-Cookie",
            "X-API-KEY",
            "X-Auth-Token",
            "X-Token",
            "accessKey",
            "sign"
        };

        /// <summary>JSON 序列化口径(与全仓一致:camelCase + 中文不转义;线程安全可共享)。</summary>
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>判定为"可记录文本体"的内容类型片段(小写比较)。</summary>
        private static readonly string[] TextContentTypeHints =
        {
            "json", "xml", "text/", "x-www-form-urlencoded", "javascript", "html", "csv"
        };

        /// <summary>
        /// 请求体读取上限(字节):超过则**不读体**(记占位)——记录只保留 MaxBodyLength 个字符,
        /// 但读取动作本身会把整个 body 缓冲进内存(ReadAsStringAsync 语义),故对已知超大 body 直接跳过,
        /// 防"日志能力把大请求打爆内存"(响应体不设此限:base.SendAsync 默认已把响应读入缓冲,无额外成本)。
        /// </summary>
        private const long MaxReadableRequestBodyBytes = 1_000_000;

        private readonly HttpLogOptions _options;
        private readonly IEventPublisher? _publisher;
        private readonly IHttpContextAccessor? _httpContextAccessor;
        private readonly ILogger<HttpLoggingHandler> _logger;

        public HttpLoggingHandler(
            IOptions<HttpLogOptions> options,
            ILogger<HttpLoggingHandler> logger,
            IEventPublisher? publisher = null,
            IHttpContextAccessor? httpContextAccessor = null)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // 可选依赖(批 X/R 同口径降级):最小宿主无事件总线/无 Http 上下文时,仅少落库/少身份字段,不抛
            _publisher = publisher;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>
        /// 发送并记录:开关关/命中排除主机 → 直接透传(零开销);否则记录后发布 http_log 事件。
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_options.Enabled || IsExcludedHost(request.RequestUri?.Host))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var log = new SysLogHttp
            {
                HttpMethod = request.Method.Method,
                RequestUrl = Truncate(request.RequestUri?.ToString(), 1024),
                RequestHeaders = BuildHeadersJson(request.Headers, request.Content?.Headers),
                TraceId = _httpContextAccessor?.HttpContext?.TraceIdentifier
            };

            FillCurrentUser(log);

            var requestBody = await ReadBodyAsync(request.Content, cancellationToken).ConfigureAwait(false);
            log.RequestBody = requestBody;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                log.StatusCode = (int)response.StatusCode;
                log.ElapsedMs = stopwatch.ElapsedMilliseconds;
                log.ResponseBody = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                await PublishAsync(log).ConfigureAwait(false);
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // 异常请求:记录(HasException + 异常全文)后**原样重抛**——出站日志不得改变业务异常语义
                log.HasException = true;
                log.ElapsedMs = stopwatch.ElapsedMilliseconds;
                log.Exception = Truncate(ex.ToString(), _options.MaxBodyLength > 0 ? Math.Max(_options.MaxBodyLength, 4096) : 4096);
                await PublishAsync(log).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>命中排除主机(后缀匹配,大小写不敏感)。</summary>
        private bool IsExcludedHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host) || _options.ExcludeHosts.Length == 0)
            {
                return false;
            }

            return _options.ExcludeHosts
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Any(h => host.EndsWith(h.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 读取请求/响应体:仅文本类内容类型记录且按 MaxBodyLength 截断;非文本记类型占位;
        /// MaxBodyLength&lt;=0 记 "(未记录:MaxBodyLength<=0)"。
        /// </summary>
        private async Task<string?> ReadBodyAsync(HttpContent? content, CancellationToken cancellationToken)
        {
            if (content is null)
            {
                return null;
            }

            var contentType = content.Headers.ContentType?.ToString();
            if (!IsTextContent(contentType))
            {
                return $"(未记录:非文本内容 {contentType ?? "未知类型"})";
            }

            if (_options.MaxBodyLength <= 0)
            {
                return "(未记录:MaxBodyLength<=0)";
            }

            // 已知超大 body(Content-Length 可读且超限)→ 不读体,避免整body缓冲进内存(见常量 WHY)
            var declaredLength = content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > MaxReadableRequestBodyBytes)
            {
                return $"(未记录:体积 {declaredLength.Value} 字节超出读取上限 {MaxReadableRequestBodyBytes})";
            }

            try
            {
                var text = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Truncate(text, _options.MaxBodyLength);
            }
            catch (Exception ex)
            {
                // 读体失败不影响业务请求(内容可能已被消费/流已关闭)
                return $"(读取失败:{ex.GetType().Name})";
            }
        }

        /// <summary>是否文本类内容类型。</summary>
        private static bool IsTextContent(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            var lower = contentType.ToLowerInvariant();
            return TextContentTypeHints.Any(hint => lower.Contains(hint));
        }

        /// <summary>请求头 → JSON(多值头逗号拼接;敏感头值掩码)。</summary>
        private string? BuildHeadersJson(System.Net.Http.Headers.HttpRequestHeaders headers,
            System.Net.Http.Headers.HttpContentHeaders? contentHeaders)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                dict[header.Key] = MaskIfSensitive(header.Key, string.Join(",", header.Value));
            }

            if (contentHeaders is not null)
            {
                foreach (var header in contentHeaders)
                {
                    dict[header.Key] = MaskIfSensitive(header.Key, string.Join(",", header.Value));
                }
            }

            return dict.Count == 0 ? null : JsonSerializer.Serialize(dict, JsonOpts);
        }

        /// <summary>敏感头掩码(默认集合 + 配置追加)。</summary>
        private string MaskIfSensitive(string name, string value)
        {
            if (DefaultMaskHeaders.Contains(name) || _options.MaskHeaders.Any(h => string.Equals(h?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                return "***";
            }

            return value;
        }

        /// <summary>填当前发起方身份(Account/RealName 经 JwtClaimNames;无请求上下文为空——后台任务发起的出站调用)。</summary>
        private void FillCurrentUser(SysLogHttp log)
        {
            var user = _httpContextAccessor?.HttpContext?.User;
            if (user is null)
            {
                return;
            }

            log.Account = user.FindFirst(JwtClaimNames.Account)?.Value;
            log.RealName = user.FindFirst(JwtClaimNames.RealName)?.Value;
        }

        /// <summary>
        /// 发布 http_log 事件(失败只记 Warning——出站日志是审计增强件,不得影响业务调用链)。
        /// 无事件总线(最小宿主)时降级为只记 ILogger。
        /// </summary>
        private async Task PublishAsync(SysLogHttp log)
        {
            if (_publisher is null)
            {
                _logger.LogDebug("Http 出站调用(无事件总线,未落库): {Method} {Url} → {Status} {Elapsed}ms",
                    log.HttpMethod, log.RequestUrl, log.StatusCode, log.ElapsedMs);
                return;
            }

            try
            {
                await _publisher.PublishAsync("http_log", log).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Http 出站日志发布失败(业务调用不受影响): {Method} {Url}",
                    log.HttpMethod, log.RequestUrl);
            }
        }

        /// <summary>按最大长度截断(超出追加 "(truncated)";为避免截断多字节字符,按字符截断)。</summary>
        private static string? Truncate(string? text, int maxLength)
        {
            if (text is null)
            {
                return null;
            }

            if (maxLength <= 0 || text.Length <= maxLength)
            {
                return text;
            }

            var builder = new StringBuilder(maxLength + 12);
            builder.Append(text, 0, maxLength).Append("(truncated)");
            return builder.ToString();
        }
    }
}