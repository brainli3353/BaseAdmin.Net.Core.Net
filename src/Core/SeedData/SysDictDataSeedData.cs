using AdminCore.Core.Entities;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 字典值种子(契约 §8 附带):「账号状态」的 5 个状态值(Id 60000-60004)。
    /// Code/Value 取状态机 int 值 "0"-"4"(与 intel 库 Status int 直接对齐,消费方免转换),Label 为中文展示名。
    /// </summary>
    [IncreSeed]
    [SeedData(6)] // 顺序 6:在 SysDictTypeSeedData(顺序 5)之后,DictTypeId 引用类型 Id=50000
    public class SysDictDataSeedData : ISqlSugarEntitySeedData<SysDictData>
    {
        public IEnumerable<SysDictData> HasData()
        {
            var tenantId = SqlSugarConst.DefaultTenantId;
            return new[]
            {
                new SysDictData
                {
                    Id = 60000,
                    DictTypeId = 50000,
                    Code = "0",
                    Value = "0",
                    Label = "待验号",
                    TagType = 5, // 信息态
                    OrderNo = 0,
                    IsEnum = true,
                    IsEnable = true,
                    TenantId = tenantId
                },
                new SysDictData
                {
                    Id = 60001,
                    DictTypeId = 50000,
                    Code = "1",
                    Value = "1",
                    Label = "观察",
                    TagType = 3, // 警示态
                    OrderNo = 1,
                    IsEnum = true,
                    IsEnable = true,
                    TenantId = tenantId
                },
                new SysDictData
                {
                    Id = 60002,
                    DictTypeId = 50000,
                    Code = "2",
                    Value = "2",
                    Label = "养号中",
                    TagType = 1, // 主态
                    OrderNo = 2,
                    IsEnum = true,
                    IsEnable = true,
                    TenantId = tenantId
                },
                new SysDictData
                {
                    Id = 60003,
                    DictTypeId = 50000,
                    Code = "3",
                    Value = "3",
                    Label = "停用",
                    TagType = 5, // 信息态
                    OrderNo = 3,
                    IsEnum = true,
                    IsEnable = true,
                    TenantId = tenantId
                },
                new SysDictData
                {
                    Id = 60004,
                    DictTypeId = 50000,
                    Code = "4",
                    Value = "4",
                    Label = "冻结",
                    TagType = 4, // 危险态
                    OrderNo = 4,
                    IsEnum = true,
                    IsEnable = true,
                    TenantId = tenantId
                }
            };
        }
    }
}
