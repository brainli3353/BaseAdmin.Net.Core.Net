namespace AdminCore.Core.Interfaces
{
    /// <summary>
    /// 文件内容存取抽象(DB 模式专用,§4.5):按 SysFileId 存取/删除二进制内容。
    /// WHY 独立抽象:DbFileProvider 落 Core 层,而内容实体 SysFileContent 落 Business 层(Core 不能反向引用 Business),
    /// 故以「契约在 Core、实现在 Business」解耦——与 IUserApiPermissionProvider/Web.Core 契约同模式,
    /// 避免为让 Core 直接引用实体而制造 Core↔Business 循环引用。
    /// </summary>
    public interface IFileContentStore
    {
        /// <summary>保存/更新某文件的内容(按 SysFileId 幂等 upsert)。</summary>
        Task SaveAsync(long sysFileId, byte[] content);

        /// <summary>读取某文件的内容;不存在返回 null。</summary>
        Task<byte[]?> GetAsync(long sysFileId);

        /// <summary>物理删除某文件的内容行(幂等)。</summary>
        Task DeleteAsync(long sysFileId);
    }
}
