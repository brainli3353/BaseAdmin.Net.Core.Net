# 验收报告 task_012_ha_file（文件上传模块：存储抽象双 Provider + MD5 去重 + DataId 关联 + 下载 md5 校验）

- 分支：`task_task_012_ha_file`（复用原 worktree 直接继续，HEAD 前驱 `39438b3`）
- 代码提交：**e463d00**（本报告为该提交的验收文档，独立提交见 git log 后继）
- 执行人：开发（dev），2026-08-27
- 实跑环境：dev PG `172.19.0.10`（appsettings 默认 `172.18.0.3` 本会话不可达，环境变量覆盖连接串）；`Cache:Type=Memory`（dev Redis `172.19.0.2` 不可达）——等价 dev 环境，见披露⑥

## 结论速览

| # | 验收项（任务书 §验收标准） | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 | **PASS** | 实跑 0W/0E（15.2s），见「build / 回归」节 |
| 2 | upload（multipart 小文件）→ 返回文件记录（含 md5/url） | **PASS** | 实测①：200 + id/fileMd5/url 全字段 |
| 3 | 同文件再传 → 复用同记录（无新增行，psql 验证） | **PASS** | 实测②：同 id；psql `count(*)=1` |
| 4 | download 字节一致；md5 错 → 400 | **PASS** | 实测③（sha256 一致）/ 实测④（HTTP 400） |
| 5 | UpdateFileByDataId 差集同步正确（psql 验证） | **PASS** | 实测⑤：多余行软删+内容物理清理、集合内行回填 DataId |
| 6 | 切换 EnableSaveDb → 重启 → DB 模式（sys_file_content 有行）；旧 Local 数据仍可下载（回退实测） | **PASS** | 实测⑥⑦⑧：bytea 行长度与源文件精确一致；旧 Local 记录 DB 模式下字节一致 |
| 7 | 非白名单类型 → 400；超 MaxSize → 400 | **PASS** | 实测⑨（c.exe 400）/ 实测⑩（11MB 400，限 10240KB） |
| 8 | git commit + 验收报告附实测输出 | **PASS** | e463d00 + 本报告 |

**全部 8 项验收口径命中。** 另加测权限三态（401/403/匿名直连）见实测⑪。

## 交付范围（按任务书 files 清单）

Core 存储抽象（`ICustomFileProvider`/`IFileContentStore` 接口 + `DefaultFileProvider` 本地 wwwroot/uploads + `DbFileProvider` 整段 bytea 落库）+ 实体（`SysFile`/`SysFileContent`，EntityBase + [IncreTable]，任务书口径非 EntityTenant）+ 服务（`SysFileService`：白名单→上限→MD5→去重复用→后缀规范化→Provider 存储→落库；GetStream/GetBase64 按记录 Provider 路由含 Local 回退；UpdateFileByDataId 差集同步）+ Dto×3 + `FileContentStore`（IFileContentStore 实现）+ 控制器（upload / download[AllowAnonymous] / delete / associate）+ `BadRequestException` + ExceptionMiddleware 加性扩展 + appsettings `Upload` 段 + DI 注册。

## build / 回归

- `dotnet build src/huluyeye-admin.slnx`（任务书验收命令，提交后干净树实跑）：**Build succeeded. 0 Warning(s) 0 Error(s)**（15.18s，全 7 工程）。
- `dotnet test src/Test/Huluyeye.Test.csproj`：**Passed! 6/6**（Failed 0 / Skipped 0，2s）——共享路径 ExceptionMiddleware 改动回归干净。

## 实测证据（curl 实跑，dev 实例端口 5132）

- ① upload a.png（69B PNG）→ `200`：`{"code":0,"data":{"id":842325791260741,"fileMd5":"ee767024...cbe5","sizeKb":1,"fileName":"a.png","filePath":"2026/8/27","suffix":".png","provider":"Local","dataId":null,"url":"/uploads/2026/8/27/842325791260741.png"}}`
- ② 同内容 a_copy.png 再传 → 同 id `842325791260741`（复用）；psql：`select count(*) from sys_file where "FileMd5"='ee767024...'` → **1**（无新增行）。
- ③ download `?id=842325791260741&md5=ee767024...` → 200；sha256 源/下载：`b1ff9c8ea3a7...a946640` 两值一致。
- ④ download 错误 md5（`...dead`）→ **HTTP 400** `{"code":1,"msg":"文件MD5校验失败"}`。
- ⑤ associate `{dataId:111,fileIds:[b]}` → psql：a 行 `IsDelete=True`（多余整删）且本地物理文件已清理（uploads 目录仅剩 b）；b 行 `DataId=111, IsDelete=False`（回填）。
- ⑥ `Upload__EnableSaveDb=true` 重启后 upload e.png（74B）→ 记录 `provider=Db`、`url=/api/sysFile/download?id=...&md5=...`；psql：`select "SysFileId", length("Content") from sys_file_content` → **74**（与源文件字节数精确一致）。
- ⑦ DB 模式下下载旧 Local 记录 d.png（切换前上传）→ 200；sha256 `beed649e...` 两值一致（**回退实测命中**）。
- ⑧ DB 模式同文件重传 → 复用同 id，`sys_file_content` 仍 **1** 行；delete 后该内容行物理删 → **0** 行。
- ⑨ upload c.exe（application/octet-stream）→ **HTTP 400** `文件类型不允许: application/octet-stream`。
- ⑩ upload big.pdf（11,534,345B > 10240KB）→ **HTTP 400** `文件大小超出限制(最大 10240KB)`。
- ⑪ 权限三态：无 token upload → **401**；真 777 普通账号 token（无该路由授权）upload/delete → **403** `无权限:sysFile/upload`；无 token download → 直达处理器返回 400（文件不存在）证明 [AllowAnonymous] 生效。
- 附加：delete 端点 → 软删 + 物理清理；重复 delete → `{"code":1,"msg":"文件不存在"}`（HTTP 200 + 业务码，AppException 口径）。

## 披露

1. **associate 为契约外薄端点**：任务书契约 3 端点（upload/download/delete），`UpdateFileByDataId` 服务方法无契约端点不可 curl 实测——加 `POST /api/sysFile/associate`（UpdateFileInput{DataId,FileIds}→服务方法→ApiResult）作薄暴露，不改变契约 3 端点行为。
2. **400 落地方式**：契约要求非白名单类型/超 MaxSize/md5 错/文件不存在/内容缺失返回真 HTTP 400 → 新增 `BadRequestException`（Web.Core/Exceptions）+ ExceptionMiddleware 加 `catch` 分支映射 400（统一响应出口加性扩展，属 Web.Core 职责）；业务规则类失败（如重复 delete）仍 AppException（200+业务码）——两口径区分见异常类头注释。
3. **读路由契约**：GetStream/GetBase64 按**记录 Provider 字段**路由（非当前配置）→ DB 模式下历史 Provider=Local 数据回退本地读，模式切换不破坏历史（实测⑦）。
4. **DI 形态**：仅注册 `IFileContentStore→FileContentStore` + `SysFileService`；两 Provider 由服务内部 new（无其他消费者，不注册 ICustomFileProvider 接口——不投机）。
5. **评估文件位置**：`/data/tasks/running` 对开发会话只读（dispatcher 持有，EPROT）→ 评估文件落 worktree `docs/tasks/task_012_ha_file.md.assess.md`（*.assess.md 已被 .gitignore 排除，不入库）。
6. **build 自动验证文件未生成**：checkpoint 约定提交后系统自动跑 build 并写 `/data/tasks/build/task_012_ha_file.txt`；commit 后轮询 >10 分钟未出现（目录为空，task_008 亦无历史 build 文件，先例见 008 报告披露③）。为给权威验收证据并满足 0/0 口径，**dev 实跑任务书验收命令**（0W/0E）+ `dotnet test` 6/6，输出见「build / 回归」节。
7. **冒烟环境替换**：PG 用 `172.19.0.10`（默认 `172.18.0.3` 本会话不可达）、缓存 Memory（Redis 不可达）；启动自检（缓存预热 + CodeFirst 建表 + 种子）实际连库建 sys_file/sys_file_content。冒烟数据已全部清理：sys_file 测试行 0、sys_file_content 0、worktree uploads 目录已删。

## 交付最小清单

- **限域**：新增 Core 接口×2 + Providers×2、Business 实体×2 + Services/File 目录（服务+Dto×3+FileContentStore）、Web.Core BadRequestException、Web.Entry 控制器；修改仅 3 处——ExceptionMiddleware（新增 catch 分支，既有异常路径零变化）、Program.cs（AddScoped×2）、appsettings.json（新增 Upload 段，与 010 Configuration 目录不重叠）。
- **影响面**：全局 ExceptionMiddleware 为纯加性（新异常类型→400，既有类型映射不变）；其余 /api 路由行为不变（dotnet test 6/6 回归佐证）；DB 仅新增两表（CodeFirst 自建）。
- **回滚**：`git revert e463d00`（独立新文件 + 3 文件小改动，无跨任务耦合）；回滚后遗留的 sys_file/sys_file_content 空表不影响启动（无代码引用）。
- **最小验证**：build 0/0 + test 6/6 + 上表 8 项验收口径实测（①–⑩ + 权限⑪）复跑命令即得。
