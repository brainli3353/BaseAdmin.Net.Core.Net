// 批 S-G 纵深期 D4.4·权限矩阵静态守卫（红线⑤门控，用户 2026-10-26 批准入门，见纵深期计划 §四）。
// 判据：反射全部控制器，凡 HTTP 写端点（POST/PUT/DELETE/PATCH）既无 [MenuPerm] 又不在下方显式豁免表 ⇒ 红。
// WHY 门控：纵深期审计的 D1.1 匿名 demo 洞（未登录可翻全库 account/realName）与 B3a 的 ResetPwd/GrantRole
//   缺超管守卫，同属"写端点漏挂权限"缺陷族；此前只有一次性手写脚本能发现，本类把它变成每拍必过的门。
// WHY 反射而非源码正则：[MenuPerm] 只能挂方法（AttributeTargets.Method，全仓 0 处类级用法），
//   端点身份由 IHttpMethodMetadata 决定（含 [ApiController] 约定路由的隐式动词），正则扫会误判。
// 零库：只反射已加载程序集，不连 PG/Redis ⇒ 已纳入 CLAUDE.md「测试分域三档·零库档」预设。
using System.Reflection;
using AdminCore.Core.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace AdminCore.Test;

/// <summary>D4.4 权限矩阵守卫：写端点必须挂 [MenuPerm] 或在显式豁免表（表内每条都要写理由）。</summary>
public class BatchSGPermMatrixGuardTest
{
    /// <summary>
    /// 显式豁免表（键＝"控制器名.方法名"）——2026-10-26 入门时反射实测全仓 81 个写端点，
    /// 除下表 9 处外全部挂 [MenuPerm]（门控首跑实锤：源码正则摸底只报出 6 处，反射口径又抓出 3 处，
    /// 正是本门类改用反射而非正则的理由）。逐条理由：
    /// ① AuthController.Login/LoginOtp/Logout：登录链本身，登录前无身份可判权限（挂了会死锁登录）；
    /// ② DemoController.Validate：示例端点，B1a 已类级 [Authorize] ⇒ 口径＝登录即可，无独立菜单权限键；
    /// ③ SysColumnCustomController.Store/Reset：用户列个性化自助面（只读写自己的偏好），参照公告自读面豁免先例；
    /// ④ SysNoticeController.SetRead／SysUserController.UpdatePassword：写自己那一行的自助面（服务层 UserId 取自 Claim）；
    /// ⑤ SysFileController.Upload：跨业务复用的上传原语，无单一菜单归属，靠"登录即可＋机构数据权限"约束。
    /// 反向防腐化：表内键若已挂上 [MenuPerm] 或端点已删 ⇒ 同样红（僵尸豁免即红，见第二个用例）。
    /// </summary>
    private static readonly Dictionary<string, string> ExemptWithReason = new(StringComparer.Ordinal)
    {
        ["AuthController.Login"] = "登录链：登录前无身份，不可要求菜单权限",
        ["AuthController.LoginOtp"] = "登录链二阶段（动态码）：同上",
        ["AuthController.Logout"] = "登出：仅需已认证，无菜单权限语义",
        ["DemoController.Validate"] = "示例端点：DemoController 类级 [Authorize]（B1a 匿名收口）＝登录即可",
        ["SysColumnCustomController.Store"] = "列个性化自助面：仅读写本人偏好（参照公告自读面豁免口径）",
        ["SysColumnCustomController.Reset"] = "列个性化自助面：同上",
        ["SysNoticeController.SetRead"] = "公告自读面：服务层按 Claim UserId 只更新本人接收行(SetReadAsync)，登录即可（控制器注释既定口径）",
        ["SysUserController.UpdatePassword"] = "改本人密码：UserId 取自 Claim(_userManager.UserId)防改他人，登录即可（控制器注释既定口径；管理员重置他人走 resetPwd，已挂 sysUser/resetPwd）",
        ["SysFileController.Upload"] = "通用上传原语：头像/公告附件等多业务入口复用，无单一菜单归属；口径＝登录即可，SysFile 已纳管 EntityBaseOrg(B2b)⇒ 可见范围由机构数据权限约束，业务关联另走 associate 端点",
    };

    private static readonly string[] WriteVerbs = ["POST", "PUT", "DELETE", "PATCH"];

    [Fact]
    public void 写端点全部挂权限键或在豁免表()
    {
        var violations = new List<string>();
        var checkedCount = 0;

        foreach (var controller in CollectControllers())
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // 动词口径与 ApiDocGenerator.ResolveHttpMethod 一致：遍历 HttpMethodAttribute 基类(兼容派生的自定义动词特性)
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: false)
                    .SelectMany(a => a.HttpMethods)
                    .ToList();
                if (verbs.Count == 0)
                {
                    continue;
                }

                if (!verbs.Any(v => WriteVerbs.Contains(v, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                checkedCount++;
                var key = $"{controller.Name}.{method.Name}";

                if (method.GetCustomAttribute<MenuPermAttribute>() is not null)
                {
                    continue;
                }

                if (!ExemptWithReason.ContainsKey(key))
                {
                    violations.Add($"{key} [{string.Join('/', verbs)}]");
                }
            }
        }

        Assert.True(checkedCount >= 50, $"写端点扫描面异常(仅 {checkedCount} 个)——控制器程序集装载失败？");
        Assert.True(violations.Count == 0,
            $"写端点未挂 [MenuPerm] 且不在豁免表(纵深期 D4.4 权限矩阵门)：{string.Join(" | ", violations)}。"
            + "修法：给端点加 [MenuPerm(\"xxx\")] 并与菜单按钮种子一一对应（约定 14）；"
            + "确属豁免（登录链/自助面）才加进 ExemptWithReason 并写清理由。");
    }

    [Fact]
    public void 豁免表无僵尸项()
    {
        var unguarded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var controller in CollectControllers())
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: false)
                    .SelectMany(a => a.HttpMethods)
                    .ToList();
                if (verbs.Count == 0 || !verbs.Any(v => WriteVerbs.Contains(v, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (method.GetCustomAttribute<MenuPermAttribute>() is null)
                {
                    unguarded.Add($"{controller.Name}.{method.Name}");
                }
            }
        }

        var zombies = ExemptWithReason.Keys.Where(k => !unguarded.Contains(k)).ToList();
        Assert.True(zombies.Count == 0,
            $"豁免表存在僵尸项（端点已删或已挂 [MenuPerm]）：{string.Join(" | ", zombies)}——请从豁免表删除。");
    }

    /// <summary>装载本仓控制器（Web.Entry 框架面＋ Business 业务面），fail-loud：装不到即红，不静默跳过。</summary>
    private static List<Type> CollectControllers()
    {
        var assemblies = new List<Assembly>();
        foreach (var name in new[] { "AdminCore.Web.Entry", "AdminCore.Business" })
        {
            try
            {
                assemblies.Add(Assembly.Load(name));
            }
            catch (Exception ex)
            {
                Assert.Fail($"控制器程序集 {name} 装载失败（权限矩阵门无法执行）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        var controllers = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        Assert.True(controllers.Count >= 20, $"控制器扫描面异常(仅 {controllers.Count} 个)——程序集或基类口径变了？");
        return controllers;
    }
}
