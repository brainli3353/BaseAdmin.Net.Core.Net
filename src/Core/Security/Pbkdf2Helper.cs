using System.Security.Cryptography;
using System.Text;

namespace AdminCore.Core.Security
{
    /// <summary>
    /// PBKDF2 密码哈希(原生实现,零依赖包):16 字节随机盐 + Rfc2898DeriveBytes(10 万轮,SHA256)→ 32 字节派生密钥。
    /// 存储格式 "{saltBase64}${hashBase64}"(盐与哈希拼一列,登录时解析存储串用原盐重算比对)。
    /// </summary>
    public static class Pbkdf2Helper
    {
        // 迭代次数:任务书契约给定 100000(管理端登录低频场景,单次校验约几十毫秒可接受)
        private const int Iterations = 100000;
        private const int SaltSize = 16;
        private const int KeySize = 32;

        /// <summary>
        /// 哈希密码:每次随机新盐(同一密码多次哈希存储值不同,防彩虹表/同密码同哈希碰撞枚举),
        /// 返回 "盐Base64$哈希Base64" 拼接串。
        /// </summary>
        /// <param name="password">明文密码(仅内存中短暂存在,不落日志/不入库)。</param>
        public static string HashPassword(string password)
        {
            // RandomNumberGenerator:密码学安全随机源(非 Random,防盐被预测)
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            // 契约的 Rfc2898DeriveBytes 构造形式在 net10 已标过时(SYSLIB0060),改等价静态 Pbkdf2 方法:
            // 同为 PBKDF2-HMAC-SHA256、同 10 万轮、同 32 字节输出,算法参数与契约完全一致
            var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
            return $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// 校验密码:解析存储串 → 用原盐重算派生密钥 → 恒定时间比对。
        /// </summary>
        /// <remarks>
        /// WHY 恒定时间比较(CryptographicOperations.FixedTimeEquals):逐字节比较在首个不匹配处提前返回,
        /// 响应时长随前缀匹配长度变化,攻击者可用时序侧信道逐字节猜出哈希;FixedTimeEquals 始终跑完全部字节。
        /// 畸形存储串(无 $ / 段数错 / 非 Base64)一律返回 false 而非抛异常:
        /// 历史脏数据/库被人篡改时应表现为"登录失败",而不是 500 泄漏内部异常。
        /// </remarks>
        public static bool VerifyPassword(string password, string? stored)
        {
            var parts = stored?.Split('$');
            if (parts is null || parts.Length != 2)
            {
                return false;
            }

            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(parts[0]);
                expected = Convert.FromBase64String(parts[1]);
            }
            catch (FormatException)
            {
                // 存储串非 Base64(脏数据)→ 按校验失败处理
                return false;
            }

            // 重算长度对齐存储哈希长度:若存储哈希长度被改(≠32),比对必败,不额外报错
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }
}
