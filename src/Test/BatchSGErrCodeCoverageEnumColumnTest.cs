// 批 S-G 纵深期·错误码覆盖补漏小片（C 域·枚举/列自定义 7 码）——**零库档**（不连 PG/Redis）。
// 覆盖：enum.type_required / enum.type_not_found / enum.entity_field_required / enum.field_not_enum /
//       column_custom.grid_id_required / column_custom.user_unresolved / column_custom.data_corrupted。
// WHY 零库档：这 7 条守卫分别落在「枚举反射内省」（`SysEnumService` 无构造依赖）、「入参＋身份前置校验」
//   （`SysColumnCustomService.RequireUser` 先查 gridId 再查 userId，两条都在触碰仓储之前抛出）、
//   「静态 JSON 解析守卫」（`ParseDict`）——**全链路不需要数据库**，故零依赖直构服务（`default!` 注入 repo/cache），
//   好处：跑得快（秒级）＋不在共享库留探针行（零夹具污染），是本仓"相关面验证优先"口径下的首选形状。
// 反向哨兵（防"断言碰巧红"）：枚举名与实体字段用**反射动态发现**取真实存在者并断言结果非空——
//   证明 `type_not_found` 的因由确为"名字不存在"、`field_not_enum` 的因由确为"字段非枚举"，而非内省器整体失效。
// 身份口径：`UserManager(IHttpContextAccessor)` 挂空 accessor ⇒ `UserId==null` ⇒ 命中 user_unresolved（未认证即可达，无需造 Claim）。
using System.Reflection;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.ColumnCustom;
using AdminCore.Core.Services.ColumnCustom.Dto;
using AdminCore.Core.Services.Enum;
using AdminCore.Core.Utils.EnumIntrospection;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageEnumColumnTest
{
    [Fact]
    public void 枚举内省域_四条校验码_精确断言()
    {
        var svc = new SysEnumService();

        // ① 枚举名为空 → enum.type_required
        var ex1 = Assert.Throws<AppException>(() => svc.GetEnumDataList(new EnumQueryInput { EnumName = " " }));
        Assert.Equal(ErrorCodes.EnumTypeRequired, ex1.ErrorCode);

        // ② 枚举名不存在 → enum.type_not_found
        var ex2 = Assert.Throws<AppException>(() =>
            svc.GetEnumDataList(new EnumQueryInput { EnumName = "Sgx_NoSuchEnum_" + Guid.NewGuid().ToString("N")[..8] }));
        Assert.Equal(ErrorCodes.EnumTypeNotFound, ex2.ErrorCode);

        // ③ 实体名或字段名空白 → enum.entity_field_required（两个入口分别断言，防"只测一半"）
        var ex3a = Assert.Throws<AppException>(() =>
            svc.GetEnumDataListByField(new EnumDataByFieldInput { EntityName = " ", FieldName = "NoticeStatus" }));
        Assert.Equal(ErrorCodes.EnumEntityFieldRequired, ex3a.ErrorCode);
        var ex3b = Assert.Throws<AppException>(() =>
            svc.GetEnumDataListByField(new EnumDataByFieldInput { EntityName = "SysNotice", FieldName = " " }));
        Assert.Equal(ErrorCodes.EnumEntityFieldRequired, ex3b.ErrorCode);

        // ④ 字段存在但非枚举（或字段不存在）→ enum.field_not_enum
        var ex4 = Assert.Throws<AppException>(() =>
            svc.GetEnumDataListByField(new EnumDataByFieldInput { EntityName = "SysNotice", FieldName = "Title" }));
        Assert.Equal(ErrorCodes.EnumFieldNotEnum, ex4.ErrorCode);

        // 反向哨兵：动态发现**真实存在**的枚举名与枚举字段，断言可取到值 ⇒ 证明 ②④ 的因由是"输入不存在/非枚举"
        var asm = typeof(SysEnumService).Assembly;
        var realEnum = asm.GetTypes().First(t => t.IsEnum && t.IsPublic).Name;
        Assert.NotEmpty(svc.GetEnumDataList(new EnumQueryInput { EnumName = realEnum }));

        var probe = asm.GetTypes()
            .Where(t => t.IsClass && t.Namespace != null && t.Namespace.StartsWith("AdminCore.Core", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Entity: t.Name, Field: p.Name, Type: Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)))
            .First(x => x.Type.IsEnum);
        Assert.NotEmpty(svc.GetEnumDataListByField(new EnumDataByFieldInput { EntityName = probe.Entity, FieldName = probe.Field }));
    }

    [Fact]
    public async Task 列自定义域_三条校验码_精确断言()
    {
        // 未认证 accessor ⇒ UserManager.UserId == null；repo/cache 传 default!（三条守卫均在触碰它们之前抛出）
        var svc = new SysColumnCustomService(default!, default!, new UserManager(new FakeHttpContextAccessor()));

        // ① gridId 空白 → column_custom.grid_id_required（先于身份校验）
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            svc.ResetAsync(new GetColumnCustomInput { GridId = " " }));
        Assert.Equal(ErrorCodes.ColumnCustomGridIdRequired, ex1.ErrorCode);

        // ② gridId 合法但身份缺失 → column_custom.user_unresolved（自助端点身份单点，禁止匿名落库）
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.ResetAsync(new GetColumnCustomInput { GridId = $"sgx_grid_{Guid.NewGuid():N}"[..24] }));
        Assert.Equal(ErrorCodes.ColumnCustomUserUnresolved, ex2.ErrorCode);

        // ③ 坏 JSON → column_custom.data_corrupted（静态解析守卫，携带 detail 供检索）
        var ex3 = Assert.Throws<AppException>(() => SysColumnCustomService.ParseDict<int>("{这不是合法 JSON"));
        Assert.Equal(ErrorCodes.ColumnCustomDataCorrupted, ex3.ErrorCode);

        // 反向哨兵：合法 JSON 正常解析 / 空串按契约返回 null（证明 ③ 的因由确为"损坏"而非"解析器坏了"）
        var parsed = SysColumnCustomService.ParseDict<int>("{\"a\": 7}");
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!["a"]);
        Assert.Null(SysColumnCustomService.ParseDict<int>(string.Empty));
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
