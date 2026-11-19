using System.ComponentModel;
using AdminCore.Core.Attributes;

namespace AdminCore.Core.Services.Serial
{
    /// <summary>
    /// 框架内置流水号类型注册(批 W 4.3):test = 框架自检/演示类型(单测与 e2e 用,业务勿用)。
    /// 业务类型由业务仓实现 ISerialTypeProvider 注册(字段名 = 类型标识,[Description] = 显示名)。
    /// </summary>
    [Service]
    public class BuiltInSerialTypeProvider : ISerialTypeProvider
    {
        /// <summary>框架自检类型(测试/演示)。</summary>
        [Description("自检流水号")]
        public const string Test = "test";
    }
}
