using AdminCore.Core.Entities;
using AdminCore.Core.Services.OnlineUser.Dto;

namespace AdminCore.Core.Hub
{
    /// <summary>
    /// 在线用户 Hub 客户端契约(批 F 5.13;批 G 补全消息面;Hub&lt;T&gt; 强类型客户端消息名映射)。
    /// 客户端须订阅:onlineUserList(在线列表变动)/forceOffline(强制下线,收到后断开)/
    /// publicNotice(通知公告发布推送,载荷 SysNotice)/receiveMessage(站内消息单播,载荷=消息日志全量)。
    /// 参照 AdminNetPro IOnlineUserHub 全消息面移植(批 F 先裁 2 方法,批 G 4.7/4.8 落地兑现);
    /// 参照的 client→server 方法(SendMessageToUser/SendMessageToAllUser)不移植——本仓消息发送入口仅服务端(裁决②)。
    /// </summary>
    public interface IOnlineUserHub
    {
        /// <summary>在线用户列表变动(上线/下线时服务端向租户分组推送全量列表)。</summary>
        Task OnlineUserList(OnlineUserListMessage message);

        /// <summary>强制下线通知(服务端推送,参数=提示文案;客户端收到应断开连接并提示)。</summary>
        Task ForceOffline(string message);

        /// <summary>通知公告发布推送(批 G 4.7;服务端向在线接收人单播,载荷=通知实体,客户端收到渲染通知)。</summary>
        Task PublicNotice(SysNotice notice);

        /// <summary>站内消息单播(批 G 4.8;服务端向在线接收人单播,载荷=消息日志全量(含发送方/接收方快照),object 兼容两类载荷)。</summary>
        Task ReceiveMessage(object context);
    }
}
