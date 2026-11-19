using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Services.Dict.Dto;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的;与 Program.cs 同口径)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.Dict
{
    /// <summary>
    /// 字典值服务(task_011 契约 §2 后半):分页/新建/更新/删除/更新状态 + 缓存读取入口 GetDataListByIdOrCodeAsync。
    /// 缓存契约:键 sys_dict:{类型 Code};读只取「双 Enable」(类型启用 且 值启用);
    /// 全部写路径(增/改/删/状态)主动失效该键,变更立即生效、不依赖过期。
    /// </summary>
    [Service]
    public class SysDictDataService
    {
        private readonly ISqlSugarRepository<SysDictData> _dataRepo;
        private readonly ISqlSugarRepository<SysDictType> _typeRepo;
        private readonly ICacheService _cache;

        public SysDictDataService(ISqlSugarRepository<SysDictData> dataRepo, ISqlSugarRepository<SysDictType> typeRepo, ICacheService cache)
        {
            _dataRepo = dataRepo;
            _typeRepo = typeRepo;
            _cache = cache;
        }

        /// <summary>
        /// 缓存读取入口(契约 §2,对应参考手册 GetDataListByIdOrCode 伪码):
        /// 按字典类型 Id 或 Code 取字典值列表——命中缓存直返;未命中则类型 LEFT JOIN 值单条 SQL 查出
        /// (跨租户 ClearFilter,软删过滤自动生效,只取双 Enable),按 OrderNo 升序后落缓存。
        /// WHY 跨租户:字典是全局参考数据,种子值落默认租户,业务租户读的是同一份,租户过滤会读空。
        /// WHY 统一落 Code 键:Id 入口先解析出类型取其 Code,两条入口共享同一缓存键,失效只需删一个键。
        /// WHY code 可空:HTTP 查询参数可缺省(只传 typeId 时 code 为 null),契约伪码 string code 为简写;
        /// 入参组合 null+null 由下方空值守卫拦截,不会落入 SQL 比较。
        /// </summary>
        public async Task<List<DictDataOutput>> GetDataListByIdOrCodeAsync(long? typeId, string? code)
        {
            if (typeId is null && string.IsNullOrWhiteSpace(code))
            {
                throw AppException.Of(ErrorCodes.DictTypeIdCodeRequired);
            }
            var type = await _typeRepo.AsQueryable()
                .ClearFilter<ITenantIdFilter>()
                .Where(t => typeId.HasValue ? t.Id == typeId.Value : t.Code == code)
                .FirstAsync();
            // 类型不存在/停用 → 空集且不落缓存:避免负向缓存钉死(类型后建/再启用后读回旧空集)
            if (type is null || !type.IsEnable)
            {
                return new List<DictDataOutput>();
            }

            var key = $"{CacheConst.KeyDict}{type.Code}";
            var cached = await _cache.GetAsync<List<DictDataOutput>>(key);
            if (cached is not null)
            {
                return cached;
            }

            // 未命中:类型 LEFT JOIN 值(导航属性声明的同一关系,此处显式 join 单条 SQL)
            var list = await _dataRepo.AsQueryable()
                .LeftJoin<SysDictType>((d, t) => d.DictTypeId == t.Id)
                .ClearFilter<ITenantIdFilter>()
                .Where((d, t) => t.Id == type.Id && t.IsEnable && d.IsEnable)
                .OrderBy(d => d.OrderNo)
                .Select(d => d)
                .ToListAsync();
            var outputs = list.Select(d => new DictDataOutput(
                d.Id, d.DictTypeId, d.Code, d.Value, d.Label, d.TagType, d.StyleSetting,
                d.ClassSetting, d.OrderNo, d.IsEnum, d.IsEnable, d.ExtData, d.Remark)).ToList();
            // 无 TTL(传 null):字典缓存只靠写路径主动失效,不靠过期兜底(契约 §2)
            await _cache.SetAsync(key, outputs);
            return outputs;
        }

        /// <summary>分页查询字典值(管理页;可选按字典类型过滤,默认 OrderNo 升序、同序 Id 倒序)。</summary>
        public async Task<PageResult<DictDataOutput>> PageAsync(PageDictDataInput input)
        {
            RefAsync<int> total = 0;
            // 批 UX:Keyword 先 Trim(空白视同不过滤),Label/Value 模糊 OR
            var keyword = input.Keyword?.Trim();
            var query = _dataRepo.AsQueryable();
            if (input.DictTypeId is not null)
            {
                query = query.Where(d => d.DictTypeId == input.DictTypeId.Value);
            }
            query = query.WhereIF(!string.IsNullOrWhiteSpace(keyword), d => d.Label!.Contains(keyword!) || d.Value!.Contains(keyword!));
            // 批 CI:表头点击排序(orderBy 空=原默认序 OrderNo asc+Id desc;非空=白名单校验+表达式树排序)
            var list = await query
                .ApplyDynamicSort(input.OrderBy, input.Desc, q => q.OrderBy(d => d.OrderNo).OrderBy(d => d.Id, OrderByType.Desc))
                .ToPageListAsync(input, total);
            return new PageResult<DictDataOutput>
            {
                Total = total,
                List = list.Select(d => new DictDataOutput(
                    d.Id, d.DictTypeId, d.Code, d.Value, d.Label, d.TagType, d.StyleSetting,
                    d.ClassSetting, d.OrderNo, d.IsEnum, d.IsEnable, d.ExtData, d.Remark)).ToList()
            };
        }

        /// <summary>新建字典值;Code 同类型下唯一(服务层校验),重复 → AppException 业务码 400(对齐验收)。成功后主动失效所属类型缓存。</summary>
        public async Task<long> AddAsync(AddDictDataInput input)
        {
            var type = await _typeRepo.GetFirstAsync(t => t.Id == input.DictTypeId);
            if (type is null)
            {
                throw AppException.Of(ErrorCodes.DictTypeNotFound);
            }
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(input.Value) || string.IsNullOrWhiteSpace(input.Label))
            {
                throw AppException.Of(ErrorCodes.DictDataFieldsRequired);
            }
            if (await _dataRepo.IsAnyAsync(d => d.DictTypeId == input.DictTypeId && d.Code == code))
            {
                throw AppException.Of(ErrorCodes.DictTypeCodeExistsUnder, new Dictionary<string, object?> { ["type"] = type.Code, ["code"] = code }, 400);
            }
            var data = new SysDictData
            {
                DictTypeId = input.DictTypeId,
                Code = code,
                Value = input.Value,
                Label = input.Label,
                TagType = input.TagType,
                StyleSetting = input.StyleSetting,
                ClassSetting = input.ClassSetting,
                OrderNo = input.OrderNo,
                IsEnum = input.IsEnum,
                ExtData = input.ExtData,
                Remark = input.Remark
            };
            await _dataRepo.InsertAsync(data);
            // 写路径主动失效:变更后立即可读新值(契约 §2)
            await _cache.RemoveAsync($"{CacheConst.KeyDict}{type.Code}");
            return data.Id;
        }

        /// <summary>更新字典值;DictTypeId 不可变更(跨类型迁移走删+建)。Code 变更仍须满足同类型唯一(排除自身)。成功后主动失效所属类型缓存。</summary>
        public async Task UpdateAsync(UpdateDictDataInput input)
        {
            var data = await _dataRepo.GetFirstAsync(d => d.Id == input.Id);
            if (data is null)
            {
                throw AppException.Of(ErrorCodes.DictDataNotFound);
            }
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(input.Value) || string.IsNullOrWhiteSpace(input.Label))
            {
                throw AppException.Of(ErrorCodes.DictDataFieldsRequired);
            }
            if (await _dataRepo.IsAnyAsync(d => d.DictTypeId == data.DictTypeId && d.Code == code && d.Id != input.Id))
            {
                throw AppException.With(ErrorCodes.DictDataCodeExists, "code", code, 400);
            }
            data.Code = code;
            data.Value = input.Value;
            data.Label = input.Label;
            data.TagType = input.TagType;
            data.StyleSetting = input.StyleSetting;
            data.ClassSetting = input.ClassSetting;
            data.OrderNo = input.OrderNo;
            data.IsEnum = input.IsEnum;
            data.ExtData = input.ExtData;
            data.Remark = input.Remark;
            await _dataRepo.UpdateAsync(data);
            // 写路径主动失效:验收实测「改值后 list 立即返回新值」依赖本步
            await InvalidateTypeCacheAsync(data.DictTypeId);
        }

        /// <summary>删除字典值(软删,项目软删口径:行保留、读路径经 IDeletedFilter 不可见)。成功后主动失效所属类型缓存。</summary>
        public async Task DeleteAsync(long id)
        {
            var data = await _dataRepo.GetFirstAsync(d => d.Id == id);
            if (data is null)
            {
                throw AppException.Of(ErrorCodes.DictDataNotFound);
            }
            await _dataRepo.DeleteByIdAsync(id);
            await InvalidateTypeCacheAsync(data.DictTypeId);
        }

        /// <summary>更新字典值启用状态(「双 Enable」的值侧);停用后该值对缓存读取立即不可见(主动失效,不依赖过期)。</summary>
        public async Task UpdateStatusAsync(UpdateDictDataStatusInput input)
        {
            var data = await _dataRepo.GetFirstAsync(d => d.Id == input.Id);
            if (data is null)
            {
                throw AppException.Of(ErrorCodes.DictDataNotFound);
            }
            data.IsEnable = input.IsEnable;
            await _dataRepo.UpdateAsync(data);
            await InvalidateTypeCacheAsync(data.DictTypeId);
        }

        /// <summary>写路径缓存主动失效:解析值所属字典类型取其 Code,删 sys_dict:{Code} 键(唯一缓存键维度,跨租户共享)。</summary>
        private async Task InvalidateTypeCacheAsync(long dictTypeId)
        {
            var type = await _typeRepo.AsQueryable()
                .ClearFilter<ITenantIdFilter>()
                .FirstAsync(t => t.Id == dictTypeId);
            if (type is not null)
            {
                await _cache.RemoveAsync($"{CacheConst.KeyDict}{type.Code}");
            }
        }
    }
}
