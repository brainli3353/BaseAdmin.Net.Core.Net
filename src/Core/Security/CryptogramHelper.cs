using AdminCore.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AdminCore.Core.Security
{
    /// <summary>
    /// AES 对称加解密(task_007):OTP 密钥密文入库,库内不落明文。
    /// 算法锁定 AES-256-CBC + PKCS7,每次加密生成新随机 IV(同明文→不同密文),
    /// 密文格式 = Base64(IV || 密文);密钥来自配置 <c>Cryptogram:Key</c>(Base64,32 字节,
    /// 生产由环境变量注入,仓库内仅为开发占位)。
    /// </summary>
    [Service(Lifetime = ServiceLifetime.Singleton)]
    public class CryptogramHelper
    {
        private const int KeySize = 32;
        private const int IvSize = 16;

        private readonly byte[] _key;

        public CryptogramHelper(IConfiguration config)
        {
            var b64 = config["Cryptogram:Key"];
            if (string.IsNullOrWhiteSpace(b64))
            {
                throw new InvalidOperationException("缺少 Cryptogram:Key 配置(32 字节 Base64 密钥材料)");
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("Cryptogram:Key 不是合法的 Base64 串");
            }

            if (key.Length != KeySize)
            {
                throw new InvalidOperationException($"Cryptogram:Key 必须为 32 字节(实际 {key.Length})");
            }

            _key = key;
        }

        /// <summary>加密:输出 Base64(IV || 密文)</summary>
        public string Encrypt(string plainText)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();

            using var enc = aes.CreateEncryptor();
            var cipher = enc.TransformFinalBlock(Encoding.UTF8.GetBytes(plainText ?? string.Empty), 0, Encoding.UTF8.GetByteCount(plainText ?? string.Empty));

            var result = new byte[IvSize + cipher.Length];
            aes.IV.CopyTo(result, 0);
            cipher.CopyTo(result, IvSize);
            return Convert.ToBase64String(result);
        }

        /// <summary>解密:输入 Base64(IV || 密文);格式非法/被篡改抛异常,由业务层决定语义(如按"校验失败"处理)</summary>
        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrWhiteSpace(cipherText))
            {
                throw new ArgumentException("密文为空", nameof(cipherText));
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(cipherText);
            }
            catch (FormatException)
            {
                throw new ArgumentException("密文不是合法的 Base64", nameof(cipherText));
            }

            if (raw.Length <= IvSize)
            {
                throw new ArgumentException("密文格式非法", nameof(cipherText));
            }

            using var aes = Aes.Create();
            aes.Key = _key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.IV = raw.AsSpan(0, IvSize).ToArray();

            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(raw, IvSize, raw.Length - IvSize);
            return Encoding.UTF8.GetString(plain);
        }
    }
}
