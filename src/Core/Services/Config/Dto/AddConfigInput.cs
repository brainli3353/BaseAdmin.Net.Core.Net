namespace AdminCore.Core.Services.Config.Dto
{
    /// <summary>
    /// 配置新建请求 {name?, code, value, groupCode?, sysFlag?, remark?, enumOptions?}(JSON camelCase)。
    /// code 必填且唯一;value 可空串(空值合法);sysFlag 缺省 false(业务新增项非内置)。
    /// enumOptions 非空 = 枚举型配置(每行 `值|显示名`),管理端值列渲染为快捷下拉。
    /// </summary>
    public sealed record AddConfigInput(string? Name, string Code, string Value, string? GroupCode, bool SysFlag = false, string? Remark = null, string? EnumOptions = null);
}
