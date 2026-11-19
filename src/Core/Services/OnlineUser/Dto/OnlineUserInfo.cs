namespace AdminCore.Core.Services.OnlineUser.Dto
{
    /// <summary>
    /// 在线用户信息(批 F 1.8/5.13;Hash 值载荷,field = ConnectionId)。
    /// 参照 AdminNetPro OnlineUser 移植:去 LoginMode/Device(本仓无多端登录模式,设备信息折入 Browser/Os 展示);
    /// UserName 改名 Account(本仓 Claim/实体字段口径)。
    /// </summary>
    public sealed class OnlineUserInfo
    {
        /// <summary>连接 Id(SignalR ConnectionId,Hash field)。</summary>
        public string ConnectionId { get; set; } = string.Empty;

        /// <summary>用户 Id。</summary>
        public long UserId { get; set; }

        /// <summary>租户 Id。</summary>
        public long TenantId { get; set; }

        /// <summary>账号。</summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>姓名。</summary>
        public string? RealName { get; set; }

        /// <summary>上线时间。</summary>
        public DateTime Time { get; set; }

        /// <summary>登录 IP(优先 X-Forwarded-For,回退 RemoteIpAddress)。</summary>
        public string? Ip { get; set; }

        /// <summary>浏览器(UA 解析,UserAgentHelper)。</summary>
        public string? Browser { get; set; }

        /// <summary>操作系统(UA 解析,UserAgentHelper)。</summary>
        public string? Os { get; set; }
    }
}
