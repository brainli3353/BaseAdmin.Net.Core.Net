using System.Security.Claims;
using AdminCore.Core.Attributes;
using Microsoft.AspNetCore.Http;

namespace AdminCore.Core.Security
{
    /// <summary>
    /// 当前登录用户上下文门面(批 D 1.14):收敛散落各处的 User.FindFirst(JwtClaimNames.X) 直接读——
    /// 超管判定("999" 字面量)、Claim 解析(TryParse 失败处理)、身份字段读取统一经本类单点。
    /// </summary>
    /// <remarks>
    /// WHY 门面而非扩展方法:超管判定含业务语义(AccountType==999 = 权限穿透凭证),
    /// 语义收敛到具名类型比静态扩展更可发现(IDE 自动补全可见 IsSuperAdmin);
    /// Scoped 生命周期(每请求一实例);Claim 读取为**实时**(对齐 IHttpContextAccessor 每次读取语义——
    /// 构造快照会失配「服务先解析、HttpContext 后填充」场景[单测/后台手动挂 Claims],批 B 越权防护回归实证)。
    /// 消费方:MenuPermFilter/SysRoleService(越权防护操作者)/各控制器/OpLogFilter/PermissionMiddleware;
    /// 不收敛:JwtAuthHandler(认证机制内部,User 定稿前运行)、SqlSugarSetup AOP(审计填充内部细节)。
    /// 无请求上下文(后台任务/单测未挂 HttpContext)→ 全属性 null/IsAuthenticated=false。
    /// </remarks>
    [Service]
    public class UserManager
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public UserManager(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        }

        /// <summary>是否已认证(JwtBearer 认证后恒 true;匿名 false)。</summary>
        public bool IsAuthenticated => _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true;

        /// <summary>用户 Id(Claim UserId;缺/解析失败 → null)。</summary>
        public long? UserId => ParseLong(JwtClaimNames.UserId);

        /// <summary>登录账号(Claim Account;如 admin)。</summary>
        public string? Account => Find(JwtClaimNames.Account);

        /// <summary>真实姓名(Claim RealName)。</summary>
        public string? RealName => Find(JwtClaimNames.RealName);

        /// <summary>账号类型(Claim AccountType;999=超管)。</summary>
        public int? AccountType => ParseInt(JwtClaimNames.AccountType);

        /// <summary>租户 Id(Claim TenantId)。</summary>
        public long? TenantId => ParseLong(JwtClaimNames.TenantId);

        /// <summary>
        /// 机构 Id(Claim OrgId;批 E 1.7 开放接口已写入 Claim,本门面补读——批 W 4.3 流水号 {OrgId} 插槽消费)。
        /// 无机构/未绑定 → null(同 TenantId 缺省语义)。
        /// </summary>
        public long? OrgId => ParseLong(JwtClaimNames.OrgId);

        /// <summary>
        /// 超管账号类型值(权限穿透凭证的判定值)。
        /// WHY 提为常量:S-G D1.4 片 a 的写侧校验要在**无 Claim 的实体行**上判超管(禁止把超管绑成开放接口身份),
        /// 若在服务层再写一次 999 就是第二处字面量 ⇒ 与本类"超管判定单点"的既有意图相悖。
        /// </summary>
        public const int SuperAdminAccountType = 999;

        /// <summary>
        /// 是否超管(Claim AccountType == <see cref="SuperAdminAccountType"/>,权限穿透凭证——超管权限不依赖菜单授权链,
        /// 对齐 008 超管穿透语义)。未认证/缺 Claim/解析失败 → false(安全侧:认不出即非超管)。
        /// </summary>
        public bool IsSuperAdmin => AccountType == SuperAdminAccountType;

        private string? Find(string claimType)
            => _httpContextAccessor.HttpContext?.User?.FindFirst(claimType)?.Value;

        private long? ParseLong(string claimType)
            => long.TryParse(Find(claimType), out var value) ? value : null;

        private int? ParseInt(string claimType)
            => int.TryParse(Find(claimType), out var value) ? value : null;
    }
}
