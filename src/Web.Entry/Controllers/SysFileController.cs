using AdminCore.Core.Attributes;
using AdminCore.Core.Services.File;
using AdminCore.Core.Services.File.Dto;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 文件管理接口（参考手册 §4.5 契约）：分页清单 / 上传(multipart) / 下载(md5 校验) / 删除 / 业务关联差集同步，路由 /api/sysFile/*。
    /// delete/associate/page 标注 [MenuPerm] 受校验（批 A 2.10 覆盖：标注端点校验、未标注默认放行，超管穿透）；
    /// upload 为通用上传端点（业务表单均可用），登录即可不贴具体权限键；
    /// download 为 [AllowAnonymous]——文件 Url 存库作直链（img/link 场景无法携带 Token），
    /// 安全兜底：雪花 Id 不可枚举 + md5 校验防篡改（校验失败/文件不存在均 400）。
    /// </summary>
    [ApiController]
    [Route("api/sysFile")]
    public class SysFileController : ControllerBase
    {
        private readonly SysFileService _fileService;

        public SysFileController(SysFileService fileService)
        {
            _fileService = fileService;
        }

        /// <summary>POST /api/sysFile/upload（multipart/form-data：file 必填，dataId 可选业务关联）→ 文件记录（含 fileMd5/url；同文件重传返回已有记录）。</summary>
        [HttpPost("upload")]
        public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] long? dataId)
        {
            var output = await _fileService.UploadAsync(file, dataId);
            return Ok(ApiResult<SysFileOutput>.Success(output));
        }

        /// <summary>GET /api/sysFile/page?fileName=&amp;pageIndex=&amp;pageSize=&amp;startDate=&amp;endDate= → PageResult&lt;SysFilePageOutput&gt;（FileName 模糊 + 创建时间范围，按创建时间倒序）。</summary>
        [MenuPerm("sysFile/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] FilePageInput input)
        {
            var result = await _fileService.PageAsync(input);
            return Ok(ApiResult<PageResult<SysFilePageOutput>>.Success(result));
        }

        /// <summary>GET /api/sysFile/download?id=&amp;md5= → 文件字节流（Content-Type 按后缀映射，附件下载；md5 不符/文件不存在 → 400）。</summary>
        [HttpGet("download")]
        [AllowAnonymous]
        public async Task<IActionResult> Download([FromQuery] long id, [FromQuery] string? md5)
        {
            var result = await _fileService.DownloadAsync(id, md5);
            return File(result.Content, result.ContentType, result.FileName);
        }

        /// <summary>POST /api/sysFile/delete?id= → 软删记录+内容清理（幂等）。</summary>
        [HttpPost("delete")]
        [MenuPerm("sysFile/delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _fileService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>
        /// POST /api/sysFile/associate（body {dataId, fileIds}）→ 业务关联差集同步：删多余+回填 DataId。
        /// 契约外薄端点（task_012 披露）：UpdateFileByDataId 为服务方法，无端点则验收标准"curl 触发+psql 验证"不可测。
        /// </summary>
        [HttpPost("associate")]
        [MenuPerm("sysFile/associate")]
        public async Task<IActionResult> Associate([FromBody] UpdateFileInput input)
        {
            await _fileService.UpdateFileByDataIdAsync(input.DataId, input.FileIds);
            return Ok(ApiResult.Success());
        }
    }
}
