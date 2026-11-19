using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.Role.Dto
{
    /// <summary>
    /// 角色输出 {id, name, code, remark, sysFlag, dataScope}(sysFlag 供前端标识内置角色、禁用删除/授权入口;
    /// dataScope 为 task_013 数据范围,供前端标识该角色数据可见性)。
    /// </summary>
    public sealed record RoleOutput(long Id, string? Name, string? Code, string? Remark, bool SysFlag, [property: JsonConverter(typeof(LongNumberJsonConverter))] long DataScope);
}
