using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// Http 出站日志列表行输出(不含大字段——RequestHeaders/RequestBody/ResponseBody/Exception 四 text 大字段
    /// 列表不载防慢查询,经 detail 端点取全字段;HasException 为独立标记列,列表零大字段读取)。
    /// </summary>
    public sealed record SysLogHttpOutput(long Id, string? HttpMethod, string? RequestUrl, int? StatusCode, [property: JsonConverter(typeof(NullableLongNumberJsonConverter))] long? ElapsedMs,
        string? Account, string? RealName, string? TraceId, bool HasException, DateTime? CreateTime);
}