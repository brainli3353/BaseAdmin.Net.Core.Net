using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Logging
{
    /// <summary>
    /// 文件日志通道选项(5.2 多通道:与内建 Console 通道并列,经 Microsoft.Extensions.Logging 原生抽象装配)。
    /// 配置段 "Logging:File"(Logging.json 模块配置):缺省 Enabled=true / Path="logs" / MinimumLevel=Information。
    /// </summary>
    public class FileLoggerOptions
    {
        /// <summary>通道开关(false→AddFileLogger 不挂 provider;缺省 true)。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>日志目录(相对 content root 或绝对路径;缺省 "logs")。</summary>
        public string Path { get; set; } = "logs";

        /// <summary>通道缺省最低级别(低于此级不写文件;category 级过滤仍由内建 LogLevel 过滤器负责)。</summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    }
}
