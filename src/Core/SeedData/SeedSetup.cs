using System.Reflection;
using SqlSugar;
using AdminCore.Core.Attributes;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 增量种子执行器:扫描程序集内 [IncreSeed] 类并逐个执行(SqlSugarSetup.InitTables 建表侧的种子侧对应物)。
    /// </summary>
    public static class SeedSetup
    {
        /// <summary>
        /// 扫描 [IncreSeed] + <see cref="ISqlSugarSeed"/> 类并按类名排序逐个执行(排序保证重启间执行顺序稳定)。
        /// </summary>
        /// <param name="db">SqlSugar 客户端。</param>
        /// <param name="assemblies">要扫描的程序集;缺省扫 Core 自身(框架种子落点),业务程序集(业务种子落点)显式传入。</param>
        public static async Task InitSeeds(this ISqlSugarClient db, params Assembly[] assemblies)
        {
            var scanAssemblies = assemblies.Length > 0
                ? assemblies
                : new[] { typeof(SeedSetup).Assembly };

            var seedTypes = scanAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.GetCustomAttribute<IncreSeedAttribute>(inherit: false) != null
                            && typeof(ISqlSugarSeed).IsAssignableFrom(t))
                .OrderBy(t => t.Name)
                .ToList();

            foreach (var seedType in seedTypes)
            {
                // 约定无参构造(见 ISqlSugarSeed remarks);违反约定在此抛异常,启动即暴露,不静默跳过
                var seed = (ISqlSugarSeed)Activator.CreateInstance(seedType)!;
                await seed.SeedAsync(db);
            }
        }
    }
}
