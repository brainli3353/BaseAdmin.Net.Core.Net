using System.Text;
using AdminCore.Core.Services.Import;
using AdminCore.Core.Services.Pos.Dto;
using MiniExcelLibs;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 CP「通用数据导入」验证(纯内存,无 PG/Redis 依赖):
    /// MiniExcel 读侧表头映射(xlsx/csv 同源)/ 纯函数 Validate 的行号与字段定位 / 文件内编码重复 / 空集合空报告 / 报告截断。
    /// DB 查重与批量入库(全回滚事务)属服务层集成路径,由主会话另一门覆盖。
    /// </summary>
    public class BatchCPImportTest
    {
        private static readonly ImportService Service = new();

        private static MemoryStream Csv(string content) => new(Encoding.UTF8.GetBytes(content));

        // ── C2a 正常 2 行 → 0 错 ─────────────────────────────────────────────

        [Fact]
        public void C2a_正常两行_解析映射正确且零错误()
        {
            var csv = "职位名称,职位编码,排序,备注\n"
                    + "技术经理,P001,1,负责研发\n"
                    + "产品专员,P002,,负责需求\n";

            var rows = Service.Parse<ImportPosInput>(Csv(csv), ".csv");

            Assert.Equal(2, rows.Count);
            Assert.Equal("技术经理", rows[0].Name);
            Assert.Equal("P001", rows[0].Code);
            Assert.Equal("1", rows[0].Sort);
            Assert.Equal("负责研发", rows[0].Remark);
            Assert.Equal("产品专员", rows[1].Name);
            Assert.Equal("P002", rows[1].Code);
            Assert.Null(rows[1].Sort); // 空单元格 → 默认(入库时按 0 处理)
            Assert.Equal("负责需求", rows[1].Remark);

            var errors = Service.Validate(rows, null);
            Assert.Empty(errors);

            var report = Service.Build(rows.Count, rows.Count, errors);
            Assert.Equal(2, report.Total);
            Assert.Equal(2, report.Imported);
            Assert.Empty(report.Errors);
            Assert.False(report.Truncated);
        }

        [Fact]
        public void C2a_xlsx_走同一表头映射()
        {
            var stream = new MemoryStream();
            stream.SaveAs(new[]
            {
                new Dictionary<string, object?> { ["职位名称"] = "总监", ["职位编码"] = "P003", ["排序"] = 3, ["备注"] = null },
                new Dictionary<string, object?> { ["职位名称"] = "助理", ["职位编码"] = "P004", ["排序"] = 4, ["备注"] = "行政" }
            });
            stream.Position = 0;

            var rows = Service.Parse<ImportPosInput>(stream, ".xlsx");

            Assert.Equal(2, rows.Count);
            Assert.Equal("总监", rows[0].Name);
            Assert.Equal("P003", rows[0].Code);
            Assert.Equal("3", rows[0].Sort);
            Assert.Equal("行政", rows[1].Remark);
            Assert.Empty(Service.Validate(rows, null));
        }

        // ── C2b 缺必填 / 超长 / 排序非数字 → 行号 + 字段名正确 ───────────────

        [Fact]
        public void C2b_缺Name_Code超长_Sort非数字_行号字段正确()
        {
            var rows = new List<ImportPosInput>
            {
                new() { Name = null, Code = "P100", Sort = "1" },                                     // 行 2:缺 Name
                new() { Name = "岗位", Code = new string('C', ImportService.MaxCodeLength + 1) },     // 行 3:Code 超长
                new() { Name = "岗位B", Code = "P101", Sort = "abc" }                                 // 行 4:Sort 非整数
            };

            var errors = Service.Validate(rows, null);

            Assert.Equal(3, errors.Count);

            var missingName = Assert.Single(errors, e => e.Row == 2);
            Assert.Equal(nameof(ImportPosInput.Name), missingName.Field);

            var tooLongCode = Assert.Single(errors, e => e.Row == 3);
            Assert.Equal(nameof(ImportPosInput.Code), tooLongCode.Field);

            var badSort = Assert.Single(errors, e => e.Row == 4);
            Assert.Equal(nameof(ImportPosInput.Sort), badSort.Field);
        }

        [Fact]
        public void C2b_与库内既有编码撞码_报错()
        {
            var rows = new List<ImportPosInput> { new() { Name = "岗位", Code = "P001" } };

            var errors = Service.Validate(rows, new HashSet<string> { "P001" });

            var error = Assert.Single(errors);
            Assert.Equal(2, error.Row);
            Assert.Equal(nameof(ImportPosInput.Code), error.Field);
        }

        // ── C2c 文件内 Code 重复 → 第 2 次出现报错 ──────────────────────────

        [Fact]
        public void C2c_文件内编码重复_第二次出现报错()
        {
            var csv = "职位名称,职位编码,排序,备注\n"
                    + "岗位A,P900,,\n"
                    + "岗位B,P900,,\n";

            var rows = Service.Parse<ImportPosInput>(Csv(csv), ".csv");
            var errors = Service.Validate(rows, null);

            var error = Assert.Single(errors);
            Assert.Equal(3, error.Row); // 首次出现(行 2)合法,第二次(行 3)报错
            Assert.Equal(nameof(ImportPosInput.Code), error.Field);
        }

        // ── C2d 空集合 → 空报告（纯函数面；服务层批 CPΔ 起对解析后零数据行先抛 import.file_empty，
        //    见 SysPosService.ImportAsync——本用例锁纯函数行为不变） ─────────────

        [Fact]
        public void C2d_空集合_空报告()
        {
            var errors = Service.Validate(Array.Empty<ImportPosInput>(), null);
            Assert.Empty(errors);

            var report = Service.Build(0, 0, errors);
            Assert.Equal(0, report.Total);
            Assert.Equal(0, report.Imported);
            Assert.Empty(report.Errors);
            Assert.False(report.Truncated);
        }

        // ── 报告截断:错误 > 100 → 只回 100 条 + Truncated ──────────────────

        [Fact]
        public void C2e_错误超过上限_截断并置Truncated()
        {
            var rows = Enumerable.Range(0, ImportService.MaxErrors + 1)
                .Select(i => new ImportPosInput { Name = null, Code = $"P{i:D4}" })
                .ToList();

            var errors = Service.Validate(rows, null);
            Assert.Equal(ImportService.MaxErrors + 1, errors.Count);

            var report = Service.Build(rows.Count, 0, errors);
            Assert.Equal(ImportService.MaxErrors, report.Errors.Count);
            Assert.Equal(2, report.Errors[0].Row);
            Assert.True(report.Truncated);
        }
    }
}