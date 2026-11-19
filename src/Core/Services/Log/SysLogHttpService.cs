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
    /// Http 出站日志查询服务(5.3 查询侧):分页(列表不载大字段)+ 详情(全字段)。
    /// 写入侧不在本服务——HttpLoggingHandler 记录对外调用后发布 http_log 事件,
    /// 事件总线订阅者(HttpLogSubscriber)异步落库 SysLogHttp([LogTable] 多库通用选库,本服务零感知)。
    /// </summary>
    [Service]
    public class SysLogHttpService
    {
        private readonly ISqlSugarRepository<SysLogHttp> _repo;

        public SysLogHttpService(ISqlSugarRepository<SysLogHttp> repo)
        {
            _repo = repo;
        }

        /// <summary>
        /// 分页查询出站日志(默认 Id 倒序=最新在前;Account 精确、Url 模糊包含、HttpMethod 精确、
        /// StatusCode 精确、时间范围按 CreateTime 闭区间——与 SysLogOp/SysLogEx 同口径)。
        /// 大字段口径:投影排除 RequestHeaders/RequestBody/ResponseBody/Exception(列表不载)。
        /// </summary>
        public async Task<PageResult<SysLogHttpOutput>> PageAsync(PageSysLogHttpInput input)
        {
            var account = input.Account?.Trim();
            var url = input.Url?.Trim();
            var httpMethod = input.HttpMethod?.Trim();
            RefAsync<int> total = 0;
            var list = await _repo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(account), l => l.Account == account)
                .WhereIF(!string.IsNullOrWhiteSpace(url), l => l.RequestUrl!.Contains(url!))
                .WhereIF(!string.IsNullOrWhiteSpace(httpMethod), l => l.HttpMethod == httpMethod)
                .WhereIF(input.StatusCode.HasValue, l => l.StatusCode == input.StatusCode)
                .WhereIF(input.StartTime.HasValue, l => l.CreateTime >= input.StartTime)
                .WhereIF(input.EndTime.HasValue, l => l.CreateTime <= input.EndTime)
                .ApplyDynamicSort(input.OrderBy, input.Desc, l => l.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<SysLogHttpOutput>
            {
                Total = total,
                List = list
                    .Select(l => new SysLogHttpOutput(
                        l.Id, l.HttpMethod, l.RequestUrl, l.StatusCode, l.ElapsedMs,
                        l.Account, l.RealName, l.TraceId, l.HasException, l.CreateTime))
                    .ToList()
            };
        }

        /// <summary>详情(全字段,含请求头/请求体/响应体/异常四大字段);不存在或已软删返回 AppException。</summary>
        public async Task<SysLogHttpDetailOutput> GetDetailAsync(long id)
        {
            var log = await _repo.GetFirstAsync(l => l.Id == id);
            if (log is null)
            {
                throw AppException.Of(ErrorCodes.LogHttpNotFound);
            }

            return new SysLogHttpDetailOutput(
                log.Id, log.HttpMethod, log.RequestUrl, log.StatusCode, log.ElapsedMs,
                log.Account, log.RealName, log.TraceId, log.HasException,
                log.RequestHeaders, log.RequestBody, log.ResponseBody, log.Exception, log.CreateTime);
        }
    }
}