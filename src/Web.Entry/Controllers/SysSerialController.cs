using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Serial;
using AdminCore.Core.Services.Serial.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 流水号管理 API(批 W 4.3/3.4):/api/sysSerial/*,薄层(绑定 → Service → ApiResult)。
    /// 全部端点标注 [MenuPerm](批 A 2.10 口径);动词对齐 CRUD:GET page/detail/typeList/slotList、
    /// POST add、PUT update、DELETE delete、POST setStatus/nextSeqNo/globalNextSeqNo/preview。
    /// </summary>
    [ApiController]
    [Route("api/sysSerial")]
    public class SysSerialController : ControllerBase
    {
        private readonly SysSerialService _serialService;

        public SysSerialController(SysSerialService serialService)
        {
            _serialService = serialService;
        }

        /// <summary>GET /api/sysSerial/page → PageResult&lt;PageSerialOutput&gt;(Type/Status 精确,Keyword 模糊)。</summary>
        [MenuPerm("sysSerial/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageSerialInput input)
        {
            var result = await _serialService.PageAsync(input);
            return Ok(ApiResult<PageResult<PageSerialOutput>>.Success(result));
        }

        /// <summary>GET /api/sysSerial/detail?id= → SysSerial 全字段;不存在 404。</summary>
        [MenuPerm("sysSerial/detail")]
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] long id)
        {
            var result = await _serialService.DetailAsync(id);
            return Ok(ApiResult<SysSerial>.Success(result));
        }

        /// <summary>POST /api/sysSerial/add → 新配置行 Id;Type 未注册/同租户已建 → 400。</summary>
        [MenuPerm("sysSerial/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddSerialInput input)
        {
            var id = await _serialService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>PUT /api/sysSerial/update → 空成功(只改配置列;Seq/Expy 运行时状态不可改)。</summary>
        [MenuPerm("sysSerial/update")]
        [HttpPut("update")]
        public async Task<IActionResult> Update([FromBody] UpdateSerialInput input)
        {
            await _serialService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>DELETE /api/sysSerial/delete?id= → 空成功(物理删;序列状态随配置作废)。</summary>
        [MenuPerm("sysSerial/delete")]
        [HttpDelete("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _serialService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysSerial/setStatus?id=&amp;status=(0 启用/1 停用)→ 空成功;停用后取号拒绝。</summary>
        [MenuPerm("sysSerial/setStatus")]
        [HttpPost("setStatus")]
        public async Task<IActionResult> SetStatus([FromQuery] long id, [FromQuery] int status)
        {
            await _serialService.SetStatusAsync(id, status);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysSerial/nextSeqNo {type} → 本租户流水号字符串(分布式锁 + 周期重置 + 上限 + 插槽渲染)。</summary>
        [MenuPerm("sysSerial/nextSeqNo")]
        [HttpPost("nextSeqNo")]
        public async Task<IActionResult> NextSeqNo([FromBody] GetNextSeqInput input)
        {
            var result = await _serialService.NextSeqNoAsync(input);
            return Ok(ApiResult<string>.Success(result));
        }

        /// <summary>POST /api/sysSerial/globalNextSeqNo {type} → 全局流水号(跨租户共享默认租户行)。</summary>
        [MenuPerm("sysSerial/globalNextSeqNo")]
        [HttpPost("globalNextSeqNo")]
        public async Task<IActionResult> GlobalNextSeqNo([FromBody] GetNextSeqInput input)
        {
            var result = await _serialService.GlobalNextSeqNoAsync(input);
            return Ok(ApiResult<string>.Success(result));
        }

        /// <summary>POST /api/sysSerial/preview {formater, seq, max} → 渲染结果(不消耗序列)。</summary>
        [MenuPerm("sysSerial/preview")]
        [HttpPost("preview")]
        public async Task<IActionResult> Preview([FromBody] PreviewSerialInput input)
        {
            var result = _serialService.PreviewAsync(input);
            return Ok(ApiResult<string>.Success(result));
        }

        /// <summary>GET /api/sysSerial/typeList → 已注册类型列表(text=显示名,value=类型标识)。</summary>
        [MenuPerm("sysSerial/typeList")]
        [HttpGet("typeList")]
        public IActionResult TypeList()
        {
            var result = _serialService.GetTypeList();
            return Ok(ApiResult<List<KeyValuePair<string, string>>>.Success(result));
        }

        /// <summary>GET /api/sysSerial/slotList → 可用插槽列表(模板提示用)。</summary>
        [MenuPerm("sysSerial/slotList")]
        [HttpGet("slotList")]
        public IActionResult SlotList()
        {
            var result = _serialService.GetSlotList();
            return Ok(ApiResult<List<string>>.Success(result));
        }
    }
}
