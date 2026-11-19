# task_id: task_012_ha_file
# subject: task_012_ha_file
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P0
# dep_on: task_008_ha_rbac
# files: src/Core/Interfaces/ICustomFileProvider.cs, src/Core/Providers/, src/Business/Entitys/SysFile.cs, src/Business/Entitys/SysFileContent.cs, src/Business/Services/File/, src/Web.Entry/Controllers/SysFileController.cs, src/Web.Entry/appsettings.json

# 任务：文件上传模块（存储抽象：本地 + DB 两 Provider + MD5 去重 + DataId 关联）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md §4.5（文件管理：结构树/代码参考/替换点）+ docs/baseadmin设计精华参考.md 第八节（目录约定）。
**代码纪律**：关键位置必须补中文注释；实体 [IncreTable]；禁止修改框架代码。
**投递前提**：008 已 accepted；与 010/011 无 files 冲突（appsettings 仅加 Upload 配置段，与 010 的 Configuration 目录不重叠）。

## 背景

框架标配文件能力（intel 养号截图证据、头像、导入导出文件都依赖）。**只做本地 + DB 两 Provider**（OSS/SSH 裁剪，按需再加）——按参考手册 §4.5 实现。

## 契约（按此实现）

1. **存储抽象**（Core/Interfaces/ICustomFileProvider.cs + Core/Providers/）：
   - `ICustomFileProvider`：UploadAsync(流→存储)/GetStreamAsync(Id→流)/GetBase64Async/DeleteAsync
   - `DefaultFileProvider`（本地 wwwroot/uploads，命名 `{FilePath}/{Id}{Suffix}`）、`DbFileProvider`（内容进 SysFileContent 表）
   - 构造按配置链式选择：`Upload:EnableSaveDb` → DbFileProvider，否则 DefaultFileProvider（仅两档）

2. **实体**（Business/Entitys/，继承 EntityBase + [IncreTable]）：
   - `SysFile`：FileMd5、SizeKb、FileName、FilePath、Suffix、Provider（存哪了）、DataId（业务关联通用外键）、Url（DB 模式 = /api/sysFile/download?id=..&md5=..）
   - `SysFileContent`：[LogTable] 不需要——普通表，SysFileId 外键 + byte[] Content

3. **服务**（Business/Services/File/SysFileService.cs + Dto/）：
   - 上传管线：类型白名单（Upload:ContentType）→ 大小上限（Upload:MaxSize）→ 算 MD5 → EnableMd5 时同 MD5+大小复用已存记录（去重）→ 后缀规范化（jpeg→jpg）→ Provider 存储 → 落库
   - `GetStreamAsync/GetBase64Async`（DB 模式旧数据 Provider=="Local" 回退 DefaultFileProvider——模式切换不破坏历史）
   - `UpdateFileByDataId(dataId, files)`：业务关联差集同步（删多余+回填 DataId）
   - 下载接口：`GET /api/sysFile/download?id=&md5=`（md5 校验防篡改）

4. **控制器**（Web.Entry/Controllers/SysFileController.cs）：POST /api/sysFile/upload、GET /api/sysFile/download、POST /api/sysFile/delete

5. **配置**（appsettings.json 加 `Upload` 段）：EnableSaveDb、MaxSize、ContentType、EnableMd5、AllowSuffix——注释"仅开发用，生产环境变量注入"

## 交付物

- `/data/worktrees/admin/task_012_ha_file/src/Core/Interfaces/ICustomFileProvider.cs`（Providers/ 两实现同批）
- `/data/worktrees/admin/task_012_ha_file/src/Business/Entitys/SysFile.cs`（SysFileContent.cs 同批）
- `/data/worktrees/admin/task_012_ha_file/src/Business/Services/File/SysFileService.cs`（Dto/ + 控制器同批）

## 验收标准

- `cd /data/worktrees/admin/task_012_ha_file && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- 接口实测（curl，验收报告附输出）：
  - POST /api/sysFile/upload（multipart 小文件）→ 返回文件记录（含 md5/url）
  - 同文件再传 → EnableMd5 时复用同记录（无新增行，psql 验证）
  - GET /api/sysFile/download?id=&md5= → 字节一致；md5 错 → 400
  - UpdateFileByDataId：业务 DataId 关联后差集同步正确（psql 验证）
  - 切换 `Upload:EnableSaveDb` → 重启 → 上传走 DB 模式（sys_file_content 有行）；旧 Local 数据仍可下载（回退实测）
- 类型/大小校验：非白名单类型 → 400；超 MaxSize → 400
- git commit（提交号写入验收报告），验收报告 acceptance_task_012_ha_file.md 附实测输出
