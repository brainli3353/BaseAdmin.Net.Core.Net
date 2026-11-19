namespace AdminCore.Core.Services.Print.Dto
{
    /// <summary>
    /// 新增打印模板请求 {name, template, printType?, status?, orderNo?, remark?}(JSON camelCase)。
    /// name/template 必填;printType 缺省 1(浏览器);status 缺省 1(启用);orderNo 缺省 100。
    /// WHY 显式 Dto 而非直接用实体(参照仓 AddPrintInput : SysPrint):避免请求体携带 TenantId/审计字段
    /// (约定 #9 + 防越权写租户字段);本仓业务入参一律独立 Dto。
    /// </summary>
    public sealed record AddSysPrintInput(string? Name, string? Template, int? PrintType, int? Status, int? OrderNo, string? Remark);
}
