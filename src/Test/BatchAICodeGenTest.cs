using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.CodeGen;
using AdminCore.Core.Services.CodeGen.Dto;
using AdminCore.Core.Services.Database;
using AdminCore.Core.Services.Database.Dto;
using AdminCore.Web.Entry.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AI 6.1「代码生成器」测试:被测主体 = <see cref="CodeGenService"/>
/// (<see cref="CodeGenService.GetTableListAsync"/> 表清单复用 / <see cref="CodeGenService.PreviewAsync"/> 三段文本生成)。
/// </summary>
/// <remarks>
/// WHY 需真实 PG:生成物的每一行都由"**真实 schema**(pg_catalog 里的列名/类型/可空/长度/注释) × 模板规则"合成,
/// 断言的正是这个合成结果(主键/审计列跳过、PG→C# 类型映射、长度与 IsNullable 特性、列序)——内存替身只能证明
/// "我喂了什么就吐什么"。PG 不可达时 Assert.Fail 显式降级(禁止冒充通过)。
/// <para>
/// WHY 期望值全部**从真实列结构反推**而非硬编码列清单:一旦库结构变化(加列/改类型),硬编码的期望会
/// "看着绿其实测的是旧 schema";本用例的期望 = 用例侧独立实现的 PG→C# 映射表(spec 副本)+ 独立 SQL 查询的类型清单,
/// 与实现形成**差分核对**(两边都写错同一处的概率远低于一边错)。
/// </para>
/// <para>
/// <b>零污染裁决(本批)</b>:全程只读——不插数据、不建表、不改 schema,故 DbScope **不调用** <c>InitTables</c>
/// 与 <c>HealColumnCase</c>(两者都会写 schema:建表 / ALTER RENAME COLUMN),代价是"库里没表就红"(前置已实证 40 张表存在)。
/// <see cref="生成_不写盘"/> 另证:被测链路零**文件系统**副作用。
/// </para>
/// <para>
/// <b>未覆盖分支登记(不造假数据)</b>:①"空壳表"warning 需要一张"除审计列外零业务列"的真实表——本库 40 张表扫描后
/// 为 0 命中,故该分支如实标"未覆盖"(用例先做前置取证,若将来出现该形态的表会自动真跑);②"未知列类型"本库亦无
/// (7 种真实类型全部命中映射表,已差分核实),故改用**反射直调私有 MapType** 覆盖该分支(任务书允许)。
/// </para>
/// </remarks>
public class BatchAICodeGenTest
{
    private readonly ITestOutputHelper _output;

    public BatchAICodeGenTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchADDatabaseTest 同源,仅开发用;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchADDatabaseTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    private const string UserTable = "sys_user";

    /// <summary>
    /// 与实现同口径的"审计列/主键列"集合(用例侧**独立声明**)。
    /// WHY 必须自己写一份:断言"某列被跳过"的前提是"我知道它**该**被跳过"——若直接读实现的私有集合当期望,
    /// 实现漏跳一列时用例会跟着漏判(永真)。
    /// </summary>
    private static readonly HashSet<string> AuditColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "createtime", "createuserid", "createusername", "createorgid",
        "updatetime", "updateuserid", "updateusername", "updateorgid",
        "deletetime", "isdelete", "tenantid"
    };

    /// <summary>生成的属性行(实体模板口径:<c>public {type} {name} {{ get; set; }}</c>)。</summary>
    private static readonly Regex PropertyLinePattern = new(
        @"^[ \t]*public ([^\s]+) ([A-Za-z_][A-Za-z0-9_]*) \{ get; set; \}\r?$", RegexOptions.Multiline);

    /// <summary>生成的服务骨架方法签名(返回类型可泛型,且**含嵌套泛型**如 <c>Task&lt;PageResult&lt;UserOutput&gt;&gt;</c>)。</summary>
    private static readonly Regex AsyncMethodPattern = new(
        @"public async Task(?:<[^()]*>)?\s+([A-Za-z0-9_]+)\(", RegexOptions.Multiline);

    // ─────────────────────────────────────────────────────────────
    // 1. 表清单:复用数据库管理只读查询(含 [Service] 自注册与端点/权限键面)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 表清单_复用数据库管理只读查询()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        // [Service] 自注册:从容器解析(取不到即红)——生成的 ServiceCode 里也带 [Service],此处先证**本服务自身**受同一机制管辖
        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var database = scope.Provider.GetRequiredService<SysDatabaseService>();

        var all = await codeGen.GetTableListAsync(null);
        Assert.NotEmpty(all);
        Assert.Contains(all, t => t.TableName == UserTable);

        // "复用"的可执行证据:同参调用下与 SysDatabaseService 直接调用**逐行完全一致**(平行实现会在此分叉)
        var direct = await database.ListTablesAsync(null);
        Assert.Equal(direct.Select(t => t.TableName).ToList(), all.Select(t => t.TableName).ToList());
        Assert.Equal(direct.Select(t => t.Comment).ToList(), all.Select(t => t.Comment).ToList());
        Assert.Equal(direct.Select(t => t.IndexCount).ToList(), all.Select(t => t.IndexCount).ToList());

        // keyword 过滤:每行必须"真的含关键字"(表名或注释,大小写不敏感)——防过滤条件被旁路成全量返回
        var hit = await codeGen.GetTableListAsync(UserTable);
        Assert.NotEmpty(hit);
        Assert.Contains(hit, t => t.TableName == UserTable);
        foreach (var row in hit)
        {
            var matched = row.TableName.Contains(UserTable, StringComparison.OrdinalIgnoreCase)
                || (row.Comment?.Contains(UserTable, StringComparison.OrdinalIgnoreCase) ?? false);
            Assert.True(matched, $"行 {row.TableName}(注释 {row.Comment})不含关键字 {UserTable},过滤条件疑似失效");
        }
        Assert.True(hit.Count < all.Count, $"关键字 {UserTable} 应缩小结果集(全量 {all.Count} 行,命中 {hit.Count} 行)");

        // 无匹配 → 空集(不是全量、也不是异常)
        var none = await codeGen.GetTableListAsync("不存在的表xyz");
        Assert.Empty(none);

        // 端点面(6.1 对外唯一面):路由 + 两端点各自的 [MenuPerm] 键——权限键与生成器同名漂移是"静默 403"类事故源,固化为门
        Assert.Equal("api/sysCodeGen", typeof(SysCodeGenController).GetCustomAttribute<RouteAttribute>()?.Template);
        var endpoints = typeof(SysCodeGenController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m.Name, Route: m.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault()?.Template,
                Perm: m.GetCustomAttribute<MenuPermAttribute>()?.Permission))
            .OrderBy(x => x.Method)
            .ToList();
        Assert.Equal(2, endpoints.Count);
        Assert.Equal(("Preview", "preview", "sysCodeGen/preview"), endpoints[0]);
        Assert.Equal(("Tables", "tables", "sysCodeGen/tables"), endpoints[1]);

        _output.WriteLine($"判定:全量 {all.Count} 张(含 {UserTable});与 SysDatabaseService 逐行一致;" +
                          $"keyword={UserTable} 命中 {hit.Count} 行;无匹配=0;端点={endpoints[0].Perm}/{endpoints[1].Perm}");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 实体类文本:结构 + 主键/审计列跳过 + 列结构逐行对应
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 生成_实体类文本含预期结构()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var database = scope.Provider.GetRequiredService<SysDatabaseService>();
        var preview = await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        var entity = preview.EntityCode;

        Assert.Equal(UserTable, preview.TableName);
        // sys_ 前缀去除 + PascalCase:sys_user → sys 之后的 "user" → User
        Assert.Equal("User", preview.ClassName);
        Assert.Contains("[SugarTable(\"sys_user\")]", entity);
        Assert.Contains("[IncreTable]", entity);
        // 批 BF:sys_user 含 TenantId 列 ⇒ 缺省基类推断为 EntityTenant(原断言 EntityBase 正是静默丢租户列的缺陷行为)
        Assert.Contains("public class User : EntityTenant", entity);
        Assert.Contains("[SugarColumn(", entity);

        // 列结构与属性行**逐行对应**(顺序 = attnum,与 ListColumnsAsync 的 OrdinalPosition 同序):跳过集合 + 类型 + 可空标记三者合成
        var columns = await database.ListColumnsAsync(UserTable);
        var expected = columns.Where(c => !AuditColumns.Contains(c.ColumnName))
            .Select(c => PropertyLine(Classify(c.DataType).CsType, c.IsNullable, PascalCase(c.ColumnName)))
            .ToList();
        var actual = ExtractPropertyLines(entity);
        Assert.Equal(expected, actual);

        // 主键跳过(列名 id):既不看得到 "public long Id",也没有任何名为 Id 的属性行
        Assert.DoesNotContain("public long Id", entity);
        Assert.DoesNotContain(actual, line => line.Split(' ')[1] == "Id");
        Assert.DoesNotContain(columns.Where(c => c.IsPrimaryKey).Select(c => c.ColumnName), n => actual.Any(l => l.Split(' ')[1] == PascalCase(n)));

        // 审计列跳过:实体文本**不含**这些列名(由基类 EntityBase/EntityTenant 提供,TenantId 在 EntityTenant)
        foreach (var audit in new[] { "CreateTime", "CreateUserId", "CreateUserName", "UpdateTime", "UpdateUserId", "UpdateUserName", "DeleteTime", "IsDelete", "TenantId" })
        {
            Assert.DoesNotContain(audit, entity);
        }

        // 真实产出片段(硬编码取证,防止"模板整体重写"时上面的结构性断言被绕过):
        // Account 是 varchar(64) NOT NULL → 实现只对非 string 类型追加 '?',故真实产出是 **public string Account**(无 '?')
        Assert.Contains("public string Account { get; set; }", entity);
        Assert.Contains("[SugarColumn(ColumnDescription = \"账号\", Length = 64)]", entity);
        // 可空 bigint → long? + IsNullable = true;可空 timestamp → DateTime?
        Assert.Contains("public long? OrgId { get; set; }", entity);
        Assert.Contains("[SugarColumn(ColumnDescription = \"组织Id(数据权限预留)\", IsNullable = true)]", entity);
        Assert.Contains("public DateTime? LastChangePasswordTime { get; set; }", entity);

        _output.WriteLine($"判定:ClassName={preview.ClassName};业务列 {actual.Count}/{columns.Count}(跳过 {columns.Count - actual.Count} 列);" +
                          $"片段=<<public string Account {{ get; set; }}>> <<public long? OrgId {{ get; set; }}>>");
    }

    // ─────────────────────────────────────────────────────────────
    // 3. Dto 组与服务骨架
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 生成_Dto与服务骨架()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var database = scope.Provider.GetRequiredService<SysDatabaseService>();
        var preview = await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        var dto = preview.DtoCode;
        var service = preview.ServiceCode;

        var columns = await database.ListColumnsAsync(UserTable);
        var business = columns.Where(c => !AuditColumns.Contains(c.ColumnName))
            .Select(c => (Name: PascalCase(c.ColumnName), Type: Classify(c.DataType).CsType, Nullable: c.IsNullable))
            .ToList();
        // 与生成器口径同步(修复 D):Output/Add 的可空列带 '?'
        var props = business.Select(c => $"{c.Type}{(c.Nullable && !c.Type.EndsWith("?") ? "?" : string.Empty)} {c.Name}").ToList();

        // ── Dto 组:四个 record 齐备(命名按实现口径 Page/Output/Add/Update + 类名)
        Assert.Contains("record PageUserInput", dto);
        Assert.Contains("record UserOutput", dto);
        Assert.Contains("record AddUserInput", dto);
        Assert.Contains("record UpdateUserInput", dto);

        // 分页入参:关键字可空 + 页参数默认值
        Assert.Equal("string? Keyword, int PageIndex = 1, int PageSize = 10", RecordParams(dto, "PageUserInput"));

        // 输出行:Id(主键,基类外显式带上) + 各业务列 + CreateTime —— 与真实列结构逐字段相同
        var outputParams = RecordParams(dto, "UserOutput").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Equal(new[] { "long Id" }.Concat(props).Append("DateTime? CreateTime").ToList(), outputParams);
        // 修复 D 后的行为断言:可空列(如 OrgId/PosId=bigint NULL、LastChangePasswordTime)在 Output/Add 里也带 '?',
        // 与实体属性行、Update 记录口径一致(此前"一律非空"与 IsNullable 语义矛盾)
        Assert.Contains("long? OrgId", outputParams);
        Assert.Contains("long? PosId", outputParams);
        Assert.Contains("DateTime? LastChangePasswordTime", outputParams);
        Assert.DoesNotContain("long OrgId", outputParams);
        Assert.DoesNotContain("DateTime LastChangePasswordTime", outputParams);

        // 新增入参:仅业务列(不含 Id/审计列)
        Assert.Equal(props, RecordParams(dto, "AddUserInput").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList());

        // 更新入参:**部分更新口径** —— 除 Id 外每个字段类型都可空(约定 #9 "只更新明确允许的字段")
        var updateParams = RecordParams(dto, "UpdateUserInput").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Equal("long Id", updateParams[0]);
        // Update 的期望:每个字段类型可空(**若 props 里已是可空类型则不再叠加 '?**——批 AI 修复 D 后 props 可能已带 ?)
        Assert.Equal(
            new[] { "long Id" }.Concat(props.Select(p =>
            {
                var type = p.Split(' ')[0];
                var name = p.Split(' ')[1];
                return (type.EndsWith("?") ? type : type + "?") + " " + name;
            })).ToList(),
            updateParams);
        Assert.All(updateParams.Skip(1), p => Assert.EndsWith("?", p.Split(' ')[0]));
        Assert.Contains("部分更新:null=不改", dto);

        // ── 服务骨架:5 方法(序也固定) + [Service] 自注册 + 仓储注入
        Assert.Contains("using AdminCore.Core.Attributes;", service);
        Assert.Contains("[Service]", service);
        Assert.Contains("public class UserService", service);
        Assert.Contains("ISqlSugarRepository<User>", service);
        var methods = AsyncMethodPattern.Matches(service).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "PageAsync", "GetDetailAsync", "AddAsync", "UpdateAsync", "DeleteAsync" }, methods);
        Assert.Contains("PageResult<UserOutput>", service);
        Assert.Contains("部分更新:null=不改", service);
        Assert.Contains("PageUserInput input", service);
        Assert.Contains("AddUserInput input", service);
        Assert.Contains("UpdateUserInput input", service);

        _output.WriteLine($"判定:Dto 四 record 齐备;UserOutput({string.Join(", ", outputParams)});" +
                          $"Update{string.Join(", ", updateParams)};Service 方法 [{string.Join(", ", methods)}]");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 类型映射:真实类型逐类型差分 + 未知类型分支(反射)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 生成_类型映射逐类型()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var database = scope.Provider.GetRequiredService<SysDatabaseService>();

        // 私有 MapType 直调(任务书允许):① 差分核对全库类型清单 ② 覆盖未知类型分支(本库无未知类型列)
        var mapType = typeof(CodeGenService).GetMethod("MapType", BindingFlags.NonPublic | BindingFlags.Static);
        if (mapType is null)
        {
            Assert.Fail("降级:未能反射到 CodeGenService.MapType(签名已变?)——未知类型分支未实跑,禁止冒充通过");
        }

        // ① 全库真实类型清单(独立 SQL,**不经被测代码**)→ 与实现 MapType 逐类型差分
        var types = await scope.Db.Ado.SqlQueryAsync<string>(
            """
            SELECT DISTINCT format_type(a.atttypid, a.atttypmod)
              FROM pg_attribute a
              JOIN pg_class c ON c.oid = a.attrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = current_schema() AND c.relkind = 'r'
               AND a.attnum > 0 AND NOT a.attisdropped
             ORDER BY 1
            """);
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var (expected, unknown) = Classify(type);
            Assert.False(unknown,
                $"环境出现映射表未覆盖的列类型 {type}(生成器会回落 string 且仅记 warning)。" +
                "若该类型确为合法列类型,应先按 PG 语义扩展映射表(见本用例 Classify),再放开本断言——不许改断言迁就");
            var warnings = new List<string>();
            var actual = (string)mapType!.Invoke(null, new object?[] { type, false, null, "MapProbe", warnings })!;
            Assert.Equal(expected, actual);
            Assert.Empty(warnings);
        }

        // ② 抽样表:生成文本的属性行(类型 + 可空标记)与真实列结构逐行一致 —— 覆盖本库全部 7 种真实类型
        var samples = new[] { UserTable, "sys_log_http", "sys_notice", "sys_column_custom", "sys_file_content" };
        foreach (var table in samples)
        {
            var preview = await codeGen.PreviewAsync(new CodeGenInput(table));
            var columns = await database.ListColumnsAsync(table);
            var expected = columns.Where(c => !AuditColumns.Contains(c.ColumnName))
                .Select(c => PropertyLine(Classify(c.DataType).CsType, c.IsNullable, PascalCase(c.ColumnName)))
                .ToList();
            Assert.Equal(expected, ExtractPropertyLines(preview.EntityCode));
            // 抽样表列类型全在映射表内 → 零回落 ⇒ 无"未在映射表中"warning
            // (批 BF:sys_user 含密码族列 Password ⇒ 敏感列 warning,循环后单独断言;其余抽样表无密码族列 ⇒ 仍必须全空)
            if (table == UserTable)
            {
                Assert.DoesNotContain(preview.Warnings, w => w.Contains("未在映射表中"));
            }
            else
            {
                Assert.Empty(preview.Warnings);
            }
        }

        // 批 BF:密码族列进入行输出 ⇒ 敏感列 warning 点名;时间列 LastChangePasswordTime 不得被误判
        var userWarn = await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        Assert.Single(userWarn.Warnings, w => w.Contains("敏感列(密码族)") && w.Contains("Password"));
        Assert.DoesNotContain(userWarn.Warnings, w => w.Contains("LastChangePasswordTime"));

        // ③ 点名断言(任务书指定的关键映射,取值全部来自真实 schema,非编造)
        var user = await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        Assert.Contains("public string Account { get; set; }", user.EntityCode);   // character varying(64) NOT NULL → string
        Assert.Contains("public int Status { get; set; }", user.EntityCode);       // integer NOT NULL → int
        Assert.DoesNotContain(ExtractPropertyLines(user.EntityCode), l => l.Split(' ')[1] == "Id"); // bigint 主键 → 跳过
        var http = await codeGen.PreviewAsync(new CodeGenInput("sys_log_http"));
        Assert.Contains("public long? ElapsedMs { get; set; }", http.EntityCode);   // bigint 可空 → long?
        Assert.Contains("public bool HasException { get; set; }", http.EntityCode); // boolean NOT NULL → bool
        var notice = await codeGen.PreviewAsync(new CodeGenInput("sys_notice"));
        Assert.Contains("public DateTime? PublicTime { get; set; }", notice.EntityCode); // timestamp 可空 → DateTime?
        var columnCustom = await codeGen.PreviewAsync(new CodeGenInput("sys_column_custom"));
        Assert.Contains("public string? FixedData { get; set; }", columnCustom.EntityCode); // text 可空 → string?(修复 C:可空即带 '?')
        var fileContent = await codeGen.PreviewAsync(new CodeGenInput("sys_file_content"));
        Assert.Contains("public byte[] Content { get; set; }", fileContent.EntityCode);   // bytea NOT NULL → byte[]
        Assert.Contains("public long UserId { get; set; }", columnCustom.EntityCode);     // bigint NOT NULL → long

        // 修复 C 后的行为断言:可空 string 与其它可空类型同口径带 '?'(HttpMethod=varchar(16) NULL → string?),
        // 与同列特性的 IsNullable = true 一致(此前"string 永不加 ?"与特性声明矛盾)
        Assert.Contains("public string? HttpMethod { get; set; }", http.EntityCode);
        Assert.DoesNotContain("public string HttpMethod { get; set; }", http.EntityCode);
        Assert.Contains("[SugarColumn(ColumnDescription = \"HTTP方法\", Length = 16, IsNullable = true)]", http.EntityCode);

        // ④ 未知类型分支(本库无此类列 → 反射直调):PG 真实存在的类型名 "inet"/"int4range"/"interval" 均不在映射表
        foreach (var unknownType in new[] { "inet", "int4range", "interval" })
        {
            var warnings = new List<string>();
            var csType = (string)mapType!.Invoke(null, new object?[] { unknownType, false, null, "ProbeColumn", warnings })!;
            Assert.Equal("string", csType);
            Assert.Single(warnings);
            Assert.Contains("未在映射表中", warnings[0]);
            Assert.Contains("ProbeColumn", warnings[0]);
        }

        // ⑤ 带精度/标度声明的类型(批 AI 修复 B 后的**行为断言**):SysDatabaseService 的 DataType 取自
        //    format_type(atttypid, atttypmod),PG 对带精度声明产出 "numeric(18,4)"/"timestamp(6) with time zone";
        //    修复前 MapType 精确匹配不同名 ⇒ 静默回落 string(仅 warning);修复后按**族名**匹配 ⇒ 正确映射且零 warning。
        foreach (var (oidLiteral, typmod, pgExpected, csExpected) in new[]
                 {
                     ("numeric", ((18 << 16) | 4) + 4, "numeric(18,4)", "decimal"),
                     ("timestamptz", 6, "timestamp(6) with time zone", "DateTime")
                 })
        {
            var pgType = (await scope.Db.Ado.SqlQueryAsync<string>(
                $"SELECT format_type('{oidLiteral}'::regtype, @tm)", new SugarParameter("@tm", typmod)))[0];
            Assert.Equal(pgExpected, pgType);
            var warnings = new List<string>();
            var csType = (string)mapType!.Invoke(null, new object?[] { pgType, false, null, "Amount", warnings })!;
            Assert.Equal(csExpected, csType);
            Assert.Empty(warnings);
            _output.WriteLine($"判定:PG 对 {oidLiteral}(typmod={typmod}) 产出 \"{pgType}\" → MapType 正确映射为 {csType}(零 warning)");
        }

        // ⑥ 前缀误吞回归(修复 B 的边界):int/date 族改精确匹配后,"interval"/"daterange"/"int4range" 不得被吞
        foreach (var notMapped in new[] { "interval", "daterange", "int4range" })
        {
            var warnings = new List<string>();
            var csType = (string)mapType!.Invoke(null, new object?[] { notMapped, false, null, "ProbeRange", warnings })!;
            Assert.Equal("string", csType);
            Assert.Single(warnings);
        }

        _output.WriteLine($"判定:全库真实类型 {types.Count} 种 [{string.Join(", ", types)}] 逐类型差分一致且零回落;" +
                          $"抽样 {samples.Length} 表属性行逐行一致;未知类型分支 3/3 回落 string + warning");
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 表不存在 → 业务异常(不返回空文本误导)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 生成_表不存在_业务异常()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        const string ghost = "no_such_table_ai";

        // 前置:该表确实不存在(否则用例退化为"错误断言")
        var exists = await scope.Db.Ado.SqlQueryAsync<int>(
            """
            SELECT COUNT(*)::int FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = current_schema() AND c.relname = @t
            """,
            new SugarParameter("@t", ghost));
        Assert.Equal(0, exists[0]);

        var ex = await Assert.ThrowsAsync<AppException>(() => codeGen.PreviewAsync(new CodeGenInput(ghost)));
        Assert.Equal(ErrorCodes.DatabaseTableNotFound, ex.ErrorCode); // 批 AR-5 口径
        Assert.Equal(ghost, ex.Args!["table"]); // 参数经 Args 回带

        // 反向取证:存在的表**不抛**(证上面的异常来自"表不存在"判定,而非"一律抛")
        var real = await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        Assert.Equal("User", real.ClassName);

        // 空表名另走入参校验分支(消息含"表名",与"表不存在"区分)
        var blank = await Assert.ThrowsAsync<AppException>(() => codeGen.PreviewAsync(new CodeGenInput("   ")));
        Assert.Equal(ErrorCodes.CodeGenTableRequired, blank.ErrorCode); // CodeGen 自身表名校验先于库查询

        _output.WriteLine($"判定:文案=<<{ex.Message}>>;空表名文案=<<{blank.Message}>>;存在的表 {UserTable} 不抛");
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 入参生效(className / removePrefix / baseType)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 生成_参数生效()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();

        // className 覆盖 → 三段文本全部改用该名(实体/命名空间/服务类)
        var custom = await codeGen.PreviewAsync(new CodeGenInput(UserTable, ClassName: "MyUser", ModuleName: "IntelCore"));
        Assert.Equal("MyUser", custom.ClassName);
        Assert.Contains("public class MyUser : EntityTenant", custom.EntityCode); // 批 BF:缺省按表结构推断(含 TenantId → EntityTenant)
        Assert.Contains("[SugarTable(\"sys_user\")]", custom.EntityCode); // 表名不受类名参数影响(仍指向真实表)
        Assert.Contains("record PageMyUserInput", custom.DtoCode);
        Assert.Contains("record MyUserOutput", custom.DtoCode);
        Assert.Contains("public class MyUserService", custom.ServiceCode);
        Assert.Contains("namespace IntelCore.Services.MyUser", custom.ServiceCode);
        Assert.DoesNotContain("class UserService", custom.ServiceCode);

        // removePrefix 缺省 "sys_":去掉前缀 → User
        var defaultPrefix = await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        Assert.Equal("User", defaultPrefix.ClassName);

        // removePrefix = "" (显式空串,非 null → 不走 ??"sys_") ⇒ 表名整体切分:sys_user → sys + user → **SysUser**
        var noPrefix = await codeGen.PreviewAsync(new CodeGenInput(UserTable, RemovePrefix: ""));
        Assert.Equal("SysUser", noPrefix.ClassName);
        Assert.Contains("public class SysUser : EntityTenant", noPrefix.EntityCode); // 批 BF:缺省推断与 removePrefix 无关
        Assert.Contains("public class SysUserService", noPrefix.ServiceCode);

        // 去前缀的边界:prefix="sys" 不留前导下划线(ToPascal 按分隔符切分,_user → User)
        var barePrefix = await codeGen.PreviewAsync(new CodeGenInput(UserTable, RemovePrefix: "sys"));
        Assert.Equal("User", barePrefix.ClassName);

        // baseType → 实体基类替换(其余结构不变)
        var tenantBase = await codeGen.PreviewAsync(new CodeGenInput(UserTable, BaseType: "EntityTenant"));
        Assert.Contains("public class User : EntityTenant", tenantBase.EntityCode);
        Assert.DoesNotContain(": EntityBase", tenantBase.EntityCode);
        Assert.Equal(ExtractPropertyLines(defaultPrefix.EntityCode), ExtractPropertyLines(tenantBase.EntityCode));

        _output.WriteLine($"判定:ClassName=MyUser 生效(实体/PageMyUserInput/MyUserService/命名空间 IntelCore.Services.MyUser);" +
                          "RemovePrefix 缺省=User,空串=SysUser,sys=User;BaseType=EntityTenant 生效(属性行不变)");
    }

    // ─────────────────────────────────────────────────────────────
    // 7. 不写盘:PreviewAsync 前后文件集合(路径+最后写入时间)完全一致
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 生成_不写盘()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var repoRoot = LocateRepoRoot();

        // 前置(防永真):快照必须真的覆盖到被测面——若根目录写错/被全量排除,下面的"前后一致"就是空壳断言
        var before = SnapshotTree(repoRoot);
        Assert.Contains(before, e => e.StartsWith("src/Core/Services/CodeGen/CodeGenService.cs|", StringComparison.Ordinal));
        foreach (var dir in new[] { "src/Core/Entities", "src/Core/Services", "src/Web.Entry/Controllers" })
        {
            Assert.Contains(before, e => e.StartsWith(dir + "/", StringComparison.Ordinal));
        }

        // 三条生成路径 + 表清单:缺省参数 / 自定义类名+模块 / 空前缀+换基类(模板分支全过一遍)
        await codeGen.GetTableListAsync(null);
        await codeGen.GetTableListAsync(UserTable);
        await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        await codeGen.PreviewAsync(new CodeGenInput(UserTable, ClassName: "MyUser", ModuleName: "IntelCore"));
        await codeGen.PreviewAsync(new CodeGenInput(UserTable, RemovePrefix: "", BaseType: "EntityTenant"));

        var after = SnapshotTree(repoRoot);
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        Assert.True(added.Count == 0 && removed.Count == 0,
            $"调用 PreviewAsync 后文件集合发生变化(疑似写盘副作用):新增/改动 {added.Count} 项 [{string.Join(", ", added.Take(5))}]," +
            $"消失/被改前 {removed.Count} 项 [{string.Join(", ", removed.Take(5))}]");
        Assert.Equal(before, after);

        _output.WriteLine($"判定:仓根 {repoRoot} 下 {before.Count} 项(文件路径+最后写入时间+长度,含三处源码目录)调用前后完全一致 → 零文件系统副作用");
    }

    // ─────────────────────────────────────────────────────────────
    // 8. warnings:类型契约 + 普通表零回落 + 空壳表分支(本库无此形态 → 如实登记未覆盖)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task warning_空壳实体或未知类型()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();

        // 类型契约:List<string>(实现若改成 string[] / IReadOnlyList 即红——调用方按 List 消费)
        var preview = await codeGen.PreviewAsync(new CodeGenInput(UserTable));
        Assert.IsType<List<string>>(preview.Warnings);
        // 批 BF:sys_user.Password 属密码族 ⇒ 敏感列 warning(不再全空);时间列 LastChangePasswordTime 不得被误判;无类型回落 warning
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("未在映射表中"));
        Assert.Contains(preview.Warnings, w => w.Contains("敏感列(密码族)") && w.Contains("Password"));
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("LastChangePasswordTime"));

        // 未知类型 → warning(本库无未知类型列,此处反射直调私有 MapType 补覆盖):文案须含列名与"回落"语义
        var mapType = typeof(CodeGenService).GetMethod("MapType", BindingFlags.NonPublic | BindingFlags.Static);
        if (mapType is null)
        {
            Assert.Fail("降级:未能反射到 CodeGenService.MapType(签名已变?)——未知类型 warning 分支未实跑,禁止冒充通过");
        }
        var unknownWarnings = new List<string>();
        _ = (string)mapType!.Invoke(null, new object?[] { "inet", false, null, "IpAddress", unknownWarnings })!;
        Assert.Single(unknownWarnings);
        Assert.Contains("IpAddress", unknownWarnings[0]);
        Assert.Contains("inet", unknownWarnings[0]);
        Assert.Contains("未在映射表中", unknownWarnings[0]);
        Assert.Contains("回落 string", unknownWarnings[0]);

        // 空壳表前置取证:是否存在"除审计/主键列外零业务列"的真实表(审计列清单由用例侧独立声明)
        var auditList = string.Join(", ", AuditColumns.Select(a => "'" + a + "'"));
        var shellTables = await scope.Db.Ado.SqlQueryAsync<string>(
            $"""
             SELECT c.relname
               FROM pg_attribute a
               JOIN pg_class c ON c.oid = a.attrelid
               JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE n.nspname = current_schema() AND c.relkind = 'r'
                AND a.attnum > 0 AND NOT a.attisdropped
              GROUP BY c.relname
             HAVING COUNT(*) FILTER (WHERE LOWER(a.attname) NOT IN ({auditList})) = 0
              ORDER BY c.relname
             """);
        if (shellTables.Count > 0)
        {
            // 有真实空壳表 → 真跑该分支(不造假表,用库里的)
            var shell = await codeGen.PreviewAsync(new CodeGenInput(shellTables[0]));
            Assert.Contains(shell.Warnings, w => w.Contains("空壳"));
            Assert.Empty(ExtractPropertyLines(shell.EntityCode));
            _output.WriteLine($"判定:空壳表 {shellTables[0]} 真跑 → warning=<<{shell.Warnings.Single()}>>,实体零属性行");
        }
        else
        {
            // 本库无此形态 → 前置断言(0 命中是**取证结果**,不是跳过的借口)+ 明示未覆盖
            Assert.Empty(shellTables);
            _output.WriteLine("未覆盖:本库 40 张表均含业务列(扫描 0 命中)→ \"空壳实体\"warning 分支无真实输入可用(不造假表、不造数据);" +
                              "该分支的输入前提已在源码层核对(仅当 businessColumns.Count==0 触发)");
        }

        // 全表扫描:每张真实表都不出现"未在映射表中"(本库 7 种真实类型全部命中映射表——类型清单差分已在用例 4 覆盖)
        var tables = await codeGen.GetTableListAsync(null);
        Assert.True(tables.Count >= 40, $"全表扫描样本过小({tables.Count} 张),warning 断言不具代表性");
        var database = scope.Provider.GetRequiredService<SysDatabaseService>();
        var badSearchField = new List<string>();
        foreach (var table in tables)
        {
            var p = await codeGen.PreviewAsync(new CodeGenInput(table.TableName));
            Assert.DoesNotContain(p.Warnings, w => w.Contains("未在映射表中"));
            Assert.NotNull(p.EntityCode);
            Assert.False(string.IsNullOrWhiteSpace(p.ClassName), $"{table.TableName} 生成类名不应为空");

            // 缺陷**现状取证**(报告登记,不写成断言):搜索字段取"首个 string 业务列",无 string 业务列时回落 Id,
            // 模板会产出 x.Id!.Contains(keyword!) —— Id 是 long,落码后编译不过。此处只登记命中的表名:
            // WHY 不写成 Assert.Contains:那会把缺陷固化成"期望值",将来模板修好反而红。
            var columns = await database.ListColumnsAsync(table.TableName);
            var hasStringBusiness = columns.Any(c => !AuditColumns.Contains(c.ColumnName) && Classify(c.DataType).CsType == "string");
            if (!hasStringBusiness && p.ServiceCode.Contains("x.Id!.Contains(keyword!)", StringComparison.Ordinal))
            {
                badSearchField.Add(table.TableName);
            }
        }
        Assert.NotEmpty(tables);

        _output.WriteLine($"判定:warnings 类型=List<string>;sys_user 零 warning;未知类型 warning=<<{unknownWarnings[0]}>>;" +
                          $"全表 {tables.Count} 张均无\"未在映射表中\"");
        if (badSearchField.Count > 0)
        {
            _output.WriteLine($"缺陷取证(未修,报告登记):{badSearchField.Count} 张无 string 业务列的表生成 <<x.Id!.Contains(keyword!)>>," +
                              $"落码后编译不过 —— [{string.Join(", ", badSearchField)}]");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助:期望值构造(用例侧独立实现,与实现形成差分)
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 实体属性行期望文本(与实现模板同口径):<c>public {type}{?} {name} {{ get; set; }}</c>。
    /// 可空标记口径:实现只对**非 string** 的可空列追加 '?'(string 永不加)——此处按实测口径复刻,
    /// 该口径本身的疑点(与 [SugarColumn(IsNullable=true)] 自相矛盾)在报告中登记,不在用例里"顺手修正"。
    /// </summary>
    /// <summary>
    /// 期望属性行(与生成器口径同步):**可空列一律带 '?'**(含 string?;批 AI 修复 C——此前可空 string 生成非空 string
    /// 却写 IsNullable=true,属口径矛盾;本辅助函数按同一规则构造期望,避免"测试与实现对不上")。
    /// </summary>
    private static string PropertyLine(string csType, bool isNullable, string property)
        => $"public {(isNullable && !csType.EndsWith("?") ? csType + "?" : csType)} {property} {{ get; set; }}";

    /// <summary>提取生成文本里的属性行(顺序保持)。</summary>
    private static List<string> ExtractPropertyLines(string code)
        => PropertyLinePattern.Matches(code).Select(m => $"public {m.Groups[1].Value} {m.Groups[2].Value} {{ get; set; }}").ToList();

    /// <summary>提取 record 的参数字面(如 <c>PageUserInput(...)</c> 括号内文本)。</summary>
    private static string RecordParams(string code, string recordName)
    {
        var m = Regex.Match(code, $"record {recordName}\\(([^)]*)\\);", RegexOptions.Multiline);
        Assert.True(m.Success, $"Dto 文本未找到 record {recordName}(...); 实际文本:\n{code}");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// PG 类型 → C# 类型(用例侧独立实现的规格副本)。返回 Unknown=true 表示该类型不在映射表内(实现口径:回落 string + warning)。
    /// WHY 独立实现而非反射实现的 MapType 当期望:同源会造成"两边一起错"的永真断言;此处与实现互为差分。
    /// </summary>
    private static (string CsType, bool Unknown) Classify(string dataType)
    {
        var t = (dataType ?? string.Empty).ToLowerInvariant();
        if (t.StartsWith("character varying") || t.StartsWith("character") || t.Contains("text")
            || t.Contains("json") || t.Contains("xml"))
        {
            return ("string", false);
        }

        return t switch
        {
            "bigint" or "int8" => ("long", false),
            "integer" or "int" or "int4" or "smallint" or "int2" => ("int", false),
            "boolean" or "bool" => ("bool", false),
            "timestamp without time zone" or "timestamp with time zone" or "timestamp" or "timestamptz" or "date" => ("DateTime", false),
            "numeric" or "decimal" or "money" => ("decimal", false),
            "double precision" or "float8" => ("double", false),
            "real" or "float4" => ("float", false),
            "uuid" => ("Guid", false),
            "bytea" => ("byte[]", false),
            _ => ("string", true)
        };
    }

    /// <summary>下划线/短横线分隔名 → PascalCase(按生成器口径复刻:sys_user → SysUser)。</summary>
    private static string PascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var parts = name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(char.ToUpperInvariant(part[0])).Append(part.Length > 1 ? part[1..] : string.Empty);
        }

        return sb.Length > 0 ? sb.ToString() : name;
    }

    /// <summary>定位仓根:自测试输出目录向上找含 <c>src/Core</c> 的目录(找不到即 Assert.Fail——不静默跳过)。</summary>
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

    /// <summary>
    /// 文件集合快照:每项 = <c>相对路径|最后写入时间(Ticks)|长度</c>,目录记为 <c>相对路径|DIR</c>。
    /// WHY 带 mtime:只比"路径集合"漏得掉"同一文件被重写"(生成器若覆盖写既有文件,路径集合不变、mtime 必变)。
    /// WHY 排除 .git/bin/obj/node_modules:它们是 VCS 元数据 / **构建产物** / **第三方依赖**,构建与测试宿主会写,
    /// 纳入即让断言测到"构建器行为"而非"生成器行为"(被测面的源码目录在快照中**无任何排除**)。
    /// </summary>
    private static List<string> SnapshotTree(string root)
    {
        var skipDirs = new HashSet<string>(StringComparer.Ordinal) { ".git", "bin", "obj", "node_modules" };
        var entries = new List<string>();

        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (skipDirs.Contains(name))
                {
                    continue;
                }

                entries.Add(Path.GetRelativePath(root, sub) + "|DIR");
                Walk(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(file);
                entries.Add($"{Path.GetRelativePath(root, file)}|{info.LastWriteTimeUtc.Ticks}|{info.Length}");
            }
        }

        Walk(root);
        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    /// <summary>
    /// 测试用 DB 容器:与生产同一条 AddSqlSugar 注册路径 + AddAutoServices(Core 程序集,解析 [Service] 的
    /// CodeGenService/SysDatabaseService)。
    /// </summary>
    /// <remarks>
    /// 与 BatchADDatabaseTest.DbScope 的两处**有意差异**:
    /// ① **不调用** InitTables/HealColumnCase——本批"零污染"裁决(只读,不建表不改 schema),代价是"库里没表即红";
    /// ② PG 探测走**异步** ConnectAsync(CancellationToken 超时),不用 <c>Task.Wait</c>(仓规禁同步阻塞等待)。
    /// </remarks>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; private set; } = null!;

        public IServiceProvider Provider { get; private set; } = null!;

        public bool PgReachable { get; private set; }

        public static async Task<DbScope> CreateAsync()
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
                return scope; // PgReachable=false → 用例侧 Assert.Fail 降级(不静默跳过)
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddAutoServices(typeof(CodeGenService).Assembly); // [Service] 自动发现(与 Program.cs 同口径)
            scope._provider = services.BuildServiceProvider();
            scope.Db = scope._provider.GetRequiredService<ISqlSugarClient>();
            scope.Provider = scope._provider;
            scope.PgReachable = true;
            return scope;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 BatchABTenantDbTest 口径,改异步等待)。</summary>
        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
                await client.ConnectAsync(DbHost, DbPort, cts.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}