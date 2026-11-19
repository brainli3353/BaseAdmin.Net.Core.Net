namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 绑定流水号特性(批 W 4.3/3.4,参照 AdminNetPro BindSerialAttribute 移植):标注在实体字符串属性上,
    /// 插入时若属性值为空 → 由框架 AOP 自动生成流水号填入(属性已有值不覆盖——业务显式赋值优先)。
    /// 例:[BindSerial("sale_order")] public string OrderNo { get; set; }
    /// </summary>
    /// <remarks>
    /// 类型 Type = sys_serial 配置行的分类(须 ∈ ISerialTypeProvider 注册集且已建配置行);
    /// IsGlobal = true 时取全局流水号(跨租户共享默认租户行,IgnoreTenant 读写)。
    /// 消费点:SqlSugarSetup Aop.DataExecuting InsertByObject 分支(框架内建,全实体生效,非 IDataExecuting——
    /// 后者须显式声明实体类型集,流水号面向任意业务实体)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
    public class BindSerialAttribute : Attribute
    {
        /// <summary>流水号类型(sys_serial.Type)。</summary>
        public string Type { get; }

        /// <summary>是否全局唯一流水号(跨租户共享)。</summary>
        public bool IsGlobal { get; }

        public BindSerialAttribute(string type, bool isGlobal = false)
        {
            Type = type;
            IsGlobal = isGlobal;
        }
    }
}
