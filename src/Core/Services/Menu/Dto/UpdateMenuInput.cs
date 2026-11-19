namespace AdminCore.Core.Services.Menu.Dto
{
    /// <summary>
    /// 更新菜单请求 {id, pid, name, title, type, permission, orderNo, icon}(pid 可调整挂接位置;
    /// 口径:禁止挂到自身(成环),父节点必须存在;按钮 Permission 必填同 Add)。
    /// isKeepAlive=是否缓存(0 不缓存/1 缓存;缺省 null = 不修改,部分更新口径)。
    /// </summary>
    public sealed record UpdateMenuInput(long Id, long Pid, string? Name, string? Title, int Type, string? Permission, int? OrderNo, string? Icon, int? IsKeepAlive = null);
}
