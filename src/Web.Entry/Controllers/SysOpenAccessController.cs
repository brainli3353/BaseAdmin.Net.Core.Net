using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.OpenAccess;
using AdminCore.Core.Services.OpenAccess.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 开放接口身份管理(批 E 1.6/1.7/6.10):建号/改绑/删除/重置密钥/分页/签名预览。
    /// 全部端点标注 [MenuPerm] 受按钮权限校验(超管穿透);AccessSecret 仅 Add/ResetSecret 响应一次性返回。
    /// </summary>
    [ApiController]
    [Route("api/sysOpenAccess")]
    public class SysOpenAccessController : Controller
    {
        private readonly SysOpenAccessService _service;

        public SysOpenAccessController(SysOpenAccessService service)
        {
            _service = service;
        }

        /// <summary>分页(AccessKey 模糊;不含密钥)。</summary>
        [MenuPerm("sysOpenAccess/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageOpenAccessInput input)
        {
            return Ok(ApiResult<PageResult<OpenAccessOutput>>.Success(await _service.PageAsync(input)));
        }

        /// <summary>建号(AccessKey 唯一;AccessSecret 自动生成,仅本次响应返回)。</summary>
        [MenuPerm("sysOpenAccess/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddOpenAccessInput input)
        {
            var (output, secret) = await _service.AddAsync(input);
            return Ok(ApiResult<AddOpenAccessResult>.Success(new AddOpenAccessResult(output, secret)));
        }

        /// <summary>改绑(仅绑定用户/租户;AccessKey/AccessSecret 不可改)。</summary>
        [MenuPerm("sysOpenAccess/update")]
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateOpenAccessInput input)
        {
            await _service.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>删除(软删)。</summary>
        [MenuPerm("sysOpenAccess/delete")]
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _service.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>重置密钥(旧密钥即时失效;新密钥仅本次响应返回)。</summary>
        [MenuPerm("sysOpenAccess/resetSecret")]
        [HttpPost("resetSecret")]
        public async Task<IActionResult> ResetSecret([FromQuery] long id)
        {
            return Ok(ApiResult<string>.Success(await _service.ResetSecretAsync(id), "密钥已重置,仅本次返回"));
        }

        /// <summary>签名预览(管理端调试:按四头口径算 sign,与 Handler 验签同算法)。</summary>
        [MenuPerm("sysOpenAccess/generateSignature")]
        [HttpPost("generateSignature")]
        public async Task<IActionResult> GenerateSignature([FromBody] GenerateSignatureInput input)
        {
            return Ok(ApiResult<GenerateSignatureOutput>.Success(await _service.GenerateSignatureAsync(input)));
        }
    }

    /// <summary>建号响应:身份详情 + 仅本次返回的 AccessSecret(调用方须妥善保存,分页不回显)。</summary>
    public sealed record AddOpenAccessResult(OpenAccessOutput Data, string AccessSecret);
}
