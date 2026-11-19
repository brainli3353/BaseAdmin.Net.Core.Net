using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 缓存失效配对守卫:权限/身份类服务里的**变更方法体内必须出现缓存失效动作**,否则红。
/// </summary>
/// <remarks>
/// 依据(2026-10-27 用户裁定:"由于缓存未刷新导致的修改失效问题 必须避免"):
/// <c>KeyUserApiPerm</c>/<c>KeyUserOrg</c>/<c>KeyRoleMaxDataScope</c> 等 **TTL 均为 7 天** ⇒ 漏刷的表现
/// **不是报错,而是"管理员明明改了、过几天才生效"**,且**全量全绿也照样带病**(用户已因角色权限与账号类型踩过两次)。
/// 本件把 <c>docs/框架索引.md</c> §4.1 的配对面表**变成可执行的门**——新增变更方法漏刷缓存即在 CI 红,不靠人读。
/// WHY 静态扫描而非运行时探针:该风险是"写法"层面的,静态扫描能在提交前拦住,且不依赖真库与并发时序。
/// 例外白名单:**必须写理由**;口径变更须与该表**同批**修改(禁止只改代码不改表,反之亦然)。
/// </remarks>
public class BatchT48CacheInvalidationGuardTest
{
    private readonly ITestOutputHelper _output;

    public BatchT48CacheInvalidationGuardTest(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 受管服务目录(=该域存在缓存键且被读侧使用)。**新增权限/身份类服务时必须同步加入本表**,
    /// 否则该服务不受本守卫保护(目录不存在即红,防被重命名后静默失效)。
    /// </summary>
    private static readonly string[] GuardedDirs =
    {
        "Menu", "User", "Role", "Org", "Dict", "Config", "RoleTable", "OpenAccess", "ColumnCustom", "Tenant"
    };

    /// <summary>
    /// 需要失效的变更动词。**不含 Add/Insert/Save/Import**——新增行尚无任何引用者,不存在"改了不生效"
    /// (本守卫首跑审计实测:受管目录里"缺失效"的方法几乎全落在这一类,故按语义排除而非逐条白名单)。
    /// </summary>
    private static readonly Regex MutatorSig = new(
        @"^\s{8}public\s+async\s+Task[^;{]*\b(Grant|Update|Set|Assign|Remove|Delete|Disable|Enable|Reset|Change|Bind|Unbind)\w*\s*\(",
        RegexOptions.Compiled);

    /// <summary>同级成员声明(切方法体用:遇到下一个成员即截断,防把邻居的失效动作算给自己)。</summary>
    private static readonly Regex MemberSig = new(
        @"^\s{8}(public|private|protected|internal)\s", RegexOptions.Compiled);

    /// <summary>
    /// 失效动作:显式失效方法 / 缓存删除 / 封装的一次删多键入口 / 事件广播(订阅者据此刷新)。
    /// ⚠ <c>DeleteUserOrgCache</c> 必须在内——<c>KeyRoleMaxDataScope</c> 只经由它失效,漏了会把好代码误判成缺陷。
    /// </summary>
    private static readonly Regex Invalidation = new(
        @"Invalidate|RemoveAsync|RemoveByPrefix|DeleteUserOrgCache|_cache\.Remove|Publish\w*\s*\(",
        RegexOptions.Compiled);

    /// <summary>显式豁免(文件名片段, 方法名, 理由)。**禁止无理由扩表**。</summary>
    private static readonly (string File, string Method, string Why)[] Whitelist =
    {
        ("SysMenuService.cs", "SetStatusAsync",
            "设计口径:停用按钮≠回收 API 权限(只作用于登录菜单树),该口径由 BatchCHMenuStatusTest 守"),
    };

    [Fact]
    public void 权限类服务的变更方法_方法体内必须含缓存失效动作()
    {
        var repoRoot = FindRepoRoot();
        var servicesRoot = Path.Combine(repoRoot, "src", "Core", "Services");
        Assert.True(Directory.Exists(servicesRoot), $"未找到服务目录:{servicesRoot}");

        var offenders = new List<string>();
        var scanned = 0;
        var exempted = 0;

        foreach (var dir in GuardedDirs)
        {
            var full = Path.Combine(servicesRoot, dir);
            if (!Directory.Exists(full))
            {
                offenders.Add($"受管目录不存在(被重命名/移动?若已废弃请同步本表与 §4.1):Services/{dir}");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!MutatorSig.IsMatch(lines[i]))
                    {
                        continue;
                    }

                    var methodName = Regex.Match(lines[i],
                        @"\b((?:Grant|Update|Set|Assign|Remove|Delete|Disable|Enable|Reset|Change|Bind|Unbind)\w*)\s*\(").Groups[1].Value;

                    var hit = Whitelist.FirstOrDefault(w =>
                        Path.GetFileName(file).Contains(w.File, StringComparison.OrdinalIgnoreCase) && w.Method == methodName);
                    if (hit.Why is not null)
                    {
                        exempted++;
                        continue;
                    }

                    scanned++;
                    var body = new StringBuilder();
                    for (var j = i + 1; j < lines.Length && j < i + 400; j++)
                    {
                        if (MemberSig.IsMatch(lines[j]) && lines[j].Contains('('))
                        {
                            break;
                        }

                        body.Append(lines[j]);
                    }

                    if (!Invalidation.IsMatch(body.ToString()))
                    {
                        offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1} {methodName}() → 方法体内无任何缓存失效动作");
                    }
                }
            }
        }

        _output.WriteLine($"受管目录 {GuardedDirs.Length} 个;扫描变更方法 {scanned} 个;豁免 {exempted} 个。");
        foreach (var o in offenders)
        {
            _output.WriteLine(o);
        }

        Assert.True(offenders.Count == 0,
            $"权限/身份类变更方法缺失缓存失效({offenders.Count} 处):\n{string.Join("\n", offenders)}\n" +
            "⇒ 修法:在变更方法内调用对应缓存键的失效入口(键→入口对照见 docs/框架索引.md §4.1);" +
            "确属无需失效的特例,须同批在该表与本件 Whitelist 写明理由。");
    }

    private static string FindRepoRoot()
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

        Assert.Fail("未定位到仓根(自 AppContext.BaseDirectory 向上未见 src/Core)");
        return string.Empty;
    }
}
