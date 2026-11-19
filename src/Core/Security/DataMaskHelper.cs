using AdminCore.Core.Attributes;

namespace AdminCore.Core.Security
{
    /// <summary>
    /// 敏感数据脱敏工具(批 D 6.11):静态纯函数,供 Output DTO 投影/日志展示显式调用。
    /// 原则:保留识别头尾片段、中间掩码;null/空/长度不足的值原样返回
    /// (过掩会丢信息或产生歧义——如 5 位"手机号"本身即非法,保持原样由上游校验负责)。
    /// </summary>
    public static class DataMaskHelper
    {
        /// <summary>按类型脱敏;null/空原样返回。</summary>
        public static string? Mask(string? value, MaskType type)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return type switch
            {
                MaskType.Phone => MaskPhone(value),
                MaskType.IdCard => MaskIdCard(value),
                MaskType.Email => MaskEmail(value),
                MaskType.BankCard => MaskBankCard(value),
                MaskType.Name => MaskName(value),
                _ => value
            };
        }

        /// <summary>手机号(11 位):138****5678;非 11 位原样(非法号不掩)。</summary>
        private static string MaskPhone(string value)
            => value.Length == 11 ? $"{value[..3]}****{value[7..]}" : value;

        /// <summary>身份证号(15/18 位):前 6 + 中间全掩 + 后 4;其余长度原样。</summary>
        private static string MaskIdCard(string value)
            => value.Length is 15 or 18 ? $"{value[..6]}{new string('*', value.Length - 10)}{value[^4..]}" : value;

        /// <summary>邮箱:本地段首字符 + *** + @域名;无 @ 或本地段不足 2 字符原样。</summary>
        private static string MaskEmail(string value)
        {
            var at = value.IndexOf('@');
            return at <= 1 ? value : $"{value[0]}***{value[at..]}";
        }

        /// <summary>银行卡号:保留后 4 位,其余全掩;不足 8 位原样(掩了就没信息量)。</summary>
        private static string MaskBankCard(string value)
            => value.Length >= 8 ? new string('*', value.Length - 4) + value[^4..] : value;

        /// <summary>姓名:保留首字符,其余掩码;单字符原样。</summary>
        private static string MaskName(string value)
            => value.Length > 1 ? value[0] + new string('*', value.Length - 1) : value;
    }
}
