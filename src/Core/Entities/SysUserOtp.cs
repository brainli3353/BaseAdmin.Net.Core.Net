using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 用户 OTP 绑定(task_007):一个用户一条密钥记录。
    /// SecretCipher 为 AES 密文(CryptogramHelper 格式,库内不落明文密钥);
    /// LastUsedTimeStep 为防重放基线(已校验的时间步不可复用,见 OtpService.Validate)。
    /// </summary>
    /// <remarks>
    /// t846 批 1 纳管（2026-10-26，DDL 与实体同批）：改继承 <see cref="EntityTenant"/>。
    /// 登录期无租户 Claim ⇒ PermissionMiddleware 不加过滤，OTP 查询路径不受影响；
    /// 已认证后的改绑/解绑按 Claim 租户过滤，回填链取绑定用户自身的 TenantId（同源，必匹配）。
    /// 存量缺 (UserId) 唯一约束属另一议题（专项件 §10.1 已登记，本批不擅改）。
    /// </remarks>
    [SugarTable("sys_user_otp", "用户OTP绑定")]
    [IncreTable]
    public class SysUserOtp : EntityTenant
    {
        /// <summary>用户 Id(关联 sys_user 主键;一个用户唯一一条绑定记录)。</summary>
        [SugarColumn(ColumnDescription = "用户Id")]
        public long UserId { get; set; }

        /// <summary>OTP 密钥密文(AES-256-CBC,Base64(IV||密文);首次校验通过时写入,重绑时覆盖)。</summary>
        [SugarColumn(ColumnDescription = "OTP密钥密文", Length = 256)]
        public string? SecretCipher { get; set; }

        /// <summary>最后校验通过的时间步(防重放基线;未校验过为 null)。</summary>
        [SugarColumn(ColumnDescription = "最后使用时间步", IsNullable = true)]
        public long? LastUsedTimeStep { get; set; }

        /// <summary>是否启用(false = 绑定被禁用,下次登录重新走绑定流程)。</summary>
        [SugarColumn(ColumnDescription = "是否启用")]
        public bool Enabled { get; set; }

        /// <summary>首次绑定时间(重绑不更新,保留初始绑定痕迹)。</summary>
        [SugarColumn(ColumnDescription = "绑定时间", IsNullable = true)]
        public DateTime? BindTime { get; set; }
    }
}
