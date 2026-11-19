using System.Text.Json;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.ColumnCustom.Dto;

namespace AdminCore.Core.Services.ColumnCustom
{
    /// <summary>
    /// 用户表格列配置服务(批 A 4.12,参照 Admin.NET SysColumnCustomService 移植):
    /// 用户 × 表格四类列状态(冻结/列宽/顺序/显隐)的存取。
    /// 身份取本人(UserManager.UserId Claim,自助口径——无 [MenuPerm],同 updatePassword 裁决);
    /// 缓存 7 天 + 存/重置精准失效(参照同口径);
    /// upsert 走仓储 GetFirst→Insert/Update 两步(本仓仓储契约无 Context/Storageable 直访,#11 不绕契约;
    /// 并发防重兜底=实体 (UserId,GridId) 唯一索引,参照无此索引——本仓加,裁决留痕)。
    /// </summary>
    [Service]
    public class SysColumnCustomService
    {
        /// <summary>列配置缓存 TTL(对齐权限缓存 7 天口径;精准失效为主,TTL 兜底)。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly ISqlSugarRepository<SysColumnCustom> _repo;
        private readonly ICacheService _cache;
        private readonly UserManager _userManager;

        public SysColumnCustomService(
            ISqlSugarRepository<SysColumnCustom> repo,
            ICacheService cache,
            UserManager userManager)
        {
            _repo = repo;
            _cache = cache;
            _userManager = userManager;
        }

        /// <summary>
        /// 取本人指定表格的列配置;无配置 → null(前端回退默认列态)。
        /// </summary>
        public async Task<ColumnCustomOutput?> GetDetailAsync(GetColumnCustomInput input)
        {
            var (userId, cacheKey) = RequireUser(input.GridId);
            var cached = await _cache.GetAsync<ColumnCustomOutput>(cacheKey);
            if (cached is not null)
            {
                return cached;
            }

            var row = await _repo.GetFirstAsync(e => e.UserId == userId && e.GridId == input.GridId);
            if (row == null)
            {
                return null;
            }

            var result = new ColumnCustomOutput
            {
                UserId = row.UserId,
                GridId = row.GridId,
                FixedData = ParseDict<string>(row.FixedData),
                ResizableData = ParseDict<int>(row.ResizableData),
                SortData = ParseDict<int>(row.SortData),
                VisibleData = ParseDict<bool>(row.VisibleData)
            };
            await _cache.SetAsync(cacheKey, result, CacheTtl);
            return result;
        }

        /// <summary>
        /// 保存本人指定表格的列配置(业务键 upsert,四类字典整体覆盖——设置态全量提交语义)。
        /// </summary>
        public async Task StoreAsync(StoreColumnCustomInput input)
        {
            var (userId, cacheKey) = RequireUser(input.GridId);

            var existing = await _repo.GetFirstAsync(e => e.UserId == userId && e.GridId == input.GridId);
            if (existing == null)
            {
                await _repo.InsertAsync(new SysColumnCustom
                {
                    UserId = userId,
                    GridId = input.GridId,
                    FixedData = Serialize(input.FixedData),
                    ResizableData = Serialize(input.ResizableData),
                    SortData = Serialize(input.SortData),
                    VisibleData = Serialize(input.VisibleData)
                });
            }
            else
            {
                // 增量更新:仅覆盖四类状态字段(#9;Id/租户/审计不动)
                existing.FixedData = Serialize(input.FixedData);
                existing.ResizableData = Serialize(input.ResizableData);
                existing.SortData = Serialize(input.SortData);
                existing.VisibleData = Serialize(input.VisibleData);
                await _repo.UpdateAsync(existing);
            }
            await _cache.RemoveAsync(cacheKey);
        }

        /// <summary>
        /// 重置(物理删)本人指定表格的列配置(仓储 DeletePhysicalByAsync 口径)。
        /// </summary>
        public async Task ResetAsync(GetColumnCustomInput input)
        {
            var (userId, cacheKey) = RequireUser(input.GridId);
            await _repo.DeletePhysicalByAsync(e => e.UserId == userId && e.GridId == input.GridId);
            await _cache.RemoveAsync(cacheKey);
        }

        /// <summary>
        /// 取当前登录用户 Id + 缓存键(gridId 空/身份缺 → 业务拒绝;自助端点身份单点)。
        /// </summary>
        private (long userId, string cacheKey) RequireUser(string gridId)
        {
            if (string.IsNullOrWhiteSpace(gridId))
            {
                throw AppException.Of(ErrorCodes.ColumnCustomGridIdRequired);
            }

            var userId = _userManager.UserId;
            if (userId == null)
            {
                throw AppException.Of(ErrorCodes.ColumnCustomUserUnresolved);
            }

            return (userId.Value, CacheConst.KeyColumnCustom + userId + ":" + gridId.Trim());
        }

        /// <summary>
        /// 字典字段 JSON 反序列化;空串/null → null;坏 JSON → 业务拒绝(配置数据损坏可检索)。
        /// </summary>
        public static Dictionary<string, T>? ParseDict<T>(string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, T>>(json, JsonOpts);
            }
            catch (JsonException ex)
            {
                throw AppException.With(ErrorCodes.ColumnCustomDataCorrupted, "detail", ex.Message);
            }
        }

        private static string? Serialize<T>(Dictionary<string, T>? dict)
        {
            return dict == null ? null : JsonSerializer.Serialize(dict, JsonOpts);
        }
    }
}
