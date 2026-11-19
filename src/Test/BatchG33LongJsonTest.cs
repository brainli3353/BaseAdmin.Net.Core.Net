using System.Text.Json;
using AdminCore.Core.Json;
using AdminCore.Core.Services.Role.Dto;

namespace AdminCore.Test;

/// <summary>
/// 批 G33 契约测试(纯序列化,不连库):锁「long id 全局出口字符串化 + 数值语义白名单保持 number」两面。
/// 被测主体 = <see cref="AdminJsonOptions"/> 单源 + <see cref="LongToStringJsonConverter"/> +
/// <see cref="LongNumberJsonConverter"/>(属性级例外)。
/// </summary>
public class BatchG33LongJsonTest
{
    /// <summary>出口单源(与生产 MVC/异常中间件/PermissionMiddleware/SignalR 同一份)。</summary>
    private static readonly JsonSerializerOptions Options = AdminJsonOptions.Create();

    /// <summary>C1:long 序列化为**带引号字符串**(雪花 19 位精度在 JSON.parse 期不丢)。</summary>
    [Fact]
    public void C1_Long序列化为带引号字符串()
    {
        Assert.Equal("\"123\"", JsonSerializer.Serialize(123L, Options));
        Assert.Equal("\"9223372036854775807\"", JsonSerializer.Serialize(long.MaxValue, Options));
    }

    /// <summary>C2:long? null → JSON null;""→ 反序列化 null;非空型 null/"" 抛 JsonException。</summary>
    [Fact]
    public void C2_可空long的null与空串口径()
    {
        Assert.Equal("null", JsonSerializer.Serialize<long?>(null, Options));

        Assert.Null(JsonSerializer.Deserialize<long?>("\"\"", Options));
        Assert.Null(JsonSerializer.Deserialize<long?>("null", Options));
        Assert.Equal(123L, JsonSerializer.Deserialize<long?>("\"123\"", Options)!.Value);
        Assert.Equal(123L, JsonSerializer.Deserialize<long?>("123", Options)!.Value);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<long>("\"\"", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<long>("null", Options));
    }

    /// <summary>C3:Read 双兼容(123 数字与 "123" 串都能进)+ 19 位 long.MaxValue 往返无损。</summary>
    [Fact]
    public void C3_Read双兼容与19位往返无损()
    {
        Assert.Equal(123L, JsonSerializer.Deserialize<long>("123", Options));
        Assert.Equal(123L, JsonSerializer.Deserialize<long>("\"123\"", Options));

        var json = JsonSerializer.Serialize(long.MaxValue, Options);
        Assert.Equal("\"9223372036854775807\"", json);
        Assert.Equal(long.MaxValue, JsonSerializer.Deserialize<long>(json, Options));

        // 非数字串 → 响亮异常(不静默给 0)
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<long>("\"abc\"", Options));
    }

    /// <summary>C4:数值语义白名单属性(RoleOutput.DataScope)仍为 number,同记录 long Id 仍为 string。</summary>
    [Fact]
    public void C4_白名单属性保持number而id仍为string()
    {
        var json = JsonSerializer.Serialize(new RoleOutput(9223372036854775807L, "r", "c", null, false, 5L), Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("id").ValueKind);
        Assert.Equal("9223372036854775807", root.GetProperty("id").GetString());

        Assert.Equal(JsonValueKind.Number, root.GetProperty("dataScope").ValueKind);
        Assert.Equal(5L, root.GetProperty("dataScope").GetInt64());
    }

    /// <summary>C5:List&lt;long&gt; 元素序列化全为 string(工厂覆盖集合元素类型)。</summary>
    [Fact]
    public void C5_List_long元素序列化全为string()
    {
        var json = JsonSerializer.Serialize(new List<long> { 1L, long.MaxValue }, Options);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, arr.Count);
        Assert.All(arr, e => Assert.Equal(JsonValueKind.String, e.ValueKind));
        Assert.Equal("9223372036854775807", arr[1].GetString());
    }
}