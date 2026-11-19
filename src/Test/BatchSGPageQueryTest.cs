// 批 S-G 纵深期·分页原语收口(B5b)零库测试:
// ① IPageInput 契约:20 个 Page*Input record 全部实现接口(反射断言,防新域漏接);
// ② NormalizePaging 口径:PageIndex 下限 1 / PageSize 钳制 1..PageConst.MaxPageSize(与历史 Math.Max/Math.Clamp 逐点等价);
// ③ 扩展重载存在性:ToPageAsync(两重载)+ ToPageListAsync(IPageInput) 三个入口在(编译期已证,反射再钉防误删)。
using System.Reflection;
using AdminCore.Core.Extensions;
using AdminCore.Core.Models;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGPageQueryTest
{
    /// <summary>测试用 IPageInput 实现(公开形态无关,只验钳制口径)。</summary>
    private sealed record ProbePageInput(int PageIndex, int PageSize, string? OrderBy = null, bool? Desc = null) : IPageInput;

    [Theory]
    [InlineData(0, 0, 1, 1)]        // 下限:页码 0/页大小 0 → 1/1
    [InlineData(-5, -100, 1, 1)]    // 负数同样钳到下限
    [InlineData(1, 10, 1, 10)]      // 常规值不变
    [InlineData(3, 200, 3, 200)]    // 恰好上限不变
    [InlineData(2, 201, 2, 200)]    // 超上限钳到 200(PageConst.MaxPageSize)
    [InlineData(1, 100000, 1, 200)] // 拖库型超大值钳到 200(S-G B1c 防拖库口径)
    public void NormalizePaging_钳制口径(int pageIndex, int pageSize, int expectedIndex, int expectedSize)
    {
        var input = new ProbePageInput(pageIndex, pageSize);

        var (index, size) = input.NormalizePaging();

        Assert.Equal(expectedIndex, index);
        Assert.Equal(expectedSize, size);
        Assert.Equal(PageConst.MaxPageSize, 200);
    }

    [Fact]
    public void 全部PageInput实现IPageInput契约()
    {
        // 扫描 Core 程序集内 Dto 命名空间下带 PageIndex 的 record(class),断言均实现 IPageInput
        // ——新域新增 Page*Input 漏接接口时本例即红(契约收敛的可执行判据)
        var coreAssembly = typeof(PageConst).Assembly;
        var pageInputs = coreAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, Namespace: not null })
            .Where(t => t.Namespace!.Contains(".Dto", StringComparison.Ordinal))
            .Where(t => t.GetProperty("PageIndex") is not null && t.GetProperty("PageSize") is not null)
            .ToList();

        Assert.True(pageInputs.Count >= 18, $"Page*Input 数量异常(实际 {pageInputs.Count},预期 ≥18——契约扫描口径可能失效)");
        var missing = pageInputs.Where(t => !typeof(IPageInput).IsAssignableFrom(t)).Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0, $"以下分页 DTO 未实现 IPageInput:{string.Join(", ", missing)}");
    }

    [Fact]
    public void 分页扩展入口齐全()
    {
        var methods = typeof(PageQueryExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "ToPageAsync" or "ToPageListAsync" or "NormalizePaging")
            .ToList();

        Assert.Contains(methods, m => m.Name == "NormalizePaging");
        Assert.True(methods.Count(m => m.Name == "ToPageAsync") >= 2, "ToPageAsync 应有两个重载(排序+分页 / 仅分页)");
        Assert.Contains(methods, m => m.Name == "ToPageListAsync" && m.GetParameters().Any(p => p.ParameterType == typeof(RefAsync<int>)));
    }
}
