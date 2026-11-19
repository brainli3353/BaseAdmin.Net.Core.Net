using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Notice.Dto;
using AdminCore.Core.Services.OnlineUser;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

namespace AdminCore.Core.Services.Notice
{
    /// <summary>
    /// 通知公告服务(批 G 4.7;参照 AdminNetPro SysNoticeService 移植,去 Furion):
    /// 草稿/发布/接收阅读三态管理——分页(非超管仅本人发布)/新建/更新(仅发布人)/删除(仅发布人,已发布禁删,级联清理接收行)/
    /// 发布(userIds 空=全租户用户,先物理删旧行再重建,广播在线用户)/设置已读/接收分页/未读列表/最新/详情。
    /// 映射口径:接收行与通知的关联用 LeftJoin 手动映射(不用导航属性——批 E 导航泄露教训)。
    /// </summary>
    [Service]
    public class SysNoticeService
    {
        private readonly ISqlSugarRepository<SysNotice> _noticeRep;
        private readonly ISqlSugarRepository<SysNoticeUser> _noticeUserRep;
        private readonly ISqlSugarRepository<SysUser> _userRep;
        private readonly ISqlSugarRepository<SysOrg> _orgRep;
        private readonly UserManager _userManager;
        private readonly SysOnlineUserService _onlineUserService;
        private readonly AdminCore.Core.Services.Config.SysConfigService _configService;

        public SysNoticeService(
            ISqlSugarRepository<SysNotice> noticeRep,
            ISqlSugarRepository<SysNoticeUser> noticeUserRep,
            ISqlSugarRepository<SysUser> userRep,
            ISqlSugarRepository<SysOrg> orgRep,
            UserManager userManager,
            SysOnlineUserService onlineUserService,
            AdminCore.Core.Services.Config.SysConfigService configService)
        {
            _noticeRep = noticeRep ?? throw new ArgumentNullException(nameof(noticeRep));
            _noticeUserRep = noticeUserRep ?? throw new ArgumentNullException(nameof(noticeUserRep));
            _userRep = userRep ?? throw new ArgumentNullException(nameof(userRep));
            _orgRep = orgRep ?? throw new ArgumentNullException(nameof(orgRep));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _onlineUserService = onlineUserService ?? throw new ArgumentNullException(nameof(onlineUserService));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        }

        /// <summary>
        /// 分页:标题模糊/类型过滤;非超管仅本人发布(参照口径,租户过滤由全局过滤器自动限域);创建时间降序。
        /// </summary>
        public async Task<PageResult<NoticeOutput>> PageAsync(PageNoticeInput input)
        {
            var query = _noticeRep.AsQueryable();
            if (!string.IsNullOrWhiteSpace(input.Title))
            {
                var title = input.Title.Trim();
                query = query.Where(u => u.Title.Contains(title));
            }

            if (input.Type > 0)
            {
                query = query.Where(u => u.Type == (NoticeTypeEnum)input.Type);
            }

            // 批 UX B1d:CreateTime 范围(闭区间口径;草稿/发布行都有 CreateTime,PublicTime 草稿为 null 故不用)
            query = query
                .WhereIF(input.StartDate.HasValue, u => u.CreateTime >= input.StartDate)
                .WhereIF(input.EndDate.HasValue, u => u.CreateTime <= input.EndDate);

            if (!_userManager.IsSuperAdmin)
            {
                var userId = _userManager.UserId ?? 0;
                query = query.Where(u => u.CreateUserId == userId);
            }

            RefAsync<int> total = 0;
            var list = await query
                .ApplyDynamicSort(input.OrderBy, input.Desc, u => u.CreateTime, OrderByType.Desc)
                .ToPageListAsync(input, total);
            return new PageResult<NoticeOutput> { Total = total, List = list.Select(ToOutput).ToList() };
        }

        /// <summary>
        /// 新建(恒草稿):发布人快照服务端自填——PublicUserId/PublicUserName 自 Claim,
        /// PublicOrgId/PublicOrgName 按发布人当前机构查得(OrgId Claim 未接入,仓储查询口径)。
        /// </summary>
        public async Task<long> AddAsync(AddNoticeInput input)
        {
            var userId = _userManager.UserId ?? 0;
            var user = await _userRep.GetFirstAsync(u => u.Id == userId);
            long? orgId = user?.OrgId;
            string? orgName = orgId is null ? null : await _orgRep.AsQueryable().Where(o => o.Id == orgId).Select(o => o.Name).FirstAsync();

            var notice = new SysNotice
            {
                Title = input.Title,
                Content = input.Content,
                Type = ToType(input.Type),
                PublicUserId = userId,
                PublicUserName = _userManager.RealName,
                PublicOrgId = orgId ?? 0,
                PublicOrgName = orgName,
                Status = NoticeStatusEnum.Draft
            };
            await _noticeRep.InsertAsync(notice);
            return notice.Id;
        }

        /// <summary>更新(仅发布人本人;部分更新,约定 #9)。</summary>
        public async Task UpdateAsync(UpdateNoticeInput input)
        {
            var notice = await RequireOwnerAsync(input.Id);

            if (!string.IsNullOrEmpty(input.Title))
            {
                notice.Title = input.Title;
            }

            if (!string.IsNullOrEmpty(input.Content))
            {
                notice.Content = input.Content;
            }

            if (input.Type is > 0)
            {
                notice.Type = ToType(input.Type.Value);
            }

            await _noticeRep.UpdateAsync(notice);
        }

        /// <summary>
        /// 删除(仅发布人本人;已发布禁删;级联物理删接收行——中间行无审计价值,仓储 DeletePhysicalByAsync 口径)。
        /// </summary>
        public async Task DeleteAsync(long id)
        {
            var notice = await RequireOwnerAsync(id);
            if (notice.Status == NoticeStatusEnum.Public)
            {
                throw AppException.Of(ErrorCodes.NoticePublishedUndeletable);
            }

            await _noticeRep.DeleteByIdAsync(id);
            await _noticeUserRep.DeletePhysicalByAsync(u => u.NoticeId == id);
        }

        /// <summary>
        /// 发布:发布人校验 → 置发布态/时间 → 接收人(userIds 空 = 全租户用户,租户过滤器自动限域)
        /// 先物理删旧行再批量重建(重发幂等)→ 广播在线用户 PublicNotice。
        /// </summary>
        public async Task PublicAsync(PublicNoticeInput input)
        {
            var notice = await RequireOwnerAsync(input.Id);

            notice.Status = NoticeStatusEnum.Public;
            notice.PublicTime = DateTime.Now;
            await _noticeRep.UpdateAsync(notice);

            var userIdList = input.UserIds is { Count: > 0 }
                ? input.UserIds
                : (await _userRep.AsQueryable().Select(u => u.Id).ToListAsync()).Distinct().ToList();

            await _noticeUserRep.DeletePhysicalByAsync(u => u.NoticeId == notice.Id);
            foreach (var uid in userIdList)
            {
                await _noticeUserRep.InsertAsync(new SysNoticeUser
                {
                    NoticeId = notice.Id,
                    UserId = uid,
                    ReadStatus = NoticeUserStatusEnum.Unread
                });
            }

            // 广播在线接收人(离线不推送——通知公告落库,上线后仍可经未读列表/接收列表读取)
            await _onlineUserService.PublicNoticeAsync(notice, userIdList);
        }

        /// <summary>设置当前用户对指定通知的已读态。</summary>
        public async Task SetReadAsync(long id)
        {
            var userId = _userManager.UserId ?? 0;
            var row = await _noticeUserRep.GetFirstAsync(u => u.NoticeId == id && u.UserId == userId);
            if (row is null)
            {
                return; // 无接收行(未发给本人)幂等放行
            }

            row.ReadStatus = NoticeUserStatusEnum.Read;
            row.ReadTime = DateTime.Now;
            await _noticeUserRep.UpdateAsync(row);
        }

        /// <summary>接收的分页(LeftJoin 通知表;标题/类型过滤;通知创建时间降序)。</summary>
        public async Task<PageResult<NoticeReceivedOutput>> PageReceivedAsync(PageNoticeInput input)
        {
            var userId = _userManager.UserId ?? 0;
            var query = _noticeUserRep.AsQueryable()
                .LeftJoin<SysNotice>((u, n) => u.NoticeId == n.Id)
                // WHY 过滤孤儿接收行:通知删除时服务层级联物理删接收行,但存量/手工数据仍可能孤儿化;
                // LEFT JOIN 无匹配 → null 行进入结果集 → 非空枚举(NoticeTypeEnum)反序列化 500(批 V 部署全量验证抓出)
                .Where((u, n) => u.UserId == userId && n.Title != null)
                .WhereIF(!string.IsNullOrWhiteSpace(input.Title),
                    (u, n) => n.Title.Contains(input.Title!.Trim()))
                .WhereIF(input.Type > 0, (u, n) => n.Type == (NoticeTypeEnum)input.Type)
                // 批 UX B1d:同 DTO 的时间范围在接收视图同口径生效(按通知 CreateTime;避免参数被静默忽略)
                .WhereIF(input.StartDate.HasValue, (u, n) => n.CreateTime >= input.StartDate)
                .WhereIF(input.EndDate.HasValue, (u, n) => n.CreateTime <= input.EndDate);

            RefAsync<int> total = 0;
            var list = await query
                .OrderBy((u, n) => n.CreateTime, OrderByType.Desc)
                .Select((u, n) => new
                {
                    NoticeId = n.Id,
                    n.Title,
                    n.Type,
                    n.Content,
                    n.Status,
                    u.ReadStatus,
                    u.ReadTime
                })
                .ToPageListAsync(input, total);

            return new PageResult<NoticeReceivedOutput>
            {
                Total = total,
                List = list.Select(r => new NoticeReceivedOutput(r.NoticeId, r.Title, (int)r.Type, r.Content, (int)r.Status, (int)r.ReadStatus, r.ReadTime)).ToList()
            };
        }

        /// <summary>
        /// 未读的通知列表(当前用户;通知创建时间降序)。
        /// 批 AF 4.11:升级公告(NoticeTypeEnum.Upgrade)是否参与未读提示由配置 <c>sys_upgrade</c> 控制(关=排除该类型,其余照常)。
        /// </summary>
        public async Task<List<NoticeOutput>> GetUnReadListAsync()
        {
            var userId = _userManager.UserId ?? 0;
            var includeUpgrade = await UpgradeNoticeEnabledAsync();
            var list = await _noticeUserRep.AsQueryable()
                .LeftJoin<SysNotice>((u, n) => u.NoticeId == n.Id)
                // WHY 过滤孤儿接收行:同 PageReceivedAsync 口径(LEFT JOIN 无匹配 → null 行 → ToOutput(null) NRE 500)
                .Where((u, n) => u.UserId == userId && u.ReadStatus == NoticeUserStatusEnum.Unread && n.Title != null)
                .WhereIF(!includeUpgrade, (u, n) => n.Type != NoticeTypeEnum.Upgrade)
                .OrderBy((u, n) => n.CreateTime, OrderByType.Desc)
                .Select((u, n) => n)
                .ToListAsync();
            return list.Select(ToOutput).ToList();
        }

        /// <summary>最新的通知公告(不限接收人,创建时间最新一条;无则 null)。批 AF:开关关时排除升级公告类型。</summary>
        public async Task<NoticeOutput?> GetLastAsync()
        {
            var includeUpgrade = await UpgradeNoticeEnabledAsync();
            var notice = await _noticeRep.AsQueryable()
                .WhereIF(!includeUpgrade, u => u.Type != NoticeTypeEnum.Upgrade)
                .OrderBy(u => u.CreateTime, OrderByType.Desc)
                .FirstAsync();
            return notice is null ? null : ToOutput(notice);
        }

        /// <summary>
        /// 升级公告提示开关(批 AF 4.11):读 <c>sys_upgrade</c>;True/缺失/解析失败=false 时的口径见下。
        /// fail-open:缺失或解析失败视为**开启**(提示)——与 sys_oplog 开关同口径(宁可提示不误吞)。
        /// </summary>
        private async Task<bool> UpgradeNoticeEnabledAsync()
        {
            var value = await _configService.GetConfigValueByCodeAsync(AdminCore.Core.Services.Config.SysConfigService.SysUpgrade);
            return !bool.TryParse(value, out var enabled) || enabled;
        }

        /// <summary>通知详情(自读面访问判定:超管放行;否则仅发布人或收件人;非超管视角下不存在/无权同型 NoticeNotFound)。</summary>
        public async Task<NoticeOutput?> GetDetailAsync(long id)
        {
            var notice = await _noticeRep.GetFirstAsync(u => u.Id == id);
            // WHY IDOR 收口:detail 端点仅登录即可达,原实现按 id 直读 ⇒ 任意登录用户可读他人草稿/无关通知;
            // 草稿态无非发布人收件行天然被挡;"不存在/无权"同抛 NoticeNotFound 防存在性泄露(与 RequireOwnerAsync 同口径)
            if (!_userManager.IsSuperAdmin)
            {
                var userId = _userManager.UserId ?? 0;
                var isPublisher = notice is not null && notice.PublicUserId == userId;
                if (!isPublisher && !await _noticeUserRep.IsAnyAsync(u => u.NoticeId == id && u.UserId == userId))
                {
                    throw AppException.Of(ErrorCodes.NoticeNotFound);
                }
            }

            return notice is null ? null : ToOutput(notice);
        }

        /// <summary>取通知并校验发布人归属(仅发布人本人可操作;不存在 → 通用提示不泄露存在性)。</summary>
        private async Task<SysNotice> RequireOwnerAsync(long id)
        {
            var notice = await _noticeRep.GetFirstAsync(u => u.Id == id);
            if (notice is null)
            {
                throw AppException.Of(ErrorCodes.NoticeNotFound);
            }

            if (notice.CreateUserId != _userManager.UserId)
            {
                throw AppException.Of(ErrorCodes.NoticeCreatorOnly);
            }

            return notice;
        }

        private static NoticeOutput ToOutput(SysNotice n)
            => new(n.Id, n.Title, n.Content, (int)n.Type, n.PublicUserId, n.PublicUserName,
                n.PublicOrgId, n.PublicOrgName, n.PublicTime, n.CancelTime, (int)n.Status, n.CreateTime);

        /// <summary>
        /// 入参类型 → 枚举:合法枚举值原样接受,**非法值回落 Notice**(容错)。
        /// 批 AF 修正(P1):此前硬编码 <c>type is 1 or 2</c> ⇒ 新增的 <see cref="NoticeTypeEnum.Upgrade"/>(3)被**静默改写成 Notice**——
        /// 服务层因此建不出/改不出升级公告(前端选"系统升级"落库变"通知"),编辑真升级公告还会被改写(绕过 sys_upgrade 开关)。
        /// 改为按枚举定义判定:新增枚举值自动放行,不再需要同步改这里。
        /// </summary>
        private static NoticeTypeEnum ToType(int type)
            // WHY 全限定 System.Enum:本仓存在 AdminCore.Core.Services.Enum 命名空间,短名 Enum 会被解析为该命名空间(批 Z 已登记的同类坑:命名空间段与 BCL 类型同名 ⇒ CS0234)
            => System.Enum.IsDefined(typeof(NoticeTypeEnum), type) ? (NoticeTypeEnum)type : NoticeTypeEnum.Notice;
    }
}
