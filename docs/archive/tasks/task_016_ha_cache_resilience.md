# task_id: task_016_ha_cache_resilience
# subject: task_016_ha_cache_resilience
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P2
# dep_on: task_008_ha_rbac
# files: src/Core/Cache/, src/Web.Entry/appsettings.json

# 任务：缓存初始化健壮化（Redis 不可达降级 + 连接串主机名化）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md 缓存相关章节（ICacheService 双实现：Redis/Memory）；docs/servers-ops.md「工作负载分配规定」无需读取。
**代码纪律**：关键位置中文注释；禁止修改 files 声明外文件。

## 背景（2026-08-27 发布门首战实测）

main 稳定版部署 test 环境启动即崩：`CacheSetup.AddCache` 强制 `ConnectionMultiplexer.Connect`（abortConnect 默认 true），Redis 不可达 = **整个应用无法启动**（未处理 RedisConnectionException）。稳定性缺陷：依赖中间件故障不应致应用瘫痪。另：appsettings.json 内 PG/Redis 地址为网桥 IP（172.18.0.x），容器重启漂移风险高——统一改容器名主机名（redis 容器已接入 agent 网络，postgres 同理）。

## 契约（按此实现）

1. **连接失败可降级**（src/Core/Cache/CacheSetup.cs）：
   - AddCache 构造 ICacheService（Redis 实现）时 Connect 失败：捕获异常 → 记 ILogger 错误日志（含目标地址与降级说明）→ 注入 MemoryCache 等效实现，**不抛出不崩溃**
   - 配置节 `Cache:Type` 显式声明 Redis 时优先尝试 Redis，失败降级 Memory 并告警（语义清晰）；显式 Memory 行为不变
   - 降级为应用生命周期决定（恢复需重启应用），不需动态探测切换

2. **连接串主机名化**（src/Web.Entry/appsettings.json）：
   - `ConnectionStrings:Default` 与 `ConnectionConfigs[0].ConnectionString` 的 Host 由 IP 改 `postgres`
   - `ConnectionStrings:Redis` 改 `redis:6379`（保留原注释纪律：密码由环境变量注入不入库）
   - 其余配置节不动

3. **构建零警告**：修改处不引入新告警

## 交付物

- `/data/worktrees/admin/task_016_ha_cache_resilience/src/Core/Cache/CacheSetup.cs`（降级逻辑同批）
- `/data/worktrees/admin/task_016_ha_cache_resilience/src/Web.Entry/appsettings.json`

## 验收标准

- `cd /data/worktrees/admin/task_016_ha_cache_resilience && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- 降级实测（验收报告附输出）：以不可达 Redis 启动服务——
  ```
  docker exec huluyeye-dev-a sh -c "cd /data/worktrees/admin/task_016_ha_cache_resilience/src/Web.Entry && export DOTNET_ROOT=/usr/lib/dotnet && export PATH=/usr/lib/dotnet:$PATH && ASPNETCORE_URLS=http://127.0.0.1:5199 ConnectionStrings__Redis='postgres:19999' nohup dotnet run --no-build --no-launch-profile > /tmp/t16_a.log 2>&1 & echo \$!"
  ```
  → 进程存活（`sleep 12 && kill -0 $(pgrep -f "5199") 最新进程`）+ 日志含降级告警 + `docker exec huluyeye-dev-a curl -s -o /dev/null -w "%{http_code}" -m 3 http://127.0.0.1:5199/` 非 000（业务接口可用）；测完 kill 该进程（精确 pgrep pid）
- 正常路径回归：默认配置（redis 可达）启动另一个端口 → 启动无降级告警、键写入验证走 Redis 实现可用接口返回正常
- 主机名化回归：默认配置启动成功且连库正常（sys_user 表可查）
