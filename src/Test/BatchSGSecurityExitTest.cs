using System.Reflection;
using AdminCore.Core.Security;
using AdminCore.Web.Entry.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 S-G 纵深期·安全出口止血 零库档测试:
/// ① <see cref="LogDataRedactor"/> 审计日志脱敏(敏感属性名遮蔽/otpauth 值遮蔽/长度截断/不抛异常);
/// ② <see cref="DemoController"/> 认证收口反射断言(类级 [Authorize] 覆盖全部动作、无 [AllowAnonymous])。
/// 纯函数 + 反射,无 PG/Redis 依赖。
/// </summary>
public class BatchSGSecurityExitTest
{
    // 用例 1:嵌套 JSON 中敏感属性名整值遮蔽,普通字段(account/realName)保留
    [Fact]
    public void 用例1_嵌套Json敏感字段遮蔽_普通字段保留()
    {
        const string json = """
        {"account":"admin","realName":"葫芦爷","password":"P@ss",
         "profile":{"secret":"s3cr3t","otpauthUri":"otpauth://totp/x","token":"tok","accessKey":"AK","realName":"inner"}}
        """;

        var result = LogDataRedactor.Redact(json);

        Assert.NotNull(result);
        // 普通字段保留
        Assert.Contains("\"account\":\"admin\"", result);
        Assert.Contains("\"realName\":\"葫芦爷\"", result);
        Assert.Contains("\"realName\":\"inner\"", result);
        // 敏感字段全部 *** 且原文不残留
        Assert.Contains("\"password\":\"***\"", result);
        Assert.Contains("\"secret\":\"***\"", result);
        Assert.Contains("\"otpauthUri\":\"***\"", result);
        Assert.Contains("\"token\":\"***\"", result);
        Assert.Contains("\"accessKey\":\"***\"", result);
        Assert.DoesNotContain("P@ss", result);
        Assert.DoesNotContain("s3cr3t", result);
        Assert.DoesNotContain("\"tok\"", result);
        Assert.DoesNotContain("otpauth://", result);
    }

    // 用例 2:非敏感属性名但字符串值含 otpauth:// 时整体遮蔽
    [Fact]
    public void 用例2_字符串值含otpauth_整体遮蔽()
    {
        const string json = """{"note":"otpauth://totp/x?secret=ABC","tip":"normal"}""";

        var result = LogDataRedactor.Redact(json);

        Assert.NotNull(result);
        Assert.Contains("\"note\":\"***\"", result);
        Assert.Contains("\"tip\":\"normal\"", result);
        Assert.DoesNotContain("otpauth://", result);
    }

    // 用例 3:超过 4096 截断并追加 ...[truncated]
    [Fact]
    public void 用例3_超长截断并标记()
    {
        var json = "{\"blob\":\"" + new string('a', 5000) + "\"}";

        var result = LogDataRedactor.Redact(json);

        Assert.NotNull(result);
        Assert.True(result.Length > LogDataRedactor.MaxLength, "超限应被截断前仍保留上限长度");
        Assert.EndsWith(LogDataRedactor.TruncatedSuffix, result);
        Assert.Contains("[truncated]", result);
    }

    // 用例 4:null/空白 → null;非法 JSON 不抛异常且只截断原文
    [Fact]
    public void 用例4_空值与非法Json不抛异常()
    {
        Assert.Null(LogDataRedactor.Redact(null));
        Assert.Null(LogDataRedactor.Redact(string.Empty));
        Assert.Null(LogDataRedactor.Redact("   \t  "));

        const string invalid = "{not-json";
        var result = LogDataRedactor.Redact(invalid);
        Assert.Equal(invalid, result);
    }

    // 用例 5:DemoController 全部动作方法类级 [Authorize] 覆盖、无 [AllowAnonymous]
    [Fact]
    public void 用例5_DemoController动作方法认证收口()
    {
        var type = typeof(DemoController);

        var classAuthorized = type.GetCustomAttribute<AuthorizeAttribute>(inherit: true) is not null;
        Assert.True(classAuthorized, "DemoController 缺类级 [Authorize](匿名可达须收口)");

        var actions = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(inherit: true).Any(a => a is HttpMethodAttribute))
            .ToList();

        Assert.NotEmpty(actions); // 防止反射集为空导致断言空转

        foreach (var method in actions)
        {
            var attrs = method.GetCustomAttributes(inherit: true);
            Assert.DoesNotContain(attrs, a => a is AllowAnonymousAttribute);
            Assert.True(
                classAuthorized || attrs.Any(a => a is AuthorizeAttribute),
                $"{type.Name}.{method.Name} 既无类级也无方法级 [Authorize]");
        }
    }
}