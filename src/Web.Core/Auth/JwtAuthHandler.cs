﻿using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AdminCore.Web.Core.Auth
{
    /// <summary>
    /// 业务 Token 校验链(task_007;JwtBearer 内建校验通过后经 OnTokenValidated 事件执行):
    /// a. 黑名单——POST /logout 写入 sys_token_blacklist:{原始token}(TTL=剩余有效期),命中即失效;
    /// b. TokenVersion——Claim 值对 Redis sys_user_token:{userId};缓存缺失时经提供器(DB 回查)兜底,
    ///    并顺带校验账号存在/停用(停用账号一律拒绝) + 租户有效(批 C 1.4:租户不存在/停用/过期 → 拒绝,
    ///    租户停用时业务侧已失效本键触发回查)。
    /// 命中 → context.Fail(JwtBearer 统一转 401)。这是内建签名/有效期校验之后的第二/三道防线:
    /// 强制失效机制——版本一升,旧 token 即刻作废,无需等 8 小时自然过期。
    /// </summary>
    [Service]
    public class JwtAuthHandler
    {
        private readonly ICacheService _cache;
        private readonly IUserTokenStateProvider _userState;

        public JwtAuthHandler(ICacheService cache, IUserTokenStateProvider userState)
        {
            _cache = cache;
            _userState = userState;
        }

        /// <summary>执行黑名单 + TokenVersion 链;命中即以 context.Fail 使请求失败(401)。</summary>
        public async Task HandleAsync(TokenValidatedContext context)
        {
            // 内建校验已通过(签名/有效期/issuer);UserId 是必需业务 Claim,缺失 → 旧 token 或伪造 Claim 组合,拒绝
            if (!long.TryParse(context.Principal?.FindFirst(JwtClaimNames.UserId)?.Value, out var userId))
            {
                context.Fail("Token 无效,请重新登录");
                return;
            }

            // a. 黑名单(key 用原始 token,与 logout 写入侧对齐)
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader["Bearer ".Length..].Trim();
                if (token.Length > 0 && await _cache.ExistKeyAsync($"sys_token_blacklist:{token}"))
                {
                    context.Fail("Token 已失效,请重新登录");
                    return;
                }
            }

            // b. TokenVersion(Claim → Redis 快路径 → DB 回查兜底)
            if (!int.TryParse(context.Principal.FindFirst(JwtClaimNames.TokenVersion)?.Value, out var tokenVersion))
            {
                // task_007 之前签发的 token 无此 Claim → 一律强制重登(不做兼容回填)
                context.Fail("Token 无效,请重新登录");
                return;
            }

            var versionKey = $"{CacheConst.KeyUserToken}{userId}";
            // 快路径:读 Redis 当前版本。GetAsync 对值类型键缺失返回 default(int=0);
            // TokenVersion 起始 1(DB DefaultValue)且只增(强制失效机制),0 可统一视为缓存缺失
            var current = await _cache.GetAsync<int>(versionKey);
            if (current <= 0)
            {
                // 缓存缺失 → DB 回查(顺带校验账号存在/停用)
                var state = await _userState.GetUserStateAsync(userId);
                if (state is null)
                {
                    context.Fail("用户不存在");
                    return;
                }

                if (state.IsDisabled)
                {
                    context.Fail("账号已停用");
                    return;
                }

                // 批 C 1.4:租户有效态(回查时租户不存在/停用/过期 → 拒绝;仅缓存缺失路径,
                // 租户停用由 SetStatusAsync 失效本键触发回查,不逐请求查库)
                if (!state.TenantValid)
                {
                    context.Fail("租户已停用或已过期");
                    return;
                }

                current = state.TokenVersion;
                // 回填缓存:后续请求走快路径;账号版本变更(改密/强制失效)时由业务侧刷新本 key
                await _cache.SetAsync(versionKey, state.TokenVersion);
            }

            if (current != tokenVersion)
            {
                context.Fail("Token 已失效,请重新登录");
            }
        }
    }
}
