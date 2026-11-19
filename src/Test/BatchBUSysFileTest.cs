﻿﻿﻿using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Providers;
using AdminCore.Core.Repository;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Services.File;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using SqlSugar;

namespace AdminCore.Test;

/// <summary>
/// 批 BU「sysFile 文件管理菜单复审」测试:被测主体 =
/// ① Local 档写入面与静态服务面的路径契约(D1:运行期 wwwroot/uploads 文件须可被 wwwroot 静态面服务——
/// MapStaticAssets 只服务构建期清单,运行时文件靠 UseStaticFiles 中间件,见 Program.cs 批 BU D1 注释);
/// ② 管道守卫:wwwroot 运行时静态服务在位(静态扫描,防回归);
/// ③ DeleteAsync 幂等口径(D2:不存在/已软删静默成功,对齐注释承诺与参照 DeleteFile)。
/// </summary>
public class BatchBUSysFileTest
{
    // 开发库连接串(与 JobSchedulerTest 同源;Timeout=10 防沙箱 PG 黑洞挂死)。
    private const string DbConnectionString =
        TestPg.ConnectionString;

    /// <summary>PG 可达性探测(TCP 直连,不依赖 ORM;同 JobSchedulerTest 口径)。</summary>
    private static bool IsPostgresReachable()
    {
        try
        {
            var parts = DbConnectionString.Split(';');
            using var tcp = new System.Net.Sockets.TcpClient();
            var ar = tcp.BeginConnect(parts[0].Split('=')[1], int.Parse(parts[1].Split('=')[1]), null, null);
            return ar.AsyncWaitHandle.WaitOne(3000) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    // ── D1a:Local 写入面 ↔ wwwroot 静态服务面 路径契约(不依赖宿主/PG) ────────────────────────

    /// <summary>
    /// D1 防复发:DefaultFileProvider 写入的物理文件必须落在 <c>{ContentRootPath}/wwwroot/uploads</c> 下,
    /// 且以虚拟路径 <c>/uploads/{filePath}/{id}{suffix}</c> 对 wwwroot 的 PhysicalFileProvider 可见——
    /// 这是"任意 wwwroot 静态服务(中间件)能直链到运行期文件"的充要前提(部署实测:MapStaticAssets
    /// 只认构建期清单,该路径契约一旦漂移,Local 档直链静默 404)。
    /// </summary>
    [Fact]
    public async Task D1_Local写入面_wwwroot静态面按直链路径可见()
    {
        var temp = Directory.CreateTempSubdirectory("bu_d1a_");
        try
        {
            var env = new FakeWebHostEnvironment(temp.FullName);
            var provider = new DefaultFileProvider(env);
            var content = "bu_d1a_probe";
            await provider.UploadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), 12345, "2026/10/2", ".txt");

            // 写入面:物理文件确在 ContentRoot/wwwroot/uploads 下
            var phys = Path.Combine(temp.FullName, "wwwroot", "uploads", "2026", "10", "2", "12345.txt");
            Assert.True(File.Exists(phys), $"D1:物理文件应落 wwwroot/uploads: {phys}");

            // 服务面:wwwroot 的 FileProvider 按 Url 同款虚拟路径可见(= 直链 /uploads/... 可服务的充要条件)
            var webRoot = new PhysicalFileProvider(Path.Combine(temp.FullName, "wwwroot"));
            var info = webRoot.GetFileInfo("/uploads/2026/10/2/12345.txt");
            Assert.True(info.Exists, "D1:wwwroot FileProvider 必须能按 /uploads/{path}/{id}{suffix} 虚拟路径看到运行期文件");
            using var stream = info.CreateReadStream();
            using var reader = new StreamReader(stream);
            Assert.Equal(content, await reader.ReadToEndAsync());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // ── D1b:管道守卫——wwwroot 运行时静态服务在位(静态扫描,同批 AE 守卫先例) ────────────────

    /// <summary>
    /// D1 防复发(管道面):Program.cs 必须挂载**不带参的** <c>app.UseStaticFiles()</c>(默认 FileProvider=wwwroot)——
    /// MapStaticAssets 只服务构建期清单资产(.NET 9+ 官方口径),运行期上传文件只靠该中间件实时查盘;
    /// 若将来移除/改指他处,本守卫红。断言"不带参"以与 spaRoot 具名实例区分。
    /// </summary>
    [Fact]
    public void D1_管道守卫_Program挂载wwwroot运行时静态服务()
    {
        var program = LocateRepoRoot() is var root && root.Length > 0
            ? Path.Combine(root, "src", "Web.Entry", "Program.cs")
            : throw new FileNotFoundException("未定位到仓根");
        Assert.True(File.Exists(program), $"未找到 Program.cs: {program}");

        var text = File.ReadAllText(program);
        Assert.Matches(@"app\.UseStaticFiles\(\s*\)", text);
    }

    // ── D2:DeleteAsync 幂等口径(DI + 真实 PG) ────────────────────────────────────────────────

    /// <summary>
    /// D2 防复发:DeleteAsync 对「不存在」与「已软删」的 id 都静默成功(不抛)——
    /// 软删后 AOP 过滤使 GetFirstAsync 查不到,重复 delete/差集同步重跑是合法成功;
    /// 修复前行为=第二次 delete 抛 file.not_found(注释承诺幂等而行为失败,语义断裂)。
    /// </summary>
    [Fact]
    public async Task D2_删除幂等_不存在与已软删均静默成功()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过文件删除幂等用例(同 RepositoryTest 口径)");
        }

        var temp = Directory.CreateTempSubdirectory("bu_d2_");
        var vars = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = DbConnectionString };
        var config = new ConfigurationBuilder().AddInMemoryCollection(vars).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment(temp.FullName));
        services.AddSingleton<IFileContentStore, FileContentStore>();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSqlSugar(config);
        services.AddCache(config);
        services.AddScoped<SysFileService>();
        using var provider = services.BuildServiceProvider();

        var db = provider.GetRequiredService<ISqlSugarClient>();
        db.InitTables(typeof(SqlSugarSetup).Assembly);

        var marker = $"bu_d2_{Guid.NewGuid():N}";
        using (var scope = provider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysFile>>();
            var svc = scope.ServiceProvider.GetRequiredService<SysFileService>();

            // 探针行(Provider=Local 但无物理文件——内容清理幂等,不依赖文件存在)
            var entity = new SysFile
            {
                FileMd5 = new string('a', 32),
                SizeKb = 1,
                FileName = marker,
                FilePath = "2026/10/2",
                Suffix = ".txt",
                Provider = SysFileService.ProviderLocal,
                Url = "/uploads/2026/10/2/0.txt"
            };
            await repo.InsertAsync(entity);

            // 首删成功(软删)
            await svc.DeleteAsync(entity.Id);
            Assert.True(await repo.GetFirstAsync(f => f.Id == entity.Id) is null, "首删后 AOP 过滤应查不到(软删生效)");

            // D2 核心:再删不抛(修复前=抛 file.not_found)
            await svc.DeleteAsync(entity.Id);
            // 不存在 id 同样不抛
            await svc.DeleteAsync(long.MaxValue);

            // 探针清理(物理删软删行;残留断言走 ISqlSugarClient 直查——仓储接口不暴露 Context(CLAUDE.md 契约边界))
            await repo.DeletePhysicalByAsync(f => f.FileName == marker);
            var leftover = await provider.GetRequiredService<ISqlSugarClient>().Queryable<SysFile>()
                .ClearFilter<IDeletedFilter>().Where(f => f.FileName == marker).CountAsync();
            Assert.True(leftover == 0, "探针行物理清理残留");
        }
        temp.Delete(recursive: true);
    }

    /// <summary>
    /// 定位仓根:从测试输出目录向上找含 <c>src/Core</c> 的目录(同批 AE 守卫口径,找不到即红不静默)。
    /// </summary>
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

        Assert.Fail("未定位到仓根(自 AppContext.BaseDirectory 向上未见 src/Core)");
        return string.Empty;
    }

    /// <summary>最小 IWebHostEnvironment:仅 ContentRootPath 有值(DefaultFileProvider 只读它)。</summary>
    private sealed class FakeWebHostEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot; // .NET 10:可写
        public string EnvironmentName { get; set; } = "Production";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(contentRoot, "wwwroot");
        public IWebHostEnvironment UseContentRoot(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            return this;
        }

        public IWebHostEnvironment UseWebRoot(string? webRootPath)
        {
            WebRootPath = webRootPath ?? string.Empty;
            return this;
        }
    }

    private sealed class NullFileProvider : IFileProvider
    {
        public void Dispose() { }
        public IChangeToken Watch(string filter) => throw new NotSupportedException();
        public IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();
        public IFileInfo GetFileInfo(string subpath) => new NullFileInfo();
    }

    /// <summary>空 IFileInfo(Exists=false;.NET 10 无内置 NullFileInfo 公共件)。</summary>
    private sealed class NullFileInfo : IFileInfo
    {
        public bool Exists => false;
        public long Length => 0;
        public string PhysicalPath => string.Empty;
        public string Name => string.Empty;
        public DateTimeOffset LastModified => DateTimeOffset.MinValue;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => throw new NotSupportedException();
    }
}
