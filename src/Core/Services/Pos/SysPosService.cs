using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Pos.Dto;
using AdminCore.Core.Services.Import;
using AdminCore.Core.Models;
using AdminCore.Core.Models.Import;
using AdminCore.Core.Repository;
using AdminCore.Core.Utils.DynamicQuery;
using Microsoft.AspNetCore.Http;
using SqlSugar;

namespace AdminCore.Core.Services.Pos
{
    /// <summary>
    /// 职位管理(task_020 契约 §2):分页(Name/Code 模糊)/ 新建(Code 全表唯一)/ 更新(Code 唯一性排除自身)/ 删除(关联用户校验)。
    /// </summary>
    [Service]
    public class SysPosService
    {
        private readonly ISqlSugarRepository<SysPos> _posRepo;
        private readonly ISqlSugarRepository<SysUser> _userRepo;
        // 批 T 2.6:归属校验(非超管仅可操作自己创建的职位,参照 D6002 语义)
        private readonly UserManager _userManager;
        // 批 CP:通用导入服务(Parse 读文件 + Validate 纯函数校验)
        private readonly ImportService _importService;

        public SysPosService(ISqlSugarRepository<SysPos> posRepo, ISqlSugarRepository<SysUser> userRepo, UserManager userManager, ImportService importService)
        {
            _posRepo = posRepo;
            _userRepo = userRepo;
            _userManager = userManager;
            _importService = importService;
        }

        /// <summary>
        /// 批 T 2.6:非超管归属校验(参照 Admin.NET D6002 语义)——
        /// 已认证非超管且操作者非创建者 → 拒绝;匿名/无 Claims(测试基线)放行,与仓内 4 处既有范式同口径。
        /// </summary>
        private void CheckOwnership(long? createUserId)
        {
            if (_userManager.IsAuthenticated && _userManager.UserId is > 0 && !_userManager.IsSuperAdmin
                && createUserId != _userManager.UserId)
            {
                throw AppException.Of(ErrorCodes.PosCreatorOnly, args: null, code: 400);
            }
        }

        /// <summary>分页查询职位(Name/Code 模糊过滤,空即不过滤;默认 Id 倒序,与字典/角色等管理页同口径)。</summary>
        public async Task<PageResult<PosOutput>> PageAsync(PagePosInput input)
        {
            RefAsync<int> total = 0;
            var list = await _posRepo.AsQueryable()
                .Where(p => string.IsNullOrWhiteSpace(input.Name) || p.Name!.Contains(input.Name))
                .Where(p => string.IsNullOrWhiteSpace(input.Code) || p.Code!.Contains(input.Code))
                // 批 UX:状态精确过滤(int? Status,缺省 null=不过滤;0 正常/1 停用)
                .WhereIF(input.Status.HasValue, p => p.Status == input.Status)
                .ApplyDynamicSort(input.OrderBy, input.Desc, p => p.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<PosOutput>
            {
                Total = total,
                List = list.Select(p => new PosOutput(p.Id, p.Name, p.Code, p.Sort, p.Remark, p.Status)).ToList()
            };
        }

        /// <summary>新建职位;Code 全表唯一(服务层校验),重复 → AppException 业务码 400(与字典类型同口径)。</summary>
        public async Task<long> AddAsync(AddPosInput input)
        {
            var name = input.Name?.Trim();
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
            {
                throw AppException.Of(ErrorCodes.PosNameCodeRequired);
            }
            if (await _posRepo.IsAnyAsync(p => p.Code == code))
            {
                throw AppException.With(ErrorCodes.PosCodeExists, "code", code, 400);
            }
            var pos = new SysPos
            {
                Name = name,
                Code = code,
                Sort = input.Sort,
                Remark = input.Remark,
                Status = input.Status
            };
            await _posRepo.InsertAsync(pos);
            return pos.Id;
        }

        /// <summary>更新职位;Name/Code 非空校验 + Code 唯一性校验排除自身(变更 Code 时防与他行撞码)。</summary>
        public async Task UpdateAsync(UpdatePosInput input)
        {
            var pos = await _posRepo.GetFirstAsync(p => p.Id == input.Id);
            if (pos is null)
            {
                throw AppException.Of(ErrorCodes.PosNotFound);
            }
            CheckOwnership(pos.CreateUserId);
            var name = input.Name?.Trim();
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
            {
                throw AppException.Of(ErrorCodes.PosNameCodeRequired);
            }
            if (code != pos.Code && await _posRepo.IsAnyAsync(p => p.Code == code && p.Id != input.Id))
            {
                throw AppException.With(ErrorCodes.PosCodeExists, "code", code, 400);
            }
            pos.Name = name;
            pos.Code = code;
            pos.Sort = input.Sort;
            pos.Remark = input.Remark;
            pos.Status = input.Status;
            await _posRepo.UpdateAsync(pos);
        }

        /// <summary>
        /// 删除职位:存在关联用户(SysUser.PosId 指向本职位)时拒绝(业务码 400),否则软删(审计保留)。
        /// WHY 只拒绝不级联清用户 PosId:职位是用户属性锚点,误删会破坏用户↔职位引用;清用户 PosId 属用户域副作用,
        /// 越权且破坏审计链,契约 §2 仅要求"有关联用户时拒绝",故不在此清关联。
        /// WHY 校验用 IsAnyAsync(全局软删过滤器自动生效):已软删用户不计入关联,与其删除路径清关联口径一致。
        /// </summary>
        public async Task DeleteAsync(long id)
        {
            var pos = await _posRepo.GetFirstAsync(p => p.Id == id);
            if (pos is null)
            {
                throw AppException.Of(ErrorCodes.PosNotFound);
            }
            CheckOwnership(pos.CreateUserId);
            if (await _userRepo.IsAnyAsync(u => u.PosId == id))
            {
                throw AppException.With(ErrorCodes.PosHasUsers, "name", pos.Name, 400);
            }
            await _posRepo.DeleteByIdAsync(id);
        }

        /// <summary>
        /// 批 CP 通用导入(代表端点 sysPos/import):文件格式/空文件/超行限 → AppException(错误码三步);
        /// 行级错误 → **全回滚**(0 行入库,错误经 ImportReport 回传,HTTP 200 + code=0),不是异常。
        /// </summary>
        /// <remarks>
        /// 全回滚口径:先把库内 Code 查重集 → 纯函数 Validate(无 DB)→ 有错直接回 report(不写任何行);
        /// 无错才逐行插入,端点贴 <c>[UnitOfWork]</c> 包事务(中途异常整体回滚,不留半成品)。
        /// 新行 Status=0;雪花 Id/审计列由 EntityTenant 基类 AOP 自动填充(与 AddAsync 同口径)。
        /// 事务/仓储约束:只走 <c>ISqlSugarRepository&lt;SysPos&gt;</c>(接口不暴露 Context/Storageable),不写裸 Ado 事务。
        /// </remarks>
        public async Task<ImportReport> ImportAsync(IFormFile file)
        {
            if (file is null)
            {
                throw AppException.Of(ErrorCodes.ImportFileEmpty);
            }

            var extension = Path.GetExtension(file.FileName);
            if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                throw AppException.Of(ErrorCodes.ImportFileFormatInvalid);
            }

            if (file.Length <= 0)
            {
                throw AppException.Of(ErrorCodes.ImportFileEmpty);
            }

            await using var stream = file.OpenReadStream();
            var rows = _importService.Parse<ImportPosInput>(stream, extension);
            if (rows.Count == 0)
            {
                // 口径(批 CPΔ,C5b 改判 2026-10-25 批准):解析后零数据行(如仅表头文件)按空文件处理,
                // 不再回"0 行成功"的静默空报告;Validate 纯函数的空集合空报告行为不变。
                throw AppException.Of(ErrorCodes.ImportFileEmpty);
            }
            if (rows.Count > ImportService.MaxRows)
            {
                throw AppException.Of(ErrorCodes.ImportRowLimitExceeded);
            }

            // 库内既有编码(全局软删过滤器自动生效):与文件内重复同一判定集,见 ImportService.Validate。
            var existingCodes = (await _posRepo.AsQueryable().Select(p => p.Code).ToListAsync())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!.Trim())
                .ToHashSet(StringComparer.Ordinal);

            var errors = _importService.Validate(rows, existingCodes);
            if (errors.Count > 0)
            {
                return _importService.Build(rows.Count, 0, errors);
            }

            foreach (var row in rows)
            {
                var pos = new SysPos
                {
                    Name = row.Name!.Trim(),
                    Code = row.Code!.Trim(),
                    Sort = int.TryParse(row.Sort?.Trim(), out var sort) ? sort : 0,
                    Remark = string.IsNullOrWhiteSpace(row.Remark) ? null : row.Remark.Trim(),
                    Status = 0
                };
                await _posRepo.InsertAsync(pos);
            }

            return _importService.Build(rows.Count, rows.Count, errors);
        }
    }
}
