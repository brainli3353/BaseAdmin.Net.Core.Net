using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 机构种子(task_019 契约 §4):初始根节点「总部」(Pid=0)。
    /// 显式 Id + 显式 TenantId(默认租户):种子按主键缺行插入、已有跳过,主键必须确定性;
    /// TenantId 不依赖 AOP 填充(种子无请求上下文,显式赋值口径更稳,与菜单种子同口径)。
    /// </summary>
    /// <remarks>
    /// Id 分段约定(种子 Id 空间按库标识递增):菜单 10000+ / 角色 20000+ / 角色授权 30000+ /
    /// 用户关联 40000+ / 机构 50000+。Code 取 HQ(总部业务标识;与运行期业务新增编码无冲突面)。
    /// 幂等:重启重跑不重复;运行期对种子行的改名**不会被下次启动回写**(2026-10-30 起种子只补缺行,
    /// 系统行的演进改走版本化升级件)。
    /// </remarks>
    [IncreSeed]
    [SeedData(1)] // 顺序 1:无跨种子依赖(机构不引用其他种子,其他种子不引用机构)
    public class SysOrgSeedData : ISqlSugarEntitySeedData<SysOrg>
    {
        public IEnumerable<SysOrg> HasData()
        {
            var tenantId = AdminCore.Core.SqlSugarConst.DefaultTenantId;

            return new[]
            {
                // 初始根节点(契约:Name=总部,Pid=0)。
                // Remark 显式给值:共享开发库 sys_org.Remark 仍是 NOT NULL(task_013 旧 schema),
                // 列级 ALTER 未落地前种子插入不能依赖可空性——双保险
                new SysOrg { Id = 50000, Pid = 0, Name = "总部", Code = "HQ", Sort = 0, Remark = "初始根机构(系统种子)", Status = 0, TenantId = tenantId }
            };
        }
    }
}
