namespace AdminCore.Core.Services.OnlineUser.Dto
{
    /// <summary>
    /// 在线列表广播载荷(批 F 5.13):连接/断开时向租户分组全量推送(参照口径,简单全量)。
    /// 客户端收到后刷新在线用户面板;RealName/Online 供客户端做变动提示。
    /// </summary>
    public sealed class OnlineUserListMessage
    {
        /// <summary>触发变动的用户姓名。</summary>
        public string? RealName { get; set; }

        /// <summary>true=上线,false=下线。</summary>
        public bool Online { get; set; }

        /// <summary>当前租户在线列表(全量)。</summary>
        public List<OnlineUserInfo> UserList { get; set; } = new();
    }
}
