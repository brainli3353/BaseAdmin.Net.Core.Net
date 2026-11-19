using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdminCore.Core.Json;

/// <summary>
/// 全局 long/long? JSON 出口字符串化（G33）：雪花 Id 为 19 位 long，超过 JS
/// <c>Number.MAX_SAFE_INTEGER</c>（2^53-1），以 JSON number 出口时前端/下游 <c>JSON.parse</c>
/// 会在解析期**静默丢精度**（下游 FlowCore R2-A 实锤：19 位 pairIds 只能正则抠原文自救）。
/// 本转换器把 long 一律写成 JSON 字符串，从服务端出口一次性消除该精度面。
/// </summary>
/// <remarks>
/// WHY <see cref="JsonConverterFactory"/> 而非 <see cref="JsonConverter{T}"/>：long 与 long? 是两个封闭类型，
/// 单个 <c>JsonConverter&lt;long&gt;</c> 的 <c>CanConvert</c> 不覆盖 <c>long?</c>（值类型不满足协变），
/// 故按运行时类型各出转换器。工厂须自行判 <c>long?/long</c> 两种，不能依赖其他转换器兜 null。
/// WHY <c>HandleNull =&gt; true</c>：System.Text.Json 对可空值类型的 null 默认**不回调**自定义转换器，
/// 显式声明后 Read/Write 才会收到 null 分支——否则 <c>long?</c> 的 ""/"null" 口径无法在此收敛。
/// Read 双兼容：JSON string 与 JSON number 都能进（旧调用方/前端既有 number 入参不被断）；
/// 空串 "" 口径（G33 定死）：可空型 → null（"未提供"语义），非空型 → <see cref="JsonException"/>（"" 不是 0）。
/// </remarks>
public sealed class LongToStringJsonConverter : JsonConverterFactory
{
    /// <summary>仅接管 long 与 long?（其余类型放行给默认解析器）。</summary>
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert == typeof(long) || typeToConvert == typeof(long?);

    /// <summary>按运行时类型出转换器（long? 走可空版，其余走非空版）。</summary>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => typeToConvert == typeof(long?)
            ? new NullableLongToStringConverter()
            : new LongToStringConverter();

    /// <summary>非空 long：Write 恒字符串；Read 兼容 string/number，null/""/非法串抛。</summary>
    private sealed class LongToStringConverter : JsonConverter<long>
    {
        public override bool HandleNull => true;

        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var s = reader.GetString();
                    if (string.IsNullOrEmpty(s))
                        throw new JsonException("long 值不能为空字符串。");
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                    throw new JsonException($"无法将字符串 \"{s}\" 解析为 long。");
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var number))
                        return number;
                    throw new JsonException("JSON 数字超出 long 范围或非整数。");
                case JsonTokenType.Null:
                    throw new JsonException("非空 long 值不能为 null。");
                default:
                    throw new JsonException($"无法从 {reader.TokenType} 读取 long。");
            }
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>可空 long?：Write null 写 null；Read ""/null → null，string/number 解析。</summary>
    private sealed class NullableLongToStringConverter : JsonConverter<long?>
    {
        public override bool HandleNull => true;

        public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    var s = reader.GetString();
                    if (string.IsNullOrEmpty(s))
                        return null;
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                    throw new JsonException($"无法将字符串 \"{s}\" 解析为 long?。");
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var number))
                        return number;
                    throw new JsonException("JSON 数字超出 long 范围或非整数。");
                default:
                    throw new JsonException($"无法从 {reader.TokenType} 读取 long?。");
            }
        }

        public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
            else
                writer.WriteNullValue();
        }
    }
}