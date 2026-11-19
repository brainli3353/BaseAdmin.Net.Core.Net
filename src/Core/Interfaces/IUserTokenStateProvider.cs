using AdminCore.Core.Models;

namespace AdminCore.Core.Interfaces
{
    /// <summary>
    /// 用户 Token 状态提供器(框架层抽象,Business 层实现并注册)。
    /// WHY 抽象:JwtAuthHandler 在 Web.Core,用户实体在 Business——直接引用会形成
    /// Web.Core→Business 反向依赖(分层方向是 Core←Web.Core←Business 单向);
    /// 本接口做依赖倒置:Web.Core 定契约,Business 供实现。
    /// </summary>
    public interface IUserTokenStateProvider
    {
        /// <summary>按 userId 查当前 TokenVersion 与账号状态(用户不存在返回 null)。</summary>
        Task<UserTokenState?> GetUserStateAsync(long userId);
    }
}
