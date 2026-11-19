using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 用户密码历史记录(批 A 1.11 密码安全策略:改密/重置时写入,统一 MD5 存储)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// 口径(手册 1.11):历史记录无论"历史校验"开关是否开启**总是入库**(统一 MD5)——开关只挡"重复校验",
    /// 不挡写入。三套加密并存不可混用:传输 SM2(前端)、密码字段 PBKDF2(SysUser.Password)、本表历史比对 MD5。
    /// 用途:开启"历史校验"开关后,改密/重置时比对新密码 MD5 是否落在最近 N 条历史内,命中即拒(防频繁回退旧密码)。
    /// </remarks>
    [SugarTable("sys_user_password_record", "用户密码历史")]
    [IncreTable]
    [SysTable]
    public class SysUserPasswordRecord : EntityTenant
    {
        /// <summary>用户 Id(sys_user 主键)。</summary>
        [SugarColumn(ColumnDescription = "用户Id")]
        public long UserId { get; set; }

        /// <summary>历史密码 MD5(统一 MD5 十六进制小写;明文永不落库)。</summary>
        [SugarColumn(ColumnDescription = "密码MD5", Length = 64, IsNullable = true)]
        public string? PasswordMd5 { get; set; }
    }
}
