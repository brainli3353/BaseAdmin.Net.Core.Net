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
    /// 字典类型服务(task_011 契约 §2 前半):分页/新建(Code 全局唯一)/更新(Code 不可变)/删除。
    /// 删除为级联删:先物理清子表 SysDictData,再软删类型,最后主动失效该类型字典缓存。
    /// 批 BO 复审:D1 Code 唯一性口径=**全局跨租户**(判重 ClearFilter&lt;ITenantIdFilter&gt;)——
    /// 读路径 GetDataListByIdOrCodeAsync 跨租户 ClearFilter、缓存键 sys_dict:{Code} 无租户维,
    /// 字典语义是「全局参考数据」(种子值落默认租户、业务租户读同一份,契约 §2);
    /// 原实现走租户过滤后查询(租户用户视角),Code 命名空间实际按租户隔离 ⇒ 他租户可建同 Code 类型,
    /// 跨租户读 FirstAsync 命中任意行 + 缓存跨租户串读。判重收紧为全局后,命名空间与读/缓存语义一致。
    /// </summary>
    [Service]
    public class SysDictTypeService
    {
        private readonly ISqlSugarRepository<SysDictType> _typeRepo;
        private readonly ISqlSugarRepository<SysDictData> _dataRepo;
        private readonly ICacheService _cache;

        public SysDictTypeService(ISqlSugarRepository<SysDictType> typeRepo, ISqlSugarRepository<SysDictData> dataRepo, ICacheService cache)
        {
            _typeRepo = typeRepo;
            _dataRepo = dataRepo;
            _cache = cache;
        }

        /// <summary>分页查询字典类型(默认 Id 倒序,与角色等管理页同口径)。</summary>
        public async Task<PageResult<DictTypeOutput>> PageAsync(PageDictTypeInput input)
        {
            // 批 UX:Keyword 先 Trim(空白视同不过滤),Name/Code 模糊 OR
            var keyword = input.Keyword?.Trim();
            RefAsync<int> total = 0;
            var list = await _typeRepo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(keyword), t => t.Name!.Contains(keyword!) || t.Code!.Contains(keyword!))
                .ApplyDynamicSort(input.OrderBy, input.Desc, t => t.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);
            return new PageResult<DictTypeOutput>
            {
                Total = total,
                List = list.Select(t => new DictTypeOutput(t.Id, t.Name, t.Code, t.IsEnum, t.IsEnable, t.Remark)).ToList()
            };
        }

        /// <summary>
        /// 新建字典类型;Code **全局唯一**(服务层校验,跨租户 ClearFilter——与读路径跨租户/缓存键无租户维一致,
        /// 批 BO D1;软删行经 IDeletedFilter 排除,删后同 Code 重建放行)。重复 → AppException 业务码 400(对齐验收「重复 Code → 400」)。
        /// </summary>
        public async Task<long> AddAsync(AddDictTypeInput input)
        {
            var name = input.Name?.Trim();
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
            {
                throw AppException.Of(ErrorCodes.DictTypeNameCodeRequired);
            }
            // 全局判重:租户过滤视角(租户用户请求)下他租户同 Code 行不可见 ⇒ 须 ClearFilter(类头批 BO D1 口径)
            if (await _typeRepo.AsQueryable()
                    .ClearFilter<ITenantIdFilter>()
                    .AnyAsync(t => t.Code == code))
            {
                throw AppException.With(ErrorCodes.DictTypeCodeExists, "code", code, 400);
            }
            var type = new SysDictType
            {
                Name = name,
                Code = code,
                IsEnum = input.IsEnum,
                Remark = input.Remark
            };
            await _typeRepo.InsertAsync(type);
            return type.Id;
        }

        /// <summary>更新字典类型;Code 不可变更(缓存键与业务引用锚定 Code)。类型属性变更会改变缓存读取语义,须主动失效缓存。</summary>
        public async Task UpdateAsync(UpdateDictTypeInput input)
        {
            var type = await _typeRepo.GetFirstAsync(t => t.Id == input.Id);
            if (type is null)
            {
                throw AppException.Of(ErrorCodes.DictTypeNotFound);
            }
            var name = input.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw AppException.Of(ErrorCodes.DictTypeNameRequired);
            }
            type.Name = name;
            type.IsEnum = input.IsEnum;
            type.Remark = input.Remark;
            await _typeRepo.UpdateAsync(type);
            // 写路径主动失效:变更立即生效、不依赖过期(契约 §2),键 = sys_dict:{Code}
            await _cache.RemoveAsync($"{CacheConst.KeyDict}{type.Code}");
        }

        /// <summary>
        /// 级联删除字典类型:先物理删该类型下全部字典值,再软删类型,最后失效缓存。
        /// WHY 子表在前:若先软删类型、子表删除失败,会留下对消费方可见的孤儿字典值;
        /// 子表在前部分失败仅留「空类型」(可补值恢复),且类型软删行对读路径不可见。
        /// WHY 子表物理删:字典值无独立审计价值(审计单元是类型),M2 中间表清理同口径;
        /// 且软删行会被 IDeletedFilter 挡出后续读取,物理清更干净,验收 psql 验证 count 归零也稳过。
        /// 批 D 3.2 事务闭环:端点 sysDictType/delete 已贴 [UnitOfWork](级联删全量替换在事务内,
        /// 中途失败整体回滚不留「空类型」半成品);顺序仍保持子表在前(事务内顺序语义不变,见上)。
        /// </summary>
        public async Task DeleteAsync(long id)
        {
            var type = await _typeRepo.GetFirstAsync(t => t.Id == id);
            if (type is null)
            {
                throw AppException.Of(ErrorCodes.DictTypeNotFound);
            }
            await _dataRepo.DeletePhysicalByAsync(d => d.DictTypeId == id);
            await _typeRepo.DeleteByIdAsync(id);
            await _cache.RemoveAsync($"{CacheConst.KeyDict}{type.Code}");
        }
    }
}
