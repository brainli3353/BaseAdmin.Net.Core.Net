using System.Text.Encodings.Web;
using System.Text.Json;

namespace AdminCore.Core.Json;

/// <summary>
/// 管理端 JSON 序列化**出口单源**（G33）：long id 的字符串化策略 + camelCase + 中文不转义，
/// 集中一处供所有 JSON 出口取用，避免「每个控制器/DTO 自觉转 string」漏配即静默错值。
/// </summary>
/// <remarks>
/// WHY 单源：仓内 JSON 出口分散（MVC AddJsonOptions、异常中间件 ApiResult.Json、
/// PermissionMiddleware 401、SignalR PayloadSerializerOptions 等），此前各自 new JsonSerializerOptions
/// 并手工重复 camelCase/Encoder——G33 起统一经本类，新增出口挂一次即继承 long→string 约定。
/// Read 侧向后兼容 number，故本约定对旧客户端/旧调用方是**加引号而非破坏**。
/// 内部面（缓存/EventBus/OpLog/HttpLogging/ApiDoc/ColumnCustom/SqlSugar）**不接**本单源：
/// 那些是进程内往返，字符串化无收益且徒增 CPU（G33 定稿口径）。
/// 未挂的对外同类出口：ApiKeyAuthenticationHandler / SignatureAuthenticationHandler 的 JsonOptions
/// 当前仅序列化 ApiResult.Fail（int code＋string，无 long）⇒ 暂不接；**未来其响应若新增 long 字段，必须改接本单源**（防漂移注，2026-10-25 父裁）。
/// </remarks>
public static class AdminJsonOptions
{
    /// <summary>
    /// 就地把 G33 出口规则挂到既有 <see cref="JsonSerializerOptions"/>（供 MVC/SignalR 这类
    /// 「属性只读、须原地变更默认实例」的挂点使用）。
    /// </summary>
    public static void Configure(JsonSerializerOptions o)
    {
        // 与控制器默认输出、全局异常中间件、PermissionMiddleware 统一响应保持同一套规则：
        // camelCase + 中文不转义（三处出口同构），再加 long→string（G33 主约定）
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        o.Converters.Add(new LongToStringJsonConverter());
    }

    /// <summary>返回一份挂了 G33 出口规则的新 options（供 static 字段/独立出口使用）。</summary>
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        return options;
    }
}