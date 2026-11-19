using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Utils.DynamicQuery;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers;

/// <summary>
/// 演示控制器：验证统一响应格式与全局异常中间件生效（task_006 验收用，后续可删）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/demo")]
public class DemoController : Controller
{
    /// <summary>成功示例：返回统一 ApiResult（Code=0 + 数据）</summary>
    [HttpGet("success")]
    public ActionResult<ApiResult<string>> Success()
    {
        return ApiResult<string>.Success("你好，葫芦爷 Admin 统一响应！");
    }

    /// <summary>业务异常示例：抛 AppException，由中间件转成统一响应（含中文错误消息）</summary>
    [HttpGet("error")]
    public IActionResult Error()
    {
        throw AppException.Of(ErrorCodes.CommonDemoForbidden, args: null, code: 1001);
    }

    /// <summary>未捕获异常示例：除零触发 DivideByZeroException，中间件返回 Code=500 通用文案（不泄露堆栈）</summary>
    [HttpGet("crash")]
    public IActionResult Crash()
    {
        int a = 1;
        int b = 0;
        // 故意除零：b 是局部变量，编译器不做常量折叠，运行期才抛异常
        int c = a / b;
        return Ok(ApiResult.Success($"1/0 = {c}"));
    }

    /// <summary>
    /// 模型校验示例(批 O 6.11)：非法 body(缺 Name/Age 越界)→ 自动模型校验失败 →
    /// InvalidModelStateResponseFactory 抛 AppException → 全局异常中间件统一出口
    /// (HTTP 200 + ApiResult.Fail(msg, 400)——与 403/429 业务码出口同型,见 docs/研究/g9_refer_research.md)。
    /// 合法 body → 回显 Data(验证校验通过路径)。
    /// </summary>
    [HttpPost("validate")]
    public ActionResult<ApiResult<DemoValidateInput>> Validate([FromBody] DemoValidateInput input)
    {
        return ApiResult<DemoValidateInput>.Success(input, $"校验通过:{input.Name}/{input.Age}");
    }

    /// <summary>
    /// 动态查询演示(批 AG 3.11):排序/搜索**字段名经实体元数据白名单校验**,关键字走参数化 LIKE——
    /// 非法字段名(含注入串)一律业务异常,不拼 SQL 片段。
    /// 口径:登录即可(类级 `[Authorize]`)——匿名无 TenantId Claim 会使租户过滤器不装配、可跨租户翻 SysUser,
    /// 故收口为登录态;保持演示用途,不挂菜单/权限键。
    /// 示例:GET /api/demo/dynamicQuery?searchBy=account,realName&amp;keyword=admin&amp;orderBy=id&amp;desc=true
    /// </summary>
    [HttpGet("dynamicQuery")]
    public async Task<IActionResult> DynamicQuery(
        [FromServices] AdminCore.Core.Repository.ISqlSugarRepository<AdminCore.Core.Entities.SysUser> userRepo,
        [FromQuery] string? searchBy = null,
        [FromQuery] string? orderBy = null,
        [FromQuery] bool desc = true,
        [FromQuery] string? keyword = null,
        [FromQuery] int pageSize = 10)
    {
        SqlSugar.RefAsync<int> total = 0;
        var list = await userRepo.AsQueryable()
            .ApplyDynamic(new AdminCore.Core.Utils.DynamicQuery.DynamicQueryInput(searchBy, orderBy, desc), keyword)
            .Select(u => new { u.Id, u.Account, u.RealName, u.Status })
            .ToPageListAsync(1, Math.Clamp(pageSize, 1, 50), total);

        // WHY 显式转 int:RefAsync<int> 是 SqlSugar 的分页计数包装类型,直接放进匿名对象会被序列化成 {"value":N}
        // (e2e 实证抓出),消费方期望的是数字——显式取值。
        return Ok(ApiResult<object>.Success(new { total = (int)total, list }));
    }


}

/// <summary>
/// 演示校验 DTO(批 O 6.11)：展示 [Required]/[MinValue]/[MaxValue] 校验特性的使用范式(见框架使用指南 §13.6)。
/// .NET 10 实证坑:record 主构造位置参数的校验特性必须挂在**构造参数**上(直接写,不带 [property:] 前缀)——
/// 只挂属性([property: X])会抛 InvalidOperationException("validation metadata ... will be ignored,
/// must be associated with the constructor parameter",e2e 实证);挂参数则 record 自动双关联参数+属性。
/// </summary>
public sealed record DemoValidateInput(
    [Required(ErrorMessage = "Name 必填")] string Name,
    [MinValue(0)] [MaxValue(150)] int Age);
