using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Filters;
using SqlSugar;

namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 工作单元/事务特性(批 D 3.2):标注在 Controller 端点上,声明该端点内全部主库 DB 操作处于
    /// 同一个本地事务(全部成功才提交;任一环节抛异常 → 整体回滚)。
    /// 适用场景:「先清后挂」全量替换、级联删除、跨表多写——中途失败不留半成品。
    /// </summary>
    /// <remarks>
    /// 判定与执行由全局 UnitOfWorkFilter 承担(本文件同居,仿 MenuPermAttribute 契约件单文件口径);
    /// 无标注的端点直通零开销(不产生 BeginTran 往返)。
    /// G7 已知限制:事务期间主库连接被占用至 commit/rollback(事务时长≈端点时长),
    /// 事务须保持短小(只做 DB 操作;缓存失效等旁路副作用不参与回滚)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class UnitOfWorkAttribute : Attribute
    {
    }

    /// <summary>
    /// 工作单元/事务全局 ActionFilter(批 D 3.2):端点标注 [UnitOfWork] → 主库连接 UseTranAsync 事务包裹 →
    /// 执行 action → 成功 CommitTran / 异常 RollbackTran 后重抛(由 ExceptionMiddleware 统一转 ApiResult)。
    /// </summary>
    /// <remarks>
    /// 连接口径:ISqlSugarClient 单例(多库主 scope),UseTranAsync 事务作用于当前主库连接;
    /// 请求内主库实体 CRUD 走同一连接(同一事务);独立日志库实体走各自连接不参与回滚
    /// (日志 append-only,不参与回滚语义合理)。
    /// 检测方法:遍历 context.ActionDescriptor.EndpointMetadata 取本特性(与 MenuPermFilter 同路径——
    /// WHY 手动遍历而非 GetMetadata<T>() 扩展:该扩展在 Core 工程(仅 FrameworkReference)下解析不可达,
    /// 直接类型匹配遍历等价且零依赖歧义;特性路由下 EndpointMetadata 由路由系统填充)。
    /// 注册:Program.cs MvcOptions Filters——排 MenuPermFilter 之内层(权限 403 短路时不开事务)。
    /// </remarks>
    [Service(ServiceType = typeof(UnitOfWorkFilter), Lifetime = ServiceLifetime.Transient)]
    public sealed class UnitOfWorkFilter : IAsyncActionFilter
    {
        private readonly ISqlSugarClient _db;

        public UnitOfWorkFilter(ISqlSugarClient db)
        {
            _db = db;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            // 无 [UnitOfWork] 标注 → 直通(零开销:无 BeginTran 往返)
            UnitOfWorkAttribute? attr = null;
            foreach (var metadata in context.ActionDescriptor.EndpointMetadata)
            {
                if (metadata is UnitOfWorkAttribute u)
                {
                    attr = u;
                    break;
                }
            }
            if (attr is null)
            {
                await next();
                return;
            }

            // UseTranAsync 是 SqlSugarScope 实例方法(5.1.4.217 的 ISqlSugarClient 接口无事务成员)——
            // DI 单例运行期类型即 SqlSugarScope(AddSqlSugar 工厂返回),模式匹配取实例;
            // 非 SqlSugarScope(理论上不可能)→ 降级直通并记日志语义:直接执行不包事务。
            if (_db is not SqlSugarScope tranScope)
            {
                await next();
                return;
            }

            // UseTranAsync(官方事务包裹):成功 Commit / 内部异常 Rollback 并吞异常(ErrorException 携带)。
            // WHY 不用裸 Ado.BeginTran/CommitTran:Commit 路径会在线程执行上下文残留连接状态、污染后续事务
            //(实证:前驱 Commit 后,次事务 insert 自动提交、回滚失效;UseTranAsync 探针连续混合成功/失败全对)。
            var result = await tranScope.UseTranAsync(async () =>
            {
                var executed = await next();
                // MVC 管道口径:action 异常被捕获进 executed.Exception 而非抛出——
                // 将「未处理异常」转为真实抛出以触发回滚(已处理异常视为成功)
                if (executed.Exception is not null && !executed.ExceptionHandled)
                {
                    throw executed.Exception;
                }
                return executed;
            }, _ => { });

            if (result.IsSuccess)
            {
                return; // 已提交
            }

            // 已回滚 → 重抛原异常(外层 OpLog 记失败 / ExceptionMiddleware 统一转 ApiResult,口径不变)
            throw result.ErrorException ?? new InvalidOperationException("[UnitOfWork] 事务回滚(未知异常)");
        }
    }
}
