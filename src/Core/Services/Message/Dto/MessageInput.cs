namespace AdminCore.Core.Services.Message.Dto
{
    /// <summary>
    /// 站内消息发送请求 {messageType, title, message, sendUserId, receiveUserIds}(JSON camelCase;参照 MessageInput 字面量)。
    /// messageType 数值域见 MessageTypeEnum(0 消息 1 成功 2 警告 3 错误)。
    /// </summary>
    public sealed record MessageInput(int MessageType, string Title, string Message, long SendUserId, List<long> ReceiveUserIds);
}
