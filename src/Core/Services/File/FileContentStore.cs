using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Repository;

namespace AdminCore.Core.Services.File
{
    /// <summary>
    /// 文件内容存取实现（IFileContentStore，接口在 Core、实现在 Business——见接口头注释的 WHY）：
    /// 按 SysFileId 对 sys_file_content 表做内容存取/物理清理，DbFileProvider 上传与 DB 模式读取都走这里。
    /// </summary>
    [Service]
    public class FileContentStore : IFileContentStore
    {
        private readonly ISqlSugarRepository<SysFileContent> _repo;

        public FileContentStore(ISqlSugarRepository<SysFileContent> repo)
        {
            _repo = repo;
        }

        /// <inheritdoc />
        public async Task SaveAsync(long sysFileId, byte[] content)
        {
            // 幂等 upsert：同一 SysFileId 重存不产生重复内容行（去重复用场景下文件行与内容行严格一对一）
            var existing = await _repo.GetFirstAsync(c => c.SysFileId == sysFileId);
            if (existing is null)
            {
                await _repo.InsertAsync(new SysFileContent { SysFileId = sysFileId, Content = content });
            }
            else
            {
                existing.Content = content;
                await _repo.UpdateAsync(existing);
            }
        }

        /// <inheritdoc />
        public async Task<byte[]?> GetAsync(long sysFileId)
            => (await _repo.GetFirstAsync(c => c.SysFileId == sysFileId))?.Content;

        /// <inheritdoc />
        public Task DeleteAsync(long sysFileId)
            // 物理删（内容行无软删价值）：无匹配行时受影响 0 行，天然幂等
            => _repo.DeletePhysicalByAsync(c => c.SysFileId == sysFileId);
    }
}
