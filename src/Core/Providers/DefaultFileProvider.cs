using AdminCore.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;

namespace AdminCore.Core.Providers
{
    /// <summary>
    /// 本地文件存储 Provider(§4.5 默认档):内容落 wwwroot/uploads 物理文件,静态资源可直连 /uploads/...。
    /// 命名约定 <c>{FilePath}/{Id}{Suffix}</c>(如 2026/8/27/1234567890.jpg)。无状态(仅持有根路径),可作单例。
    /// </summary>
    public class DefaultFileProvider : ICustomFileProvider
    {
        private readonly string _root;

        /// <param name="env">宿主环境;取 ContentRootPath 拼 wwwroot/uploads 作为本地存储根。</param>
        public DefaultFileProvider(IWebHostEnvironment env)
        {
            // 本地存储根固定 wwwroot/uploads:既受 MapStaticAssets 静态服务托管,又与业务物理隔离
            _root = Path.Combine(env.ContentRootPath, "wwwroot", "uploads");
        }

        /// <summary>拼物理全路径:<c>root/{filePath}/{id}{suffix}</c>。</summary>
        private string FullPath(long id, string filePath, string suffix)
            => Path.Combine(_root, $"{filePath.Trim('/')}/{id}{suffix}");

        /// <inheritdoc />
        public async Task UploadAsync(Stream stream, long id, string filePath, string suffix)
        {
            var fullPath = FullPath(id, filePath, suffix);
            // 目录按需创建(日期目录首次上传才出现,不预建)
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var fileStream = File.Create(fullPath);
            await stream.CopyToAsync(fileStream);
        }

        /// <inheritdoc />
        public Task<Stream?> GetStreamAsync(long id, string filePath, string suffix)
        {
            var fullPath = FullPath(id, filePath, suffix);
            // 物理文件缺失 → null(调用方转业务"内容不存在"),不在此抛异常(见接口口径)
            return File.Exists(fullPath) ? Task.FromResult<Stream?>(File.OpenRead(fullPath)) : Task.FromResult<Stream?>(null);
        }

        /// <inheritdoc />
        public async Task<string> GetBase64Async(long id, string filePath, string suffix)
        {
            var fullPath = FullPath(id, filePath, suffix);
            if (!File.Exists(fullPath))
            {
                return string.Empty;
            }

            return Convert.ToBase64String(await File.ReadAllBytesAsync(fullPath));
        }

        /// <inheritdoc />
        public Task DeleteAsync(long id, string filePath, string suffix)
        {
            var fullPath = FullPath(id, filePath, suffix);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return Task.CompletedTask;
        }
    }
}
