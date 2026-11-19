# task_id: task_014_ha_eventbus
# subject: task_014_ha_eventbus
# summary: 轻量事件总线(内存 Channel+订阅+重试)+操作日志异步化
# source: huluyeye
# created: 2026-08-28
# format: v3
# repo: huluyeye-admin
# priority: P1
# dep_on: task_024_ha_auto_service
# files: src/Core/EventBus/, src/Core/Attributes/EventSubscribeAttribute.cs, src/Core/Attributes/SuppressEventLogAttribute.cs, src/Core/Services/Log/, src/Web.Core/Auth/OpLogFilter.cs

# 任务：[框架] 轻量事件总线重写版（内存 Channel + 订阅 + 异步消费 + 重试）+ 操作日志异步化

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**重写说明（2026-08-28 葫芦爷爷）**：本版取代 2026-08-27 旧稿（已废弃）。依据最新架构定案对齐：①框架代码一律落 **Core/**（023），Business 为空置预留层——订阅者落 `Core/Services/Log/` 而非 Business；②服务注册**不声明入口**（024 [Service] 自动发现 + §99③ 开发与注册分离——基线注入由实现方按仓库既有约定（RegisterService/AddAutoServices 已根除散注册）完成，任务书不指定调用入口文件）；③旧稿引用的 Web.Core/Startup.cs 不存在（无 Startup 概念，入口仅 Web.Entry/Program.cs），相关表述删除。
**必读参考**：仓库 docs/框架开发参考手册.md §5.6（事件总线）+ §5.8（重试/执行日志）+ §5.9（订阅异步解耦）。

## 背景

框架事件解耦缺失（操作日志等目前同步落库）。实现**轻量内存事件总线**（单机够用，Redis 版留作集群扩展）+ 操作日志异步化首个接入场景。

## 契约（按此实现）

1. **事件总线核心**（Core/EventBus/）：
   - `IEventPublisher`：`PublishAsync<T>(string eventName, T payload)`——入队即返回（异步消费）
   - `IEventSubscriber`：`[EventSubscribe(name)]` 特性标记订阅方法（声明式，参照 §5.9）
   - `EventBus`：Channel<object>（有界容量 1000，满则等待/丢弃策略注明实现选择）+ 后台消费者（Channel reader 循环）
   - 订阅注册：启动时反射扫描 [EventSubscribe] 方法（Assembly + 特性过滤）
   - **重试**（§5.8 简化）：订阅执行失败重试 ≤2 次（间隔 1s），仍失败记 ILogger 告警（不阻断队列——事件消费失败不影响主流程）

2. **事件执行日志**（§5.8 简化，可选）：执行前写 SysLogEvent（Running），完成更新 Success/Error；`[SuppressEventLog]` 跳过自身（防递归）

3. **操作日志异步化接入**（Core/Services/Log/ + Web.Core/Auth/OpLogFilter.cs）：
   - OpLogFilter 采集逻辑改为 `PublishAsync("op_log", SysLogOp)` ——响应正常返回不等待落库
   - 订阅者 `OpLogSubscriber`（[EventSubscribe("op_log")]，落 Core/Services/Log/）：复用现有 OpLog 落库逻辑
   - 保持：列表查询/清理/统计接口不变

## 交付物

- `/data/worktrees/admin/task_014_ha_eventbus/src/Core/EventBus/EventBus.cs`（IEventPublisher/IEventSubscriber/EventSubscribe 特性/EventBus 同批）
- `/data/worktrees/admin/task_014_ha_eventbus/src/Core/Services/Log/OpLogSubscriber.cs`（操作日志异步化订阅者）
- `/data/worktrees/admin/task_014_ha_eventbus/src/Web.Core/Auth/OpLogFilter.cs`（采集点改造为发布事件）

## 验收标准

- `cd /data/worktrees/admin/task_014_ha_eventbus && dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误；`dotnet test src/Test --no-build` 全绿
- 实测（验收报告附输出）：
  - 调用任一业务 API → 响应正常返回（请求链路不等待落库），随后 sys_log_op 表出现记录（异步落库，延迟 <1s 实测）
  - 事件失败重试：构造抛异常订阅者 → 重试 2 次后 ILogger 告警（日志可见），主流程不受影响
  - 队列有界：连续发布超容量 → 行为符合契约（等待或丢弃策略，注明实测）
  - 订阅扫描：新订阅者类（加 [EventSubscribe]）自动注册生效（无手写注册代码）
  - 原操作日志查询/清理接口回归正常
- git commit（提交号写入验收报告），验收报告 acceptance_task_014_ha_eventbus.md 附实测输出