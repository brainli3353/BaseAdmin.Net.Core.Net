using System;
using System.ComponentModel.DataAnnotations;

namespace AdminCore.Core.Attributes;

/// <summary>
/// 最大值校验(批 O 6.11,参照 Admin.NET MaxValueAttribute 原文移植):null 放行(可空性由 [Required] 管),
/// 其余值经 Convert.ToDouble 与上限比较。挂 DTO 属性使用;400 出口见 Program.cs InvalidModelStateResponseFactory
/// (HTTP 200 + ApiResult.Fail(msg, 400),与 ExceptionMiddleware 同契约,见 docs/研究/g9_refer_research.md)。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MaxValueAttribute : ValidationAttribute
{
    private readonly double _maxValue;

    public MaxValueAttribute(double maxValue) => _maxValue = maxValue;

    public override string FormatErrorMessage(string name)
        => string.Format("字段 {0} 必须小于等于 {1}", name, _maxValue);

    public override bool IsValid(object? value)
        => value is null || Convert.ToDouble(value) <= _maxValue;
}
