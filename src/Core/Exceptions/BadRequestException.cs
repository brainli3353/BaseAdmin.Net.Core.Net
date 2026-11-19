namespace AdminCore.Core.Exceptions;

/// <summary>
/// 请求级无效异常（task_012 文件模块引入，契约要求真 HTTP 400 的场景：类型/大小白名单未过、MD5 校验失败）：
/// 由 ExceptionMiddleware 映射为 **HTTP 400** + 统一 ApiResult 体。
/// 与 AppException 的区分：AppException="业务规则不通过"（HTTP 200 + 业务码，前端按 Code 分支），
/// BadRequestException="请求本身无效"（HTTP 400，客户端错误语义，网关/监控按状态码统计）。
/// 批 AR-5：与 <see cref="AppException"/> 对齐支持**错误码口径**（<see cref="Of"/>／<see cref="With"/>），
/// 解释文案由出口按请求语言渲染并回带 errCode；存量 <c>new BadRequestException("中文")</c> 行为不变。
/// </summary>
public class BadRequestException : Exception
{
    /// <summary>稳定错误码（批 AR-5）：与 Lang.*.json 键同源；存量构造为 null</summary>
    public string? ErrorCode { get; }

    /// <summary>错误码文案的命名占位参数（如 <c>{fileName}</c>）</summary>
    public IReadOnlyDictionary<string, object?>? Args { get; }

    /// <summary>存量口径：直接给中文消息（ErrorCode=null，行为不变）</summary>
    public BadRequestException(string message) : base(message)
    {
    }

    private BadRequestException(string errorCode, IReadOnlyDictionary<string, object?>? args)
        : base(errorCode) // Message 暂存错误码:日志按错误码可检索;对外文案由出口渲染
    {
        ErrorCode = errorCode;
        Args = args;
    }

    /// <summary>错误码口径（推荐）：出口按请求语言渲染解释文案，响应为 HTTP 400 + errCode</summary>
    /// <param name="errorCode">稳定错误码，取值见 <see cref="ErrorCodes"/></param>
    /// <param name="args">命名占位参数</param>
    public static BadRequestException Of(string errorCode, IReadOnlyDictionary<string, object?>? args = null)
        => new(errorCode, args);

    /// <summary>命名参数便捷构造（单键）：<c>BadRequestException.With(ErrorCodes.FileSuffixNotAllowed, "fileName", name)</c></summary>
    public static BadRequestException With(string errorCode, string argName, object? argValue)
        => new(errorCode, new Dictionary<string, object?> { [argName] = argValue });
}