namespace AdminCore.Core.Security
{
    /// <summary>
    /// TOTP 校验结果(批 AX F1):把「码不对」与「码对但已被用过(防重放)」**分开**。
    /// </summary>
    /// <remarks>
    /// WHY 需要三态(实测缺陷):修前 <see cref="OtpService.Validate"/> 对"不匹配"与"重放"**都返回 null**,
    /// 调用方只能回笼统的 <c>auth.otp_verify_failed</c>(「校验码验证失败」)——用户把一个**正确但已消费**的动态码
    /// 再提交一次(连点/同一步骤内重试),看到的却是"验证失败",于是以为绑定坏了、密钥不对(2026-09-28 实测:
    /// 登录日志出现同一秒"成功+失败"成对,即同码重复提交被判重放)。
    ///
    /// 三态编码(单类型,不引额外枚举文件):
    /// - <see cref="Ok"/>:校验通过,<see cref="MatchedStep"/> 为命中的时间步(写库做防重放基线);
    /// - <see cref="Reused"/>:码**匹配**某时间步,但该步 ≤ 已用基线 ⇒ 重放(应提示"请用最新码",**不是**"码错误");
    /// - <see cref="Invalid"/>:不匹配/格式错/密钥损坏(既有语义,回 <c>auth.otp_verify_failed</c>)。
    ///
    /// 安全边界:**只有 <see cref="Ok"/> 可登录**;三态只是把失败原因区分开以便正确提示,不放松任何判据。
    /// </remarks>
    /// <param name="IsValid">是否通过(仅 Ok 为 true)。</param>
    /// <param name="IsReused">是否属"正确但已用过"的重放。</param>
    /// <param name="MatchedStep">命中的 TOTP 时间步(仅 Ok 非空)。</param>
    public sealed record OtpValidateResult(bool IsValid, bool IsReused, long? MatchedStep)
    {
        /// <summary>不匹配/格式错/密钥损坏(既有语义)。</summary>
        public static OtpValidateResult Invalid { get; } = new(false, false, null);

        /// <summary>码正确但时间步已被使用(防重放命中)。</summary>
        public static OtpValidateResult Reused { get; } = new(false, true, null);

        /// <summary>校验通过(携带命中时间步)。</summary>
        public static OtpValidateResult Ok(long matchedStep) => new(true, false, matchedStep);
    }
}