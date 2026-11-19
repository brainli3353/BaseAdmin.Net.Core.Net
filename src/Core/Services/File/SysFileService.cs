using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using System.Security.Cryptography;
using AdminCore.Core.Entities;
using AdminCore.Core.Services.File.Dto;
using AdminCore.Core;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Models;
using AdminCore.Core.Providers;
using AdminCore.Core.Repository;
using AdminCore.Core.Utils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace AdminCore.Core.Services.File
{
    /// <summary>
    /// 文件管理服务（参考手册 §4.5 契约）：上传管线（类型白名单→大小限制→MD5 去重→后缀规范化→Provider 存储→落库）/
    /// 取流与取 Base64（按记录 Provider 路由读取，模式切换不破坏历史）/ 删除（记录软删+内容物理清理）/
    /// UpdateFileByDataId 业务关联差集同步（删多余+回填 DataId）。
    /// 写 Provider 由 Upload:EnableSaveDb 配置链式选择（仅 Local/Db 两档，OSS/SSH 裁剪）；静态配置切换需重启。
    /// </summary>
    [Service]
    public class SysFileService
    {
        /// <summary>Provider 常量：本地 wwwroot/uploads（DefaultFileProvider）。</summary>
        public const string ProviderLocal = "Local";
        /// <summary>Provider 常量：sys_file_content 表（DbFileProvider）。</summary>
        public const string ProviderDb = "Db";

        /// <summary>规范化后缀 → 下载响应 Content-Type（未知后缀回落 octet-stream）。</summary>
        private static readonly Dictionary<string, string> SuffixToContentType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg"] = "image/jpeg",
            ["png"] = "image/png",
            ["gif"] = "image/gif",
            ["webp"] = "image/webp",
            ["pdf"] = "application/pdf",
            ["txt"] = "text/plain"
        };

        private readonly ISqlSugarRepository<SysFile> _fileRepo;
        private readonly ICustomFileProvider _localProvider;
        private readonly ICustomFileProvider _dbProvider;

        // Upload 段配置（静态：切换 EnableSaveDb 需重启；历史数据读取按记录 Provider 字段路由，切换不破坏历史）
        private readonly bool _enableSaveDb;
        private readonly bool _enableMd5;
        private readonly long _maxSizeKb;
        private readonly List<string> _contentTypeWhitelist;
        private readonly Dictionary<string, string> _allowSuffix;

        public SysFileService(
            ISqlSugarRepository<SysFile> fileRepo,
            IWebHostEnvironment env,
            IConfiguration config,
            IFileContentStore contentStore)
        {
            _fileRepo = fileRepo;
            // 两个 Provider 都要持有：写走当前配置选中的那个；读必须按记录 Provider 路由
            //（DB 模式下 Provider=="Local" 的历史数据要回退本地读——模式切换不破坏历史）
            _localProvider = new DefaultFileProvider(env);
            _dbProvider = new DbFileProvider(contentStore);

            // 批 AW F3:键名走 UploadStorageGuard 常量(启动审计件读同一常量 ⇒ 档位判定与写档位选择不会漂移)
            _enableSaveDb = config.GetValue(UploadStorageGuard.EnableSaveDbConfigKey, false);
            _enableMd5 = config.GetValue("Upload:EnableMd5", true);
            _maxSizeKb = config.GetValue("Upload:MaxSize", 10240L);
            _contentTypeWhitelist = config.GetSection("Upload:ContentType").GetChildren()
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim().ToLowerInvariant())
                .ToList();
            // 允许后缀表：原后缀(小写) → 规范后缀；表外后缀一律拒绝（白名单而非黑名单）
            _allowSuffix = config.GetSection("Upload:AllowSuffix").GetChildren()
                .Where(c => !string.IsNullOrWhiteSpace(c.Value))
                .ToDictionary(c => c.Key.Trim().ToLowerInvariant(), c => c.Value!.Trim().ToLowerInvariant());
        }

        /// <summary>当前写 Provider（配置链式选择：EnableSaveDb→Db，否则 Local；仅两档）。</summary>
        private ICustomFileProvider ActiveProvider => _enableSaveDb ? _dbProvider : _localProvider;

        /// <summary>
        /// 上传管线（契约顺序）：类型白名单 → 大小限制 → 后缀规范化 → MD5 计算 → EnableMd5 去重复用 → 落库+Provider 存储。
        /// 校验失败一律 BadRequestException（HTTP 400，验收标准明文要求）；去重命中返回已有记录（不产生新行）。
        /// </summary>
        public async Task<SysFileOutput> UploadAsync(IFormFile file, long? dataId)
        {
            if (file is null || file.Length <= 0)
            {
                throw BadRequestException.Of(ErrorCodes.FileContentRequired);
            }

            // ① 类型白名单（Upload:ContentType，客户端声明的 MIME，忽略大小写精确匹配）
            var contentType = (file.ContentType ?? string.Empty).Trim().ToLowerInvariant();
            if (!_contentTypeWhitelist.Contains(contentType))
            {
                throw BadRequestException.With(ErrorCodes.FileTypeNotAllowed, "contentType", file.ContentType);
            }

            // ② 大小限制（Upload:MaxSize 单位 KB）
            if (file.Length > _maxSizeKb * 1024L)
            {
                throw BadRequestException.With(ErrorCodes.FileSizeExceeded, "maxKb", _maxSizeKb);
            }

            // ③ 后缀规范化（AllowSuffix 表：原后缀→规范后缀，如 jpeg→jpg；无后缀或表外 → 400）
            var ext = Path.GetExtension(file.FileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0 || !_allowSuffix.TryGetValue(ext, out var canonicalSuffix))
            {
                throw BadRequestException.With(ErrorCodes.FileSuffixNotAllowed, "fileName", file.FileName);
            }

            // ④ 全量读入内存后算 MD5（内容已被 MaxSize 约束，内存可接受）
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            var sizeKb = (file.Length + 1023) / 1024;   // KB 向上取整

            // ⑤ MD5 去重（EnableMd5 开关）：同 MD5+大小复用已有记录（不产生新行）；传了 dataId 且不同则回填关联
            if (_enableMd5)
            {
                var existing = await _fileRepo.GetFirstAsync(f => f.FileMd5 == md5 && f.SizeKb == sizeKb);
                if (existing is not null)
                {
                    if (dataId is not null && existing.DataId != dataId)
                    {
                        existing.DataId = dataId;
                        await _fileRepo.UpdateAsync(existing);
                    }
                    return ToOutput(existing);
                }
            }

            // ⑥ 预生成雪花主键再落库：存储命名与 Url 都依赖 Id，预生成可一次插入成稿（AOP 主键非空保留）
            var filePath = DateTime.Now.ToString("yyyy/M/d");
            var suffix = "." + canonicalSuffix;
            var entity = new SysFile
            {
                FileMd5 = md5,
                SizeKb = sizeKb,
                FileName = file.FileName,
                FilePath = filePath,
                Suffix = suffix,
                Provider = _enableSaveDb ? ProviderDb : ProviderLocal,
                DataId = dataId
            };
            entity.Id = YitIdHelper.NextId();
            // 访问 URL 按模式分流：Local=静态直链（MapStaticAssets 托管）；Db=下载 API（带 md5 防篡改）
            entity.Url = _enableSaveDb
                ? $"/api/sysFile/download?id={entity.Id}&md5={md5}"
                : $"/uploads/{filePath}/{entity.Id}{suffix}";
            await _fileRepo.InsertAsync(entity);

            // ⑦ Provider 存储（落库在前：存储命名 {路径段}/{Id}{后缀} 依赖主键）
            await using var contentStream = new MemoryStream(bytes);
            await ActiveProvider.UploadAsync(contentStream, entity.Id, filePath, suffix);

            return ToOutput(entity);
        }

        /// <summary>
        /// 分页清单（批 UX B1c，补 G38"sysFile 无 page 端点→页面刷新即丢"）：
        /// FileName 模糊（包含，可选）+ CreateTime 时间范围（可选，与日志域同口径），按创建时间倒序；
        /// 输出走 SysFilePageOutput（对齐 download 出口，不暴露存储路径段/Provider 内部档名）。
        /// </summary>
        public async Task<PageResult<SysFilePageOutput>> PageAsync(FilePageInput input)
        {
            // 先落局部变量再进表达式，避免 WhereIF 内对可空引用解引用（零警告口径，同 SysLogExService）
            var fileName = input.FileName?.Trim();
            RefAsync<int> total = 0;
            var list = await _fileRepo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(fileName), f => f.FileName!.Contains(fileName!))
                .WhereIF(input.StartDate.HasValue, f => f.CreateTime >= input.StartDate)
                .WhereIF(input.EndDate.HasValue, f => f.CreateTime <= input.EndDate)
                .OrderByDescending(f => f.CreateTime)
                .ToPageListAsync(input, total);

            return new PageResult<SysFilePageOutput>
            {
                Total = total,
                List = list.Select(f => new SysFilePageOutput(
                    f.Id, f.FileMd5 ?? string.Empty, f.SizeKb, f.FileName, f.Suffix, f.DataId, f.Url, f.CreateTime)).ToList()
            };
        }

        /// <summary>
        /// 下载（契约）：查记录 → md5 校验（请求 md5 与记录 FileMd5 不符/缺失 → 400 防篡改）→ 按记录 Provider 路由读内容。
        /// </summary>
        public async Task<DownloadFileOutput> DownloadAsync(long id, string? md5)
        {
            var file = await _fileRepo.GetFirstAsync(f => f.Id == id);
            if (file is null)
            {
                throw BadRequestException.Of(ErrorCodes.FileNotFound);
            }

            // md5 校验防篡改（契约）：URL 可被传播/截获，md5 不符即拒绝（文件不存在与 md5 错同口径 400）
            if (string.IsNullOrWhiteSpace(md5) || !string.Equals(md5.Trim(), file.FileMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw BadRequestException.Of(ErrorCodes.FileMd5Mismatch);
            }

            var stream = await ResolveProvider(file).GetStreamAsync(file.Id, file.FilePath ?? string.Empty, file.Suffix ?? string.Empty);
            if (stream is null)
            {
                throw BadRequestException.Of(ErrorCodes.FileContentMissing);
            }

            await using (stream)
            {
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return new DownloadFileOutput(ms.ToArray(), ContentTypeOf(file.Suffix), file.FileName);
            }
        }

        /// <summary>取文件内容流（按记录 Provider 路由读取；记录或内容缺失返回 null，调用方自行处理）。</summary>
        public async Task<Stream?> GetStreamAsync(long id)
        {
            var file = await _fileRepo.GetFirstAsync(f => f.Id == id);
            if (file is null)
            {
                return null;
            }
            return await ResolveProvider(file).GetStreamAsync(file.Id, file.FilePath ?? string.Empty, file.Suffix ?? string.Empty);
        }

        /// <summary>取文件内容 Base64（路由口径同上；记录或内容缺失返回空串）。</summary>
        public async Task<string> GetBase64Async(long id)
        {
            var file = await _fileRepo.GetFirstAsync(f => f.Id == id);
            if (file is null)
            {
                return string.Empty;
            }
            return await ResolveProvider(file).GetBase64Async(file.Id, file.FilePath ?? string.Empty, file.Suffix ?? string.Empty);
        }

        /// <summary>
        /// 文件删除：记录软删（保留审计轨迹）+ 内容物理清理（按记录 Provider 路由）——
        /// 内容必须同步清，否则 Local 物理文件 / Db 内容行成为孤儿残留。
        /// 幂等口径（批 BU D2，对齐注释承诺与参照 DeleteFile）：记录不存在/已软删静默返回，不抛——
        /// 软删后 AOP 过滤使 GetFirstAsync 查不到，重复 delete/差集同步重跑都是合法成功。
        /// </summary>
        public async Task DeleteAsync(long id)
        {
            var file = await _fileRepo.GetFirstAsync(f => f.Id == id);
            if (file is null)
            {
                return; // 幂等：已删/不存在静默成功（参照同口径）
            }

            await _fileRepo.DeleteByIdAsync(id);
            // 内容清理幂等：内容已不在（历史清理过/存储失败）时不报错
            await ResolveProvider(file).DeleteAsync(file.Id, file.FilePath ?? string.Empty, file.Suffix ?? string.Empty);
        }

        /// <summary>
        /// 业务关联差集同步（契约 UpdateFileByDataId）：fileIds 为业务方持有的该业务权威文件全集——
        /// 回填：集合内文件的 DataId 置为 dataId（含新文件，集合内文件必须存在，否则报 400 防悬空引用）；
        /// 删多余：原属 dataId 但不在集合内的文件整删（软删+内容清理，同 DeleteAsync 语义）。
        /// 注意：DataId 是单值通用外键（非多对多），调用方必须传权威全集，文件被多业务共用的场景需业务层自行规避。
        /// </summary>
        public async Task UpdateFileByDataIdAsync(long dataId, List<long>? fileIds)
        {
            var ids = fileIds is { Count: > 0 } ? fileIds.Where(x => x > 0).Distinct().ToList() : new List<long>();

            // 回填：先取集合内已存在的文件（顺带校验防悬空），DataId 不同的才写（避免无意义更新）
            if (ids.Count > 0)
            {
                var existing = await _fileRepo.AsQueryable().Where(f => ids.Contains(f.Id)).ToListAsync();
                var missing = ids.Where(fid => existing.All(f => f.Id != fid)).ToList();
                if (missing.Count > 0)
                {
                    throw BadRequestException.With(ErrorCodes.FileNotFoundIds, "ids", string.Join(",", missing));
                }

                foreach (var file in existing)
                {
                    if (file.DataId != dataId)
                    {
                        file.DataId = dataId;
                        await _fileRepo.UpdateAsync(file);
                    }
                }
            }

            // 删多余：原属 dataId 且不在权威集合内的，逐个删（软删+内容清理）
            var surplus = await _fileRepo.AsQueryable().Where(f => f.DataId == dataId && !ids.Contains(f.Id)).ToListAsync();
            foreach (var file in surplus)
            {
                await DeleteAsync(file.Id);
            }
        }

        /// <summary>
        /// 读取路由（契约）：按记录 Provider 字段而非当前配置——DB 模式下 Provider=="Local" 的历史数据回退本地 Provider
        /// （模式切换不破坏历史）；值缺失/非 Db 一律按 Local 处理（早期数据可能无该字段值）。
        /// </summary>
        private ICustomFileProvider ResolveProvider(SysFile file)
            => string.Equals(file.Provider, ProviderDb, StringComparison.OrdinalIgnoreCase) ? _dbProvider : _localProvider;

        /// <summary>后缀 → 响应 Content-Type（规范化后缀；未知回落 octet-stream，浏览器按下载处理）。</summary>
        private static string ContentTypeOf(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return "application/octet-stream";
            }
            return SuffixToContentType.TryGetValue(suffix.TrimStart('.').ToLowerInvariant(), out var contentType)
                ? contentType
                : "application/octet-stream";
        }

        private static SysFileOutput ToOutput(SysFile f)
            => new(f.Id, f.FileMd5 ?? string.Empty, f.SizeKb, f.FileName, f.FilePath, f.Suffix, f.Provider, f.DataId, f.Url);
    }
}
