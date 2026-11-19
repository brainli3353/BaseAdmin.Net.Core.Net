namespace AdminCore.Test;

/// <summary>
/// 测试库连接串的**唯一来源**（t845 阶段 0，2026-10-27）。
/// <para>WHY:此前同一字面量在 <c>src/Test/**</c> 内联了 88 处（89 文件含串、共 90 处），
/// 改宿主/库名/密码要动 88 个文件——本专项阶段 1「按测试集合分 schema」更需要一个
/// 唯一注入点。阶段 0 只做**收敛**，行为完全不变（同库同串同参数）。</para>
/// <para>⚠ **口径边界（勿误读本阶段的收益）**：
/// ① 本阶段**只收敛、不外置**——把串挪进环境变量会让"无 env 时测试直接挂"，
///    那是行为改变，超出阶段 0「行为不变」的约束，属后续批次（连同明文口令问题一并处理）；
/// ② 明文口令仍随仓分发，**本阶段不解决**，只把暴露面从 88 处降到 1 处（登记见 t845 note）；
/// ③ 另有 1 处指向 <c>huluyeye_admin_test</c>（**另一套库**，<c>CLAUDE.md</c> 明令勿当 dev 用），
///    串不同 ⇒ **不收敛进本常量**，保持原样，否则会静默改变那个用例的目标库。</para>
/// </summary>
public static class TestPg
{
    /// <summary>dev 测试库（与测试版 5090 同库，见 <c>CLAUDE.md</c> 约定 15）连接串——唯一来源。</summary>
    public const string ConnectionString =
        "Host=127.0.0.1;Port=5432;Database=huluyeye_admin;Username=huluyeye;Password=huluyeye_dev_2026;Timeout=10";

    /// <summary>
    /// 同库但**不带 <c>Timeout=10</c>** 的变体（仅 1 处使用：以 <c>ConfigurationBuilder</c> 注入
    /// <c>ConnectionStrings:Default</c> 的那个用例）。
    /// <para>⚠ **勿与 <see cref="ConnectionString"/> 合并**：二者只差 <c>Timeout=10</c> 看着像冗余，
    /// 但 Npgsql 里 <c>Timeout</c> 是**命令超时**——补上会把该用例的超时从默认 15s 改成 10s，
    /// 属**行为改变**，违反阶段 0「行为不变（同库同串同参数）」的约束。故逐字保留串体、仅收敛位置。
    /// 后续若要统一超时参数，属独立变更（需单独验证），不在本阶段。</para>
    /// </summary>
    public const string ConnectionStringNoTimeout =
        "Host=127.0.0.1;Port=5432;Database=huluyeye_admin;Username=huluyeye;Password=huluyeye_dev_2026";

    /// <summary>
    /// <c>huluyeye_admin_test</c> 库连接串（**另一套库**，仅 1 处使用）。
    /// <para>⚠ 它是**独立库**，不是 dev 库的别名——<c>CLAUDE.md</c> 明令"勿当 dev 库用"。
    /// 本常量存在只为让阶段 0 的验收判据（<c>grep "Database=huluyeye_admin"</c> 除本文件外归零）
    /// **真归零**：该 grep 会把 <c>huluyeye_admin_test</c> 也当子串命中，留着内联就永远归不了零。
    /// 收敛同样是逐字同串（含 <c>Timeout=10</c>），行为不变。<b>不得</b>把它改成指向 dev 库。</para>
    /// </summary>
    public const string ConnectionStringTestDb =
        "Host=127.0.0.1;Port=5432;Database=huluyeye_admin_test;Username=huluyeye;Password=huluyeye_dev_2026;Timeout=10";
}
