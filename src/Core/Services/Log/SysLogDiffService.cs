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
    /// 差异日志查询服务(批 X 5.4,参照 Admin.NET SysLogDiffService 移植):
    /// 分页(列表不载 BeforeData/AfterData 大字段,record 投影口径)+ 详情(全字段)。
    /// 写入侧不在本服务——SqlSugar <c>Aop.OnDiffLogEvent</c> 钩子(SqlSugarSetup,
    /// 开关 <c>DbSettings:EnableDiffLog</c>)每次写操作自动落库,本服务零感知。
    /// </summary>
    [Service]
    public class SysLogDiffService
    {
        private readonly ISqlSugarRepository<SysLogDiff> _repo;

        public SysLogDiffService(ISqlSugarRepository<SysLogDiff> repo)
        {
            _repo = repo;
        }

        /// <summary>
        /// 分页查询差异日志(Id 倒序=最新在前;CreateTime 闭区间过滤——与异常日志同口径)。
        /// 大字段口径:投影排除 BeforeData/AfterData(参照 Page 的 IgnoreColumns 同义)。
        /// </summary>
        public async Task<PageResult<PageLogDiffOutput>> PageAsync(PageLogDiffInput input)
        {
            RefAsync<int> total = 0;
            var list = await _repo.AsQueryable()
                .WhereIF(input.StartTime.HasValue, l => l.CreateTime >= input.StartTime)
                .WhereIF(input.EndTime.HasValue, l => l.CreateTime <= input.EndTime)
                .ApplyDynamicSort(input.OrderBy, input.Desc, l => l.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<PageLogDiffOutput>
            {
                Total = total,
                List = list
                    .Select(l => new PageLogDiffOutput(
                        l.Id, l.BusinessData, l.DiffType, l.Elapsed,
                        l.CreateUserName, l.CreateTime, l.TenantId))
                    .ToList()
            };
        }

        /// <summary>详情(全字段,含 BeforeData/AfterData 大字段);不存在或已软删返回 AppException。</summary>
        public async Task<SysLogDiffDetailOutput> GetDetailAsync(long id)
        {
            var log = await _repo.GetFirstAsync(l => l.Id == id);
            if (log is null)
            {
                throw AppException.Of(ErrorCodes.LogDiffNotFound);
            }

            return new SysLogDiffDetailOutput(
                log.Id, log.BeforeData, log.AfterData, log.Sql, log.Parameters,
                log.BusinessData, log.DiffType, log.Elapsed,
                log.CreateUserName, log.CreateTime, log.TenantId);
        }
    }
}
