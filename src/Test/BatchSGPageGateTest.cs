// 批 S-G 纵深期·分页防回潮门（用户在 2026-10-26 批准"源码扫描防回潮"作为门控保留，见纵深期计划 §四）:
// 判据:src/Core/Services/**/*.cs 中每个 .ToPageListAsync( 调用行都必须带 PageConst.MaxPageSize 上限。
// WHY 比"查旧字面量"更严:只查 Math.Max(1, input.PageSize) 会漏掉裸 input.PageSize 调用点
// (B1c 实测漏了 SysJobService 2 处,由 BatchSGPageSizeClampTest 的真实 PG 用例抓出后补齐)。
// 本类零库(arguments 目录定位源码,不连 DB),已纳入 CLAUDE.md"测试分域三档·零库档"预设。
using Xunit;

namespace AdminCore.Test;

public class BatchSGPageGateTest
{
    [Fact]
    public void 源码扫描_全部分页调用带上限()
    {
        var servicesDir = FindServicesDirectory();
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(servicesDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue; // 注释里的示例不算调用点
                }

                // 合法三态(B5b 后)：①显式带 PageConst 上限；②经分页原语传 IPageInput(钳制在 PageQueryExtensions 内)；
                // ③生成器模板仍须产出显式上限文本。除此之外的 .ToPageListAsync 裸数值调用一律视为回潮。
                var usesPrimitive = trimmed.Contains(".ToPageListAsync(input", StringComparison.Ordinal)
                    || trimmed.Contains(".ToPageAsync(input", StringComparison.Ordinal);
                if (trimmed.Contains(".ToPageListAsync(", StringComparison.Ordinal)
                    && !trimmed.Contains("PageConst.MaxPageSize", StringComparison.Ordinal)
                    && !usesPrimitive)
                {
                    violations.Add($"{name}: {trimmed}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"发现未收口的分页调用点(防回潮门):{string.Join(" | ", violations)}");
    }

    /// <summary>从测试 bin 目录向上定位 src/Core/Services(找不到即 Fail,fail-loud 不静默跳过)。</summary>
    private static string FindServicesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Core", "Services");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent!;
        }

        Assert.Fail($"未从 {AppContext.BaseDirectory} 向上定位到 src/Core/Services(源码扫描用例无法执行)");
        return string.Empty; // 不可达:Assert.Fail 总抛
    }
}
