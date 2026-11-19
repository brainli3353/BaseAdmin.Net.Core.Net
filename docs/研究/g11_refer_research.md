# 批 Q 参照调研：IOptions 强类型配置绑定（6.8）

> 依据件：参照仓 Admin.NET Option 体系逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/Option/`（18 个 Option 类：CacheOptions/SnowIdOptions/UploadOptions/...）＋`Web.Core/ProjectOptions.cs`（`AddConfigurableOptions` 按类名=配置节名绑定）。
> 手册口径：`框架开发参考手册.md` §6.8（配置绑定体系）——原生版 = `services.Configure<T>(config.GetSection("节名"))` + `IOptions<T>`/`IOptionsMonitor<T>`，节名=类名约定保留，集中写一个扩展方法；`IPostConfigureOptions<T>` 补默认值。

## §0 映射总表

| 参照 | 本仓现状/落点 | 处置 |
|---|---|---|
| `Option/` 18 个 `IConfigurableOptions<T>` 类（Furion 标记接口+`PostConfigure` 钩子；字段=配置节字段） | **零 Option 类**；配置全散读（`config["节:键"]`/`GetSection`，盘点 8 节：Jwt/DbSettings/Cache/Yit/SysPassword/Otp/Cryptogram/Upload） | **建**：`Core/Option/` 8 个 Option POCO（只建实际消费节，#8——参照 18 类含 Email/SMS/Wechat/Alipay 等本仓无消费业务件，不造空壳） |
| `AddConfigurableOptions<T>`（Furion：按类名=节名反射自动绑定+`PostConfigure` 钩子） | 无 | **建**：`AddProjectOptions(IServiceCollection, IConfiguration)` 集中扩展方法（原生 `services.Configure<T>(config.GetSection(节名))` ×8，节名=类名约定保留） |
| `IOptions<T>.Value` 消费（`_codeGenOptions = codeGenOptions.Value` 模式） | 零 IOptions 消费（仅鉴权 scheme 自带 Options，非 6.8 范围） | **代表性迁移 2 处**（裁决①）：`AuthService`（Jwt:RefreshTokenExpireHours/Otp:Issuer 两散读→`IOptions<JwtOptions>`/`IOptions<OtpOptions>`）＋`Program.cs AddJwtBearer` 块（builder.Configuration 散读→`sp.GetRequiredService<IOptions<JwtOptions>>()`，.NET 8+ 双参回调重载）——证明闭环+示范口径；**其余散读点不迁**（#8 最小必要：AddSqlSugar/AddCache/CryptogramHelper/SysFileService/SysUserService 各自工厂/静态消费路径改造 ROI 低且回归面大，保持 IConfiguration 直读不冲突） |
| `PostConfigure` 补默认值钩子 | 无 | 采用 `IPostConfigureOptions<T>`（裁决②）：仅 `JwtOptions`（ExpireHours 缺省 8/RefreshTokenExpireHours 缺省 168）与 `UploadOptions`（列表缺省空集语义）落默认值；其余节配置缺省由各消费点既有缺省逻辑保留（不搬不删） |
| 动态配置（Config 表）分工 | 已建（动态 ConfigId 体系） | 不动（手册口径：静态 Option 与动态 Config 表分工明确——已建） |

**参照实证**：参照 Option 体系 = 18 个业务 Option 类（Email/SMS/Wechat/Alipay/ElasticSearch/Mqtt/OAuth/...本仓均无对应业务）＋Furion 反射自动绑定。本仓 6.8 缺件 = **Option POCO + 集中绑定 + 消费示范**——本批按本仓实际 8 节落地。

## §1 参照原文要点

```csharp
// CacheOptions.cs(参照,去版权头)
public sealed class CacheOptions : IConfigurableOptions<CacheOptions>
{
    public string Prefix { get; set; }
    public string CacheType { get; set; }
    public RedisOption Redis { get; set; }
    public void PostConfigure(CacheOptions options, IConfiguration configuration)
    {
        options.Prefix = string.IsNullOrWhiteSpace(options.Prefix) ? "" : options.Prefix.Trim();
    }
}
// Web.Core/ProjectOptions.cs(参照):AddConfigurableOptions<T> 按类名=配置节名绑定(Furion)
```

## §2 本仓现状（散读盘点）

| 节 | 消费点 | 字段（本仓实际） |
|---|---|---|
| `Jwt` | Program.cs `AddJwtBearer` 块（Issuer/Audience/Secret）＋`AuthService`（RefreshTokenExpireHours） | Secret/Issuer/Audience/ExpireHours/RefreshTokenExpireHours |
| `DbSettings` | `AddSqlSugar`（EnableIncreTable/SlowSqlSeconds） | 两键 |
| `Cache` | `AddCache`（Type） | Type |
| `Yit` | `AddSqlSugar`（WorkerId） | WorkerId |
| `SysPassword` | `SysUserService`（StrengthExpression） | StrengthExpression |
| `Otp` | `AuthService`（Issuer） | Issuer |
| `Cryptogram` | `CryptogramHelper`（Key） | Key |
| `Upload` | `SysFileService`（ContentType 白名单/AllowSuffix 白名单，GetSection 子项集合） | ContentType(string[])/AllowSuffix(string[]) |

## §3 设计（本批实施口径）

1. `src/Core/Option/` 8 个 sealed POCO（`Core.Option` 命名空间）：`JwtOptions`/`DbSettingsOptions`/`CacheOptions`/`YitOptions`/`SysPasswordOptions`/`OtpOptions`/`CryptogramOptions`/`UploadOptions`——字段=上表，**属性可空默认 null/空集**（配置缺省时消费点既有缺省逻辑兜底，Option 层不做业务判断）；`UploadOptions` 两 `List<string>` 缺省空集。
2. `src/Core/Extensions/ProjectOptionsExtensions.cs`：`AddProjectOptions(this IServiceCollection, IConfiguration)` 集中 8 行 `Configure<T>(config.GetSection("节名"))`（节名=类名去 Options 后缀约定，手写显式不反射——零 Furion 红线；参照 AddConfigurableOptions 的自动反射在本仓 8 节规模下不划算，#8）。
3. `JwtOptionsPostConfigure`（`IPostConfigureOptions<JwtOptions>`）：ExpireHours `<=0`→8、RefreshTokenExpireHours `<=0`→168（与 AuthService 既有缺省口径一致——**双保险**：消费点缺省保留，Option 层同步落默认值供其他消费方取一致值）。
4. 装配：`Program.cs` `AddSqlSugar` 旁加 `builder.Services.AddProjectOptions(builder.Configuration)`。
5. 代表性迁移（闭环证明）：`AuthService` 构造器注入 `IOptions<JwtOptions>`＋`IOptions<OtpOptions>`，**Jwt 节全 7 处散读＋Otp:Issuer 共 8 处**改 `options.Value.*`（`_config` 仍保留——非 Jwt/Otp 节散读不在本批）。`Program.cs` `AddJwtBearer` 块**保留 IConfiguration 直读**（实证更正见 §5 ⑦：JwtBearer 10.0.11 无双参回调重载，builder 世界无 sp 注入通道；两处配置源同节同值无漂移）。
6. **不做**：18 类全量对齐（Email/SMS/Wechat 等无业务消费节不造空壳）；其余 5 处散读点迁移（#8）；`IOptionsMonitor` 动态热更演示（本仓配置无运行期变更场景，按需）。

## §4 验证口径

`BatchQIOptionsTest` 用例：
1. **8 节绑定**：in-memory config 全 8 节填值 → `GetRequiredService<IOptions<T>>().Value` 逐字段断言
2. **缺省回落**：空 config → JwtOptions PostConfigure 缺省（ExpireHours=8/RefreshTokenExpireHours=168）；UploadOptions 空集
3. **`<=0` 回落**：`Jwt:RefreshTokenExpireHours=-1` → 168（PostConfigure 口径，与 AuthService 消费口径一致）
4. **代表性迁移闭环**：`AddProjectOptions` + 注册 `AuthService` 依赖 → `GenerateRefreshToken` 时效读 Option 值生效（配置 24 → 差 24h±1h）——证明 IOptions 链路真消费
探针用户 finally 清理；真实 PG（用例 4）；用例 1–3 纯 DI 无 DB。
**测试容器模板口径（批 Q 闭合时确立）**：所有构造 `AuthService` 等 IOptions 消费方的测试容器，装配链须含 `services.AddProjectOptions(config)`（镜像生产 Program.cs）——缺失则 IOptions 解析到空值实例，消费方"缺少 Jwt:Secret"快速失败（全量首跑 9 回归实证）。本批已将 17 个既有 Batch*Test 容器统一补行（机械一行，含 BatchJ/BatchK 补 using）。

## §5 参照偏差声明

1. **代表性迁移非全量**（#8）：参照 18 类 Option 全量消费（`_xxxOptions = xxxOptions.Value` 模式遍布服务层）；本仓散读点仅 8 节，本批迁 AuthService 单点（Jwt 7 处+Otp:Issuer 共 8 处全迁）证明闭环+立口径，其余散读点保留 IConfiguration 直读（各消费点既有缺省逻辑不动，零回归面）——后续业务 Option 消费天然走 IOptions 口径。Program.cs AddJwtBearer 块保留 IConfiguration（实证更正见 ⑥）。
2. **`IPostConfigureOptions<T>` 替代参照 `PostConfigure` 实例钩子**（参照是 Furion `IConfigurableOptions<T>` 接口方法，原生无此接口——手册既定替换口径）。
3. **`AddProjectOptions` 手写 8 行 Configure 替代参照 `AddConfigurableOptions<T>` 反射**（节名=类名约定保留；零 Furion 红线；8 节规模反射不划算，#8）。
4. **18 类→8 类**（只建实际消费节；参照 Email/SMS/Wechat/Alipay/ElasticSearch/Mqtt/OAuth/CodeGen/APIJSON/Enum 等节本仓无业务消费，不造空壳）。
5. **`ConnectionStrings` 节不建 Option**（多库 ConnectionConfigs 是 `AddSqlSugar` 既有结构化读取路径——`List<ConnectionConfig>` 直接绑定已有，再建 DbConnectionOptions 平行冗余，#11 复用既有）。
6. **实证更正：JwtBearer 10.0.11 无 `AddJwtBearer(Action<IServiceProvider, JwtBearerOptions>)` 双参回调重载**（探测工程实证 CS1660 "Cannot convert lambda expression to type 'string'"）——`Program.cs` AddJwtBearer 块保留 IConfiguration 直读（builder 世界标准通道）；DI 世界消费（AuthService）走 IOptions。代表性迁移范围修正为 AuthService 单点（8 处散读全迁）。

## §6 范围边界（不做）

- 业务 Option 类（Email/SMS/Wechat 等——无业务消费）。
- `IOptionsMonitor` 动态热更（无运行期配置变更场景）。
- 动态配置（Config 表）体系——已建，不动。
