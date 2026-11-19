using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using ICacheService = AdminCore.Core.Cache.ICacheService;
using AdminCore.Core.Entities;
using AdminCore.Core.Job;
using AdminCore.Core.SeedData;
using AdminCore.Core.Services.Server;
using AdminCore.Core.Utils;
using AdminCore.Core.Utils.ServerMonitor;
using AdminCore.Web.Entry.Controllers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AT「服务器监控多节点化」验证件:被测主体 =
/// ① <see cref="ServerInfoCollector.IsReportableFileSystem"/>(F2 磁盘裁剪纯函数);
/// ② <see cref="ServerMetricsReporter"/> → <see cref="SysServerNodeService"/> 的自报-聚合闭环(F3/F4);
/// ③ 读侧过期隐藏与"无身份载荷"跳过(F4 判据的否定面);
/// ④ 身份单点回归:调度器与监控快照同一 InstanceId(F1);
/// ⑤ 端点权限键 ↔ 菜单种子一一对齐(F6,约定 #14 的"一处配置两端生效")。
/// </summary>
/// <remarks>
/// WHY 用 Memory 档即可:本批要证的是**投影/过滤/对号语义**(纯进程内成立),不是跨实例可见性——
/// 跨实例属 Redis 档行为且已在指南 §46.2/G18 写明边界,真机双实例 e2e 另证(见差距清单历史行)。
/// 零污染:全程用新建的 <see cref="MemoryCacheService"/> 实例(键空间随实例消亡),不碰真实 Redis/PG。
/// </remarks>
public class BatchATServerNodesTest
{
    // ── ① F2:伪文件系统一律不上报;真盘(含容器 overlay 根盘)保留 ──────────────────────────
    [Fact]
    public void F2_磁盘类型裁剪_伪文件系统不报真盘保留()
    {
        // 真盘/容器根盘必须留(滤掉 overlay 等于没看这台机器)
        foreach (var real in new[] { "ext4", "ext3", "ext2", "xfs", "btrfs", "zfs", "ntfs", "exfat", "vfat", "overlay" })
        {
            Assert.True(ServerInfoCollector.IsReportableFileSystem(real), $"真盘类型 {real} 不应被滤掉");
        }

        // 容器内 70+ 挂载点的主力:无容量语义或非持久盘
        foreach (var pseudo in new[] { "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "pstore", "securityfs", "debugfs", "tracefs", "configfs", "fusectl", "mqueue", "shm", "binfmt_misc", "autofs", "squashfs", "efivarfs", "nsfs", "rpc_pipefs" })
        {
            Assert.False(ServerInfoCollector.IsReportableFileSystem(pseudo), $"伪文件系统 {pseudo} 不应进快照");
        }

        // 大小写与空白容错(DriveInfo 在不同内核上可能回 EXT4/带尾空格)
        Assert.True(ServerInfoCollector.IsReportableFileSystem("EXT4"));
        Assert.True(ServerInfoCollector.IsReportableFileSystem(" overlay "));
        Assert.False(ServerInfoCollector.IsReportableFileSystem("TMPFS"));

        // 取不到类型 ⇒ 保守保留(真盘偶发权限抖动不该把盘抹掉)
        Assert.True(ServerInfoCollector.IsReportableFileSystem(null));
        Assert.True(ServerInfoCollector.IsReportableFileSystem(""));
    }

    // ── ② F3→F4:自报一轮后本实例进入节点表,字段投影正确且 isSelf=true ────────────────────
    [Fact]
    public async Task F3F4_自报一轮后进节点表_本实例标记与投影字段正确()
    {
        var cache = NewCache();
        var reporter = NewReporter(cache);

        Assert.True(await reporter.ReportOnceAsync(), "上报轮应返回成功(Memory 档可用)");

        var nodes = await new SysServerNodeService(EmptyProvider(), cache).GetNodesAsync();

        var self = Assert.Single(nodes);
        Assert.Equal(InstanceIdentity.Current.InstanceId, self.InstanceId);
        Assert.True(self.IsSelf);
        Assert.Equal(Environment.ProcessId, self.Pid);                      // 载荷来自本进程采集
        Assert.Equal(InstanceIdentity.Current.NodeTag, self.NodeTag);
        Assert.NotEmpty(self.HostName);
        Assert.NotNull(self.Runtime);                                        // 三块采集原文都要随行走(详情区不再派生)
        Assert.NotNull(self.Hardware);
        Assert.True(self.Assemblies.Any(a => a.Name == "AdminCore.Core"), "程序集清单应含 AdminCore.Core");
        Assert.Equal(self.Assemblies.First(a => a.Name == "AdminCore.Core").Version, self.CoreVersion); // 版本串由该家族条目派生
        Assert.True(self.ReportAt >= DateTime.Now.AddSeconds(-30), "reportAt 应为刚才时刻");
        Assert.True(self.ProcessorCount > 0);
        // 载荷的 field 名必须等于 InstanceId(读侧按它去重/对号的物理前提)
        var raw = await cache.HashGetAllAsync<ServerMetricsSnapshot>(CacheConst.KeyServerMetrics);
        Assert.Contains(raw, s => s.InstanceId == self.InstanceId);
    }

    // ── ③ F4 否定面:陈旧快照与"无身份载荷"都不出现在视图里 ─────────────────────────────────
    [Fact]
    public async Task F4_陈旧快照与无身份载荷_读侧隐藏()
    {
        var cache = NewCache();
        var identity = InstanceIdentity.Current;

        // 一条新鲜(本实例)＋一条超期(>90s)＋一条无身份
        await cache.HashSetAsync(CacheConst.KeyServerMetrics, identity.InstanceId, Fresh(identity, TimeSpan.Zero));
        await cache.HashSetAsync(CacheConst.KeyServerMetrics, "ghost:1:dead", Fresh(new InstanceIdentity("ghost", "ghost:1:dead", 1, DateTime.Now), TimeSpan.FromMinutes(10)));
        await cache.HashSetAsync(CacheConst.KeyServerMetrics, "weird", new ServerMetricsSnapshot { InstanceId = string.Empty, NodeTag = "weird", ReportAt = DateTime.Now });

        var nodes = await new SysServerNodeService(EmptyProvider(), cache).GetNodesAsync();

        var only = Assert.Single(nodes);                      // 死节点与无名载荷都被挡掉
        Assert.Equal(identity.InstanceId, only.InstanceId);
        Assert.DoesNotContain(nodes, n => n.NodeTag == "ghost");
        Assert.DoesNotContain(nodes, n => n.NodeTag == "weird");

        // 边界:阈值两侧各测一次(89s 存活 / 91s 隐藏)——防"用 > 还是 >="写错无人知
        await cache.HashSetAsync(CacheConst.KeyServerMetrics, "edge:2:b", Fresh(new InstanceIdentity("edge", "edge:2:b", 2, DateTime.Now), TimeSpan.FromSeconds(89)));
        var with89 = await new SysServerNodeService(EmptyProvider(), cache).GetNodesAsync();
        Assert.Contains(with89, n => n.NodeTag == "edge");

        await cache.HashSetAsync(CacheConst.KeyServerMetrics, "edge:2:b", Fresh(new InstanceIdentity("edge", "edge:2:b", 2, DateTime.Now), TimeSpan.FromSeconds(91)));
        var with91 = await new SysServerNodeService(EmptyProvider(), cache).GetNodesAsync();
        Assert.DoesNotContain(with91, n => n.NodeTag == "edge");
    }

    // ── ④ F1:身份同源(调度器 InstanceId == 单点 InstanceId),且格式符合 {NodeTag}:{Pid}:{GUID} ──
    [Fact]
    public void F1_身份单点_调度器与监控快照同源()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var scheduler = new JobScheduler(provider, NewCache(), NullLogger<JobScheduler>.Instance,
            new[] { typeof(JobScheduler).Assembly }, 5000);

        // 同源的直接判据:两视图用同一个 field,运维才能把"调度节点表"与"监控节点表"对上号
        Assert.Equal(InstanceIdentity.Current.InstanceId, scheduler.InstanceId);

        // 格式钉住(批 AJ 口径不变):NodeTag:Pid:GUID(N)
        var identity = InstanceIdentity.Current;
        Assert.Equal($"{identity.NodeTag}:{identity.Pid}:{identity.InstanceId.Split(':')[2]}", identity.InstanceId);
        Assert.Equal(Environment.ProcessId, identity.Pid);
        Assert.False(string.IsNullOrWhiteSpace(identity.NodeTag));

        // 幂等:第二次 Resolve(即便带别的配置)不改变身份——单点只认第一次
        Assert.Same(identity, InstanceIdentity.Resolve(null));
    }

    // ── ⑤ F6:控制器权限键与菜单种子一一对齐(含 Id 唯一性防未来批次撞号) ───────────────────
    [Fact]
    public void F6_端点权限键与菜单种子一一对齐()
    {
        var keys = typeof(SysServerController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (Perm: m.GetCustomAttribute<MenuPermAttribute>()?.Permission,
                          Get: m.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpGetAttribute>()?.Template))
            .Where(x => x.Perm is not null)
            .ToList();

        // 四把键齐(三即时 + 一集群聚合),且 nodes 端点是 GET
        Assert.Contains("sysServer/nodes", keys.Select(k => k.Perm));
        Assert.Equal(4, keys.Count);
        Assert.Contains(keys, k => k.Perm == "sysServer/nodes" && k.Get == "nodes");

        var seed = new SysMenuSeedData().HasData().ToList();

        // 10178 行存在且挂对位置(Pid=10144 服务器监控、Type=3 按钮、Permission 与键同值)
        var row = Assert.Single(seed, m => m.Id == 10178L);   // 过滤走断言重载(xUnit2031:不在外面 Where)
        Assert.Equal(10144L, row.Pid);   // SysMenu.Pid 是 long
        Assert.Equal(3, row.Type);
        Assert.Equal("sysServer/nodes", row.Permission);
        Assert.Equal("sysServer/nodes", row.Name);

        // 本控制器全部权限键都要有对应种子行(#14 的"一处配置两端生效"闭环)
        var perms = seed.Where(m => m.Permission != null).Select(m => m.Permission!).ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            Assert.True(perms.Contains(key.Perm!), $"控制器权限键 {key.Perm} 无对应菜单种子行(约定 #14 一处配置两端生效)");
        }

        // 种子 Id 全局唯一(未来批次撞号在这里就红,不等 upsert 互相覆盖)
        Assert.Equal(seed.Count, seed.Select(m => m.Id).Distinct().Count());
    }

    // ── 装置 ────────────────────────────────────────────────────────────────────────────────

    private static ICacheService NewCache() => new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()));

    /// <summary>空容器(被测服务按可选配置解析身份;无 IConfiguration ⇒ 回落机器名标签,与载荷同标签)。</summary>
    private static IServiceProvider EmptyProvider() => new ServiceCollection().BuildServiceProvider();

    /// <summary>造一份本进程真实采集的载荷,只把 reportAt 往回拨 <paramref name="age"/>(判据要的是"多旧",不是内容)。</summary>
    private static ServerMetricsSnapshot Fresh(InstanceIdentity identity, TimeSpan age) => new()
    {
        InstanceId = identity.InstanceId,
        NodeTag = identity.NodeTag,
        ReportAt = DateTime.Now - age,
        Runtime = ServerInfoCollector.GetRuntimeInfo(),
        Hardware = ServerInfoCollector.GetHardwareInfo(),
        Assemblies = ServerInfoCollector.GetFrameworkAssemblies()
    };

    /// <summary>被测上报器(构造签名要 IServiceProvider 以可选解析 IConfiguration;单测给空容器 → 走缺省机器名)。</summary>
    private static ServerMetricsReporter NewReporter(ICacheService cache) =>
        new(new ServiceCollection().BuildServiceProvider(), cache, NullLogger<ServerMetricsReporter>.Instance);
}
