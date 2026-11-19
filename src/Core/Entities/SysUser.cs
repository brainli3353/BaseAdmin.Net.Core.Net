using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统用户(登录账号 + 密码哈希 + 姓名 + 状态)。继承 EntityTenant 获得雪花主键/审计/软删/租户字段
    /// (task_008 起基座先行:业务实体一律 EntityTenant,存量行 TenantId 可空由登录侧兜底默认租户)。
    /// </summary>
    [SugarTable("sys_user", "系统用户")]
    // WHY 索引:高频查询列——AuthService 登录按账号取用户＋SysUserService.Page 账号条件/Add 查重(IsAnyAsync Account==)
    [SugarIndex("i_sys_user_account", nameof(Account), OrderByType.Asc)]
    // WHY 索引:高频查询列——SysUserService.Page 机构范围过滤(数据权限 orgIdList.Contains(u.OrgId))
    [SugarIndex("i_sys_user_orgid", nameof(OrgId), OrderByType.Asc)]
    // WHY 索引:高频查询列——SysUserService.Page 状态过滤(WhereIF u.Status==input.Status)＋登录停用校验
    [SugarIndex("i_sys_user_status", nameof(Status), OrderByType.Asc)]
    [IncreTable]
    [SysTable]
    public class SysUser : EntityTenant
    {
        /// <summary>账号(登录名)。ORM 读回无空值保证,按项目实体惯例用可空引用类型(基类同口径)。</summary>
        [SugarColumn(ColumnDescription = "账号", Length = 64)]
        public string? Account { get; set; }

        /// <summary>密码哈希(PBKDF2 存储格式 "{盐Base64}${哈希Base64}",明文永不落库;见 Core/Security/Pbkdf2Helper)。</summary>
        [SugarColumn(ColumnDescription = "密码哈希", Length = 256)]
        public string? Password { get; set; }

        /// <summary>姓名(展示名)。</summary>
        [SugarColumn(ColumnDescription = "姓名", Length = 64)]
        public string? RealName { get; set; }

        /// <summary>状态:0 正常,1 停用(停用账号登录被拒,见 AuthService 登录校验顺序)。</summary>
        [SugarColumn(ColumnDescription = "状态")]
        public int Status { get; set; } = 0;

        /// <summary>
        /// 账号类型:999 超级管理员,777 普通(默认)。M2 权限体系据此区分超管穿透;
        /// DefaultValue 兜底存量行——dev 库 sys_user 已存在,CodeFirst 增量加列时旧行取默认值,不报非空约束错。
        /// </summary>
        [SugarColumn(ColumnDescription = "账号类型", DefaultValue = "777")]
        public int AccountType { get; set; } = 777;

        /// <summary>
        /// Token 版本(强制失效机制:改密/踢出时 +1,旧 Token 的 TokenVersion Claim 与库不一致即被 JwtAuthHandler 拒绝)。
        /// 同样配 DefaultValue 兜底存量行。
        /// </summary>
        [SugarColumn(ColumnDescription = "Token版本", DefaultValue = "1")]
        public int TokenVersion { get; set; } = 1;

        /// <summary>
        /// 组织 Id(数据权限迭代预留位:M2.5 按组织范围做 ORM 行级拦截时读取本字段;本任务只建列不实现过滤)。
        /// 可空:存量用户无组织归属;DefaultValue 不加(语义上"无组织"就是 NULL,不给假默认值)。
        /// </summary>
        [SugarColumn(ColumnDescription = "组织Id(数据权限预留)", IsNullable = true)]
        public long? OrgId { get; set; }

        /// <summary>
        /// 职位 Id(task_020 职位模块:用户↔职位关联锚点)。
        /// WHY 本任务加列:契约 §2「Delete 校验:有关联用户时拒绝」需用户↔职位关联字段方可实现,
        /// 必读参考手册 §2.6 代码参考即 SysUser.PosId,机构模块 OrgId 同为先例(task_013 增量加列)。
        /// 可空:存量用户无职位归属;"无职位"即 NULL,不给假默认值;用户域 Add/Update 何时回填本列属用户模块职责,
        /// 本任务仅建列供 SysPosService.Delete 关联校验读取(最小必要改动,不动用户域契约)。
        /// </summary>
        [SugarColumn(ColumnDescription = "职位Id(用户↔职位关联)", IsNullable = true)]
        public long? PosId { get; set; }

        /// <summary>
        /// 最后改密时间(批 M 1.11 密码有效期):用户自改密=Now;管理员重置=null(用户未自证新密码→必触发到期改密,同参照口径)。
        /// 可空:存量行 NULL=未改过密码→启用有效期(ExpirationDays>0)即到期,标准"首次使用强制改密"实践。
        /// </summary>
        [SugarColumn(ColumnDescription = "最后改密时间(密码有效期判定)", IsNullable = true)]
        public DateTime? LastChangePasswordTime { get; set; }
    }
}
