using AdminCore.Core.Extensions;
using AdminCore.Core.Models;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 上游回灌③（2026-10-28，来源 fork 侧实证）：<see cref="PageQueryExtensions.NormalizePaging"/> 必须
/// **按页大小动态夹页上界**，防 SqlSugar 内部 `Skip=(pageIndex-1)*pageSize` 的 int 乘法溢出成负 OFFSET
/// （PG 报 `OFFSET must not be negative`；业务侧同形手写分页则静默回第 1 页）。
/// 零库：纯函数，不连 PG/Redis。
/// </summary>
public class BatchBackflowPagingGuardTest
{
    private sealed record Probe(int PageIndex, int PageSize, string? OrderBy = null, bool? Desc = null) : IPageInput;

    [Fact]
    public void 深页入参_页上界被夹且offset不溢出()
    {
        // 修复前的爆点：25,000,000 × 100 = 2,499,999,900 > int.MaxValue ⇒ 溢出为负
        var (pageIndex, pageSize) = new Probe(25_000_000, 100).NormalizePaging();

        Assert.Equal(100, pageSize);
        Assert.True(pageIndex <= int.MaxValue / pageSize + 1,
            $"页上界未按 pageSize 夹紧：pageIndex={pageIndex}");
        var offset = (long)(pageIndex - 1) * pageSize;
        Assert.InRange(offset, 0, int.MaxValue);
    }

    [Fact]
    public void 极值边界_pageSize为1时intMax也不溢出()
    {
        var (pageIndex, pageSize) = new Probe(int.MaxValue, 1).NormalizePaging();

        Assert.Equal(1, pageSize);
        Assert.Equal(int.MaxValue, pageIndex); // 合法页不被过度夹紧（上界=MaxValue）
        Assert.True((long)(pageIndex - 1) * pageSize <= int.MaxValue);
    }

    [Fact]
    public void 反向哨兵_正常页原样返回()
    {
        var (pageIndex, pageSize) = new Probe(2, 20).NormalizePaging();

        Assert.Equal(2, pageIndex);
        Assert.Equal(20, pageSize);
    }

    [Fact]
    public void 非法入参_下限钳制()
    {
        var (pageIndex, pageSize) = new Probe(-5, 0).NormalizePaging();

        Assert.Equal(1, pageIndex);
        Assert.Equal(1, pageSize);
    }
}
