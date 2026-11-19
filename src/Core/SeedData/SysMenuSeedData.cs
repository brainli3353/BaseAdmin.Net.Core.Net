using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 系统菜单/按钮种子(契约 §8):基础菜单 Id 分段 10000 起——
    /// 工作台(dashboard 菜单)+ 六顶级目录(系统管理/系统配置/运维监控/日志审计/消息通知/工具开发,2026-09-30 拆分)
    /// + 24 管理菜单 + 全部管理端点按钮权限(sysUser/sysRole/sysMenu/sysOrg/sysPos/sysDictType/sysDictData/sysFile/sysJob/sysTenant)。
    /// 2026-09-30 拆分定案(葫芦爷爷):原"系统管理"单目录直挂 24 菜单不便使用,按职责拆六组——
    /// 系统管理 10001(用户/角色/菜单/机构/职位/在线用户)、系统配置 10180(租户/系统配置/字典/枚举/多语言)、
    /// 运维监控 10181(服务器监控/数据库管理/定时任务)、日志审计 10182(日志管理)、
    /// 消息通知 10183(通知公告/站内消息)、工具开发 10184(文件/开放接口/接口文档/代码生成/流水号/打印模板/交互示例)。
    /// 存量部署的树结构调整（如 2026-09-30 拆分）**不再靠种子 upsert 自动迁移**——种子只补缺行；
    /// 存量库结构演进改走版本化升级件（2026-10-30 缺陷上报 §4 第二步）；下文批注"挂系统管理 10001"为批历史溯源,以本头注释的树为准。
    /// Permission 与 API 路由一致(契约"一处配置两端生效":前端渲染与后端 403 校验同一份数据)。
    /// </summary>
    /// <remarks>
    /// 批 A 安全授权闭环(2.10 接口鉴权覆盖):补齐全部 RBAC 管理端点的按钮权限种子,
    /// 与控制器 [MenuPerm] 标注一一对应。登录即可端点(sysDictData/list、sysFile/upload)、
    /// 匿名端点(sysFile/download)、认证端点(auth/me|logout)不设按钮权限(无具体权限键,登录门禁承担)。
    /// </remarks>
    /// <remarks>
    /// 显式 Id + 显式 TenantId(默认租户):种子按主键缺行插入、已有跳过(2026-10-30 起),主键必须确定性;
    /// TenantId 不依赖 AOP 填充(种子无请求上下文,显式赋值口径更稳)。
    /// Id 分段约定(§5.2 种子 Id 空间按库标识递增):菜单 10000+ / 角色 20000+ / 关联 30000+。
    /// </remarks>
    [IncreSeed]
    [SeedData(1)] // 顺序 1:被 SysRoleMenuSeedData(顺序 3)引用,必须先执行
    public class SysMenuSeedData : ISqlSugarEntitySeedData<SysMenu>
    {
        public IEnumerable<SysMenu> HasData()
        {
            var tenantId = AdminCore.Core.SqlSugarConst.DefaultTenantId;

            return new[]
            {
                // ── 顶级 ─────────────────────────────────────────────
                // 工作台(菜单,路由 dashboard)
                new SysMenu { Id = 10000, Pid = 0, Name = "dashboard", Title = "工作台", Type = 2, OrderNo = 1, TenantId = tenantId },
                // 系统管理(目录)
                new SysMenu { Id = 10001, Pid = 0, Name = "sys", Title = "系统管理", Type = 1, OrderNo = 2, TenantId = tenantId },
                // ── 2026-09-30 拆分新增目录(Id 接菜单段末尾 10180+,与 SysRoleMenuSeedData 按 Id 升序的序号映射同约束:
                //    新 Id 大于既有最大 Id ⇒ 既有授权行序号不漂移;SysRoleMenuSeedData 动态派生 ⇒ 新目录自动授权系统管理员)──
                new SysMenu { Id = 10180, Pid = 0, Name = "config", Title = "系统配置", Type = 1, OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10181, Pid = 0, Name = "monitor", Title = "运维监控", Type = 1, OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10182, Pid = 0, Name = "logAudit", Title = "日志审计", Type = 1, OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10183, Pid = 0, Name = "messageCenter", Title = "消息通知", Type = 1, OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10184, Pid = 0, Name = "tooling", Title = "工具开发", Type = 1, OrderNo = 7, TenantId = tenantId },

                // ── 系统管理下(2026-09-30 拆分后:用户/角色/菜单/机构/职位/在线用户六菜单)──
                new SysMenu { Id = 10002, Pid = 10001, Name = "user", Title = "用户管理", Type = 2, OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10003, Pid = 10001, Name = "role", Title = "角色管理", Type = 2, OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10004, Pid = 10001, Name = "menu", Title = "菜单管理", Type = 2, OrderNo = 3, TenantId = tenantId },

                // ── 用户管理按钮(Permission = 接口路由)──────────────
                new SysMenu { Id = 10010, Pid = 10002, Name = "sysUser/page", Title = "用户查询", Type = 3, Permission = "sysUser/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10011, Pid = 10002, Name = "sysUser/add", Title = "用户新增", Type = 3, Permission = "sysUser/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10012, Pid = 10002, Name = "sysUser/update", Title = "用户修改", Type = 3, Permission = "sysUser/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10013, Pid = 10002, Name = "sysUser/delete", Title = "用户删除", Type = 3, Permission = "sysUser/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10014, Pid = 10002, Name = "sysUser/grantRole", Title = "分配角色", Type = 3, Permission = "sysUser/grantRole", OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10015, Pid = 10002, Name = "sysUser/resetPwd", Title = "重置密码", Type = 3, Permission = "sysUser/resetPwd", OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10016, Pid = 10002, Name = "sysUser/unlockLogin", Title = "解锁登录", Type = 3, Permission = "sysUser/unlockLogin", OrderNo = 7, TenantId = tenantId },
                new SysMenu { Id = 10017, Pid = 10002, Name = "sysUser/export", Title = "用户导出", Type = 3, Permission = "sysUser/export", OrderNo = 8, TenantId = tenantId },

                // ── 角色管理按钮 ─────────────────────────────────────
                new SysMenu { Id = 10020, Pid = 10003, Name = "sysRole/page", Title = "角色查询", Type = 3, Permission = "sysRole/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10021, Pid = 10003, Name = "sysRole/add", Title = "角色新增", Type = 3, Permission = "sysRole/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10022, Pid = 10003, Name = "sysRole/update", Title = "角色修改", Type = 3, Permission = "sysRole/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10023, Pid = 10003, Name = "sysRole/delete", Title = "角色删除", Type = 3, Permission = "sysRole/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10024, Pid = 10003, Name = "sysRole/grantMenu", Title = "角色授权", Type = 3, Permission = "sysRole/grantMenu", OrderNo = 5, TenantId = tenantId },

                // ── 菜单管理按钮(树查询端点路由名为 page:权限键与路由必须一致)──
                new SysMenu { Id = 10030, Pid = 10004, Name = "sysMenu/page", Title = "菜单查询", Type = 3, Permission = "sysMenu/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10031, Pid = 10004, Name = "sysMenu/add", Title = "菜单新增", Type = 3, Permission = "sysMenu/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10032, Pid = 10004, Name = "sysMenu/update", Title = "菜单修改", Type = 3, Permission = "sysMenu/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10033, Pid = 10004, Name = "sysMenu/delete", Title = "菜单删除", Type = 3, Permission = "sysMenu/delete", OrderNo = 4, TenantId = tenantId },

                // ── 角色管理接口黑名单按钮(批 A 2.10:SysRoleApi 细粒度禁用——角色已授权按钮中显式回收)──
                new SysMenu { Id = 10025, Pid = 10003, Name = "sysRole/grantApi", Title = "角色接口黑名单授权", Type = 3, Permission = "sysRole/grantApi", OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10026, Pid = 10003, Name = "sysRole/api", Title = "角色接口黑名单查询", Type = 3, Permission = "sysRole/api", OrderNo = 7, TenantId = tenantId },
                // ── 角色管理数据范围授权按钮(批 B B2:Define 自定义范围 SysRoleOrg;Id 取菜单段末尾 10090/10091,
                //    物理归角色组仅可读——Id 最大保证 SysRoleMenuSeedData 按 Id 升序时排末位,不漂移既有授权行序号/防孤儿行)──
                new SysMenu { Id = 10090, Pid = 10003, Name = "sysRole/grantOrg", Title = "角色数据范围授权", Type = 3, Permission = "sysRole/grantOrg", OrderNo = 8, TenantId = tenantId },
                new SysMenu { Id = 10091, Pid = 10003, Name = "sysRole/org", Title = "角色数据范围授权查询", Type = 3, Permission = "sysRole/org", OrderNo = 9, TenantId = tenantId },

                // ── 机构管理菜单 + 按钮(批 A 2.10 覆盖:sysOrg 端点权限键与路由一致)──
                new SysMenu { Id = 10005, Pid = 10001, Name = "sysOrg", Title = "机构管理", Type = 2, OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10040, Pid = 10005, Name = "sysOrg/page", Title = "机构查询", Type = 3, Permission = "sysOrg/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10041, Pid = 10005, Name = "sysOrg/tree", Title = "机构树", Type = 3, Permission = "sysOrg/tree", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10042, Pid = 10005, Name = "sysOrg/add", Title = "机构新增", Type = 3, Permission = "sysOrg/add", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10043, Pid = 10005, Name = "sysOrg/update", Title = "机构修改", Type = 3, Permission = "sysOrg/update", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10044, Pid = 10005, Name = "sysOrg/delete", Title = "机构删除", Type = 3, Permission = "sysOrg/delete", OrderNo = 5, TenantId = tenantId },

                // ── 职位管理菜单 + 按钮 ──
                new SysMenu { Id = 10006, Pid = 10001, Name = "sysPos", Title = "职位管理", Type = 2, OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10050, Pid = 10006, Name = "sysPos/page", Title = "职位查询", Type = 3, Permission = "sysPos/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10051, Pid = 10006, Name = "sysPos/add", Title = "职位新增", Type = 3, Permission = "sysPos/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10052, Pid = 10006, Name = "sysPos/update", Title = "职位修改", Type = 3, Permission = "sysPos/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10053, Pid = 10006, Name = "sysPos/delete", Title = "职位删除", Type = 3, Permission = "sysPos/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10054, Pid = 10006, Name = "sysPos/import", Title = "职位导入", Type = 3, Permission = "sysPos/import", OrderNo = 5, TenantId = tenantId },

                // ── 字典管理菜单 + 按钮(类型 + 值两组;值读取端点 list 登录即可不设权限键)──
                new SysMenu { Id = 10007, Pid = 10180, Name = "sysDict", Title = "字典管理", Type = 2, OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10060, Pid = 10007, Name = "sysDictType/page", Title = "字典类型查询", Type = 3, Permission = "sysDictType/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10061, Pid = 10007, Name = "sysDictType/add", Title = "字典类型新增", Type = 3, Permission = "sysDictType/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10062, Pid = 10007, Name = "sysDictType/update", Title = "字典类型修改", Type = 3, Permission = "sysDictType/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10063, Pid = 10007, Name = "sysDictType/delete", Title = "字典类型删除", Type = 3, Permission = "sysDictType/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10064, Pid = 10007, Name = "sysDictData/page", Title = "字典值查询", Type = 3, Permission = "sysDictData/page", OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10065, Pid = 10007, Name = "sysDictData/add", Title = "字典值新增", Type = 3, Permission = "sysDictData/add", OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10066, Pid = 10007, Name = "sysDictData/update", Title = "字典值修改", Type = 3, Permission = "sysDictData/update", OrderNo = 7, TenantId = tenantId },
                new SysMenu { Id = 10067, Pid = 10007, Name = "sysDictData/delete", Title = "字典值删除", Type = 3, Permission = "sysDictData/delete", OrderNo = 8, TenantId = tenantId },
                new SysMenu { Id = 10068, Pid = 10007, Name = "sysDictData/updateStatus", Title = "字典值启停", Type = 3, Permission = "sysDictData/updateStatus", OrderNo = 9, TenantId = tenantId },

                // ── 文件管理菜单 + 按钮(upload 登录即可 / download 匿名,均不设权限键)──
                new SysMenu { Id = 10008, Pid = 10184, Name = "sysFile", Title = "文件管理", Type = 2, OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10070, Pid = 10008, Name = "sysFile/delete", Title = "文件删除", Type = 3, Permission = "sysFile/delete", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10071, Pid = 10008, Name = "sysFile/associate", Title = "文件关联", Type = 3, Permission = "sysFile/associate", OrderNo = 2, TenantId = tenantId },
                // 文件清单(批 UX B1c:sysFile/page 端点权限键):Id=10188 接既有最大 Id 10187 之后——
                // SysRoleMenuSeedData 按 Id 升序的序号映射不漂移,系统管理员自动授新按钮
                new SysMenu { Id = 10188, Pid = 10008, Name = "sysFile/page", Title = "文件查询", Type = 3, Permission = "sysFile/page", OrderNo = 3, TenantId = tenantId },

                // ── 定时任务菜单 + 按钮 ──
                new SysMenu { Id = 10009, Pid = 10181, Name = "sysJob", Title = "定时任务", Type = 2, OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10080, Pid = 10009, Name = "sysJob/page", Title = "任务查询", Type = 3, Permission = "sysJob/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10081, Pid = 10009, Name = "sysJob/add", Title = "任务新增", Type = 3, Permission = "sysJob/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10082, Pid = 10009, Name = "sysJob/update", Title = "任务修改", Type = 3, Permission = "sysJob/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10083, Pid = 10009, Name = "sysJob/enable", Title = "任务启用", Type = 3, Permission = "sysJob/enable", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10084, Pid = 10009, Name = "sysJob/disable", Title = "任务禁用", Type = 3, Permission = "sysJob/disable", OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10085, Pid = 10009, Name = "sysJob/runNow", Title = "任务执行", Type = 3, Permission = "sysJob/runNow", OrderNo = 6, TenantId = tenantId },
                // 执行记录(2026-09-30):Id=10185 接既有最大 Id(10184)后——SysRoleMenuSeedData 按 Id 升序的序号映射不漂移,
                // 动态派生 ⇒ 系统管理员自动授新按钮
                new SysMenu { Id = 10185, Pid = 10009, Name = "sysJob/logPage", Title = "任务执行记录", Type = 3, Permission = "sysJob/logPage", OrderNo = 7, TenantId = tenantId },

                // ── 租户管理菜单 + 按钮(批 C 多租户业务模块 C1:sysTenant 端点,权限键与路由一致;
                //    Id 取菜单段末尾 10092-10097(>10091),保证 SysRoleMenuSeedData 按 Id 升序时排末位,不漂移既有授权行序号/防孤儿行)──
                new SysMenu { Id = 10092, Pid = 10180, Name = "sysTenant", Title = "租户管理", Type = 2, OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10093, Pid = 10092, Name = "sysTenant/page", Title = "租户查询", Type = 3, Permission = "sysTenant/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10094, Pid = 10092, Name = "sysTenant/add", Title = "租户新增", Type = 3, Permission = "sysTenant/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10095, Pid = 10092, Name = "sysTenant/update", Title = "租户修改", Type = 3, Permission = "sysTenant/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10096, Pid = 10092, Name = "sysTenant/delete", Title = "租户删除", Type = 3, Permission = "sysTenant/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10097, Pid = 10092, Name = "sysTenant/setStatus", Title = "租户启停", Type = 3, Permission = "sysTenant/setStatus", OrderNo = 5, TenantId = tenantId },

                // ── 系统配置菜单 + 按钮(批 D 4.1:sysConfig 端点;Id 取菜单段末尾 10098-10103(>10097),
                //    保证 SysRoleMenuSeedData 按 Id 升序时排末位,不漂移既有授权行序号/防孤儿行)──
                new SysMenu { Id = 10098, Pid = 10180, Name = "sysConfig", Title = "系统配置", Type = 2, OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10099, Pid = 10098, Name = "sysConfig/page", Title = "配置查询", Type = 3, Permission = "sysConfig/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10100, Pid = 10098, Name = "sysConfig/add", Title = "配置新增", Type = 3, Permission = "sysConfig/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10101, Pid = 10098, Name = "sysConfig/update", Title = "配置修改", Type = 3, Permission = "sysConfig/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10102, Pid = 10098, Name = "sysConfig/delete", Title = "配置删除", Type = 3, Permission = "sysConfig/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10103, Pid = 10098, Name = "sysConfig/get", Title = "配置读值", Type = 3, Permission = "sysConfig/get", OrderNo = 5, TenantId = tenantId },

                // ── 日志管理菜单 + 按钮(批 D 5.1/5.18:操作日志 + 登录日志查询端点;Id 取菜单段末尾 10104-10106)──
                new SysMenu { Id = 10104, Pid = 10182, Name = "log", Title = "日志管理", Type = 2, OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10105, Pid = 10104, Name = "sysLogOp/page", Title = "操作日志查询", Type = 3, Permission = "sysLogOp/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10106, Pid = 10104, Name = "sysLogLogin/page", Title = "登录日志查询", Type = 3, Permission = "sysLogLogin/page", OrderNo = 2, TenantId = tenantId },
                // 操作日志详情(2026-09-30 批 AZ):Id=10186 接既有最大 Id(10185)后——参照口径补 RequestParam/ReturnResult 大字段,
                // 详情按钮 sysLogOp/detail 与 [MenuPerm] 一一对应(约定 14 全栈闭环)
                new SysMenu { Id = 10186, Pid = 10104, Name = "sysLogOp/detail", Title = "操作日志详情", Type = 3, Permission = "sysLogOp/detail", OrderNo = 3, TenantId = tenantId },

                // 开放接口(批 E 1.6/1.7/6.10):菜单 + 六按钮(sysOpenAccess/page|add|update|delete|resetSecret|generateSignature)
                new SysMenu { Id = 10107, Pid = 10184, Name = "openAccess", Title = "开放接口", Type = 2, OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10108, Pid = 10107, Name = "sysOpenAccess/page", Title = "身份查询", Type = 3, Permission = "sysOpenAccess/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10109, Pid = 10107, Name = "sysOpenAccess/add", Title = "身份建号", Type = 3, Permission = "sysOpenAccess/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10110, Pid = 10107, Name = "sysOpenAccess/update", Title = "身份改绑", Type = 3, Permission = "sysOpenAccess/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10111, Pid = 10107, Name = "sysOpenAccess/delete", Title = "身份删除", Type = 3, Permission = "sysOpenAccess/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10112, Pid = 10107, Name = "sysOpenAccess/resetSecret", Title = "密钥重置", Type = 3, Permission = "sysOpenAccess/resetSecret", OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10113, Pid = 10107, Name = "sysOpenAccess/generateSignature", Title = "签名预览", Type = 3, Permission = "sysOpenAccess/generateSignature", OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10114, Pid = 10001, Name = "onlineUser", Title = "在线用户", Type = 2, OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10115, Pid = 10114, Name = "onlineUser/list", Title = "在线列表", Type = 3, Permission = "onlineUser/list", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10116, Pid = 10114, Name = "onlineUser/forceOffline", Title = "强制下线", Type = 3, Permission = "onlineUser/forceOffline", OrderNo = 2, TenantId = tenantId },

                // ── 通知公告菜单 + 管理面五按钮(批 G 4.7:自读面 setRead/pageReceived/unReadList/last/detail 豁免 [MenuPerm] 无按钮——裁决③)──
                new SysMenu { Id = 10117, Pid = 10183, Name = "sysNotice", Title = "通知公告", Type = 2, OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10118, Pid = 10117, Name = "sysNotice/page", Title = "通知查询", Type = 3, Permission = "sysNotice/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10119, Pid = 10117, Name = "sysNotice/add", Title = "通知新建", Type = 3, Permission = "sysNotice/add", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10120, Pid = 10117, Name = "sysNotice/update", Title = "通知修改", Type = 3, Permission = "sysNotice/update", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10121, Pid = 10117, Name = "sysNotice/delete", Title = "通知删除", Type = 3, Permission = "sysNotice/delete", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10122, Pid = 10117, Name = "sysNotice/public", Title = "通知发布", Type = 3, Permission = "sysNotice/public", OrderNo = 5, TenantId = tenantId },

                // ── 站内消息菜单 + 发送双按钮(批 G 4.8:发送入口仅服务端)──
                new SysMenu { Id = 10123, Pid = 10183, Name = "sysMessage", Title = "站内消息", Type = 2, OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10124, Pid = 10123, Name = "sysMessage/sendUser", Title = "消息发送某人", Type = 3, Permission = "sysMessage/sendUser", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10125, Pid = 10123, Name = "sysMessage/sendAll", Title = "消息群发", Type = 3, Permission = "sysMessage/sendAll", OrderNo = 2, TenantId = tenantId },

                // ── 角色复制 + 菜单授权回读按钮(批 H 2.2/2.4)──
                new SysMenu { Id = 10126, Pid = 10003, Name = "sysRole/copy", Title = "角色复制", Type = 3, Permission = "sysRole/copy", OrderNo = 10, TenantId = tenantId },
                new SysMenu { Id = 10127, Pid = 10003, Name = "sysRole/menu", Title = "角色菜单授权查询", Type = 3, Permission = "sysRole/menu", OrderNo = 11, TenantId = tenantId },

                // ── 异常日志按钮(批 I 5.1:挂日志管理菜单 10104 段尾,OrderNo 接 sysLogLogin/page 之后)──
                new SysMenu { Id = 10128, Pid = 10104, Name = "sysLogEx/page", Title = "异常日志查询", Type = 3, Permission = "sysLogEx/page", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10129, Pid = 10104, Name = "sysLogEx/detail", Title = "异常日志详情", Type = 3, Permission = "sysLogEx/detail", OrderNo = 4, TenantId = tenantId },

                // ── 差异日志按钮(批 X 5.4:挂日志管理菜单 10104 段尾,OrderNo 接异常日志之后)──
                new SysMenu { Id = 10142, Pid = 10104, Name = "sysLogDiff/page", Title = "差异日志查询", Type = 3, Permission = "sysLogDiff/page", OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10143, Pid = 10104, Name = "sysLogDiff/detail", Title = "差异日志详情", Type = 3, Permission = "sysLogDiff/detail", OrderNo = 6, TenantId = tenantId },

                // ── 流水号管理(批 W 4.3/3.4:挂系统管理 10001,目录 + 11 按钮端点全量 [MenuPerm] 对齐)──
                new SysMenu { Id = 10130, Pid = 10184, Name = "sysSerial", Title = "流水号管理", Type = 2, OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10131, Pid = 10130, Name = "sysSerial/page", Title = "流水号查询", Type = 3, Permission = "sysSerial/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10132, Pid = 10130, Name = "sysSerial/detail", Title = "流水号详情", Type = 3, Permission = "sysSerial/detail", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10133, Pid = 10130, Name = "sysSerial/add", Title = "流水号新增", Type = 3, Permission = "sysSerial/add", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10134, Pid = 10130, Name = "sysSerial/update", Title = "流水号更新", Type = 3, Permission = "sysSerial/update", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10135, Pid = 10130, Name = "sysSerial/delete", Title = "流水号删除", Type = 3, Permission = "sysSerial/delete", OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10136, Pid = 10130, Name = "sysSerial/setStatus", Title = "流水号启停", Type = 3, Permission = "sysSerial/setStatus", OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10137, Pid = 10130, Name = "sysSerial/nextSeqNo", Title = "取流水号", Type = 3, Permission = "sysSerial/nextSeqNo", OrderNo = 7, TenantId = tenantId },
                new SysMenu { Id = 10138, Pid = 10130, Name = "sysSerial/globalNextSeqNo", Title = "取全局流水号", Type = 3, Permission = "sysSerial/globalNextSeqNo", OrderNo = 8, TenantId = tenantId },
                new SysMenu { Id = 10139, Pid = 10130, Name = "sysSerial/preview", Title = "流水号预览", Type = 3, Permission = "sysSerial/preview", OrderNo = 9, TenantId = tenantId },
                new SysMenu { Id = 10140, Pid = 10130, Name = "sysSerial/typeList", Title = "流水号类型列表", Type = 3, Permission = "sysSerial/typeList", OrderNo = 10, TenantId = tenantId },
                new SysMenu { Id = 10141, Pid = 10130, Name = "sysSerial/slotList", Title = "流水号插槽列表", Type = 3, Permission = "sysSerial/slotList", OrderNo = 11, TenantId = tenantId },

                // ── 服务器监控(批 Y 4.14:挂系统管理 10001,菜单 + 3 只读按钮端点全量 [MenuPerm] 对齐)──
                new SysMenu { Id = 10144, Pid = 10181, Name = "sysServer", Title = "服务器监控", Type = 2, OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10145, Pid = 10144, Name = "sysServer/runtime", Title = "运行时信息", Type = 3, Permission = "sysServer/runtime", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10146, Pid = 10144, Name = "sysServer/hardware", Title = "硬件信息", Type = 3, Permission = "sysServer/hardware", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10147, Pid = 10144, Name = "sysServer/assembly", Title = "程序集清单", Type = 3, Permission = "sysServer/assembly", OrderNo = 3, TenantId = tenantId },
                // 批 AT 多节点监控:第 4 把按钮键(聚合端点 sysServer/nodes),Id 取当时最大值 +1(10177 之后)
                new SysMenu { Id = 10178, Pid = 10144, Name = "sysServer/nodes", Title = "集群节点视图", Type = 3, Permission = "sysServer/nodes", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10148, Pid = 10180, Name = "sysEnum", Title = "系统枚举", Type = 2, OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10149, Pid = 10148, Name = "sysEnum/typeList", Title = "枚举类型列表", Type = 3, Permission = "sysEnum/typeList", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10150, Pid = 10148, Name = "sysEnum/data", Title = "枚举值查询", Type = 3, Permission = "sysEnum/data", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10151, Pid = 10148, Name = "sysEnum/dataByField", Title = "字段枚举查询", Type = 3, Permission = "sysEnum/dataByField", OrderNo = 3, TenantId = tenantId },

                // ── 租户库初始化按钮(批 AB 多租户库级隔离 C4:挂租户管理菜单 10092 段尾,
                //    OrderNo 接 sysTenant/setStatus 之后;Id 取全局段末 10152 防既有授权行序号漂移)──
                new SysMenu { Id = 10152, Pid = 10092, Name = "sysTenant/initDb", Title = "租户库初始化", Type = 3, Permission = "sysTenant/initDb", OrderNo = 6, TenantId = tenantId },

                // ── Http 出站日志按钮(批 AD 5.3:挂日志管理菜单 10104 段尾,OrderNo 接差异日志之后)──
                new SysMenu { Id = 10153, Pid = 10104, Name = "sysLogHttp/page", Title = "Http日志查询", Type = 3, Permission = "sysLogHttp/page", OrderNo = 7, TenantId = tenantId },
                new SysMenu { Id = 10154, Pid = 10104, Name = "sysLogHttp/detail", Title = "Http日志详情", Type = 3, Permission = "sysLogHttp/detail", OrderNo = 8, TenantId = tenantId },

                // ── 数据库管理(批 AD 3.8:挂系统管理 10001,菜单 + 3 只读按钮端点全量 [MenuPerm] 对齐;
                //    危险操作(DDL/备份/还原)未开放,见 SysDatabaseService remarks)──
                new SysMenu { Id = 10155, Pid = 10181, Name = "sysDatabase", Title = "数据库管理", Type = 2, OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10156, Pid = 10155, Name = "sysDatabase/tables", Title = "表清单", Type = 3, Permission = "sysDatabase/tables", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10157, Pid = 10155, Name = "sysDatabase/columns", Title = "列结构", Type = 3, Permission = "sysDatabase/columns", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10158, Pid = 10155, Name = "sysDatabase/indexes", Title = "索引清单", Type = 3, Permission = "sysDatabase/indexes", OrderNo = 3, TenantId = tenantId },

                // ── 打印模板(批 AF 4.6:只存不渲染;挂系统管理 10001,菜单 + 6 按钮端点全量 [MenuPerm] 对齐,
                //    含独立 sysPrint/copy 键——参照仓复制复用 add 键,本仓单列以防越权)──
                new SysMenu { Id = 10159, Pid = 10184, Name = "sysPrint", Title = "打印模板", Type = 2, OrderNo = 6, TenantId = tenantId },
                new SysMenu { Id = 10160, Pid = 10159, Name = "sysPrint/page", Title = "模板查询", Type = 3, Permission = "sysPrint/page", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10161, Pid = 10159, Name = "sysPrint/detail", Title = "模板详情", Type = 3, Permission = "sysPrint/detail", OrderNo = 2, TenantId = tenantId },
                new SysMenu { Id = 10162, Pid = 10159, Name = "sysPrint/add", Title = "模板新增", Type = 3, Permission = "sysPrint/add", OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10163, Pid = 10159, Name = "sysPrint/update", Title = "模板修改", Type = 3, Permission = "sysPrint/update", OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10164, Pid = 10159, Name = "sysPrint/copy", Title = "模板复制", Type = 3, Permission = "sysPrint/copy", OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10165, Pid = 10159, Name = "sysPrint/delete", Title = "模板删除", Type = 3, Permission = "sysPrint/delete", OrderNo = 6, TenantId = tenantId },

                // ── 接口文档(批 AG 6.7:零依赖自建 OpenAPI;挂系统管理 10001,菜单 + 1 按钮端点 [MenuPerm] 对齐)──
                new SysMenu { Id = 10166, Pid = 10184, Name = "apiDoc", Title = "接口文档", Type = 2, OrderNo = 3, TenantId = tenantId },
                new SysMenu { Id = 10167, Pid = 10166, Name = "apiDoc/openapi", Title = "接口文档查询", Type = 3, Permission = "apiDoc/openapi", OrderNo = 1, TenantId = tenantId },

                // ── 角色字段权限按钮(批 AH 2.9:挂角色管理菜单 10003 段尾,OrderNo 接 sysRole/menu 之后)──
                new SysMenu { Id = 10168, Pid = 10003, Name = "sysRole/grantTable", Title = "角色字段权限授权", Type = 3, Permission = "sysRole/grantTable", OrderNo = 12, TenantId = tenantId },
                new SysMenu { Id = 10169, Pid = 10003, Name = "sysRole/table", Title = "角色字段权限查询", Type = 3, Permission = "sysRole/table", OrderNo = 13, TenantId = tenantId },
                new SysMenu { Id = 10170, Pid = 10003, Name = "sysRole/allTableColumn", Title = "字段权限授权树", Type = 3, Permission = "sysRole/allTableColumn", OrderNo = 14, TenantId = tenantId },
                new SysMenu { Id = 10175, Pid = 10003, Name = "sysRole/tableScope", Title = "字段权限表级开关", Type = 3, Permission = "sysRole/tableScope", OrderNo = 15, TenantId = tenantId },

                // ── 代码生成(批 AI 6.1:零依赖自建模板,只返回文本不写盘;挂系统管理 10001)──
                new SysMenu { Id = 10171, Pid = 10184, Name = "sysCodeGen", Title = "代码生成", Type = 2, OrderNo = 4, TenantId = tenantId },
                new SysMenu { Id = 10176, Pid = 10180, Name = "sysLocalization", Title = "多语言", Type = 2, OrderNo = 5, TenantId = tenantId },
                new SysMenu { Id = 10177, Pid = 10176, Name = "sysLocalization/cultures", Title = "语言列表", Type = 3, Permission = "sysLocalization/cultures", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10172, Pid = 10171, Name = "sysCodeGen/tables", Title = "表清单查询", Type = 3, Permission = "sysCodeGen/tables", OrderNo = 1, TenantId = tenantId },
                new SysMenu { Id = 10173, Pid = 10171, Name = "sysCodeGen/preview", Title = "生成预览", Type = 3, Permission = "sysCodeGen/preview", OrderNo = 2, TenantId = tenantId },

                // ── 定时任务集群视图(批 AJ 5.16;挂任务管理 10009 段尾)──
                new SysMenu { Id = 10174, Pid = 10009, Name = "sysJob/nodes", Title = "集群节点视图", Type = 3, Permission = "sysJob/nodes", OrderNo = 9, TenantId = tenantId },

                // ── 交互模式示例页(批 BD:需求单「表格加载态与统一交互」2026-09-29 六件套的一页示例;
                //    纯前端演示页、零 API 端点 ⇒ 无按钮权限键;挂系统管理 10001 段尾,OrderNo 接多语言之后)──
                new SysMenu { Id = 10179, Pid = 10184, Name = "uiPatterns", Title = "交互模式示例", Type = 2, OrderNo = 7, TenantId = tenantId },

                // ── 菜单启停按钮(批 CH G28:sysMenu/setStatus 端点权限键;Id=10187 接既有最大 Id 10186 之后,
                //    SysRoleMenuSeedData 按 Id 升序序号映射不漂移 ⇒ 自动授权系统管理员,防孤儿授权行)──
                new SysMenu { Id = 10187, Pid = 10004, Name = "sysMenu/setStatus", Title = "菜单启停", Type = 3, Permission = "sysMenu/setStatus", OrderNo = 5, TenantId = tenantId }
            };
        }
    }
}
