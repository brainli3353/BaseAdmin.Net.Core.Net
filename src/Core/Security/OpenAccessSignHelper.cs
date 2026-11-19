using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AdminCore.Core.Security
{
    /// <summary>
    /// 开放接口签名算法助手(批 E 1.6):HMAC-SHA256 密钥生成/签名/验签/时间窗,纯函数单点——
    /// 服务端验签(SignatureAuthenticationHandler)、管理端签名预览(SysOpenAccessService.GenerateSignatureAsync)、
    /// 单测三方共用同一算法实现,防两端漂移(约定 #11)。
    /// </summary>
    /// <remarks>
    /// 移植参照 AdminNetPro(Handler 内 GetMessageForSign/SignData + Service CreateSecret,
    /// 完整规格与魔法常量来源见 docs/研究/e1_refer_research.md §6):
    /// 签名串 = "{METHOD}&{URL}&{accessKey}&{timestamp}&{nonce}"(METHOD 大写、URL=请求 Path 无查询串;
    /// body/query 不参与签名——参照既定口径,安全边界由 nonce+时间窗兜底);
    /// 签名值 = Base64(HMAC-SHA256(secret UTF8 字节, 签名串 UTF8 字节))(非 hex);
    /// 秒/毫秒自判阈值 9999999999(参照 Handler 魔数);验签用恒定时间比较(参照 != 的时序侧信道加固)。
    /// </remarks>
    public static class OpenAccessSignHelper
    {
        /// <summary>秒/毫秒时间戳自判阈值(参照魔数:当前秒级时间戳约 1.78e9,远小于阈值;毫秒级约 1.78e12)。</summary>
        public const long MillisecondsThreshold = 9999999999L;

        /// <summary>
        /// 生成 AccessSecret:16 字节 GUID 的 Base64 截末 2 位 = 22 字符(参照 CreateSecret 口径)。
        /// 仅作生成器——Secret 的存储/校验经 sys_open_access 表,本方法不接触任何状态。
        /// </summary>
        public static string CreateSecret()
            => Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..^2];

        /// <summary>
        /// 生成签名:签名串 = "{METHOD}&{URL}&{accessKey}&{timestamp}&{nonce}" → Base64(HMAC-SHA256)。
        /// timestamp 传**与请求头相同的字符串原值**(秒或毫秒级均可,服务端自判)。
        /// </summary>
        public static string Sign(string accessSecret, string method, string url,
            string accessKey, string timestamp, string nonce)
        {
            ArgumentException.ThrowIfNullOrEmpty(accessSecret);
            var message = $"{method.ToUpperInvariant()}&{url}&{accessKey}&{timestamp}&{nonce}";
            using var hmac = new HMACSHA256();
            hmac.Key = Encoding.UTF8.GetBytes(accessSecret);
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
        }

        /// <summary>验签:按同参数重算签名后恒定时间比较(防时序侧信道;参照 != 的等价加固)。</summary>
        public static bool Verify(string accessSecret, string expectedSign, string method, string url,
            string accessKey, string timestamp, string nonce)
        {
            if (string.IsNullOrEmpty(expectedSign))
            {
                return false;
            }
            var actual = Sign(accessSecret, method, url, accessKey, timestamp, nonce);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expectedSign));
        }

        /// <summary>
        /// 时间戳窗口校验:秒/毫秒自判(阈值 <see cref="MillisecondsThreshold"/>),
        /// |请求时间-当前时间| ≤ drift 通过。参照 Handler 口径(ToUtc 比较,不做本地时区换算)。
        /// </summary>
        public static bool InWindow(string timestamp, TimeSpan drift)
        {
            if (!long.TryParse(timestamp, out var ts))
            {
                return false;
            }
            var requestUtc = ts > MillisecondsThreshold
                ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
            var nowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
            return requestUtc <= nowUtc.Add(drift) && requestUtc >= nowUtc.Subtract(drift);
        }
    }
}
