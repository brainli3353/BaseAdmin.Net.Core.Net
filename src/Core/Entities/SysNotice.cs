using AdminCore.Core.Attributes;
using AdminCore.Core.Enums;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统通知公告(批 G 4.7;参照仓 Admin.NET SysNotice 字面量移植,零 Furion):
    /// 标题/内容/类型(1 通知 2 公告)/发布人快照(PublicUserId/PublicUserName/PublicOrgId/PublicOrgName)/
    /// 发布时间/撤回时间/状态(0 草稿 1 发布 2 撤回 3 删除,默认草稿)。
    /// 继承 EntityTenantBaseData 获得主键/审计/软删/租户隔离字段 + 机构数据权限(CreateOrgId 行级过滤,
    /// D1.3 S-G 纵深纳管;原继承 EntityTenant 无机构过滤,公告对 DataScope=本部门/本部门及以下曾等价全部)。
    /// 发布流程:草稿 → Public 发布(生成 SysNoticeUser 接收记录＋SignalR 广播在线用户)→ 禁删;
    /// 接收人阅读态独立于本表(sys_notice_user.ReadStatus)。
    /// </summary>
    [SugarTable("sys_notice", "系统通知公告表")]
    [IncreTable]
    [SugarIndex("i_{table}_t", nameof(Type), OrderByType.Asc)]
    public class SysNotice : EntityTenantBaseData
    {
        /// <summary>标题。</summary>
        [SugarColumn(ColumnDescription = "标题", Length = 256)]
        public string Title { get; set; } = string.Empty;

        /// <summary>内容(大文本 text——PG 无长度上限,对齐参照 BigString 语义)。</summary>
        [SugarColumn(ColumnDescription = "内容", ColumnDataType = "text")]
        public string Content { get; set; } = string.Empty;

        /// <summary>类型(1 通知 2 公告)。</summary>
        [SugarColumn(ColumnDescription = "类型（1通知 2公告）", DefaultValue = "1")]
        public NoticeTypeEnum Type { get; set; } = NoticeTypeEnum.Notice;

        /// <summary>发布人 Id(发布时填,参照 InitNoticeInfo)。</summary>
        [SugarColumn(ColumnDescription = "发布人Id")]
        public long PublicUserId { get; set; }

        /// <summary>发布人姓名(快照,参照字面量)。</summary>
        [SugarColumn(ColumnDescription = "发布人姓名", Length = 32, IsNullable = true)]
        public string? PublicUserName { get; set; }

        /// <summary>发布机构 Id(发布时按发布人当前机构查得,快照;用户无机构则 0)。</summary>
        [SugarColumn(ColumnDescription = "发布机构Id")]
        public long PublicOrgId { get; set; }

        /// <summary>发布机构名称(快照,参照字面量)。</summary>
        [SugarColumn(ColumnDescription = "发布机构名称", Length = 64, IsNullable = true)]
        public string? PublicOrgName { get; set; }

        /// <summary>发布时间(发布动作置值)。</summary>
        [SugarColumn(ColumnDescription = "发布时间", IsNullable = true)]
        public DateTime? PublicTime { get; set; }

        /// <summary>撤回时间(撤回动作置值)。</summary>
        [SugarColumn(ColumnDescription = "撤回时间", IsNullable = true)]
        public DateTime? CancelTime { get; set; }

        /// <summary>状态(0 草稿 1 发布 2 撤回 3 删除,默认草稿)。</summary>
        [SugarColumn(ColumnDescription = "状态（0草稿 1发布 2撤回 3删除）", DefaultValue = "0")]
        public NoticeStatusEnum Status { get; set; } = NoticeStatusEnum.Draft;
    }
}
