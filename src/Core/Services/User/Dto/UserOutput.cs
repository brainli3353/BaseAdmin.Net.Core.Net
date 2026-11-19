using AdminCore.Core.Attributes;

namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>
    /// 用户分页行输出 {id, account, realName, status, accountType, orgId, posId}(密码/哈希等敏感字段永不外泄;
    /// orgId/posId 为批 B 数据权限收口 B1 补的机构/职位归属,供前端编辑回显)。
    /// 批 CO:各属性加 [ExportColumn] 列定义,供通用导出服务按声明顺序生成列(Order 1..7)。
    /// </summary>
    public sealed record UserOutput(
        [property: ExportColumn("用户ID", Order = 1)] long Id,
        [property: ExportColumn("账号", Order = 2)] string? Account,
        [property: ExportColumn("姓名", Order = 3)] string? RealName,
        [property: ExportColumn("状态", Order = 4)] int Status,
        [property: ExportColumn("账号类型", Order = 5)] int AccountType,
        [property: ExportColumn("机构ID", Order = 6)] long? OrgId,
        [property: ExportColumn("职位ID", Order = 7)] long? PosId);
}