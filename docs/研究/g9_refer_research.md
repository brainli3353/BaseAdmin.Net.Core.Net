# 批 O 参照调研：校验类特性（6.11 补件）

> 依据件：参照仓 Admin.NET 自定义 ValidationAttribute 逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/Attribute/MaxValueAttribute.cs`/`MinValueAttribute.cs`（全文移植级）；手册 6.11 点名"校验类（IdCardNo 等）"。

## §0 映射总表

| 参照/手册 | 本仓现状/落点 | 处置 |
|---|---|---|
| `MaxValueAttribute`（ValidationAttribute 子类：null 放行，`Convert.ToDouble(value) <= max`） | 全仓零自定义校验特性（DTO 零 DataAnnotations 使用点） | **补**：原文移植（去版权头，补 `using System.ComponentModel.DataAnnotations;`） |
| `MinValueAttribute`（同构，`>= min`） | 同上 | **补**：原文移植 |
| 手册点名"校验类（IdCardNo 等）" | 无 | **补** `IdCardNoAttribute`（18 位含校验位/15 位老证兼容——参照仓未建此件，按手册点名自建，正则+GB11643 校验位算法） |
| 模型校验 400 出口（参照 Furion 自有校验管道） | 本仓零 Furion：`[ApiController]` 默认 ProblemDetails 400（与全仓"HTTP 200+体业务码"契约断裂） | **补** `InvalidModelStateResponseFactory`→`ApiResult.Fail(首条错误, 400)`（HTTP 200，与 ExceptionMiddleware 同契约，#10 对外信息稳定） |
| `[BindSerial]` 流水号特性（参照 3.4） | 依赖 SysSerialService+流水号表+SqlSugar DataFilter 钩子（重件，垂直增值） | **不做**（裁决③）：3.4 维持 🟡 按需后补，#8 |

## §1 参照原文要点

```csharp
// Admin.NET.Core/Attribute/MaxValueAttribute.cs(全文)
public class MaxValueAttribute : ValidationAttribute
{
    private double MaxValue { get; }
    public MaxValueAttribute(double value) => this.MaxValue = value;
    public override bool IsValid(object value) => value == null || Convert.ToDouble(value) <= this.MaxValue;
    public override string FormatErrorMessage(string name) => base.FormatErrorMessage(name);
}
// MinValueAttribute 同构(>=)
```

参照仓内 MaxValue/MinValue 亦为**零使用点的提供件**（纯库位）——本仓同口径：建特性＋接通 400 消费链，不强造业务使用点（#8）。

## §2 本仓现状

- `src/Core/Attributes/`：15 个框架特性（MenuPerm/OpenApi/Idempotent/…）消费链已接线；**无校验类特性**。
- DTO 零 DataAnnotations 使用点；`[ApiController]` 自动模型校验默认 ProblemDetails 400 出口——与全仓"HTTP 200 + ApiResult 体业务码"契约（ExceptionMiddleware 口径：`StatusCode=200`，`ApiResult.Fail(msg, 500)`）断裂。
- 控制器统一 `ApiResult<T>` 出口；错误消息风格=中文稳定可检索（#10）。

## §3 设计（本批实施口径）

1. **三个校验特性**（`src/Core/Attributes/`，单文件单类，与既有特性同目录同风格）：
   - `MaxValueAttribute`/`MinValueAttribute`：参照原文移植（`double` 构造参；null 放行；`FormatErrorMessage` 缺省模板 `{0} 必须小于等于 {1}` 风格——参照 base 模板为英文，本仓覆写中文模板对齐 #10）。
   - `IdCardNoAttribute`：18 位（正则 `^\d{17}[\dXx]$` + GB11643 加权校验位）或 15 位老证（`^\d{15}$`）；null 放行（可空字段由 `[Required]` 管）；`AllowMultiple=false`。
2. **400 统一出口**：`Program.cs` `AddControllers(o => o.InvalidModelStateResponseFactory = ...)`——`ModelState.ErrorValues` 首条 `string.Join(";", msgs)` → HTTP 200 + `ApiResult.Fail(msg, 400)`（**与 ExceptionMiddleware 同契约**：HTTP 状态恒 200，业务码在体；裁决①）。
3. **不强造业务使用点**（#8，与参照同口径）：特性即库位，400 链路即消费链；使用指南给使用范式（DTO 上挂 `[Required]/[MaxValue]` 等）。
4. **不做 BindSerial**（裁决③，见 §0/§5）。

## §4 验证口径

`BatchOValidationAttrTest` 用例（纯函数，无 DB）：
1. `MaxValue`：null 放行/边界等值通过/超限拒绝/非法类型抛 FormatException（参照 `Convert.ToDouble` 原语义）
2. `MinValue`：同构 3 用例
3. `IdCardNo`：合法 18 位（含校验位）/校验位错误拒绝/15 位老证/17 位拒绝/Xx 大小写
4. **400 链路**：Web.Entry 集成路径——Demo 端点挂 `[Required]` DTO 字段，POST 非法 body→HTTP 200 + `code=400` + msg 含字段错误（e2e 或 WebApplicationFactory；若 Factory 装配重则走 e2e 真实 app）
build 双工程 0W/0E；全量后台。

## §5 参照偏差声明

1. **400 出口改走 ApiResult（HTTP 200+体 code=400）**：参照走 Furion 自有管道（ProblemDetails 系），本仓零 Furion 且全仓契约"HTTP 恒 200+体业务码"（ExceptionMiddleware 口径）——同契约优先于参照管道形态。
2. `FormatErrorMessage` 中文模板覆写：参照用 base 英文模板，本仓对外消息中文稳定可检索（#10）。
3. **不做 `[BindSerial]`**（手册 6.11 同列）：依赖 SysSerialService+流水号表+DataFilter 钩子重件，垂直增值按需后补（3.4 维持 🟡）；参照仓内 BindSerial 仅测试实体使用点，本仓无流水号业务场景。
4. `IdCardNoAttribute` 为**手册点名自建件**（参照仓未建）：18 位校验位算法按 GB11643；15 位老证兼容为最小放宽（不做升位补全）。
5. 不强造业务 DTO 使用点（#8，与参照同口径：特性=库位）。

## §6 范围边界（不做）

- BindSerial/流水号服务（3.4 按需）。
- 邮箱/手机号/URL 等更多校验特性（按需后补；#8 只建手册点名的 IdCardNo＋参照原有的 Max/Min）。
- 字段级错误明细回传（只回首条错误聚合消息，与 #10 稳定消息口径一致；明细进日志）。
