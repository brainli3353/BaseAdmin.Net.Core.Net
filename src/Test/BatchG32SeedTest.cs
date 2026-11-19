using AdminCore.Core.Extensions;
using AdminCore.Core.Option;
using AdminCore.Core.Services.Seed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 G32:命令式启动种子链(IStartupSeed + StartupSeedRunner)——**纯 DI 无库**。
/// C1 Order 串行序 / C2 坏 seed fail-fast 断后续 / C3 ContinueOnFailure 跑完且计数对 /
/// C4 Enabled=false 零执行 + Warning 含清单名 / C5 GetStatus 快照读数。
/// </summary>
public class BatchG32SeedTest
{
    /// <summary>
    /// 与生产同一条装配路径:AddAutoServices(Core 程序集,解析 [Service] 的 StartupSeedRunner)
    /// + Configure&lt;StartupSeedOptions&gt; + 本用例显式注册的探针种子(无 DB 依赖)。
    /// </summary>
    private static ServiceProvider Build(List<string> sink, bool enabled, bool continueOnFailure, params IStartupSeed[] seeds)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new CapturingProvider(sink)));
        services.AddAutoServices(typeof(StartupSeedRunner).Assembly);
        services.Configure<StartupSeedOptions>(o =>
        {
            o.Enabled = enabled;
            o.ContinueOnFailure = continueOnFailure;
        });
        foreach (var seed in seeds)
        {
            services.AddScoped<IStartupSeed>(_ => seed);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task G32_C1_按Order升序串行执行()
    {
        var trace = new List<string>();
        var sink = new List<string>();
        // 刻意乱序注册(30,10,20),断言执行序由 Order 决定而非注册序
        using var provider = Build(sink, enabled: true, continueOnFailure: false,
            new SeedC(trace), new SeedA(trace), new SeedB(trace));

        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<StartupSeedRunner>();
        await runner.RunAllAsync(scope.ServiceProvider);

        Assert.Equal(new[] { "SeedA", "SeedB", "SeedC" }, trace);
    }

    [Fact]
    public async Task G32_C2_坏seed缺省failFast断后续()
    {
        var trace = new List<string>();
        var sink = new List<string>();
        using var provider = Build(sink, enabled: true, continueOnFailure: false,
            new SeedA(trace), new FailSeed(trace), new SeedC(trace));

        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<StartupSeedRunner>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAllAsync(scope.ServiceProvider));

        // 坏 seed 之后那颗不得执行
        Assert.Equal(new[] { "SeedA", "FailSeed" }, trace);
        var status = runner.GetStatus();
        Assert.Equal(3, status.Registered);
        Assert.Equal(1, status.Executed);
        Assert.Equal(1, status.Failed);
        Assert.Equal(1, status.SkippedByToggle);
        Assert.Contains(sink, l => l.Contains("FAIL") && l.Contains(nameof(FailSeed)));
    }

    [Fact]
    public async Task G32_C3_ContinueOnFailure坏后仍跑完且Failed计数对()
    {
        var trace = new List<string>();
        var sink = new List<string>();
        using var provider = Build(sink, enabled: true, continueOnFailure: true,
            new SeedA(trace), new FailSeed(trace), new SeedC(trace));

        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<StartupSeedRunner>();
        await runner.RunAllAsync(scope.ServiceProvider); // 不抛

        Assert.Equal(new[] { "SeedA", "FailSeed", "SeedC" }, trace);
        var status = runner.GetStatus();
        Assert.Equal(3, status.Registered);
        Assert.Equal(2, status.Executed);
        Assert.Equal(1, status.Failed);
        Assert.Equal(0, status.SkippedByToggle);
        Assert.Contains(sink, l => l.Contains("[seed] 注册 3/执行 2/失败 1/跳过 0"));
    }

    [Fact]
    public async Task G32_C4_开关关闭零执行且Warning列未执行清单()
    {
        var trace = new List<string>();
        var sink = new List<string>();
        using var provider = Build(sink, enabled: false, continueOnFailure: false,
            new SeedA(trace), new SeedB(trace));

        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<StartupSeedRunner>();
        await runner.RunAllAsync(scope.ServiceProvider);

        Assert.Empty(trace);
        var status = runner.GetStatus();
        Assert.Equal(2, status.Registered);
        Assert.Equal(0, status.Executed);
        Assert.Equal(2, status.SkippedByToggle);
        Assert.Contains(sink, l => l.Contains("Warning") && l.Contains("SeedA") && l.Contains("SeedB") && l.Contains("Enabled=false"));
    }

    [Fact]
    public async Task G32_C5_GetStatus快照读数对()
    {
        var trace = new List<string>();
        var sink = new List<string>();
        using var provider = Build(sink, enabled: true, continueOnFailure: false,
            new SeedA(trace), new SeedB(trace));

        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<StartupSeedRunner>();

        // 未执行过 ⇒ 零快照
        Assert.Equal(StartupSeedStatus.Empty, runner.GetStatus());

        await runner.RunAllAsync(scope.ServiceProvider);

        var status = runner.GetStatus();
        Assert.Equal(2, status.Registered);
        Assert.Equal(2, status.Executed);
        Assert.Equal(0, status.Failed);
        Assert.Equal(0, status.SkippedByToggle);
        Assert.Equal(new[] { "SeedA", "SeedB" }, status.Names);
        Assert.Contains(sink, l => l.Contains("[seed] SeedA ok"));
    }

    /// <summary>探针种子基类:记录执行序,可选抛异常(坏 seed)。</summary>
    private abstract class ProbeSeed : IStartupSeed
    {
        private readonly string _name;
        private readonly List<string> _trace;
        private readonly bool _fail;

        protected ProbeSeed(int order, string name, List<string> trace, bool fail = false)
        {
            Order = order;
            _name = name;
            _trace = trace;
            _fail = fail;
        }

        public int Order { get; }

        public async Task RunAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            _trace.Add(_name);
            await Task.Yield();
            if (_fail)
            {
                throw new InvalidOperationException($"boom:{_name}");
            }
        }
    }

    private sealed class SeedA(List<string> trace, bool fail = false) : ProbeSeed(10, nameof(SeedA), trace, fail);

    private sealed class SeedB(List<string> trace, bool fail = false) : ProbeSeed(20, nameof(SeedB), trace, fail);

    private sealed class SeedC(List<string> trace, bool fail = false) : ProbeSeed(30, nameof(SeedC), trace, fail);

    private sealed class FailSeed(List<string> trace) : ProbeSeed(20, nameof(FailSeed), trace, fail: true);

    /// <summary>日志捕获器(同 BatchASClusterStartupGuardTest 口径):断言"说了什么"而不只是"返回了什么"。</summary>
    private sealed class CapturingProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose() { }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add($"{logLevel}:{formatter(state, exception)}");
                }
            }
        }
    }
}