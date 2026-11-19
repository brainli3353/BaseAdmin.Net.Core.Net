using System.ComponentModel;

namespace AdminCore.Core.Enums
{
    /// <summary>
    /// 打印模板类型(批 AF 4.6;对齐参照仓 PrintTypeEnum 值域):1 浏览器打印 / 2 客户端打印。
    /// </summary>
    /// <remarks>
    /// 本仓口径:后端**只存模板不渲染**(见 SysPrintService remarks)——类型字段仅作存储与展示语义,
    /// 不代表本仓具备客户端打印通道(参照仓的客户端打印需部署外部 hiprint electron 客户端,本仓不引入)。
    /// </remarks>
    [Description("打印模板类型")]
    public enum PrintTypeEnum
    {
        /// <summary>浏览器打印(页面 window.print 或前端模板引擎)。</summary>
        [Description("浏览器打印")]
        Browser = 1,

        /// <summary>客户端打印(需外部客户端服务;本仓仅存储该标记)。</summary>
        [Description("客户端打印")]
        Client = 2
    }
}
