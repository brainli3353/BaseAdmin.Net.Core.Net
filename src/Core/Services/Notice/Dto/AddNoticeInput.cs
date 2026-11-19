namespace AdminCore.Core.Services.Notice.Dto
{
    /// <summary>
    /// 通知公告新建请求 {title, content, type}:发布人快照(Id/姓名/机构)由服务端自填,前端不传;
    /// 初始态恒草稿(发布走 /public 端点)。type 缺省 1(通知)。
    /// </summary>
    public sealed record AddNoticeInput(string Title, string Content, int Type = 1);
}
