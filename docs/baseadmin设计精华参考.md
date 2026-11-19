# baseadmin 设计精华参考（零 Furion 版）

> 来源：baseadmin(Admin.NET.Pro.HHBase) 核心设计提炼（2026-08-26 葫芦爷爷）。
> huluyeye-admin 是**原生 ASP.NET Core MVC 零 Furion**——本文档提炼 baseadmin 的**框架无关设计精华**（实体/ORM/仓储/鉴权/审计），Furion 特有部分（动态 API、AOP 特性、`App.` 服务定位）一律用原生替代。
> **写代码前必读**：本文档 + 对应任务书；ORM/实体/仓储用法必须与本文档一致。

## 一、实体基类分层设计

三层递进，业务实体按需继承：

```csharp
// ① 只有主键（雪花 long）
public abstract class EntityBaseId
{
    [SugarColumn(ColumnName = "Id", ColumnDescription = "主键Id", IsPrimaryKey = true, IsIdentity = false)]
    public virtual long Id { get; set; }
}

// ② 审计字段 + 软删（大多数实体继承这个）
public abstract class EntityBase : EntityBaseId, IDeletedFilter
{
    [SugarColumn(ColumnDescription = "创建时间", IsNullable = true, IsOnlyIgnoreUpdate = true)]
    public virtual DateTime CreateTime { get; set; }

    [SugarColumn(ColumnDescription = "更新时间")]
    public virtual DateTime? UpdateTime { get; set; }

    [SugarColumn(ColumnDescription = "创建者Id", IsOnlyIgnoreUpdate = true)]
    public virtual long? CreateUserId { get; set; }

    [SugarColumn(ColumnDescription = "创建者姓名", Length = 64, IsOnlyIgnoreUpdate = true)]
    public virtual string? CreateUserName { get; set; }

    [SugarColumn(ColumnDescription = "修改者Id")]
    public virtual long? UpdateUserId { get; set; }

    [SugarColumn(ColumnDescription = "修改者姓名", Length = 64)]
    public virtual string? UpdateUserName { get; set; }

    // 软删（配合 IDeletedFilter 查询自动过滤已删行）
    [SugarColumn(ColumnDescription = "软删除")]
    public virtual bool IsDelete { get; set; } = false;

    [SugarColumn(ColumnDescription = "软删除时间")]
    public virtual DateTime? DeleteTime { get; set; }
}

// ③ 业务数据扩展（数据权限，M2 之后按需加机构/租户字段）
```

**要点**：雪花主键 `IsIdentity = false`（不由数据库自增，框架赋值）；`IsOnlyIgnoreUpdate = true` 表示更新时忽略该列（创建类字段不可改）；软删是**标记不是物理删**（DeleteById 打 IsDelete 标记）。

## 二、ORM 初始化设计（零 Furion 版 SqlSugarSetup）

```csharp
public static class SqlSugarSetup
{
    public static void AddSqlSugar(this IServiceCollection services, IConfiguration config)
    {
        // ① 注册雪花 Id（YitIdHelper 源码移植，thirdparty/YitIdHelper）
        services.AddYitIdHelper();
        StaticConfig.CustomSnowFlakeFunc = YitIdHelper.NextId;  // SqlSugar 主键赋值走雪花

        // ② SqlSugarScope 初始化（单库；多库 M2 时改 ConnectionConfigs + ITenant）
        var connStr = config["DbConnection:ConnectionString"];  // 连接串走配置，密钥不入库
        var sqlSugar = new SqlSugarScope(new ConnectionConfig
        {
            ConfigId = "main",
            ConnectionString = connStr,
            DbType = DbType.PostgreSQL,          // PostgreSQL + Npgsql（SqlSugar 内置）
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute, // 特性优先（SugarColumn 标注）
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (type, entity) => { entity.IsDisabledDelete = true; } // 禁止删非 SqlSugar 创建的列
            }
        }, db => SetDbAop(db));

        // ③ 注册：单例客户端 + scoped 仓储
        services.AddSingleton<ISqlSugarClient>(sqlSugar);
        services.AddScoped(typeof(SqlSugarRepository<>));

        // ④ 启动时初始化数据库（建表 + 种子）
        InitDatabase(sqlSugar);
    }
}
```

### 2.1 AOP 三段（审计自动填充 + 日志 + 过滤器）

```csharp
private static void SetDbAop(SqlSugarScopeProvider db)
{
    db.Ado.CommandTimeOut = 30;

    // SQL 日志：执行 SQL 打印 + 慢 SQL（>5 秒）告警 + 错误 SQL 记录
    db.Aop.OnLogExecuting = (sql, pars) => { /* 打印执行 SQL */ };
    db.Aop.OnLogExecuted = (sql, pars) => { if (db.Ado.SqlExecutionTime.TotalSeconds > 5) { /* 慢 SQL 告警 */ } };
    db.Aop.OnError = ex => { /* 错误 SQL 记录 */ };

    // ★ 数据审计自动填充（核心设计）：插入/更新时框架自动填雪花 Id、创建时间、创建者
    db.Aop.DataExecuting = (oldValue, entityInfo) =>
    {
        if (entityInfo.OperationType == DataFilterType.InsertByObject)
        {
            // 主键为空 → 雪花赋值（业务代码禁止手动设 Id）
            if (entityInfo.EntityColumnInfo.IsPrimarykey && !entityInfo.EntityColumnInfo.IsIdentity)
            {
                if (entityInfo.EntityColumnInfo.PropertyInfo.PropertyType == typeof(long))
                {
                    var id = entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue);
                    if (id == null || (long)id == 0) entityInfo.SetValue(YitIdHelper.NextId());
                }
            }
            // 创建时间为空 → 当前时间
            else if (entityInfo.PropertyName == nameof(EntityBase.CreateTime))
            {
                var t = entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue)!;
                if (t == null || t.Equals(DateTime.MinValue)) entityInfo.SetValue(DateTime.Now);
            }
            // 当前用户自动填充（从 HttpContext 取登录用户 Claim——原生 IHttpContextAccessor）
            if (entityInfo.PropertyName == nameof(EntityBase.CreateUserId)) { /* 填当前用户 Id */ }
            else if (entityInfo.PropertyName == nameof(EntityBase.CreateUserName)) { /* 填当前用户名 */ }
        }
        else if (entityInfo.OperationType == DataFilterType.UpdateByObject)
        {
            if (entityInfo.PropertyName == nameof(EntityBase.UpdateTime)) entityInfo.SetValue(DateTime.Now);
            if (entityInfo.PropertyName == nameof(EntityBase.DeleteTime))
                if (((dynamic)entityInfo.EntityValue).IsDelete) entityInfo.SetValue(DateTime.Now);
            if (entityInfo.PropertyName == nameof(EntityBase.UpdateUserId)) { /* 填当前用户 Id */ }
            else if (entityInfo.PropertyName == nameof(EntityBase.UpdateUserName)) { /* 填当前用户名 */ }
        }
    };

    // ★ 软删过滤器：查询自动过滤已删行（IsDelete == false）
    db.QueryFilter.AddTableFilter<IDeletedFilter>(u => u.IsDelete == false);
}
```

**要点**：审计自动填充是 baseadmin 的核心设计——**业务代码只管业务字段**，雪花 Id/创建时间/创建者由框架 AOP 自动填；当前用户从 JWT Claim 取（原生用 `IHttpContextAccessor` 替代 Furion 的 `App.User`）。

### 2.2 初始化数据库（CodeFirst 增量建表 + 增量种子）

```csharp
private static void InitDatabase(SqlSugarScope db)
{
    // 建库（PostgreSQL 不存在则创建）
    db.DbMaintenance.CreateDatabase();
    // 增量建表：只处理贴 [IncreTable] 的实体（新增实体后重启自动建表）
    var entityTypes = <扫描程序集：SugarTable 特性 + IncreTable 特性 + 无 IgnoreTable>;
    foreach (var t in entityTypes) db.CodeFirst.InitTables(t);
    // 增量种子：只处理贴 [IncreSeed] 的种子类（StorageableByObject 按主键增量插入/更新）
    var seedTypes = <扫描程序集：ISqlSugarEntitySeedData<> 实现 + IncreSeed 特性>;
    foreach (var s in seedTypes) db.InitTableSeedData(s);
}
```

**要点**：实体默认贴 `[IncreTable]`、种子类贴 `[IncreSeed]`（增量，避免全量重建/重复插入）；种子用 `StorageableByObject` 按主键 upsert。

## 三、仓储设计

```csharp
public class SqlSugarRepository<T> : SimpleClient<T>, ISqlSugarRepository<T> where T : class, new()
{
    public SqlSugarRepository(ISqlSugarClient client)
    {
        base.Context = client;  // 单库直接注入；多库时按实体特性切库（M2）
    }

    // SimpleClient<T> 内置 CRUD：InsertAsync/UpdateAsync/DeleteByIdAsync/GetFirstAsync/IsAnyAsync/AsQueryable
    // 分页（返回统一 PageResult<T>）
    public async Task<PageResult<T>> ToPagedListAsync(int pageIndex, int pageSize)
    {
        RefAsync<int> total = 0;
        var list = await AsQueryable().ToPageListAsync(pageIndex, pageSize, total);
        return new PageResult<T> { Total = total, List = list };
    }

    // 乐观锁更新（实体带 Ver 字段时用，更新校验版本）
    public async Task<bool> UpdateWithVerAsync(T input) { ... }
}
```

**要点**：`SimpleClient<T>` 是 SqlSugar 内置（继承即得 CRUD）；业务服务构造函数注入 `SqlSugarRepository<T>`；分页统一 `PageResult<T>`（Total + List）；软删由过滤器自动处理（DeleteByIdAsync 是软删标记）。

## 四、JWT 鉴权设计（原生实现，零 Furion）

baseadmin 用 Furion 的 `AppAuthorizeHandler`——原生替代：**`AuthorizationHandler<IAuthorizationRequirement>` + 自定义中间件/过滤器**。

```csharp
// ① 登录签发 Token（账号密码初验，M2 加 OTP 二次）
//    Claims: UserId、Account(账号)、RealName、AccountType(超管/普通)
//    JWT 用 JwtBearer 标准签发（密钥走配置，不入库）

// ② 鉴权处理（原生 AuthorizationHandler）
public class JwtAuthHandler : AuthorizationHandler<JwtAuthRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, JwtAuthRequirement requirement)
    {
        // 校验链（baseadmin 精华，按需裁剪）：
        // a. Token 有效性（JwtBearer 自动）
        // b. Token 黑名单（Redis：登出/踢下线时写入，Key=Token 值）→ 命中则失败
        // c. Token 版本号（用户表 TokenVersion 字段 + Redis 缓存）：
        //    Token 内 TokenVersion Claim ≠ 当前用户版本 → 失败（改密码/踢下线时版本+1，旧 Token 全失效）
        // d. 账号状态（停用 → 失败）
        // 全部通过 → context.Succeed(requirement)
        return Task.CompletedTask;
    }
}

// ③ 权限校验（M2 RBAC 落地）：超管直接放行；普通用户查按钮权限集合，
//    当前路由在集合内 → 放行；在黑名单 → 拒绝
```

**要点**：Token 版本号机制是精华（可强制下线/改密码失效旧 Token）；Token 黑名单用 Redis；超管不受任何过滤器限制（软删/租户过滤器也跳过）。

## 五、统一响应 + 异常处理（baseadmin 设计）

```csharp
// ApiResult<T>：Code(状态码)/Msg(消息)/Data(数据) 三件套
// 业务异常抛 AppException → 全局异常中间件捕获 → 统一格式返回
// 未捕获异常 → 500 + 不泄露堆栈（堆栈只进日志）
// 健康检查端点 /health → ok
```

## 六、后续里程碑参考（M2 下发时细化）

> 状态更新（2026-09-09）：下表为 M2 规划期底稿，落地进度以 `docs/框架差距清单.md` §四「源仓域映射」为权威（OTP 007 ✅／RBAC 008 ✅／缓存 007/010 ✅／多库 🟡 基座就位；日志 Op/Login 已落 008/021）。

| 功能 | baseadmin 精华 | 零 Furion 替代 |
|---|---|---|
| OTP 两阶段登录 | 密码初验 → Authenticator TOTP 校验（Otp.NET 已移植 thirdparty）| 原生登录流程分两段接口 |
| RBAC | 用户/角色/菜单 + 按钮权限集合（接口级鉴权）| 原生 [Authorize] + 自定义权限过滤器 |
| 审计日志落库 | AOP 差异日志（OnDiffLogEvent 前后数据对比）| SqlSugar AOP 同款（框架无关）|
| 缓存 | SysCacheService（Redis 封装：会话/验证码/黑名单/Token 版本）| StackExchange.Redis 封装 Cache 服务 |
| 多库 | ConnectionConfigs + ITenant 切换 + 实体特性指定库 | SqlSugar 同款（框架无关）|

## 七、硬性约定（继承 baseadmin/CLAUDE.md）

1. 业务代码只写 `Business`，框架层（Core/Web.Core/Entry）只做支撑
2. 编码与命名从 `../MainWorkspace/docs/全局公约.md` §3/§4（2026-09-29：行尾已归一 LF；I 前缀/PascalCase 口径以公约为准）
3. 异步 `Async` 后缀，禁 `.Result`/`.Wait()`；注释优先中文
4. 实体默认 `[IncreTable]` 增量建表；种子 `[IncreSeed]` 增量
5. 密钥不入库（appsettings 生产密钥环境变量/Secret 注入）
6. 验证：`dotnet build` 最小范围 + Test 全绿

## 八、代码目录与文件命名约定（对齐 baseadmin 结构，开发组必读）

> 来源：baseadmin `开发规定.md` §3-4「业务层落点/目录组织要求」+ 实际仓库结构提炼（2026-08-26）。
> 目的：让 dev-a/dev-b 写出的代码目录/命名与 baseadmin 同构，多任务协作不各自发挥。

### 8.1 四层职责边界（禁止越界）

| 层 | 放什么 | 禁止 |
|---|---|---|
| Core | 框架核心：EntityBase/仓储/SqlSugarSetup/雪花/常量/Attributes/扩展/Cache 服务 | 禁止业务代码 |
| Web.Core | 服务注册/鉴权/审计/统一响应/中间件 | 禁止业务代码 |
| Web.Entry | Program/appsettings/Controllers（路由层，保持薄） | 禁止业务逻辑（只做参数绑定与返回） |
| **Business** | ★ 业务唯一落点：Entitys/Services/Models/SeedData | — |

### 8.2 Business 目录组织（对齐 Admin.NET.Business/Admin.NET.Core 惯例）

```
Business/
├── Entitys/            # 实体平铺，单文件单实体（SysUser.cs / SysRole.cs / SysUserOtp.cs）
├── Services/           # ★ 按业务域分子目录（对齐 baseadmin Service/User|Role|Menu|Auth…）
│   ├── Auth/           #   登录/鉴权域
│   │   ├── AuthService.cs
│   │   └── Dto/        #   DTO 就近放 Dto/（对齐 baseadmin 惯例）
│   │       ├── LoginInput.cs / LoginOtpInput.cs
│   │       └── LoginOutput.cs
│   ├── User/           # 用户管理域（SysUserService.cs + Dto/）
│   ├── Role/           # 角色管理域（SysRoleService.cs + Dto/）
│   └── Menu/           # 菜单管理域（SysMenuService.cs + Dto/）
├── Models/             # 通用模型（PageResult 等跨域模型；域内 DTO 仍就近 Dto/）
└── SeedData/           # 种子平铺（SysMenuSeedData.cs / SysRoleSeedData.cs…）
```

- **域划分**：按业务域（Auth/User/Role/Menu…）建目录，禁止 Services/ 平铺堆文件；新域 = 新目录
- **DTO 就近**：每域内建 `Dto/` 子目录放 Input/Output，禁止集中堆在 Models/（跨域通用模型才进 Models/）
- **一个文件一个公开类**：文件名 = 类名（PascalCase），与 .editorconfig 惯例一致

### 8.3 文件命名（对齐 baseadmin）

| 类型 | 命名 | 示例 |
|---|---|---|
| 服务类 | `{域}Sys{Entity}Service.cs`（或 `{Entity}Service.cs`） | SysUserService.cs、SysRoleService.cs、AuthService.cs |
| 入参 DTO | `{操作}{实体}Input.cs` | AddUserInput.cs、PageUserInput.cs |
| 出参 DTO | `{实体}Output.cs` | UserOutput.cs |
| 实体 | `Sys{名}.cs`（系统实体 Sys 前缀） | SysUser.cs、SysMenu.cs |
| 控制器 | `{域}Controller.cs` | AuthController.cs、SysUserController.cs |
| 种子 | `Sys{名}SeedData.cs` | SysMenuSeedData.cs |
| 接口 | `I` 前缀（已有） | ISqlSugarRepository.cs |

- **C# 文件 PascalCase**（与类名一致）；其它语言的 snake_case 约定不适用于 C# 项目——注意区分
- 服务方法命名：CRUD 用 `Page/Add/Update/Delete` 统一动词（对齐 baseadmin 动态 API 惯例，前端调用一致）
- 临时调试探针 `_` 前缀、验收前删除或转正，禁止混入正式目录（见《开发规范》约定 2）

### 8.4 控制器（Web.Entry/Controllers/）

- 按域命名 `{域}Controller.cs`，路由 `[Route("api/{域小写}")]`（如 `/api/auth/login`、`/api/sysUser/page`）
- 控制器只做：参数绑定 → 调 Service → 返回 ApiResult；业务逻辑禁止进控制器

### 8.5 测试（Test/）

- 按被测域镜像建目录（Test/User/、Test/Role/…），命名 `{类}Test.cs`；测试断言真实结果，禁止空跑

## 九、租户基座设计（2026-08-26 定，基座先行/业务后补）

> 决策：只做基座（实体/过滤/写入/选库预留），不做租户业务模块（CRUD/独立库/套餐）——intel 单组织用不上，未来多组织需求时后补，基座已就位则**零重构**。

### 9.1 实体基类（业务实体一律继承 EntityTenant）

```csharp
public interface ITenantIdFilter { long? TenantId { get; set; } }   // 过滤锚点接口

public abstract class EntityTenant : EntityBase, ITenantIdFilter
{
    [SugarColumn(ColumnDescription = "租户Id", IsOnlyIgnoreUpdate = true)]
    public virtual long? TenantId { get; set; }
}
```

- **业务实体统一继承 `EntityTenant`**（不是 EntityBase）——存量数据天然带默认租户值（后补多租户时免回填迁移）
- `ITenantIdFilter` 是 SqlSugar `AddTableFilter<T>` 的锚点：只有实现它的实体才被自动加租户条件

### 9.2 三层机制（SetDbAop 装配）

1. **查询过滤**：`AddTableFilter<ITenantIdFilter>(u => u.TenantId == 当前租户)`——从 JWT Claim `TenantId` 取；无 Claim 或超管跳过（超管豁免）
2. **写入自动填**：`Aop.DataExecuting` 插入时 TenantId 为空 → 填 Claim 值，缺省 `SqlSugarConst.DefaultTenantId`
3. **选库预留**：`[Tenant]` 特性定义好（仓储按特性选 ConfigId），租户库懒加载逻辑后补

### 9.3 常量约定（写死，后补不重构的前提）

- `SqlSugarConst.MainConfigId / LogConfigId / DefaultTenantId`（对齐 baseadmin：1300000000001/1300000000002）
- **ConfigId 即租户 Id**（未来租户库 ConfigId=租户 Id）
- 种子 Id 空间按库标识递增（同一环境重复启动 Id 稳定）

### 9.4 后补清单（未来任务书，基座零改动）

SysTenant 表与 CRUD、InitNewTenant（自动造租管机构/角色/管理员账号——复用本框架 RBAC）、租户独立库初始化（InitTenantDatabase + [TenantSeed] 种子改写）、租户套餐/有效期校验、前端租户管理页。
