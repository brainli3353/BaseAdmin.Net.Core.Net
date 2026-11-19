namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 数据种子契约(task_008,对齐 baseadmin):实现类贴 [IncreSeed] 由 SqlSugarSetup.InitSeedData 扫描执行,
    /// HasData() 提供种子数据,框架按主键**缺行插入、已有跳过**(2026-10-30 起)——幂等,重启重跑不重复,
    /// 且永不覆写库内既有行(运行期/租户修改跨部署存活)。
    /// </summary>
    /// <remarks>
    /// 与命令式种子 ISqlSugarSeed 的关系:两者并存,机制独立——
    /// 数据种子(本接口)适合"数据即种子"的纯数据表(菜单/角色/关联表);
    /// 命令式种子适合需要查询/分支/跨表逻辑的种子(如按账号解析现有用户 Id 再挂角色)。
    /// 跨种子引用(如 RoleMenu 引用 RoleSeedData().HasData() 取 Id)直接 new 对方种子类,
    /// 执行顺序由 [SeedData(Order)] 升序保证。
    /// 约定:种子对象显式赋 Id(主键判存在性,Id=0 每轮都当新行)与 TenantId(种子行不依赖 AOP 填充)。
    /// 结构演进(新增/改默认值/删行)**不走种子**——种子只负责首次引导,演进由版本化升级件承接
    /// (2026-10-30 缺陷上报 §4 第二步;计划书 docs/业务/规划/计划_版本化升级件机制_2026-10-30.md)。
    /// </remarks>
    public interface ISqlSugarEntitySeedData<TEntity> where TEntity : class
    {
        /// <summary>种子数据(每次启动重新提供;幂等性由"主键缺行插入、已有跳过"保证)。</summary>
        IEnumerable<TEntity> HasData();
    }
}
