namespace AdminCore.Core.Services.CodeGen.Dto
{
    /// <summary>
    /// 代码生成预览输出 {tableName, className, entityCode, dtoCode, serviceCode, warnings}。
    /// **只返回文本,不写盘**(见 CodeGenService remarks):由开发者复制到目标工程。
    /// warnings=生成过程中的提示(如未知列类型已回落 string、主键/审计列已跳过)。
    /// </summary>
    public sealed record CodeGenPreviewOutput(string TableName, string ClassName, string EntityCode, string DtoCode,
        string ServiceCode, List<string> Warnings);
}
