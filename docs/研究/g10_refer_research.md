# 批 P 参照调研：自定义过滤器（3.3）

> 依据件：参照仓 Admin.NET 自定义数据过滤器（IDataExecuting）逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/SqlSugar/SqlSugarDataExecuting.cs`（49 行全文）＋`SqlSugarSetup.cs` L228/L318（挂钩点）。

## §0 映射总表

| 参照 | 本仓现状/落点 | 处置 |
|---|---|---|
| `IDataExecuting` 接口（`EntityNames` 实体类型集＋`Execute(object oldData, DataFilterModel model)`） | 本仓 `Aop.DataExecuting` 内建自动填充已全（雪花/CreateTime/租户/机构/软删审计），**无自定义扩展点** | **补**：接口移植（去 Furion 依赖） |
| `SqlSugarDataExecuting.Execute` 静态分发（Furion `Cache.Default` 内存缓存＋`App.EffectiveTypes` 反射＋`Activator.CreateInstance` 逐类型实例化＋按 `EntityNames.Contains(实体类型)` 分发） | 无 Furion（零 Furion 红线）；DI 装配已有 `AddAutoServices`（[Service] 恰好一接口→接口注册） | **简化移植**：DI 注入列表替代反射+缓存+Activator（裁决②）——`SqlSugarDataExecuting.Init(IEnumerable<IDataExecuting>)`（SqlSugarSetup 闭包经 IServiceProvider 解析）＋`Execute` 按实体类型分发；分发语义（EntityNames 含目标实体类型才执行）保留 |
| 挂钩点 `dbProvider.Aop.DataExecuting = (oldValue, entityInfo) => {...内建填充...; //SqlSugarDataExecuting.Execute(oldValue, entityInfo);}`——**参照 L318 该行被注释（死代码）**，全仓 `IDataExecuting` 零消费方 | 本仓 `db.Aop.DataExecuting` 钩子已存在（SqlSugarSetup L125，内建填充全） | **接通**（裁决①）：钩子末尾追加分发调用——参照死方法本仓接通闭环（g7 `VerifyPwdExpirationTime` 同先例） |

**参照实证**：参照"自定义过滤器"= 内建 `Aop.DataExecuting` 自动填充（已启用，本仓已移植）＋ `IDataExecuting` 自定义扩展点（**禁用死代码**：挂钩行注释＋零消费方）。本仓 3.3 缺件 = 扩展点体系——本批移植并**接通**（参照缺陷修复）。

## §1 参照原文要点

```csharp
// SqlSugarDataExecuting.cs(49 行,去版权头)
public class SqlSugarDataExecuting
{
    private static readonly ICache _cache = Cache.Default;   // Furion 内存缓存
    public static void Execute(object oldData, DataFilterModel model)
    {
        // 缓存 key "db:SqlSugarDataExecuting" → List<IDataExecuting>
        // 未命中:App.EffectiveTypes 反射扫描实现类 + Activator.CreateInstance 实例化 + 缓存
        foreach (var item in items.Where(u => u.EntityNames != null && u.EntityNames.Contains(model.EntityValue.GetType())))
            item.Execute(oldData, model);
    }
}
public interface IDataExecuting
{
    List<Type> EntityNames { get; }
    void Execute(object oldData, DataFilterModel model);
}
```

## §2 本仓现状

- `src/Core/SqlSugarSetup.cs` L125 `db.Aop.DataExecuting = (value, data) => {...}`：InsertByObject（雪花/CreateTime/CreateUser/TenantId/CreateOrgId）＋UpdateByObject（UpdateTime/软删审计/UpdateUser）内建填充全——业务代码只管业务字段。
- L83 闭包持有 `sp`（IServiceProvider）：`sp.GetRequiredService<IHttpContextAccessor>()` 先例——IDataExecuting 列表可同路径解析。
- `AddAutoServices`：`[Service]` 类恰好实现一个公共接口 → 注册接口→实现（`GetServices<IDataExecuting>()` 可聚全量实现）。

## §3 设计（本批实施口径）

1. `src/Core/SqlSugar/IDataExecuting.cs`：
   - `IDataExecuting`：`IReadOnlyList<Type> EntityNames { get; }`（收紧 readonly，语义同参照）＋`void Execute(object oldData, DataFilterModel model)`。
   - `SqlSugarDataExecuting`：`private static IReadOnlyList<IDataExecuting> _items = []`；`Init(IEnumerable<IDataExecuting> items)`（幂等：非空覆盖；SqlSugarSetup 每次 AddSqlSugar 调用重置）；`Execute(object oldData, DataFilterModel model)` 按 `EntityNames.Contains(实体运行时类型)` 分发（参照语义保留）。**去 Furion 缓存/反射/Activator**（裁决②）：DI 解析一次，零反射零缓存。
2. `SqlSugarSetup` 装配：钩子建立前 `SqlSugarDataExecuting.Init(sp.GetServices<IDataExecuting>())`；`db.Aop.DataExecuting` 钩子体末尾（内建填充之后）追 `SqlSugarDataExecuting.Execute(value, data)`——**所有操作类型（Insert/Update/Delete）均分发**（参照被注释行位置即全类型分发口径；自定义逻辑自行按 `OperationType` 分支）。
3. 使用方式：业务层（Business）写 `[Service]` 类实现 `IDataExecuting`（`EntityNames` 声明目标实体）→ `AddAutoServices` 自动注册 → 钩子自动分发。**框架层零示例实现**（#8；参照全仓零消费方同口径）。
4. **不做**：事件化（`IDataExecuting` 保持同步 void 回调，非事件总线——数据执行钩子在 SQL 生成前同步窗口，异步化会破坏填充语义）；批量 List 操作的分发（内建填充同口径：只处理单对象操作）。

## §4 验证口径（真实 PG）

`BatchPCustomFilterTest` 用例：
1. **命中分发**：测试 `IDataExecuting`（`EntityNames=[SysUser]`，Execute 置探针字段/记调用）注册进 DI → `Insertable<SysUser>` → 探针被填（钩子接通实证）
2. **未命中不分发**：`EntityNames=[SysOrg]` 的实现 → 插 SysUser 不触发（EntityNames 语义）
3. **多实现全分发**：两个命中实现都执行（列表遍历，非首个短路）
4. **oldData 可见**：UpdateByObject 时 `oldData` 非 null 且为旧值（参照签名语义）
5. **未注册（空列表）不炸**：`Init([])` → 任何操作正常（零扩展点基线）
探针实现为测试类内嵌套类经 `services.AddSingleton<IDataExecuting>(new ...)` 显式注册（不走 [Service] 扫描——测试容器 AddAutoServices 只扫 Core 程序集，测试程序集实现经显式注册同效）。探针用户 finally 清理；真实 PG。

## §5 参照偏差声明

1. **接通被参照注释掉的挂钩行**（参照 L318 死代码＋全仓零消费方）：本仓 `Aop.DataExecuting` 钩子末尾追加分发——参照缺陷修复（g7 `VerifyPwdExpirationTime` 死方法接通同先例）。
2. **去 Furion 三件套**（`Cache.Default` 缓存/`App.EffectiveTypes` 反射/`Activator.CreateInstance`）→ DI 注入列表：零 Furion 红线＋`AddAutoServices` 已有同语义装配（#11 复用优先）；分发语义（EntityNames 含目标类型才执行）保留。
3. `EntityNames` 收紧 `IReadOnlyList<Type>`（参照 `List<Type>`，语义不变，防外部改写）。
4. `Init` 幂等覆盖而非"首调生效"（参照缓存语义 = 首调后冻结；本仓 DI 解析天然无缓存问题，每次装配刷新更正确）。
5. 框架层零示例实现（#8；参照全仓零消费方同口径）。
6. **实证偏差：SqlSugar 5.1.4.217 InsertByObject 的 oldData 实机非 null**（参照注释口径"Insert 时 oldData 为 null"与实机不符——5.1.4.217 插入路径传递的是实体对象本身）。消费方不得依赖"Insert 时 oldData==null"判别插入场景，应判别 `model.OperationType`。本仓测试按实证口径断言（回调到达+Update 旧值可见），不锁死 Insert 的 oldData 形态。

## §6 范围边界（不做）

- 事件化/异步化 `IDataExecuting`（数据执行钩子为 SQL 前同步窗口）。
- 批量 List 操作分发（内建填充同口径：单对象）。
- BindSerial（3.4 维持按需——依赖流水号服务重件，批 O 裁决不变）。
