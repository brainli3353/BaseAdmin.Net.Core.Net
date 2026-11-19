using System.Security.Claims;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Http;

namespace AdminCore.Test.Infrastructure;

/// <summary>
/// 测试用可造身份访问器（2026-10-27 ㉖ 配置写侧闸适配）。
/// <para>WHY 提到 Infrastructure：超管/非超管身份已是多个测试片的公共前置（批 S-G 菜单配置片、
/// 批 CN 单登录、批 I 日志片），各抄一份 Claim 构造必然漂移；本类为唯一构造点。</para>
/// <para>身份口径与 <see cref="UserManager"/> 实读一致：<c>IsAuthenticated ⇐ Identity.IsAuthenticated</c>、
/// <c>UserId ⇐ Claim UserId</c>、<c>IsSuperAdmin ⇐ Claim AccountType == 999</c>。默认 <see cref="HttpContext"/>
/// 为 null（等价未认证基线）；需要"已认证"时用 <see cref="Actor"/> / <see cref="SuperAdmin"/>。</para>
/// <para>可写属性 <see cref="HttpContext"/> 与既有测试内联实现同形，便于"只在写配置那一段挂身份、其余还原"。</para>
/// </summary>
public sealed class TestActorAccessor : IHttpContextAccessor
{
    /// <summary>当前请求上下文（可写）；null＝未认证基线。</summary>
    public HttpContext? HttpContext { get; set; }

    /// <summary>未认证基线（HttpContext 为 null）。</summary>
    public static TestActorAccessor Anonymous() => new();

    /// <summary>已认证 + 指定 AccountType（"999"＝超管，其余＝普通/探针用户）。</summary>
    public static TestActorAccessor Actor(long userId, string accountType)
        => new() { HttpContext = BuildContext(userId, accountType) };

    /// <summary>已认证 + 超管（AccountType 取 <see cref="UserManager.SuperAdminAccountType"/>，不写死字面量）。</summary>
    public static TestActorAccessor SuperAdmin(long userId = 1L)
        => Actor(userId, UserManager.SuperAdminAccountType.ToString());

    /// <summary>就地切换身份（用于"仅写配置段挂超管、结束后还原"的夹具）。</summary>
    public void SetActor(long userId, string accountType) => HttpContext = BuildContext(userId, accountType);

    /// <summary>就地切换为超管（AccountType 取 <see cref="UserManager.SuperAdminAccountType"/>，不写死字面量）。</summary>
    public void SetSuperAdmin(long userId = 1L) => SetActor(userId, UserManager.SuperAdminAccountType.ToString());

    /// <summary>还原为未认证基线。</summary>
    public void Clear() => HttpContext = null;

    private static HttpContext BuildContext(long userId, string accountType)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(JwtClaimNames.UserId, userId.ToString()),
            new Claim(JwtClaimNames.Account, $"sgx_tester_{userId}"),
            new Claim(JwtClaimNames.AccountType, accountType)
        ], "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }
}
