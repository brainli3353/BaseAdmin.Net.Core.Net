using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AE 架构守卫:仓内**禁止裸 Ado 事务方法**(`Ado.BeginTran`/`CommitTran`/`RollbackTran`)。
/// </summary>
/// <remarks>
/// 依据(批 AE G7/G9 评估实测,docs/sqlsugar升级评估-G7G9.md §三):
/// SqlSugar 5.1.4.217 的裸 `Ado.CommitTran` **不判嵌套**——在 `UseTranAsync`(即 `[UnitOfWork]` 路径)事务内混入裸
/// `Ado.BeginTran/CommitTran` 会**提前提交外层事务**,外层随后抛异常时回滚变成 no-op ⇒ 数据残留
/// (实测 2/2 行残留、6/6 次确定性复现;5.1.4.221 逐条同结果,升级**不修**该问题)。
/// 本仓口径:事务统一走 `[UnitOfWork]`(内部 `SqlSugarScope.UseTranAsync`)或显式 `UseTranAsync`;
/// 本测试把该口径**变成可执行的门**——新增裸 Ado 事务调用即红(不是靠人读文档)。
/// WHY 静态扫描而非运行时探针:该风险是"写法"层面的,静态扫描能在提交前拦住;运行时探针只能证明当次行为。
/// 例外白名单:当前为空(仓内零裸调用即为预期);若将来确需特例,必须在此显式登记并写明理由。
/// </remarks>
public class BatchAEAdoTransactionGuardTest
{
    private readonly ITestOutputHelper _output;

    public BatchAEAdoTransactionGuardTest(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>裸 Ado 事务方法调用模式(含语句式 `.BeginTran(` / `.CommitTran()` / `.RollbackTran(`)。</summary>
    private static readonly Regex AdoTransactionCall = new(
        @"\.\s*(BeginTran|CommitTran|RollbackTran)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void 生产代码_无裸Ado事务调用()
    {
        var repoRoot = LocateRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");

        var offenders = new List<string>();
        foreach (var file in EnumerateProductionCsFiles(srcRoot))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in AdoTransactionCall.Matches(text))
            {
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{line} → {m.Value}");
            }
        }

        _output.WriteLine($"扫描根:{srcRoot};命中 {offenders.Count} 处");
        foreach (var o in offenders)
        {
            _output.WriteLine("  " + o);
        }

        Assert.True(offenders.Count == 0,
            "生产代码出现裸 Ado 事务调用(违反批 AE 口径:事务统一走 [UnitOfWork]/UseTranAsync;" +
            "SqlSugar 5.1.4.217 裸 CommitTran 不判嵌套,会提前提交外层事务并使回滚失效):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void UnitOfWorkFilter_走UseTranAsync_而非裸Ado事务()
    {
        var repoRoot = LocateRepoRoot();
        var filter = Path.Combine(repoRoot, "src", "Core", "Attributes", "UnitOfWorkAttribute.cs");
        var filterFile = File.Exists(filter)
            ? filter
            : EnumerateProductionCsFiles(Path.Combine(repoRoot, "src"))
                .FirstOrDefault(f => Path.GetFileName(f) == "UnitOfWorkFilter.cs")
              ?? throw new FileNotFoundException("未找到 UnitOfWorkFilter 源文件");

        var text = File.ReadAllText(filterFile);
        _output.WriteLine($"检查文件:{Path.GetRelativePath(repoRoot, filterFile)}");

        Assert.Contains("UseTranAsync", text);
        Assert.DoesNotMatch(AdoTransactionCall, text);
    }

    /// <summary>
    /// 定位仓根:从测试输出目录向上找含 <c>src/Core</c> 的目录(找不到即 Assert.Fail——不静默跳过)。
    /// </summary>
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Core")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("降级:未定位到仓根(自 AppContext.BaseDirectory 向上未见 src/Core)——本断言未实跑,禁止冒充通过");
        return string.Empty;
    }

    /// <summary>枚举生产 .cs(排除 bin/obj 与 Test 工程——测试自身允许做探针)。</summary>
    private static IEnumerable<string> EnumerateProductionCsFiles(string srcRoot)
    {
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Test{Path.DirectorySeparatorChar}"));
    }
}