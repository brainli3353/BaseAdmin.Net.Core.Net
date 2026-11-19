// 批 S-G 纵深期·错误码覆盖补漏（**零库形状**片：不建夹具、不连 PG、不写一行数据）。
// 覆盖：tenant.name_required / tenant.default_undeletable / tenant_db.db_name_invalid。
// 抛点实读：`SysTenantService.cs:84-86`（AddAsync 方法体第一句就是 name 校验）、`:166-168`
//   （DeleteAsync 首句是 `id == SqlSugarConst.DefaultTenantId` 常量比较）、
//   `TenantDbRegistry.cs:250-252`（`EnsureDatabaseAsync` 第一句是库名正则校验，**在 `CREATE DATABASE` 之前**）。
// 形状依据＝框架索引/CLAUDE.md 记的零库参考形状 `BatchSGErrCodeCoverageEnumColumnTest`：
//   服务直构 ＋ `default!` 注入本路径绝不触碰的依赖 ⇒ 构造只是字段赋值，不连库不建表。
// 有意的边界（不留假象）：
//   · `tenant.default_undisableable` **不在本文件**——实读 `SetStatusAsync:258-266` 会先 `GetFirstAsync` 查租户，
//     查不到先抛 `tenant.not_found` ⇒ 它需要真库夹具（默认租户行必须在），不能塞进零库档假装能跑。
//   · `db_name_invalid` 的**反向哨兵不能传合法库名**——那会真的执行 `CREATE DATABASE`（生产库 DDL，红线①），
//     故反向只用"另一种非法形态"，并在断言里说明这不是"合法路径已通过"。
using AdminCore.Core;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Tenant;
using AdminCore.Core.Services.Tenant.Dto;
using AdminCore.Core.SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageTenantZeroDbTest
{
    [Fact]
    public async Task 租户名空白_精确断言()
    {
        // 本用例路径在方法第一句就抛出 ⇒ 仓储/缓存/库依赖全部传 null 即可（构造不触碰）
        var svc = new SysTenantService(null!, null!, null!, null!, null!, null!);

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(
            new AddTenantInput("   ", null, null, null, null)));
        Assert.Equal(ErrorCodes.TenantNameRequired, ex.ErrorCode);

        // 反向哨兵：null 与空白同口径（都是 IsNullOrWhiteSpace），确认不是"只对空串生效"
        var ex2 = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(
            new AddTenantInput(null, null, null, null, null)));
        Assert.Equal(ErrorCodes.TenantNameRequired, ex2.ErrorCode);
    }

    [Fact]
    public async Task 默认租户禁删_精确断言()
    {
        var svc = new SysTenantService(null!, null!, null!, null!, null!, null!);

        // 首句即常量比较 ⇒ 不查库不写库；默认租户是平台标识，删掉＝全平台归属塌陷
        var ex = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(SqlSugarConst.DefaultTenantId));
        Assert.Equal(ErrorCodes.TenantDefaultUndeletable, ex.ErrorCode);
    }

    [Fact]
    public async Task 租户库名非法_精确断言()
    {
        // 校验在 `CREATE DATABASE` **之前**（`TenantDbRegistry.cs:250`）⇒ 本用例保证"绝不会走到建库"。
        // 反向哨兵刻意**不用合法名**：传合法名会真的建库（生产库 DDL＝红线①，未获批）⇒
        // 这里只再验"另一种非法形态同样被拒"，**不构成"合法路径可用"的证据**。
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            TenantDbRegistry.EnsureDatabaseAsync(null!, "sgx-bad-name"));
        Assert.Equal(ErrorCodes.TenantDbNameInvalid, ex.ErrorCode);

        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            TenantDbRegistry.EnsureDatabaseAsync(null!, "1_starts_with_digit"));
        Assert.Equal(ErrorCodes.TenantDbNameInvalid, ex2.ErrorCode);

        var ex3 = await Assert.ThrowsAsync<AppException>(() =>
            TenantDbRegistry.EnsureDatabaseAsync(null!, string.Empty));
        Assert.Equal(ErrorCodes.TenantDbNameInvalid, ex3.ErrorCode);
    }

    [Fact]
    public void 启用连接串加密但未注入加解密helper_精确断言()
    {
        // 抛点实读 `TenantDbRegistry.cs:276-284`：`EnableConnEncrypt` 为真且 `Cryptogram` 为 null ⇒
        // `AppException.Of(TenantDbEncryptHelperMissing)`。两个属性都是 `internal static { get; private set; }`
        // ⇒ 测试侧没有写入口，只能反射置位（本仓首次用这个形状钉错误码）。
        // ⚠ **已知副作用（如实标注，不留假象）**：改的是**进程级静态**，而 xUnit 默认跨集合并行 ⇒ 置位窗口内
        //    若别的线程正走 `EnsureConnectionAsync` 的解密分支会被扰动。故本用例写成**不含 await 的同步小段**
        //    （窗口只有几条反射调用）并在 `finally` 原值恢复；**将来若出现"偶发的解密/租户库相关红"，先怀疑本用例**。
        var reg = typeof(TenantDbRegistry);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var enable = reg.GetProperty("EnableConnEncrypt", flags)!;
        var crypto = reg.GetProperty("Cryptogram", flags)!;
        var decrypt = reg.GetMethod("DecryptConnection", flags)!;

        var origEnable = enable.GetValue(null);
        var origCrypto = crypto.GetValue(null);
        enable.SetValue(null, true);
        crypto.SetValue(null, null);
        try
        {
            var tie = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => decrypt.Invoke(null, new object[] { "sgx_cipher_text_not_real" }));
            var app = Assert.IsType<AppException>(tie.InnerException);
            Assert.Equal(ErrorCodes.TenantDbEncryptHelperMissing, app.ErrorCode);
        }
        finally
        {
            enable.SetValue(null, origEnable);
            crypto.SetValue(null, origCrypto);
        }
    }
}
