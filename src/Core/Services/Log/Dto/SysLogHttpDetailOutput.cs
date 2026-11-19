using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// Http 出站日志详情输出(全字段:含请求头/请求体/响应体/异常全文四大字段)。
    /// </summary>
    public sealed record SysLogHttpDetailOutput(long Id, string? HttpMethod, string? RequestUrl, int? StatusCode, [property: JsonConverter(typeof(NullableLongNumberJsonConverter))] long? ElapsedMs,
        string? Account, string? RealName, string? TraceId, bool HasException, string? RequestHeaders, string? RequestBody,
        string? ResponseBody, string? Exception, DateTime? CreateTime);
}