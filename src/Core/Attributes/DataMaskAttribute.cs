using System.ComponentModel;

namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 敏感数据脱敏类型(批 D 6.11):决定 DataMaskHelper 使用的脱敏算法。
    /// </summary>
    [Description("脱敏类型")]
    public enum MaskType
    {
        /// <summary>手机号:保留前 3 后 4,中间掩码(138****5678)。</summary>
        [Description("手机号")]
        Phone,

        /// <summary>身份证号:保留前 6 后 4,中间掩码。</summary>
        [Description("身份证号")]
        IdCard,

        /// <summary>邮箱:保留本地段首字符 + 域名,本地段其余掩码(a***@example.com)。</summary>
        [Description("邮箱")]
        Email,

        /// <summary>银行卡号:保留后 4 位,其余掩码。</summary>
        [Description("银行卡号")]
        BankCard,

        /// <summary>姓名:保留首字符,其余掩码(张**)</summary>
        [Description("姓名")]
        Name
    }

    /// <summary>
    /// 敏感字段脱敏特性(批 D 6.11):标注在 Output DTO 的字符串属性上,声明该字段的脱敏类型。
    /// WHY 用「标记 + 显式调用」而非序列化挂钩:批 D 探针实证 .NET 10 的 System.Text.Json 无按属性
    /// 取自定义特性的 converter 公共机制(AttributeProvider 类型不存在,JsonConverterFactory 仅无参构造)——
    /// 逐属性自动脱敏不可行,降级为「Output DTO 投影显式调 DataMaskHelper.Mask(value, type)」,
    /// 本特性作敏感字段标记(文档约定 + 未来框架挂钩点)。
    /// 当前系统实体无电话/身份证字段,机制供 Business 层敏感输出使用(机制就绪,消费随业务)。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class DataMaskAttribute : Attribute
    {
        /// <summary>脱敏类型。</summary>
        public MaskType Type { get; }

        public DataMaskAttribute(MaskType type)
        {
            Type = type;
        }
    }
}
