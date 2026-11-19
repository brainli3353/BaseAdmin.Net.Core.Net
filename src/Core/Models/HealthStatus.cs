namespace AdminCore.Core.Models
{
    /// <summary>
    /// /health 就绪探针结果映射(批 S-G B4a,纯函数零 I/O):把"依赖就绪度 → HTTP 状态/响应体"的判定
    /// 从 Program.cs 抽出到本类,便于零库单测覆盖三态;Program.cs 只做探测接线(实探 DB/缓存)。
    /// </summary>
    /// <remarks>
    /// live/ready 语义分离:/health/live=存活探针(静态恒 200,仅证进程活着);/health=就绪探针(实探依赖)。
    /// 降级响应**不携带异常原文/连接串**:降级档两依赖字段(db/cache)固定为枚举词 ok|fail,缓存类型(memory|redis)
    /// 只在双就绪(200)档透传——调用方传入的 cacheType 即使被污染(异常文本等)也不会出现在失败档响应里。
    /// </remarks>
    public static class HealthStatus
    {
        /// <summary>
        /// 就绪判定:db+缓存均就绪 → 200 {status:"ok",db:"ok",cache:"&lt;type&gt;"};
        /// 任一失败 → 503 {status:"degraded",db:"ok|fail",cache:"ok|fail"}。
        /// </summary>
        /// <param name="dbOk">DB 探活(select 1)是否成功</param>
        /// <param name="cacheOk">缓存探活是否成功</param>
        /// <param name="cacheType">缓存实现标识(memory|redis);仅双就绪(200 档)透传,降级档两依赖字段固定 ok|fail 枚举</param>
        public static ProbeResult ToResult(bool dbOk, bool cacheOk, string cacheType)
        {
            var ok = dbOk && cacheOk;
            return new ProbeResult(
                StatusCode: ok ? 200 : 503,
                Status: ok ? "ok" : "degraded",
                Db: dbOk ? "ok" : "fail",
                Cache: cacheOk ? (ok ? cacheType : "ok") : "fail");
        }

        /// <summary>探测结果载荷:StatusCode=HTTP 状态码;Status/Db/Cache 为响应体字段
        /// (值域:ok|degraded|fail|缓存类型,失败档不含异常原文)。</summary>
        public sealed record ProbeResult(int StatusCode, string Status, string Db, string Cache);
    }
}
