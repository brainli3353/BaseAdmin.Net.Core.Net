using AdminCore.Core.Localization;

namespace AdminCore.Core.Exceptions;

/// <summary>
/// 业务异常(批 AQ 起推荐用 <see cref="Of"/>)——两种口径并存,渐进迁移:
/// ① **错误码口径(推荐)**:<c>AppException.Of(ErrorCodes.UserNotFound, args)</c> —— 只携带**稳定错误码**与命名参数,
///    解释文案由全局出口 <c>ExceptionMiddleware</c> 按**请求语言**渲染(资源见 Lang.*.json / LocalizationDefaults),
///    业务代码不再写死中文;
/// ② **存量口径(兼容)**:<c>new AppException("中文消息")</c> —— 原样返回,行为与批 AQ 前**完全一致**(ErrorCode=null)。
/// 两者都由 ExceptionMiddleware 统一转 ApiResult(HTTP 200 + code + errCode + msg),不泄露堆栈。
/// </summary>
public class AppException : Exception
{
    /// <summary>业务码:作为 ApiResult.Code 返回(兼容既有判断);默认 1</summary>
    public int Code { get; }

    /// <summary>稳定错误码(批 AQ):与 Lang.*.json 键同源;存量构造为 null</summary>
    public string? ErrorCode { get; }

    /// <summary>错误码文案的命名占位参数(如 <c>{name}</c>)</summary>
    public IReadOnlyDictionary<string, object?>? Args { get; }

    /// <summary>存量口径:直接给中文消息(ErrorCode=null,行为不变)</summary>
    /// <param name="message">面向用户的中文错误消息</param>
    /// <param name="code">业务码,默认 1</param>
    public AppException(string message, int code = 1) : base(L.T(message))
    {
        Code = code;
    }

    /// <summary>存量口径(带内层异常):保留原始异常链便于日志排查</summary>
    public AppException(string message, int code, Exception innerException)
        : base(L.T(message), innerException)
    {
        Code = code;
    }

    private AppException(string errorCode, IReadOnlyDictionary<string, object?>? args, int code, Exception? inner)
        : base(errorCode, inner) // Message 暂存错误码:日志按错误码可检索;对外文案由出口按请求语言渲染
    {
        ErrorCode = errorCode;
        Args = args;
        Code = code;
    }

    /// <summary>错误码口径(推荐):出口按请求语言渲染解释文案</summary>
    /// <param name="errorCode">稳定错误码,取值见 <see cref="ErrorCodes"/></param>
    /// <param name="args">命名占位参数(如 new Dictionary&lt;string, object?&gt; { ["name"] = account })</param>
    /// <param name="code">业务码,默认 1</param>
    public static AppException Of(string errorCode, IReadOnlyDictionary<string, object?>? args = null, int code = 1)
        => new(errorCode, args, code, null);

    /// <summary>错误码口径(带内层异常)</summary>
    public static AppException Of(string errorCode, Exception innerException, IReadOnlyDictionary<string, object?>? args = null, int code = 1)
        => new(errorCode, args, code, innerException);

    /// <summary>命名参数便捷构造(单键):<c>AppException.With(ErrorCodes.UserAccountExists, "account", account)</c></summary>
    public static AppException With(string errorCode, string argName, object? argValue, int code = 1)
        => new(errorCode, new Dictionary<string, object?> { [argName] = argValue }, code, null);
}