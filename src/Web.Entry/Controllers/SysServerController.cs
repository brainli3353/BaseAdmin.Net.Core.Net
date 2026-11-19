using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Server;
using AdminCore.Core.Services.Server.Dto;
using AdminCore.Core.Utils.ServerMonitor;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 服务器监控 API(批 Y 4.14 ＋批 AT 多节点):运行时/硬件/框架程序集三个即时端点
    /// ＋ <c>nodes</c> 集群聚合端点(读各实例自报快照),路由 /api/sysServer,全只读。
    /// 全 [MenuPerm](超管穿透;未授权 403)——诊断信息含主机名/内存布局,不裸奔。
    /// </summary>
    [ApiController]
    [Route("api/sysServer")]
    public class SysServerController : ControllerBase
    {
        private readonly SysServerService _serverService;
        private readonly SysServerNodeService _nodeService;

        public SysServerController(SysServerService serverService, SysServerNodeService nodeService)
        {
            _serverService = serverService;
            _nodeService = nodeService;
        }

        /// <summary>
        /// GET /api/sysServer/nodes → List&lt;ServerNodeOutput&gt;(批 AT:全部存活实例的自报快照,含 isSelf 标注)。
        /// 三个即时端点是"谁应答看谁"的本实例诊断;本端点是集群视角(粒度＝上报周期 30s,非秒级监控)。
        /// </summary>
        [MenuPerm("sysServer/nodes")]
        [HttpGet("nodes")]
        public async Task<IActionResult> Nodes()
        {
            return Ok(ApiResult<List<ServerNodeOutput>>.Success(await _nodeService.GetNodesAsync()));
        }

        /// <summary>GET /api/sysServer/runtime → ServerRuntimeOutput(进程/内存/GC/线程/OS)。</summary>
        [MenuPerm("sysServer/runtime")]
        [HttpGet("runtime")]
        public IActionResult Runtime()
        {
            return Ok(ApiResult<ServerRuntimeOutput>.Success(_serverService.GetRuntimeInfo()));
        }

        /// <summary>GET /api/sysServer/hardware → ServerHardwareOutput(CPU/内存/磁盘)。</summary>
        [MenuPerm("sysServer/hardware")]
        [HttpGet("hardware")]
        public IActionResult Hardware()
        {
            return Ok(ApiResult<ServerHardwareOutput>.Success(_serverService.GetHardwareInfo()));
        }

        /// <summary>GET /api/sysServer/assembly → List&lt;ServerAssemblyOutput&gt;(框架程序集清单)。</summary>
        [MenuPerm("sysServer/assembly")]
        [HttpGet("assembly")]
        public IActionResult Assembly()
        {
            return Ok(ApiResult<List<ServerAssemblyOutput>>.Success(_serverService.GetFrameworkAssemblies()));
        }
    }
}
