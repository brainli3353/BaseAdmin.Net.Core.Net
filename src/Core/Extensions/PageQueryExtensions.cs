using System.Linq.Expressions;
using AdminCore.Core.Models;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

namespace AdminCore.Core.Extensions
{
    /// <summary>
    /// 分页查询扩展(批 S-G 纵深期·分页原语收口 B5b):把各域 PageAsync 重复的
    /// "钳制(<c>Math.Max(1,PageIndex)</c> + <c>Math.Clamp(PageSize,1,MaxPageSize)</c>) +
    /// <c>ToPageListAsync</c> + <c>new PageResult</c>" 样板收口为两个重载。
    /// </summary>
    /// <remarks>
    /// 等价性口径(与被替换的既有写法逐点一致):
    /// ① <see cref="NormalizePaging"/> = 原 <c>(Math.Max(1, input.PageIndex), Math.Clamp(input.PageSize, 1, PageConst.MaxPageSize))</c>;
    /// ② 重载二(保留调用方排序链):原 <c>query.ToPageListAsync(钳制, total)</c> + <c>new PageResult{T}{Total=total,List=list}</c>,
    ///    供"多级默认序/固定序/join 投影"调用点在自身 ApplyDynamicSort/OrderBy 之后接用(泛型 T 不加 new() 约束,匿名投影可用);
    /// ③ 重载一(排序一并收口):内部施加与既有调用同参的 <see cref="DynamicQueryExtensions.ApplyDynamicSort"/>(单表达式默认序形式)
    ///    后走重载二,供"单默认序"调用点整体替换(排序逻辑同一条链,行为不变)。
    /// </remarks>
    public static class PageQueryExtensions
    {
        /// <summary>
        /// 统一分页钳制：<c>PageSize</c> 夹 <c>1..PageConst.MaxPageSize</c>；<c>PageIndex</c> 夹下界 1
        /// ＋**按页大小动态夹上界**（防深页 int 溢出，见 remarks）。
        /// </summary>
        /// <remarks>
        /// ⚠ **页上界不是随便一个大数**：SqlSugar（5.1.4.221）内部 <c>Skip = (pageIndex - 1) * pageSize</c> 是
        /// <b>int 乘法且无上界</b>。只夹下界时，<c>pageIndex=25,000,000</c> ＋ <c>pageSize=100</c>（int 范围内
        /// 完全合法的入参）⇒ 乘积溢出为负 ⇒ 生成负 OFFSET ⇒ PG 报 <c>OFFSET must not be negative</c>；
        /// 而同形的业务手写分页（<c>Math.Max(0, 负数)</c>）会**静默返回第 1 页**，比报错更难发现。
        /// 上界取 <c>int.MaxValue / pageSize + 1</c>（保证 <c>(pageIndex-1)*pageSize ≤ int.MaxValue</c>），
        /// 超深页自然得到**空页**——语义比报错可用，也贴合"翻超界＝没有数据"。
        /// 覆盖面＝本仓全部分页调用点（三个分页重载都经本方法）；**绕过本原语直调 SqlSugar 分页重载**的写法
        /// 不在此保护面内（那属绕过，不是原语缺陷）。
        /// （上游回灌 2026-10-28，来源＝fork 侧实证清单。）
        /// </remarks>
        public static (int PageIndex, int PageSize) NormalizePaging(this IPageInput input)
        {
            var pageSize = Math.Clamp(input.PageSize, 1, PageConst.MaxPageSize);
            // 用 long 算上界再夹回 int：pageSize=1 时 int.MaxValue/1+1 已溢出，必须先 long。
            var maxPageIndex = (int)Math.Min(int.MaxValue, (long)int.MaxValue / pageSize + 1);
            return (Math.Min(Math.Max(1, input.PageIndex), maxPageIndex), pageSize);
        }

        /// <summary>
        /// 重载一(排序+分页一体):按 <paramref name="defaultSort"/>/<paramref name="defaultOrder"/> 施加动态排序
        /// (OrderBy 非空时白名单校验+表达式树排序,同既有 ApplyDynamicSort 单表达式重载)后钳制分页并构造
        /// <see cref="PageResult{T}"/>。调用点原 <c>.ApplyDynamicSort(input.OrderBy, input.Desc, u => u.Id, OrderByType.Desc)</c>
        /// + <c>.ToPageListAsync(钳制, total)</c> + <c>new PageResult</c> 三段整体替换为本方法。
        /// </summary>
        public static async Task<PageResult<T>> ToPageAsync<T>(
            this ISugarQueryable<T> query,
            IPageInput input,
            Expression<Func<T, object?>> defaultSort,
            OrderByType defaultOrder = OrderByType.Desc)
            where T : class, new()
        {
            RefAsync<int> total = 0;
            return await query
                .ApplyDynamicSort(input.OrderBy, input.Desc, defaultSort, defaultOrder)
                .ToPageAsync(input, total);
        }

        /// <summary>
        /// 重载三(仅钳制,返回原始行):供"需要投影/手工构造 PageResult/多级默认序"的调用点替换
        /// 原 <c>.ToPageListAsync(Math.Max(1, input.PageIndex), Math.Clamp(input.PageSize, 1, PageConst.MaxPageSize), total)</c>
        /// ——钳制口径与 <see cref="NormalizePaging"/> 同源,行为逐点一致。
        /// </summary>
        public static async Task<List<T>> ToPageListAsync<T>(
            this ISugarQueryable<T> query,
            IPageInput input,
            RefAsync<int> total)
        {
            var (pageIndex, pageSize) = input.NormalizePaging();
            return await query.ToPageListAsync(pageIndex, pageSize, total);
        }

        /// <summary>
        /// 重载二(仅钳制+分页+构造 PageResult):供调用点**保留原排序链**(多级默认序/固定序/join 投影——
        /// ApplyDynamicSort 链形重载或 OrderByDescending 之后)接用,替换原 <c>.ToPageListAsync(钳制, total)</c> +
        /// <c>new PageResult{T}</c> 两段;排序行为与原调用点逐点一致(扩展内不再触碰 ORDER BY)。
        /// </summary>
        public static async Task<PageResult<T>> ToPageAsync<T>(
            this ISugarQueryable<T> query,
            IPageInput input,
            RefAsync<int> total)
        {
            var (pageIndex, pageSize) = input.NormalizePaging();
            var list = await query.ToPageListAsync(pageIndex, pageSize, total);
            return new PageResult<T> { Total = total, List = list };
        }
    }
}
