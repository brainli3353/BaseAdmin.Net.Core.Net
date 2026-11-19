using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Repository;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Models;

namespace AdminCore.Core.Services.Auth
{
    /// <summary>
    /// IUserTokenStateProvider 的业务实现:查 sys_user 得 TokenVersion + 账号状态,并(批 C 1.4)
    /// 查 sys_tenant 得租户有效态(JwtAuthHandler 缓存缺失时 DB 回查路径:租户不存在/停用/过期即拒绝)。
    /// </summary>
    [Service]
    public class UserTokenStateProvider : IUserTokenStateProvider
    {
        private readonly ISqlSugarRepository<SysUser> _userRepo;
        private readonly ISqlSugarRepository<SysTenant> _tenantRepo;

        public UserTokenStateProvider(ISqlSugarRepository<SysUser> userRepo, ISqlSugarRepository<SysTenant> tenantRepo)
        {
            _userRepo = userRepo;
            _tenantRepo = tenantRepo;
        }

        public async Task<UserTokenState?> GetUserStateAsync(long userId)
        {
            var user = await _userRepo.GetFirstAsync(u => u.Id == userId);
            if (user is null)
            {
                return null;
            }

            // 批 C 1.4:租户有效态(TenantId 为空 → 默认租户;须行存在 + 未停用 + 未过期)
            var tenantId = user.TenantId ?? SqlSugarConst.DefaultTenantId;
            var tenant = await _tenantRepo.GetFirstAsync(t => t.Id == tenantId);
            var tenantValid = tenant is not null && tenant.Status == 0
                && (tenant.ExpirationTime is null || tenant.ExpirationTime >= DateTime.Now);

            return new UserTokenState(user.TokenVersion, user.Status != 0, tenantValid);
        }
    }
}
