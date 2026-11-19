#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把开发仓的干净快照对齐到发布仓 BaseAdminCore（业务仓 fork 源）。

规则单源：同目录 allowlist.txt / forbidden.txt / sanitize 替换表 / templates/，
行为等价于 export-base.ps1（Snapshot 模式）+ sanitize.ps1 + verify-base.ps1 的串联，
但改为**零依赖 python3**（本机无 pwsh）。

为什么有本脚本（2026-10-26 用户定规）：此前每次都要人工点名"对齐发布仓"，
对齐动作本身机械且易漏；定规后 = 每批提交 push main 后同批执行本脚本，留痕「dev sha → release sha＋回滚点」。

安全边界：
* 只从 `git show <HEAD>:<path>` 读取**已提交内容**（不导出脏工作区）；
* 只导出 allowlist 前缀/精确路径；模板 README 覆盖；按替换表清洗内部路径/口令/主机名；
* 发布前跑 verify（必需件/禁路径/禁词/历史禁路径），任一不过 = 非零退出、不提交不推送；
* push 非 force：发布仓分叉（他处改过）→ 尝试 `pull --ff-only`，仍失败即退出并提示人工裁决；
* `--dry-run` 只导出到临时目录并打印读数，不碰发布仓。

用法：
  python3 scripts/base-export/sync-base.py                # 导出 + 提交 + 推送
  python3 scripts/base-export/sync-base.py --dry-run      # 只看读数
  python3 scripts/base-export/sync-base.py --no-push      # 提交到本地发布克隆，不推
  python3 scripts/base-export/sync-base.py --allow-dirty  # 允许开发仓有未提交改动（仍只导出 HEAD）
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
DEFAULT_RELEASE_REMOTE = "http://192.168.1.202:3000/LocalDevelopment/BaseAdminCore.git"

TEXT_EXTENSIONS = {
    ".md", ".cs", ".json", ".ts", ".vue", ".csproj", ".props", ".slnx", ".sln",
    ".html", ".css", ".scss", ".yml", ".yaml", ".txt", ".ps1", ".xml", ".config",
}
SKIP_DIRS = {".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea", "logs"}

# 与 sanitize.ps1 同源、同序（顺序敏感：长路径先于短词，避免二次替换）
REPLACEMENTS: list[tuple[str, str]] = [
    ("Admin@123", "ChangeMe@123"),
    ("hulubao", "deploy-host"),
    (r"192\.168\.1\.202", "test-host"),
    (r"172\.18\.[0-9.]+", "db-host"),
    ("（原名 huluyeye-admin）", ""),
    ("huluyeye_admin_test", "baseadmin_test"),
    ("huluyeye_admin", "baseadmin"),
    ("huluyeye-admin", "BaseAdminCore"),
    ("huluyeye", "BaseAdminCore"),
    (r"\.\./MainWorkspace/docs/全局公约\.md", "项目编码约定"),
    (r"MainWorkspace/docs/全局公约\.md", "项目编码约定"),
    (r"docs/全局公约\.md", "项目编码约定"),
    ("MainWorkspace", "工作区"),
    ("葫芦爷爷", "项目维护者"),
    (r"docs/研究/[A-Za-z0-9_./-]+\.md", "内部移植调研记录"),
    (r"docs/archive/[A-Za-z0-9_./-]+", "内部历史归档"),
    (r"docs/业务/规划/[^\s)]+", "内部规划台账"),
    (r"docs/上送_[^\s)]+\.md", "内部流程记录"),
    (r"docs/sqlsugar升级评估-G7G9\.md", "内部升级评估"),
    (r"docs/自主推进_BaseAdminCore\.md", "内部工作记录"),
    (r"docs/框架功能清单\.md", "框架功能概览"),
    (r"docs/框架差距清单\.md", "内部差距清单"),
    (r"docs/立项\.md", "项目立项记录"),
    (r"docs/环境管理\.md", "环境部署说明"),
    (r"框架功能清单\.md", "框架索引.md"),
    (r"框架差距清单\.md", "内部维护台账（不在本仓）"),
    (r"plans/项目计划书\.md", "内部项目计划"),
    (r"plans/[^\s)]+", "内部计划资料"),
    (r"docs/研究", "内部移植调研记录"),
    (r"docs/archive", "内部历史归档"),
    (r"docs/业务", "内部规划台账"),
    (r"docs/框架差距清单", "内部差距清单"),
    (r"CLAUDE\.md 硬性约定", "项目开发约定"),
    ("BaseAdminCore框架开发参考手册", "BaseAdminCore 框架开发参考手册"),
    (r"CLAUDE\.md", "项目开发约定"),
]
REPLACEMENTS_COMPILED = [(re.compile(p, re.IGNORECASE), v) for p, v in REPLACEMENTS]

REQUIRED_PATHS = [
    "README.md",
    "docs/README.md",
    "docs/框架索引.md",
    "docs/框架使用指南.md",
    "docs/框架开发参考手册.md",
    "docs/baseadmin设计精华参考.md",
    "src/AdminCore.slnx",
    "src/Core",
    "src/Web.Core",
    "src/Web.Entry",
    "src/Business",
    "web/package.json",
    "templates/dotnet-repo/README.md",
    "templates/dotnet-repo/scripts/doctor.sh",
    "docs/项目地图_模板.md",
]

FORBIDDEN_PATHS = [
    "CLAUDE.md",
    "plans",
    "docs/archive",
    "docs/研究",
    "docs/业务",
    "docs/框架差距清单.md",
    "docs/框架功能清单.md",
    "docs/立项.md",
    "docs/环境管理.md",
    "docs/自主推进_BaseAdminCore.md",
    "docs/sqlsugar升级评估-G7G9.md",
]


def sh(cmd: list[str], cwd: Path | None = None, check: bool = True) -> subprocess.CompletedProcess:
    proc = subprocess.run(cmd, cwd=str(cwd) if cwd else None, capture_output=True, text=True)
    if check and proc.returncode != 0:
        raise RuntimeError(f"命令失败({proc.returncode}): {' '.join(cmd)}\n{proc.stderr.strip()[:800]}")
    return proc


def read_allowlist() -> list[str]:
    path = SCRIPT_DIR / "allowlist.txt"
    entries = [
        line.strip()
        for line in path.read_text(encoding="utf-8-sig").splitlines()
        if line.strip() and not line.strip().startswith("#")
    ]
    if not entries:
        raise RuntimeError(f"allowlist 为空: {path}")
    return entries


def is_allowlisted(rel: str, entries: list[str]) -> bool:
    return any(rel == e or rel.startswith(e + "/") for e in entries)


def export_snapshot(dest_dir: Path) -> tuple[str, int]:
    """把 HEAD 的 allowlist 文件按字节导出到 dest_dir（不含 .git）。"""
    head = sh(["git", "-C", str(REPO_ROOT), "rev-parse", "HEAD"]).stdout.strip()
    allowlist = read_allowlist()
    tracked = sh(["git", "-C", str(REPO_ROOT), "ls-tree", "-r", "-z", "--name-only", "HEAD"]).stdout.split("\0")
    copied = 0
    for rel in tracked:
        if not rel or not is_allowlisted(rel, allowlist):
            continue
        blob = subprocess.run(
            ["git", "-C", str(REPO_ROOT), "show", f"HEAD:{rel}"],
            capture_output=True,
        )
        if blob.returncode != 0:
            raise RuntimeError(f"读取 HEAD:{rel} 失败")
        target = dest_dir / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(blob.stdout)
        copied += 1

    shutil.copyfile(SCRIPT_DIR / "templates" / "README.md", dest_dir / "README.md")
    (dest_dir / "docs").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(SCRIPT_DIR / "templates" / "docs-README.md", dest_dir / "docs" / "README.md")
    return head, copied


def sanitize(root: Path) -> int:
    changed = 0
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(root).as_posix()
        if any(part in SKIP_DIRS for part in rel.split("/")):
            continue
        if path.suffix.lower() not in TEXT_EXTENSIONS:
            continue
        data = path.read_bytes()
        has_bom = data.startswith(b"\xef\xbb\xbf")
        text = data.decode("utf-8-sig")
        updated = text
        for pattern, value in REPLACEMENTS_COMPILED:
            updated = pattern.sub(value, updated)
        if updated != text:
            path.write_bytes((b"\xef\xbb\xbf" if has_bom else b"") + updated.encode("utf-8"))
            changed += 1
            print(f"[sanitize] {rel}")
    print(f"[sanitize] scanned, changed {changed}")
    return changed


def verify(root: Path) -> list[str]:
    failures: list[str] = []
    for rel in REQUIRED_PATHS:
        if not (root / rel).exists():
            failures.append(f"missing required path: {rel}")
    for rel in FORBIDDEN_PATHS:
        if (root / rel).exists():
            failures.append(f"forbidden path present: {rel}")

    forbidden_file = str(SCRIPT_DIR / "forbidden.txt")
    # git grep 需要仓库语境：在 root 内临时 init 一个只读索引检查太重；直接 python 扫描等价语义
    patterns = [
        line.strip()
        for line in (SCRIPT_DIR / "forbidden.txt").read_text(encoding="utf-8-sig").splitlines()
        if line.strip() and not line.strip().startswith("#")
    ]
    compiled = [re.compile(p, re.IGNORECASE) for p in patterns]
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        rel = path.relative_to(root).as_posix()
        if any(part in SKIP_DIRS for part in rel.split("/")):
            continue
        if path.suffix.lower() not in TEXT_EXTENSIONS:
            continue
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        for line_no, line in enumerate(text.splitlines(), start=1):
            for pattern in compiled:
                if pattern.search(line):
                    failures.append(f"forbidden content: {rel}:{line_no}: {line.strip()[:120]}")
                    break
    return failures


def commit_and_push(dest: Path, dev_sha: str, remote: str | None, push: bool) -> tuple[str, str]:
    sh(["git", "-C", str(dest), "add", "-A"])
    status = sh(["git", "-C", str(dest), "status", "--porcelain"]).stdout.strip()
    if not status:
        sha = sh(["git", "-C", str(dest), "rev-parse", "HEAD"]).stdout.strip()
        print("[sync] 发布仓无变化（快照已与 HEAD 一致）")
        return sha, sha

    prev = sh(["git", "-C", str(dest), "rev-parse", "HEAD"]).stdout.strip()
    message = f"chore: sync clean base snapshot from {dev_sha}"
    sh([
        "git", "-C", str(dest),
        "-c", "user.name=BaseAdminCore Export",
        "-c", "user.email=baseadmin-export@localhost",
        "commit", "--quiet", "-m", message,
    ])
    sha = sh(["git", "-C", str(dest), "rev-parse", "HEAD"]).stdout.strip()
    print(f"[sync] 提交 {sha[:12]}（回滚点 {prev[:12]}）")

    if not push:
        print("[sync] --no-push：未推送")
        return sha, prev

    if remote:
        has_origin = sh(["git", "-C", str(dest), "remote", "get-url", "origin"], check=False).returncode == 0
        if has_origin:
            sh(["git", "-C", str(dest), "remote", "set-url", "origin", remote])
        else:
            sh(["git", "-C", str(dest), "remote", "add", "origin", remote])

    result = sh(["git", "-C", str(dest), "push", "origin", "main"], check=False)
    if result.returncode != 0:
        print("[sync] push 失败，尝试 pull --ff-only 后重试一次（发布仓分叉需人工裁决）")
        pull = sh(["git", "-C", str(dest), "pull", "--ff-only", "origin", "main"], check=False)
        if pull.returncode != 0:
            raise RuntimeError("发布仓分叉且无法快进，需人工裁决：\n" + result.stderr.strip()[:800])
        sh(["git", "-C", str(dest), "push", "origin", "main"])
    print(f"[sync] pushed {sha[:12]} -> {remote}")
    return sha, prev


def main() -> int:
    parser = argparse.ArgumentParser(description="把开发仓 HEAD 的干净快照对齐到发布仓")
    parser.add_argument("--dest", default=str(REPO_ROOT.parent / "BaseAdminCore-base"),
                        help="发布仓本地克隆目录（缺省 ../BaseAdminCore-base）")
    parser.add_argument("--remote", default=DEFAULT_RELEASE_REMOTE, help="发布仓远端 URL")
    parser.add_argument("--dry-run", action="store_true", help="只导出到临时目录并校验，不碰发布仓")
    parser.add_argument("--no-push", action="store_true", help="提交到发布仓本地克隆但不推送")
    parser.add_argument("--allow-dirty", action="store_true", help="允许开发仓有未提交改动（仍只导出 HEAD）")
    args = parser.parse_args()

    dirty = sh(["git", "-C", str(REPO_ROOT), "status", "--porcelain"]).stdout.strip()
    if dirty and not args.allow_dirty:
        print("[sync] 开发仓有未提交改动，先提交（或 --allow-dirty 仍只导出 HEAD）：")
        print(dirty[:1200])
        return 2

    head = sh(["git", "-C", str(REPO_ROOT), "rev-parse", "HEAD"]).stdout.strip()
    print(f"[sync] 开发仓 HEAD = {head}")

    if args.dry_run:
        with tempfile.TemporaryDirectory(prefix="base-snapshot-") as tmp:
            dest = Path(tmp)
            _, copied = export_snapshot(dest)
            sanitize(dest)
            failures = verify(dest)
            print(f"[sync] dry-run: 导出 {copied} 文件；校验失败 {len(failures)} 项")
            for failure in failures[:20]:
                print("  " + failure)
            return 0 if not failures else 1

    dest = Path(args.dest)
    if not (dest / ".git").exists():
        print(f"[sync] 发布仓本地克隆不存在或非 git 仓库: {dest}")
        return 2

    # 清空工作树（保留 .git），再按 HEAD 写快照
    for child in dest.iterdir():
        if child.name == ".git":
            continue
        shutil.rmtree(child) if child.is_dir() else child.unlink()
    _, copied = export_snapshot(dest)
    print(f"[sync] 导出 {copied} 个 allowlist 文件 + 模板 README")
    sanitize(dest)
    failures = verify(dest)
    if failures:
        print(f"[sync] verify FAILED（{len(failures)} 项），不提交不推送：")
        for failure in failures[:20]:
            print("  " + failure)
        return 1
    print("[sync] verify OK：必需件齐全、禁路径/禁词零命中")

    sha, prev = commit_and_push(dest, head, args.remote, push=not args.no_push)
    remote_tip = sh(["git", "-C", str(dest), "ls-remote", "origin", "main"], check=False).stdout.split()[0] if not args.no_push else sha
    print(f"[sync] 完成: dev {head[:12]} -> release {sha[:12]}（远端 main {remote_tip[:12]}，回滚点 {prev[:12]}）")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:  # noqa: BLE001 - CLI 入口统一失败出口
        print(f"[sync] ERROR: {exc}")
        sys.exit(1)
