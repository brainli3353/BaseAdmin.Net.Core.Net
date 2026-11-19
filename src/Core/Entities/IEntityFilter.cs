namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 软删过滤标记接口:实现本接口的实体,其查询/更新/删除自动被全局 QueryFilter 追加 IsDelete=false 条件。
    /// </summary>
    public interface IDeletedFilter
    {
        /// <summary>软删标记(为 true 的行对常规查询不可见;软删=更新本字段为 true,非物理删除)。</summary>
        bool IsDelete { get; set; }
    }
}
