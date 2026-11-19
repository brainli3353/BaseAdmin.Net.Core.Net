using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdminCore.Core.Json;

/// <summary>
/// 数值语义 long 的**属性级例外**（G33 白名单）：这类 long 表达的是数量/时长/字节数/枚举式取值，
/// 不是雪花 Id，必须继续以 JSON number 出口——否则前端既有数值消费面会被破坏
/// （典型：角色 <c>dataScope</c> 前端 <c>=== 5</c> 硬比较、字节数除法格式化、分页 Total 计算）。
/// 与全局 <see cref="LongToStringJsonConverter"/> 互逆：Write 数字、Read 兼容 string/number。
/// </summary>
/// <remarks>
/// 用法：输出面属性/记录位置参数加 <c>[JsonConverter(typeof(LongNumberJsonConverter))]</c>
/// （record 位置参数用 <c>[property: JsonConverter(...)]</c>）；可空 long? 用
/// <see cref="NullableLongNumberJsonConverter"/>（值类型不满足协变，单类覆盖不了 long?）。
/// WHY 属性级而非全局注册：全局解析器已被 long→string 工厂占据，STJ 中属性级特性优先于 options 级，
/// 故白名单只能落在属性上（G33 定稿口径）。
/// </remarks>
public sealed class LongNumberJsonConverter : JsonConverter<long>
{
    public override bool HandleNull => true;

    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var number))
                    return number;
                throw new JsonException("JSON 数字超出 long 范围或非整数。");
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrEmpty(s))
                    throw new JsonException("long 值不能为空字符串。");
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                throw new JsonException($"无法将字符串 \"{s}\" 解析为 long。");
            case JsonTokenType.Null:
                throw new JsonException("非空 long 值不能为 null。");
            default:
                throw new JsonException($"无法从 {reader.TokenType} 读取 long。");
        }
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>可空数值语义 long?：Write 数字/ null；Read ""/null → null，string/number 解析。</summary>
public sealed class NullableLongNumberJsonConverter : JsonConverter<long?>
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
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}