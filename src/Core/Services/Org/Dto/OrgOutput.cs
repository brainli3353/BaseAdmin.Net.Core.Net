namespace AdminCore.Core.Services.Org.Dto
{
    /// <summary>
    /// 机构输出/树节点 {id, pid, name, code, sort, remark, status, children, disabled}(Tree 端点递归返回;
    /// 叶子节点 Children 为空列表,前端无需判空)。
    /// Disabled(批 H 2.5):非超管视角下范围外机构=true(前端选择器禁用,防越权挂靠/创建);
    /// 超管与全量范围恒 false。
    /// </summary>
    public sealed record OrgOutput(long Id, long Pid, string? Name, string? Code, int Sort, string? Remark, int Status, List<OrgOutput> Children, bool Disabled = false);
}
