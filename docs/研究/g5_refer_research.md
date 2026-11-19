# 批 K 参照调研：仓储乐观锁 UpdateWithVerAsync（3.1）

> 依据件：参照仓 Admin.NET 乐观锁机制逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/SqlSugar/SqlSugarRepository.cs` `#region 乐观锁更新`（L112-215，SqlSugarCore 5.1.4.214；本仓 5.1.4.217 同代 API 兼容）。

## §0 映射总表

| 参照（SqlSugarRepository<T>） | 本仓现状/落点 | 处置 |
|---|---|---|
| `UpdateWithVerAsync(T input, string verFieldName = "Ver")` 仓储方法 | `ISqlSugarRepository<T>` 无 | **补**：接口＋实现 |
| `GetVerProperty` 反射取 Ver（int/int?） | 无 | **补**：私有辅助（同参照） |
| 无 Ver 属性 → 回落普通 `AsUpdateable(input).ExecuteCommandAsync()` | `UpdateAsync` 已有 | 复用回落 |
| Ver 存在：`AsUpdateable(input).PublicSetColumns(it => it.Ver, it => it.Ver + 1).Where(it => it.Ver == originalVer)` | 无 | **补**（Expression 树同参照） |
| 返回 bool（true=更新成功；false=并发冲突或记录不存在） | — | 同参照 |
| int 型 Ver=null 按 0 处理；int? 型 Ver=null 抛 `ArgumentException` | — | 同参照 |

参照仓 Ver 实体分布：仅 `SysLdap`/`SMSOptions` 两外围实体带 Ver——乐观锁是**仓储层通用能力**，实体按需声明 Ver 字段，框架不强制。本仓当前无带 Ver 实体（能力先行，业务实体按需采用）。

## §1 参照原文要点

```csharp
public virtual async Task<bool> UpdateWithVerAsync(T input, string verFieldName = "Ver")
{
    var verProperty = GetVerProperty(verFieldName);   // int/int? 反射,其余类型 null
    if (verProperty == null)
        return await base.AsUpdateable(input).ExecuteCommandAsync() > 0;   // 无 Ver → 普通更新

    // int 型 null → 0;int? 型 null → throw ArgumentException
    // left:  it => (object)it.Ver
    // right: it => (object)(it.Ver + 1)          // int? 先 .Value + 1 再转回 int?
    // where: it => it.Ver == originalVer
    return await base.AsUpdateable(input)
        .PublicSetColumns(leftExpr, rightExpr)
        .Where(whereLambda)
        .ExecuteCommandAsync() > 0;
}
```

机制：`UPDATE ... SET Ver = Ver + 1, <其余列> = @input... WHERE <主键条件> AND Ver = @originalVer`——数据库单语句原子完成"版本匹配校验＋自增"，无 check-then-update 竞态窗口。`PublicSetColumns`（SqlSugar 列级表达式更新 API，5.1.4.x 稳定）与整对象更新共存：Ver 列走表达式自增、其余列走 input 值。

## §2 本仓现状

`src/Core/Repository/ISqlSugarRepository<T>`：Insert/Update/DeleteById(软删)/DeletePhysicalBy/GetFirst/IsAny/AsQueryable/ToPagedList。`SqlSugarRepository<T> : SimpleClient<T>`（继承即得 `AsUpdateable` 基类能力）＋多库选库（`SelectConnection`）。`AsUpdateable(input)` 走构造时选定连接，与其余 CRUD 同库口径 ✓。

## §3 设计（本批实施口径）

1. 接口＋实现照参照移植（Expression 树同构；`PublicSetColumns` 5.1.4.217 可用——build 验证）。
2. 本仓 `ISqlSugarRepository<T>` 接口方法签名同参照（`verFieldName = "Ver"` 缺省参数）。
3. 返回值 bool 语义同参照：false=冲突或不存在（不抛异常；冲突是正常业务分支，抛异常会把并发竞争变错误日志噪声）。
4. XML 注释写清"Ver 列由 SQL 表达式自增、不依赖 input.Ver 最终值"。

## §4 验证口径（无 HTTP 端点消费——仓储层能力，真实 PG 单测覆盖全分支）

探针表手工 DDL（`dsh_k_ver_probe`/`dsh_k_ver_probe_nullable`，测试内建＋finally Drop，不碰存量表）＋探针实体（`[SugarTable]` 映射，不入 Core 程序集故 CodeFirst 不建表）：
1. 无 Ver 属性实体（SysConfig）→ 普通更新成功（回落分支）
2. Ver 匹配（DB=5，input.Ver=5）→ true＋DB Ver=6（自增）
3. Ver 不匹配（DB=6，input.Ver=5）→ false＋DB 不变（冲突分支）
4. int 型 Ver=null（DB=0）→ 按 0 处理 → true＋DB Ver=1
5. int? 型 Ver=null → `ArgumentException`
6. 主键不存在 → false
build 双工程 0W/0E＋全量复跑。

## §5 自行裁决清单（批内自主，批末呈报）

1. **能力先行无强制消费实体**（参照同口径：Ver 实体仅 2 个外围；本仓业务实体按需声明，框架不替业务决定哪些表要乐观锁）。
2. **无 Ver 回落普通更新**（参照同口径；同一方法两种语义=调用方无感知升级）。
3. **冲突返回 false 不抛异常**（参照同口径；并发竞争是正常分支）。
4. **int 型 null 按 0 / int? 型 null 抛 ArgumentException**（参照同口径；int? null=调用方未读回当前版本，静默按 0 会把首次更新错配成无条件更新）。
5. **探针表手工 DDL 不碰存量表**（测试自含，finally Drop）。
6. **无 e2e**（仓储层能力无 HTTP 端点消费；真实 PG 单测全分支覆盖即有效验证，说明留批末呈报）。

## §6 范围边界（不做）

- 实体级 Ver 字段普及（业务实体按需，非框架职责）。
- 全局乐观锁开关/所有 UpdateAsync 自动带 Ver（参照无此行为，强制化破坏存量更新语义）。
- 重试策略（冲突重试属业务语义，仓储只提供原子判定）。
