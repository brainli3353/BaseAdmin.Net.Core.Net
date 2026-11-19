using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统职位(task_020 契约 §1):名称/编码/排序/备注/状态。继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// Code 唯一性由服务层校验(建库不配唯一约束,与 SysDictType/SysRole 实体口径一致);
    /// 用户↔职位关联经 SysUser.PosId(本任务增量加列,契约 §2 Delete 关联校验所需),
    /// 本实体不含用户导航属性——参照手册 §2.6:职位人员统计走 SysUser.PosId 子查询,不依赖 Includes 导航加载。
    /// </remarks>
    [SugarTable("sys_pos", "职位")]
    [IncreTable]
    [SysTable]
    public class SysPos : EntityTenant
    {
        /// <summary>职位名称(展示名;ORM 读回无空值保证,按项目实体惯例用可空引用类型)。</summary>
        [SugarColumn(ColumnDescription = "职位名称", Length = 64)]
        public string? Name { get; set; }

        /// <summary>职位编码(唯一,业务标识;服务层校验唯一性)。</summary>
        [SugarColumn(ColumnDescription = "职位编码", Length = 64)]
        public string? Code { get; set; }

        /// <summary>排序号(前端列表展示顺序;值小者靠前,缺省 0)。</summary>
        [SugarColumn(ColumnDescription = "排序")]
        public int Sort { get; set; } = 0;

        /// <summary>备注(用途说明;可空——可选字段,对齐 SysMenu.Perm/Icon 的 IsNullable 范式,允许为空)。</summary>
        [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
        public string? Remark { get; set; }

        /// <summary>状态:0 正常(启用),1 停用(停用职位不再用于新关联,存量关联保留;与 SysUser.Status 同口径)。</summary>
        [SugarColumn(ColumnDescription = "状态")]
        public int Status { get; set; } = 0;
    }
}
