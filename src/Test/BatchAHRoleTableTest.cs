using System.Reflection;
using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Notice.Dto;
using AdminCore.Core.Services.RoleTable;
using AdminCore.Core.Services.RoleTable.Dto;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Web.Core.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的;同 BatchLOrgScopeTest 口径)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 AH 2.9「表级字段权限」验证:A 组 = 存储与服务(SysRoleTableService,真实 PG),
    /// B 组 = 响应出口强制点(<see cref="ColumnScopeFilter"/>.OnResultExecutionAsync,真实 PG + Claims 身份模拟)。
    /// </summary>
    /// <remarks>
    /// WHY B 组也必须走真实 PG:强制点的判定输入是"受管表集合"(sys_role_table 的 TableName 去重)与
    /// "该用户角色白名单并集"(sys_user_role × sys_role_table)——这两者只能由真实库给出。内存替身只能证明
    /// "我喂了什么就裁什么",证明不了"库里的授权行真的决定了裁剪"。PG 不可达一律 Assert.Fail 显式降级,禁止冒充通过。
    /// WHY 伪造 ResultExecutingContext 而非起 TestServer:强制点是纯 IAsyncResultFilter(无路由/无认证依赖),
    /// 直接把"响应出口"这一步单独拎出来测,失败归因最小;身份仍走 Claims(经 UserManager 实时读 accessor),
    /// 与批 B/批 L 同一身份链路,不另造捷径。
    /// 零污染:探针角色/授权行/用户/用户角色 finally 物理清理 ＋ 清缓存键;探针固定 Id 走 99_000_000_003_xx 段
    /// (批 H 占 002_ 段),绝不触碰默认租户既有数据。
    /// 生产发现(未改任何生产代码,证据固化在本文件):用例 5 固化两条缺陷(受管表键不失效、角色白名单键是死缓存)。
    /// 口径变更留痕:用例 4 原按"授权树排除 [SysTable]"断言,父 agent 实机 e2e 后把 GetAllTableColumnAsync 改为
    /// **只排除 [IgnoreTable]**(系统表的敏感列正是字段权限的首要治理对象),本文件已按新口径改写为"含系统表、含 sys_user"。
    /// </remarks>
    public class BatchAHRoleTableTest
    {
        // 开发库连接串(与批 AD/批 H 同源,仅开发用;Timeout=10 让不可达场景快速失败)
        private const string DbConnectionString =
            TestPg.ConnectionString;

        // 诚实降级文案(与 BatchADDatabaseTest 同源)
        private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

        // 超管判定口径:AccountType == 999(见 UserManager.IsSuperAdmin)
        private const int SuperAdminAccountType = 999;
        private const int NormalAccountType = 777;

        // 探针固定 Id(99_000_000_003_xx 段 = 批 AH;幂等重建 + finally 物理清理)
        private const long ProbeRole = 99_000_000_003_01;
        private const long ProbeRolePrint = 99_000_000_003_02;
        private const long ProbeUser = 99_000_000_003_11;
        private const long ProbeUserNoRole = 99_000_000_003_12;

        // 必然不存在的角色 Id(雪花域内不冲突)
        private const long NonExistRole = 999_999_999_999_999;

        private static readonly long[] ProbeRoleIds = [ProbeRole, ProbeRolePrint];

        // 探针用表名。[SysTable] 与否只影响**授权树**,不影响强制点——强制点的"受管"判定来自 sys_role_table 的授权行,
        // 故 sys_user(带 [SysTable])照样能被列为受管表并裁剪(这正是 B 组的主用例)。
        private const string TableUser = "sys_user";
        private const string TableNotice = "sys_notice";
        private const string TablePrint = "sys_print";

        /// <summary>受管表集合缓存键(与 SysRoleTableService.GetManagedTablesAsync 内部拼接一致)。</summary>
        private static readonly string ManagedTablesKey = CacheConst.KeyRoleTable + "__managed";

        /// <summary>
        /// [SysTable] 真实名单(grep 实证:Core/Entities 下 32 个带 [SugarTable] 的实体中 17 个贴了 [SysTable])。
        /// 口径(批 AH 父 agent 实机 e2e 后定稿):授权树**不再排除** [SysTable]——系统表的敏感列(账号/姓名等)
        /// 正是字段权限的首要治理对象,排除会让 sys_user/sys_role 等**无法配置字段权限**;故本名单的表必须**全部在树中**。
        /// 实体→表:SysDictData/SysDictType/SysFile/SysFileContent/SysJobDetail/SysMenu/SysOpenAccess/SysOrg/SysPos/
        /// SysRole/SysRoleApi/SysRoleMenu/SysRoleOrg/SysTenant/SysUser/SysUserPasswordRecord/SysUserRole。
        /// 注意:SysConfig(sys_config)**未**贴 [SysTable](只在 [SugarTable] 上)——它一直就在树中(任务书把它当系统表是事实错误)。
        /// </summary>
        private static readonly string[] SysTableNames =
        [
            "sys_dict_data", "sys_dict_type", "sys_file", "sys_file_content", "sys_job_detail",
            "sys_menu", "sys_open_access", "sys_org", "sys_pos", "sys_role",
            "sys_role_api", "sys_role_menu", "sys_role_org", "sys_tenant", "sys_user",
            "sys_user_password_record", "sys_user_role"
        ];

        /// <summary>非 [SysTable] 实体表(15 张):业务/日志/配置表,同样必须在树中。</summary>
        private static readonly string[] NonSystemTableNames =
        [
            "sys_column_custom", "sys_config", "sys_log_diff", "sys_log_event", "sys_log_ex",
            "sys_log_http", "sys_log_login", "sys_log_msg", "sys_log_op", "sys_notice",
            "sys_notice_user", "sys_print", "sys_role_table", "sys_serial", "sys_user_otp"
        ];

        // ─────────────────────────────────────────────────────────────────────────
        // A. 存储与服务(真实 PG)
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>用例 1:授权是全量替换语义(提交什么就是什么),回读与提交对偶且排序稳定(非提交序)。</summary>
        [Fact]
        public async Task 授权_全量替换与回读对偶()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRole, "ahr_probe_role", "ahr_探针角色");
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();

                // 逆序提交:若回读的是"插入序/提交序"而非 (TableName, ColumnName) 序,下面的断言必然红
                await svc.GrantAsync(new RoleTableInput(ProbeRole, ["sys_user:RealName", "sys_user:Account"]));
                var read1 = await svc.GetRoleTableAsync(ProbeRole);
                Assert.Equal(ProbeRole, read1.RoleId);
                Assert.Equal(new List<string> { "sys_user:Account", "sys_user:RealName" }, read1.Items);

                // 全量替换:提交单项 → 旧行必须消失(不是追加)
                await svc.GrantAsync(new RoleTableInput(ProbeRole, ["sys_user:Account"]));
                var read2 = await svc.GetRoleTableAsync(ProbeRole);
                Assert.Equal(new List<string> { "sys_user:Account" }, read2.Items);

                // 空数组 = 清空(与 grantMenu/grantOrg 同语义)
                await svc.GrantAsync(new RoleTableInput(ProbeRole, new List<string>()));
                var read3 = await svc.GetRoleTableAsync(ProbeRole);
                Assert.Empty(read3.Items);
                // 物理删除而非软删:关联表不留不可见脏行(否则唯一索引会被历史行挡住)
                Assert.Equal(0, await CountRowsAsync(unit.ServiceProvider, ProbeRole));
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>用例 2:格式非法 fail-loud 且不落半截写;审计列静默忽略;重复项去重后仅 1 物理行。</summary>
        [Fact]
        public async Task 授权_格式非法与审计列()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRole, "ahr_probe_role", "ahr_探针角色");
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();

                // 格式非法("表名:列名"缺冒号)→ AppException,消息含"格式非法"
                var ex = await Assert.ThrowsAsync<AppException>(
                    () => svc.GrantAsync(new RoleTableInput(ProbeRole, ["没有冒号"])));
                Assert.Equal(ErrorCodes.FieldScopeGrantItemInvalid, ex.ErrorCode);
                // 清洗先于落库:抛错不得留下半截写入(否则"全量替换"会变成"部分替换")
                Assert.Equal(0, await CountRowsAsync(unit.ServiceProvider, ProbeRole));

                // 审计列(Id)静默忽略:不抛异常,也不产生授权行
                await svc.GrantAsync(new RoleTableInput(ProbeRole, ["sys_user:Account", "sys_user:Id"]));
                var read = await svc.GetRoleTableAsync(ProbeRole);
                Assert.Equal(new List<string> { "sys_user:Account" }, read.Items);

                // 重复项去重:内存 GroupBy 去重后逐条插入 → 唯一索引 (RoleId,TableName,ColumnName) 下恰 1 行
                await svc.GrantAsync(new RoleTableInput(ProbeRole, ["sys_user:Account", "sys_user:Account"]));
                Assert.Equal(1, await CountRowsAsync(unit.ServiceProvider, ProbeRole));
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>用例 3:角色不存在即拒绝(fail-closed:授权不能挂到不存在的角色上)。</summary>
        [Fact]
        public async Task 授权_角色不存在拒绝()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();

            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.GrantAsync(new RoleTableInput(NonExistRole, ["sys_user:Account"])));
            Assert.Equal(ErrorCodes.RoleNotFound, ex.ErrorCode);
            Assert.Empty((await svc.GetRoleTableAsync(NonExistRole)).Items);
        }

        /// <summary>
        /// 用例 4(口径变更后):授权树**含**系统表(含 sys_user 的 Account 列)、**排除**审计列。
        /// WHY 方法名由"排除系统表"改为"含系统表":父 agent 实机 e2e 后把 GetAllTableColumnAsync 的排除项从
        /// [SysTable] 改为 [IgnoreTable](系统表敏感列才是字段权限的首要治理对象),旧名会与新口径相反、误导后来人。
        /// </summary>
        [Fact]
        public async Task 授权树_含系统表并排除审计字段()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            using var unit = NewUnit(scope);
            var tree = await unit.ServiceProvider.GetRequiredService<SysRoleTableService>().GetAllTableColumnAsync();

            Assert.NotEmpty(tree);

            // ① [SysTable] 真实名单(17 张)必须**全部在场**:系统表可配置字段权限正是本次口径变更的核心(旧口径下 sys_user 缺席)
            foreach (var sysTable in SysTableNames)
            {
                Assert.Contains(tree, n => n.TableName == sysTable);
            }

            // ② 期望含 sys_user,且其列含 Account/RealName(字段权限的首要治理对象;授权树与裁剪侧必须同时认这些列)
            var user = tree.Single(n => n.TableName == TableUser);
            Assert.Contains(user.Columns, c => c.ColumnName == "Account");
            Assert.Contains(user.Columns, c => c.ColumnName == "RealName");

            // ③ 非 [SysTable] 实体表(15 张)同样必须在场(业务/日志/配置表)
            foreach (var nonSystem in NonSystemTableNames)
            {
                Assert.Contains(tree, n => n.TableName == nonSystem);
            }

            // ④ 每张表的列不含基类审计字段(Id/CreateTime/IsDelete/TenantId 等:不由字段权限治理)
            string[] forbidden = ["Id", "CreateTime", "IsDelete", "TenantId"];
            foreach (var node in tree)
            {
                Assert.NotEmpty(node.Columns);
                foreach (var column in node.Columns)
                {
                    Assert.False(forbidden.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase),
                        $"{node.TableName}.{column.ColumnName} 是基类审计列,不应出现在授权树");
                    Assert.False(SysRoleTableService.IsAuditColumn(column.ColumnName),
                        $"{node.TableName}.{column.ColumnName} 被 IsAuditColumn 判为审计列,授权树与裁剪侧口径漂移");
                }
            }

            // ⑤ 授权树与裁剪侧共用列名解析:非系统表 sys_notice 的列名应与实体一致(抽样)
            var notice = tree.Single(n => n.TableName == TableNotice);
            Assert.Contains(notice.Columns, c => c.ColumnName == "Title");

            // ⑥ [IgnoreTable] 分支的本仓现状(**不编造**):grep 实证本仓没有任何实体贴 [IgnoreTable](属性类存在、零使用),
            //    故生产代码里"排除 [IgnoreTable]"这条虽在,当前**不可观测**(没有任何这类实体可被排除)。
            //    这里用反射把该前提固化为断言:将来一旦有人新增 [IgnoreTable] 实体,本断言立刻红,
            //    提醒把"该表不在授权树中"补成显式用例(而不是悄悄漏测)。
            var ignoreTableEntities = typeof(SysUser).Assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<IgnoreTableAttribute>() is not null)
                .Select(t => t.Name)
                .ToList();
            Assert.Empty(ignoreTableEntities);
        }

        /// <summary>
        /// 用例 5:受管表集合随授权变化——但受 7 天缓存影响(缺陷 1 的可执行证据)。
        /// </summary>
        [Fact]
        public async Task 受管表集合_随授权变化()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRolePrint, "ahr_probe_role_print", "ahr_探针角色打印");
            try
            {
                using var unit = NewUnit(scope);
                var sp = unit.ServiceProvider;
                var svc = sp.GetRequiredService<SysRoleTableService>();
                var cache = sp.GetRequiredService<ICacheService>();

                // 前置:该表当前不受管(库中无任何授权行)
                Assert.Equal(0, await scope.Db.Queryable<SysRoleTable>().Where(r => r.TableName == TablePrint).CountAsync());

                // 预热 managed 键(空集也是缓存值,GetAsync 返回非 null → 后续读全走缓存)
                var before = await svc.GetManagedTablesAsync();
                Assert.DoesNotContain(TablePrint, before);
                Assert.True(await cache.ExistKeyAsync(ManagedTablesKey), "预热后 managed 键应已写入缓存(缺陷复现的前提)");

                // 给探针角色授权该表一列
                await svc.GrantAsync(new RoleTableInput(ProbeRolePrint, [$"{TablePrint}:Template"]));
                Assert.Equal(1, await CountRowsAsync(sp, ProbeRolePrint));

                // 缺陷①②修复后的行为断言(批 AH 自纠):GrantAsync 变更即失效 **managed 键 + 角色键** ⇒
                // 新受管表**立即**被强制点识别(此前会漏裁最长 7 天);角色白名单也**真走缓存**(此前是死缓存,
                // 每响应 2 次 DB 查询)。
                var after = await svc.GetManagedTablesAsync();
                Assert.Contains(TablePrint, after);

                // 角色白名单缓存应已生效:授权后首次读取回填该键(GrantAsync 失效 → Get 回源 → Set)
                var allowed = await svc.GetAllowedColumnsAsync(0, new List<long> { ProbeRolePrint });
                Assert.True(allowed.ContainsKey(TablePrint));
                Assert.True(await cache.ExistKeyAsync($"{CacheConst.KeyRoleTable}{ProbeRolePrint}"),
                    "角色白名单缓存应在首次读取后回填(缺陷②修复:不再是死缓存)");

                // 清授权 → managed 键再次失效,受管集合不再含该表(回归:变更即失效双向生效)
                await svc.GrantAsync(new RoleTableInput(ProbeRolePrint, new List<string>()));
                await cache.RemoveAsync($"{CacheConst.KeyRoleTable}{ProbeRolePrint}");
                var cleared = await svc.GetManagedTablesAsync();
                Assert.DoesNotContain(TablePrint, cleared);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // B. 强制点(响应出口裁剪;真实 PG + Claims 身份模拟)
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>用例 6:白名单制——授权列保留原值,未授权列置空,审计/主键列豁免。</summary>
        [Fact]
        public async Task 强制点_授权列保留未授权列置空()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRole, "ahr_probe_role", "ahr_探针角色");
            await EnsureProbeUserAsync(scope, ProbeUser, "ahr_probe_user");
            await BindUserRoleAsync(scope, ProbeUser, ProbeRole);
            await GrantProbeAsync(scope, ProbeRole, $"{TableUser}:Account");
            try
            {
                // 前置(否则后面的裁剪断言归因不成立):DTO→实体映射命中 SysUser,且该表确实已受管
                Assert.Equal(typeof(SysUser), SysRoleTableService.ResolveEntity(typeof(UserOutput)));

                using var unit = NewUnit(scope);
                var managed = await unit.ServiceProvider.GetRequiredService<SysRoleTableService>().GetManagedTablesAsync();
                Assert.Contains(TableUser, managed);

                var row = new UserOutput(ProbeUser, "ahr_probe_account", "ahr_探针真名", 1, NormalAccountType, 42, 43);
                var page = new PageResult<UserOutput> { Total = 1, List = [row] };

                await RunColumnScopeFilterAsync(scope, unit.ServiceProvider, page, ProbeUser, NormalAccountType);

                Assert.Equal("ahr_probe_account", row.Account); // 授权列:原值保留
                Assert.Null(row.RealName);                       // 未授权列:置空
                Assert.Equal(0, row.Status);                     // 未授权列:值类型置 default
                Assert.Equal(0, row.AccountType);
                Assert.Null(row.OrgId);
                Assert.Null(row.PosId);
                Assert.Equal(ProbeUser, row.Id);                 // 主键/审计列:豁免(否则列表主键消失、前端整页不可用)
                Assert.Equal(1, page.Total);                     // PageResult 包装层不受影响(非实体类型:只递归)
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>用例 7:超管(AccountType=999)直通——同一被测值不裁剪。</summary>
        [Fact]
        public async Task 强制点_超管直通()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRole, "ahr_probe_role", "ahr_探针角色");
            await EnsureProbeUserAsync(scope, ProbeUser, "ahr_probe_user");
            await BindUserRoleAsync(scope, ProbeUser, ProbeRole);
            await GrantProbeAsync(scope, ProbeRole, $"{TableUser}:Account");
            try
            {
                using var unit = NewUnit(scope);
                // 前置:该表已受管且探针角色只授了 Account —— 若超管被误裁,RealName 必空,断言可捕获
                var managed = await unit.ServiceProvider.GetRequiredService<SysRoleTableService>().GetManagedTablesAsync();
                Assert.Contains(TableUser, managed);

                var row = new UserOutput(ProbeUser, "ahr_probe_account", "ahr_探针真名", 1, NormalAccountType, 42, 43);
                var page = new PageResult<UserOutput> { Total = 1, List = [row] };

                await RunColumnScopeFilterAsync(scope, unit.ServiceProvider, page, ProbeUser, SuperAdminAccountType);

                Assert.Equal("ahr_probe_account", row.Account);
                Assert.Equal("ahr_探针真名", row.RealName); // 超管直通:未授权列亦保留
                Assert.Equal(1, row.Status);
                Assert.Equal(NormalAccountType, row.AccountType);
                Assert.Equal(42, row.OrgId);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>用例 8:未受管表零影响——机制是"活"的(另有受管表)也不碰未受管表。</summary>
        [Fact]
        public async Task 强制点_未受管表不受影响()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRole, "ahr_probe_role", "ahr_探针角色");
            await EnsureProbeUserAsync(scope, ProbeUser, "ahr_probe_user");
            await BindUserRoleAsync(scope, ProbeUser, ProbeRole);
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();

                // 前置①:sys_notice 无任何授权行,且不在受管集合
                Assert.Equal(0, await scope.Db.Queryable<SysRoleTable>().Where(r => r.TableName == TableNotice).CountAsync());
                Assert.DoesNotContain(TableNotice, await svc.GetManagedTablesAsync());

                // 前置②:让机制处于活跃状态(sys_user 受管)——否则"未裁"可能只是因为机制整体没生效(假绿)
                await GrantProbeAsync(scope, ProbeRole, $"{TableUser}:Account");
                Assert.Contains(TableUser, await svc.GetManagedTablesAsync());

                var now = new DateTime(2026, 9, 16, 10, 30, 0, DateTimeKind.Utc);
                var notice = new NoticeOutput(7, "ahr_标题", "ahr_内容", 1, 2, "ahr_发布人", 3, "ahr_发布机构", now, null, 0, now);

                await RunColumnScopeFilterAsync(scope, unit.ServiceProvider, notice, ProbeUser, NormalAccountType);

                Assert.Equal("ahr_标题", notice.Title);          // 未受管表:全字段保留
                Assert.Equal("ahr_内容", notice.Content);
                Assert.Equal(1, notice.Type);
                Assert.Equal(2, notice.PublicUserId);
                Assert.Equal("ahr_发布人", notice.PublicUserName);
                Assert.Equal(3, notice.PublicOrgId);
                Assert.Equal("ahr_发布机构", notice.PublicOrgName);
                Assert.Equal(now, notice.PublicTime);
                Assert.Equal(0, notice.Status);
                Assert.Equal(now, notice.CreateTime);
                Assert.Equal(7, notice.Id);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>用例 9:fail-closed——表已受管而用户角色白名单并集为空 ⇒ 非审计列全置空。</summary>
        [Fact]
        public async Task 强制点_fail_closed()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRole, "ahr_probe_role", "ahr_探针角色");
            await EnsureProbeUserAsync(scope, ProbeUserNoRole, "ahr_probe_user_norole");
            await GrantProbeAsync(scope, ProbeRole, $"{TableUser}:Account");
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();

                // 前置①:该表已受管 前置②:探针用户确实无任何角色(用服务自己的读路径复核,不靠"我以为")
                Assert.Contains(TableUser, await svc.GetManagedTablesAsync());
                Assert.Empty(await svc.GetUserRoleIdsAsync(ProbeUserNoRole));

                var row = new UserOutput(ProbeUserNoRole, "ahr_secret_account", "ahr_secret_realname", 1, NormalAccountType, 42, 43);
                var page = new PageResult<UserOutput> { Total = 1, List = [row] };

                await RunColumnScopeFilterAsync(scope, unit.ServiceProvider, page, ProbeUserNoRole, NormalAccountType);

                Assert.Null(row.Account);   // 未授权即不可见(fail-closed)
                Assert.Null(row.RealName);
                Assert.Equal(0, row.Status);
                Assert.Equal(0, row.AccountType);
                Assert.Null(row.OrgId);
                Assert.Null(row.PosId);
                Assert.Equal(ProbeUserNoRole, row.Id); // 审计列仍豁免(fail-closed 也不吞主键)
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>用例 10:保守解析——DTO 名映射不到实体 → 一律不裁(宁可不裁也不误裁)。</summary>
        [Fact]
        public async Task 强制点_映射不到的类型不裁()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable) { Assert.Fail(Downgrade); }

            await EnsureProbeRoleAsync(scope, ProbeRole, "ahr_probe_role", "ahr_探针角色");
            await EnsureProbeUserAsync(scope, ProbeUser, "ahr_probe_user");
            await BindUserRoleAsync(scope, ProbeUser, ProbeRole);
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();

                // 前置:机制活跃(受管集合非空)且该 DTO 确实映射不到实体(直接用生产 API 验证前提)
                await GrantProbeAsync(scope, ProbeRole, $"{TableUser}:Account");
                Assert.Contains(TableUser, await svc.GetManagedTablesAsync());
                Assert.Null(SysRoleTableService.ResolveEntity(typeof(AhrUnknownDto)));

                // 属性名故意与 SysUser 同名(Account/RealName):若把未知类型误当 sys_user 处理,断言立刻抓到
                var dto = new AhrUnknownDto { Id = 1, Account = "ahr_unknown_account", RealName = "ahr_unknown_realname" };

                await RunColumnScopeFilterAsync(scope, unit.ServiceProvider, dto, ProbeUser, NormalAccountType);

                Assert.Equal("ahr_unknown_account", dto.Account);
                Assert.Equal("ahr_unknown_realname", dto.RealName);
                Assert.Equal(1, dto.Id);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 工具:强制点驱动 / 探针 / 清理 / 容器
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 以指定身份跑一次响应出口强制点:伪造 ResultExecutingContext(被测值 = <c>ObjectResult.Value</c>),
        /// 过滤器按真实构造签名 new ColumnScopeFilter(UserManager, SysRoleTableService, ILogger) 直构(不 mock)。
        /// WHY 身份走 Claims:UserManager 是"实时读 IHttpContextAccessor"的门面,身份只能经 Claims 注入才有意义
        /// (与批 B/批 L 同口径);裁剪结果就写回被测对象本身,故调用方直接读原对象断言。
        /// </summary>
        private static async Task RunColumnScopeFilterAsync(DbScope scope, IServiceProvider sp, object value,
            long userId, int accountType)
        {
            var httpContext = BuildUserContext(userId, accountType);
            scope.FakeAccessor.HttpContext = httpContext;

            var filter = new ColumnScopeFilter(
                new UserManager(scope.FakeAccessor),
                sp.GetRequiredService<SysRoleTableService>(),
                sp.GetRequiredService<ILogger<ColumnScopeFilter>>());

            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
            var filterMetadata = new List<IFilterMetadata>();
            var executing = new ResultExecutingContext(actionContext, filterMetadata, new ObjectResult(value), controller: null!);

            await filter.OnResultExecutionAsync(executing, () => Task.FromResult(
                new ResultExecutedContext(actionContext, filterMetadata, executing.Result, controller: null!)));
        }

        /// <summary>模拟登录身份(Claim 键与生产 JwtClaimNames 同源,非超管/超管仅 AccountType 不同)。</summary>
        private static HttpContext BuildUserContext(long userId, int accountType)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, "ahr_probe_user"),
                new Claim(JwtClaimNames.RealName, "ahr_探针用户"),
                new Claim(JwtClaimNames.AccountType, accountType.ToString()),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return context;
        }

        /// <summary>
        /// 前置:给探针角色授权(全量替换),并**手工**清受管表键。
        /// WHY 手工清:GrantAsync 只删 "sys_role_table:{roleId}",不删 "sys_role_table:__managed"(缺陷 1,证据见用例 5),
        /// 本容器内若 managed 已被预热,前置"该表已受管"就不自洽 → 必须清键后重算,否则用例会假绿/假红。
        /// </summary>
        private static async Task GrantProbeAsync(DbScope scope, long roleId, params string[] items)
        {
            using var unit = NewUnit(scope);
            await unit.ServiceProvider.GetRequiredService<SysRoleTableService>()
                .GrantAsync(new RoleTableInput(roleId, items.ToList()));
            await unit.ServiceProvider.GetRequiredService<ICacheService>().RemoveAsync(ManagedTablesKey);
        }

        /// <summary>授权行计数(走仓储契约同一入口,不绕过 ISqlSugarRepository)。</summary>
        private static async Task<int> CountRowsAsync(IServiceProvider sp, long roleId)
            => await sp.GetRequiredService<ISqlSugarRepository<SysRoleTable>>()
                .AsQueryable()
                .Where(r => r.RoleId == roleId)
                .CountAsync();

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        /// <summary>探针角色幂等重建(固定 Id;Name/Code 走 ahr_ 前缀防撞名)。</summary>
        private static async Task EnsureProbeRoleAsync(DbScope scope, long roleId, string code, string name)
        {
            if (await scope.Db.Queryable<SysRole>().AnyAsync(r => r.Id == roleId))
            {
                return;
            }

            // Remark/Code 显式给值:SqlSugar 对 string 引用类型 CodeFirst 默认 NOT NULL(批 V 部署验证抓出的坑)
            await scope.Db.Insertable(new SysRole
            {
                Id = roleId,
                Name = name,
                Code = code,
                Remark = "批 AH 探针",
                DataScope = 4,
                SysFlag = false,
                TenantId = SqlSugarConst.DefaultTenantId
            }).ExecuteCommandAsync();
        }

        /// <summary>探针用户幂等重建(固定 Id;Password 列 NOT NULL → 填哑值,永不登录)。</summary>
        private static async Task EnsureProbeUserAsync(DbScope scope, long userId, string account)
        {
            if (await scope.Db.Queryable<SysUser>().AnyAsync(u => u.Id == userId))
            {
                return;
            }

            await scope.Db.Insertable(new SysUser
            {
                Id = userId,
                Account = account,
                RealName = "ahr_探针用户",
                Password = "ahr_probe_never_login",
                AccountType = NormalAccountType,
                Status = 0,
                TokenVersion = 1,
                TenantId = SqlSugarConst.DefaultTenantId
            }).ExecuteCommandAsync();
        }

        private static async Task BindUserRoleAsync(DbScope scope, long userId, long roleId)
        {
            if (await scope.Db.Queryable<SysUserRole>().AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId))
            {
                return;
            }

            await scope.Db.Insertable(new SysUserRole
            {
                UserId = userId,
                RoleId = roleId,
                TenantId = SqlSugarConst.DefaultTenantId
            }).ExecuteCommandAsync();
        }

        /// <summary>
        /// 零污染清理(物理删,幂等):授权行 → 用户角色 → 用户 → 角色,再清缓存键
        /// (同进程后续用例/后续测试类不再读到探针快照)。
        /// </summary>
        private static async Task CleanupAsync(DbScope scope)
        {
            if (!scope.PgReachable)
            {
                return;
            }

            // WHY 条件内联常量数组而非引用 private static 字段:SqlSugar 解析表达式树时拒读 private 字段
            // (实测 SqlSugarException "Field \"ProbeRoleIds\" can't be private"),故探针 Id 直接内联(与本仓 BatchL 同口径)。
            await scope.Db.Deleteable<SysRoleTable>()
                .Where(r => new[] { ProbeRole, ProbeRolePrint }.Contains(r.RoleId)).ExecuteCommandAsync();
            await scope.Db.Deleteable<SysUserRole>()
                .Where(ur => new[] { ProbeUser, ProbeUserNoRole }.Contains(ur.UserId)).ExecuteCommandAsync();
            await scope.Db.Deleteable<SysUser>()
                .Where(u => new[] { ProbeUser, ProbeUserNoRole }.Contains(u.Id)).ExecuteCommandAsync();
            await scope.Db.Deleteable<SysRole>()
                .Where(r => new[] { ProbeRole, ProbeRolePrint }.Contains(r.Id)).ExecuteCommandAsync();

            // 批 AK:清探针表名的表级开关行(批 AK 起 GrantAsync 授权即入册;不清则残留行把"受管"前置断言打翻)
            await scope.Db.Deleteable<SysTableFieldScope>()
                .Where(s => new[] { TableUser, TableNotice, TablePrint }.Contains(s.TableName)).ExecuteCommandAsync();

            var cache = scope.Provider.GetRequiredService<ICacheService>();
            foreach (var roleId in ProbeRoleIds)
            {
                await cache.RemoveAsync($"{CacheConst.KeyRoleTable}{roleId}");
            }

            await cache.RemoveAsync(ManagedTablesKey);
        }

        /// <summary>
        /// 用例 10 专用:名字映射不到任何实体的自定义 DTO(ResolveEntity 保守解析 → null → 不裁)。
        /// 属性名故意与 SysUser 的 Account/RealName 同名,用于抓捕"未知类型被误当 sys_user 处理"。
        /// </summary>
        private sealed class AhrUnknownDto
        {
            public long Id { get; set; }

            public string? Account { get; set; }

            public string? RealName { get; set; }
        }

        /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd;AOP 审计/租户填充读它)。</summary>
        private sealed class FakeHttpContextAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }

        /// <summary>
        /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddProjectOptions + AddAutoServices(Core 程序集,
        /// 解析 [Service] 的 SysRoleTableService),InitTables 扫 Core 程序集增量建表(幂等,顺带建出 sys_role_table)。
        /// WHY 异步工厂而非同步构造:PG 探测是 I/O,同步构造只能 .Wait()/.Result 硬等(项目硬约定禁),
        /// 故探测改 await(TcpClient.ConnectAsync),容器经 CreateAsync 产出。
        /// </summary>
        private sealed class DbScope : IAsyncDisposable
        {
            public ISqlSugarClient Db { get; private set; } = null!;

            public ServiceProvider Provider { get; private set; } = null!;

            public bool PgReachable { get; private set; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

            public static async Task<DbScope> CreateAsync()
            {
                var scope = new DbScope();
                if (!await IsPostgresReachableAsync())
                {
                    // 降级:不建容器(用例侧 Assert.Fail);Provider 保持空壳以免调用方 NRE 掩盖真实原因
                    scope.Provider = new ServiceCollection().BuildServiceProvider();
                    return scope;
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
                services.AddSingleton<IHttpContextAccessor>(scope.FakeAccessor); // 须先于 AddSqlSugar(TryAdd 语义)
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config); // 无 ConnectionStrings:Redis → 内存实现(进程内,测试互不串扰)
                services.AddAutoServices(typeof(SysRoleTableService).Assembly);

                scope.Provider = services.BuildServiceProvider();
                scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
                TestSchemaBootstrap.Ensure(scope.Db);
                scope.PgReachable = true;
                return scope;
            }

            public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

            /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 BatchABTenantDbTest 口径,但不阻塞等待)。</summary>
            private static async Task<bool> IsPostgresReachableAsync()
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.ConnectAsync("127.0.0.1", 5432, timeout.Token);
                    return client.Connected;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}