using System.Text.Json;

namespace AdminCore.Test;

/// <summary>
/// 批 BS「sysNotice 通知公告复审」测试:被测主体 = 错误文案 <c>notice.published_undeletable</c> 的语义完整性
/// (已发布禁删的提示不得引导用户执行**不存在的操作**)。
/// </summary>
/// <remarks>
/// WHY 只测文案:本批唯一缺陷 D1＝移植时自加「请先撤回」引导,但撤回功能全链不存在
/// (无端点/无按钮/无 Cancel 态设置路径/参照 D7001 本身为中性文案)⇒ 用户被引导进死胡同。
/// 断言口径:zh 不含「撤回」、en 不含 "withdraw"(**不承诺不存在的操作**),
/// 且保留核心语义(zh「不可删除」/en "cannot be deleted")——防"改文案时把禁删语义也改掉"。
/// WHY 直接读 Lang.*.json 落盘文件而非 LocalizationDefaults:后者 internal 且与文件是"同源双份",
/// 读文件同时覆盖"资源文件"这一真机加载层(Defaults 仅保底层)。
/// </remarks>
public class BatchBSNoticeTest
{
    // 仓库根定位:测试宿主 cwd 与 AppContext.BaseDirectory 落点随构建态漂移(批 BQ 实测三种形态)——
    // 向上探测第一个同时含 web/ 与 src/ 子目录的祖先目录,稳定不随构建态变
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "web")) && Directory.Exists(Path.Combine(dir.FullName, "src"))))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// D1 防复发:已发布禁删文案不得承诺「撤回」(全链无撤回操作——端点/按钮/Cancel 态设置路径均不存在),
    /// 且保留禁删核心语义(zh「不可删除」/en "cannot be deleted")。
    /// </summary>
    [Fact]
    public void D1_已发布禁删文案不承诺不存在的撤回操作()
    {
        var zh = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Core", "Resources", "Lang.zh-CN.json")));
        var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Core", "Resources", "Lang.en.json")));

        var zhMsg = zh.RootElement.GetProperty("notice.published_undeletable").GetString()!;
        var enMsg = en.RootElement.GetProperty("notice.published_undeletable").GetString()!;

        Assert.False(zhMsg.Contains("撤回"),
            $"zh 文案承诺了不存在的「撤回」操作(全链无撤回端点/按钮/Cancel 态设置路径):{zhMsg}");
        Assert.False(enMsg.Contains("withdraw", StringComparison.OrdinalIgnoreCase),
            $"en 文案承诺了不存在的 withdraw 操作(同口径):{enMsg}");

        Assert.True(zhMsg.Contains("不可删除"), $"zh 文案须保留禁删核心语义:{zhMsg}");
        Assert.True(enMsg.Contains("cannot be deleted", StringComparison.OrdinalIgnoreCase), $"en 文案须保留禁删核心语义:{enMsg}");
    }
}
