using System.Globalization;
using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Models.Import;
using AdminCore.Core.Services.Pos.Dto;
using MiniExcelLibs;

namespace AdminCore.Core.Services.Import
{
    /// <summary>
    /// 通用数据导入服务(批 CP):MiniExcel 读侧(Parse)+ 纯函数校验(Validate)+ 报告装配(Build)。
    /// </summary>
    /// <remarks>
    /// 职责边界:
    /// ① <see cref="Parse{T}"/> 只做「源文件 → DTO 列表」,按 <see cref="ImportColumnAttribute.Header"/> 与表头行对齐,
    ///    xlsx/csv 同源(xlsx 单元格数字经 InvariantCulture 转文本,保证"排序"列原文可达);
    /// ② <see cref="Validate"/> 是**无 DB 纯函数**(不碰仓储/HttpContext),逐行必填/长度/可解析/编码唯一性判定,
    ///    便于单测与"先校验后写库"的全回滚口径;
    /// ③ <see cref="Build"/> 统一截断(上限 100 条 + Truncated 标志),入库与否由调用方决定。
    /// 行级错误**不是异常**:一律经 <see cref="ImportReport"/> 回传;文件格式/超行限/空文件才抛 AppException(见 SysPosService)。
    /// 注册口径:<c>[Service]</c> 无接口 = 按具体类自注册(与 ExportService 同口径)。
    /// </remarks>
    [Service]
    public class ImportService
    {
        /// <summary>单次导入允许的最大数据行数(超出 → AppException.ImportRowLimitExceeded)。</summary>
        public const int MaxRows = 5000;

        /// <summary>报告回传的最大错误条数(超出 → Truncated=true)。</summary>
        public const int MaxErrors = 100;

        /// <summary>职位名称最大长度(对齐 SysPos.Name 列长)。</summary>
        public const int MaxNameLength = 64;

        /// <summary>职位编码最大长度(对齐 SysPos.Code 列长)。</summary>
        public const int MaxCodeLength = 64;

        /// <summary>备注最大长度(对齐 SysPos.Remark 列长)。</summary>
        public const int MaxRemarkLength = 256;

        /// <summary>
        /// 解析导入文件为 DTO 列表:按 <typeparamref name="T"/> 上 [ImportColumn] 声明,与源文件**表头行**按名对齐取值。
        /// </summary>
        /// <typeparam name="T">导入行 DTO(public 无参构造,属性标注 <see cref="ImportColumnAttribute"/>)。</typeparam>
        /// <param name="stream">源文件流(Position 需在起点)。</param>
        /// <param name="extension">文件扩展名(.xlsx 或 .csv;大小写不敏感)。</param>
        /// <returns>按源文件行序排列的 DTO 列表(空表头行不产生行)。</returns>
        public List<T> Parse<T>(Stream stream, string extension) where T : new()
        {
            ArgumentNullException.ThrowIfNull(stream);

            var excelType = string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
                ? ExcelType.CSV
                : ExcelType.XLSX;

            var columns = typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Property: p, Attr: p.GetCustomAttribute<ImportColumnAttribute>()))
                .Where(x => x.Attr is not null)
                .OrderBy(x => x.Attr!.Order)
                .ToList();

            var result = new List<T>();
            foreach (var raw in MiniExcel.Query(stream, useHeaderRow: true, excelType: excelType))
            {
                if (raw is not IDictionary<string, object> row)
                {
                    continue;
                }

                var item = new T();
                foreach (var (property, attr) in columns)
                {
                    if (!row.TryGetValue(attr!.Header, out var cell) || cell is null)
                    {
                        continue; // 列缺省/单元格空 → 保持属性默认值(空串与缺省同义,交 Validate 判定)
                    }

                    var text = Convert.ToString(cell, CultureInfo.InvariantCulture)?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    SetValue(item, property, text);
                }

                result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// 纯函数校验(无 DB):逐行必填/长度/排序可解析/编码唯一性(库内既有 + 文件内重复)。
        /// </summary>
        /// <param name="rows">已解析的导入行。</param>
        /// <param name="existingCodes">库内既有职位编码集合(可为空)。</param>
        /// <returns>行级错误(按行号升序、同行按字段检查顺序;Row 从 2 起——表头占第 1 行)。</returns>
        public List<ImportRowError> Validate(IReadOnlyList<ImportPosInput> rows, IReadOnlySet<string>? existingCodes)
        {
            ArgumentNullException.ThrowIfNull(rows);

            var errors = new List<ImportRowError>();
            // 判定集 = 库内既有 ∪ 本文件已出现:同一个 Add 同时覆盖"与库撞码"和"文件内重复"。
            var seen = new HashSet<string>(existingCodes ?? new HashSet<string>(), StringComparer.Ordinal);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var rowNo = i + 2; // 表头第 1 行,首条数据从 2 起

                var name = row.Name?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    errors.Add(new ImportRowError(rowNo, nameof(ImportPosInput.Name), "职位名称不能为空"));
                }
                else if (name.Length > MaxNameLength)
                {
                    errors.Add(new ImportRowError(rowNo, nameof(ImportPosInput.Name), $"职位名称长度不能超过 {MaxNameLength}"));
                }

                var code = row.Code?.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    errors.Add(new ImportRowError(rowNo, nameof(ImportPosInput.Code), "职位编码不能为空"));
                }
                else if (code.Length > MaxCodeLength)
                {
                    errors.Add(new ImportRowError(rowNo, nameof(ImportPosInput.Code), $"职位编码长度不能超过 {MaxCodeLength}"));
                }
                else if (!seen.Add(code))
                {
                    errors.Add(new ImportRowError(rowNo, nameof(ImportPosInput.Code), $"职位编码已存在或文件内重复：{code}"));
                }

                var sort = row.Sort?.Trim();
                if (!string.IsNullOrEmpty(sort)
                    && !int.TryParse(sort, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    errors.Add(new ImportRowError(rowNo, nameof(ImportPosInput.Sort), "排序必须为整数"));
                }

                if (!string.IsNullOrEmpty(row.Remark) && row.Remark.Length > MaxRemarkLength)
                {
                    errors.Add(new ImportRowError(rowNo, nameof(ImportPosInput.Remark), $"备注长度不能超过 {MaxRemarkLength}"));
                }
            }

            return errors;
        }

        /// <summary>装配报告:错误超过 <see cref="MaxErrors"/> 时截断并置 Truncated=true。</summary>
        public ImportReport Build(int total, int imported, List<ImportRowError>? errors)
        {
            var all = errors ?? new List<ImportRowError>();
            var truncated = all.Count > MaxErrors;
            return new ImportReport
            {
                Total = total,
                Imported = imported,
                Errors = truncated ? all.Take(MaxErrors).ToList() : all,
                Truncated = truncated
            };
        }

        /// <summary>按属性类型落值:string 直存;int 用 InvariantCulture 解析(非法值回落 0——合法性由 Validate 保证)。</summary>
        private static void SetValue(object target, PropertyInfo property, string text)
        {
            if (property.PropertyType == typeof(string))
            {
                property.SetValue(target, text);
            }
            else if (property.PropertyType == typeof(int))
            {
                property.SetValue(target, int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0);
            }
            else
            {
                var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                property.SetValue(target, Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture));
            }
        }
    }
}