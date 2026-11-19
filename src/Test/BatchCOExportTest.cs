using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Export;
using MiniExcelLibs;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 CO「通用列表导出」G33c 小件臂(纯内存,无 PG/Redis 依赖):
    /// long/long? 取值出口的统一文本化——19 位雪花 Id 落 Excel 文本单元格,避免 15 位有效数字二次丢精度。
    /// </summary>
    public class BatchCOExportTest
    {
        private static readonly ExportService Service = new();

        /// <summary>最小导出 DTO:1 个 long 列 + 1 个 long? 列(对齐 UserOutput.Id/OrgId 形态)。</summary>
        private sealed class ExportLongDto
        {
            [ExportColumn("编号", Order = 1)]
            public long Id { get; set; }

            [ExportColumn("机构ID", Order = 2)]
            public long? OrgId { get; set; }
        }

        /// <summary>经导出管道写盘后用 MiniExcel 读回首行(测试直达的最小层:公开 ExportAsync 出口)。</summary>
        private static async Task<IDictionary<string, object>> ReadFirstRowAsync(ExportFormat format, ExportLongDto row)
        {
            var stream = await Service.ExportAsync(new[] { row }, format);
            stream.Position = 0;

            var excelType = format == ExportFormat.Csv ? ExcelType.CSV : ExcelType.XLSX;
            var first = MiniExcel.Query(stream, useHeaderRow: true, excelType: excelType).First();
            return (IDictionary<string, object>)first;
        }

        // ── C1:long.MaxValue(19 位)经 xlsx/csv 产物均为无损字符串 ───────────

        [Fact]
        public async Task C1_long最大值_导出xlsx与csv均为无损文本()
        {
            const string expected = "9223372036854775807";

            var xlsxRow = await ReadFirstRowAsync(ExportFormat.Xlsx, new ExportLongDto { Id = long.MaxValue });
            var xlsxId = Assert.IsType<string>(xlsxRow["编号"]);
            Assert.Equal(expected, xlsxId);

            var csvRow = await ReadFirstRowAsync(ExportFormat.Csv, new ExportLongDto { Id = long.MaxValue });
            var csvId = Assert.IsType<string>(csvRow["编号"]);
            Assert.Equal(expected, csvId);
        }

        // ── C2:null long? 导出为空单元格(不抛、不落 "0"/"null") ─────────────

        [Fact]
        public async Task C2_null可空long_导出为空单元格()
        {
            var xlsxRow = await ReadFirstRowAsync(ExportFormat.Xlsx, new ExportLongDto { Id = 1, OrgId = null });
            Assert.True(xlsxRow["机构ID"] is null || string.IsNullOrEmpty(xlsxRow["机构ID"]?.ToString()));

            var csvRow = await ReadFirstRowAsync(ExportFormat.Csv, new ExportLongDto { Id = 1, OrgId = null });
            Assert.True(csvRow["机构ID"] is null || string.IsNullOrEmpty(csvRow["机构ID"]?.ToString()));
        }
    }
}