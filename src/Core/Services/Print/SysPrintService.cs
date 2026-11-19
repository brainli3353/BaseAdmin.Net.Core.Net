using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Print.Dto;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

namespace AdminCore.Core.Services.Print
{
    /// <summary>
    /// 打印模板服务(批 AF 4.6):模板 CRUD(名称唯一)+ 复制。
    /// </summary>
    /// <remarks>
    /// **范围裁决:只存模板不渲染**——本仓后端零渲染、零导出(与参照仓后端真实形态一致:参照仓 6 个方法同样无渲染,
    /// 渲染 100% 在前端 hiprint 设计器 + 外部 electron 客户端)。故本仓**不引入** hiprint/print-js/jsplumb 三件前端重依赖,
    /// 也不移植参照仓的 `ClientServiceAddress`(客户端打印服务地址,其消费方在参照仓内未找到实现)/`PrintDataDemo`(预览数据)/
    /// `print/{name}` 端点(参照前端零消费方)。渲染能力按需另立批(登记见差距清单)。
    /// 口径要点:① 新增/更新走**部分更新**(约定 #9),不复用参照的 `IgnoreColumns(true)` 整对象覆盖;
    /// ② 入参用独立 Dto(参照用实体做入参 ⇒ 请求体可携带 TenantId/审计字段);
    /// ③ 名称唯一性:应用层同名拒绝 + 库层唯一索引(`i_sys_print_name`)双保险(参照仅普通索引,并发可穿透);
    /// ④ 列表不载 Template 大字段(详情端点取全文)。
    /// </remarks>
    [Service]
    public class SysPrintService
    {
        private readonly ISqlSugarRepository<SysPrint> _printRepo;

        public SysPrintService(ISqlSugarRepository<SysPrint> printRepo)
        {
            _printRepo = printRepo;
        }

        /// <summary>
        /// 分页查询(Name 模糊;OrderNo 升序、Id 降序稳定)。**列表在库层就不选 Template 大字段**
        /// (批 AF 修正 P3:此前取整实体再内存投影,SQL 实选列仍含 Template,"不载大字段"只在响应层成立;
        /// 投影下推到 SqlSugar 查询侧 ⇒ 大字段不进内存、不进网络)。
        /// </summary>
        public async Task<PageResult<SysPrintOutput>> PageAsync(PageSysPrintInput input)
        {
            var name = input.Name?.Trim();
            RefAsync<int> total = 0;

            // 投影用可写属性 class DTO:SqlSugar 查询侧投影需可映射类型(不映射 positional record,批 E 实证)
            var list = await _printRepo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(name), p => p.Name.Contains(name!))
                // 批 UX B1d:CreateTime 范围(闭区间口径,与同批 sysFile/既有 log 域一致)
                .WhereIF(input.StartDate.HasValue, p => p.CreateTime >= input.StartDate)
                .WhereIF(input.EndDate.HasValue, p => p.CreateTime <= input.EndDate)
                // 批 CI:表头点击排序(orderBy 空=原默认序 OrderNo asc+Id desc;非空=白名单校验+表达式树排序)
                .ApplyDynamicSort(input.OrderBy, input.Desc, q => q.OrderBy(p => p.OrderNo).OrderByDescending(p => p.Id))
                .Select(p => new SysPrintOutput
                {
                    Id = p.Id,
                    Name = p.Name,
                    PrintType = (int)p.PrintType,
                    Status = p.Status,
                    OrderNo = p.OrderNo,
                    Remark = p.Remark,
                    CreateTime = p.CreateTime
                })
                .ToPageListAsync(input, total);

            return new PageResult<SysPrintOutput> { Total = total, List = list };
        }

        /// <summary>模板详情(全字段,含 Template 全文);不存在 → 业务异常。</summary>
        public async Task<SysPrintDetailOutput> GetDetailAsync(long id)
        {
            var print = await _printRepo.GetFirstAsync(p => p.Id == id);
            if (print is null)
            {
                throw AppException.Of(ErrorCodes.PrintNotFound);
            }

            return new SysPrintDetailOutput(print.Id, print.Name, print.Template, (int)print.PrintType,
                print.Status, print.OrderNo, print.Remark, print.CreateTime);
        }

        /// <summary>新增模板:名称/模板内容必填校验 → 同名拒绝(应用层;库层唯一索引兜底)→ 插入 → 返回新 Id。</summary>
        public async Task<long> AddAsync(AddSysPrintInput input)
        {
            var name = input.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw AppException.Of(ErrorCodes.PrintNameRequired);
            }

            if (string.IsNullOrWhiteSpace(input.Template))
            {
                throw AppException.Of(ErrorCodes.PrintContentRequired);
            }

            await EnsureNameUniqueAsync(name, excludeId: null);

            var print = new SysPrint
            {
                Name = name,
                Template = input.Template!,
                PrintType = input.PrintType is 2 ? Enums.PrintTypeEnum.Client : Enums.PrintTypeEnum.Browser,
                Status = input.Status ?? 1,
                OrderNo = input.OrderNo ?? 100,
                Remark = input.Remark
            };
            await _printRepo.InsertAsync(print);
            return print.Id;
        }

        /// <summary>
        /// 更新模板(**部分更新:null=不改**):名称非空时先做同名拒绝;内容/类型/状态/排序/备注按需覆盖。
        /// </summary>
        public async Task UpdateAsync(UpdateSysPrintInput input)
        {
            var print = await _printRepo.GetFirstAsync(p => p.Id == input.Id);
            if (print is null)
            {
                throw AppException.Of(ErrorCodes.PrintNotFound);
            }

            if (input.Name is not null)
            {
                var name = input.Name.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw AppException.Of(ErrorCodes.PrintNameRequired);
                }

                await EnsureNameUniqueAsync(name, excludeId: input.Id);
                print.Name = name;
            }

            if (input.Template is not null)
            {
                print.Template = input.Template;
            }

            if (input.PrintType is not null)
            {
                print.PrintType = input.PrintType is 2 ? Enums.PrintTypeEnum.Client : Enums.PrintTypeEnum.Browser;
            }

            if (input.Status is not null)
            {
                print.Status = input.Status.Value;
            }

            if (input.OrderNo is not null)
            {
                print.OrderNo = input.OrderNo.Value;
            }

            if (input.Remark is not null)
            {
                print.Remark = input.Remark;
            }

            await _printRepo.UpdateAsync(print);
        }

        /// <summary>
        /// 复制模板:源不存在 → 拒绝;新行名称为 <c>原名 - copy(N)</c>(N 从 1 起探测首个不冲突者),
        /// Id 由雪花重新生成(不沿用源 Id——参照仓 Copy 把 Id 置 0 依赖自增,本仓雪花口径必须显式新建行)。
        /// </summary>
        public async Task<long> CopyAsync(long id)
        {
            var source = await _printRepo.GetFirstAsync(p => p.Id == id);
            if (source is null)
            {
                throw AppException.Of(ErrorCodes.PrintNotFound);
            }

            var copyName = await BuildCopyNameAsync(source.Name);
            var copy = new SysPrint
            {
                Name = copyName,
                Template = source.Template,
                PrintType = source.PrintType,
                Status = source.Status,
                OrderNo = source.OrderNo,
                Remark = source.Remark
            };
            await _printRepo.InsertAsync(copy);
            return copy.Id;
        }

        /// <summary>删除模板(软删;存在性校验防误报成功)。</summary>
        public async Task DeleteAsync(long id)
        {
            var print = await _printRepo.GetFirstAsync(p => p.Id == id);
            if (print is null)
            {
                throw AppException.Of(ErrorCodes.PrintNotFound);
            }

            await _printRepo.DeleteByIdAsync(id);
        }

        /// <summary>
        /// 同名拒绝(排除自身 Id;应用层友好提示,库层唯一索引是最终防线)。
        /// 批 AF 修正(P2):查重须**忽略软删过滤器**——否则软删同名行"看不见",应用层放行而 INSERT 撞库层唯一索引,
        /// 抛 PostgresException(500)而非业务码(实测:删模板后同名重建必 500)。
        /// </summary>
        private async Task EnsureNameUniqueAsync(string name, long? excludeId)
        {
            var exists = await _printRepo.AsQueryable()
                .ClearFilter<IDeletedFilter>()
                .AnyAsync(p => p.Name == name && (excludeId == null || p.Id != excludeId));
            if (exists)
            {
                throw AppException.With(ErrorCodes.PrintNameExists, "name", name, 400);
            }
        }

        /// <summary>构造副本名称 <c>原名 - copy(N)</c>(N 自 1 递增至首个不冲突;上限 100 后回落时间戳后缀)。</summary>
        private async Task<string> BuildCopyNameAsync(string sourceName)
        {
            for (var i = 1; i <= 100; i++)
            {
                var candidate = $"{sourceName} - copy({i})";
                // 同 EnsureNameUniqueAsync:软删行也占用唯一索引 ⇒ 副本命名必须看得见软删行
                if (!await _printRepo.AsQueryable().ClearFilter<IDeletedFilter>().AnyAsync(p => p.Name == candidate))
                {
                    return candidate;
                }
            }

            return $"{sourceName} - copy({DateTime.Now:yyyyMMddHHmmss})";
        }
    }
}