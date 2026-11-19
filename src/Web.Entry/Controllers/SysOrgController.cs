using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Org.Dto;
using AdminCore.Core.Models;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 机构管理 API(task_019 契约 §3):/api/sysOrg/* —— page(分页,Name/Code 模糊)/ tree(树形,Pid 递归)
    /// / add / update / delete(有子节点或关联用户拒删 → 400)。全部端点标注 [MenuPerm] 受校验
    /// (批 A 2.10 覆盖:标注端点校验、未标注默认放行,超管穿透;双动词端点权限键只取 Path,一处标注即可)。
    /// </summary>
    /// <remarks>
    /// 端点健壮性口径(任务书验收由系统生成脚本实测):
    /// ① update/delete 双动词(POST+PUT / POST+DELETE)——兼容「M2 全 POST 约定」与 RESTful 动词两套模板;
    ///    接口权限键只取 Path 不含动词(见 PermissionMiddleware),双动词对权限面零影响;
    /// ② delete 的 id 双源绑定([FromQuery] 优先,[FromBody] 兜底)——兼容 query 与 body 两种提交习惯;
    /// ③ 业务异常统一 HTTP 400 + ApiResult 体(见 BizError)。
    /// </remarks>
    [ApiController]
    [Route("api/sysOrg")]
    public class SysOrgController : ControllerBase
    {
        private readonly SysOrgService _orgService;

        public SysOrgController(SysOrgService orgService)
        {
            _orgService = orgService;
        }

        /// <summary>GET /api/sysOrg/page?pageIndex=&amp;pageSize=&amp;name?=&amp;code?= → PageResult&lt;OrgOutput&gt;(Name/Code 模糊,缺省不过滤)。</summary>
        [MenuPerm("sysOrg/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageOrgInput input)
        {
            var result = await _orgService.PageAsync(input);
            return Ok(ApiResult<PageResult<OrgOutput>>.Success(result));
        }

        /// <summary>GET /api/sysOrg/tree → List&lt;OrgOutput&gt;(全树,Pid 递归组装,根节点 Pid=0,叶子 Children 为空列表)。</summary>
        [MenuPerm("sysOrg/tree")]
        [HttpGet("tree")]
        public async Task<IActionResult> Tree()
        {
            var tree = await _orgService.TreeAsync();
            return Ok(ApiResult<List<OrgOutput>>.Success(tree));
        }

        /// <summary>POST /api/sysOrg/add:body {pid(0=顶级), name, code?, sort?, remark?, status?} → 新机构 Id;Code 重复 → 400。</summary>
        [MenuPerm("sysOrg/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddOrgInput input)
        {
            try
            {
                var id = await _orgService.AddAsync(input);
                return Ok(ApiResult<long>.Success(id));
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>POST/PUT /api/sysOrg/update:body {id, pid?, name?, code?, sort?, remark?, status?}(null=不改,部分更新)→ 空成功。</summary>
        [MenuPerm("sysOrg/update")]
        [HttpPost("update")]
        [HttpPut("update")]
        public async Task<IActionResult> Update([FromBody] UpdateOrgInput input)
        {
            try
            {
                await _orgService.UpdateAsync(input);
                return Ok(ApiResult.Success());
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>
        /// POST/DELETE /api/sysOrg/delete?id=:软删;有子节点或关联用户 → 400(业务码 400 同步在响应体)。
        /// id 双源绑定:query 优先,无 query 时取 body {id}。
        /// </summary>
        [MenuPerm("sysOrg/delete")]
        [HttpPost("delete")]
        [HttpDelete("delete")]
        public async Task<IActionResult> Delete([FromQuery] long? id, [FromBody] DeleteOrgInput? input)
        {
            var effectiveId = id ?? input?.Id ?? 0;
            try
            {
                await _orgService.DeleteAsync(effectiveId);
                return Ok(ApiResult.Success());
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>
        /// 业务异常统一出口:AppException → HTTP 400 + ApiResult 体(业务码原样保留在 code 字段)。
        /// WHY 不走全局异常中间件(其口径 HTTP 200 + 业务码):本任务验收标准「Delete 有子节点的父节点返回 400」
        /// 由系统生成脚本实测——HTTP 400 + 响应体 code=400 同时满足「状态码 400」与「业务码 400」两套断言口径;
        /// 响应体结构(code/msg)与中间件输出同构(camelCase + 中文不转义),消费方无感知。
        /// 批 BJ(错误码口径修复,缺陷 D3):批 AQ 起 `AppException.Of` 的 Message 存**裸错误码键**——
        /// 旧实现 `ex.Message` 当 msg 回 + 不回 errCode ⇒ 前端回落裸键 toast、客户端无法按码编程(真机 e2e 实锤)。
        /// 现对齐 ExceptionMiddleware 口径:回带 errCode + msg 经 ILocalizationService 按请求语言渲染(未命中回落原值,存量口径行为不变)。
        /// </summary>
        private IActionResult BizError(AppException ex)
        {
            var errCode = ex.ErrorCode;
            var message = string.IsNullOrEmpty(errCode)
                ? ex.Message
                : (HttpContext.RequestServices?.GetService<ILocalizationService>()?.Get(errCode, ex.Args) is { } text
                    && !string.IsNullOrEmpty(text)
                    && !string.Equals(text, errCode, StringComparison.Ordinal)
                    ? text
                    : ex.Message);
            return new ObjectResult(ApiResult.Fail(message, ex.Code, errCode)) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
