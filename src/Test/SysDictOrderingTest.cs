using SqlSugar;
using AdminCore.Core.Entities;

namespace AdminCore.Test;

/// <summary>
/// task_011 fixa:字典值分页查询多列排序的离线 SQL 验证(不依赖 DB,任何环境可实跑)。
/// 背景:盲审发现 <c>.ThenByDescending(d =&gt; d.Id)</c> 编译错误 CS1061——ThenByDescending 是
/// System.Linq.Enumerable 的 IQueryable 扩展方法,本文件未用 ImplicitUsings 且无 using System.Linq,
/// 扩展方法不在符号表内;修复改为 SqlSugar 自有扩展的链式 <c>.OrderBy(...)</c> 追加排序列
/// (SqlSugar 多次 OrderBy 依次追加列,同 SysMenuService.TreeAsync 口径)。
/// 本用例离线生成 SQL(ToSql 只构造 SQL 串,不开连接),断言两列排序方向,
/// 证明修复后 PageAsync 排序语义仍为「OrderNo 升序 + Id 倒序」。
/// </summary>
public class SysDictOrderingTest
{
    /// <summary>
    /// 与 SysDictDataService.PageAsync 相同的链式排序(修复变更点:两次 OrderBy 调用)。
    /// 断言:ORDER BY 含 OrderNo(升序)与 id(降序)两列且升在前、降在后。
    /// </summary>
    [Fact]
    public void 分页查询_链式OrderBy_OrderNo升序且Id倒序()
    {
        // 占位连接串:ToSql 不打开连接,仅用于构造查询客户端
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Host=127.0.0.1;Port=5432;Database=offline;Username=offline;Password=offline",
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });

        // ToSql 返回 (SQL 串, 参数表);本用例只断言 SQL 串
        var (sql, _) = db.Queryable<SysDictData>()
            .OrderBy(d => d.OrderNo)
            .OrderBy(d => d.Id, OrderByType.Desc)
            .ToSql();

        // 实测生成口径(SqlSugar/PG):ORDER BY "orderno" ASC,"id" DESC;按逗号拆列项断言,容忍引号风格变化
        var orderClause = sql[sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase)..];
        var items = orderClause["ORDER BY".Length..].Trim().Split(',');
        Assert.Equal(2, items.Length);
        Assert.Contains("orderno", items[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ASC", items[0].Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id", items[1], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("DESC", items[1].Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
