using AdminCore.Business;
using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Utils.ApiDoc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 接口文档 API(批 AG 6.7,批 BE 加分区):`GET /api/apiDoc/openapi` 返回反射自建的 OpenAPI 3.0 最小文档(JSON)。
    /// 由零依赖生成器 <see cref="ApiDocGenerator"/> 扫描 Web.Entry(框架)＋ Business(业务)两程序集产出,
    /// 每操作带 <c>x-doc-zone</c> 供前端分「框架文档」「业务接口」两区。
    /// </summary>
    /// <remarks>
    /// 开关:`ApiDoc:Enabled`(缺省 **true**;false=端点直接返回业务异常)。
    /// WHY 缺省开:手册 6.7 的硬性要求是「**生产关闭**」——开发/测试环境需要文档,生产在部署配置里置 false 即可;
    /// 若缺省 false,则"没配=看不到文档",与手册"开发价值高"的意图相反。
    /// 安全:端点标注 <c>[MenuPerm("apiDoc/openapi")]</c>——文档含全部端点与权限键(信息面较大),不裸奔。
    /// </remarks>
    [ApiController]
    [Route("api/apiDoc")]
    public class ApiDocController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public ApiDocController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// GET /api/apiDoc/openapi → OpenAPI 3.0.1 文档(JSON;含 paths/components.schemas,标注 [MenuPerm] 的操作带
        /// security 与 x-perm)。批 BE 起同时扫描 Web.Entry(框架)＋ Business(业务)两程序集,每操作带
        /// <c>x-doc-zone</c>(framework/business),前端据此分「框架文档」「业务接口」两区。
        /// </summary>
        [MenuPerm("apiDoc/openapi")]
        [HttpGet("openapi")]
        public IActionResult Openapi()
        {
            if (!_configuration.GetValue("ApiDoc:Enabled", true))
            {
                // 关闭属部署决策(生产硬性):明确拒绝,不返回空文档误导
                throw AppException.Of(ErrorCodes.CommonApiDocDisabled, args: null, code: 403);
            }

            // 批 BE:业务程序集锚点复用 Program.cs 既有口径(typeof(Class1).Assembly);框架=Web.Entry(本控制器程序集)。
            var businessAssembly = typeof(Class1).Assembly;
            var document = ApiDocGenerator.Generate(
                new[] { typeof(ApiDocController).Assembly, businessAssembly },
                "BaseAdminCore API",
                "1.0",
                businessAssembly);

            return Ok(ApiResult<Dictionary<string, object?>>.Success(document));
        }
    }
}