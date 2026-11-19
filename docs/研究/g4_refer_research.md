# 批 J 参照调研：SqlSugar AOP 慢 SQL 检测（5.5）

> 依据件：参照仓 Admin.NET 慢 SQL 机制逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/SqlSugar/SqlSugarSetup.cs` `SetDbAop`（L170-221，SqlSugarCore 5.1.4.214；本仓 5.1.4.217 同代 API 兼容）。

## §0 映射总表

| 参照（SetDbAop） | 本仓现状/落点 | 处置 |
|---|---|---|
| `dbProvider.Ado.CommandTimeOut = 30` | 未设（SqlSugar 缺省 30s 同值，但显式化更稳） | **补**：`db.Ado.CommandTimeOut = 30`（显式，同参照） |
| `dbProvider.Aop.OnError` 错误 SQL | 已有（`db.Aop.OnError` → LogError） | 保留不动 |
| `dbProvider.Aop.OnLogExecuting` 执行 SQL（enableConsoleSql 开关） | 已有（→ LogDebug） | 保留不动 |
| `dbProvider.Aop.OnLogExecuted` 慢 SQL（`SqlExecutionTime.TotalSeconds <= 5` 跳过；`Ado.SqlStackTrace.FirstFileName/FirstLine/FirstMethodName` 首帧 + `UtilMethods.GetNativeSql` → `Log.Warning`） | **缺** | **补**：`ConfigureProvider` 内 `db.Aop.OnLogExecuted`（本批核心） |
| `dbProvider.Aop.DataExecuting` 数据审计 | 已有（主键/创建/更新/租户/机构填充） | 保留不动 |

## §1 参照原文（慢 SQL 段逐行）

```csharp
dbProvider.Aop.OnLogExecuted = (sql, pars) =>
{
    // 执行时间超过5秒时
    if (dbProvider.Ado.SqlExecutionTime.TotalSeconds <= 5) return;

    var fileName = dbProvider.Ado.SqlStackTrace.FirstFileName; // 文件名
    var fileLine = dbProvider.Ado.SqlStackTrace.FirstLine;     // 行号
    var firstMethodName = dbProvider.Ado.SqlStackTrace.FirstMethodName; // 方法名
    var log = $"【{DateTime.Now}——超时SQL】\r\n【所在文件名】：{fileName}\r\n【代码行数】：{fileLine}\r\n【方法名】：{firstMethodName}\r\n" +
              $"【SQL语句】：{UtilMethods.GetNativeSql(sql, pars)}";
    Log.Warning(log);
};
```

要点：① 阈值硬编码 5s；② 堆栈定位取 `Ado.SqlStackTrace` **首帧**（最内层业务调用点，非 SqlSugar 内部帧——SqlSugar 已在堆栈构造时过滤自身框架帧）；③ 输出**原生 SQL**（参数内联，`GetNativeSql`）而非参数化占位形态；④ Warning 级日志，**不落独立表**（参照无 SysLogSlowSql）。

## §2 本仓现状

`src/Core/SqlSugarSetup.cs` `AddSqlSugar` → `ConfigureProvider(SqlSugarProvider db)` 逐连接配置：
- 已有：`Aop.OnError`（LogError 带 ex.Sql）、`Aop.OnLogExecuting`（LogDebug）、`Aop.DataExecuting`（审计填充）。
- 缺：`OnLogExecuted`、`CommandTimeOut` 显式化。
- 配置段：`DbSettings`（Database.json 模块配置＋appsettings.json 双处，环境可覆盖）已有 `EnableIncreTable` 先例——慢 SQL 阈值挂同段。

## §3 设计（本批实施口径）

1. **阈值配置化**：`DbSettings:SlowSqlSeconds`（double，缺省 5.0；**<=0 = 关闭慢 SQL 检测**）。读取点同 `EnableIncreTable`（AddSqlSugar 处 `config.GetValue`，值捕获进闭包）。
2. **超时显式化**：`db.Ado.CommandTimeOut = 30`（同参照硬编码；最小必要不另设配置键）。
3. **日志形态**：`ILogger` Warning 结构化参数（非参照字符串拼接）：`慢 SQL（{Elapsed} 秒 > 阈值 {Threshold} 秒，库 {ConfigId}）：{Sql}`＋堆栈首帧 `{File}:{Line} {Method}`；SQL 用 `UtilMethods.GetNativeSql(sql, pars)` 内联参数。
4. **作用面**：`ConfigureProvider` 内 → main/log 全连接同口径（逐连接 AOP 一致性，同 OnError/OnLogExecuting）。

## §4 验证口径

- **单测（真实 PG）**：阈值临时置 0.1s（独立 `AddSqlSugar` 容器）→ `await Task.Delay(200)` 包裹一条查询触发慢判定 → 捕获 `ILogger` 输出（`FakeLoggerProvider`）断言含"慢 SQL"＋原生 SQL＋首帧文件名；阈值 0（关闭）→ 同等待不触发。
- **e2e 实证**：临时改 `DbSettings:SlowSqlSeconds=0.2` 起 app → 任意查询 → logs/app-*.log 或控制台出现慢 SQL Warning 行 → 恢复配置。
- build 双工程 0W/0E。

## §5 自行裁决清单（批内自主，批末呈报）

1. **阈值配置化 `DbSettings:SlowSqlSeconds` 缺省 5、<=0 关闭**（参照硬编码 5s；管理端机型差异需可调，配置段已有先例）。
2. **CommandTimeOut=30 硬编码显式化**（参照同值；不加配置键——最小必要）。
3. **日志走 ILogger Warning 结构化参数**（参照 `Log.Warning` 字符串拼接；本仓全链路 ILogger 口径）。
4. **不落独立慢 SQL 表**（参照不落库；日志即可检索，建表留按需——与异常档"事件落库"不同，慢 SQL 属诊断信号非审计件）。
5. **堆栈首帧口径同参照**（`SqlStackTrace.First*`；SqlSugar 已过滤框架自身帧，首帧即业务调用点）。**实证补充**：异步链（`*Async`）下 SqlSugar 5.1.4.217 的同步堆栈在回调点退化为 `?:0 ExecutionContext` 帧（线程池恢复续体的同步栈不含调用方帧；参照仓同版本同特性）——同步场景首帧定位正常，异步场景仅阈值/原生 SQL 可用，堆栈字段照打不删（诊断价值降级但形态稳定，单测不断言文件名）。
6. **全连接同口径**（main/log 逐连接配置；不引入按库差异）。

## §6 范围边界（不做）

- 慢 SQL 入库面板（按需批次，日志可先支撑定位）。
- 按库/按会话差异化阈值（无需求）。
- 参数截断（参照注释掉的 100 字符截断未启用，本仓同口径不启用）。
