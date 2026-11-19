namespace AdminCore.Core.Utils
{
    /// <summary>
    /// 上传存储的**集群安全性判据**(批 AW F1):纯函数 + 配置键常量,供启动审计与单测共用。
    /// </summary>
    /// <remarks>
    /// WHY 需要本件(G22② 的病灶):写 Provider 由 <c>Upload:EnableSaveDb</c> 决定——缺省 <c>false</c> 时文件落**本机**
    /// <c>wwwroot/uploads</c>。单实例无碍,但**集群下 A 实例上传的文件在 B 实例下载 404**(各实例各一份本地盘)。
    /// 框架侧**不能替部署选档**(那是"存储不共享"这个根因的对策,只能部署决定:置 DB 档或各实例挂同一共享卷),
    /// 但**必须让它可见**——修前既无档位留痕、也无任何集群告警 ⇒ "上线后才发现随机 404"。
    ///
    /// 集群声明的信号取 <c>EventBus:EnableRedis</c>(与批 AU/AV 守卫同源):运维把双通道打开即等于声明"这是集群",
    /// 此时文件仍落本地盘就是明确风险;**未开双通道(单实例部署)不打扰**(不制造噪声告警)。
    ///
    /// 档位切换的既有口径(不在本批变更):历史数据读取**按记录 Provider 字段路由** ⇒ 切档不破坏历史(见 SysFileService)。
    /// </remarks>
    public static class UploadStorageGuard
    {
        /// <summary>写 Provider 配置键(值 true=落 <c>sys_file_content</c> 表;缺省 false=落本机 wwwroot/uploads)。</summary>
        public const string EnableSaveDbConfigKey = "Upload:EnableSaveDb";

        /// <summary>集群声明信号键(双通道开启＝运维已声明多实例;与批 AU/AV 守卫复用同一信号)。</summary>
        public const string ClusterDeclaredConfigKey = "EventBus:EnableRedis";

        /// <summary>
        /// 是否需要"跨实例文件不共享"告警(批 AW F1 真值表):**集群声明为真** 且 **落本地盘** ⇒ true;其余组合 false。
        /// </summary>
        /// <param name="clusterDeclared">是否已声明集群(<c>EventBus:EnableRedis=true</c>)。</param>
        /// <param name="saveToDb">写 Provider 是否走 DB 档(<c>Upload:EnableSaveDb=true</c>)。</param>
        public static bool NeedsClusterStorageWarning(bool clusterDeclared, bool saveToDb)
            => clusterDeclared && !saveToDb;

        /// <summary>
        /// 生效写存储的可读描述(留痕日志用):DB 档 → <c>Db(sys_file_content)</c>;本地档 → <c>Local(wwwroot/uploads)</c>。
        /// </summary>
        public static string DescribeEffectiveStorage(bool saveToDb)
            => saveToDb ? "Db(sys_file_content)" : "Local(wwwroot/uploads)";
    }
}