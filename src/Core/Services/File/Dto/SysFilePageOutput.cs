using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.File.Dto
{
    /// <summary>
    /// 文件清单行输出(分页端点专用):id/md5/大小/名称/后缀/业务关联/访问 URL/创建时间。
    /// 口径对齐 download 出口(对外只给"能标识 + 能访问"的字段,不给存储实现细节):
    /// 明确**不含** FilePath(存储路径段)与 Provider(存储档内部名 Local/Db),防管理清单泄露存储实现细节;
    /// 访问入口仍由 Url 承担(Url 本就是 upload 返回的对外直链/下载 API)。
    /// </summary>
    public sealed record SysFilePageOutput(
        long Id,
        string FileMd5,
        [property: JsonConverter(typeof(LongNumberJsonConverter))] long SizeKb,
        string? FileName,
        string? Suffix,
        long? DataId,
        string? Url,
        DateTime? CreateTime);
}