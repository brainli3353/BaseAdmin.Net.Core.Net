using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

namespace AdminCore.Core.Services.Log
{
    /// <summary>
    /// 操作日志查询服务(批 D 5.1 日志闭环查询侧):分页 + Account/ControllerName/Success/时间范围过滤。
    /// 写入侧不在本服务——OpLogFilter(全局 ActionFilter)执行后发布 op_log 事件,
    /// 事件总线订阅者(OpLogSubscriber)异步落库 SysLogOp([LogTable] 多库通用选库,本服务零感知)。
    /// </summary>
    [Service]
    public class SysLogOpService
    {
        private readonly ISqlSugarRepository<SysLogOp> _repo;

        public SysLogOpService(ISqlSugarRepository<SysLogOp> repo)
        {
            _repo = repo;
        }

        /// <summary>
        /// 分页查询操作日志(默认 Id 倒序=最新在前,与登录日志/字典等管理页同口径;
        /// Account 精确匹配、ControllerName 模糊包含、时间范围按 CreateTime 闭区间)。
        /// </summary>
        public async Task<PageResult<SysLogOpOutput>> PageAsync(PageSysLogOpInput input)
        {
            // 过滤项先规范化(Trim):空白/全空格视同不过滤;先落局部变量再进表达式(零警告口径,同 SysLogLoginService)
            var account = input.Account?.Trim();
            var controller = input.ControllerName?.Trim();
            RefAsync<int> total = 0;
            var list = await _repo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(account), l => l.Account == account)
                .WhereIF(!string.IsNullOrWhiteSpace(controller), l => l.ControllerName!.Contains(controller!))
                .WhereIF(input.Success.HasValue, l => l.Success == input.Success)
                .WhereIF(input.StartTime.HasValue, l => l.CreateTime >= input.StartTime)
                .WhereIF(input.EndTime.HasValue, l => l.CreateTime <= input.EndTime)
                .ApplyDynamicSort(input.OrderBy, input.Desc, l => l.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<SysLogOpOutput>
            {
                Total = total,
                List = list
                    .Select(l => new SysLogOpOutput(
                        l.Id, l.ControllerName, l.ActionName, l.HttpMethod, l.RemoteIp,
                        l.ElapsedMs, l.Account, l.Success, l.CreateTime))
                    .ToList()
            };
        }

        /// <summary>
        /// 详情(全字段,含 RequestParam 请求参数/ReturnResult 返回结果两大字段;批 AZ 参照口径——
        /// 列表投影 PageAsync 已排除两大字段,避免大字段随分页加载,详情单独取全量)。不存在返回 AppException。
        /// </summary>
        public async Task<SysLogOpDetailOutput> GetDetailAsync(long id)
        {
            var log = await _repo.GetFirstAsync(l => l.Id == id);
            if (log is null)
            {
                throw AppException.Of(ErrorCodes.LogOpNotFound);
            }

            return new SysLogOpDetailOutput(
                log.Id, log.ControllerName, log.ActionName, log.HttpMethod, log.RemoteIp,
                log.ElapsedMs, log.Account, log.Success, log.RequestParam, log.ReturnResult, log.CreateTime);
        }
    }
}
