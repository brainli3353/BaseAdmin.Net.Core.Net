using Npgsql;

namespace AdminCore.Core.Exceptions
{
    /// <summary>
    /// DB 冲突 → 业务错误码映射(批 S-G B2b):纯静态单件,无运行时依赖,管道各处可复用。
    /// </summary>
    /// <remarks>
    /// WHY 需要本件:并发插入重复唯一键时 PostgreSQL 抛 SQLSTATE 23505(unique_violation),
    /// 属「前一行已存在」的可预期业务态,应回业务冲突响应而非 500 兜底文案;
    /// 映射逻辑收敛单件,避免各处散写 SqlState 字符串漂移。
    /// </remarks>
    public static class DbConflictMapper
    {
        /// <summary>PostgreSQL unique_violation 的 SQLSTATE(唯一约束/唯一索引冲突)。</summary>
        public const string UniqueViolationSqlState = "23505";

        /// <summary>
        /// 尝试把异常映射为唯一约束冲突:沿 InnerException 链有限深度探测
        /// <c>PostgresException.SqlState == 23505</c>。
        /// WHY 沿内层链:SqlSugar 等封装层会把底层异常包一层,只认顶层会让映射在真实管道空转;
        /// 有限深度(而非无限递归)防异常链成环/超长。命中 ⇒ true 且 errCode = <see cref="ErrorCodes.Conflict"/>;
        /// 未命中 ⇒ false 且 errCode 为空串(out 语义要求必赋值)。
        /// </summary>
        public static bool TryMapUniqueViolation(Exception ex, out string errCode)
        {
            var current = ex;
            var depth = 0;
            while (current is not null && depth < MaxInnerDepth)
            {
                if (current is PostgresException pg &&
                    string.Equals(pg.SqlState, UniqueViolationSqlState, StringComparison.Ordinal))
                {
                    errCode = ErrorCodes.Conflict;
                    return true;
                }

                current = current.InnerException;
                depth++;
            }

            errCode = string.Empty;
            return false;
        }

        /// <summary>内层探测深度上限(实际包装层 1–2 层,8 已远超;防异常链成环时无限循环)。</summary>
        private const int MaxInnerDepth = 8;
    }
}
