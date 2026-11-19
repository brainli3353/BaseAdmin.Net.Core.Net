namespace AdminCore.Core.Services.Config.Dto
{
    /// <summary>
    /// 配置行输出 {id, name, code, value, groupCode, sysFlag, remark, enumOptions, createTime, updateTime}。
    /// enumOptions 非空 = 枚举型配置(管理端值列渲染为快捷下拉)。
    /// </summary>
    public sealed record SysConfigOutput(long Id, string? Name, string Code, string Value, string? GroupCode, bool SysFlag, string? Remark, DateTime? CreateTime, DateTime? UpdateTime, string? EnumOptions = null);
}
