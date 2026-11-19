using AdminCore.Core.Localization;

namespace AdminCore.Core.Exceptions;

/// <summary>
/// 稳定错误码(批 AQ):与 <c>Lang.*.json</c> / <see cref="LocalizationDefaults"/> 的资源键**同源**。
/// 口径(用户拍板 2026-09-26):
/// ① 客户端按 <see cref="AppException.ErrorCode"/> **编程判断**(永不变);
/// ② **解释文案**由全局出口 ExceptionMiddleware 按**请求语言**渲染(不在业务代码写死中文);
/// ③ 响应体保留 <c>code</c>(int,兼容既有判断) + <c>errCode</c>(string,新增) + <c>msg</c>(本地化解释)。
/// 命名约定 <c>域.语义</c>;**新增错误码必须同批补 zh/en 两组资源键**(见 框架使用指南 §错误码)。
/// </summary>
public static class ErrorCodes
{
    /* ── 通用 ─────────────────────────────── */
    /// <summary>参数不合法</summary>
    public const string ParamInvalid = "common.param_invalid";
    /// <summary>资源不存在</summary>
    public const string NotFound = "common.not_found";
    /// <summary>操作失败</summary>
    public const string OperationFailed = "common.operation_failed";
    /// <summary>未登录或 Token 已失效(401)</summary>
    public const string Unauthorized = "common.unauthorized";
    /// <summary>没有权限执行该操作(403)</summary>
    public const string Forbidden = "common.forbidden";
    /// <summary>数据状态冲突</summary>
    public const string Conflict = "common.conflict";
    /// <summary>服务器内部错误</summary>
    public const string InternalError = "common.internal_error";
    /// <summary>系统繁忙,请稍后重试(未处理异常的对外文案)</summary>
    public const string Busy = "common.busy";
    /// <summary>开放接口端点缺 AuthenticationSchemes 声明</summary>
    public const string OpenApiSchemeMissing = "common.open_api_scheme_missing";
    /// <summary>开放接口认证失败</summary>
    public const string OpenApiAuthFailed = "common.open_api_auth_failed";

    /* ── 通用导入(批 CP)──────────────────── */
    /// <summary>导入文件格式非法(仅支持 .xlsx/.csv)</summary>
    public const string ImportFileFormatInvalid = "import.file_format_invalid";
    /// <summary>导入行数超过上限(单次最多 5000 行)</summary>
    public const string ImportRowLimitExceeded = "import.row_limit_exceeded";
    /// <summary>导入文件为空</summary>
    public const string ImportFileEmpty = "import.file_empty";

    /* ── 鉴权 ─────────────────────────────── */
    /// <summary>账号或密码错误</summary>
    public const string AuthLoginFailed = "auth.login_failed";
    /// <summary>登录失败次数过多</summary>
    public const string AuthTooManyAttempts = "auth.too_many_attempts";
    /// <summary>账号已被锁定</summary>
    public const string AuthAccountLocked = "auth.account_locked";
    /// <summary>账号已停用</summary>
    public const string AuthAccountDisabled = "auth.account_disabled";
    /// <summary>动态码错误或已过期</summary>
    public const string AuthOtpInvalid = "auth.otp_invalid";
    /// <summary>校验码验证失败</summary>
    public const string AuthOtpVerifyFailed = "auth.otp_verify_failed";
    /// <summary>动态码已被使用(防重放:码正确但时间步已用过)——与"码错误"分开,便于正确提示(批 AX)</summary>
    public const string AuthOtpReused = "auth.otp_reused";
    /// <summary>登录验证已过期,请重新登录(挑战失效)</summary>
    public const string AuthChallengeExpired = "auth.challenge_expired";
    /// <summary>登录状态已过期,请重新登录</summary>
    public const string AuthTokenExpired = "auth.token_expired";
    /// <summary>无权访问该资源</summary>
    public const string AuthNoPermission = "auth.no_permission";

    /* ── 用户域 ───────────────────────────── */
    /// <summary>用户不存在</summary>
    public const string UserNotFound = "user.not_found";
    /// <summary>账号不能为空</summary>
    public const string UserAccountRequired = "user.account_required";
    /// <summary>账号与密码不能为空</summary>
    public const string UserCredentialsRequired = "user.credentials_required";
    /// <summary>账号已存在({account})</summary>
    public const string UserAccountExists = "user.account_exists";
    /// <summary>机构不存在({id})</summary>
    public const string UserOrgNotFound = "user.org_not_found";
    /// <summary>职位不存在({id})</summary>
    public const string UserPosNotFound = "user.pos_not_found";
    /// <summary>角色不存在({ids})</summary>
    public const string UserRoleNotFound = "user.role_not_found";
    /// <summary>超管账号不允许修改</summary>
    public const string UserSuperAdminReadonly = "user.super_admin_readonly";
    /// <summary>超管账号不允许删除</summary>
    public const string UserSuperAdminUndeletable = "user.super_admin_undeletable";
    /// <summary>不允许删除当前登录账号</summary>
    public const string UserSelfDeleteForbidden = "user.self_delete_forbidden";
    /// <summary>原密码与新密码不能为空</summary>
    public const string UserOldNewPasswordRequired = "user.old_new_password_required";
    /// <summary>新密码不能为空</summary>
    public const string UserNewPasswordRequired = "user.new_password_required";
    /// <summary>原密码不正确</summary>
    public const string UserOldPasswordWrong = "user.old_password_wrong";
    /// <summary>新密码不满足强度要求</summary>
    public const string UserPasswordWeak = "user.password_weak";
    /// <summary>新密码不能与最近密码重复</summary>
    public const string UserPasswordReused = "user.password_reused";

    /* ── 角色(示范域复用既有键) ─────────────── */
    /// <summary>角色不存在({ids})</summary>
    public const string RoleNotFound = "role.not_found";

    /* ── 租户(示范域复用:登录租户校验) ─────── */
    /// <summary>租户不存在</summary>
    public const string TenantNotFound = "tenant.not_found";
    /// <summary>租户已停用</summary>
    public const string TenantDisabled = "tenant.disabled";
    /// <summary>租户已过期</summary>
    public const string TenantExpired = "tenant.expired";

    /* ── 角色 / 字段权限 / 菜单 / 配置(RBAC·系统域,批 AR) ── */
    /// <summary>角色名称与编码不能为空</summary>
    public const string RoleNameCodeRequired = "role.name_code_required";
    /// <summary>角色编码已存在({code})</summary>
    public const string RoleCodeExists = "role.code_exists";
    /// <summary>数据范围取值非法</summary>
    public const string RoleDataScopeInvalid = "role.datascope_invalid";
    /// <summary>角色名称不能为空</summary>
    public const string RoleNameRequired = "role.name_required";
    /// <summary>内置角色不允许删除</summary>
    public const string RoleBuiltinUndeletable = "role.builtin_undeletable";
    /// <summary>菜单不存在({ids})</summary>
    public const string RoleMenuNotFound = "role.menu_not_found";
    /// <summary>角色编码已存在,改后重试({code})</summary>
    public const string RoleCodeExistsUpdate = "role.code_exists_update";
    /// <summary>机构不存在({ids})</summary>
    public const string RoleOrgNotFound = "role.org_not_found";
    /// <summary>无法解析操作者身份</summary>
    public const string RoleOperatorUnresolved = "role.operator_unresolved";
    /// <summary>越权:所选机构超出自身数据范围</summary>
    public const string RoleOrgOutOfScope = "role.org_out_of_scope";
    /// <summary>授权项格式非法({item})</summary>
    public const string FieldScopeGrantItemInvalid = "field_scope.grant_item_invalid";
    /// <summary>表名不能为空</summary>
    public const string FieldScopeTableRequired = "field_scope.table_required";
    /// <summary>表不在字段权限可配置范围内({table})</summary>
    public const string FieldScopeTableNotConfigurable = "field_scope.table_not_configurable";
    /// <summary>父节点不存在</summary>
    public const string MenuParentNotFound = "menu.parent_not_found";
    /// <summary>菜单不存在</summary>
    public const string MenuNotFound = "menu.not_found";
    /// <summary>父节点不能是自身</summary>
    public const string MenuParentSelf = "menu.parent_self";
    /// <summary>存在子节点,不允许删除</summary>
    public const string MenuHasChildren = "menu.has_children";
    /// <summary>类型仅支持 1 目录/2 菜单/3 按钮</summary>
    public const string MenuTypeInvalid = "menu.type_invalid";
    /// <summary>按钮必须填写权限标识</summary>
    public const string MenuPermissionRequired = "menu.permission_required";
    /// <summary>菜单名(路由标识)已存在(全局唯一)</summary>
    public const string MenuNameExists = "menu.name_exists";
    /// <summary>父节点不能是按钮</summary>
    public const string MenuParentIsButton = "menu.parent_is_button";
    /// <summary>同父下已存在重复菜单(按钮按权限标识判重)</summary>
    public const string MenuDuplicate = "menu.duplicate";
    /// <summary>菜单状态值非法(仅 0 启用/1 停用,批 CH G28)</summary>
    public const string MenuStatusInvalid = "menu.status_invalid";
    /// <summary>配置代码不能为空</summary>
    public const string ConfigCodeRequired = "config.code_required";
    /// <summary>配置代码已存在({code})</summary>
    public const string ConfigCodeExists = "config.code_exists";
    /// <summary>配置不存在</summary>
    public const string ConfigNotFound = "config.not_found";
    /// <summary>系统内置配置不允许删除</summary>
    public const string ConfigBuiltinUndeletable = "config.builtin_undeletable";

    /* ── 字典 / 流水号 / 枚举 / 列设置 / 动态查询(数据·工具域,批 AR-2) ── */
    /// <summary>字典类型名称与编码不可为空</summary>
    public const string DictTypeNameCodeRequired = "dict.type_name_code_required";
    /// <summary>字典类型不存在</summary>
    public const string DictTypeNotFound = "dict.type_not_found";
    /// <summary>字典类型名称不可为空</summary>
    public const string DictTypeNameRequired = "dict.type_name_required";
    /// <summary>类型 Id 与 Code 至少提供一个</summary>
    public const string DictTypeIdCodeRequired = "dict.type_id_code_required";
    /// <summary>字典值 Code/Value/Label 不可为空</summary>
    public const string DictDataFieldsRequired = "dict.data_fields_required";
    /// <summary>字典值不存在</summary>
    public const string DictDataNotFound = "dict.data_not_found";
    /// <summary>流水号配置不存在</summary>
    public const string SerialConfigNotFound = "serial.config_not_found";
    /// <summary>状态只能为 0 或 1</summary>
    public const string SerialStatusInvalid = "serial.status_invalid";
    /// <summary>流水号类型重复注册</summary>
    public const string SerialTypeDuplicated = "serial.type_duplicated";
    /// <summary>枚举类型名不能为空</summary>
    public const string EnumTypeRequired = "enum.type_required";
    /// <summary>实体名与字段名不能为空</summary>
    public const string EnumEntityFieldRequired = "enum.entity_field_required";
    /// <summary>表格Id不能为空</summary>
    public const string ColumnCustomGridIdRequired = "column_custom.grid_id_required";
    /// <summary>未获取到当前用户身份</summary>
    public const string ColumnCustomUserUnresolved = "column_custom.user_unresolved";
    /// <summary>关键字需配搜索字段</summary>
    public const string DynamicQuerySearchByRequired = "dynamic_query.search_by_required";

    /* ── 插值形式补充(批 AR-2 补:统计口径含 $" 形式) ── */
    /// <summary>字典类型编码已存在({code})</summary>
    public const string DictTypeCodeExists = "dict.type_code_exists";
    /// <summary>类型下值编码已存在({type},{code})</summary>
    public const string DictTypeCodeExistsUnder = "dict.type_code_exists_under";
    /// <summary>同类型下值编码已存在({code})</summary>
    public const string DictDataCodeExists = "dict.data_code_exists";
    /// <summary>流水号类型未注册({type})</summary>
    public const string SerialTypeUnregistered = "serial.type_unregistered";
    /// <summary>流水号类型已存在({type})</summary>
    public const string SerialTypeExists = "serial.type_exists";
    /// <summary>配置不存在或已停用({type})</summary>
    public const string SerialConfigUnavailable = "serial.config_unavailable";
    /// <summary>流水号超上限({type},{max})</summary>
    public const string SerialOutOfRange = "serial.out_of_range";
    /// <summary>流水号推进失败({type})</summary>
    public const string SerialAdvanceFailed = "serial.advance_failed";
    /// <summary>无效插槽({slot})</summary>
    public const string SerialSlotInvalid = "serial.slot_invalid";
    /// <summary>枚举类型不存在({name})</summary>
    public const string EnumTypeNotFound = "enum.type_not_found";
    /// <summary>字段不存在或非枚举({entity},{field})</summary>
    public const string EnumFieldNotEnum = "enum.field_not_enum";
    /// <summary>列配置数据损坏({detail})</summary>
    public const string ColumnCustomDataCorrupted = "column_custom.data_corrupted";
    /// <summary>排序字段不在白名单({field})</summary>
    public const string DynamicQueryOrderByNotAllowed = "dynamic_query.order_by_not_allowed";
    /// <summary>搜索字段不在白名单({field})</summary>
    public const string DynamicQueryFieldNotAllowed = "dynamic_query.field_not_allowed";

    /* ── 机构 / 职位 / 租户(组织·租户域,批 AR-3) ── */
    /// <summary>机构名称不可为空</summary>
    public const string OrgNameRequired = "org.name_required";
    /// <summary>父节点不存在</summary>
    public const string OrgParentNotFound = "org.parent_not_found";
    /// <summary>机构不存在</summary>
    public const string OrgNotFound = "org.not_found";
    /// <summary>父节点不能是自身</summary>
    public const string OrgParentSelf = "org.parent_self";
    /// <summary>存在子节点,不允许删除</summary>
    public const string OrgHasChildren = "org.has_children";
    /// <summary>存在关联用户,不允许删除</summary>
    public const string OrgHasUsers = "org.has_users";
    /// <summary>机构编码已存在({code})</summary>
    public const string OrgCodeExists = "org.code_exists";
    /// <summary>同父下已存在同名机构</summary>
    public const string OrgDuplicate = "org.duplicate";
    /// <summary>父节点不能是自己的后代(树会成环)</summary>
    public const string OrgParentDescendant = "org.parent_descendant";
    /// <summary>顶级机构仅超管可创建(非超管须挂自身范围内机构)</summary>
    public const string OrgTopForbidden = "org.top_forbidden";
    /// <summary>越权:父机构超出操作者自身数据范围</summary>
    public const string OrgParentOutOfScope = "org.parent_out_of_scope";
    /// <summary>越权:机构超出操作者自身数据范围</summary>
    public const string OrgOutOfScope = "org.out_of_scope";
    /// <summary>改挂顶级仅超管可操作</summary>
    public const string OrgTopMoveForbidden = "org.top_move_forbidden";
    /// <summary>仅创建者或超管可操作</summary>
    public const string PosCreatorOnly = "pos.creator_only";
    /// <summary>职位名称与编码不可为空</summary>
    public const string PosNameCodeRequired = "pos.name_code_required";
    /// <summary>职位不存在</summary>
    public const string PosNotFound = "pos.not_found";
    /// <summary>职位编码已存在({code})</summary>
    public const string PosCodeExists = "pos.code_exists";
    /// <summary>职位已关联用户({name})</summary>
    public const string PosHasUsers = "pos.has_users";
    /// <summary>租户名称不能为空</summary>
    public const string TenantNameRequired = "tenant.name_required";
    /// <summary>默认租户不可删除</summary>
    public const string TenantDefaultUndeletable = "tenant.default_undeletable";
    /// <summary>默认租户恒用主库</summary>
    public const string TenantDefaultMainDb = "tenant.default_main_db";
    /// <summary>默认租户不可停用</summary>
    public const string TenantDefaultUndisableable = "tenant.default_undisableable";
    /// <summary>默认租户不可修改(批 BM:防平台自锁——改过期时间/连接串与禁删/禁停同类风险面)</summary>
    public const string TenantDefaultUneditable = "tenant.default_uneditable";
    /// <summary>租户已绑定开放接口,不可删除(批 BM:参照 D1031 口径,防绑定悬空)</summary>
    public const string TenantOpenAccessBound = "tenant.open_access_bound";

    /* ── 任务 / 打印 / 公告 / 消息 / 日志 / 开放接口(业务功能域,批 AR-4) ── */
    /// <summary>任务名不能为空</summary>
    public const string JobNameRequired = "job.name_required";
    /// <summary>执行间隔须为正整数(秒)</summary>
    public const string JobIntervalInvalid = "job.interval_invalid";
    /// <summary>任务未注册代码({name})</summary>
    public const string JobCodeUnregistered = "job.code_unregistered";
    /// <summary>任务已存在({name})</summary>
    public const string JobExists = "job.exists";
    /// <summary>任务不存在</summary>
    public const string JobNotFound = "job.not_found";
    /// <summary>立即执行被拒绝</summary>
    public const string JobRunRejected = "job.run_rejected";
    /// <summary>打印模板不存在</summary>
    public const string PrintNotFound = "print.not_found";
    /// <summary>模板名称不能为空</summary>
    public const string PrintNameRequired = "print.name_required";
    /// <summary>模板内容不能为空</summary>
    public const string PrintContentRequired = "print.content_required";
    /// <summary>同名模板已存在({name})</summary>
    public const string PrintNameExists = "print.name_exists";
    /// <summary>已发布公告不可删除</summary>
    public const string NoticePublishedUndeletable = "notice.published_undeletable";
    /// <summary>通知公告不存在</summary>
    public const string NoticeNotFound = "notice.not_found";
    /// <summary>仅发布人可操作</summary>
    public const string NoticeCreatorOnly = "notice.creator_only";
    /// <summary>消息内容不能为空</summary>
    public const string MessageContentRequired = "message.content_required";
    /// <summary>接收人集合不能为空</summary>
    public const string MessageReceiversRequired = "message.receivers_required";
    /// <summary>操作日志不存在</summary>
    public const string LogOpNotFound = "log.op_not_found";
    /// <summary>差异日志不存在</summary>
    public const string LogDiffNotFound = "log.diff_not_found";
    /// <summary>异常日志不存在</summary>
    public const string LogExNotFound = "log.ex_not_found";
    /// <summary>Http 日志不存在</summary>
    public const string LogHttpNotFound = "log.http_not_found";
    /// <summary>开放接口身份不存在</summary>
    public const string OpenAccessNotFound = "open_access.not_found";
    /// <summary>身份标识已存在({accessKey})</summary>
    public const string OpenAccessAccessKeyExists = "open_access.access_key_exists";
    /// <summary>绑定用户不存在(S-G D1.4 片 a:写侧校验与鉴权侧 `Status==0` 谓词同口径,不再"写侧宽容+鉴权侧兜底")</summary>
    public const string OpenAccessBindUserNotFound = "open_access.bind_user_not_found";
    /// <summary>绑定用户已停用(与鉴权侧 BindUserValid 同源口径:停用用户不得新建/改绑到开放接口身份)</summary>
    public const string OpenAccessBindUserDisabled = "open_access.bind_user_disabled";
    /// <summary>禁止绑定超级管理员(S-G D1.4 片 a:机器身份不得继承平台最高权限,否则密钥泄露=全平台失守)</summary>
    public const string OpenAccessBindUserSuperAdmin = "open_access.bind_user_super_admin";
    /// <summary>绑定用户与目标租户不匹配(S-G D1.4 片 b:机器身份的租户归属必须与绑定用户同租户)</summary>
    public const string OpenAccessBindTenantMismatch = "open_access.bind_tenant_mismatch";

    /* ── 文件 / 数据库 / 租户库 / 出口杂项(收尾组,批 AR-5) ── */
    /// <summary>文件不能为空</summary>
    public const string FileContentRequired = "file.content_required";
    /// <summary>文件类型不允许({contentType})</summary>
    public const string FileTypeNotAllowed = "file.type_not_allowed";
    /// <summary>文件大小超限({maxKb}KB)</summary>
    public const string FileSizeExceeded = "file.size_exceeded";
    /// <summary>文件后缀不允许({fileName})</summary>
    public const string FileSuffixNotAllowed = "file.suffix_not_allowed";
    /// <summary>文件不存在</summary>
    public const string FileNotFound = "file.not_found";
    /// <summary>文件不存在({ids})</summary>
    public const string FileNotFoundIds = "file.not_found_ids";
    /// <summary>文件 MD5 校验失败</summary>
    public const string FileMd5Mismatch = "file.md5_mismatch";
    /// <summary>文件内容不存在</summary>
    public const string FileContentMissing = "file.content_missing";
    /// <summary>表不存在或不可见({table})</summary>
    public const string DatabaseTableNotFound = "database.table_not_found";
    /// <summary>表名不能为空</summary>
    public const string DatabaseTableRequired = "database.table_required";
    /// <summary>表名过长</summary>
    public const string DatabaseTableNameTooLong = "database.table_name_too_long";
    /// <summary>租户不存在({tenantId})</summary>
    public const string TenantDbTenantNotFound = "tenant_db.tenant_not_found";
    /// <summary>未配置独立库连接串({name})</summary>
    public const string TenantDbConnectionMissing = "tenant_db.connection_missing";
    /// <summary>租户库名不合法({dbName})</summary>
    public const string TenantDbNameInvalid = "tenant_db.db_name_invalid";
    /// <summary>创建租户库失败({dbName},{reason})</summary>
    public const string TenantDbCreateFailed = "tenant_db.create_failed";
    /// <summary>加密已启用但缺 CryptogramHelper</summary>
    public const string TenantDbEncryptHelperMissing = "tenant_db.encrypt_helper_missing";
    /// <summary>连接串解密失败</summary>
    public const string TenantDbDecryptFailed = "tenant_db.decrypt_failed";
    /// <summary>接口文档已关闭</summary>
    public const string CommonApiDocDisabled = "common.api_doc_disabled";
    /// <summary>缺少用户标识</summary>
    public const string CommonUserIdentityMissing = "common.user_identity_missing";
    /// <summary>演示业务异常</summary>
    public const string CommonDemoForbidden = "common.demo_forbidden";
    /// <summary>表名不能为空</summary>
    public const string CodeGenTableRequired = "code_gen.table_required";
}
