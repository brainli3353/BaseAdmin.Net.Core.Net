namespace AdminCore.Core.Services.Org.Dto
{
    /// <summary>
    /// 更新机构请求 {id, pid?, name?, code?, sort?, remark?, status?}。
    /// WHY 全字段可空 + null=不改(部分更新语义):管理端常见只发改动字段的提交(如仅改名),
    /// 若 Pid 取默认 0 会把子节点静默改挂顶级、破坏树结构(验收「Delete 有子节点的父节点 → 400」
    /// 这类依赖父子关系的断言会连带失真);Pid 显式传 0 才是改挂顶级。
    /// </summary>
    public sealed record UpdateOrgInput(long Id, long? Pid, string? Name, string? Code, int? Sort, string? Remark, int? Status);
}
