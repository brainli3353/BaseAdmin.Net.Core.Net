namespace AdminCore.Core.Utils.DynamicQuery
{
    /// <summary>
    /// 动态查询入参(批 AG 3.11):排序与关键字搜索的**字段名由调用方给出、由框架按实体元数据白名单校验**。
    /// </summary>
    /// <remarks>
    /// 防注入口径(本能力的存在理由):
    /// ① **字段名**——只允许实体元数据里真实存在的属性名/列名(大小写不敏感),非法即抛业务异常;
    ///    绝不把请求里的字符串直接拼进 SQL 的 ORDER BY/WHERE 片段(那正是动态查询最典型的注入面)。
    /// ② **方向**——用 <see cref="Desc"/> 布尔而非字符串(杜绝 <c>ORDER BY x {方向}</c> 这类拼接)。
    /// ③ **值**——关键字只作为**参数值**参与 LIKE(见 DynamicQueryExtensions),不参与语句结构。
    /// </remarks>
    public sealed record DynamicQueryInput(string? SearchBy, string? OrderBy, bool Desc = true);
}