using System.ComponentModel;
using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.Enum;
using AdminCore.Core.Utils.EnumIntrospection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 Z 6.9 系统枚举(枚举内省)单测:
    /// EnumIntrospector 纯函数(固定类型直测不依赖宿主)+ SysEnumService 业务拒绝分支
    /// (AppException)+ DI 轻量容器解析(AddAutoServices,无 DB)。
    /// </summary>
    public class BatchZEnumTest
    {
        // 被测程序集=Core(枚举类型所在),非测试程序集
        private static readonly Assembly CoreAssembly = typeof(ResetIntervalEnum).Assembly;

        // ─────────────────────────── EnumIntrospector 纯函数 ───────────────────────────

        [Fact(DisplayName = "ToEnumEntities: ResetIntervalEnum 四成员名/值/描述口径")]
        public void ToEnumEntities_ResetInterval()
        {
            var list = EnumIntrospector.ToEnumEntities(typeof(ResetIntervalEnum));

            Assert.Equal(4, list.Count);
            Assert.Equal(
                new[] { ("Never", 0), ("Day", 1), ("Month", 2), ("Year", 3) },
                list.Select(e => (e.Name, e.Value)));
            // 本仓框架枚举已挂 [Description] → Describe 取特性值(无特性回落成员名口径见用例 5 PlainEnum)
            Assert.Equal(
                new[] { "永不重置", "按日", "按月", "按年" },
                list.Select(e => e.Describe).ToArray());
        }

        [Fact(DisplayName = "GetEnumTypes: Core 程序集枚举类型名排序无重复且含 6 个本仓枚举")]
        public void GetEnumTypes_CoreAssembly()
        {
            var list = EnumIntrospector.GetEnumTypes(CoreAssembly);

            Assert.True(list.Count >= 6, $"Core 程序集应至少含 6 个本仓枚举,实际 {list.Count}");
            foreach (var expected in new[]
            {
                "DataScopeEnum", "MessageTypeEnum", "NoticeStatusEnum",
                "NoticeTypeEnum", "NoticeUserStatusEnum", "ResetIntervalEnum"
            })
            {
                Assert.Contains(list, t => t.TypeName == expected);
            }

            var names = list.Select(t => t.TypeName).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
            Assert.Equal(names.Distinct().Count(), names.Count);
            Assert.All(list, t => Assert.False(t.EnumEntities.Count == 0));
        }

        [Fact(DisplayName = "GetEnumEntitiesByName: 命中返回值集;不存在返回 null")]
        public void GetEnumEntitiesByName_HitAndMiss()
        {
            var hit = EnumIntrospector.GetEnumEntitiesByName(CoreAssembly, "NoticeStatusEnum");
            Assert.NotNull(hit);
            Assert.Equal(4, hit!.Count); // Draft=0/Public=1/Cancel=2/Deleted=3

            Assert.Null(EnumIntrospector.GetEnumEntitiesByName(CoreAssembly, "NotExistEnum"));
        }

        [Fact(DisplayName = "GetFieldEnumEntities: SysNotice.Status 命中 NoticeStatusEnum;非枚举字段/不存在 → null")]
        public void GetFieldEnumEntities_Branches()
        {
            var hit = EnumIntrospector.GetFieldEnumEntities(CoreAssembly, "SysNotice", "Status");
            Assert.NotNull(hit);
            Assert.Equal(4, hit!.Count);

            // 非枚举字段(string)
            Assert.Null(EnumIntrospector.GetFieldEnumEntities(CoreAssembly, "SysNotice", "Title"));
            // 字段不存在
            Assert.Null(EnumIntrospector.GetFieldEnumEntities(CoreAssembly, "SysNotice", "NotExistField"));
            // 实体不存在
            Assert.Null(EnumIntrospector.GetFieldEnumEntities(CoreAssembly, "NotExistEntity", "Status"));
        }

        [Fact(DisplayName = "GetDescription: [Description] 类型/成员读取,无特性 → null")]
        public void GetDescription_Attribute()
        {
            Assert.Equal("测试枚举", EnumIntrospector.GetDescription(typeof(WithDescriptionEnum)));
            // 框架枚举已挂 [Description] → 读特性值(批 AF+ sysEnum 页中文描述)
            Assert.Equal("流水号重置间隔", EnumIntrospector.GetDescription(typeof(ResetIntervalEnum)));
            Assert.Equal("成员甲", EnumIntrospector.GetDescription(WithDescriptionEnum.Alpha));
            // Beta 无 [Description] → null(特性仅 Alpha 有)
            Assert.Null(EnumIntrospector.GetDescription(WithDescriptionEnum.Beta));
            // PlainEnum 类型无特性 → null(回落成员名分支)
            Assert.Null(EnumIntrospector.GetDescription(typeof(PlainEnum)));
        }

        [Description("测试枚举")]
        private enum WithDescriptionEnum
        {
            [Description("成员甲")]
            Alpha,
            Beta
        }

        // 无任何 [Description] 的枚举 → 验证"回落名称"分支(框架枚举已全部挂特性后,回落分支改由本测试枚举承载)
        private enum PlainEnum
        {
            A,
            B
        }

        // ─────────────────────────── SysEnumService 业务分支 ───────────────────────────

        [Fact(DisplayName = "Service: 枚举名空/不存在 → AppException;合法 → 值集")]
        public void Service_GetEnumDataList_Branches()
        {
            var service = CreateService();

            Assert.Throws<AppException>(() => service.GetEnumDataList(new EnumQueryInput()));
            Assert.Throws<AppException>(
                () => service.GetEnumDataList(new EnumQueryInput { EnumName = "NotExistEnum" }));

            var list = service.GetEnumDataList(new EnumQueryInput { EnumName = " ResetIntervalEnum " });
            Assert.Equal(4, list.Count);
        }

        [Fact(DisplayName = "Service: 字段查询空参/非枚举字段 → AppException;合法 → 值集")]
        public void Service_GetEnumDataListByField_Branches()
        {
            var service = CreateService();

            Assert.Throws<AppException>(
                () => service.GetEnumDataListByField(new EnumDataByFieldInput()));
            Assert.Throws<AppException>(
                () => service.GetEnumDataListByField(new EnumDataByFieldInput { EntityName = "SysNotice", FieldName = "Title" }));

            var list = service.GetEnumDataListByField(new EnumDataByFieldInput { EntityName = "SysNotice", FieldName = "Type" });
            Assert.Equal(3, list.Count); // Notice=1/Announcement=2/Upgrade=3(批 AF 4.11 加"系统升级"承载升级公告)
            Assert.Contains(list, e => e.Name == "Upgrade" && e.Value == 3);
        }

        [Fact(DisplayName = "DI: SysEnumService 经 AddAutoServices 解析,typeList 含 6 个本仓枚举")]
        public void Service_Di_Resolve()
        {
            var services = new ServiceCollection();
            services.AddAutoServices(typeof(SysEnumService).Assembly);
            using var provider = services.BuildServiceProvider();

            var service = provider.GetRequiredService<SysEnumService>();
            var list = service.GetEnumTypeList();
            Assert.Contains(list, t => t.TypeName == "ResetIntervalEnum");
        }

        private static SysEnumService CreateService() => new();
    }
}
