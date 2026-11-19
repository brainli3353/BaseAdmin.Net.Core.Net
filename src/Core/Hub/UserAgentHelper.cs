namespace AdminCore.Core.Hub
{
    /// <summary>
    /// 最小 User-Agent 解析(批 F 1.8):在线用户列表的 Browser/Os 展示列。
    /// 参照 AdminNetPro GetClientBrowser/GetClientOs(依赖 Furion 扩展)的零 Furion 替代——
    /// 关键词顺序判定(Edge 先于 Chrome:Edge UA 含 "Chrome" 字样,顺序颠倒会误判),识别不了回退 Other。
    /// </summary>
    public static class UserAgentHelper
    {
        /// <summary>浏览器(Edge/Chrome/Firefox/Safari/Other;null/空 UA 返回 Other)。</summary>
        public static string ParseBrowser(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                return "Other";
            }

            // Edge UA 同时含 Chrome 字样,必须先判 Edge
            if (userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("Edge/", StringComparison.OrdinalIgnoreCase))
            {
                return "Edge";
            }

            if (userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("Chromium/", StringComparison.OrdinalIgnoreCase))
            {
                return "Chrome";
            }

            if (userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase))
            {
                return "Firefox";
            }

            if (userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase)
                && !userAgent.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            {
                return "Safari";
            }

            return "Other";
        }

        /// <summary>操作系统(Windows/macOS/Linux/Android/iOS/Other;null/空 UA 返回 Other)。</summary>
        public static string ParseOs(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                return "Other";
            }

            if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows";
            }

            if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("iOS", StringComparison.OrdinalIgnoreCase))
            {
                return "iOS";
            }

            if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
            {
                return "Android";
            }

            // macOS 判定须在 Windows 之后(iOS UA 含 "like Mac OS X",但 iPhone/iPad 已先行判定)
            if (userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
            {
                return "macOS";
            }

            if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase))
            {
                return "Linux";
            }

            return "Other";
        }
    }
}
