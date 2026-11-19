namespace AdminCore.Core.Services.OpenAccess.Dto
{
    /// <summary>
    /// 开放接口分页/详情出参(不含 AccessSecret——密钥仅建号/重置响应一次性返回,分页不回显)。
    /// BindUserAccount/BindTenantName 由服务层 LeftJoin 映射(展示用)。
    /// 注意:class 而非 record——SqlSugar ToPageListAsync 的动态构建不认 positional record 生成构造器。
    /// </summary>
    public sealed class OpenAccessOutput
    {
        public long Id { get; set; }

        public string AccessKey { get; set; } = string.Empty;

        public long BindUserId { get; set; }

        public string? BindUserAccount { get; set; }

        public long BindTenantId { get; set; }

        public string? BindTenantName { get; set; }

        public DateTime? CreateTime { get; set; }

        public DateTime? UpdateTime { get; set; }
    }
}
