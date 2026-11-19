using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AdminCore.Core.Models;
using AdminCore.Core.Option;

namespace AdminCore.Web.Entry.Controllers;

/// <summary>
/// 站点身份(批 AY,G24):运行期品牌/标题来源。
/// 前端在挂载前拉一次并覆盖品牌与浏览器标题——同一份前端产物部署到多套服务只靠配置就能区分
/// (构建期 Vite 变量降为兜底,不再要求重新构建产物)。
/// </summary>
[ApiController]
[Route("api/app")]
public class AppController : ControllerBase
{
    private readonly IOptions<AppOptions> _app;

    public AppController(IOptions<AppOptions> app)
    {
        _app = app;
    }

    /// <summary>
    /// GET /api/app/info -&gt; {name, fullName, titleSuffix}。
    /// 匿名只读:登录页渲染前就要用,故 <c>[AllowAnonymous]</c>(不含任何敏感信息);
    /// 取 <c>IOptions&lt;AppOptions&gt;</c> 的**已回落**值(FullName 空已回落 Name)。
    /// titleSuffix 可空(null 表示未配置)→ 前端按构建期兜底。
    /// </summary>
    [AllowAnonymous]
    [HttpGet("info")]
    public IActionResult Info()
    {
        var app = _app.Value;
        return Ok(ApiResult<object>.Success(new
        {
            name = app.Name,
            fullName = app.FullName,
            titleSuffix = app.TitleSuffix
        }));
    }
}