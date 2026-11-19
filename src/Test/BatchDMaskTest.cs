using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Security;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 D 6.11 脱敏机制测试:DataMaskHelper 五类型算法 + 边界(null/空/长度不足原样)
/// + [DataMask] 标记可反射读取(投影约定依据)。
/// 路线留痕:探针曾验证 System.Text.Json「[JsonConverter] + AttributeProvider 构造器」按属性取
/// [DataMask] 的自动脱敏路径——.NET 10 公共 API 无 AttributeProvider 类型(全程序集扫描 0 命中),
/// 该路径不可行,正式件走「标记 + 显式投影」(见 DataMaskAttribute 注释)。
/// </summary>
public class BatchDMaskTest
{
    [Theory]
    [InlineData("13812345678", "138****5678")]   // 标准 11 位
    [InlineData("1381234567", "1381234567")]     // 10 位非法号 → 原样
    [InlineData(null, null)]                     // null 原样
    [InlineData("", "")]                         // 空串原样
    public void 手机号脱敏(string? input, string? expected)
    {
        Assert.Equal(expected, DataMaskHelper.Mask(input, MaskType.Phone));
    }

    [Theory]
    [InlineData("110101199001011234", "110101********1234")]  // 18 位
    [InlineData("110101900101123", "110101*****1123")]        // 15 位(中间掩 5:15-6-4)
    [InlineData("12345", "12345")]                             // 长度不足 → 原样
    public void 身份证脱敏(string? input, string? expected)
    {
        Assert.Equal(expected, DataMaskHelper.Mask(input, MaskType.IdCard));
    }

    [Theory]
    [InlineData("zhangsan@example.com", "z***@example.com")]
    [InlineData("a@example.com", "a@example.com")]     // 本地段 1 字符(首字符即全部) → 原样(不足口径)
    [InlineData("abc", "abc")]                          // 无 @ → 原样
    [InlineData("@example.com", "@example.com")]        // at<=1 → 原样
    public void 邮箱脱敏(string? input, string? expected)
    {
        Assert.Equal(expected, DataMaskHelper.Mask(input, MaskType.Email));
    }

    [Theory]
    [InlineData("6222020200112233445", "***************3445")]// 19 位卡号(掩 15:19-4)
    [InlineData("12345678", "****5678")]                      // 8 位下限
    [InlineData("1234567", "1234567")]                        // 7 位不足 → 原样
    public void 银行卡脱敏(string? input, string? expected)
    {
        Assert.Equal(expected, DataMaskHelper.Mask(input, MaskType.BankCard));
    }

    [Theory]
    [InlineData("张三丰", "张**")]
    [InlineData("张", "张")]        // 单字符 → 原样
    [InlineData("AB", "A*")]       // 多字符
    public void 姓名脱敏(string? input, string? expected)
    {
        Assert.Equal(expected, DataMaskHelper.Mask(input, MaskType.Name));
    }

    [Fact]
    public void DataMask标记_反射可读取()
    {
        // 投影约定依据:Output DTO 属性贴 [DataMask] 后,反射按属性取类型(框架/审计工具消费)
        var prop = typeof(MaskedDto).GetProperty("Phone")!;
        var attr = prop.GetCustomAttribute<DataMaskAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(MaskType.Phone, attr!.Type);
    }

    /// <summary>标记读取探针 DTO(测试专用)。</summary>
    private sealed class MaskedDto
    {
        [DataMask(MaskType.Phone)]
        public string? Phone { get; set; }
    }
}
