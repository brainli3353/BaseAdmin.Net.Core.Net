using System;

using System.ComponentModel.DataAnnotations;



namespace AdminCore.Core.Attributes;



/// <summary>

/// 身份证号校验(批 O 6.11,手册 6.11 点名自建件——参照仓未建,见 docs/研究/g9_refer_research.md §5 ④):

/// 18 位(正则语义:17 位数字 + 末位数字或 X/x,GB11643 加权校验位)或 15 位老证(纯数字,最小兼容,不做升位补全)。

/// null 放行(可空性由 [Required] 管)。

/// </summary>

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]

public sealed class IdCardNoAttribute : ValidationAttribute

{

    // GB11643 加权因子(第 1-17 位)

    private static readonly int[] Weights = { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };

    // 校验位映射(加权和 mod 11 → 校验字符)

    private static readonly char[] CheckCodes = { '1', '0', 'X', '9', '8', '7', '6', '5', '4', '3', '2' };



    public override string FormatErrorMessage(string name)

        => string.Format("字段 {0} 身份证号格式不正确", name);



    public override bool IsValid(object? value)

    {

        if (value is null)

        {

            return true;

        }



        var s = Convert.ToString(value)?.Trim();

        if (s is null)

        {

            return false;

        }



        // 15 位老证:纯数字最小兼容

        if (s.Length == 15)

        {

            return s.All(char.IsDigit);

        }



        if (s.Length != 18)

        {

            return false;

        }



        var allDigit = true;

        for (var i = 0; i < 17; i++)

        {

            if (!char.IsDigit(s[i]))

            {

                allDigit = false;

                break;

            }

        }

        if (!allDigit)

        {

            return false;

        }



        var last = char.ToUpperInvariant(s[17]);

        if (last != 'X' && !char.IsDigit(last))

        {

            return false;

        }



        var sum = 0;

        for (var i = 0; i < 17; i++)

        {

            sum += (s[i] - '0') * Weights[i];

        }



        return CheckCodes[sum % 11] == last;

    }

}

