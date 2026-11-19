using SqlSugar;

namespace AdminCore.Core.SqlSugar;

/// <summary>
/// 自定义数据过滤器扩展点(批 P 3.3,参照 Admin.NET SqlSugarDataExecuting 移植,见 docs/研究/g10_refer_research.md):
/// 业务层实现本接口(类加 [Service] 特性,AddAutoServices 自动注册为 IDataExecuting),
/// 在 SQL 生成前的数据执行窗口(Aop.DataExecuting 钩子)按实体类型分发——
/// 框架内建填充(雪花主键/创建更新审计/租户/机构)之后的自定义逻辑扩展点。
/// </summary>
public interface IDataExecuting
{
    /// <summary>处理的实体类型集合(运行时类型精确匹配;null/空 = 不分发)。</summary>
    IReadOnlyList<Type> EntityNames { get; }

    /// <summary>
    /// 数据执行回调:<paramref name="oldData"/> 为旧值(InsertByObject 时为 null;UpdateByObject/DeleteByObject 时为变更前旧值),
    /// <paramref name="model"/> 为 SqlSugar 数据过滤器模型(含 OperationType/EntityValue/列信息)。
    /// 同步窗口(SQL 生成前):勿做 IO 重活/异步等待。
    /// </summary>
    void Execute(object oldData, DataFilterModel model);
}

/// <summary>
/// 自定义数据过滤器分发(批 P 3.3):纯函数,零静态态——实现列表由 SqlSugarSetup 装配时
/// 经 IServiceProvider 解析后闭包捕获(每 DI 容器独立,避免多容器/多测试进程互踩),
/// 按 EntityNames 含目标实体运行时类型分发(参照 SqlSugarDataExecuting.Execute 语义保留)。
/// 裁决 g10 §5 ②:去参照 Furion 三件套(Cache.Default 缓存/App.EffectiveTypes 反射/Activator.CreateInstance)——
/// 零 Furion 红线 + AddAutoServices 已有同语义装配(#11 复用优先)。
/// </summary>
public static class SqlSugarDataExecuting
{
    /// <summary>按实体类型分发:<paramref name="items"/> 为当前容器的实现列表(可为空 = 零扩展点基线)。</summary>
    public static void Execute(IReadOnlyList<IDataExecuting> items, object oldData, DataFilterModel model)
    {
        if (items is not { Count: > 0 }) return;
        var entityType = model.EntityValue?.GetType();
        if (entityType == null) return;

        foreach (var item in items)
        {
            if (item.EntityNames is not { Count: > 0 } names) continue;
            if (!names.Contains(entityType)) continue;
            item.Execute(oldData, model);
        }
    }
}
