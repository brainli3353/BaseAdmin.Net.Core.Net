using System.Linq.Expressions;
using SqlSugar;

namespace AdminCore.Core.Repository
{
    /// <summary>
    /// 差异日志显式 opt-in 扩展(批 X 5.4,对齐参照仓 RepositoryExtension 的 WithDiffLog 家族)。
    /// WHY 显式:SqlSugar 5.1.4.217 的 OnDiffLogEvent 非默认触发——每次写操作需 IsEnableDiffLogEvent
    /// 开关(EnableDiffLogEvent/EnableDiffLogEventIF),全局钩子(DbSettings:EnableDiffLog)与每次
    /// opt-in 两者齐备才落差异日志。INSERT/UPDATE 的 opt-in 带一次前值/后值快照查询开销,故不作默认。
    /// 软删例外:仓储 DeleteByIdAsync 软删路径按开关自动 opt-in(对齐参照 FakeDelete 默认带)。
    /// enableDiffLog 由调用方读 DbSettings:EnableDiffLog 传入(与框架散读同口径;
    /// 传 false 或钩子未注册时与直接 CRUD 等价,零额外开销)。
    /// </summary>
    public static class DiffLogExtensions
    {
        /// <summary>插入(带差异日志 opt-in):开关开时落一条 INSERT 档差异日志。</summary>
        public static Task<T> InsertWithDiffLogAsync<T>(
            this ISqlSugarClient db, T entity, bool enableDiffLog, object? businessData = null)
            where T : class, new()
        {
            return enableDiffLog
                ? db.Insertable(entity).EnableDiffLogEvent(businessData).ExecuteReturnEntityAsync()
                : db.Insertable(entity).ExecuteReturnEntityAsync();
        }

        /// <summary>整对象更新(带差异日志 opt-in):开关开时落一条 UPDATE 档差异日志(前后整行快照)。</summary>
        public static Task<int> UpdateWithDiffLogAsync<T>(
            this ISqlSugarClient db, T entity, bool enableDiffLog, bool ignoreAllNullColumns = true, object? businessData = null)
            where T : class, new()
        {
            return enableDiffLog
                ? db.Updateable(entity)
                    .EnableDiffLogEvent(businessData)
                    .IgnoreColumns(ignoreAllNullColumns: ignoreAllNullColumns)
                    .ExecuteCommandAsync()
                : db.Updateable(entity)
                    .IgnoreColumns(ignoreAllNullColumns: ignoreAllNullColumns)
                    .ExecuteCommandAsync();
        }

        /// <summary>条件物理删除(带差异日志 opt-in):开关开时落一条 DELETE 档差异日志。</summary>
        public static Task<int> DeleteWithDiffLogAsync<T>(
            this ISqlSugarClient db, Expression<Func<T, bool>> expression, bool enableDiffLog, object? businessData = null)
            where T : class, new()
        {
            return enableDiffLog
                ? db.Deleteable<T>().Where(expression)
                    .EnableDiffLogEvent(businessData)
                    .ExecuteCommandAsync()
                : db.Deleteable<T>().Where(expression).ExecuteCommandAsync();
        }
    }
}
