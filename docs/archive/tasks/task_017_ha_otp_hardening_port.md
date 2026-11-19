# task_id: task_017_ha_otp_hardening_port
# subject: task_017_ha_otp_hardening_port
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P1
# dep_on: task_008_ha_rbac
# files: src/Business/Services/Auth/, src/Core/Cache/, src/Core/Interfaces/

# 任务：OTP 安全加固移植（假合并存量补录之一）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：本地分支 `task_task_007_ha_otp_fix` 的提交 `bc6065e`（git show 可读完整 diff）——该分支交付因历史流程缺陷未合入 main，本任务把其中**仍然有意义的功能语义**在当前 main 代码基线上重新实现。
**代码纪律**：关键位置中文注释；禁止修改 files 外文件；**不得回退 main 已有的任何行为**（CacheSetup 含 016 刚并入的 Redis 降级逻辑，只能在其上扩展）。

## 契约（按此实现）

1. **账号级 OTP 失败锁**：同一账号连续 10 次校验失败 → 锁定 30 分钟（锁内直接拒绝校验，防爆破）
2. **挑战失败 TTL 语义修正**：校验失败时若需重新下发挑战期（LastUsedTimeStep 回写类逻辑），按**剩余 TTL** 写回缓存，不允许失败重试刷新有效窗口
3. **输入长度上限**：OTP code 输入字段加最大长度校验（防超长 DoS），超限 400
4. **ICacheService 扩展**：如上述需要 `GetExpireAsync` 类能力，Redis 与 Memory **两个实现同步加**（接口 + 双实现）

## 交付物

- `/data/worktrees/admin/task_017_ha_otp_hardening_port/src/Business/Services/Auth/AuthService.cs`（加固逻辑）
- `/data/worktrees/admin/task_017_ha_otp_hardening_port/src/Core/Cache/`、`src/Core/Interfaces/ICacheService.cs`（如契约 4 需要则同批）

## 验收标准

- `cd /data/worktrees/admin/task_017_ha_otp_hardening_port && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误；`dotnet test src/Test --no-build` 全绿
- 失败锁实测（curl 附输出）：以错误 OTP 连续调用登录校验 ≥10 次 → 第 11 次返回锁定（非原校验错误码）；等待或改参确认 30 分钟窗口语义生效
- TTL 不续期实测：构造剩余时间窗口断言（伪码/单元测试亦可），证明失败不影响剩余时长单调递减
- 超长输入实测：code 参数传 500 字符 → 400；正常 6 位 → 400/200 语义不变
- 单测覆盖：以上三类各至少 1 条断言写进 Test 工程

