namespace AdminCore.Core.Services.CodeGen.Dto
{
    /// <summary>
    /// 代码生成预输入参 {tableName, className?, moduleName?, removePrefix?, baseType?}(JSON camelCase)。
    /// className 缺省=表名去前缀后 PascalCase;moduleName 缺省 "Business";removePrefix 缺省 "sys_"。
    /// baseType 缺省(null/空)=按表结构推断:含 TenantId 列→EntityTenant,否则 EntityBase(批 BF);显式传值则照传。
    /// </summary>
    public sealed record CodeGenInput(string TableName, string? ClassName = null, string? ModuleName = null,
        string? RemovePrefix = "sys_", string? BaseType = null);
}
