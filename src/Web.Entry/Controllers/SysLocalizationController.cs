using AdminCore.Core.Attributes;
using AdminCore.Core.Localization;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 多语言(批 AL 6.6):只读暴露受支持语言与当前解析结果(前端/运维的单一事实源)。
    /// 文案资源在 Core/Resources/Lang.*.json;解析口径见 ILocalizationService。
    /// </summary>
    [ApiController]
    [Route("api/sysLocalization")]
    public class SysLocalizationController : ControllerBase
    {
        private readonly ILocalizationService _localization;

        public SysLocalizationController(ILocalizationService localization)
        {
            _localization = localization;
        }

        /// <summary>GET /api/sysLocalization/cultures -> {defaultCulture, supported[], current}。</summary>
        [MenuPerm("sysLocalization/cultures")]
        [HttpGet("cultures")]
        public IActionResult Cultures()
        {
            return Ok(ApiResult<object>.Success(new
            {
                defaultCulture = _localization.DefaultCulture,
                supported = _localization.SupportedCultures,
                current = _localization.CurrentCulture
            }));
        }
    }
}