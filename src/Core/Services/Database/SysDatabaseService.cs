using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Database.Dto;
using SqlSugar;

namespace AdminCore.Core.Services.Database
{
    /// <summary>
    /// 数据库管理服务(3.8,平台裁剪·**只读**):表清单 / 列结构 / 索引三只读查询。
    /// </summary>
    /// <remarks>
    /// 范围裁决(本批):参照仓 SysDatabaseService 含建表/删表/执行 SQL/备份/还原等危险操作,
    /// 本仓**只移植只读部分**——理由是「最小权限 + 生产安全」:DDL/备份/还原需要实例级权限与外部进程
    /// (pg_dump/pg_restore),在 Web 端点暴露等于把库管理权交给 HTTP 面,且与"框架只做支撑"的定位不符;
    /// 运维实际由 DBA 用 psql 完成(沿用本仓 3.8 原判"dev 直接 psql"口径)。危险操作留白已登记(gap 清单/功能清单)。
    /// 实现口径:全部经 <c>Ado.SqlQueryAsync&lt;T&gt;</c> 执行**只读系统目录查询**(pg_catalog/information_schema),
    /// 表名一律作为**参数值**传入(无标识符拼接),无 SQL 注入面;查不到表 → 业务异常(不返回空集误导)。
    /// 库选择:走注入的 ISqlSugarClient(主库)——数据库管理面向主库 schema(current_schema)。
    /// </remarks>
    [Service]
    public class SysDatabaseService
    {
        private readonly ISqlSugarClient _db;

        public SysDatabaseService(ISqlSugarClient db)
        {
            _db = db;
        }

        /// <summary>
        /// 表清单(按占用大小倒序):keyword 非空时按表名/表注释模糊过滤(大小写不敏感)。
        /// 行数取统计估算(n_live_tup),大小含索引与 TOAST;索引数与索引清单端点**同源**(pg_index)。
        /// </summary>
        /// <remarks>
        /// 口径局限(登记):仅列 PG 普通表(relkind='r')——**原生分区表('p')/物化视图('m')/外部表('f')不在清单内**
        /// (本仓分表由 SqlSugar 表名后缀实现,不产生 PG 原生分区,当前无影响;若将来引入原生分区需扩展 relkind 集合)。
        /// </remarks>
        public async Task<List<DbTableOutput>> ListTablesAsync(string? keyword)
        {
            var kw = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
            return await _db.Ado.SqlQueryAsync<DbTableOutput>(
                """
                SELECT c.relname AS "TableName",
                       obj_description(c.oid) AS "Comment",
                       COALESCE(s.n_live_tup, 0) AS "RowEstimate",
                       pg_size_pretty(pg_total_relation_size(c.oid)) AS "TotalSize",
                       (SELECT COUNT(*) FROM pg_index i WHERE i.indrelid = c.oid) AS "IndexCount"
                  FROM pg_class c
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                  LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
                 WHERE n.nspname = current_schema()
                   AND c.relkind = 'r'
                   AND (@kw::text IS NULL
                        OR c.relname ILIKE '%' || @kw || '%'
                        OR COALESCE(obj_description(c.oid), '') ILIKE '%' || @kw || '%')
                 ORDER BY pg_total_relation_size(c.oid) DESC, c.relname
                """,
                new SugarParameter("@kw", kw));
        }

        /// <summary>
        /// 列结构(按序位):表不存在或无可见列 → 业务异常。
        /// 长度口径:仅字符型(varchar/bpchar)给 MaxLength(atttypmod-4),其余类型为 null。
        /// </summary>
        public async Task<List<DbColumnOutput>> ListColumnsAsync(string table)
        {
            var tableName = RequireTableName(table);

            var columns = await _db.Ado.SqlQueryAsync<DbColumnOutput>(
                """
                SELECT a.attname AS "ColumnName",
                       format_type(a.atttypid, a.atttypmod) AS "DataType",
                       (NOT a.attnotnull) AS "IsNullable",
                       CASE WHEN a.atttypid IN ('varchar'::regtype, 'bpchar'::regtype) AND a.atttypmod > 4
                            THEN a.atttypmod - 4 ELSE NULL END AS "MaxLength",
                       COALESCE((SELECT TRUE FROM pg_index i
                                  WHERE i.indrelid = a.attrelid AND i.indisprimary
                                    AND a.attnum = ANY(i.indkey)), FALSE) AS "IsPrimaryKey",
                       col_description(a.attrelid, a.attnum) AS "Comment",
                       a.attnum AS "OrdinalPosition"
                  FROM pg_attribute a
                  JOIN pg_class c ON c.oid = a.attrelid
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = current_schema()
                   AND c.relname = @table
                   AND a.attnum > 0
                   AND NOT a.attisdropped
                 ORDER BY a.attnum
                """,
                new SugarParameter("@table", tableName));

            if (columns.Count == 0)
            {
                throw AppException.With(ErrorCodes.DatabaseTableNotFound, "table", tableName);
            }

            return columns;
        }

        /// <summary>索引清单(主键索引在前,再按索引名):表不存在 → 业务异常(与列结构同口径);无索引返回空表。</summary>
        public async Task<List<DbIndexOutput>> ListIndexesAsync(string table)
        {
            var tableName = RequireTableName(table);

            // 先校验表存在(空索引集是合法结果,但"表不存在"必须与"表无索引"区分——否则前端把错表名当无索引)
            var exists = await _db.Ado.SqlQueryAsync<int>(
                """
                SELECT COUNT(*)::int FROM pg_class c
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = current_schema() AND c.relname = @table AND c.relkind = 'r'
                """,
                new SugarParameter("@table", tableName));
            if (exists.Count == 0 || exists[0] == 0)
            {
                throw AppException.With(ErrorCodes.DatabaseTableNotFound, "table", tableName);
            }

            return await _db.Ado.SqlQueryAsync<DbIndexOutput>(
                """
                SELECT i.relname AS "IndexName",
                       pg_get_indexdef(x.indexrelid) AS "Definition",
                       x.indisunique AS "IsUnique",
                       x.indisprimary AS "IsPrimary"
                  FROM pg_index x
                  JOIN pg_class i ON i.oid = x.indexrelid
                  JOIN pg_class t ON t.oid = x.indrelid
                  JOIN pg_namespace n ON n.oid = t.relnamespace
                 WHERE n.nspname = current_schema() AND t.relname = @table
                 ORDER BY x.indisprimary DESC, i.relname
                """,
                new SugarParameter("@table", tableName));
        }

        /// <summary>表名入参校验(非空 + 长度上限;实际注入面由参数化查询消除,此处只拦明显非法入参)。</summary>
        private static string RequireTableName(string? table)
        {
            var tableName = table?.Trim();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw AppException.Of(ErrorCodes.DatabaseTableRequired);
            }

            if (tableName.Length > 128)
            {
                throw AppException.Of(ErrorCodes.DatabaseTableNameTooLong);
            }

            return tableName;
        }
    }
}