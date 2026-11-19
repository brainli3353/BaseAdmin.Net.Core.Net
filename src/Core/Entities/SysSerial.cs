using AdminCore.Core.Attributes;
using AdminCore.Core.Enums;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统流水号配置(批 W 4.3,参照 AdminNetPro SysSerial 移植):一行 = 一个「类型 × 租户」的流水号序列状态。
    /// Seq/Expy 是运行时状态(NextSeqNo 原子推进,更新语句显式忽略——只经流水号服务改);
    /// Type/Formater/ResetInterval/Min/Max 是配置(管理端 CRUD)。
    /// </summary>
    /// <remarks>
    /// 唯一索引 (Type, TenantId):同租户内类型唯一;全局流水号(isGlobal)复用默认租户行(IgnoreTenant 读写)。
    /// 本表行经 AOP 自动填 TenantId(插入时缺省 DefaultTenantId)。
    /// </remarks>
    [IncreTable]
    [SugarTable("sys_serial", "系统流水号表")]
    [SugarIndex("u_{table}_type_tenant", nameof(Type), OrderByType.Asc, nameof(TenantId), OrderByType.Asc, IsUnique = true)]
    public class SysSerial : EntityTenant
    {
        /// <summary>当前序列号(运行时状态;NextSeqNo 原子推进,管理端不可直接改)。</summary>
        [SugarColumn(ColumnDescription = "序列号")]
        public long Seq { get; set; }

        /// <summary>有效期(当前序列所属周期锚点,用于判定重置:Day 跨日/Month 跨月/Year 跨年重置回 Min)。</summary>
        [SugarColumn(ColumnDescription = "有效期")]
        public DateTime Expy { get; set; }

        /// <summary>使用分类(注册类型标识,∈ ISerialTypeProvider 注册集;同租户唯一)。</summary>
        [SugarColumn(ColumnDescription = "使用分类", Length = 32)]
        public string? Type { get; set; }

        /// <summary>重置间隔(Never/Day/Month/Year)。</summary>
        [SugarColumn(ColumnDescription = "重置间隔")]
        public ResetIntervalEnum ResetInterval { get; set; }

        /// <summary>格式化表达式(插槽模板,如 {yyyy}{MM}{dd}-{SEQ};渲染规则见 SysSerialService.FormatSeqNoAsync)。</summary>
        [SugarColumn(ColumnDescription = "格式化表达式", Length = 128)]
        public string? Formater { get; set; }

        /// <summary>序列号最小值(重置落点;缺省 1)。</summary>
        [SugarColumn(ColumnDescription = "最小值")]
        public long Min { get; set; } = 1;

        /// <summary>序列号最大值(上限,超出拒绝生成;缺省 1000000000)。</summary>
        [SugarColumn(ColumnDescription = "最大值")]
        public long Max { get; set; } = 1_000_000_000;

        /// <summary>排序(缺省 100)。</summary>
        [SugarColumn(ColumnDescription = "排序")]
        public int OrderNo { get; set; } = 100;

        /// <summary>状态(0=启用 1=停用;与 SysUser/SysTenant 同口径。停用后 NextSeqNo 拒绝)。</summary>
        [SugarColumn(ColumnDescription = "状态")]
        public int Status { get; set; }

        /// <summary>备注(可选,可空)。</summary>
        [SugarColumn(ColumnDescription = "备注", Length = 128, IsNullable = true)]
        public string? Remark { get; set; }
    }
}
