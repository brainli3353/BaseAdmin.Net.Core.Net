using System.Threading;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 租户执行上下文(批 AB 多租户库级隔离):当前执行流的租户 Id(AsyncLocal),供 [Tenant] 实体
    /// 的选库解析(SqlSugarSetup.SelectConfigIdFor)与审计填充在同一次调用链内取到"当前租户"。
    /// </summary>
    /// <remarks>
    /// WHY AsyncLocal 而非 IHttpContextAccessor:选库消费点在仓储构造(同步)与建表/种子单点(静态),
    /// 均不宜逐处注入 Http 依赖;AsyncLocal 随 async 调用链流动,HTTP 请求由 PermissionMiddleware
    /// 在认证后设定/请求结束清理,后台任务与单测经 <see cref="Begin"/> 显式切换——
    /// 同一套解析逻辑覆盖两种宿主(参照 AdminNetPro 从 Claim 直读的口径,本仓收敛为上下文单点)。
    /// WHY 不用 static 字段(非 AsyncLocal):并发请求各自租户不同,共享字段会互相覆盖(批 L/批 B
    /// 缓存串扰同族风险);AsyncLocal 天然按执行流隔离。
    /// WHY 不在此处查库/解密:上下文只承载"当前是谁",连接解析(懒加载/解密/fail-loud)归
    /// <see cref="TenantDbRegistry"/>,职责单一(约定 #11 复用优先:与仓储/AOP 选库共用同一解析结果)。
    /// </remarks>
    public static class TenantScope
    {
        private static readonly AsyncLocal<long?> CurrentHolder = new();

        /// <summary>当前执行流的租户 Id(null=无租户上下文:后台任务/单测/未认证请求)。</summary>
        public static long? Current => CurrentHolder.Value;

        /// <summary>
        /// 设定当前执行流的租户 Id(null=清理)。
        /// </summary>
        public static void Set(long? tenantId) => CurrentHolder.Value = tenantId;

        /// <summary>清理当前执行流的租户 Id(请求结束 finally 调用,防上下文泄漏到复用线程的下一请求)。</summary>
        public static void Clear() => CurrentHolder.Value = null;

        /// <summary>
        /// 显式切换租户上下文(后台任务/单测/跨租户运维脚本):<c>using var scope = TenantScope.Begin(tenantId);</c>
        /// 退出时恢复进入前的值(可嵌套)。
        /// </summary>
        public static IDisposable Begin(long tenantId)
        {
            var previous = CurrentHolder.Value;
            CurrentHolder.Value = tenantId;
            return new RestoreScope(previous);
        }

        /// <summary>上下文恢复句柄(仅内部使用)。</summary>
        private sealed class RestoreScope : IDisposable
        {
            private readonly long? _previous;

            public RestoreScope(long? previous) => _previous = previous;

            public void Dispose() => CurrentHolder.Value = _previous;
        }
    }
}