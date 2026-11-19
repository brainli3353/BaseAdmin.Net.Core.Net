namespace AdminCore.Core.Services.Org.Dto
{
    /// <summary>
    /// 删除机构 body 兜底绑定 {id}:delete 端点 id 双源绑定([FromQuery] 优先,无 query 时取本 body)。
    /// Id 默认 0:body 缺省时不报错(由控制器合并 query 源裁决)。
    /// </summary>
    public sealed record DeleteOrgInput(long Id = 0);
}
