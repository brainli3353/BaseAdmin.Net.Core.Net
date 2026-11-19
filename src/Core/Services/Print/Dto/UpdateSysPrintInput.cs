namespace AdminCore.Core.Services.Print.Dto
{
    /// <summary>
    /// 更新打印模板请求 {id, name?, template?, printType?, status?, orderNo?, remark?}——
    /// **部分更新口径:null=不改该字段**(与 UpdateUserInput/UpdateTenantInput 同口径,约定 #9;
    /// 不复用参照仓的 IgnoreColumns(true) 整对象覆盖)。
    /// </summary>
    public sealed record UpdateSysPrintInput(long Id, string? Name, string? Template, int? PrintType, int? Status, int? OrderNo, string? Remark);
}
