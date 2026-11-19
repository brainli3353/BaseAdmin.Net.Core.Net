using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Repository;

namespace AdminCore.Core.Services.Log
{
    /// <summary>
    /// 操作日志异步落库订阅者(task_014 契约 §3):经事件总线消费 "op_log" 事件,复用原 OpLogFilter 的落库口径
    /// (ISqlSugarRepository&lt;SysLogOp&gt; 按 [LogTable] 选库插入),把「请求链路内同步/火忘落库」改为「总线后台异步落库」。
    /// </summary>
    /// <remarks>
    /// 自注册口径:总线按「订阅者具体类型」从 DI 作用域解析(见 EventBus.DispatchAsync),故须显式
    /// ServiceType=具体类自注册——若省略,自动匹配唯一公共接口会注册成 IEventSubscriber→本类,具体类反而解析不到,
    /// 总线将告警跳过(见 EventBus 的 DI 解析失败告警分支)。
    /// 生命周期 Scoped:总线每事件消费临时建作用域解析本类(见 EventBus),Scoped 随该作用域生灭,仓储注入安全。
    /// 落库失败由总线重试(≤2 次)后记告警,绝不上抛——操作日志丢失不伤主流程(审计可容忍,业务优先)。
    /// </remarks>
    [Service(ServiceType = typeof(OpLogSubscriber))]
    public class OpLogSubscriber : IEventSubscriber
    {
        private readonly ISqlSugarRepository<SysLogOp> _repo;

        public OpLogSubscriber(ISqlSugarRepository<SysLogOp> repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        /// <summary>
        /// 消费 "op_log" 事件:复用原 fire-and-forget 的 InsertAsync 落库逻辑(按 [LogTable] 选日志库,未配置回落主库)。
        /// 返回 Task 供总线等待完成并纳入重试计数;抛出的异常由总线按重试策略处理(不回流主流程)。
        /// </summary>
        [EventSubscribe("op_log")]
        public Task HandleOpLogAsync(SysLogOp log)
        {
            if (log is null)
            {
                return Task.CompletedTask;
            }

            return _repo.InsertAsync(log);
        }
    }
}
