using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 主键基类:雪花主键(非自增,由 StaticConfig.CustomSnowFlakeFunc → YitIdHelper 生成,AOP 插入时自动填)。
    /// 列名约定(task_008 fixa):主键列小写 "id"、其余列 PascalCase——
    /// 验收口径 psql 以无引号 id 引用主键(PG 未加引号标识符折小写且引用大小写敏感,列必须恰好小写才命中),
    /// 审计列以引号精确引用 PascalCase(如 "CreateTime"),两口径并存。
    /// </summary>
    public abstract class EntityBaseId
    {
        [SugarColumn(ColumnName = "id", ColumnDescription = "主键Id", IsPrimaryKey = true, IsIdentity = false)]
        public virtual long Id { get; set; }
    }
}
