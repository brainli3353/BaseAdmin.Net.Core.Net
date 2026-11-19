using System.Text.Encodings.Web;
using System.Text.Json;

namespace AdminCore.Core.Models;

/// <summary>
/// 泛型统一响应模型（含 Data）。Code=0 成功，非 0 业务失败。
/// 全项目接口统一返回此格式，序列化规则见 <see cref="ApiResult.Json"/>。
/// </summary>
public class ApiResult<T>
{
    /// <summary>业务码：0 成功；非 0 失败（业务码，非 HTTP 状态码）</summary>
    public int Code { get; set; }

    /// <summary>消息</summary>
    public string Msg { get; set; } = string.Empty;

    /// <summary>数据</summary>
    public T Data { get; set; } = default!;

    /// <summary>稳定错误码(批 AQ):客户端按此编程判断(永不变);成功与存量失败为空(null)</summary>
    public string? ErrCode { get; set; }

    /// <summary>成功：Code=0，附数据</summary>
    public static ApiResult<T> Success(T data, string msg = "操作成功")
        => new() { Code = 0, Msg = msg, Data = data };

    /// <summary>失败：Code 默认 1，可覆写；errCode 为稳定错误码(批 AQ)</summary>
    public static ApiResult<T> Fail(string msg, int code = 1, string? errCode = null)
        => new() { Code = code, Msg = msg, ErrCode = errCode };
}

/// <summary>
/// 非泛型统一响应模型（无 Data 场景：纯成功/纯失败提示）。
/// 与泛型版提供相同的工厂方法与序列化约定。
/// </summary>
public class ApiResult
{
    /// <summary>业务码：0 成功；非 0 失败</summary>
    public int Code { get; set; }

    /// <summary>消息</summary>
    public string Msg { get; set; } = string.Empty;

    /// <summary>稳定错误码(批 AQ):客户端按此编程判断(永不变);成功与存量失败为空(null)</summary>
    public string? ErrCode { get; set; }

    /// <summary>成功：Code=0</summary>
    public static ApiResult Success(string msg = "操作成功")
        => new() { Code = 0, Msg = msg };

    /// <summary>失败：Code 默认 1，可覆写；errCode 为稳定错误码(批 AQ)</summary>
    public static ApiResult Fail(string msg, int code = 1, string? errCode = null)
        => new() { Code = code, Msg = msg, ErrCode = errCode };

    /// <summary>
    /// 统一 JSON 序列化规则(出口单源 <see cref="AdminCore.Core.Json.AdminJsonOptions"/>)：
    /// camelCase + 中文不转义 + long→string(G33)。全局异常中间件手动写响应时使用本配置，
    /// 保证中间件输出与控制器输出同构。
    /// </summary>
    public static readonly JsonSerializerOptions Json = AdminCore.Core.Json.AdminJsonOptions.Create();
}
