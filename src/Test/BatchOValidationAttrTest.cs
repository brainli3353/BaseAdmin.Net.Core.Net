using AdminCore.Core.Attributes;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 O(6.11 校验类特性)测试(纯函数,无 DB):
/// MaxValue/MinValue(参照 Admin.NET 原文移植口径:null 放行/边界/超限)/IdCardNo(18 位 GB11643 校验位/15 位老证/拒绝形态)。
/// 400 统一出口(HTTP 200 + ApiResult.Fail(msg, 400) 经 ExceptionMiddleware)走 e2e 验证(真实 app 非法 body)。
/// </summary>
public class BatchOValidationAttrTest
{
    [Fact]
    public void MaxValue_null放行() => Assert.True(new MaxValueAttribute(10).IsValid(null));

    [Fact]
    public void MaxValue_边界等值通过() => Assert.True(new MaxValueAttribute(10).IsValid(10));

    [Fact]
    public void MaxValue_上限内通过() => Assert.True(new MaxValueAttribute(10).IsValid(3.5));

    [Fact]
    public void MaxValue_超限拒绝() => Assert.False(new MaxValueAttribute(10).IsValid(10.1));

    [Fact]
    public void MaxValue_字符串数字可转() => Assert.True(new MaxValueAttribute(10).IsValid("9"));

    [Fact]
    public void MinValue_null放行() => Assert.True(new MinValueAttribute(0).IsValid(null));

    [Fact]
    public void MinValue_边界等值通过() => Assert.True(new MinValueAttribute(0).IsValid(0));

    [Fact]
    public void MinValue_下限内拒绝() => Assert.False(new MinValueAttribute(0).IsValid(-0.1));

    [Fact]
    public void MinValue_下限上通过() => Assert.True(new MinValueAttribute(0).IsValid(1));

    [Fact]
    public void IdCardNo_null放行() => Assert.True(new IdCardNoAttribute().IsValid(null));

    [Fact]
    public void IdCardNo_合法18位_校验位通过()
    {
        // 11010519491231002X:校验位 X(加权 sum=58? 实际以算法自洽为准——用已知合法样例)
        // 构造算法自洽样例:前 17 位 11010519491231002,校验位 = CheckCodes[sum%11]
        Assert.True(new IdCardNoAttribute().IsValid("11010519491231002X"));
        Assert.True(new IdCardNoAttribute().IsValid("11010519491231002x"));
    }

    [Fact]
    public void IdCardNo_校验位错误拒绝() => Assert.False(new IdCardNoAttribute().IsValid("110105194912310021"));

    [Fact]
    public void IdCardNo_合法18位_数字校验位()
    {
        // 53010219200508011X 是流传合法样例;此处用算法自洽:11010519491231002 的校验位是 X,
        // 换前 17 位 11010519491231001 → 校验位另行计算
        var attr = new IdCardNoAttribute();
        Assert.True(attr.IsValid("110105194912310011"), "11010519491231001+1 应自洽合法");
    }

    [Fact]
    public void IdCardNo_15位老证_纯数字通过() => Assert.True(new IdCardNoAttribute().IsValid("110105491231002"));

    [Fact]
    public void IdCardNo_15位含X拒绝() => Assert.False(new IdCardNoAttribute().IsValid("11010549123100X"));

    [Fact]
    public void IdCardNo_长度错误拒绝()
    {
        var attr = new IdCardNoAttribute();
        Assert.False(attr.IsValid("11010519491231002")); // 17 位
        Assert.False(attr.IsValid("11010519491231002X1")); // 19 位
    }

    [Fact]
    public void IdCardNo_前17位含字母拒绝() => Assert.False(new IdCardNoAttribute().IsValid("11010519491231A02X"));

    [Fact]
    public void IdCardNo_空白串拒绝() => Assert.False(new IdCardNoAttribute().IsValid("   "));
}
