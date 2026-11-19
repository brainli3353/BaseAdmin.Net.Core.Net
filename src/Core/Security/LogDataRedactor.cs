using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdminCore.Core.Security
{
    /// <summary>
    /// 审计日志数据脱敏器(安全出口止血):操作日志采集到的请求参数/响应结果在入库前统一过本器,
    /// 按「敏感属性名」与「otpauth:// 字符串值」两类规则把凭据替换为 <c>"***"</c>,并做长度上限截断。
    /// </summary>
    /// <remarks>
    /// 设计口径:
    /// ① 纯静态、零第三方依赖(仅 BCL System.Text.Json),可在 Web.Core/Web.Entry/测试任意层引用;
    /// ② 永不抛异常——脱敏失败绝不阻断业务请求(与 OpLogFilter 采集失败仅告警同口径);
    /// ③ 能按 JSON 解析 ⇒ 递归遍历遮蔽后输出紧凑 JSON;不能解析 ⇒ 只截断原文(不是 JSON 就无从定位敏感字段);
    /// ④ 长度上限 <see cref="MaxLength"/>:超限截断并追加 <see cref="TruncatedSuffix"/>,防 SysMenu/Page 类
    ///    26KB 整包响应把审计表撑爆。
    /// 敏感属性名匹配为**大小写不敏感的包含匹配**(如 updatePassword/resetPwd/sysOpenAccess
    /// 等动作参数名、otpauthUri 等均能被覆盖)。
    /// </remarks>
    public static class LogDataRedactor
    {
        /// <summary>输出长度上限(字符数)。</summary>
        public const int MaxLength = 4096;

        /// <summary>截断标记。</summary>
        public const string TruncatedSuffix = "...[truncated]";

        /// <summary>遮蔽占位。</summary>
        public const string Mask = "***";

        // 敏感属性名片段(命中即整值遮蔽);统一小写比较,故表内全小写
        private static readonly string[] SensitiveKeyFragments =
        {
            "password", "passwd", "pwd", "secret", "cipher", "token", "otpauth",
            "accesskey", "privatekey", "credential", "authorization", "cookie", "session"
        };

        private static readonly JsonSerializerOptions CompactJsonOptions = new()
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 脱敏入口:null/空白 → null;可解析 JSON → 递归遮蔽后紧凑输出并截断;解析失败 → 原文截断(不抛异常)。
        /// </summary>
        public static string? Redact(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string output;
            try
            {
                var node = JsonNode.Parse(text);
                if (node is null)
                {
                    // JSON 字面量 null:无敏感字段可遮,保持原文
                    output = text;
                }
                else
                {
                    RedactNode(node);
                    output = node.ToJsonString(CompactJsonOptions);
                }
            }
            catch (Exception)
            {
                // 非 JSON / 解析异常:宁缺勿错,只做截断,绝不阻断请求
                output = text;
            }

            return Truncate(output);
        }

        private static string Truncate(string text)
        {
            return text.Length > MaxLength
                ? string.Concat(text.AsSpan(0, MaxLength), TruncatedSuffix)
                : text;
        }

        private static void RedactNode(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    // ToList 快照:循环体内要改写属性值,不能边遍历边改
                    foreach (var key in obj.Select(kv => kv.Key).ToList())
                    {
                        if (IsSensitiveKey(key))
                        {
                            obj[key] = Mask;
                            continue;
                        }

                        var value = obj[key];
                        if (IsOtpAuthString(value))
                        {
                            obj[key] = Mask;
                            continue;
                        }

                        RedactNode(value);
                    }
                    break;

                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (IsOtpAuthString(arr[i]))
                        {
                            arr[i] = Mask;
                            continue;
                        }

                        RedactNode(arr[i]);
                    }
                    break;

                default:
                    // 标量(数字/布尔/普通字符串):无需处理
                    break;
            }
        }

        private static bool IsSensitiveKey(string key)
        {
            var lower = key.ToLowerInvariant();
            foreach (var fragment in SensitiveKeyFragments)
            {
                if (lower.Contains(fragment, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsOtpAuthString(JsonNode? node)
        {
            return node is JsonValue value
                && value.TryGetValue<string>(out var s)
                && s.Contains("otpauth://", StringComparison.OrdinalIgnoreCase);
        }
    }
}