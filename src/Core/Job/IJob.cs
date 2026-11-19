namespace AdminCore.Core.Job
{
    /// <summary>
    /// 定时任务执行上下文(task_108):标识任务的一次执行(任务名/触发来源/触发时刻)。
    /// Trigger 取值:scheduled(调度到点)/manual(RunNow 手动)。
    /// </summary>
    public sealed record JobContext(string JobName, string Trigger, DateTime TriggeredAt);

    /// <summary>
    /// 定时任务契约(task_108):实现类贴 <c>[JobName("...")]</c> 即被调度器反射扫描自动登记
    /// (同事件总线 IEventSubscriber 的声明式模式,零注册代码);任务体经独立作用域解析依赖(仓储/配置)。
    /// 执行异常由调度器捕获(记 ILogger + 不退出调度器),任务体无需为调度存活兜底;
    /// 但任务体自身的数据一致性(事务/幂等)仍由任务体负责。
    /// </summary>
    public interface IJob
    {
        /// <summary>
        /// 执行任务一轮。<paramref name="ct"/> 为宿主停止令牌——正常停止时宿主动员在途执行,
        /// 但不强制杀:任务体可选择是否响应取消(契约「正在执行的任务允许跑完」)。
        /// </summary>
        Task ExecuteAsync(JobContext context, CancellationToken ct);
    }
}
