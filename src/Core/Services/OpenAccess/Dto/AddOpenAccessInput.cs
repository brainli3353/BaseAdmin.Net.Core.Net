namespace AdminCore.Core.Services.OpenAccess.Dto
{
    /// <summary>
    /// 开放接口建号入参 {accessKey, bindUserId, bindTenantId}(JSON camelCase)。
    /// AccessSecret 不接受入参——服务端自动生成(22 字符,仅建号响应一次性返回)。
    /// </summary>
    public sealed record AddOpenAccessInput(string AccessKey, long BindUserId, long BindTenantId);
}
