namespace AdminCore.Core.Interfaces
{
    /// <summary>
    /// 文件存储抽象(框架标配,§4.5):上传/取流/取 Base64/删除,四种存储实现(本地/DB/OSS/SSH)同接口,
    /// 业务代码只依赖本接口,由服务侧按配置选实现(业务无感)。
    /// 本任务仅落地本地 + DB 两档(OSS/SSH 裁剪,按需再加)。
    /// </summary>
    /// <remarks>
    /// 口径约定:
    /// - <c>id</c> 为文件主键(雪花 long);<c>filePath</c> 为存储路径段(本地=日期目录,如 "2026/8/27";DB 模式无物理路径,可传空);
    /// - <c>suffix</c> 为规范化后缀(含前导点,如 ".jpg");
    /// - 取流/取 Base64 在内容缺失时返回 null/空串(不抛异常),由调用方决定业务响应——
    ///   WHY 不在此层抛业务异常:Core 不引用 Web.Core,无法使用 AppException,保持 Core 零业务异常依赖。
    /// </remarks>
    public interface ICustomFileProvider
    {
        /// <summary>上传:把流内容写入存储(本地落盘 / DB 内容表)。流由调用方负责释放。</summary>
        Task UploadAsync(Stream stream, long id, string filePath, string suffix);

        /// <summary>取流:读回文件内容内存流。内容不存在时返回 null。</summary>
        Task<Stream?> GetStreamAsync(long id, string filePath, string suffix);

        /// <summary>取 Base64:读回文件内容并 Base64 编码。内容不存在时返回空串。</summary>
        Task<string> GetBase64Async(long id, string filePath, string suffix);

        /// <summary>删除存储内容(本地删物理文件 / DB 删内容行)。幂等:内容不存在也不报错。</summary>
        Task DeleteAsync(long id, string filePath, string suffix);
    }
}
