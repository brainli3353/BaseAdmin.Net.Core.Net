using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Repository;

namespace AdminCore.Core.Services.Log
{
    /// <summary>
    /// 异常日志异步落库订阅者(5.1 异常档,与操作档 OpLogSubscriber 同范式):
    /// 经事件总线消费 "ex_log" 事件,插入 SysLogEx([LogTable] 选库,未配置回落主库)。
    /// </summary>
    /// <remarks>
    /// 自注册口径:总线按「订阅者具体类型」从 DI 作用域解析(见 EventBus.DispatchAsync),
    /// 故须显式 ServiceType=具体类自注册(同 OpLogSubscriber 注释)。
    /// 生命周期 Scoped:总线每事件消费临时建作用域解析本类,仓储注入安全。
    /// 落库失败由总线重试(≤2 次)后记告警,绝不上抛——异常日志丢失不伤主流程(业务优先)。
    /// </remarks>
    [Service(ServiceType = typeof(ExLogSubscriber))]
    public class ExLogSubscriber : IEventSubscriber
    {
        private readonly ISqlSugarRepository<SysLogEx> _repo;

        public ExLogSubscriber(ISqlSugarRepository<SysLogEx> repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        /// <summary>消费 "ex_log" 事件:InsertAsync 落库(按 [LogTable] 选日志库)。</summary>
        [EventSubscribe("ex_log")]
        public Task HandleExLogAsync(SysLogEx log)
        {
            if (log is null)
            {
                return Task.CompletedTask;
            }

            return _repo.InsertAsync(log);
        }
    }
}
