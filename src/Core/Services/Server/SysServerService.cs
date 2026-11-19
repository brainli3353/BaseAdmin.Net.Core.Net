using AdminCore.Core.Attributes;
using AdminCore.Core.Utils.ServerMonitor;

namespace AdminCore.Core.Services.Server
{
    /// <summary>
    /// 服务器监控服务(批 Y 4.14,参照 Admin.NET SysServerService 移植):
    /// 三个只读端点——运行时信息/硬件信息/框架程序集清单。
    /// 无数据库无状态;采集实现全在 <c>ServerInfoCollector</c>(Utils,纯函数可单测)。
    /// </summary>
    [Service]
    public class SysServerService
    {
        /// <summary>
        /// 运行时信息(进程/内存/GC/线程/OS 单点快照)。
        /// </summary>
        public ServerRuntimeOutput GetRuntimeInfo()
        {
            return ServerInfoCollector.GetRuntimeInfo();
        }

        /// <summary>
        /// 硬件/系统信息(CPU 型号与使用率/内存/磁盘)。
        /// </summary>
        public ServerHardwareOutput GetHardwareInfo()
        {
            return ServerInfoCollector.GetHardwareInfo();
        }

        /// <summary>
        /// 框架程序集清单(已加载的 AdminCore.* 程序集)。
        /// </summary>
        public List<ServerAssemblyOutput> GetFrameworkAssemblies()
        {
            return ServerInfoCollector.GetFrameworkAssemblies();
        }
    }
}
