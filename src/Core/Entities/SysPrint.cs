using AdminCore.Core.Attributes;
using AdminCore.Core.Enums;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 打印模板(批 AF 4.6):模板**只存不渲染**——本实体承载模板内容(自由格式,约定为前端打印引擎可消费的 JSON/文本),
    /// 渲染与打印由业务侧或前端完成(本仓后端零渲染,与参照仓后端形态一致,见 SysPrintService remarks)。
    /// </summary>
    /// <remarks>
    /// 继承 <see cref="EntityTenantBaseData"/>:模板属租户数据(不同租户各自维护),行级隔离/审计字段由基座统一提供;
    /// D1.3 S-G 纵深起同时获得机构数据权限(CreateOrgId 行级过滤——原继承 EntityTenant 无机构过滤,
    /// 模板对 DataScope=本部门/本部门及以下曾等价全部)。
    /// 唯一索引:参照仓 Name 仅普通索引 ⇒ 并发建同名可穿透应用层查重;本仓加**唯一索引**把唯一性落到库层,
    /// 应用层同名拒绝只是友好提示(双保险,同 SysColumnCustom 业务键唯一索引先例)。
    /// 批 AF 修正(P2):唯一键含 <c>TenantId</c>——本实体是 <see cref="EntityTenantBaseData"/>(查询按租户隔离),
    /// 若唯一键只有 Name 会**跨租户互斥**(他租户同名即被库层拒且落 500);含租户后语义＝"同租户内名称唯一"。
    /// 注意:软删行**仍占用**唯一索引(PG 无部分索引口径接入),故应用层查重必须忽略软删过滤器(见 SysPrintService)。
    /// Status 口径:1=启用 / 2=停用(对齐参照仓 StatusEnum,**与 SysTenant 的 0/1 口径不同**——照参照语义,勿混用)。
    /// </remarks>
    [SugarTable("sys_print", "打印模板")]
    [IncreTable]
    [SugarIndex("i_sys_print_name", nameof(TenantId), OrderByType.Asc, nameof(Name), OrderByType.Asc, true)]
    public class SysPrint : EntityTenantBaseData
    {
        /// <summary>模板名称(业务标识;同名拒绝 + 库层唯一索引)。</summary>
        [SugarColumn(ColumnDescription = "模板名称", Length = 64)]
        public string Name { get; set; } = string.Empty;

        /// <summary>模板内容(自由格式:通常为打印引擎的 JSON 模板;大字段 text,列表查询不载)。</summary>
        [SugarColumn(ColumnDescription = "模板内容", ColumnDataType = "text")]
        public string Template { get; set; } = string.Empty;

        /// <summary>打印类型(1=浏览器 2=客户端;仅存储/展示语义,本仓不具备客户端打印通道)。</summary>
        [SugarColumn(ColumnDescription = "打印类型(1=浏览器 2=客户端)", DefaultValue = "1")]
        public PrintTypeEnum PrintType { get; set; } = PrintTypeEnum.Browser;

        /// <summary>状态(1=启用 2=停用;对齐参照仓 StatusEnum 口径)。</summary>
        [SugarColumn(ColumnDescription = "状态(1=启用 2=停用)", DefaultValue = "1")]
        public int Status { get; set; } = 1;

        /// <summary>排序(列表展示序,升序)。</summary>
        [SugarColumn(ColumnDescription = "排序", DefaultValue = "100")]
        public int OrderNo { get; set; } = 100;

        /// <summary>备注(用途说明)。</summary>
        [SugarColumn(ColumnDescription = "备注", Length = 128, IsNullable = true)]
        public string? Remark { get; set; }
    }
}