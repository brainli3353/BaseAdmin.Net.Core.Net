// 批 S-G 纵深期·错误码覆盖补漏（**序列域·零库形状**：不建夹具、不连 PG、不写一行数据）。
// 覆盖：common.busy。
// 抛点实读：`SysSerialService.cs:240-258`——取号先抢分布式锁（`_cache.IncrementAsync(lockKey, 5s) == 1` 才算抢到），
//   5 次 ×100ms 退避后仍未抢到 ⇒ `AppException.Of(ErrorCodes.Busy, code: 429)`。
// 零库可行性依据（**实读，不是推定**）：`NextSeqNoAsync` 在**抢到锁之前**只读 `_userManager.TenantId`
//   （空 HTTP 上下文 ⇒ 回落 `SqlSugarConst.DefaultTenantId`）并拼锁键字符串，**不触碰仓储** ⇒ `rep` 传 `null!` 安全；
//   锁由假缓存恒返 `2` ⇒ 必然走到 Busy 分支。
// ⚠ 代价如实标注：本用例固定等 5×100ms≈0.5s（**生产退避语义，未为测试缩短**——缩短就等于没验到真实窗口）。
// 同域另两条为何**不在本文件**（不是偷懒，是钉不住）：
//   · `serial.type_duplicated`（`:224-227`）扫的是 **Core 程序集**内实现 `ISerialTypeProvider` 的 string 常量重复值，
//     测试程序集的类扫不到 ⇒ 当前 Core 无重复 ⇒ 该码**不可自然触发**，属"启动期防漂移守卫码"，已按 `tenant_db.create_failed`
//     同口径改判登记，**不用假对象伪造触发条件**。
//   · `serial.advance_failed`（`:289`）在"拿到锁 → 读到配置 → 单语句推进"之后，需真库配置行与真实 UPDATE 失败才可达。
using AdminCore.Core.Cache;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Serial;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageSerialZeroDbTest
{
    [Fact]
    public async Task 取号锁被占用_精确断言()
    {
        // UserManager 用真实例＋空上下文（不造假）：TenantId 取不到 ⇒ 回落默认租户，与未认证请求同口径
        var svc = new SysSerialService(null!, new AlwaysBusyCache(), new UserManager(new HttpContextAccessor()));

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.NextSeqNoAsync("sgx_probe_type"));
        Assert.Equal(ErrorCodes.Busy, ex.ErrorCode);
        // HTTP 语义 429 一并钉住（该码以 code: 429 构造，漂移即红）
        Assert.Equal(429, ex.Code);
    }

    /// <summary>
    /// 只有一个行为的假缓存：<see cref="IncrementAsync"/> 恒返 2（＝锁已被他人占用）。
    /// 其余成员**一律 fail-loud**——它们若被触碰，就证明"锁失败路径不依赖仓储/缓存其他能力"这条前提不成立，
    /// 本用例应立即变红而不是悄悄通过。
    /// </summary>
    private sealed class AlwaysBusyCache : ICacheService
    {
        public Task<long> IncrementAsync(string key, TimeSpan? expiration = null) => Task.FromResult(2L);

        private static NotSupportedException Untouched()
            => new("锁失败路径不应触碰缓存的其他能力（若命中＝本用例的零库前提已失效）");

        public Task SetAsync(string key, object? value, TimeSpan? expiration = null) => throw Untouched();
        public Task<T?> GetAsync<T>(string key) => throw Untouched();
        public Task RemoveAsync(string key) => throw Untouched();
        public Task<TimeSpan?> GetExpireAsync(string key) => throw Untouched();
        public Task<bool> ExistKeyAsync(string key) => throw Untouched();
        public Task HashSetAsync(string key, string field, object? value) => throw Untouched();
        public Task<T?> HashGetAsync<T>(string key, string field) => throw Untouched();
        public Task<List<T>> HashGetAllAsync<T>(string key) => throw Untouched();
        public Task HashDeleteAsync(string key, string field) => throw Untouched();
        public Task SetExpireAsync(string key, TimeSpan? expiration) => throw Untouched();
        public Task<bool> LockTakeAsync(string key, string token, TimeSpan expiry) => throw Untouched();
        public Task<bool> LockReleaseAsync(string key, string token) => throw Untouched();
        public Task<long> ListPushAsync(string key, string value, bool leftPush = true) => throw Untouched();
        public Task<string?> ListBlockingPopAsync(string key, TimeSpan timeout) => throw Untouched();
        public Task<long> ListLengthAsync(string key) => throw Untouched();
        public Task<List<string>> ListRangeAsync(string key, long start = 0, long stop = -1) => throw Untouched();
    }
}
