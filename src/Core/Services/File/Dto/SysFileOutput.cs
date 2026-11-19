using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.File.Dto
{
    /// <summary>文件记录输出（upload/download 等接口返回体）：id/md5/大小/名称/路径/后缀/Provider/业务关联/访问 URL。</summary>
    public sealed record SysFileOutput(
        long Id,
        string FileMd5,
        [property: JsonConverter(typeof(LongNumberJsonConverter))] long SizeKb,
        string? FileName,
        string? FilePath,
        string? Suffix,
        string? Provider,
        long? DataId,
        string? Url);
}
