using AdminCore.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace AdminCore.Core.Security
{
    /// <summary>
    /// TOTP 双因子(task_007):密钥生成 / 校验码验证 / 防重放。
    /// 密钥为 20 字节随机数(Base32),与 baseadmin KeyGeneration.GenerateRandomKey(20) 口径一致;
    /// 算法参数固定 RFC6238 默认 SHA1 / 30s / 6 位(OtpNet 默认参数,不开放配置)。
    /// </summary>
    [Service(Lifetime = ServiceLifetime.Singleton)]
    public class OtpService
    {
        // 校验窗口:允许前后各 1 步(30s)客户端时钟偏差,与 baseadmin VerificationWindow(1,1) 口径一致
        private static readonly VerificationWindow Window = new(1, 1);

        /// <summary>生成首次绑定用随机密钥(Base32,20 字节 → 32 字符)</summary>
        public string GenerateSetupSecret()
            => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

        /// <summary>
        /// TOTP 校验 + 防重放(批 AX F1 起返回**三态**):成功 ⇒ <see cref="OtpValidateResult.Ok"/>;
        /// 码匹配但时间步已被使用 ⇒ <see cref="OtpValidateResult.Reused"/>;不匹配/格式错/密钥坏 ⇒ <see cref="OtpValidateResult.Invalid"/>。
        /// 防重放语义不变(baseadmin 口径):已使用的时间步不可复用(matchedStep &lt;= lastUsedTimeStep 拒绝);
        /// lastUsedTimeStep 为库里已绑定的最后使用步(未绑定传 null,即不做防重放比较)。
        /// WHY 区分"重放"与"错码"(2026-09-28 实测缺陷):两者都判失败,但**提示必须不同**——
        /// 用户把正确但已消费的码再提交一次时,笼统回"校验码验证失败"会让人以为密钥/绑定坏了。
        /// </summary>
        public OtpValidateResult Validate(string code, string secret, long? lastUsedTimeStep)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(secret))
            {
                return OtpValidateResult.Invalid;
            }

            byte[] keyBytes;
            try
            {
                keyBytes = Base32Encoding.ToBytes(secret.Trim());
            }
            catch (Exception)
            {
                // 损坏/非法 Base32 → 按校验失败处理,不升级为 500
                return OtpValidateResult.Invalid;
            }

            if (!new Totp(keyBytes).VerifyTotp(code.Trim(), out var matched, Window))
            {
                return OtpValidateResult.Invalid;
            }

            // 防重放:与最后使用步相同(或更早)的都是重放码(码本身是对的 ⇒ 单独一态,便于正确提示)
            if (lastUsedTimeStep is { } last && matched <= last)
            {
                return OtpValidateResult.Reused;
            }

            return OtpValidateResult.Ok(matched);
        }
    }
}
