using System.Text;
using System.Text.RegularExpressions;
using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.CodeGen.Dto;
using AdminCore.Core.Services.Database;
using AdminCore.Core.Services.Database.Dto;

namespace AdminCore.Core.Services.CodeGen
{
    /// <summary>
    /// 代码生成服务(批 AI 6.1,**零新依赖自建模板**):按库表结构生成 实体类 / Dto 组 / 服务骨架 三段文本。
    /// </summary>
    /// <remarks>
    /// **关键裁决:只返回文本,不写盘**——参照仓 `CreateEntity`/`CreateSeedData` 直接把生成物写进**源码目录**
    /// (Web 端点写服务器源码 = 高危面:可被越权者改写代码)。本仓只做"预览+复制":端点零文件系统副作用,
    /// 生成物由开发者人工落到目标工程并按本仓约定审阅(与"业务代码只写 Business"的纪律相容)。
    /// WHY 自建模板而非引模板引擎:模板需求极简(占位替换),引 Scriban/Razor 属重依赖;本仓纪律＝最小依赖。
    /// 复用优先(#11):表清单/列结构直接复用 `SysDatabaseService` 的只读查询(已含参数化与表不存在语义),
    /// 不重造一套系统目录 SQL。
    /// 生成口径:主键列(Id)与基类审计列**跳过**(由 EntityBase/EntityTenant 基类提供;TenantId 在 EntityTenant);
    /// **基类缺省按表结构推断**(含 TenantId 列→EntityTenant,否则 EntityBase;显式选择失配时 warnings 标注,批 BF);
    /// 列类型未知 → 回落 string **并在 warnings 标注**;密码族列进入行输出时 warnings 点名(批 BF)。
    /// </remarks>
    [Service]
    public class CodeGenService
    {
        /// <summary>基类审计列(与 SysRoleTableService/实体基座口径一致;生成实体时跳过)。</summary>
        private static readonly HashSet<string> AuditColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "createtime", "createuserid", "createusername", "createorgid",
            "updatetime", "updateuserid", "updateusername", "updateorgid",
            "deletetime", "isdelete", "tenantid"
        };

        /// <summary>敏感列名(密码族,大小写不敏感):列名为敏感词**本身/前缀/后缀**(如 Password/PasswordMd5/ProxyPassword/SecretCipher);中缀复合词(如 LastChangePasswordTime)不点名。行输出含其时 warning 点名(批 BF,哈希不得回传前端)。</summary>
        private static readonly Regex SensitiveColumnPattern =
            new(@"^(?:password|pwd|salt|secret)\w*|\w*(?:password|pwd|salt|secret)$", RegexOptions.IgnoreCase);

        private readonly SysDatabaseService _databaseService;

        public CodeGenService(SysDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        /// <summary>可选表清单(复用数据库管理的只读表清单;keyword 模糊)。</summary>
        public Task<List<DbTableOutput>> GetTableListAsync(string? keyword)
            => _databaseService.ListTablesAsync(keyword);

        /// <summary>
        /// 生成预览(实体类 / Dto 组 / 服务骨架三段文本 + warnings)。表不存在 → 业务异常(复用列结构查询语义)。
        /// </summary>
        public async Task<CodeGenPreviewOutput> PreviewAsync(CodeGenInput input)
        {
            var table = input.TableName?.Trim();
            if (string.IsNullOrWhiteSpace(table))
            {
                throw AppException.Of(ErrorCodes.CodeGenTableRequired);
            }

            var columns = await _databaseService.ListColumnsAsync(table!);
            var prefix = input.RemovePrefix?.Trim() ?? "sys_";
            var className = string.IsNullOrWhiteSpace(input.ClassName)
                ? ToPascal(RemovePrefix(table!, prefix))
                : input.ClassName!.Trim();
            var moduleName = string.IsNullOrWhiteSpace(input.ModuleName) ? "Business" : input.ModuleName!.Trim();

            var warnings = new List<string>();

            // 批 BF:基类裁决——表带 TenantId 列而缺省/显式选了 EntityBase ⇒ 租户列静默丢失(25/40 张表命中,
            // 真机取证),且生成实体不实现 ITenantIdFilter(不参与租户过滤)。本框架约定"业务实体一律继承 EntityTenant"
            // (见 EntityTenant.cs remarks)⇒ 缺省按表结构推断;显式选择失配时给 warning(保留人工裁决权)。
            var hasTenantColumn = columns.Any(c => string.Equals(c.ColumnName, "tenantid", StringComparison.OrdinalIgnoreCase));
            var baseType = string.IsNullOrWhiteSpace(input.BaseType)
                ? (hasTenantColumn ? "EntityTenant" : "EntityBase")
                : input.BaseType!.Trim();
            if (hasTenantColumn && baseType == "EntityBase")
            {
                warnings.Add("表含 TenantId 列但所选基类 EntityBase 未映射该列:租户归属将静默丢失,且生成实体不参与租户过滤(本框架业务实体约定继承 EntityTenant)。");
            }
            else if (!hasTenantColumn && baseType == "EntityTenant")
            {
                warnings.Add("基类 EntityTenant 映射 TenantId 列但本表无该列:生成实体的插入/查询将报缺列(请改选 EntityBase 或人工处理映射)。");
            }

            var businessColumns = new List<(DbColumnOutput Column, string Property, string CsType)>();

            foreach (var column in columns)
            {
                if (AuditColumns.Contains(column.ColumnName))
                {
                    continue; // 主键/审计列由 EntityBase/EntityTenant 基类提供
                }

                var property = ToPascal(column.ColumnName);
                var csType = MapType(column.DataType, column.IsNullable, column.MaxLength, column.ColumnName, warnings);
                businessColumns.Add((column, property, csType));
            }

            if (businessColumns.Count == 0)
            {
                warnings.Add("该表除主键/审计列外没有业务列,生成的实体为空壳");
            }

            // 批 BF:密码族列进入行输出(Output record 全量拷贝业务列)——列表投影若照抄会把密码哈希回传前端,
            // 生成器不替人裁剪字段,但必须 warning 点名(与"未知类型回落"同一告警面)。
            var sensitive = businessColumns
                .Where(b => SensitiveColumnPattern.IsMatch(b.Column.ColumnName))
                .Select(b => b.Property)
                .ToList();
            if (sensitive.Count > 0)
            {
                warnings.Add($"敏感列(密码族) {string.Join("、", sensitive)} 已进入行输出 {className}Output:实现列表投影时务必排除,勿把密码哈希回传前端。");
            }

            var entityCode = BuildEntity(table!, className, baseType, businessColumns);
            var dtoCode = BuildDtos(className, businessColumns);
            var serviceCode = BuildService(table!, className, moduleName, businessColumns);

            return new CodeGenPreviewOutput(table!, className, entityCode, dtoCode, serviceCode, warnings);
        }

        /// <summary>实体类文本。</summary>
        private static string BuildEntity(string table, string className, string baseType,
            List<(DbColumnOutput Column, string Property, string CsType)> columns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using AdminCore.Core.Attributes;");
            sb.AppendLine("using SqlSugar;");
            sb.AppendLine();
            sb.AppendLine("namespace AdminCore.Core.Entities");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{table}(由代码生成器生成,请人工审阅后落入目标工程)。</summary>");
            sb.AppendLine("    [SugarTable(\"" + table + "\")]");
            sb.AppendLine("    [IncreTable]");
            sb.AppendLine($"    public class {className} : {baseType}");
            sb.AppendLine("    {");

            foreach (var (column, property, csType) in columns)
            {
                var desc = string.IsNullOrWhiteSpace(column.Comment) ? property : column.Comment!;
                var length = column.MaxLength is > 0 && csType == "string" ? $", Length = {column.MaxLength}" : string.Empty;
                var nullable = column.IsNullable ? ", IsNullable = true" : string.Empty;
                sb.AppendLine($"        /// <summary>{desc}</summary>");
                sb.AppendLine($"        [SugarColumn(ColumnDescription = \"{desc}\"{length}{nullable})]");
                // 批内自抓修正(C):可空 string 此前生成非空 string(却写 IsNullable=true,口径矛盾)
                // ⇒ 与其它可空类型统一:可空即带 '?'(string?/long?/DateTime? …)
                var typeText = column.IsNullable && !csType.EndsWith("?") ? csType + "?" : csType;
                sb.AppendLine($"        public {typeText} {property} {{ get; set; }}");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Dto 组文本(分页入参 + 输出)。</summary>
        private static string BuildDtos(string className, List<(DbColumnOutput Column, string Property, string CsType)> columns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using AdminCore.Core.Models;");
            sb.AppendLine();
            sb.AppendLine("namespace AdminCore.Core.Services." + className + ".Dto");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>分页请求 {keyword?, pageIndex, pageSize}(部分更新/过滤口径按需调整)。</summary>");
            sb.AppendLine($"    public sealed record Page{className}Input(string? Keyword, int PageIndex = 1, int PageSize = 10);");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>行输出(列表投影;大字段请按需排除)。</summary>");
            // 批内自抓修正(D):Output/Add 此前一律非空,与实体/Update 口径不一致 ⇒ 按列可空性带 '?'
            var props = string.Join(", ", columns.Select(c => $"{c.CsType}{(c.Column.IsNullable && !c.CsType.EndsWith("?") ? "?" : string.Empty)} {c.Property}"));
            sb.AppendLine($"    public sealed record {className}Output(long Id, {props}, DateTime? CreateTime);");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>新增请求(必填校验请按业务补 [Required] 等特性)。</summary>");
            sb.AppendLine($"    public sealed record Add{className}Input({props});");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>更新请求(**部分更新:null=不改**,与本仓约定 #9 一致)。</summary>");
            var updateProps = string.Join(", ", columns.Select(c => $"{c.CsType}? {c.Property}"));
            sb.AppendLine($"    public sealed record Update{className}Input(long Id, {updateProps});");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>服务骨架文本(Page/Detail/Add/Update/Delete 五方法)。</summary>
        private static string BuildService(string table, string className, string moduleName,
            List<(DbColumnOutput Column, string Property, string CsType)> columns)
        {
            // 批内自抓修正(A):此前无 string 列时回落 "Id" ⇒ 生成 x.Id!.Contains(keyword!)(Id 是 long)⇒ **编译不过**
            // (本库实测命中 sys_role_menu/sys_notice_user/sys_role_org/sys_user_role/sys_file_content 五张表)。
            // 修正:找不到 string 列就不生成关键字过滤行,改为 TODO 注释提示人工补搜索条件。
            string? firstSearchable = columns.FirstOrDefault(c => c.CsType == "string").Property;
            var sb = new StringBuilder();
            sb.AppendLine("using AdminCore.Core.Attributes;");
            sb.AppendLine("using AdminCore.Core.Entities;");
            sb.AppendLine("using AdminCore.Core.Exceptions;");
            sb.AppendLine("using AdminCore.Core.Models;");
            sb.AppendLine("using AdminCore.Core.Repository;");
            sb.AppendLine($"using AdminCore.Core.Services.{className}.Dto;");
            sb.AppendLine("using SqlSugar;");
            sb.AppendLine();
            sb.AppendLine("namespace " + moduleName + ".Services." + className);
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{table} 业务服务骨架(代码生成器产出:分页/详情/新增/更新/删除;请补业务规则与权限键)。</summary>");
            sb.AppendLine($"    /// <remarks>生成本骨架时未含端点/菜单:落码后请按硬性约定 #14 一并补控制器 + 菜单按钮种子 + 前端。</remarks>");
            sb.AppendLine("    [Service]");
            sb.AppendLine($"    public class {className}Service");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly ISqlSugarRepository<{className}> _repo;");
            sb.AppendLine();
            sb.AppendLine($"        public {className}Service(ISqlSugarRepository<{className}> repo) => _repo = repo;");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>分页(条件/排序按业务补;列表不载大字段)。</summary>");
            sb.AppendLine($"        public async Task<PageResult<{className}Output>> PageAsync(Page{className}Input input)");
            sb.AppendLine("        {");
            sb.AppendLine("            var keyword = input.Keyword?.Trim();");
            sb.AppendLine("            RefAsync<int> total = 0;");
            sb.AppendLine("            var list = await _repo.AsQueryable()");
            if (firstSearchable is not null)
            {
                sb.AppendLine($"                .WhereIF(!string.IsNullOrWhiteSpace(keyword), x => x.{firstSearchable}!.Contains(keyword!))");
            }
            else
            {
                sb.AppendLine("                // TODO 该表没有字符串列:请按业务补搜索条件(生成器未生成关键字过滤,避免对非字符串列调用 Contains)");
            }

            sb.AppendLine("                .OrderByDescending(x => x.Id)");
            sb.AppendLine("                .ToPageListAsync(Math.Max(1, input.PageIndex), Math.Clamp(input.PageSize, 1, AdminCore.Core.Models.PageConst.MaxPageSize), total);");
            sb.AppendLine();
            sb.AppendLine("            // TODO 投影为 " + className + "Output(建议下推到查询侧,避免载入大字段)");
            sb.AppendLine($"            return new PageResult<{className}Output> {{ Total = total, List = new List<{className}Output>() }};");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>详情(不存在 → 业务异常)。</summary>");
            sb.AppendLine($"        public async Task<{className}> GetDetailAsync(long id)");
            sb.AppendLine("        {");
            sb.AppendLine("            var row = await _repo.GetFirstAsync(x => x.Id == id);");
            sb.AppendLine($"            return row ?? throw new AppException(\"{className} 不存在\");");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>新增(必填校验请按业务补)。</summary>");
            sb.AppendLine($"        public async Task<long> AddAsync(Add{className}Input input)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var entity = new {className} {{ /* TODO 字段映射 */ }};");
            sb.AppendLine("            await _repo.InsertAsync(entity);");
            sb.AppendLine("            return entity.Id;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>更新(**部分更新:null=不改**,约定 #9)。</summary>");
            sb.AppendLine($"        public async Task UpdateAsync(Update{className}Input input)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var entity = await _repo.GetFirstAsync(x => x.Id == input.Id) ?? throw new AppException(\"{className} 不存在\");");
            sb.AppendLine("            // TODO 逐字段判 null 后赋值(禁止整对象覆盖)");
            sb.AppendLine("            await _repo.UpdateAsync(entity);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>删除(软删;存在性校验防误报成功)。</summary>");
            sb.AppendLine("        public async Task DeleteAsync(long id)");
            sb.AppendLine("        {");
            sb.AppendLine($"            _ = await _repo.GetFirstAsync(x => x.Id == id) ?? throw new AppException(\"{className} 不存在\");");
            sb.AppendLine("            await _repo.DeleteByIdAsync(id);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>PG 类型 → C# 类型(未知类型回落 string 并记 warning)。</summary>
        private static string MapType(string dataType, bool isNullable, int? maxLength, string column, List<string> warnings)
        {
            var type = (dataType ?? string.Empty).ToLowerInvariant();
            if (type.StartsWith("character varying") || type.StartsWith("character") || type.Contains("text")
                || type.Contains("json") || type.Contains("xml"))
            {
                return "string";
            }

            // 批内自抓修正(B):PG 的 format_type 对带精度/标度声明产出 "numeric(18,4)"/"timestamp(6) with time zone"
            // ⇒ 此前**精确匹配**会落未知分支回落 string(静默降级);改为按**族名**包含/前缀匹配。
            return type switch
            {
                _ when type.StartsWith("bigint") || type.StartsWith("int8") => "long",
                // WHY 这些用**精确**匹配:前缀匹配会把区间/范围类型误吞(实测 "interval".StartsWith("int")=true ⇒ 被错映射为 int;
                // 同理 "daterange" 会被 "date" 前缀吞掉)——int 家族与 date 本身不带精度修饰,精确即可
                _ when type is "integer" or "int" or "int4" or "smallint" or "int2" => "int",
                _ when type is "boolean" or "bool" => "bool",
                _ when type.StartsWith("timestamp") || type == "timestamptz" || type == "date" => "DateTime",
                _ when type.StartsWith("numeric") || type.StartsWith("decimal") || type.StartsWith("money") => "decimal",
                _ when type.StartsWith("double precision") || type.StartsWith("float8") => "double",
                _ when type.StartsWith("real") || type.StartsWith("float4") => "float",
                _ when type.StartsWith("uuid") => "Guid",
                _ when type.StartsWith("bytea") => "byte[]",
                _ => Unknown(type, column, warnings)
            };

            static string Unknown(string type, string column, List<string> warnings)
            {
                warnings.Add($"列 {column} 的类型 {type} 未在映射表中,已回落 string(请人工确认)");
                return "string";
            }
        }

        /// <summary>表名去前缀 + PascalCase(如 sys_user → User)。</summary>
        private static string RemovePrefix(string table, string prefix)
            => !string.IsNullOrEmpty(prefix) && table.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? table[prefix.Length..]
                : table;

        /// <summary>下划线/短横线分隔名 → PascalCase(如 user_name → UserName)。</summary>
        private static string ToPascal(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            var parts = name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                sb.Append(char.ToUpperInvariant(part[0])).Append(part.Length > 1 ? part[1..] : string.Empty);
            }

            return sb.Length > 0 ? sb.ToString() : name;
        }
    }
}