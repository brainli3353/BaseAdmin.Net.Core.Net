using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 数据底座唯一性约束的启动幂等落地(批 S-G B2b):逐条创建<strong>部分唯一索引</strong>
    /// <c>CREATE UNIQUE INDEX IF NOT EXISTS ... WHERE "IsDelete"=false</c>(PostgreSQL 语法)。
    /// </summary>
    /// <remarks>
    /// WHY 需要本件:dev 库实测 42 张表 0 个唯一约束,业务唯一性全靠代码 check-then-act
    /// (sys_user.Account / sys_role.Code / sys_pos.Code / sys_dict_type.Code / sys_open_access.AccessKey 等),
    /// 并发窗口内可插入重复行;[SugarIndex] 不支持 WHERE 子句,而软删语义要求
    /// 「同一键在未删行内唯一,软删后允许同键重建」,只有部分唯一索引能承载 ⇒ 自建启动幂等 DDL 机制。
    ///
    /// 执行口径(逐条独立,单条失败不阻断其余条目,更不阻断启动):
    /// ① 幂等判定:先查 pg_indexes(同 schema 同名)——已存在即跳过,不重复 CREATE;
    /// ② 审计在前:未删行内同键重复 ⇒ 该索引跳过 + LogError(列重复键样例前 3 条)。
    ///    WHY 先审计后建:存量已脏时 CREATE UNIQUE 必整体失败,逐条审计把爆炸面缩到"仅该表索引缺席",
    ///    其余干净表照常上约束;
    /// ③ 无重复才建索引(IF NOT EXISTS 双保险:审计与建索引间隙被他实例抢先时幂等不炸)。
    ///
    /// 异常口径:单条异常记 LogError 并入跳过清单后继续(唯一性缺失的兜底是服务层 check-then-act 现状行为,
    /// 索引是增强不是前置依赖——同 Program.cs「存活/就绪解耦」口径,启动可用性优先,fail-loud 可检索)。
    ///
    /// 边界(登记不整改):
    /// - 审计 GROUP BY 把 NULL 键视作同组(而 PG 部分唯一索引默认 NULLS DISTINCT 不视为重复)——
    ///   审计偏保守(宁可跳过不误建);实测各键列 NOT NULL、TenantId 存量 0 个 NULL,该差异当前不触发。
    /// - 索引名/表名/列名均为本文件内常量白名单,不拼接任何外部输入 ⇒ 无注入面。
    /// </remarks>
    public static class SchemaIntegrityInitializer
    {
        /// <summary>单条唯一索引定义(全部为代码常量;列名大小写按 PG 实际,Pascal 列带双引号)。</summary>
        /// <param name="IndexName">PG 索引名(全库唯一)。</param>
        /// <param name="TableName">目标表名。</param>
        /// <param name="KeyColumns">唯一键列清单(带引号的 PG 字面名)。</param>
        /// <param name="ExtraPredicate">"IsDelete"=false 之外的附加谓词(无则空串)。</param>
        private sealed record UniqueIndexSpec(string IndexName, string TableName, string[] KeyColumns, string ExtraPredicate)
        {
            /// <summary>WHERE 子句:未删行才参与唯一性(软删行不占键,允许同键重建——软删语义的核心)。</summary>
            public string WhereClause =>
                "\"IsDelete\"=false" + (ExtraPredicate.Length == 0 ? string.Empty : " AND " + ExtraPredicate);

            /// <summary>键列逗号串(直接进 CREATE INDEX 列清单 / GROUP BY)。</summary>
            public string ColumnList => string.Join(", ", KeyColumns);

            /// <summary>重复样例的键值拼接表达式(每列 COALESCE 保 NULL 可读,'|' 分隔成单列文本)。</summary>
            public string SampleKeyExpr =>
                string.Join(" || '|' || ", KeyColumns.Select(c => $"COALESCE({c}::text, '<NULL>')"));
        }

        /// <summary>
        /// 唯一索引清单(批 S-G B2b;每条列名/租户口径均已 psql 实读核对,实读为准):
        /// - EntityTenant 表(有 TenantId 列)唯一键含 TenantId = 租户内唯一;EntityBase 表全局唯一;
        /// - 派工清单原写 u_sys_dict_type_code 仅 Code,实读 SysDictType 继承 EntityTenant(有 TenantId 列)
        ///   ⇒ 按租户内唯一口径修正为 (Code, TenantId);sys_config 全局唯一(EntityBase);
        ///   sys_open_access 已纳管(EntityTenant),但 AccessKey 唯一键**按裁定保持全局**＝本口径的显式例外(见下)。
        /// </summary>
        private static readonly UniqueIndexSpec[] Specs =
        [
            // 登录账号:租户内唯一
            new UniqueIndexSpec("u_sys_user_account", "sys_user", ["\"Account\"", "\"TenantId\""], string.Empty),
            // 角色/岗位/字典类型编码:租户内唯一
            new UniqueIndexSpec("u_sys_role_code", "sys_role", ["\"Code\"", "\"TenantId\""], string.Empty),
            new UniqueIndexSpec("u_sys_pos_code", "sys_pos", ["\"Code\"", "\"TenantId\""], string.Empty),
            new UniqueIndexSpec("u_sys_dict_type_code", "sys_dict_type", ["\"Code\"", "\"TenantId\""], string.Empty),
            // 系统配置编码:全局唯一(EntityBase,无租户列)
            new UniqueIndexSpec("u_sys_config_code", "sys_config", ["\"Code\""], string.Empty),
            // 开放接口身份 AccessKey:全局唯一(显式例外——t846 批 2 起本表已是 EntityTenant 表,
            // 但唯一键按裁定**不**含 TenantId,属上方「EntityTenant 表唯一键含 TenantId」口径的例外)。
            // WHY:AccessKey 业务语义是**全局定位**(API 调用方持 key 来换身份,此时未认证、不知道租户);
            // 改成 (AccessKey, TenantId) 会让 key 变租户内唯一 ⇒ 同一 key 可存在多租户,
            // 认证按 key 等值查询可能命中他租户身份 ＝ 提权级风险。故本 spec 保持全局,勿改。
            new UniqueIndexSpec("u_sys_open_access_accesskey", "sys_open_access", ["\"AccessKey\""], string.Empty),
            // 注:sys_menu.Permission **不建全局唯一索引**(2026-10-26 全量回归实证后移除)——
            // 框架既有域规则是「同父内按钮 Permission 判重」(SysMenuService.ValidateNewNodeAsync ③),
            // 不同父节点复用同名权限键是合法配置;全局 (Permission,TenantId) 唯一会过度约束,
            // 实测使 BatchBIMenuTest 三个用例插入同父不同批的探针按钮时撞 23505。
        ];

        /// <summary>
        /// 幂等确保全部唯一索引落地(审计先行,重复/异常跳过并响亮告警,绝不抛出、不阻断启动)。
        /// </summary>
        /// <param name="db">SqlSugar 客户端(需已指向目标 PG 库;表须已建,调用方保证在 InitTables 之后)。</param>
        /// <param name="logger">日志器(已存在/已建记 Information,重复/异常记 Error——fail-loud 可检索)。</param>
        /// <returns>三类清单(已建/已存在/跳过),供测试与启动日志断言。</returns>
        public static async Task<SchemaIntegrityResult> EnsureIndexesAsync(ISqlSugarClient db, ILogger logger)
        {
            var result = new SchemaIntegrityResult();

            foreach (var spec in Specs)
            {
                try
                {
                    // ① 幂等判定:pg_indexes 同 schema 同名即视为已存在,不重复 CREATE
                    var exists = await db.Ado.GetIntAsync(
                        $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname = '{spec.IndexName}'");
                    if (exists > 0)
                    {
                        result.AlreadyExistingIndexes.Add(spec.IndexName);
                        logger.LogInformation("唯一索引已存在,跳过: {Index} ON {Table}", spec.IndexName, spec.TableName);
                        continue;
                    }

                    // ② 审计在前:未删行内同键重复 ⇒ CREATE 必失败,该索引跳过 + LogError(样例前 3 条)
                    var dupGroups = await db.Ado.GetIntAsync(
                        $"SELECT COUNT(*) FROM (SELECT {spec.ColumnList} FROM {spec.TableName} WHERE {spec.WhereClause} " +
                        $"GROUP BY {spec.ColumnList} HAVING COUNT(*) > 1) t");
                    if (dupGroups > 0)
                    {
                        // 单列文本样例(COALESCE 保 NULL 可读),供排查存量脏数据
                        var samples = await db.Ado.SqlQueryAsync<string>(
                            $"SELECT {spec.SampleKeyExpr} FROM {spec.TableName} WHERE {spec.WhereClause} " +
                            $"GROUP BY {spec.ColumnList} HAVING COUNT(*) > 1 LIMIT 3");
                        var reason = $"存量重复 {dupGroups} 组,须先清理再建(样例: {string.Join(" ; ", samples)})";
                        result.SkippedIndexes.Add(new SchemaIntegritySkip { IndexName = spec.IndexName, SkipReason = reason });
                        logger.LogError("唯一索引【{Index}】({Table})未创建——{Reason}", spec.IndexName, spec.TableName, reason);
                        continue;
                    }

                    // ③ 无重复 ⇒ 建部分唯一索引(IF NOT EXISTS 双保险:并发他实例抢先时幂等不炸)
                    await db.Ado.ExecuteCommandAsync(
                        $"CREATE UNIQUE INDEX IF NOT EXISTS {spec.IndexName} ON {spec.TableName} ({spec.ColumnList}) " +
                        $"WHERE {spec.WhereClause}");
                    result.CreatedIndexes.Add(spec.IndexName);
                    logger.LogInformation("唯一索引已创建: {Index} ON {Table} ({Columns}) WHERE {Where}",
                        spec.IndexName, spec.TableName, spec.ColumnList, spec.WhereClause);
                }
                catch (Exception ex)
                {
                    // 单条失败不阻断其余条目与启动(fail-loud:Error 可检索,唯一性暂由服务层校验兜底)
                    result.SkippedIndexes.Add(new SchemaIntegritySkip
                    {
                        IndexName = spec.IndexName,
                        SkipReason = $"执行异常: {ex.Message}"
                    });
                    logger.LogError(ex, "唯一索引【{Index}】({Table})执行异常,已跳过(不阻断启动)", spec.IndexName, spec.TableName);
                }
            }

            logger.LogInformation(
                "唯一性底座完成:已建 {Created} / 已存在 {Existed} / 跳过 {Skipped}(共 {Total})",
                result.CreatedIndexes.Count,
                result.AlreadyExistingIndexes.Count,
                result.SkippedIndexes.Count,
                Specs.Length);
            return result;
        }
    }

    /// <summary>唯一性底座执行结果(批 S-G B2b):三类清单,供测试断言与启动日志汇总。</summary>
    public sealed class SchemaIntegrityResult
    {
        /// <summary>本次新建的索引名。</summary>
        public List<string> CreatedIndexes { get; } = new();

        /// <summary>先前已存在而跳过的索引名(幂等面)。</summary>
        public List<string> AlreadyExistingIndexes { get; } = new();

        /// <summary>未能落地的索引(存量重复/执行异常),附原因。</summary>
        public List<SchemaIntegritySkip> SkippedIndexes { get; } = new();
    }

    /// <summary>跳过条目:索引名 + 原因(重复组数与样例/异常消息)。</summary>
    public sealed class SchemaIntegritySkip
    {
        public string IndexName { get; init; } = string.Empty;

        public string SkipReason { get; init; } = string.Empty;
    }
}
