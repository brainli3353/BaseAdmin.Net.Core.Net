using AdminCore.Core.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Utils
{
    /// <summary>
    /// 上传存储**启动审计**(批 AW F2):启动即留痕"生效写档位",并在"集群已声明却仍落本地盘"时**响亮告警**。
    /// </summary>
    /// <remarks>
    /// 病灶与判据见 <see cref="UploadStorageGuard"/>。本件只做"让它可见":
    /// - **总是**留痕一行 <c>文件存储档位:{Local(wwwroot/uploads)|Db(sys_file_content)}</c>——
    ///   修前连"当前是哪一档"都只能去翻配置文件,而切档需重启、错了要等运行时随机 404 才发现;
    /// - 命中判据时追加 LogWarning(**可检索**:「跨实例文件不共享」)＋动作指引(置 `Upload:EnableSaveDb=true`
    ///   或各实例挂同一共享卷;并说明"历史上传按记录 Provider 路由,切档不破坏历史");
    /// - 单实例(未开双通道)**不告警**,只留痕。
    ///
    /// 只读判定、零副作用:不碰上传管线、不改默认值、不写任何存储(与批 AS 的建表锁/批 AU 的守卫同"不改变业务语义"口径)。
    ///
    /// 注册(§99③ 零改 Program.cs):<c>[Service]</c> 锁定 ServiceType＝具体类＋Singleton,
    /// AddAutoServices 的 HostedService 分支补注册 <see cref="IHostedService"/>(与 WorkerIdGuard/ServerMetricsReporter 同模式);
    /// 本件无循环,仅用 <see cref="StartAsync"/> 做一次性判定,<see cref="StopAsync"/> 空实现。
    /// </remarks>
    [Service(ServiceType = typeof(UploadStorageAudit), Lifetime = ServiceLifetime.Singleton)]
    public sealed class UploadStorageAudit : IHostedService
    {
        private readonly bool _clusterDeclared;
        private readonly bool _saveToDb;
        private readonly ILogger<UploadStorageAudit> _logger;

        public UploadStorageAudit(
            IServiceProvider rootProvider,
            ILogger<UploadStorageAudit> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // IConfiguration 可选解析(最小宿主/单测容器可能未注册 → 按缺省:本地档 + 非集群),口径同 JobScheduler
            var config = rootProvider.GetService<IConfiguration>();
            _saveToDb = config?.GetValue(UploadStorageGuard.EnableSaveDbConfigKey, false) ?? false;
            _clusterDeclared = config?.GetValue(UploadStorageGuard.ClusterDeclaredConfigKey, false) ?? false;
        }

        /// <summary>
        /// 一次性判定:留痕档位(总是) + 集群风险告警(命中判据时)。
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var (storage, warnCluster) = Evaluate(_clusterDeclared, _saveToDb);

            _logger.LogInformation("文件存储档位:{Storage}(写 Provider;历史上传按记录 Provider 路由,切档不破坏历史)", storage);

            if (warnCluster)
            {
                _logger.LogWarning(
                    "跨实例文件不共享:已声明集群({ClusterKey}=true)但写 Provider 仍为本地档({Storage})——" +
                    "集群下 A 实例上传的文件在 B 实例下载将 404;请置 {EnableKey}=true(落 sys_file_content 表)" +
                    "或让各实例挂同一共享卷后重启(单实例部署可忽略本条)",
                    UploadStorageGuard.ClusterDeclaredConfigKey, storage, UploadStorageGuard.EnableSaveDbConfigKey);
            }

            return Task.CompletedTask;
        }

        /// <summary>无循环、无资源,停止即空实现。</summary>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// 判定入口(批 AW F5③:可脱离宿主直测,避免为断言起一个 Host):
        /// 返回(生效档位描述, 是否告警)。
        /// </summary>
        public static (string Storage, bool WarnCluster) Evaluate(bool clusterDeclared, bool saveToDb)
            => (UploadStorageGuard.DescribeEffectiveStorage(saveToDb),
                UploadStorageGuard.NeedsClusterStorageWarning(clusterDeclared, saveToDb));
    }
}