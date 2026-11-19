using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Models;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Serial.Dto;
using AdminCore.Core.Utils.DynamicQuery;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AdminCore.Core.Services.Serial
{
    /// <summary>
    /// 系统流水号服务(批 W 4.3/3.4,参照 AdminNetPro SysSerialService 移植):
    /// 序列状态机(CRUD)+ 取号(NextSeqNoAsync,分布式锁 + 周期重置 + 上限校验 + 插槽格式化)。
    /// </summary>
    /// <remarks>
    /// 取号并发口径(参照 BeginCacheLock 移植,原语对齐本仓 Idempotent 先例 #11):
    /// ICacheService.IncrementAsync 原子自增得 1 = 持锁;>1 = 他者持锁 → 短间隔重试(缺省 5 × 100ms),
    /// 耗尽 → 429「系统繁忙,请稍后再试」。锁 TTL 5s 仅防进程异常后的锁残留,不是执行超时控制。
    /// 取号落库 = 单语句 UPDATE(SET Seq=新值 WHERE Id=配置行)——SqlSugar 单语句天然原子,
    /// 无多语句一致性需求故不开事务(参照用事务因其多语句路径,本仓最小化)。
    /// 全局流水号(isGlobal)= 默认租户行 + ClearFilter 跨租户读写(参照 IgnoreTenant 同语义)。
    /// 与参照差异(裁决,批末呈报):① 无 Furion——App.EffectiveTypes 反射改 Core 程序集标准反射;
    /// ② 状态列 int 0/1(本仓口径)非 StatusEnum;③ {OrgType} 插槽裁剪(本仓无机构类型概念);
    /// ④ 锁原语 IncrementAsync 重试(本仓无 SetNx/BeginCacheLock,复用 Idempotent 同族原语);
    /// ⑤ 单语句无事务;⑥ 服务经 [Service] Transient 注册(AOP 消费点经 IServiceProvider 解析)。
    /// </remarks>
    // 缺省 Scoped(AOP 消费点经 CreateScope 解析,见 SqlSugarSetup FillBindSerial 分支 WHY)。
    [Service]
    public class SysSerialService
    {
        private readonly SqlSugarRepository<SysSerial> _rep;
        private readonly AdminCore.Core.Cache.ICacheService _cache;
        private readonly UserManager _userManager;

        /// <summary>
        /// 插槽逻辑模板缓存(进程级,参照 _slotTempMap 同构):首取反射扫 ISerialSlotProvider,
        /// 之后零反射。值 = 参数化逻辑(绑定当前取号参数前),每次取号再生成轻量闭包映射。
        /// </summary>
        private static readonly ConcurrentDictionary<string, Dictionary<string, Func<long, long, DateTime, UserManager, string>>> SlotLogicMap = new();

        /// <summary>注册类型列表缓存(进程级):{text=显示名, value=类型标识}。</summary>
        private static List<KeyValuePair<string, string>>? _typeList;

        public SysSerialService(SqlSugarRepository<SysSerial> rep, AdminCore.Core.Cache.ICacheService cache, UserManager userManager)
        {
            _rep = rep;
            _cache = cache;
            _userManager = userManager;
        }

        // ── 管理端 CRUD ──────────────────────────────────────────────────────

        /// <summary>分页查询(Type 精确/Status 过滤,Type 或 Formater 关键字模糊)。</summary>
        public async Task<PageResult<PageSerialOutput>> PageAsync(PageSerialInput input)
        {
            RefAsync<int> total = 0;
            var type = input.Type;
            var status = input.Status;
            // 批 BW:空白判据对齐全站搜索口径(IsNullOrWhiteSpace,SysConfig/SysMenu/CodeGen/SysOpenAccess 同;
            // 参照为 Trim()+!=null 同语义)——全空白/空 keyword 视为无过滤
            var keyword = string.IsNullOrWhiteSpace(input.Keyword) ? null : input.Keyword;
            var list = await _rep.AsQueryable()
                .WhereIF(type != null, u => u.Type == type)
                .WhereIF(status.HasValue, u => u.Status == status!.Value)
                .WhereIF(keyword != null, u => u.Type!.Contains(keyword!) || u.Formater!.Contains(keyword!))
                // 批 UX B1d:CreateTime 范围(闭区间口径,与同批 sysFile/既有 log 域一致)
                .WhereIF(input.StartDate.HasValue, u => u.CreateTime >= input.StartDate)
                .WhereIF(input.EndDate.HasValue, u => u.CreateTime <= input.EndDate)
                .ApplyDynamicSort(input.OrderBy, input.Desc, u => u.OrderNo, OrderByType.Asc)
                .ToPageListAsync(input, total);
            return new PageResult<PageSerialOutput>
            {
                Total = total,
                List = list.Select(u => new PageSerialOutput(u.Id, u.Seq, u.Expy, u.Type, u.ResetInterval, u.Formater, u.Min, u.OrderNo, u.Max, u.Status, u.Remark)).ToList()
            };
        }

        /// <summary>详情(不存在 → 404)。</summary>
        public async Task<SysSerial> DetailAsync(long id)
        {
            return await _rep.GetFirstAsync(u => u.Id == id) ?? throw AppException.Of(ErrorCodes.SerialConfigNotFound, args: null, code: 404);
        }

        /// <summary>新增:Type 须 ∈ 注册集且同租户未建;Seq 初值 1、Expy = 当前时间(首个周期锚点)。</summary>
        public async Task<long> AddAsync(AddSerialInput input)
        {
            var typeList = GetTypeList();
            if (typeList.All(u => u.Value != input.Type))
            {
                throw AppException.With(ErrorCodes.SerialTypeUnregistered, "type", input.Type, 400);
            }

            // 批 BW:判重显式限定本租户(对齐参照 `u.TenantId == _userManager.TenantId`)——
            // 超管豁免租户过滤走全表判重时,他租户已有同 Type 会误拦本(默认)租户建号
            var tenantId = _userManager.TenantId ?? SqlSugarConst.DefaultTenantId;
            if (await _rep.IsAnyAsync(u => u.Type == input.Type && u.TenantId == tenantId))
            {
                throw AppException.With(ErrorCodes.SerialTypeExists, "type", input.Type, 400);
            }

            var entity = new SysSerial
            {
                Type = input.Type,
                ResetInterval = input.ResetInterval,
                Formater = input.Formater,
                Min = input.Min,
                Max = input.Max,
                OrderNo = input.OrderNo,
                Status = input.Status,
                Remark = input.Remark,
                Seq = 1,
                Expy = DateTime.Now
            };
            await _rep.InsertAsync(entity);
            return entity.Id;
        }

        /// <summary>更新:只改配置列(Seq/Expy 运行时状态显式忽略);变更 Type 撞码 → 400。</summary>
        public async Task UpdateAsync(UpdateSerialInput input)
        {
            var entity = await _rep.GetFirstAsync(u => u.Id == input.Id) ?? throw AppException.Of(ErrorCodes.SerialConfigNotFound, args: null, code: 404);
            // 批 BW:同 Add 口径——判重显式限定本租户
            var tenantId = _userManager.TenantId ?? SqlSugarConst.DefaultTenantId;
            if (input.Type != entity.Type
                && await _rep.IsAnyAsync(u => u.Type == input.Type && u.TenantId == tenantId && u.Id != input.Id))
            {
                throw AppException.With(ErrorCodes.SerialTypeExists, "type", input.Type, 400);
            }

            entity.Type = input.Type;
            entity.ResetInterval = input.ResetInterval;
            entity.Formater = input.Formater;
            entity.Min = input.Min;
            entity.Max = input.Max;
            entity.OrderNo = input.OrderNo;
            entity.Status = input.Status;
            entity.Remark = input.Remark;
            await _rep.AsUpdateable(entity)
                .UpdateColumns(u => new
                {
                    u.Type, u.ResetInterval, u.Formater, u.Min, u.Max, u.OrderNo, u.Status, u.Remark
                })
                .ExecuteCommandAsync();
        }

        /// <summary>删除(物理;序列状态随配置作废,无关联表)。</summary>
        public async Task DeleteAsync(long id)
        {
            var entity = await _rep.GetFirstAsync(u => u.Id == id) ?? throw AppException.Of(ErrorCodes.SerialConfigNotFound, args: null, code: 404);
            await _rep.DeleteAsync(entity);
        }

        /// <summary>启停(0=启用 1=停用;停用后 NextSeqNo 拒绝)。</summary>
        public async Task SetStatusAsync(long id, int status)
        {
            if (status != 0 && status != 1)
            {
                throw AppException.Of(ErrorCodes.SerialStatusInvalid, args: null, code: 400);
            }

            await _rep.AsUpdateable().SetColumns(u => u.Status == status).Where(u => u.Id == id).ExecuteCommandAsync();
        }

        // ── 取号 ────────────────────────────────────────────────────────────

        /// <summary>取本租户流水号(管理端显式取号端点;isGlobal=false)。</summary>
        public Task<string> NextSeqNoAsync(GetNextSeqInput input)
        {
            return NextSeqNoAsync(input.Type, false);
        }

        /// <summary>取全局流水号(跨租户共享默认租户行)。</summary>
        public Task<string> GlobalNextSeqNoAsync(GetNextSeqInput input)
        {
            return NextSeqNoAsync(input.Type, true);
        }

        /// <summary>
        /// 预览(不消耗序列):按 Formater + 指定 Seq/Max + 当前时间渲染,供管理端配模板时即时看效果。
        /// </summary>
        public string PreviewAsync(PreviewSerialInput input)
        {
            var seq = new SysSerial
            {
                Formater = input.Formater,
                Seq = input.Seq,
                Max = input.Max
            };
            return FormatSeqNo(seq, DateTime.Now);
        }

        /// <summary>获取插槽列表(模板提示用)。</summary>
        public List<string> GetSlotList()
        {
            return GetSlotLogicMap().Keys.ToList();
        }

        /// <summary>获取已注册流水号类型列表(缓存;重复注册 → 启动期即抛防漂移)。</summary>
        public List<KeyValuePair<string, string>> GetTypeList()
        {
            if (_typeList != null)
            {
                return _typeList;
            }

            var types = typeof(SysSerialService).Assembly.GetTypes()
                .Where(u => !u.IsInterface && u.IsClass && u.GetInterfaces().Any(i => i == typeof(ISerialTypeProvider)))
                .SelectMany(u => u.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Where(f => f.FieldType == typeof(string))
                .Select(f => new KeyValuePair<string, string>(
                    f.GetCustomAttribute<DescriptionAttribute>()?.Description ?? f.Name,
                    f.GetRawConstantValue() as string ?? f.Name))
                .ToList();

            if (types.GroupBy(u => u.Value).Any(u => u.Count() > 1))
            {
                throw AppException.Of(ErrorCodes.SerialTypeDuplicated, args: null, code: 500);
            }

            _typeList = types;
            return _typeList;
        }

        /// <summary>
        /// 取号核心(参照 NextSeqNo 移植):分布式锁 → 读配置 → 周期重置判定 → 上限校验 → 单语句推进 → 渲染。
        /// </summary>
        public async Task<string> NextSeqNoAsync(string type, bool isGlobal = false)
        {
            long? tenantId = isGlobal ? SqlSugarConst.DefaultTenantId : (_userManager.TenantId ?? SqlSugarConst.DefaultTenantId);
            var lockKey = $"{CacheConst.KeySerialLock}{tenantId}:{type}";

            // 分布式锁(IncrementAsync 原子自增;参照 BeginCacheLock 1s 重试窗口,本仓 5 × 100ms 同语义)
            bool locked = false;
            for (var i = 0; i < 5; i++)
            {
                if (await _cache.IncrementAsync(lockKey, TimeSpan.FromSeconds(5)) == 1)
                {
                    locked = true;
                    break;
                }

                await Task.Delay(100);
            }

            if (!locked)
            {
                throw AppException.Of(ErrorCodes.Busy, args: null, code: 429);
            }

            try
            {
                var query = _rep.AsQueryable();
                if (isGlobal)
                {
                    query = query.ClearFilter<ITenantIdFilter>();
                }

                var seq = await query.FirstAsync(u =>
                    u.Type == type && u.Status == 0 && u.TenantId == tenantId)
                    ?? throw AppException.With(ErrorCodes.SerialConfigUnavailable, "type", type, 404);

                // 周期重置判定(Day 跨日 / Month 跨月 / Year 跨年 → 落回 Min)
                var now = DateTime.Now;
                TryResetSeqNo(seq, now);

                // 上限校验
                seq.Seq++;
                if (seq.Seq > seq.Max)
                {
                    throw AppException.Of(ErrorCodes.SerialOutOfRange, new Dictionary<string, object?> { ["type"] = type, ["max"] = seq.Max }, 400);
                }

                // 单语句原子推进(SqlSugar 生成单条 UPDATE;WHERE Id 精确,无 check-then-update 竞态——持锁串行)
                var updated = await _rep.AsUpdateable(seq)
                    .UpdateColumns(u => new { u.Expy, u.Seq })
                    .Where(u => u.Id == seq.Id)
                    .ExecuteCommandAsync();
                if (updated <= 0)
                {
                    throw AppException.With(ErrorCodes.SerialAdvanceFailed, "type", type, 400);
                }

                return FormatSeqNo(seq, now);
            }
            finally
            {
                // 释放锁(参照 BeginCacheLock dispose 释放;TTL 5s 防异常残留)
                await _cache.RemoveAsync(lockKey);
            }
        }

        /// <summary>重置判定(参照 TryRestSeqNo 同构:Expy 为当前周期锚点)。</summary>
        private static void TryResetSeqNo(SysSerial seq, DateTime now)
        {
            switch (seq.ResetInterval)
            {
                case ResetIntervalEnum.Day:
                    if (now.Date != seq.Expy.Date)
                    {
                        seq.Expy = now.Date;
                        seq.Seq = seq.Min;
                    }

                    break;

                case ResetIntervalEnum.Month:
                    if (now.Month != seq.Expy.Month || now.Year != seq.Expy.Year)
                    {
                        seq.Expy = now.Date;
                        seq.Seq = seq.Min;
                    }

                    break;

                case ResetIntervalEnum.Year:
                    if (now.Year != seq.Expy.Year)
                    {
                        seq.Expy = now.Date;
                        seq.Seq = seq.Min;
                    }

                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// 插槽渲染(参照 FormatSeqNo 同构):Formater 中 {xxx} 字面量按插槽映射替换;未知插槽 → 400。
        /// </summary>
        private string FormatSeqNo(SysSerial seq, DateTime now)
        {
            var logic = GetSlotLogicMap();
            return System.Text.RegularExpressions.Regex.Replace(seq.Formater!, @"\{.*?\}", match =>
            {
                if (logic.TryGetValue(match.Value, out var func))
                {
                    return func(seq.Seq, seq.Max, now, _userManager);
                }

                throw AppException.With(ErrorCodes.SerialSlotInvalid, "slot", match.Value, 400);
            });
        }

        /// <summary>
        /// 插槽逻辑模板(进程级缓存,参照 GetSlotMap 默认集同构)。
        /// WHY 无 ISerialSlotProvider 扩展(参照有,本批裁剪):当前无业务扩插槽需求(YAGNI,约定 #8);
        /// 后续如需 = 按 ISerialTypeProvider 同模式(程序集扫描)补扩展点,单点改动。
        /// </summary>
        private Dictionary<string, Func<long, long, DateTime, UserManager, string>> GetSlotLogicMap()
        {
            return SlotLogicMap.GetOrAdd("default", _ =>
            {
                var logicMap = new Dictionary<string, Func<long, long, DateTime, UserManager, string>>
                {
                    // 序号(按 Max 位数零填充)
                    ["{SEQ}"] = (s, m, _, _) => s.ToString($"D{Math.Min(m.ToString().Length, 18)}"),
                    // 时间
                    ["{yyyy}"] = (_, _, d, _) => d.Year.ToString(),
                    ["{yy}"] = (_, _, d, _) => d.Year.ToString().Substring(2),
                    ["{MM}"] = (_, _, d, _) => d.Month.ToString("D2"),
                    ["{dd}"] = (_, _, d, _) => d.Day.ToString("D2"),
                    ["{HH}"] = (_, _, d, _) => d.Hour.ToString("D2"),
                    ["{mm}"] = (_, _, d, _) => d.Minute.ToString("D2"),
                    ["{ss}"] = (_, _, d, _) => d.Second.ToString("D2"),
                    // 身份(Claim;无上下文 → 空串,后台任务语义)
                    ["{TenantId}"] = (_, _, _, u) => u?.TenantId?.ToString() ?? "",
                    ["{UserId}"] = (_, _, _, u) => u?.UserId?.ToString() ?? "",
                    ["{OrgId}"] = (_, _, _, u) => u?.OrgId?.ToString() ?? ""
                };

                return logicMap;
            });
        }
    }
}
