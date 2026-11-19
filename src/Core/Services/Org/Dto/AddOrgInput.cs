namespace AdminCore.Core.Services.Org.Dto
{
    /// <summary>
    /// 新建机构请求 {pid(0=顶级), name, code?, sort?, remark?, status?}。
    /// 口径:Name 必填;Code 可空(缺省时不参与唯一校验),提供时服务层校验全表唯一(重复 → 400)。
    /// </summary>
    public sealed record AddOrgInput(long Pid, string? Name, string? Code, int Sort = 0, string? Remark = null, int Status = 0);
}
