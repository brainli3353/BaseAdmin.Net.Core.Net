using System.Reflection;
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
    /// 批 AQ「错误码体系」验证(纯内存,无 PG/Redis 依赖):
    /// 错误码透传与命名参数 / 中英按请求语言渲染 / 存量口径行为不变 / 未知键回落 / **全部错误码 zh+en 资源键全覆盖**。
    /// </summary>
    public class BatchAQErrorCodeTest
    {
        private readonly ITestOutputHelper _output;

        public BatchAQErrorCodeTest(ITestOutputHelper output) => _output = output;

        private static LocalizationService Build(HttpContext? context = null)
        {
            var accessor = new HttpContextAccessor { HttpContext = context };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            return new LocalizationService(accessor, config, NullLogger<LocalizationService>.Instance);
        }

        private static DefaultHttpContext Context(string? acceptLanguage = null)
        {
            var ctx = new DefaultHttpContext();
            if (acceptLanguage is not null)
            {
                ctx.Request.Headers.AcceptLanguage = acceptLanguage;
            }

            return ctx;
        }

        [Fact]
        public void 用例1_错误码与命名参数透传()
        {
            var ex = AppException.With(ErrorCodes.UserAccountExists, "account", "admin");

            Assert.Equal(ErrorCodes.UserAccountExists, ex.ErrorCode);
            Assert.Equal("admin", ex.Args!["account"]);
            Assert.Equal(1, ex.Code);
            // Message 暂存错误码本身(日志可检索),对外文案由统一出口渲染
            Assert.Equal(ErrorCodes.UserAccountExists, ex.Message);
        }

        [Fact]
        public void 用例2_按请求语言渲染_含占位替换()
        {
            var args = new Dictionary<string, object?> { ["account"] = "admin" };

            var zh = Build(Context("zh-CN")).Get(ErrorCodes.UserAccountExists, args);
            var en = Build(Context("en-US")).Get(ErrorCodes.UserAccountExists, args);

            Assert.Equal("账号已存在：admin", zh);
            Assert.Equal("Account already exists: admin", en);
        }

        [Fact]
        public void 用例3_存量口径行为不变()
        {
            var ex = new AppException("存量中文消息");

            Assert.Null(ex.ErrorCode);
            Assert.Null(ex.Args);
            Assert.Equal("存量中文消息", ex.Message);
            Assert.Equal(1, ex.Code);

            var withCode = new AppException("存量中文消息", 400);
            Assert.Equal(400, withCode.Code);
            Assert.Null(withCode.ErrorCode);
        }

        [Fact]
        public void 用例4_未知错误码回落key本身()
        {
            // 回落 key 本身 => 漏配可检出(不抛不吞,与批 AL 口径一致)
            // 显式类型:Get(key, null) 在 (string culture) 与 (dict args) 两个重载间二义
            Assert.Equal("nope.missing", Build(Context()).Get("nope.missing", (IReadOnlyDictionary<string, object?>?)null));
            Assert.Equal("nope.missing", Build(Context("en-US")).Get("nope.missing", new Dictionary<string, object?>()));
        }

        [Fact]
        public void 用例5_全部错误码中英资源键全覆盖()
        {
            // 硬判据:ErrorCodes 每个常量都必须在 zh 与 en 有真实文案(防「新增错误码漏配资源键」)
            var codes = typeof(ErrorCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToArray();

            Assert.NotEmpty(codes);

            var zh = Build(Context("zh-CN"));
            var en = Build(Context("en-US"));
            var missingZh = codes.Where(k => zh.Get(k) == k).ToArray();
            var missingEn = codes.Where(k => en.Get(k) == k).ToArray();

            _output.WriteLine($"错误码总数={codes.Length}");
            Assert.True(missingZh.Length == 0, "zh 缺资源键: " + string.Join(",", missingZh));
            Assert.True(missingEn.Length == 0, "en 缺资源键: " + string.Join(",", missingEn));
        }
    }
}