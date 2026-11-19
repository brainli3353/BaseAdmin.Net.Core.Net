namespace AdminCore.Core.Services.Serial
{
    /// <summary>
    /// 流水号类型注册扩展点(批 W 4.3,参照 ISerialTypeProvider 移植):实现本接口的类的
    /// 公共静态/实例「字符串常量字段」即注册一个流水号类型(字段名 = 类型标识,[Description] = 显示名)。
    /// 框架启动扫描 Core 程序集全部实现(无 Furion,标准反射 + 缓存),业务仓可在自身程序集加实现扩类型。
    /// </summary>
    /// <remarks>
    /// 与参照差异:参照 App.EffectiveTypes 全程序集扫描(Furion);本仓固定扫 AdminCore.Core 程序集
    /// (AddAutoServices 同程序集口径)——业务扩类型走 Business 层时把扫描程序集参数化(迭代项,当前无此需求)。
    /// </remarks>
    public interface ISerialTypeProvider
    {
    }
}
