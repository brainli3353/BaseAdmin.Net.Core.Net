#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""5090 测试版真机业务链路验证（可复跑·只读为主）。

为什么存在:本仓 639 条单测全在测试进程内跑(WebApplicationFactory/直构服务),
真机 5090 上"业务请求实际长什么样"此前 0 资产、0 读数。本脚本把真机面固化成
可复跑的门:改完部署后跑一次,证明运行面契约没漂。

默认只做零写库动作(未认证探测/分页/错误码/i18n)。带 --write 才跑配置 CRUD
+ 缓存失效取证(需要 Redis 观测点,见下)。

前置(全部实读得出,勿背):
  - 5090 容器可直连 http://192.168.1.202:5090(宿主机本机视角另算)
  - 探针账号 E2E_MCP_1:复制 admin 行改身份,密码同 admin;**绝不碰真实 admin 的 OTP 绑定行**
  - 5090 实际缓存面 = Cache.Production.json 的 Redis 串(实读 127.0.0.1:6380 db7);
    该端口只绑 localhost,须经 ssh 到宿主观测,且 **SELECT 与查询必须拆成两个 RESP 包**
    (并成一个多参数包 → Redis 报 wrong number of arguments,而错误文本极易被读成"键不存在")

用法:
  python3 scripts/e2e/verify-5090.py            # 只读面(默认,零写库)
  python3 scripts/e2e/verify-5090.py --write     # 追加 D 配置 CRUD＋缓存失效 / E 审计链路 / F 保留期清理
退出码:0＝全过;1＝有失败项(逐项打 FAIL 行)。

覆盖(D/E/F 仅 --write):A 未认证契约 / B 错误码 i18n / C 分页 / D 配置 CRUD 与缓存失效 /
E 审计落库(sys_log_op,含 TraceId 与账号归因) / F 日志保留期物理清理(CleanSysLogJob,
含"插入确认非 0"前置——不校验就会把空插当成"清理生效") / G 业务异常虽 HTTP200 审计仍记失败。
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import random
import socket
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.request

BASE = os.environ.get("E2E_BASE", "http://localhost:5090")
PG = (os.environ.get("E2E_PG_HOST", "localhost"),
      os.environ.get("E2E_PG_USER", "huluyeye"),
      os.environ.get("E2E_PG_DB", "huluyeye_admin"))
PROBE = os.environ.get("E2E_PROBE", "E2E_MCP_1")
PROBE_PW = os.environ.get("E2E_PROBE_PW", "Admin@123")
PROBE_UID = os.environ.get("E2E_PROBE_UID", "9927180000001")
ADMIN_UID = "841915844415557"          # 真实 admin:OTP 行必须恒为 1
PROBE_RETENTION = "__e2e_retention_probe__"   # 保留期实验用的可指名假账号(不碰真实账号)
PROBE_LOW = os.environ.get("E2E_PROBE_LOW", "E2E_MCP_LOW")   # 非超管探针(AccountType=0),验权限拒绝
PROBE_LOW_UID = os.environ.get("E2E_PROBE_LOW_UID", "9927180000002")
PROBE_UID_N = os.environ.get("E2E_PROBE_UID", "9927180000001")
SSH_ALIAS = os.environ.get("E2E_SSH", "hulubao")

results = []


def ok(name, cond, detail=""):
    results.append((bool(cond), name, detail))
    print(("  PASS  " if cond else "  FAIL  ") + name + (("  ← " + detail) if detail else ""))
    return bool(cond)


def api(method, path, body=None, token=None, headers=None, raw=False):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            txt, code = r.read().decode(), r.status
    except urllib.error.HTTPError as e:
        # 有 HTTP 响应(4xx/5xx):响应体照常返回,由调用方断言契约
        txt, code = e.read().decode(), e.code
    except (urllib.error.URLError, OSError) as e:
        # 传输层失败(连不上/超时/DNS):返回结构化哨兵而非抛出。
        # WHY 必须捕:此前只捕 HTTPError ⇒ 服务停摆时脚本在第一段抛 URLError 崩掉,
        # 既看不到后续面,"连不上"也只有一坨 Python 堆栈。(已知失败校准实跑暴露)
        return ({"transportError": str(e)}, 0)
    try:
        return (txt if raw else json.loads(txt), code)
    except json.JSONDecodeError:
        # 非 JSON 响应(网关 HTML/空体):同样回哨兵,别让 traceback 冒充业务失败
        return ({"nonJsonResponse": txt[:180]}, code)


def psql_val(q):
    """取 psql 首行(RETURNING 的结果行;尾部的 INSERT/DELETE 命令标签不是数据)。"""
    r = psql(q)
    return r.splitlines()[0].strip() if r else ""


def psql(q):
    env = dict(os.environ, PGPASSWORD=os.environ.get("PGPASSWORD", os.environ.get("POSTGRES_PASSWORD", "huluyeye_dev_2026")))
    r = subprocess.run(["psql", "-h", PG[0], "-U", PG[1], "-d", PG[2], "-At", "-c", q],
                       capture_output=True, text=True, env=env)
    return r.stdout.strip() or ("ERR " + r.stderr.strip()[:120])


def redis_probe(cmds, host="127.0.0.1", port=6380):
    """经 ssh 在宿主上连 Redis。**逐条命令各自一个 RESP 包**——这是本脚本存在的理由之一。

    返回最后一条命令的回执(前面 SELECT 的回执 +OK 必须可见,否则读数无资格当证据)。
    """
    resp = "".join(("*%d\r\n" % len(c)) + "".join("$%d\r\n%s\r\n" % (len(x.encode()), x) for x in c)
                   for c in cmds)
    # 载荷走 hex:字节里有 CR/LF 控制字符,用 repr 内嵌会把宿主脚本的字符串字面量截断成多行
    script = ("import socket,binascii\n"                    # noqa: E501
              "p=binascii.unhexlify(\"%s\")\n"
              "s=socket.create_connection((\"%s\",%d),timeout=6)\n"
              "s.sendall(p)\ns.settimeout(3)\n"
              "try: print(s.recv(65535).decode(\"latin1\").strip().replace(chr(13),\"\"))\n"
              "except Exception as e: print(\"ERR\",e)\n") % (resp.encode().hex(), host, port)
    r = subprocess.run(["ssh", "-o", "StrictHostKeyChecking=no", "-o", "ConnectTimeout=8", SSH_ALIAS,
                        "python3", "-"], input=script.encode(), capture_output=True, timeout=45)
    return (r.stdout.decode().strip() or ("ERR " + " | ".join(r.stderr.decode().strip().splitlines()[-2:])))


def redis_get(key):
    """→ dict:仅当 SELECT 回执为 +OK 时 exists/value 才算证据。"""
    raw = redis_probe([["SELECT", "7"], ["EXISTS", key]])
    lines = [l for l in raw.split("\n") if l != ""]
    sel_ok = len(lines) >= 1 and lines[0].strip() == "+OK"
    exists = lines[-1].strip() if sel_ok and len(lines) >= 2 else None
    return {"select_ok": sel_ok, "exists": exists, "raw": raw}


def totp(secret):
    h = hmac.new(base64.b32decode(secret), struct.pack(">Q", int(time.time()) // 30), hashlib.sha1).digest()
    o = h[19] & 0x0F
    return str((struct.unpack(">I", h[o:o + 4])[0] & 0x7FFFFFFF) % 1000000).zfill(6)


def ensure_probe():
    """主探针账号缺位时就地创建(照 H 段低权探针同一形状,从 admin 行复制密码哈希)。

    WHY 必须自建:此前探针靠"手工建一次然后暂留",结果 = 库里永久留一行,
    与已清过的 456 行 t13q_u_* 是同一条腐化路径(暂留理由一旦被证伪就成永久残留)。
    自建之后配合 main 末尾自删 ⇒ 脚本跑完库里不欠东西。
    """
    if psql_val("SELECT count(*) FROM sys_user WHERE id=%s" % PROBE_UID) == "1":
        return
    got = psql_val("INSERT INTO sys_user (id,\"Account\",\"Password\",\"RealName\",\"Status\",\"IsDelete\","
                   "\"CreateTime\",\"CreateUserId\",\"CreateUserName\",\"UpdateTime\",\"AccountType\","
                   "\"TokenVersion\",\"OrgId\",\"TenantId\",\"PosId\",\"LastChangePasswordTime\") "
                   "SELECT %s,'%s',\"Password\",'真机验证探针',0,false,now(),1,'system',now(),999,0,\"OrgId\","
                   "\"TenantId\",\"PosId\",now() FROM sys_user WHERE \"Account\"='admin' "
                   "RETURNING \"Account\"" % (PROBE_UID, PROBE))
    if got != PROBE:
        raise SystemExit("中止:主探针创建失败(实得=%s)" % got)
    print("  主探针缺位 ⇒ 已就地创建(跑完会自删)")


def drop_probe():
    """跑完自删主探针及其 OTP 绑定(只按探针 uid 指名删,绝不碰真实用户)。"""
    psql('DELETE FROM sys_user_otp WHERE "UserId"=%s' % PROBE_UID)
    psql("DELETE FROM sys_user WHERE id=%s" % PROBE_UID)


def login(acct=None, uid=None):
    """两阶段登录,只使用探针账号自己的 secret(绝不解密真实 admin 的绑定)。"""
    acct = acct or PROBE; uid = uid or PROBE_UID
    if uid == PROBE_UID:
        ensure_probe()
    psql('DELETE FROM sys_user_otp WHERE "UserId"=%s' % uid)
    if psql('SELECT count(*) FROM sys_user_otp WHERE "UserId"=%s' % ADMIN_UID) != "1":
        raise SystemExit("中止:真实 admin 的 OTP 行数不是 1,先查清再跑")
    d1, _ = api("POST", "/api/auth/login", {"account": acct, "password": PROBE_PW})
    sec = d1["data"]["secret"]
    if not sec:
        raise SystemExit("中止:探针未拿到新 secret(bindRequired 应为 true)")
    d2, _ = api("POST", "/api/auth/login/otp", {"challengeId": d1["data"]["challengeId"], "code": totp(sec)})
    return d2.get("data") or ""


def sec_unauth():
    print("\n=== A 未认证面:统一响应契约与错误码 ===")
    for p in ("/api/sysConfig/page", "/api/sysConfig/get?code=x", "/api/nope-not-exist"):
        d, code = api("GET", p)
        ok("未认证 %-34s → errCode=common.unauthorized" % p,
           d.get("errCode") == "common.unauthorized",
           "HTTP=%s code=%s msg=%s" % (code, d.get("code"), d.get("msg")))
        ok("未认证 %-34s 三字段齐全" % p,
           set(("code", "msg", "errCode")) <= set(d.keys()), json.dumps(d, ensure_ascii=False)[:90])
    d, _ = api("GET", "/health")
    ok("/health 依赖实探 db=ok", d.get("db") == "ok", json.dumps(d, ensure_ascii=False))


def sec_i18n():
    print("\n=== B 错误码 i18n:同一 errCode 换语言应换文案 ===")
    zh, _ = api("GET", "/api/sysConfig/page", headers={"Accept-Language": "zh-CN"})
    en, _ = api("GET", "/api/sysConfig/page", headers={"Accept-Language": "en-US"})
    ok("同一 errCode 的 msg 随 Accept-Language 变化",
       zh.get("msg") != en.get("msg") and bool(en.get("msg")),
       "zh=%r en=%r" % (zh.get("msg"), en.get("msg")))
    ok("errCode 不随语言变化(稳定标识)", zh.get("errCode") == en.get("errCode"),
       "%s == %s" % (zh.get("errCode"), en.get("errCode")))


def sec_paging(token):
    print("\n=== C 分页契约(只读) ===")
    d, code = api("GET", "/api/sysConfig/page?pageIndex=1&pageSize=2", token=token)
    data = d.get("data") or {}
    rows = data.get("list") if isinstance(data, dict) else None
    ok("分页返回 list+total 形状(PageResult 真字段=Total/List)", isinstance(rows, list) and "total" in data,
       "HTTP=%s keys=%s total=%s" % (code, list(data.keys()) if isinstance(data, dict) else type(data).__name__,
                                     data.get("total") if isinstance(data, dict) else None))
    d2, _ = api("GET", "/api/sysUser/page?pageIndex=1&pageSize=1", token=token)
    rows2 = (d2.get("data") or {}).get("list") if isinstance(d2.get("data"), dict) else None
    ok("pageSize=1 确实只回 1 行(参数生效非摆设)", isinstance(rows2, list) and len(rows2) <= 1,
       "实得 %s 行" % (len(rows2) if isinstance(rows2, list) else "?"))
    d3, _ = api("GET", "/api/sysUser/page?pageIndex=1&pageSize=200", token=token)
    rows3 = (d3.get("data") or {}).get("list") if isinstance(d3.get("data"), dict) else None
    ok("pageSize 上限未被静默放大到异常值", isinstance(rows3, list), "行数=%s" % (len(rows3) if isinstance(rows3, list) else "?"))


def sec_config_cache(token):
    print("\n=== D ⭐ 配置 CRUD + 缓存失效取证(唯一写库段,自建哨兵项) ===")
    code_val = "e2e_chk_%06d" % random.randint(0, 999999)
    key = "sys_config:" + code_val
    try:
        a, _ = api("POST", "/api/sysConfig/add", {"name": "真机哨兵", "code": code_val,
                                                  "value": "V1", "groupCode": "e2e"}, token=token)
        cid = a.get("data")
        if not ok("add 哨兵配置", bool(cid), json.dumps(a, ensure_ascii=False)[:100]):
            return
        r0 = redis_get(key)
        ok("add 后未读→键不存在(且 SELECT 回执 +OK)", r0["select_ok"] and r0["exists"] == ":0", r0["raw"].replace("\n", "|"))
        g1, _ = api("GET", "/api/sysConfig/get?code=" + code_val, token=token)
        ok("第一次 GET 读到 V1", (g1.get("data") or "") == "V1", json.dumps(g1, ensure_ascii=False)[:80])
        r1 = redis_get(key)
        ok("⭐ GET 后缓存已回填", r1["select_ok"] and r1["exists"] == ":1", r1["raw"].replace("\n", "|"))
        api("POST", "/api/sysConfig/update", {"id": cid, "value": "V2"}, token=token)
        r2 = redis_get(key)
        ok("⭐⭐ UPDATE 后缓存键应被删除(TTL=-2 语义)", r2["select_ok"] and r2["exists"] == ":0",
           r2["raw"].replace("\n", "|"))
        g2, _ = api("GET", "/api/sysConfig/get?code=" + code_val, token=token)
        ok("⭐ 复读应得新值 V2(非缓存旧值 V1)", (g2.get("data") or "") == "V2", json.dumps(g2, ensure_ascii=False)[:80])
        ok("库面独立复核(不信 API 自陈)", psql('SELECT "Value" FROM sys_config WHERE "Code"=\'%s\'' % code_val) == "V2")
        api("POST", "/api/sysConfig/delete?id=%s" % cid, token=token)
        r3 = redis_get(key)
        ok("DELETE 后缓存键亦失效", r3["select_ok"] and r3["exists"] == ":0", r3["raw"].replace("\n", "|"))
        g3, _ = api("GET", "/api/sysConfig/get?code=" + code_val, token=token)
        ok("删除后读到 null(未读到已删旧值)", g3.get("data") is None, json.dumps(g3, ensure_ascii=False)[:80])
    finally:
        psql('DELETE FROM sys_config WHERE "Code"=\'%s\'' % code_val)
        left = psql('SELECT count(*) FROM sys_config WHERE "Code"=\'%s\'' % code_val)
        ok("清理断言:哨兵行物理删净", left == "0", "残留=%s" % left)
        ok("真实 admin 的 OTP 行全程未动",
           psql('SELECT count(*) FROM sys_user_otp WHERE "UserId"=%s' % ADMIN_UID) == "1")


def sec_audit(token):
    """E 审计链路:写请求 → sys_log_op 异步出现对应行(轮询,因落库走事件总线)。

    WHY 轮询而不是 sleep 固定值:落库在总线后台消费(响应先返回),固定 sleep 要么假失败要么白等。
    """
    print("\n=== E 审计链路:请求是否真进 sys_log_op ===")
    before = psql("SELECT COALESCE(MAX(id),0) FROM sys_log_op")
    api("GET", "/api/sysConfig/page?pageIndex=1&pageSize=1", token=token)
    row = None
    for _ in range(8):
        time.sleep(1)
        # 注意:SQL 里字符串字面量用单引号,别写成 "xxx"(PG 双引号＝标识符/列名)
        row = psql("SELECT \"ControllerName\"||'/'||\"ActionName\"||'/'||\"HttpMethod\""
                   "||' ok='||\"Success\"||' ms='||\"ElapsedMs\""
                   "||' trace='||CASE WHEN \"TraceId\" IS NULL THEN 'NULL' ELSE 'OK' END"
                   "||' acct='||COALESCE(\"Account\",'ANON')"
                   " FROM sys_log_op WHERE id > %s ORDER BY id DESC LIMIT 1" % before)
        if row and not row.startswith("ERR"):
            break
    if not ok("写请求 8 秒内落入 sys_log_op", bool(row) and not row.startswith("ERR"), row or "(空)"):
        return
    ok("审计行含 Controller/Action/方法/耗时", "SysConfig" in row and " ms=" in row, row[:150])
    ok("审计行 TraceId 非空(可与文件日志串查)", "trace=NULL" not in row, row[-70:])
    ok("审计行账号归因＝探针本人", ("acct=" + PROBE) in row, row[-70:])


def sec_retention(token):
    """F ⭐ 保留期清理:造一条超期行,断言被 CleanSysLogJob 物理删除且不误删近期行。

    ⚠ 本段是"空插假读数"换来的口径:**插入确认必须非 0,否则后续"消失了"全是垃圾**,故先校验再等。
    探针用可指名假账号(不碰真实账号),即便清理未生效也可按 Account 手工回滚删除。
    """
    print("\n=== F ⭐ 日志保留期清理(依赖 sys_job_detail 的 clean_sys_log) ===")
    cnt = psql("SELECT count(*) FROM sys_job_detail WHERE \"JobName\"='clean_sys_log' AND \"IsDelete\"=false")
    if not cnt.isdigit() or int(cnt) == 0:
        print("  SKIP  库里无启用的 clean_sys_log 任务(实读 count=%s),跳过(非失败)" % cnt)
        return
    iv = psql("SELECT \"Interval\" FROM sys_job_detail WHERE \"JobName\"='clean_sys_log' AND \"IsDelete\"=false LIMIT 1")
    psql("DELETE FROM sys_log_op WHERE \"Account\"='%s'" % PROBE_RETENTION)
    mid = "%d%03d" % (int(time.time() * 1000) % 10 ** 15, random.randint(0, 999))
    psql("INSERT INTO sys_log_op (id,\"ControllerName\",\"ActionName\",\"HttpMethod\",\"Account\","
         "\"Success\",\"IsDelete\",\"CreateTime\",\"ElapsedMs\") "
         "VALUES (%s,'E2EProbe','Retention','GET','%s',true,false,now() - interval '40 days',1)"
         % (mid, PROBE_RETENTION))
    ins = psql("SELECT count(*) FROM sys_log_op WHERE \"Account\"='%s'" % PROBE_RETENTION)
    if not ok("⭐ 前置:插入确认非 0(否则后续读数为垃圾)", ins == "1", "实得=%s" % ins):
        return
    near_before = psql("SELECT count(*) FROM sys_log_op WHERE \"CreateTime\" > now() - interval '1 hour'")
    wait = max(12, (int(iv) if iv.isdigit() else 10) * 2 + 4)
    print("  等待 %d 秒(clear Interval=%ss 的两个周期)…" % (wait, iv))
    time.sleep(wait)
    gone = psql("SELECT count(*) FROM sys_log_op WHERE \"Account\"='%s'" % PROBE_RETENTION)
    ok("⭐ 40 天前的行被物理清理(应 0)", gone == "0", "实得=%s" % gone)
    near_after = psql("SELECT count(*) FROM sys_log_op WHERE \"CreateTime\" > now() - interval '1 hour'")
    ok("对照:近 1 小时行未被误删", near_after == near_before, "%s → %s" % (near_before, near_after))
    psql("DELETE FROM sys_log_op WHERE \"Account\"='%s'" % PROBE_RETENTION)


def sec_bizaudit(token):
    """G 业务异常的审计口径:HTTP 200 但审计应记 Success=false。

    WHY 单独一条:本项目业务异常统一映射成 HTTP 200 + 业务码,若审计只看 HTTP 状态码就会把失败记成成功。
    OpLogFilter 靠 executed.Exception 判定(代码注释明写),此断言锁死这个反直觉契约,防回归。
    """
    print("\n=== G 业务异常(HTTP200)的审计 Success 口径 ===")
    before = psql("SELECT COALESCE(MAX(id),0) FROM sys_log_op")
    j, _ = api("POST", "/api/sysConfig/delete?id=999999999999999", token=token)
    if not isinstance(j, dict):
        j = {}
    ok("不存在的 id 删除 → errCode=config.not_found", j.get("errCode") == "config.not_found",
       "code=%s errCode=%s raw=%s" % (j.get("code"), j.get("errCode"), json.dumps(j, ensure_ascii=False)[:110]))
    row = ""
    for _ in range(8):
        time.sleep(1)
        row = psql('SELECT "Success" FROM sys_log_op WHERE id > %s AND "ActionName"=\'Delete\' '
                   'ORDER BY id DESC LIMIT 1' % before)
        if row in ("t", "f"):
            break
    ok("⭐ 业务异常虽 HTTP200,审计仍记 Success=false", row == "f", "Success=%s" % row)


def sec_perm(token):
    """H ⭐ 权限拒绝路径:[MenuPerm] 端点对超管放行、对非超管 403。

    这是框架安全命门——BatchSGPermMatrixGuard 只做静态扫描(标注齐不齐),运行时的
    AccountType==999 穿透与"权限集不含→403"裁决只有真机能验。低权探针不配任何角色,
    权限集为空 ⇒ 必拒;若哪天误把某端点的 [MenuPerm] 摘掉或短路条件写错,此段会红。
    """
    print("\n=== H ⭐ 权限拒绝路径(超管放行 vs 非超管 403) ===")
    psql("DELETE FROM sys_user WHERE id=%s" % PROBE_LOW_UID)
    ins = psql_val("INSERT INTO sys_user (id,\"Account\",\"Password\",\"RealName\",\"Status\",\"IsDelete\","
               "\"CreateTime\",\"CreateUserId\",\"CreateUserName\",\"UpdateTime\",\"AccountType\","
               "\"TokenVersion\",\"OrgId\",\"TenantId\",\"PosId\",\"LastChangePasswordTime\") "
               "SELECT %s,'%s',\"Password\",'真机低权探针',0,false,now(),1,'system',now(),0,0,\"OrgId\","
               "\"TenantId\",\"PosId\",now() FROM sys_user WHERE \"Account\"='admin' "
               "RETURNING \"Account\"" % (PROBE_LOW_UID, PROBE_LOW))
    try:
        if not ok("前置:低权探针(AccountType=0)已建", ins == PROBE_LOW, "实得=%s" % ins):
            return
        low = login(PROBE_LOW, PROBE_LOW_UID)
        if not ok("低权探针登录拿到 token", bool(low), "长度=%d" % len(low)):
            return
        j, code = api("POST", "/api/sysConfig/update", {"id": 1, "value": "x"}, token=token)
        ok("超管调 [MenuPerm] 端点 → 放行(非 403)", code != 403, "HTTP=%s" % code)
        j2, code2 = api("POST", "/api/sysConfig/update", {"id": 1, "value": "x"}, token=low)
        ok("⭐ 非超管无该权限 → HTTP 403", code2 == 403, "HTTP=%s" % code2)
        ok("403 响应带稳定 errCode/无权限 msg", "无权限" in str(j2.get("msg")) or bool(j2.get("errCode")),
           json.dumps(j2, ensure_ascii=False)[:100])
    finally:
        # 清理放 finally：断言失败或提前 return 也必须删（快件实锤：清理排在被测代码之后会整段跳过）
        psql("DELETE FROM sys_user WHERE id=%s" % PROBE_LOW_UID)
        psql('DELETE FROM sys_user_otp WHERE "UserId"=%s' % PROBE_LOW_UID)
        left = psql_val("SELECT count(*) FROM sys_user WHERE id=%s" % PROBE_LOW_UID)
        ok("清理:低权探针已删", left == "0", "残留=%s" % left)


def main():
    ap = argparse.ArgumentParser(description="5090 真机业务链路验证")
    ap.add_argument("--write", action="store_true", help="追加配置 CRUD + 缓存失效取证(会写库,自清理)")
    a = ap.parse_args()
    print("目标＝%s  探针＝%s  时刻＝%s" % (BASE, PROBE, time.strftime("%F %T %z")))
    sec_unauth()
    tok = ""
    try:
        tok = login()
    except SystemExit as e:
        print(str(e))
    except Exception as e:
        print("登录异常:%s %s(跳过需登录的段)" % (type(e).__name__, e))
    ok("登录拿到 token", bool(tok), "长度=%d" % len(tok))
    if tok:
        sec_i18n()
        sec_paging(tok)
        if a.write:
            sec_config_cache(tok)
            sec_audit(tok)
            sec_bizaudit(tok)
            sec_perm(tok)
            sec_retention(tok)
        else:
            print("\n=== D/E/F 已跳过(加 --write 才跑配置 CRUD＋缓存失效＋审计＋保留期清理) ===")
    # 自删必须**无条件**：登录段（含默认档）会 ensure_probe() 建探针，
    # 挂在 if a.write 里 ⇒ 默认档跑一次留一行残留（2026-10-27 19:3x 实跑＝探针 0→1，已证）。
    # 默认档不删的话，"暂留"这个坑又被我自己请回来了。
    drop_probe()
    print("  自删探针完成:库内 E2E 探针残留＝%s（须 0）" % (psql_val("SELECT count(*) FROM sys_user WHERE \"Account\" LIKE 'E2E_MCP%'") or "?"))
    n = sum(1 for r in results if not r[0])
    print("\n──────── 汇总:%d 项,失败 %d ────────" % (len(results), n))
    for bad in [r for r in results if not r[0]]:
        print("  FAIL %s  %s" % (bad[1], bad[2]))
    return 1 if n else 0


if __name__ == "__main__":
    sys.exit(main())
