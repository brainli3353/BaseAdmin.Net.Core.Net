using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Dict;
using AdminCore.Core.Services.Dict.Dto;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 数据字典 API(task_011 契约 §3):类型管理 /api/sysDictType/* + 值管理 /api/sysDictData/*,
    /// 含缓存读取端点 list(命中缓存直返,未命中 LEFT JOIN 单查)。管理端点标注 [MenuPerm] 受校验
    /// (批 A 2.10 覆盖:标注端点校验、未标注默认放行,超管穿透);list 为通用读取端点,登录即可不贴具体权限键。
    /// WHY 动词对齐验收口径:POST add / GET page+list / PUT update(+updateStatus) / DELETE delete
    /// (M2 全 POST 约定让位于任务书验收脚本的显式动词;任务书交付面仅此文件,故两类端点合一控制器)。
    /// </summary>
    [ApiController]
    [Route("api")]
    public class SysDictController : ControllerBase
    {
        private readonly SysDictTypeService _typeService;
        private readonly SysDictDataService _dataService;

        public SysDictController(SysDictTypeService typeService, SysDictDataService dataService)
        {
            _typeService = typeService;
            _dataService = dataService;
        }

        /// <summary>GET /api/sysDictType/page?pageIndex=&amp;pageSize= → PageResult&lt;DictTypeOutput&gt;。</summary>
        [HttpGet("sysDictType/page")]
        [MenuPerm("sysDictType/page")]
        public async Task<IActionResult> PageType([FromQuery] PageDictTypeInput input)
        {
            var result = await _typeService.PageAsync(input);
            return Ok(ApiResult<PageResult<DictTypeOutput>>.Success(result));
        }

        /// <summary>POST /api/sysDictType/add:body {name, code, isEnum?, remark?} → 新类型 Id;重复 Code → 业务码 400。</summary>
        [HttpPost("sysDictType/add")]
        [MenuPerm("sysDictType/add")]
        public async Task<IActionResult> AddType([FromBody] AddDictTypeInput input)
        {
            var id = await _typeService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>PUT /api/sysDictType/update:body {id, name, isEnum?, remark?}(Code 不可变)→ 空成功。</summary>
        [HttpPut("sysDictType/update")]
        [MenuPerm("sysDictType/update")]
        public async Task<IActionResult> UpdateType([FromBody] UpdateDictTypeInput input)
        {
            await _typeService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>DELETE /api/sysDictType/delete?id=:级联删(子表物理清 + 类型软删 + 缓存失效)→ 空成功。</summary>
        [UnitOfWork]
        [HttpDelete("sysDictType/delete")]
        [MenuPerm("sysDictType/delete")]
        public async Task<IActionResult> DeleteType([FromQuery] long id)
        {
            await _typeService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>GET /api/sysDictData/page?pageIndex=&amp;pageSize=&amp;dictTypeId?= → PageResult&lt;DictDataOutput&gt;。</summary>
        [HttpGet("sysDictData/page")]
        [MenuPerm("sysDictData/page")]
        public async Task<IActionResult> PageData([FromQuery] PageDictDataInput input)
        {
            var result = await _dataService.PageAsync(input);
            return Ok(ApiResult<PageResult<DictDataOutput>>.Success(result));
        }

        /// <summary>GET /api/sysDictData/list?code= 或 ?typeId= → List&lt;DictDataOutput&gt;(缓存读取入口,只取双 Enable,OrderNo 升序)。</summary>
        [HttpGet("sysDictData/list")]
        public async Task<IActionResult> ListData([FromQuery] long? typeId, [FromQuery] string? code)
        {
            var result = await _dataService.GetDataListByIdOrCodeAsync(typeId, code);
            return Ok(ApiResult<List<DictDataOutput>>.Success(result));
        }

        /// <summary>POST /api/sysDictData/add:body {dictTypeId, code, value, label, ...} → 新值 Id;同类型重复 Code → 业务码 400。</summary>
        [HttpPost("sysDictData/add")]
        [MenuPerm("sysDictData/add")]
        public async Task<IActionResult> AddData([FromBody] AddDictDataInput input)
        {
            var id = await _dataService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>PUT /api/sysDictData/update:body {id, code, value, label, ...}(DictTypeId 不可变)→ 空成功。</summary>
        [HttpPut("sysDictData/update")]
        [MenuPerm("sysDictData/update")]
        public async Task<IActionResult> UpdateData([FromBody] UpdateDictDataInput input)
        {
            await _dataService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>DELETE /api/sysDictData/delete?id=:软删 + 缓存失效 → 空成功。</summary>
        [HttpDelete("sysDictData/delete")]
        [MenuPerm("sysDictData/delete")]
        public async Task<IActionResult> DeleteData([FromQuery] long id)
        {
            await _dataService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>PUT /api/sysDictData/updateStatus:body {id, isEnable} → 空成功(停用后 list 立即不再返回该值)。</summary>
        [HttpPut("sysDictData/updateStatus")]
        [MenuPerm("sysDictData/updateStatus")]
        public async Task<IActionResult> UpdateDataStatus([FromBody] UpdateDictDataStatusInput input)
        {
            await _dataService.UpdateStatusAsync(input);
            return Ok(ApiResult.Success());
        }
    }
}
