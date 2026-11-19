namespace AdminCore.Core.Services.Config.Dto
{
    /// <summary>
    /// 配置更新请求 {id, name?, value?, groupCode?, sysFlag?, remark?, enumOptions?}(JSON camelCase,部分更新)。
    /// 口径(约定 #9):null = 不改;空串/false = 显式清空(清值/清分组/改回非内置均为合法操作)。
    /// code 不可改(配置标识是读取方的稳定契约,改标识 = 新增 + 删除)。
    /// </summary>
    public sealed record UpdateConfigInput(long Id, string? Name = null, string? Value = null, string? GroupCode = null, bool? SysFlag = null, string? Remark = null, string? EnumOptions = null);
}
