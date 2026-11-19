using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Utils.EnumIntrospection;

namespace AdminCore.Core.Services.Enum
{
    /// <summary>
    /// 系统枚举服务(批 Z 6.9,参照 Admin.NET SysEnumService 移植):
    /// 编译期枚举元数据只读内省——类型列表/单枚举值集/实体字段值集。
    /// 扫描范围=Core 程序集(本仓枚举全部在 Core/Enums;参照 EnumOptions 多程序集
    /// 过滤配置裁剪——单程序集是事实,#8)。无 DB 无状态;
    /// 参照 EnumToDict(枚举转字典写端点)不移植——本仓字典域独立(裁决留痕)。
    /// </summary>
    [Service]
    public class SysEnumService
    {
        private readonly Assembly _assembly = typeof(SysEnumService).Assembly;

        /// <summary>
        /// 全部枚举类型(名称排序,含值集合)。
        /// </summary>
        public List<EnumTypeOutput> GetEnumTypeList()
        {
            return EnumIntrospector.GetEnumTypes(_assembly);
        }

        /// <summary>
        /// 单枚举值集合;类型不存在 → 业务拒绝 400。
        /// </summary>
        public List<EnumEntity> GetEnumDataList(EnumQueryInput input)
        {
            if (string.IsNullOrWhiteSpace(input.EnumName))
            {
                throw AppException.Of(ErrorCodes.EnumTypeRequired);
            }

            var list = EnumIntrospector.GetEnumEntitiesByName(_assembly, input.EnumName.Trim());
            if (list == null)
            {
                throw AppException.With(ErrorCodes.EnumTypeNotFound, "name", input.EnumName);
            }

            return list;
        }

        /// <summary>
        /// 实体字段枚举值集合;实体/字段不存在或字段非枚举 → 业务拒绝 400。
        /// </summary>
        public List<EnumEntity> GetEnumDataListByField(EnumDataByFieldInput input)
        {
            if (string.IsNullOrWhiteSpace(input.EntityName) || string.IsNullOrWhiteSpace(input.FieldName))
            {
                throw AppException.Of(ErrorCodes.EnumEntityFieldRequired);
            }

            var list = EnumIntrospector.GetFieldEnumEntities(_assembly, input.EntityName.Trim(), input.FieldName.Trim());
            if (list == null)
            {
                throw AppException.Of(ErrorCodes.EnumFieldNotEnum, new Dictionary<string, object?> { ["entity"] = input.EntityName, ["field"] = input.FieldName });
            }

            return list;
        }
    }
}
