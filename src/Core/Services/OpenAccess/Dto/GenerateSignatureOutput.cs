using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.OpenAccess.Dto
{
    /// <summary>签名预览出参:最终参与请求头的时间戳/nonce 与算出的 sign 值(调用方按此四头发起请求)。</summary>
    public sealed record GenerateSignatureOutput([property: JsonConverter(typeof(LongNumberJsonConverter))] long Timestamp, string Nonce, string Signature);
}
