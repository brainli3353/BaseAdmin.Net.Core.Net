namespace AdminCore.Core.Services.Menu.Dto
{
    /// <summary>
    /// 新建菜单请求 {pid(0=顶级), name, title, type(1目录2菜单3按钮), permission, orderNo, icon}。
    /// 口径:Type=3(按钮)时 Permission 必填(按钮权限标识=接口路由,如 sysUser/page);菜单/目录可为空。
    /// isKeepAlive=是否缓存(0 不缓存/1 缓存;缺省 null 按 1 处理,与加列前行为一致)。
    /// </summary>
    public sealed record AddMenuInput(long Pid, string? Name, string? Title, int Type, string? Permission, int? OrderNo, string? Icon, int? IsKeepAlive = null);
}
