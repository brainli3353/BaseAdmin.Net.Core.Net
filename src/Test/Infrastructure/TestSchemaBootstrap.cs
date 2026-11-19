using AdminCore.Core;
using AdminCore.Core.Extensions;
using SqlSugar;

namespace AdminCore.Test;

/// <summary>
/// 测试进程级 schema 引导闸门(2026-10-26 性能实验落地)。
/// <para>WHY:每个用例 <c>new DbScope()</c> 原本都要执行 <c>HealColumnCase</c>(实测 1406ms)
/// + <c>InitTables</c>(实测 5956ms,扫 50 张 [SugarTable] 逐表比对 information_schema)。
/// 两者都是幂等操作——"已有表仅补新增列,不删列",重复执行结果完全相同,但每次仍要付约 7.4s。
/// 全量 176 次调用合计约 21.7 分钟,占全量 47m39s 的 45%。</para>
/// <para>ALTERNATIVE:给每个 DbScope 各加一份静态标志会导致 59 份重复逻辑,故收敛到本类单点。</para>
/// <para>TRADE-OFF:闸门是进程级的 ⇒ 同一测试进程内 schema 只建一次。若某用例会改表结构
/// (当前无此类用例),须在改动后自行重建而非依赖下一次 DbScope。</para>
/// </summary>
internal static class TestSchemaBootstrap
{
    private static readonly object Gate = new();
    private static bool _done;

    /// <summary>保证本进程内 HealColumnCase + InitTables 只执行一次;后续调用立即返回。</summary>
    /// <param name="db">调用方的 SqlSugar 客户端(首次调用者负责建表)。</param>
    public static void Ensure(ISqlSugarClient db)
    {
        if (_done)
        {
            return;
        }

        lock (Gate)
        {
            if (_done)
            {
                return;
            }

            db.HealColumnCase();
            db.InitTables(typeof(SqlSugarSetup).Assembly);
            _done = true;
        }
    }
}
