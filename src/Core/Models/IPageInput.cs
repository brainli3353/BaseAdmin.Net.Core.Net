namespace AdminCore.Core.Models
{
    /// <summary>
    /// 分页入参统一契约(批 S-G 纵深期·分页原语收口 B5b):各域 <c>Page*Input</c> record 声明实现本接口
    /// (位置参数自动属性即满足,不改公开构造/JSON 形态),使分页管道可经 <see cref="PageQueryExtensions"/>
    /// 统一钳制(<see cref="PageConst"/>)与分页(<see cref="PageResult{T}"/>)。
    /// </summary>
    /// <remarks>
    /// 契约语义(与既有各调用点口径逐点一致):<see cref="PageIndex"/> 下限 1(原 <c>Math.Max(1, …)</c>);
    /// <see cref="PageSize"/> 下限 1、上限 <see cref="PageConst.MaxPageSize"/>(原 <c>Math.Clamp(…, 1, MaxPageSize)</c>);
    /// <see cref="OrderBy"/>/<see cref="Desc"/> 为表头动态排序入参(批 CI),缺省走各端点默认序。
    /// 个别不含动态排序的 DTO(如 FilePageInput/PageOpenAccessInput)以**显式接口实现**返回 null,公开形态不变。
    /// </remarks>
    public interface IPageInput
    {
        /// <summary>页码(1 起;经 <see cref="PageQueryExtensions.NormalizePaging"/> 统一下限)。</summary>
        int PageIndex { get; }

        /// <summary>每页行数(经 <see cref="PageQueryExtensions.NormalizePaging"/> 统一钳制 1..MaxPageSize)。</summary>
        int PageSize { get; }

        /// <summary>排序字段(实体属性名/库列名,白名单校验;null = 各端点默认序)。</summary>
        string? OrderBy { get; }

        /// <summary>排序方向(null = 降序,与既有 ApplyDynamicSort 口径一致)。</summary>
        bool? Desc { get; }
    }
}
