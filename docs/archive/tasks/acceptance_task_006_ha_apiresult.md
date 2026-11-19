# 验收报告 — task_006_ha_apiresult（统一响应 ApiResult + 全局异常处理 + 健康检查）

- 开发：dev-a（开发秘书）
- 日期：2026-08-26
- 代码交付提交：**e61b9c3**（分支 task_task_006_ha_apiresult，基点 main@0466ae4）
- build 结果：**0 个警告 0 个错误**。系统自动 build（2026-08-26 用户定机制，提交后自动触发）结果落盘于 `/data/tasks/build/task_006_ha_apiresult.txt`；本报告撰写时该文件尚在生成中，以下附交付提交前同一命令、同一代码状态的预检 build 完整输出（提交 e61b9c3 之后代码零改动，两者输入相同）

## 交付物清单（与任务书一致）

| 文件 | 类型 | 说明 |
|---|---|---|
| src/Web.Core/Models/ApiResult.cs | 新增 | 泛型/非泛型统一响应 + Success/Fail 静态工厂 + 统一 JSON 规则（camelCase + 中文不转义） |
| src/Web.Core/Exceptions/AppException.cs | 新增 | 业务异常（Message + Code，默认 code=1） |
| src/Web.Core/Middlewares/ExceptionMiddleware.cs | 新增 | 全局异常中间件（双分支：业务异常原样返回 / 未捕获异常 Code=500 通用文案） |
| src/Web.Entry/Controllers/DemoController.cs | 新增 | 演示控制器：/api/demo/success、/error、/crash |
| src/Web.Entry/Program.cs | 修改 | UseMiddleware 置于路由前 + /health 端点 + 控制器 JSON 规则与中间件同构 |
| src/Web.Core/Huluyeye.Web.Core.csproj | 修改 | 增加 FrameworkReference Microsoft.AspNetCore.App（中间件依赖 ASP.NET Core 类型，普通 Sdk 项目必需） |

## build 验收

```
验收命令：cd /data/worktrees/admin/task_006_ha_apiresult && dotnet build src/huluyeye-admin.slnx
预检 build 输出（交付提交 e61b9c3 之前，代码零改动状态；全解 6 项目全部编译通过）：
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## 实测验收（dotnet run --project src/Web.Entry，http://localhost:5132，Development 环境，curl 完整输出）

### 1. `curl -i http://localhost:5132/health`

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Date: Tue, 25 Aug 2026 22:23:51 GMT
Server: Kestrel
Transfer-Encoding: chunked

{"status":"ok"}
```
✅ HTTP 200 且响应含 `"status":"ok"`

### 2. `curl -i http://localhost:5132/api/demo/success`

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Date: Tue, 25 Aug 2026 22:23:51 GMT
Server: Kestrel
Transfer-Encoding: chunked

{"code":0,"msg":"操作成功","data":"你好，葫芦爷 Admin 统一响应！"}
```
✅ HTTP 200 且为 `{"code":0,"msg":...,"data":...}` 统一格式（camelCase、中文不转义）

### 3. `curl -i http://localhost:5132/api/demo/error`（抛 AppException，code=1001）

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Date: Tue, 25 Aug 2026 22:23:51 GMT
Server: Kestrel
Transfer-Encoding: chunked

{"code":1001,"msg":"演示业务异常：该操作不被允许"}
```
✅ 业务异常返回统一格式，含中文错误消息，业务码 1001 透传

### 4. `curl -i http://localhost:5132/api/demo/crash`（除零触发未捕获异常）

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Date: Tue, 25 Aug 2026 22:23:51 GMT
Server: Kestrel
Transfer-Encoding: chunked

{"code":500,"msg":"系统繁忙，请稍后重试"}
```
✅ Code=500 通用文案；脚本断言响应体 grep `at System|Stack Trace|\.cs\([0-9]` 命中 0 处 —— **无堆栈泄露**

### 服务端日志佐证（堆栈只进日志，不进响应）

- 业务异常分支：`warn: Huluyeye.Web.Core.Middlewares.ExceptionMiddleware[0] 业务异常 Code=1001: 演示业务异常：该操作不被允许`（Warning，无堆栈，符合预期分支定位）
- 未处理异常分支：`fail: ... 未处理异常: GET /api/demo/crash` + `System.DivideByZeroException: Attempted to divide by zero.` 完整堆栈（含 `at Huluyeye.Web.Entry.Controllers.DemoController.Crash() ... line 35`）—— ILogger 记录含堆栈 ✅

## 风险与偏差说明

1. **任务书「必读参考」`docs/baseadmin设计精华参考.md` 全盘不存在**（worktree、主工作区、git 全分支 find 确认无此文件）。处置：按任务书「契约」一节（比参考文档更具体）实现，风格遵循 baseadmin 惯例（HTTP 200 + 业务码、不泄露堆栈）。建议葫芦爷爷补档或更正任务书引用。
2. **Web.Core csproj 附带修改**（契约隐含）：中间件需要 `HttpContext/RequestDelegate`，普通 `Microsoft.NET.Sdk` 项目必须显式 `FrameworkReference Microsoft.AspNetCore.App`，否则编译不过。
3. **.NET 10 API 迁移**：`JsonNamingPolicy` 位于 `System.Text.Json` 根命名空间（非 `System.Text.Json.Serialization`）；.NET 10 中 `JsonOptions.JsonSerializerOptions` 属性引用只读，`AddJsonOptions` 只能原地变更默认实例属性——已按此实现并在代码中注释。

## 交付最小清单

- **限域**：仅新增 Web.Core（Models/Exceptions/Middlewares 3 文件 + csproj 1 行引用）、Web.Entry（DemoController 新增 + Program.cs 接线）；未触碰 Core/Business/Test 层，与并行任务 task_002（Core 层）文件零交集。
- **引用影响面**：`ApiResult.Json` 为全局 JSON 规则单一事实源（中间件与 MVC 共用）；后续任务统一返回 `ApiResult<T>` 即可。
- **回滚方式**：`git revert e61b9c3`（单提交，自包含）。
- **最小验证**：`dotnet build src/huluyeye-admin.slnx`（0 警告 0 错误）+ 上述 4 条 curl。
