# 基础仓导出工具

从开发仓按白名单生成干净的 `BaseAdminCore` 基础仓，供业务项目 fork。

## 文件

| 文件 | 作用 |
|---|---|
| `allowlist.txt` | 基础仓保留路径（文件名与目录前缀） |
| `forbidden.txt` | 禁止出现在基础仓的敏感词/内部路径正则 |
| `export-base.ps1` | 导出：克隆/过滤、覆盖 README、清洗、提交、可选推送 |
| `sanitize.ps1` | 清洗公共文档与代码注释中的内部路径/敏感信息 |
| `verify-base.ps1` | 校验必需文件、禁止路径、禁止词与历史路径 |
| `templates/README.md` | 基础仓 README 模板 |
| `templates/docs-README.md` | 基础仓 `docs/README.md` 模板 |

## 前置条件

- Git
- PowerShell 5.1+
- `FilterHistory` 模式额外需要 `git-filter-repo`：

```bash
pip install git-filter-repo
```

没有 `git-filter-repo` 时使用 `Snapshot` 模式（单个初始提交，不保留历史）。

## 用法

```powershell
# 建议在独立目录生成，不覆盖当前工作区
.\scripts\base-export\export-base.ps1 `
  -Source . `
  -Dest ..\BaseAdminCore-base `
  -Mode Snapshot
```

指定新远端并推送：

```powershell
.\scripts\base-export\export-base.ps1 `
  -Source . `
  -Dest ..\BaseAdminCore-base `
  -Mode FilterHistory `
  -Remote <新基础仓地址> `
  -Push
```

仅校验已有导出：

```powershell
.\scripts\base-export\verify-base.ps1 -Root ..\BaseAdminCore-base
```

## 提交即对齐发布仓（2026-10-26 用户定规，首选路径）

本机无 pwsh，日常对齐改走零依赖 python3 脚本（行为等价于 `export-base.ps1` Snapshot 模式 + `sanitize.ps1` + `verify-base.ps1`）：

```bash
python3 scripts/base-export/sync-base.py --dry-run   # 只看读数（导出到临时目录+清洗+校验）
python3 scripts/base-export/sync-base.py             # 导出 → 提交 → 推送发布仓
python3 scripts/base-export/sync-base.py --no-push   # 只提交到本地发布克隆
```

- 数据源：`git show <HEAD>:<path>`（**只导出已提交内容**，不导脏工作区）；开发仓有未提交改动时默认拒绝执行。
- 出口：本地发布克隆缺省 `../BaseAdminCore-base`（origin=发布仓），导出后清空工作树重建、`add -A`、提交消息 `chore: sync clean base snapshot from <dev sha>`、**非 force** push。
- 校验：必需件齐全 + 禁路径 + 禁词 + 历史禁路径；任一不过即非零退出、不提交不推送。
- 回滚点：脚本打印上一 release sha（`git -C ../BaseAdminCore-base reset --hard <上一 sha>` 后 push 即可回退）。
- 留痕：每批把「dev sha → release sha＋回滚点」记入 `docs/自主推进_BaseAdminCore.md`。

## 注意

1. 导出脚本会先删除已存在的 `-Dest`（仅当显式传 `-Force`）；不要指向当前工作区。
2. `sanitize.ps1` 只处理文本文件，保留原 BOM 与 LF。
3. `verify-base.ps1` 会扫描工作树和历史路径；失败即非零退出。
4. 推送前先人工检查 `git log --stat`、`README.md`、`docs/README.md` 和敏感词报告。


## 约定 16：提交完成即对齐发布仓（2026-10-26 用户定规）

> 从 `CLAUDE.md` 下沉（2026-10-29 瘦身）；`CLAUDE.md` 只留"每批必跑＋判据"一句。

本仓(`BaseAdminCore-Dev`)每完成一批提交并 push main 后，**必须同批把干净快照对齐到发布仓 `BaseAdminCore`**(业务仓 fork 源)，不再由用户逐次点名。做法：`python3 scripts/base-export/sync-base.py`(零依赖 python3；规则同源 `allowlist.txt`/`forbidden.txt`/`sanitize` 替换表/templates；旧 `export-base.ps1` 保留作 Windows 侧等价实现)。**判据**：脚本末行 `[sync] verify OK` ＋ 输出 `dev <sha> -> release <sha>` ＋ `git ls-remote <release> main` 与输出一致。**留痕**：每批在 `docs/自主推进_BaseAdminCore.md` 记「dev sha → release sha＋回滚点(上一 release sha)」。**边界**：发布仓推送属对外可见动作(红线④)，本规定即用户对该动作的常设授权，逐批执行仍须留痕；**tag 不在常设范围**(发版打 tag 仍逐次请示)；先 `--dry-run` 可只看读数；开发仓有未提交改动时脚本拒绝执行(仍只导出 HEAD，`--allow-dirty` 可强过)。**⚠ "发布仓无变化"是正常态、不是漏同步(2026-10-27 实读 `allowlist.txt` 全文定案)**：白名单只有 `.gitattributes/.gitignore/NuGet.config/README.md`＋**目录 `src`/`web`/`thirdparty`/`templates`**＋**少数点名 docs**(`docs/README.md`、框架索引/使用指南/开发参考手册、设计精华参考、项目地图_模板、可用性审单)＋`CHANGELOG.md` ⇒ **`scripts/**`、`docs/业务/**`、`docs/自主推进_*.md` 都不进发布仓**。因此**只改脚本/内部规划件/快件的批**，末行必然打 `[sync] 发布仓无变化（快照已与 HEAD 一致）`＋release sha 不前进——**此时不得判为失败，也不必重跑**；反过来，看到 release sha 前进说明本批动了白名单内文件(最常见＝`src/**` 含测试)。**唯一要盯的是 `[sync] verify OK` 是否出现**(verify 缺失才是真异常)。
