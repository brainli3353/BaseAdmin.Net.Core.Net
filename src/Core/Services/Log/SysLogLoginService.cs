using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Core.Utils.DynamicQuery;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace AdminCore.Core.Services.Log
{
    /// <summary>
    /// 登录日志服务(task_021 契约 §2/§3):Page(分页 + Account/时间范围/Status 过滤)/ LogAsync(登录流程写入)/
    /// Clear(清理 N 天前数据,N 从配置读取默认 30)。
    /// </summary>
    /// <remarks>
    /// 契约 §3 集成点预留:本任务不改 AuthService、不做 DI 注册(Program.cs 在交付限域外)——
    /// 后续接入任务在 Program.cs 注册本服务(作用域,依赖仓储/配置)并在登录/登出时机调用 LogAsync;
    /// 本类接口即接入面,签名以此为准。
    /// Clear 保留天数经 IConfiguration 显式配置节 SysLogLogin:RetentionDays 读取,缺省 30——
    /// 配置文件零改动也可用(默认值兜底),运维调整无需改码。
    /// </remarks>
    [Service]
    public class SysLogLoginService
    {
        private readonly ISqlSugarRepository<SysLogLogin> _repo;
        private readonly IConfiguration _configuration;

        public SysLogLoginService(ISqlSugarRepository<SysLogLogin> repo, IConfiguration configuration)
        {
            _repo = repo;
            _configuration = configuration;
        }

        /// <summary>
        /// 分页查询登录日志(默认 Id 倒序=最新在前,与字典/角色等管理页同口径;
        /// Account 精确匹配、时间范围按 CreateTime 闭区间、Status 可选过滤)。
        /// </summary>
        public async Task<PageResult<SysLogLoginOutput>> PageAsync(PageSysLogLoginInput input)
        {
            // 账号过滤先规范化(Trim):空白/全空格视同不过滤,非空值精确匹配;
            // 先落局部变量再进表达式,避免 WhereIF 表达式内对可空引用解引用(零警告口径)
            var account = input.Account?.Trim();
            // 批 UX:LoginType 按实体列真实类型(string?)精确匹配;先 Trim 落局部变量(零警告口径)
            var loginType = input.LoginType?.Trim();
            RefAsync<int> total = 0;
            var list = await _repo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(account), l => l.Account == account)
                .WhereIF(input.StartTime.HasValue, l => l.CreateTime >= input.StartTime)
                .WhereIF(input.EndTime.HasValue, l => l.CreateTime <= input.EndTime)
                .WhereIF(input.Status.HasValue, l => l.Status == input.Status)
                .WhereIF(!string.IsNullOrWhiteSpace(loginType), l => l.LoginType == loginType)
                .ApplyDynamicSort(input.OrderBy, input.Desc, l => l.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);
            return new PageResult<SysLogLoginOutput>
            {
                Total = total,
                List = list
                    .Select(l => new SysLogLoginOutput(
                        l.Id, l.Account, l.RemoteAddr, l.Browser, l.Os,
                        l.Status, l.Message, l.LoginType, l.CreateTime))
                    .ToList()
            };
        }

        /// <summary>
        /// 登录日志写入(契约 §3 集成点预留,供 AuthService 登录/登出时机调用)。
        /// 业务字段由调用方填全;主键/CreateTime/CreateUser/Tenant 由 AOP 自动填充(手册 §3.4),本方法零审计赋值。
        /// </summary>
        public Task<bool> LogAsync(SysLogLogin entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            return _repo.InsertAsync(entity);
        }

        /// <summary>
        /// 清理 N 天前的登录日志(N 读配置 SysLogLogin:RetentionDays,缺省 30),返回物理删除行数。
        /// </summary>
        /// <remarks>
        /// WHY 物理删而非软删:留存语义是"行消失"——软删只翻 IsDelete 标记,行仍占表,
        /// 留存目标落空;过留存期的日志行审计价值已消费完毕,无保留必要。
        /// 口径复用仓储 DeletePhysicalByAsync(条件物理删,绕过软删过滤器)。
        /// </remarks>
        public async Task<int> ClearAsync()
        {
            // GetValue 缺省 30(契约口径);配置节不存在/为空时取 30,配置文件零改动也可用
            var days = _configuration.GetValue("SysLogLogin:RetentionDays", 30);
            // WHY 非正数回落:配置误写 0/负数会把截止时间推到现在/未来,等同全表清除——
            // 日志清理是运维低频操作,误配置的破坏面大,用默认值兜底优于按字面值执行
            if (days <= 0)
            {
                days = 30;
            }

            var cutoff = DateTime.Now.AddDays(-days);
            return await _repo.DeletePhysicalByAsync(l => l.CreateTime < cutoff);
        }
    }
}
