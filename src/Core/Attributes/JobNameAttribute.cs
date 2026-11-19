namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 定时任务声明特性(task_108):标注在 IJob 实现类上,调度器启动时反射扫描自动登记(代码注册),
    /// 新增任务零注册代码(同事件总线 [EventSubscribe] 模式——声明式、自动发现)。
    /// </summary>
    /// <remarks>
    /// Inherited=false:任务语义按类显式声明,不随继承传播(与 EventSubscribeAttribute 同口径)。
    /// Name 为全局唯一键:与 sys_job_detail.JobName 列匹配,调度器以它为调度键与防重入锁键
    /// (CacheConst.KeyJobLock + Name)。重复命名在调度器扫描期快速失败(启动即抛,拒绝静默误注册)。
    /// </remarks>
    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class JobNameAttribute : System.Attribute
    {
        /// <summary>任务名(全局唯一,匹配 sys_job_detail.JobName)。</summary>
        public string Name { get; }

        public JobNameAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new System.ArgumentException("任务名不能为空", nameof(name));
            }

            Name = name;
        }
    }
}
