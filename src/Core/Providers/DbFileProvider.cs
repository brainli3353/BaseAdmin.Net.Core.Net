using AdminCore.Core.Interfaces;

namespace AdminCore.Core.Providers
{
    /// <summary>
    /// DB 文件存储 Provider(§4.5):内容进 SysFileContent 表(经 IFileContentStore 存取,Core 不直接引用 Business 实体)。
    /// filePath/suffix 对 DB 模式无物理意义(内容按 SysFileId 定位),实现中忽略——保留入参仅为统一接口签名。
    /// </summary>
    public class DbFileProvider : ICustomFileProvider
    {
        private readonly IFileContentStore _store;

        /// <param name="store">内容存取实现(Business 落地,用 ISqlSugarRepository&lt;SysFileContent&gt;)。</param>
        public DbFileProvider(IFileContentStore store)
        {
            _store = store;
        }

        /// <inheritdoc />
        public async Task UploadAsync(Stream stream, long id, string filePath, string suffix)
        {
            // 流整段读入内存再落库:文件受 MaxSize 上限约束(默认 10MB),不会撑爆内存
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            await _store.SaveAsync(id, buffer.ToArray());
        }

        /// <inheritdoc />
        public async Task<Stream?> GetStreamAsync(long id, string filePath, string suffix)
        {
            var content = await _store.GetAsync(id);
            return content is null ? null : new MemoryStream(content);
        }

        /// <inheritdoc />
        public async Task<string> GetBase64Async(long id, string filePath, string suffix)
        {
            var content = await _store.GetAsync(id) ?? Array.Empty<byte>();
            return Convert.ToBase64String(content);
        }

        /// <inheritdoc />
        public Task DeleteAsync(long id, string filePath, string suffix) => _store.DeleteAsync(id);
    }
}
