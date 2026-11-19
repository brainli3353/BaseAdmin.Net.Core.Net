using System.ComponentModel;
using System.Reflection;

namespace AdminCore.Core.Utils.EnumIntrospection
{
    /// <summary>
    /// 枚举内省器(批 Z 6.9,参照 Admin.NET SysEnumService/EnumExtension 移植):
    /// 反射读编译期 C# 枚举类型/成员/[Description] 描述——"动态枚举"口径=运行时读出枚举元数据
    /// 供前端下拉/筛选/代码展示(数据源=编译期枚举,非 DB 存储;参照 EnumToDict 枚举转字典
    /// 写端点不移植——本仓字典域独立,不混入第二数据源,#8)。
    /// 全部纯函数(Assembly/Type 入参),单测直测不依赖宿主。
    /// </summary>
    public static class EnumIntrospector
    {
        /// <summary>
        /// 扫描程序集全部枚举类型(按名称排序)→ 类型输出(含值集合)。
        /// </summary>
        public static List<EnumTypeOutput> GetEnumTypes(Assembly assembly)
        {
            return assembly.GetTypes()
                .Where(t => t.IsEnum)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ThenBy(t => t.FullName, StringComparer.Ordinal)
                .Select(t => new EnumTypeOutput
                {
                    TypeDescribe = GetDescription(t) ?? t.Name,
                    TypeName = t.Name,
                    EnumEntities = ToEnumEntities(t)
                })
                .ToList();
        }

        /// <summary>
        /// 按类型名查枚举 → 值集合;类型不存在或非枚举 → null(调用方转业务拒绝)。
        /// </summary>
        public static List<EnumEntity>? GetEnumEntitiesByName(Assembly assembly, string enumName)
        {
            var type = assembly.GetTypes().FirstOrDefault(t => t.IsEnum && t.Name == enumName);
            return type == null ? null : ToEnumEntities(type);
        }

        /// <summary>
        /// 按实体类型名 + 字段名查字段类型枚举 → 值集合;
        /// 实体不存在/字段不存在/字段非枚举 → null(调用方转业务拒绝)。
        /// </summary>
        public static List<EnumEntity>? GetFieldEnumEntities(Assembly assembly, string entityName, string fieldName)
        {
            var entityType = assembly.GetTypes().FirstOrDefault(t => t.Name == entityName);
            if (entityType == null)
            {
                return null;
            }

            var fieldType = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name == fieldName)?.PropertyType;
            return fieldType == null || !fieldType.IsEnum ? null : ToEnumEntities(fieldType);
        }

        /// <summary>
        /// 枚举类型 → 值集合(成员名/描述/数值;[Description] 无则成员名)。
        /// </summary>
        public static List<EnumEntity> ToEnumEntities(Type enumType)
        {
            return Enum.GetNames(enumType)
                .Select(name =>
                {
                    var item = Enum.Parse(enumType, name);
                    return new EnumEntity
                    {
                        Name = name,
                        Describe = GetDescription(item) ?? name,
                        Value = Convert.ToInt32(item)
                    };
                })
                .ToList();
        }

        /// <summary>
        /// 读 [Description] 特性(Type 或 Enum 成员);无则 null。
        /// </summary>
        public static string? GetDescription(Type type)
        {
            return type.GetCustomAttributes(typeof(DescriptionAttribute), false)
                .Cast<DescriptionAttribute>()
                .FirstOrDefault()?.Description;
        }

        /// <summary>
        /// 读枚举成员的 [Description] 特性;无则 null(与 Type 重载同语义,按成员名查字段)。
        /// </summary>
        public static string? GetDescription(object enumValue)
        {
            var field = enumValue.GetType().GetField(Convert.ToString(enumValue) ?? string.Empty);
            return field?.GetCustomAttributes(typeof(DescriptionAttribute), false)
                .Cast<DescriptionAttribute>()
                .FirstOrDefault()?.Description;
        }
    }
}
