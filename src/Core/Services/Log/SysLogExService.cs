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
    /// 异常日志查询服务(5.1 异常档查询侧):分页(列表不载大字段)+ 详情(全字段)。
    /// 写入侧不在本服务——ExceptionMiddleware 捕获未处理异常发布 ex_log 事件,
    /// 事件总线订阅者(ExLogSubscriber)异步落库 SysLogEx([LogTable] 多库通用选库,本服务零感知)。
    /// </summary>
    [Service]
    public class SysLogExService
    {
        private readonly ISqlSugarRepository<SysLogEx> _repo;

        public SysLogExService(ISqlSugarRepository<SysLogEx> repo)
        {
            _repo = repo;
        }

        /// <summary>
        /// 分页查询异常日志(默认 Id 倒序=最新在前;Account 精确、ControllerName 模糊包含、RemoteIp 精确、
        /// 时间范围按 CreateTime 闭区间——与 SysLogOp 同口径)。
        /// 大字段口径:投影排除 Exception/RequestParam/ReturnResult 三 text 大字段(参照 IgnoreColumns)。
        /// </summary>
        public async Task<PageResult<SysLogExOutput>> PageAsync(PageSysLogExInput input)
        {
            var account = input.Account?.Trim();
            var controller = input.ControllerName?.Trim();
            var remoteIp = input.RemoteIp?.Trim();
            // 批 UX:LogLevel 按实体列真实类型(string?)精确匹配;先 Trim 落局部变量(零警告口径)
            var logLevel = input.LogLevel?.Trim();
            RefAsync<int> total = 0;
            var list = await _repo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(account), l => l.Account == account)
                .WhereIF(!string.IsNullOrWhiteSpace(controller), l => l.ControllerName!.Contains(controller!))
                .WhereIF(!string.IsNullOrWhiteSpace(remoteIp), l => l.RemoteIp == remoteIp)
                .WhereIF(!string.IsNullOrWhiteSpace(logLevel), l => l.LogLevel == logLevel)
                .WhereIF(input.StartTime.HasValue, l => l.CreateTime >= input.StartTime)
                .WhereIF(input.EndTime.HasValue, l => l.CreateTime <= input.EndTime)
                .ApplyDynamicSort(input.OrderBy, input.Desc, l => l.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<SysLogExOutput>
            {
                Total = total,
                List = list
                    .Select(l => new SysLogExOutput(
                        l.Id, l.ControllerName, l.ActionName, l.HttpMethod, l.RequestUrl, l.RemoteIp,
                        l.TraceId, l.ThreadId, l.Account, l.RealName, l.LogLevel, l.CreateTime))
                    .ToList()
            };
        }

        /// <summary>详情(全字段,含异常全文/请求参数/返回结果三大字段);不存在或已软删返回 AppException。</summary>
        public async Task<SysLogExDetailOutput> GetDetailAsync(long id)
        {
            var log = await _repo.GetFirstAsync(l => l.Id == id);
            if (log is null)
            {
                throw AppException.Of(ErrorCodes.LogExNotFound);
            }

            return new SysLogExDetailOutput(
                log.Id, log.ControllerName, log.ActionName, log.HttpMethod, log.RequestUrl, log.RemoteIp,
                log.RequestParam, log.ReturnResult, log.TraceId, log.ThreadId, log.Account, log.RealName,
                log.Exception, log.LogLevel, log.CreateTime);
        }
    }
}
