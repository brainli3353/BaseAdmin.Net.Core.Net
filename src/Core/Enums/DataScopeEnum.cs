using System.ComponentModel;

namespace AdminCore.Core.Enums
{
    /// <summary>
    /// 机构数据范围(task_013 数据权限):角色对数据的可见范围。
    /// 收敛规则:用户持多角色时取最大范围(枚举值小 = 范围大,如 2 与 4 并存收敛为 2——对齐参考手册 §2.5)。
    /// </summary>
    [Description("数据范围")]
    public enum DataScopeEnum
    {
        /// <summary>全部(无行级过滤,超管等价;普通角色原则上不授予)。</summary>
        [Description("全部")]
        All = 1,

        /// <summary>本部门及以下(用户所属机构子树,含本部门)。</summary>
        [Description("本部门及以下")]
        OrgAndChild = 2,

        /// <summary>本部门(仅用户所属机构,不含子孙)。</summary>
        [Description("本部门")]
        Org = 3,

        /// <summary>仅本人(只看自己创建的行,[OwnerUser] 路径)。</summary>
        [Description("仅本人")]
        Self = 4,

        /// <summary>自定义(角色专属机构集;批 B B2 已建 sys_role_org 授权表,GetUserOrgScopeAsync 读该角色授权机构集,无授权行=空集 fail-closed)。</summary>
        [Description("自定义")]
        Define = 5
    }
}
