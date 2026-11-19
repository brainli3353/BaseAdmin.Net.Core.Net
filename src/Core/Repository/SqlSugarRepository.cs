using System.Linq.Expressions;
using System.Reflection;
using AdminCore.Core.Entities;
using AdminCore.Core.Models;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace AdminCore.Core.Repository
{
    /// <summary>
    /// 通用仓储:继承 SqlSugar 内置 SimpleClient(即得 CRUD),叠加项目软删语义、统一分页与多库通用选库。
    /// 注册为 Scoped(每请求一个实例),底层 ISqlSugarClient 为全局单例(连接由 SqlSugarScope 自管)。
    /// </summary>
    /// <remarks>
    /// 多库通用选库(task_008,对齐 baseadmin 仓储按特性选库):构造时按实体特性把 CRUD 绑定到目标连接——
    /// [Tenant] → 租户库(基座期落主库,租户库懒加载后补)、[LogTable] → 日志库(未配置回落主库)、
    /// [SysTable]/无特性 → 主库。业务代码无感知:注入 ISqlSugarRepository&lt;SysLogOp&gt; 即自动写日志库。
    /// </remarks>
    /// <typeparam name="T">实体类型(带无参构造的 class)。</typeparam>
    public class SqlSugarRepository<T> : SimpleClient<T>, ISqlSugarRepository<T> where T : class, new()
    {
        /// <summary>
        /// 差异日志开关(批 X 5.4):软删路径按此条件 opt-in EnableDiffLogEventIF——
        /// 开=软删落差异日志(对齐参照 FakeDelete 默认带);关=零额外开销(优于参照关时仍付前值查询)。
        /// 启动读取口径同 SqlSugarSetup(部署级决策,散读)。
        /// </summary>
        private readonly bool _enableDiffLog;

        /// <summary>
        /// 注入 SqlSugar 单例客户端,按实体特性选定目标连接后传给基类(SimpleClient 所有 CRUD 都走此连接)。
        /// </summary>
        /// <remarks>
        /// config 可选(批 X):宿主未注册 IConfiguration 时(存量测试容器)DI 回落默认 null → 开关视为关,
        /// 软删 opt-in 不启用——既有容器零波及;生产宿主 Web.Entry 已注册 IConfiguration 正常生效。
        /// </remarks>
        public SqlSugarRepository(ISqlSugarClient db, IConfiguration? config = null)
            : base(SelectConnection(db))
        {
            _enableDiffLog = config?.GetValue("DbSettings:EnableDiffLog", false) ?? false;
        }

        /// <summary>
        /// 实体 → 目标连接:与 SqlSugarSetup.SelectConfigIdFor 同口径(建表/种子/仓储单点);
        /// 目标即当前连接时直接复用(无额外开销),否则经 SqlSugarSetup 单点取逐连接独立 scope
        /// (见 SqlSugarSetup.ConnectionClients:多连接 scope 的 GetConnection 不继承 AOP 配置)。
        /// </summary>
        private static ISqlSugarClient SelectConnection(ISqlSugarClient db)
        {
            var configId = SqlSugarSetup.SelectConfigIdFor(db, typeof(T));
            // WHY as string:SqlSugar 的 ConnectionConfig.ConfigId 声明为 object(兼容长整型库标识),本项目约定恒为 string
            return string.Equals(configId, db.CurrentConnectionConfig.ConfigId as string, StringComparison.OrdinalIgnoreCase)
                ? db
                : SqlSugarSetup.GetConnectionById(db, configId);
        }

        /// <summary>
        /// 取实体的乐观锁版本属性(仅 int/int? 有效,其余返回 null=非乐观锁实体)。
        /// </summary>
        private static PropertyInfo? GetVerProperty(string verFieldName)
        {
            var prop = typeof(T).GetProperty(verFieldName);
            if (prop == null)
            {
                return null;
            }

            var type = prop.PropertyType;
            return type == typeof(int) || type == typeof(int?) ? prop : null;
        }

        /// <summary>
        /// 并发安全更新(批 K 3.1,参照 Admin.NET UpdateWithVerAsync 移植,裁决见 g5 §5):
        /// 实体带 int/int? 的 Ver 属性 → 单语句原子"版本匹配校验＋自增"
        /// (UPDATE ... SET Ver = Ver + 1, <其余列> WHERE 主键 AND Ver = 读回版本),
        /// 无 check-then-update 竞态窗口;无 Ver 属性 → 回落普通整对象更新(调用方无感知)。
        /// 返回 false=并发冲突或记录不存在(正常业务分支,不抛异常)。
        /// </summary>
        public async Task<bool> UpdateWithVerAsync(T input, string verFieldName = "Ver")
        {
            ArgumentNullException.ThrowIfNull(input);
            var verProperty = GetVerProperty(verFieldName);
            if (verProperty == null)
            {
                return await AsUpdateable(input).ExecuteCommandAsync() > 0;
            }

            object? originalVerObject = verProperty.GetValue(input);
            object originalVer;
            if (originalVerObject == null)
            {
                originalVer = verProperty.PropertyType == typeof(int)
                    ? 0   // int 型 null 按 0(实体新建未读回的语义;int? 型 null=未读回版本,静默按 0 会退化成无条件更新,须显式拒绝)
                    : throw new ArgumentException($"执行并发安全更新时{verFieldName}不能为空。");
            }
            else
            {
                originalVer = originalVerObject;
            }

            var param = Expression.Parameter(typeof(T), "it");
            var verMember = Expression.Property(param, verProperty);

            // 左表达式:it => (object)it.Ver
            var leftBody = Expression.Convert(verMember, typeof(object));
            var leftExpr = Expression.Lambda<Func<T, object>>(leftBody, param);

            // 右表达式:it => (object)(it.Ver + 1)(int? 先 .Value + 1 再转回 int?)
            Expression rightBody;
            if (verProperty.PropertyType == typeof(int?))
            {
                var valueAccess = Expression.Property(verMember, "Value");
                var added = Expression.Add(valueAccess, Expression.Constant(1, typeof(int)));
                rightBody = Expression.Convert(Expression.Convert(added, typeof(int?)), typeof(object));
            }
            else
            {
                rightBody = Expression.Convert(Expression.Add(verMember, Expression.Constant(1, typeof(int))), typeof(object));
            }
            var rightExpr = Expression.Lambda<Func<T, object>>(rightBody, param);

            // Where 条件:it.Ver == originalVer
            var constExpr = Expression.Constant(originalVer, verMember.Type);
            var whereBody = Expression.Equal(verMember, constExpr);
            var whereLambda = Expression.Lambda<Func<T, bool>>(whereBody, param);

            return await AsUpdateable(input)
                .PublicSetColumns(leftExpr, rightExpr)
                .Where(whereLambda)
                .ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 按主键删除 = 软删(覆盖 SimpleClient 的物理删除)。
        /// WHY 重写:项目软删口径(task_003 定稿)= 更新 IsDelete 为 true 保留物理行——
        /// 全局软删查询过滤器与审计三元组(人/时间/标记)都依赖物理行留存,物理删除会破坏审计。
        /// 走整对象 UpdateAsync(而非列级 SetColumns):AOP DataExecuting 靠 EntityValue 填 DeleteTime,
        /// 列级更新无实体对象,删除时间会丢失。
        /// 未实现 IDeletedFilter 的实体回退基类物理删除(框架不强制软删,口径由实体自声明)。
        /// </summary>
        public override async Task<bool> DeleteByIdAsync(object id)
        {
            if (typeof(T).GetInterfaces().Contains(typeof(IDeletedFilter)))
            {
                // 按主键读回(经全局软删过滤器:已软删的行读不到 → 返回 false,幂等)
                var entity = await AsQueryable().In(new object[] { id }).FirstAsync();
                if (entity == null)
                {
                    return false;
                }

                ((IDeletedFilter)entity).IsDelete = true;
                // 批 X 5.4:软删差异日志 opt-in(对齐参照 FakeDelete 默认带);EnableDiffLogEventIF 按开关
                // 条件启用——关时不付前值查询开销(优于参照关时仍无条件 opt-in)。
                // 整对象 Updateable 与 UpdateAsync 同口径:AOP DataExecuting 靠 EntityValue 填 DeleteTime,
                // IsOnlyIgnoreUpdate 列特性同样生效。
                return await Context.Updateable(entity)
                    .EnableDiffLogEventIF(_enableDiffLog, null)
                    .ExecuteCommandAsync() > 0;
            }

            return await base.DeleteByIdAsync(id);
        }

        /// <summary>
        /// 条件物理删除(接口口径见 ISqlSugarRepository:中间表清理专用,绕过软删)。
        /// WHY 经 Context:SimpleClient 本身不是完整客户端(仅 ISugarRepository),
        /// Context 即构造时按特性选定的目标连接,Deleteable 走该连接,与其余 CRUD 同库口径。
        /// 走 Deleteable 而非 Updateable:物理删不触发软删 AOP 的 IsDelete 回填,直接 DELETE 语句。
        /// </summary>
        public Task<int> DeletePhysicalByAsync(Expression<Func<T, bool>> expression)
        {
            return Context.Deleteable<T>().Where(expression).ExecuteCommandAsync();
        }

        /// <summary>分页查询:返回统一 PageResult<T>(总条数 + 当前页列表,全局软删过滤器自动生效)。</summary>
        /// <param name="pageIndex">页码,从 1 开始。</param>
        /// <param name="pageSize">每页条数。</param>
        public async Task<PageResult<T>> ToPagedListAsync(int pageIndex, int pageSize)
        {
            RefAsync<int> total = 0;
            var list = await AsQueryable().ToPageListAsync(pageIndex, pageSize, total);
            return new PageResult<T> { Total = total, List = list };
        }

        /// <summary>
        /// 分表查询入口(批 AC 3.1):返回按分表策略展开的查询对象(跨子表的 UNION 由 SqlSugar 生成)。
        /// 仅对贴 <c>[SplitTable]</c> 的实体有效——非分表实体调用由 SqlSugar 直接抛异常(能力边界显式暴露,不静默退化为单表)。
        /// 走 Context(构造时按特性选定的目标连接),与其余 CRUD 同库口径。
        /// </summary>
        public ISugarQueryable<T> AsSplitQueryable() => Context.Queryable<T>().SplitTable();

        /// <summary>
        /// 分表插入(批 AC 3.1):按实体内的时间分表字段路由到对应子表(子表不存在时由 SqlSugar 自动创建)。
        /// 审计字段/雪花主键仍经 AOP 自动填充(AOP 挂在连接上,与是否分表无关)。
        /// </summary>
        public Task<int> InsertSplitAsync(T entity) => Context.Insertable(entity).SplitTable().ExecuteCommandAsync();

        /// <summary>实体是否分表实体(批 AC 3.1:贴 <c>[SplitTable]</c>);供调用方按能力分支,避免对非分表实体误调分表入口。</summary>
        public static bool IsSplitTableEntity => SqlSugarSetup.IsSplitTableEntity(typeof(T));
    }
}
