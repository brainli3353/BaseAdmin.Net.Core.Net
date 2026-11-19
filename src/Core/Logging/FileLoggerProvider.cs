using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Logging
{
    /// <summary>
    /// 文件日志 provider(5.2 多通道+按天切分,零 Furion 原生口径:实现 Microsoft.Extensions.Logging 抽象,
    /// 与内建 Console 通道并列)。按天切分:文件名 app-yyyy-MM-dd.log(日期入文件名,单目录平铺)。
    /// 写入:单 StreamWriter 惰性打开(append),写前检查日期→跨天重开;lock 保护;每条 Flush
    /// (管理端日志频率低,逐条刷盘可接受——最小必要;零第三方依赖,裁决 docs/研究/g3_refer_research.md §9 ⑥)。
    /// </summary>
    public sealed class FileLoggerProvider : ILoggerProvider
    {
        private readonly FileLoggerOptions _options;
        private readonly IHttpContextAccessor? _accessor; // 请求关联 id 的权威来源（可空：非 Web 宿主降级 Activity）
        private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
        private readonly object _writeLock = new();
        private StreamWriter? _writer;
        private string? _currentDate; // 当前打开文件对应的日期(yyyy-MM-dd);跨天即重开
        private volatile bool _disposed;
        private volatile bool _writeDisabled;   // 写失败后进入丢弃态（日志为旁路设施，不得因写失败打死进程）
        private bool _writeFailureNotified;     // 单次告警闸（stderr 只喊一次）

        public FileLoggerProvider(FileLoggerOptions options, IHttpContextAccessor? accessor = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _accessor = accessor;
        }

        public ILogger CreateLogger(string categoryName)
            => _loggers.GetOrAdd(categoryName, static (name, p) => new FileLogger(name, p), this);

        private void WriteLine(string line)
        {
            if (_disposed || _writeDisabled)
            {
                return;
            }

            lock (_writeLock)
            {
                if (_disposed || _writeDisabled)
                {
                    return;
                }

                // 日志是**旁路设施**：写失败（目录不可写／磁盘满／路径非法）必须降级为"丢弃"，
                // 绝不能把异常抛回 ILogger.Log 调用方——否则启动期日志失败会直接打死宿主进程，
                // 且没有 HTTP 错误面可看（上游回灌 2026-10-28，来源 fork 侧实证：日志路径不可写 ⇒ 进程退出）。
                try
                {
                    var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var file = Path.Combine(_options.Path, $"app-{date}.log");
                    if (_writer is null || date != _currentDate || !File.Exists(file))
                    {
                        // 首次打开/跨天/文件被外部删除(logrotate)重开:append 模式,目录缺失则创建
                        _writer?.Dispose();
                        Directory.CreateDirectory(_options.Path);
                        _writer = new StreamWriter(file, append: true, new UTF8Encoding(false)); // 无 BOM
                        _currentDate = date;
                    }

                    _writer.WriteLine(line);
                    _writer.Flush();
                }
                catch (Exception ex)
                {
                    // 进入丢弃态：不再重试、不再抛；单次告警走 stderr（不依赖日志设施自身）。
                    _writeDisabled = true;
                    TrySilentDisposeWriter();
                    if (!_writeFailureNotified)
                    {
                        _writeFailureNotified = true;
                        try
                        {
                            Console.Error.WriteLine(
                                $"[FileLogger] 日志写入失败，已切换为丢弃态（Path={_options.Path}）：{ex.GetType().Name}: {ex.Message}");
                        }
                        catch
                        {
                            // stderr 亦不可用时保持静默——日志设施的失败不得影响主流程。
                        }
                    }
                }
            }
        }

        /// <summary>丢弃态下的静默释放：释放失败不具处置价值（句柄随进程回收），不得再冒异常。</summary>
        private void TrySilentDisposeWriter()
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // 见方法注释：丢弃路径不处理释放异常。
            }

            _writer = null;
            _currentDate = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_writeLock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        /// <summary>单 category 的 logger:MinimumLevel 过滤 + 格式化输出(与 Console 通道信息对齐)。</summary>
        private sealed class FileLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly FileLoggerProvider _provider;

            public FileLogger(string categoryName, FileLoggerProvider provider)
            {
                _categoryName = categoryName;
                _provider = provider;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; // 文件通道无 scope 语义(与 Console 对齐)

            public bool IsEnabled(LogLevel logLevel)
                => !_provider._disposed && logLevel >= LogLevel.Trace && logLevel >= _provider._options.MinimumLevel;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var sb = new StringBuilder(160);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                sb.Append(' ').Append(logLevel.ToString());
                // 请求关联标识 ctx（纵深期 W6·2026-10-27 修正实现）：**权威来源＝`HttpContext.TraceIdentifier`**——
                // 本仓既有三面（响应头 `X-Trace-Id`〔TraceIdMiddleware〕／操作日志 `SysLogOp.TraceId`〔OpLogFilter〕／
                // 异常档 `SysLogEx.TraceId`）取的都是它，日志只有取**同一个值**才串得起来。
                // ⚠ 原实现取 `Activity.Current?.TraceId` 经真机证伪：默认无 ActivityListener 订阅 ⇒ Kestrel 不为请求
                //   创建 Activity ⇒ 请求管道内 `Activity.Current == null`（真实请求根本不打 ctx，只有启动期/个别自带
                //   Activity 的中间件会打）；而响应头此时落到 `ConnectionId:RequestNumber` 回落，形如
                //   `0HNPJUGD9THAT:00000001`，与 32 位 hex 的 TraceId **不同值、不同形**。
                // Activity 仅作**回落**（后台线程若确有 Activity 仍可用）；两者都取不到则整段不输出（不打空串）。
                // （内层 FileLogger 经 _provider 取外层字段：nested 类可访问外层 private，但需实例引用——CS0120 实测）
                var correlationId = _provider._accessor?.HttpContext?.TraceIdentifier;
                if (string.IsNullOrEmpty(correlationId))
                {
                    var traceId = Activity.Current?.TraceId ?? default;
                    if (!traceId.Equals(default(ActivityTraceId)))
                    {
                        // ActivityTraceId 是 struct 且**没有 IsZero**（凭 ActivitySpanId 记忆写会 CS1061，strict 门实测）
                        correlationId = traceId.ToString();
                    }
                }
                if (!string.IsNullOrEmpty(correlationId))
                {
                    sb.Append(" [ctx=").Append(correlationId).Append(']');
                }

                sb.Append(" [").Append(_categoryName).Append(']');
                if (eventId.Id != 0)
                {
                    sb.Append(" [EventId=").Append(eventId.Id).Append(']');
                }

                sb.Append(' ').Append(formatter(state, exception));
                if (exception is not null)
                {
                    sb.Append(Environment.NewLine).Append(exception);
                }

                _provider.WriteLine(sb.ToString());
            }
        }
    }
}
