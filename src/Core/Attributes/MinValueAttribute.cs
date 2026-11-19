using System;
using System.ComponentModel.DataAnnotations;

namespace AdminCore.Core.Attributes;

/// <summary>
/// 最小值校验(批 O 6.11,参照 Admin.NET MinValueAttribute 原文移植):null 放行,
/// 其余值经 Convert.ToDouble 与下限比较。挂 DTO 属性使用;400 出口同 MaxValueAttribute(见 docs/研究/g9_refer_research.md)。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MinValueAttribute : ValidationAttribute
{
    private readonly double _minValue;

    public MinValueAttribute(double minValue) => _minValue = minValue;

    public override string FormatErrorMessage(string name)
        => string.Format("字段 {0} 必须大于等于 {1}", name, _minValue);

    public override bool IsValid(object? value)
        => value is null || Convert.ToDouble(value) >= _minValue;
}
