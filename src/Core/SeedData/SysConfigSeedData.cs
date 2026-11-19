using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 系统配置种子(批 I 5.1 sys_oplog 开关):操作日志记录开关(sys_oplog=True,内置项禁删)。
    /// 为何必须种:OpLogFilter 发布前读该开关决定是否记操作日志——存量 sys_config 表无数据(批 D 手工 DDL 迁移),
    /// 无种子行时开关缺失。本仓缺失口径=fail-open(记录,裁决 docs/研究/g3_refer_research.md §9 ⑦),但种子保证
    /// 正常路径行为可被管理端显式关闭(参照口径:种子 sys_oplog=True)。
    /// 幂等:按显式主键缺行插入、已有跳过(2026-10-30 起;配置项 Value 是运行期可改数据,种子**不得覆写**,
    /// 否则每次部署重启都会静默回滚管理员修改——下游 OTP 开关事故即此);Id 取参照种子行值 1300000000141。
    /// </summary>
    [IncreSeed]
    [SeedData(9)] // 顺序 9:独立种子,取 SysTenantSeedData(顺序 8)之后的空闲位
    public class SysConfigSeedData : ISqlSugarEntitySeedData<SysConfig>
    {
        public IEnumerable<SysConfig> HasData()
        {
            return new[]
            {
                new SysConfig
                {
                    Id = 1300000000141L,
                    Name = "记录操作日志",
                    Code = Services.Config.SysConfigService.SysOpLog,
                    Value = "True",
                    GroupCode = "Default",
                    SysFlag = true,
                    Remark = "是否记录操作日志(关→OpLogFilter 不发 op_log 事件;缺失/解析失败 fail-open 记录)"
                },
                new SysConfig
                {
                    Id = 1300000000142L,
                    Name = "升级公告提示开关",
                    Code = Services.Config.SysConfigService.SysUpgrade,
                    Value = "True",
                    GroupCode = "Default",
                    SysFlag = true,
                    Remark = "是否在登录后提示升级公告(关→未读/最新查询排除 NoticeTypeEnum.Upgrade;缺失/解析失败 fail-open 提示)"
                },
                new SysConfig
                {
                    Id = 1300000000143L,
                    Name = "登录 OTP 第二道校验开关",
                    Code = Services.Config.SysConfigService.OtpEnabled,
                    Value = "True",
                    GroupCode = "Default",
                    SysFlag = true,
                    Remark = "全局登录两阶段 OTP 开关:开(True,缺省/种子)密码通过后强制 Google/Google-OTP 二次验证;关(值显式为 False,忽略大小写/首尾空白)密码通过直接签发令牌,不出挑战不要求码;缺失/任何无法解析值 fail-safe 视为开"
                },
                new SysConfig
                {
                    Id = 1300000000144L,
                    Name = "单点登录开关",
                    Code = Services.Config.SysConfigService.SingleLoginEnabled,
                    Value = "false",
                    GroupCode = "Default",
                    SysFlag = true,
                    Remark = "单点登录:开(true/1)新登录踢旧连接并递增 TokenVersion 作废旧 token(其他地方登录失效);关(false,缺省)多端并存零行为变化。注意缺省语义与 OTP 开关相反(本开关缺省关)"
                }
            };
        }
    }
}
