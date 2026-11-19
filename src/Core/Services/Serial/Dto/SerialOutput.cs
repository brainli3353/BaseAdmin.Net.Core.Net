using System.Text.Json.Serialization;
using AdminCore.Core.Json;
using AdminCore.Core.Enums;

namespace AdminCore.Core.Services.Serial.Dto
{
    /// <summary>流水号分页行输出(全业务字段投影;实体全字段故不另建投影类,直接投影实体形状)。</summary>
    public sealed record PageSerialOutput(long Id, [property: JsonConverter(typeof(LongNumberJsonConverter))] long Seq, DateTime Expy, string? Type, ResetIntervalEnum ResetInterval,
        string? Formater, [property: JsonConverter(typeof(LongNumberJsonConverter))] long Min, int OrderNo, [property: JsonConverter(typeof(LongNumberJsonConverter))] long Max, int Status, string? Remark);
}
