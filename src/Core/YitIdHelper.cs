using YitGen = Yitter.IdGenerator; // 别名引用上游命名空间:避免与本类同名(见下)冲突

namespace AdminCore.Core
{
    /// <summary>
    /// 雪花 ID 助手(对 thirdparty Yitter.IdGenerator 的进程级封装:初始化 + 取号)。
    /// </summary>
    /// <remarks>
    /// WHY 与上游同名:对齐 baseadmin 的 <c>YitIdHelper</c> 调用习惯;
    /// 上游 <c>Yitter.IdGenerator.YitIdHelper</c> 是实例风格样例类,本静态类在其之上加
    /// 「配置化 WorkerId + 幂等初始化」,业务侧只依赖 <c>AdminCore.Core.YitIdHelper</c>。
    /// </remarks>
    public static class YitIdHelper
    {
        private static bool _initialized;

        // 生效 WorkerId(批 AP):Init 首次调用记录,缺省 1(与 Init 参数缺省、appsettings Yit:WorkerId 缺省同值)。
        // 只读暴露给 WorkerIdGuard 做占用登记——登记要的是"本进程实际在用哪个机器码",不是配置原文。
        private static int _workerId = 1;

        /// <summary>
        /// 本进程生效的雪花机器码(批 AP):<see cref="Init"/> 首次调用记录的值(未 Init 时为缺省 1)。
        /// 消费点=<c>WorkerIdGuard</c> 的占用登记与冲突检出;多实例部署该值必须各不相同。
        /// </summary>
        public static int WorkerId => _workerId;

        /// <summary>
        /// 初始化雪花生成器(进程级,幂等)。工作节点 Id 由调用方从配置读取后传入,默认 1。
        /// </summary>
        /// <param name="workerId">机器码,多实例部署必须唯一(默认位长 6,取值 0-63)。</param>
        public static void Init(int workerId = 1)
        {
            // 幂等:防止重复初始化构造第二个生成器(同机器码双实例 = 主键碰撞风险)
            lock (typeof(YitIdHelper))
            {
                if (_initialized)
                {
                    return;
                }

                _workerId = workerId;

                YitGen.YitIdHelper.SetIdGenerator(new YitGen.IdGeneratorOptions
                {
                    WorkerId = (ushort)workerId
                });

                _initialized = true;
            }
        }

        /// <summary>
        /// 生成下一个雪花 ID(转发 Yitter;调用前须已 Init,未初始化上游抛异常)。
        /// </summary>
        public static long NextId()
        {
            return YitGen.YitIdHelper.NextId();
        }
    }
}
