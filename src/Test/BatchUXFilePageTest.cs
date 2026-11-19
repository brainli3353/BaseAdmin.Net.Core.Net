// 批 UX 后端件 B1c:sysFile 分页清单端点(真实 PG,与 BatchUXBackendFilterTest 同源 DbScope 样板;沙箱不可达时诚实降级 Assert.Fail):
// 覆盖 ① 分页回传(Total/分页/创建时间倒序/输出字段面) ② FileName 模糊(包含,非前缀) ③ CreateTime 时间范围(宽窗/过去窗/单边)。
// 探针纪律:每条用例上传两条 txt 探针 → 查询断言 → DisposeAsync 物理删探针行(幂等兜底,失败也不漏)。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.File;
using AdminCore.Core.Services.File.Dto;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SqlSugar;

namespace AdminCore.Test;

public class BatchUXFilePageTest
{
    // 开发库连接串(与 BatchUXBackendFilterTest/BatchBUSysFileTest 同源;Timeout=10 防沙箱 PG 黑洞挂死)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // ① 分页回传:Total 准确 + 分页切片 + 创建时间倒序 + 输出字段面
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 分页回传_降序且分页切片()
    {
        await using var host = await FilePageHost.CreateAsync();
        if (!host.PgReachable) { Assert.Fail(Downgrade); }

        var marker = NewMarker();
        var a = await host.UploadAsync($"{marker}A.txt", $"content-a-{marker}");
        var b = await host.UploadAsync($"{marker}B.txt", $"content-b-{marker}");

        // PageSize=1:Total 为符合过滤的全量(2),每页恰 1 条,两页并集=两探针
        var page1 = await host.Service.PageAsync(new FilePageInput(marker, PageIndex: 1, PageSize: 1));
        var page2 = await host.Service.PageAsync(new FilePageInput(marker, PageIndex: 2, PageSize: 1));
        Assert.Equal(2, page1.Total);
        Assert.Equal(2, page2.Total);
        Assert.Single(page1.List);
        Assert.Single(page2.List);
        var ids = new[] { page1.List[0].Id, page2.List[0].Id };
        Assert.Contains(a.Id, ids);
        Assert.Contains(b.Id, ids);

        // 创建时间倒序:第 1 页不早于第 2 页(同毫秒相等容忍)
        Assert.True(page1.List[0].CreateTime >= page2.List[0].CreateTime, "应按创建时间倒序返回");

        // 输出字段面:清单行有 md5/名称/后缀/Url/创建时间(不泄露 FilePath/Provider 由 DTO 字段面在编译期保证)
        var rowA = page1.List.Concat(page2.List).Single(x => x.Id == a.Id);
        Assert.Equal(a.FileMd5, rowA.FileMd5);
        Assert.Equal(a.FileName, rowA.FileName);
        Assert.Equal(".txt", rowA.Suffix);
        Assert.False(string.IsNullOrWhiteSpace(rowA.Url));
        Assert.NotNull(rowA.CreateTime);
    }

    // ─────────────────────────────────────────────────────────────
    // ② FileName 模糊:包含命中(中段关键字同样命中,排除"前缀匹配"误判)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 文件名模糊_包含命中且非前缀也命中()
    {
        await using var host = await FilePageHost.CreateAsync();
        if (!host.PgReachable) { Assert.Fail(Downgrade); }

        var marker = NewMarker();
        var a = await host.UploadAsync($"{marker}Alpha.txt", $"content-a-{marker}");
        var b = await host.UploadAsync($"{marker}Beta.txt", $"content-b-{marker}");
        var other = await host.UploadAsync($"{NewMarker()}Gamma.txt", $"content-c-{NewMarker()}");

        // 全 marker:两探针命中,无关探针不命中
        var page = await host.Service.PageAsync(new FilePageInput(marker, PageIndex: 1, PageSize: 50));
        Assert.Equal(2, page.Total);
        Assert.Contains(page.List, x => x.Id == a.Id);
        Assert.Contains(page.List, x => x.Id == b.Id);
        Assert.DoesNotContain(page.List, x => x.Id == other.Id);

        // 中段关键字:证明确实是"包含"而非"前缀"(若实现退化为 StartsWith 此断言红)
        var middle = marker.Substring(4, 6);
        var pageMid = await host.Service.PageAsync(new FilePageInput(middle, PageIndex: 1, PageSize: 50));
        Assert.Contains(pageMid.List, x => x.Id == a.Id);
        Assert.Contains(pageMid.List, x => x.Id == b.Id);
    }

    // ─────────────────────────────────────────────────────────────
    // ③ CreateTime 时间范围:宽窗命中 / 过去窗落空 / 单边界可用
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 时间范围_宽窗命中过去窗落空()
    {
        await using var host = await FilePageHost.CreateAsync();
        if (!host.PgReachable) { Assert.Fail(Downgrade); }

        var marker = NewMarker();
        await host.UploadAsync($"{marker}A.txt", $"content-a-{marker}");
        await host.UploadAsync($"{marker}B.txt", $"content-b-{marker}");

        var now = DateTime.Now;
        // 宽窗:含探针
        var inside = await host.Service.PageAsync(new FilePageInput(marker, PageIndex: 1, PageSize: 50, StartDate: now.AddMinutes(-10), EndDate: now.AddMinutes(10)));
        Assert.Equal(2, inside.Total);
        // 过去窗:不含探针
        var past = await host.Service.PageAsync(new FilePageInput(marker, PageIndex: 1, PageSize: 50, StartDate: now.AddDays(-3), EndDate: now.AddDays(-1)));
        Assert.Equal(0, past.Total);
        // 单边界各自可用
        var startOnly = await host.Service.PageAsync(new FilePageInput(marker, PageIndex: 1, PageSize: 50, StartDate: now.AddMinutes(-10)));
        Assert.Equal(2, startOnly.Total);
        var endOnly = await host.Service.PageAsync(new FilePageInput(marker, PageIndex: 1, PageSize: 50, EndDate: now.AddMinutes(10)));
        Assert.Equal(2, endOnly.Total);
    }

    private static string NewMarker() => $"uxb1c{Guid.NewGuid():N}"[..16];

    // ─────────────────────────────────────────────────────────────
    // 装置:真实 PG + 临时 wwwroot(Local Provider) 的 SysFileService 宿主
    // ─────────────────────────────────────────────────────────────
    private sealed class FilePageHost : IAsyncDisposable
    {
        private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("ux_b1c_");
        private readonly List<string> _probeNames = new();
        private ServiceProvider _provider = null!;
        private IServiceScope _scope = null!;

        public bool PgReachable { get; private set; }

        public ISqlSugarClient Db { get; private set; } = null!;

        public SysFileService Service => _scope.ServiceProvider.GetRequiredService<SysFileService>();

        public static async Task<FilePageHost> CreateAsync()
        {
            var host = new FilePageHost();
            if (!await IsPostgresReachableAsync())
            {
                // 不可达:给空容器,用例自行 Assert.Fail 诚实降级
                host._provider = new ServiceCollection().BuildServiceProvider();
                return host;
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥(32 字节 Base64):AddSqlSugar 解析 CryptogramHelper 需要
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                    // 上传配置:Local 档 + 仅放行 text/plain/.txt(探针用)
                    ["Upload:EnableSaveDb"] = "false",
                    ["Upload:EnableMd5"] = "true",
                    ["Upload:MaxSize"] = "10240",
                    ["Upload:ContentType:0"] = "text/plain",
                    ["Upload:AllowSuffix:txt"] = "txt"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment(host._temp.FullName));
            services.AddSingleton<IFileContentStore, FileContentStore>();
            services.AddScoped<SysFileService>();

            host._provider = services.BuildServiceProvider();
            host._scope = host._provider.CreateScope();
            host.Db = host._provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(host.Db);
            host.PgReachable = true;
            return host;
        }

        /// <summary>走真实 UploadAsync 面造探针(Local Provider 落临时 wwwroot/uploads),登记文件名供清理。</summary>
        public async Task<SysFilePageOutput> UploadAsync(string fileName, string content)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var formFile = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
            {
                // FormFile 的 ContentType 读自 Headers;不设则 get_ContentType 空引用(上传管线白名单首步即读它)
                Headers = new HeaderDictionary { ["Content-Type"] = "text/plain" }
            };
            await Service.UploadAsync(formFile, null);
            _probeNames.Add(fileName);
            var page = await Service.PageAsync(new FilePageInput(fileName, PageIndex: 1, PageSize: 10));
            return page.List.Single(x => x.FileName == fileName);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (PgReachable && _probeNames.Count > 0)
                {
                    var repo = _scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysFile>>();
                    foreach (var name in _probeNames)
                    {
                        await repo.DeletePhysicalByAsync(f => f.FileName == name);
                    }
                }
            }
            finally
            {
                _scope?.Dispose();
                await _provider.DisposeAsync();
                if (Directory.Exists(_temp.FullName))
                {
                    _temp.Delete(recursive: true);
                }
            }
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchUXBackendFilterTest 口径)。</summary>
        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
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

    /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd;AOP 审计/租户填充读它)。</summary>
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>最小 IWebHostEnvironment:仅 ContentRootPath/WebRootPath 有值(DefaultFileProvider 读它)。</summary>
    private sealed class FakeWebHostEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
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