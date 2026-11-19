namespace AdminCore.Core.Option;

/// <summary>
/// 站点身份配置选项(批 AY,G24):配置节 <c>App</c>,节名=类名约定。
/// 单一真源——运行期品牌名/全名/标题后缀都从这里取(env <c>App__Name</c> 等可覆盖,配置链优先级最高);
/// 下游/多套独立服务部署只改这一处即可区分:① 前端品牌与浏览器标题 ② OTP otpauth issuer
/// (③ 后端启动自检日志既有消费不变)。前端构建期 Vite 变量只作兜底,不再要求重新构建产物。
/// </summary>
public sealed class AppOptions
{
    /// <summary>应用名/品牌名(缺省回落见 PostConfigure;OTP issuer 缺省亦回落此值)。</summary>
    public string? Name { get; set; }

    /// <summary>全名(登录页页脚等);空则回落 <see cref="Name"/>。</summary>
    public string? FullName { get; set; }

    /// <summary>浏览器标题后缀(如「管理台」);空则由前端按构建期兜底。</summary>
    public string? TitleSuffix { get; set; }
}