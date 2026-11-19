using System.Globalization;

namespace AdminCore.Core.Localization
{
    /// <summary>
    /// 本地化静态访问器(批 AL 6.6):供**任意位置**(含无 DI 的异常构造、静态方法)取当前请求语言的文案。
    /// 解析器由 <c>AddAdminLocalization()</c> 注册的服务在构造时装载(未装载 ⇒ 原样返回 key,永不抛、永不吞)。
    /// 用法约定:新增面向用户的固定文案用资源键(见 Core/Resources/Lang.*.json);存量中文文案保持原样(渐进式)。
    /// </summary>
    public static class L
    {
        /// <summary>资源解析器(key → 当前语言文案;由 AddAdminLocalization 注入的服务构造期装载)。</summary>
        internal static Func<string, string>? Resolver;

        /// <summary>取当前语言文案:命中资源则翻译,未命中原样返回入参(便于检出漏配键)。</summary>
        public static string T(string key)
            => string.IsNullOrEmpty(key) ? key : Resolver?.Invoke(key) ?? key;

        /// <summary>当前请求语言(未装载解析器时返回进程默认 UI 语言)。</summary>
        public static string CurrentCulture => CultureInfo.CurrentUICulture.Name;
    }
}