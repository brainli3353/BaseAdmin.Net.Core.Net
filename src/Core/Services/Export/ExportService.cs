using System.Globalization;
using System.Reflection;
using AdminCore.Core.Attributes;
using MiniExcelLibs;

namespace AdminCore.Core.Services.Export
{
    /// <summary>导出文件格式(批 CO)。</summary>
    public enum ExportFormat
    {
        /// <summary>Excel 工作簿(.xlsx)。</summary>
        Xlsx,

        /// <summary>逗号分隔文本(.csv)。</summary>
        Csv
    }

    /// <summary>
    /// 通用列表导出服务(批 CO):把任意可枚举数据按列定义写成 Xlsx/Csv 流。
    /// </summary>
    /// <remarks>
    /// 列定义规则(见 <see cref="ExportColumnAttribute"/>):
    /// 1) 反射取 <c>T</c> 全部 public 实例属性;若其中有属性标注 <see cref="ExportColumnAttribute"/>,
    ///    则只导出被标注列并按 <see cref="ExportColumnAttribute.Order"/> 升序排列;
    /// 2) 若无任何属性标注(未标注 DTO),回退导出全部 public 属性,属性名即列头;
    /// 3) 每行映射为 <c>Dictionary&lt;string, object?&gt;</c>(按列定义顺序插入,列序稳定),
    ///    经 MiniExcel 流式写盘——内存占用与行数无关,仅与单行字段数相关。
    /// 注册口径:<c>[Service]</c> 无接口 = 按具体类自注册(见 ServiceAttribute)。
    /// </remarks>
    [Service]
    public class ExportService
    {
        /// <summary>
        /// 导出为内存流(xlsx/csv)。
        /// </summary>
        /// <typeparam name="T">行数据类型。</typeparam>
        /// <param name="data">行数据(惰性枚举,服务内部只遍历一次)。</param>
        /// <param name="format">导出格式。</param>
        /// <param name="sheetName">工作表名(仅 xlsx 生效;缺省 <c>Sheet1</c>)。</param>
        /// <returns>已写满、Position 归零的流(调用方负责释放)。</returns>
        public async Task<Stream> ExportAsync<T>(IEnumerable<T> data, ExportFormat format, string? sheetName = null)
        {
            ArgumentNullException.ThrowIfNull(data);

            var columns = BuildColumns<T>();

            // 惰性映射:MiniExcel 遍历时逐行生成字典,避免额外整体物化一份 List<Dictionary>。
            IEnumerable<Dictionary<string, object?>> rows = data.Select(item =>
            {
                var row = new Dictionary<string, object?>(columns.Count);
                foreach (var (header, getter) in columns)
                {
                    row[header] = getter(item);
                }
                return row;
            });

            var stream = new MemoryStream();
            try
            {
                if (format == ExportFormat.Csv)
                {
                    // CSV 无工作表概念,sheetName 忽略。
                    await stream.SaveAsAsync(rows, excelType: ExcelType.CSV);
                }
                else
                {
                    await stream.SaveAsAsync(rows, sheetName: string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName);
                }

                stream.Position = 0;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 构建列定义:优先取 [ExportColumn] 标注列(按 Order 升序),无标注则回退全部 public 属性(按声明顺序)。
        /// </summary>
        private static List<(string Header, Func<T, object?> Getter)> BuildColumns<T>()
        {
            // MetadataToken 保序:属性声明顺序在元数据中单调递增,保证回退列序与源码一致(避免反射顺序漂移)。
            var properties = typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(p => p.MetadataToken)
                .ToList();

            var marked = properties
                .Select(p => (Property: p, Attr: p.GetCustomAttribute<ExportColumnAttribute>()))
                .Where(x => x.Attr is not null)
                .OrderBy(x => x.Attr!.Order)
                .Select(x => (x.Attr!.DisplayName, BuildGetter<T>(x.Property)))
                .ToList();

            if (marked.Count > 0)
            {
                return marked;
            }

            // 回退:未标注类型导出全部 public 属性(属性名为列头)。
            return properties
                .Select(p => (p.Name, BuildGetter<T>(p)))
                .ToList();
        }

        /// <summary>
        /// 属性取值器(直读属性 GetValue;T 为引用类型时 null 行会抛 NRE,由调用方保证数据非空行)。
        /// long/long? 值统一转 InvariantCulture 文本——Excel 数字单元格仅 15 位有效数字,
        /// 19 位雪花 Id 会在此二次丢精度(批 G33c:xlsx 与 csv 共用本取值出口,故两格式同源无损)。
        /// </summary>
        private static Func<T, object?> BuildGetter<T>(PropertyInfo property)
        {
            var isLong = property.PropertyType == typeof(long) || property.PropertyType == typeof(long?);
            return item =>
            {
                var value = property.GetValue(item);
                if (!isLong || value is null)
                {
                    return value;
                }

                // G33c:雪花 19 位 > Excel 15 位有效数字 ⇒ 落文本单元格,避免数值化截断。
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            };
        }
    }
}