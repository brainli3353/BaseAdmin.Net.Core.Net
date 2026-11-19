using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Repository;

namespace AdminCore.Core.Services.Message
{
    /// <summary>
    /// 站内消息日志异步落库订阅者(批 G 4.8):经事件总线消费 "msg_log" 事件,把消息日志 SysLogMsg
    /// 落 [LogTable] 日志库(未配置日志库连接回落主库)——对齐 OpLogSubscriber 模式(总线后台异步,主流程不等待)。
    /// </summary>
    /// <remarks>
    /// 自注册口径同 OpLogSubscriber:总线按「订阅者具体类型」从 DI 作用域解析,须显式 ServiceType=具体类
    /// (否则自动匹配 IEventSubscriber 后具体类解析不到,总线告警跳过)。
    /// 生命周期 Scoped:总线每事件消费临时建作用域解析本类,仓储注入安全。
    /// 落库失败由总线重试(≤2 次)后记告警,绝不上抛——消息日志丢失不伤主流程(推送语义已达成,日志为审计留痕)。
    /// </remarks>
    [Service(ServiceType = typeof(MsgLogSubscriber))]
    public class MsgLogSubscriber : IEventSubscriber
    {
        private readonly ISqlSugarRepository<SysLogMsg> _repo;

        public MsgLogSubscriber(ISqlSugarRepository<SysLogMsg> repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        /// <summary>消费 "msg_log" 事件:插入消息日志(按 [LogTable] 选库)。抛出的异常由总线按重试策略处理(不回流主流程)。</summary>
        [EventSubscribe("msg_log")]
        public Task HandleMsgLogAsync(SysLogMsg log)
        {
            if (log is null)
            {
                return Task.CompletedTask;
            }

            return _repo.InsertAsync(log);
        }
    }
}
