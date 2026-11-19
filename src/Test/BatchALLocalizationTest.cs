using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 AL 6.6「多语言」验证(纯内存,无 PG/Redis 依赖):
    /// 语言解析优先级 / 资源命中(zh↔en 文案不同) / 缺键回落 / AppException 资源键渐进翻译 /
    /// 无 HttpContext 用缺省语言 / cultures 服务口径(default+supported+current)。
    /// </summary>
    public class BatchALLocalizationTest
    {
        private readonly ITestOutputHelper _output;

        public BatchALLocalizationTest(ITestOutputHelper output) => _output = output;

        private static LocalizationService Build(HttpContext? context = null)
        {
            var accessor = new HttpContextAccessor { HttpContext = context };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            return new LocalizationService(accessor, config, NullLogger<LocalizationService>.Instance);
        }

        private static DefaultHttpContext Context(string? acceptLanguage = null, string? queryLang = null)
        {
            var ctx = new DefaultHttpContext();
            if (acceptLanguage is not null)
            {
                ctx.Request.Headers.AcceptLanguage = acceptLanguage;
            }

            if (queryLang is not null)
            {
                ctx.Request.QueryString = new QueryString($"?lang={queryLang}");
            }

            return ctx;
        }

        [Fact]
        public void 用例1_解析优先级_四态()
        {
            Assert.Equal("en-US", Build(Context(acceptLanguage: "en-US")).CurrentCulture);
            Assert.Equal("en-US", Build(Context(acceptLanguage: "en")).CurrentCulture);
            Assert.Equal("en-US", Build(Context(acceptLanguage: "fr-FR,en;q=0.9")).CurrentCulture);
            Assert.Equal("zh-CN", Build(Context(acceptLanguage: "fr-FR")).CurrentCulture);
            Assert.Equal("zh-CN", Build(Context(acceptLanguage: "en-US", queryLang: "zh-CN")).CurrentCulture);
            Assert.Equal("zh-CN", Build(Context()).CurrentCulture);
            Assert.Equal("zh-CN", Build(null).CurrentCulture);
        }

        [Fact]
        public void 用例2_资源命中_中英文案不同()
        {
            // WHY 每次构造后立即断言:HttpContextAccessor.HttpContext 是 **AsyncLocal** 存储
            // (同一执行上下文共享一个槽)——先建 zh 再建 en 会让 zh.Get() 读到 en 的上下文。
            var zhText = Build(Context(acceptLanguage: "zh-CN")).Get("auth.login_failed");
            Assert.Equal("账号或密码错误", zhText);

            var enText = Build(Context(acceptLanguage: "en-US")).Get("auth.login_failed");
            Assert.Equal("Incorrect account or password", enText);

            Assert.NotEqual(zhText, enText);
            Assert.Equal("Incorrect account or password", Build(Context(acceptLanguage: "zh-CN")).Get("auth.login_failed", "en-US"));
        }

        [Fact]
        public void 用例3_缺键回落_返回key本身()
        {
            Assert.Equal("no.such.key", Build(Context(acceptLanguage: "en-US")).Get("no.such.key"));
            Assert.Equal("", Build(Context(acceptLanguage: "en-US")).Get(""));
        }

        [Fact]
        public void 用例4_AppException_资源键翻译与存量文案两态()
        {
            _ = Build(Context(acceptLanguage: "en-US"));

            var translated = new AppException("auth.login_failed");
            // WHY 宽容断言:L.Resolver 是进程级静态(生产为单例,测试多实例会互相覆盖)
            Assert.NotEqual("auth.login_failed", translated.Message);
            Assert.True(translated.Message is "账号或密码错误" or "Incorrect account or password", translated.Message);

            var legacy = new AppException("角色不存在");
            Assert.Equal("角色不存在", legacy.Message);

            Assert.Equal(400, new AppException("common.param_invalid", 400).Code);
        }

        [Fact]
        public void 用例5_无请求上下文_用缺省语言不抛()
        {
            var svc = Build(null);

            Assert.Equal("zh-CN", svc.CurrentCulture);
            Assert.Equal("账号或密码错误", svc.Get("auth.login_failed"));
        }

        [Fact]
        public void 用例6_cultures口径_缺省与受支持与当前()
        {
            var svc = Build(Context(acceptLanguage: "en-US"));

            Assert.Equal("zh-CN", svc.DefaultCulture);
            Assert.Contains("zh-CN", svc.SupportedCultures);
            Assert.Contains("en-US", svc.SupportedCultures);
            Assert.Equal("en-US", svc.CurrentCulture);
        }
    }
}