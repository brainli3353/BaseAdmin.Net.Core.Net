namespace AdminCore.Core.Services.Menu.Dto
{
    /// <summary>
    /// 菜单树节点输出 {id, pid, name, title, type, permission, orderNo, icon, status, children}(Tree 端点递归返回;
    /// 前端按 Type 渲染目录/菜单/按钮,permission 即接口权限键;status=状态(0 启用/1 停用,批 CH G28:管理端启停开关消费);
    /// 叶子节点 Children 为空列表,前端无需判空);
    /// isKeepAlive=是否缓存(0 不缓存/1 缓存,前端据此决定路由 meta.keepAlive)。
    /// </summary>
    public sealed record MenuOutput(long Id, long Pid, string? Name, string? Title, int Type, string? Permission, int OrderNo, string? Icon, int Status, int IsKeepAlive, List<MenuOutput> Children);
}
