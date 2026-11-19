namespace AdminCore.Core.Services.Role.Dto
{
    /// <summary>
    /// 新建角色请求 {name, code, remark, dataScope}(Code 服务层校验唯一)。
    /// dataScope 可空:缺省按实体默认 4(仅本人)落库(task_013 数据范围,取值 1-5)。
    /// </summary>
    public sealed record AddRoleInput(string? Name, string? Code, string? Remark, long? DataScope);
}
