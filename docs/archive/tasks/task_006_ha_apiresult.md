# task_id: task_006_ha_apiresult
# subject: task_006_ha_apiresult
# source: huluyeye
# created: 2026-08-26
# format: v3
# dep_on: task_002_ha_sqlsugar
# priority: P0
# files: src/Web.Core/Models/ApiResult.cs, src/Web.Core/Middlewares/ExceptionMiddleware.cs, src/Web.Core/Exceptions/AppException.cs, src/Web.Entry/Controllers/DemoController.cs, src/Web.Entry/Program.cs

# 任务：统一响应 ApiResult + 全局异常处理 + 健康检查

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/baseadmin设计精华参考.md 第五节「统一响应 + 异常处理」——按此实现，禁止引入 Furion。
**代码纪律**：关键位置必须补中文注释。

## 背景

骨架已就绪。本任务实现统一响应模型 + 全局异常中间件 + 健康检查端点——后续所有接口返回统一格式。

## 契约（按此实现）

1. **`src/Web.Core/Models/ApiResult.cs`**：
```csharp
public class ApiResult<T>
{
    public int Code { get; set; }     // 0 成功；非 0 失败（业务码）
    public string Msg { get; set; }   // 消息
    public T Data { get; set; }       // 数据
    // 静态工厂：Success(T data) / Fail(string msg, int code = 1)
}
// 非泛型 ApiResult（无 Data 场景）同样提供
```

2. **`src/Web.Core/Exceptions/AppException.cs`**：业务异常（Message + Code），业务代码抛它。

3. **`src/Web.Core/Middlewares/ExceptionMiddleware.cs`**（全局异常中间件）：
- 捕获 AppException → 返回 ApiResult 统一格式（Code=异常码，HTTP 200）
- 捕获未处理异常 → 记日志（ILogger，含堆栈）→ 返回 ApiResult 统一格式（Code=500，Msg「系统繁忙，请稍后重试」，**不泄露堆栈**）
- 响应 Content-Type: application/json; charset=utf-8

4. **Program.cs**：注册中间件（UseMiddleware<ExceptionMiddleware> 放在路由前）。

5. **健康检查**：`GET /health` 端点（HealthController 或 Minimal API）→ 返回 `{ "status": "ok" }`。

6. **示例用法**：一个演示控制器（如 DemoController，`GET /api/demo/error` 抛 AppException、`GET /api/demo/success` 返回 ApiResult）——验证中间件与统一格式生效。

## 交付物

- `/data/worktrees/admin/task_006_ha_apiresult/src/Web.Core/Models/ApiResult.cs`
- `/data/worktrees/admin/task_006_ha_apiresult/src/Web.Core/Middlewares/ExceptionMiddleware.cs`
- `/data/worktrees/admin/task_006_ha_apiresult/src/Web.Entry/Controllers/DemoController.cs`

（附带修改：AppException.cs + Program.cs 注册 + Health 端点——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_006_ha_apiresult && dotnet build src/huluyeye-admin.slnx` 输出 0 个警告 0 个错误
- 启动 `dotnet run --project src/Web.Entry` 后实测（curl，附完整输出进验收报告）：
  - `docker exec huluyeye-dev-a curl -s http://127.0.0.1:5090/health` → HTTP 200 且响应含 `"status":"ok"`
  - `docker exec huluyeye-dev-a curl -s http://127.0.0.1:5090/api/demo/success` → HTTP 200 且响应为 `{"code":0,"msg":...,"data":...}` 统一格式
  - `docker exec huluyeye-dev-a curl -s http://127.0.0.1:5090/api/demo/error` → 业务异常返回统一格式（含中文错误消息）
  - 未捕获异常（如 demo 接口除零）→ 响应 Code=500 且**响应体不含堆栈信息**（stack trace/at System 等字样）
- git commit（提交号写入验收报告），验收报告 `acceptance_task_006_ha_apiresult.md` 附实测输出
