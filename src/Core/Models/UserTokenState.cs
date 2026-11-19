﻿namespace AdminCore.Core.Models
{
    /// <summary>
    /// 用户 Token 状态快照(缓存缺失时 DB 回查的结果):Token 版本 + 账号停用标记 + 租户有效标记
    /// (批 C 1.4:JwtAuthHandler 回查时租户不存在/停用/过期即拒绝)。
    /// </summary>
    public sealed record UserTokenState(int TokenVersion, bool IsDisabled, bool TenantValid = true);
}
