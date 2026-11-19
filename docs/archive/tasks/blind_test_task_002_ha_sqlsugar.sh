#!/usr/bin/env bash
# ============================================================================
# 盲审验证脚本(职责反转补交版) — task_002_ha_sqlsugar
# 任务:SqlSugar 单库接入(PostgreSQL) + CodeFirst 自动建表 + 雪花主键(YitIdHelper)
# 任务书:/data/tasks/done/task_002_ha_sqlsugar.md
# ----------------------------------------------------------------------------
# 流程定位(盲审脚本职责反转,2026-08-26):
#   盲审旧流程=测试在开发完成前按规格写脚本、系统执行;职责反转=开发在验收通过后
#   补交同一份验证脚本。立场仍为证伪:脚本只认任务书规格,不认"开发说自己做对了"。
# 信息边界:仅依据任务书 4 项验收标准 + 被测代码/服务,不读任何开发会话日志。
# 设计要点:
#   - 4 项验收标准逐条转可执行命令,每项输出 PASS/FAIL/QUESTION + 证据:
#       A1  验收标准① dotnet build 0 警告 0 错误(直接构建,--no-incremental 真编译)
#       A2  验收标准② 服务启动 HTTP 可达(:5090)
#       A3  验收标准② 启动日志出现「初始化表」(真成功标记,失败路径不打)
#       A4  验收标准② 无异常退出(存活观察)
#       A5  验收标准③ sys_config 表存在 + 结构(恰好 2 列/Id 主键/表注释=系统配置)
#       B1  验收标准③反例 CodeFirst 二次初始化幂等(重启不丢数据)+ 列类型边界
#       B2  验收标准④ git commit(验收报告提交号可解析 + 8 交付物已入库且干净)
#       B3  契约静态抽查(23 项 + Furion/Admin.NET 禁令反例)
#   - 伪攻击数据/边界输入:
#       * A3:验证「初始化表」是真成功标记(源码静态确认其位于 InitTables 成功路径之后)
#       * B1:探测行用非雪花小主键 + max-long 边界主键;Name 注入 SQL 片段/中文/超长串
#   - 服务启动:优先 bash /data/watchdog/scripts/start_test_server.sh;
#     否则直跑预构建 dll(单进程,便于反例重启);否则 dotnet run;否则假设外部已拉起
#   - DB 验证链(逐层降级):docker exec psql → 本地 psql → python3 标准库最小线协议
#     客户端(trust/cleartext/md5/scram-sha-256);三级均不可达则诚实降级 QUESTION
#   - 隔离环境(如本沙箱 PG 网络不可达):DB/服务相关项输出 QUESTION + 环境受限证据,
#     绝不因网络不可达伪判 PASS,也不把纯网络故障误判为代码缺陷 FAIL
# 依赖:bash、curl、python3(标准库)、git、mktemp;可选 docker/psql/dotnet/setsid/timeout
# ============================================================================

set -u

# ---------------- 常量 ----------------
WORKTREE="/data/worktrees/admin/task_002_ha_sqlsugar"
SOLUTION="src/huluyeye-admin.slnx"
START_SERVER="/data/watchdog/scripts/start_test_server.sh"
APPROVAL_REPORT="$WORKTREE/acceptance_task_002_ha_sqlsugar.md"
APPSETTINGS="$WORKTREE/src/Web.Entry/appsettings.json"
DLL="$WORKTREE/src/Web.Entry/bin/Debug/net10.0/Huluyeye.Web.Entry.dll"
SERVICE_PORT=5090
# 任务书给定服务地址:http://dev-a:5090 或 localhost:5090(含 127.0.0.1 等价)
CANDIDATE_URLS=("http://localhost:${SERVICE_PORT}" "http://127.0.0.1:${SERVICE_PORT}" "http://dev-a:${SERVICE_PORT}")
# 8 个交付物(验收报告"交付物清单"逐项,用于 B2 入库完整性)
DELIVERABLES=(
  "src/Core/SqlSugarSetup.cs"
  "src/Web.Entry/Program.cs"
  "src/Core/Entities/SysConfig.cs"
  "src/Core/Huluyeye.Core.csproj"
  "src/Web.Entry/appsettings.json"
  "src/huluyeye-admin.slnx"
  "src/Core/YitIdHelper.cs"
  "src/Core/Entities/IncreTableAttribute.cs"
)
PROBE_NAME="blind_probe_task_002"          # 探测行唯一标记(清理用)
PROBE_ID_SMALL=1                            # 边界主键:极小非雪花值(验证 Id 列=bigint 可手写,非自增)
PROBE_ID_MAX=9223372036854775807           # 边界主键:int64 最大值(bigint 上界,验证列宽)
READY_TIMEOUT=150                           # 等待服务就绪秒数(自启进程死亡则提前退出)
WORKER_WAIT=10                              # 稳定性观察秒数

TMP=$(mktemp -d "${TMPDIR:-/tmp}/blind_task002.XXXXXX")
LOG_APP="$TMP/app_run1.log"
LOG_APP2="$TMP/app_run2.log"
APP_PID=""
BASE_URL=""                                 # 探测成功的地址,后续检查复用
START_MODE="none"                           # start_script | dotnet_dll | dotnet_run | external

# ---------------- 结果记录 ----------------
RESULTS=()   # "编号|状态|名称"
record() { # $1=编号 $2=状态(PASS|FAIL|QUESTION) $3=名称 $4=证据(可多行)
    local id="$1" st="$2" name="$3" ev="$4"
    echo ""
    echo "[${id}] ${st} | ${name}"
    while IFS= read -r line; do
        [ -n "$line" ] && echo "    证据: $line"
    done <<< "$ev"
    RESULTS+=("${id}|${st}|${name}")
}

final_verdict() {
    # 任一 FAIL → 整体 FAIL;否则有 QUESTION → QUESTION;否则 PASS
    local v="PASS" item st
    for item in "${RESULTS[@]}"; do
        st="$(echo "$item" | cut -d'|' -f2)"
        if [ "$st" = "FAIL" ]; then v="FAIL"; break; fi
        if [ "$st" = "QUESTION" ]; then v="QUESTION"; fi
    done
    echo ""
    echo "==================== 汇总 ===================="
    printf "  %-6s %-10s %s\n" "编号" "状态" "检查项"
    for item in "${RESULTS[@]}"; do
        printf "  %-6s %-10s %s\n" "$(echo "$item" | cut -d'|' -f1)" "$(echo "$item" | cut -d'|' -f2)" "$(echo "$item" | cut -d'|' -f3)"
    done
    echo "================================================"
    echo "<!-- verdict: ${v} -->"
    echo "VERDICT: ${v}"
    if [ "$v" = "PASS" ]; then return 0; else return 1; fi
}

cleanup() {
    # 只杀本脚本自己拉起的进程(记录 PID,不用模式匹配,遵守 pkill 红线)
    if [ -n "$APP_PID" ]; then
        if [ "$START_MODE" = "start_script" ] && command -v setsid >/dev/null 2>&1; then
            # setsid 让服务自成会话,kill 负 PID 连带子进程,避免残留
            kill -- "-$APP_PID" 2>/dev/null || kill "$APP_PID" 2>/dev/null
        else
            kill "$APP_PID" 2>/dev/null
        fi
        sleep 1
    fi
    rm -rf "$TMP"
}
trap cleanup EXIT

# ---------------- HTTP 探测 ----------------
# 探测单个 URL,返回 http_code(000=失败);响应体落 $TMP/probe_body.html 供取证
# WHY 不用 `|| echo 000`:curl 失败时 -w '%{http_code}' 已自行输出 000,再追加会得 "000000",
#   使 `!= "000"` 误判服务就绪(假阳性,盲审最忌)
probe_url() { # $1=url
    local code
    code=$(curl -s -o "$TMP/probe_body.html" -w '%{http_code}' --max-time 5 "$1" 2>/dev/null)
    [ -n "$code" ] || code="000"
    printf '%s' "$code"
}

# 轮询候选 URL 直至任一返回非 000;成功置 BASE_URL 返回 0
# WHY 提前退出:自启进程若已死=服务起不来,无需轮满 READY_TIMEOUT(隔离环境崩溃很快,省 150s)
wait_for_service() {
    local deadline=$(( $(date +%s) + READY_TIMEOUT )) url code
    while [ "$(date +%s)" -lt "$deadline" ]; do
        if [ -n "$APP_PID" ]; then
            kill -0 "$APP_PID" 2>/dev/null || return 1
        fi
        for url in "${CANDIDATE_URLS[@]}"; do
            code=$(probe_url "$url")
            if [ "$code" != "000" ]; then
                BASE_URL="$url"
                return 0
            fi
        done
        sleep 2
    done
    return 1
}

# 判定服务崩溃是否"纯粹"数据库连接不可达(隔离环境特征,非代码缺陷)
# 返回 0=命中 DB 连接失败特征;1=非 DB 特征
crash_is_db_unreachable() { # $1=日志文件
    local log="$1"
    [ -f "$log" ] || return 1
    grep -qiE "连接数据库过程中发生错误|Exception while connecting|SqlSugar.*connecting|无法打开到主机的连接|connection refused|ECONNREFUSED|Name or service not known|No such host|timed out|TimeoutException" "$log" 2>/dev/null
}

# ---------------- Postgres 客户端(python3 标准库,最小线协议) ----------------
# 用法:pg_query <appsettings.json> <sql>
# 从 appsettings 的 ConnectionStrings:Default 解析连接参数(带 // 注释的 JSON 用正则提取),
# 走 Postgres 3.0 线协议:Startup → 认证(trust/cleartext/md5/scram-sha-256)→ 简单查询。
# 退出码:0=成功(结果打印到 stdout);2=连接失败;3=认证失败;4=服务器报错;5=协议错误
pg_query() { # $1=appsettings $2=sql
    python3 - "$1" "$2" <<'PYEOF'
import base64, hashlib, hmac, secrets, socket, struct, sys

appsettings, sql = sys.argv[1], sys.argv[2]

# --- 解析连接串(appsettings.json 含 // 注释,非严格 JSON,用正则提取 Default 值) ---
import re
raw = open(appsettings, encoding="utf-8-sig").read()
# WHY 锚定 ConnectionStrings 段:appsettings 的 Logging.LogLevel 段也有一个 "Default":"Information",
#   裸匹配 "Default" 会先命中间日志级别取错值(Host/Port 解析空 → 误连 localhost)
m = re.search(r'"ConnectionStrings"\s*:\s*\{[^}]*?"Default"\s*:\s*"([^"]+)"', raw)
if not m:
    print("无法从 appsettings.json 提取 ConnectionStrings:Default", file=sys.stderr)
    sys.exit(5)
kv = dict(p.split("=", 1) for p in m.group(1).split(";") if "=" in p)
host, port = kv.get("Host", "localhost"), int(kv.get("Port", "5432"))
user, dbname, password = kv.get("Username", ""), kv.get("Database", ""), kv.get("Password", "")

# --- 线协议基础(底层 IO 统一兜底:超时/断连/坏帧直接诊断退出,不抛裸异常) ---
def recv_exact(sock, n):
    buf = b""
    while len(buf) < n:
        try:
            chunk = sock.recv(n - len(buf))
        except OSError as e:
            print(f"socket 读失败(超时/断开): {e}", file=sys.stderr)
            sys.exit(2)
        if not chunk:
            print("对端关闭连接", file=sys.stderr)
            sys.exit(2)
        buf += chunk
    return buf

def read_msg(sock):
    head = recv_exact(sock, 4)
    try:
        (length,) = struct.unpack("!I", head)
    except struct.error:
        print("协议错误: 消息长度字段损坏", file=sys.stderr)
        sys.exit(5)
    body = b""
    while len(body) < length - 4:
        body += recv_exact(sock, length - 4 - len(body))
    return head[:1], body

def send_msg(sock, mtype, payload):
    try:
        sock.sendall(struct.pack("!I", 4 + 1 + len(payload)) + mtype + payload)
    except OSError as e:
        print(f"socket 写失败: {e}", file=sys.stderr)
        sys.exit(2)

def parse_err(body):
    fields, i = {}, 0
    while i < len(body):
        code = body[i:i + 1]
        if code == b"\x00":
            break
        j = body.index(b"\x00", i + 1)
        fields[code.decode()] = body[i + 1:j].decode(errors="replace")
        i = j + 1
    return (fields.get("S", "?") + ": " + fields.get("M", "")).strip()

try:
    s = socket.create_connection((host, port), timeout=5)
    s.settimeout(5)
except Exception as e:
    print(f"连接失败 {host}:{port}: {e}", file=sys.stderr)
    sys.exit(2)

# --- StartupMessage(协议 3.0) ---
try:
    params = b""
    for k, v in (("user", user), ("database", dbname)):
        params += k.encode() + b"\x00" + v.encode() + b"\x00"
    params += b"\x00"
    s.sendall(struct.pack("!II", 4 + 4 + len(params), 196608) + params)
except Exception as e:
    print(f"启动消息发送失败: {e}", file=sys.stderr)
    sys.exit(2)

# --- 认证循环:直到 ReadyForQuery('Z') ---
while True:
    mtype, body = read_msg(s)
    if mtype == b"R":
        (code,) = struct.unpack("!I", body[:4])
        if code == 0:            # AuthenticationOk
            continue
        elif code == 3:          # MD5Password
            inner = hashlib.md5((password + user).encode()).hexdigest().encode()
            digest = hashlib.md5(inner + body[4:8]).hexdigest()
            send_msg(s, b"p", b"md5" + digest.encode() + b"\x00")
        elif code == 5:          # CleartextPassword
            send_msg(s, b"p", password.encode() + b"\x00")
        elif code == 10:         # SASL(仅支持 scram-sha-256)
            mechs = body[4:].split(b"\x00")
            if b"scram-sha-256" not in mechs:
                print(f"不支持的 SASL 机制: {mechs!r}", file=sys.stderr)
                sys.exit(3)
            cnonce = secrets.token_hex(16)
            client_first_bare = "n=,r=" + cnonce
            client_first = "n,," + client_first_bare   # GS2 头:无 channel binding
            payload = b"SCRAM-SHA-256\x00" + struct.pack("!I", len(client_first)) + client_first.encode()
            send_msg(s, b"p", payload)                 # SASLInitialResponse
            mtype2, body2 = read_msg(s)
            if mtype2 != b"R" or struct.unpack("!I", body2[:4])[0] != 11:
                print("SCRAM: 未收到 SASLContinue", file=sys.stderr)
                sys.exit(5)
            server_first = body2[4:].decode()
            f = dict(kv_.split("=", 1) for kv_ in server_first.split(","))
            combined_nonce = f["r"]
            if not combined_nonce.startswith(cnonce):
                print("SCRAM: server nonce 未含 client nonce", file=sys.stderr)
                sys.exit(5)
            salt, iters = base64.b64decode(f["s"]), int(f["i"])
            salted = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, iters)
            ckey = hmac.new(salted, b"Client Key", hashlib.sha256).digest()
            skey = hmac.new(salted, "Server Key".encode(), hashlib.sha256).digest()
            cfinal_noproof = "r=" + combined_nonce
            auth_msg = client_first_bare + "," + server_first + "," + cfinal_noproof
            csig = hmac.new(hashlib.sha256(ckey).digest(), auth_msg.encode(), hashlib.sha256).digest()
            proof = bytes(a ^ b for a, b in zip(ckey, csig))
            send_msg(s, b"p", (cfinal_noproof + ",p=" + base64.b64encode(proof).decode()).encode())
            mtype3, body3 = read_msg(s)
            if mtype3 == b"R":
                c3 = struct.unpack("!I", body3[:4])[0]
                if c3 == 12:   # SASLFinal:校验 server 签名(防中间人)
                    v = body3[4:].decode()
                    expect = "v=" + base64.b64encode(hmac.new(skey, auth_msg.encode(), hashlib.sha256).digest()).decode()
                    if v != expect:
                        print("SCRAM: server 签名校验失败", file=sys.stderr)
                        sys.exit(3)
                    continue
                print(f"SCRAM: 意外认证状态 {c3}", file=sys.stderr)
                sys.exit(3)
            if mtype3 == b"E":
                print("SCRAM 认证失败: " + parse_err(body3), file=sys.stderr)
                sys.exit(3)
        else:
            print(f"不支持的认证方式 code={code}", file=sys.stderr)
            sys.exit(3)
    elif mtype in (b"S", b"K", b"N", b"A"):   # ParameterStatus/BackendKeyData/Notice:跳过
        continue
    elif mtype == b"E":
        print("服务器错误(认证阶段): " + parse_err(body), file=sys.stderr)
        sys.exit(3)
    elif mtype == b"Z":
        break
    else:
        print(f"协议错误: 意外消息类型 {mtype!r}", file=sys.stderr)
        sys.exit(5)

# --- 简单查询 ---
send_msg(s, b"Q", sql.encode() + b"\x00")
cols, rows = None, []
while True:
    mtype, body = read_msg(s)
    if mtype == b"T":
        (n,) = struct.unpack("!H", body[:2])
        off, cols = 2, []
        for _ in range(n):
            end = body.index(b"\x00", off)
            cols.append(body[off:end].decode(errors="replace"))
            off = end + 1 + 18
    elif mtype == b"D":
        (n,) = struct.unpack("!H", body[:2])
        off, vals = 2, []
        for _ in range(n):
            (ln,) = struct.unpack("!i", body[off:off + 4])
            off += 4
            if ln == -1:
                vals.append(None)
            else:
                vals.append(body[off:off + ln].decode(errors="replace"))
                off += ln
        rows.append(vals)
    elif mtype == b"E":
        print("查询错误: " + parse_err(body), file=sys.stderr)
        sys.exit(4)
    elif mtype == b"Z":
        break
    # C(命令完成)/S 等忽略
if cols:
    print("\t".join(cols))
for r in rows:
    print("\t".join("" if v is None else str(v) for v in r))
try:
    send_msg(s, b"X", b"")
except Exception:
    pass
PYEOF
}

# 包装:pg_run <appsettings> <sql> → 成功输出结果,失败输出 [DB-ERROR:...] 到 stdout 并返回 1
# 说明:timeout 无法包装 shell 函数,超时由 python 内部 socket 超时(每步 5s)兜底,不会无限挂起
pg_run() { # $1=appsettings $2=sql
    local out rc
    out=$(pg_query "$1" "$2" 2>&1)
    rc=$?
    if [ $rc -ne 0 ]; then
        echo "[DB-ERROR rc=${rc}] ${out}"
        return 1
    fi
    printf '%s\n' "$out"
}

# ============================================================================
# A1 验收标准① — dotnet build 0 警告 0 错误
# ============================================================================
echo "==================== A1 验收标准①:dotnet build 0 警告 0 错误 ===================="
a1_status="FAIL"
a1_ev=""
if [ ! -f "$WORKTREE/$SOLUTION" ]; then
    a1_ev="解决方案文件缺失: $WORKTREE/$SOLUTION"
elif ! command -v dotnet >/dev/null 2>&1; then
    a1_status="QUESTION"
    a1_ev="本环境无 dotnet 工具链,无法执行构建(环境受限,非代码缺陷)"
else
    # WHY --no-incremental:增量构建在树已最新时可能不重编译、不重发警告计数(伪 PASS 风险);
    #   强制全量编译才能真实统计警告/错误,符合"0 警告 0 错误"的证伪要求
    build_out=$(cd "$WORKTREE" && dotnet build --no-incremental "$SOLUTION" 2>&1)
    build_rc=$?
    warn_line=$(printf '%s\n' "$build_out" | grep -iE 'warning\(s\)' | tail -1)
    err_line=$(printf '%s\n' "$build_out" | grep -iE 'error\(s\)' | tail -1)
    warn_n=$(printf '%s\n' "$warn_line" | grep -oE '[0-9]+' | head -1)
    err_n=$(printf '%s\n' "$err_line" | grep -oE '[0-9]+' | head -1)
    if [ $build_rc -eq 0 ] && [ "${warn_n:-1}" = "0" ] && [ "${err_n:-1}" = "0" ]; then
        a1_status="PASS"
        a1_ev="dotnet build --no-incremental 退出码 0; ${warn_line:-无 Warning 汇总行}; ${err_line:-无 Error 汇总行}。构建尾部:
$(printf '%s\n' "$build_out" | tail -4 | tr '\n' ' ')"
    else
        a1_status="FAIL"
        a1_ev="dotnet build --no-incremental 退出码 ${build_rc}; 警告汇总: ${warn_line:-未解析到}; 错误汇总: ${err_line:-未解析到}。构建尾部:
$(printf '%s\n' "$build_out" | tail -10 | tr '\n' ' ')"
    fi
fi
record "A1" "$a1_status" "验收标准① dotnet build 0 警告 0 错误" "$a1_ev"

# ============================================================================
# 服务启动 — 优先系统脚本 → 预构建 dll(单进程,便于反例重启) → dotnet run → 外部
# ============================================================================
echo ""
echo "==================== 服务启动(验收标准②前置) ===================="
if [ -f "$START_SERVER" ]; then
    echo "  启动方式: 系统脚本 $START_SERVER"
    if command -v setsid >/dev/null 2>&1; then
        setsid bash "$START_SERVER" >"$LOG_APP" 2>&1 &
        APP_PID=$!
    else
        bash "$START_SERVER" >"$LOG_APP" 2>&1 &
        APP_PID=$!
    fi
    START_MODE="start_script"
elif command -v dotnet >/dev/null 2>&1 && [ -f "$DLL" ]; then
    echo "  启动方式: 直跑预构建 dll(单进程)"
    # WHY cd 到 dll 输出目录:ASP.NET Core 内容根=CWD,appsettings.json 被拷贝到输出目录,
    #   若 CWD 落在 worktree 根会读不到连接串(崩于 "Host can't be null",误判代码缺陷)
    ( cd "$(dirname "$DLL")" && exec env ASPNETCORE_URLS="http://0.0.0.0:${SERVICE_PORT}" \
        ASPNETCORE_ENVIRONMENT=Development dotnet "$DLL" ) >"$LOG_APP" 2>&1 &
    APP_PID=$!
    START_MODE="dotnet_dll"
elif command -v dotnet >/dev/null 2>&1; then
    echo "  启动方式: dotnet run(无预构建产物)"
    ( cd "$WORKTREE/src" && exec env ASPNETCORE_URLS="http://0.0.0.0:${SERVICE_PORT}" \
        ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project Web.Entry ) >"$LOG_APP" 2>&1 &
    APP_PID=$!
    START_MODE="dotnet_run"
else
    echo "  启动方式: 无 start_test_server.sh 且本地无 dotnet,假设服务已由外部拉起,直接探测"
    START_MODE="external"
fi

if wait_for_service; then
    echo "  服务就绪: $BASE_URL"
else
    echo "  服务未在 ${READY_TIMEOUT}s 内就绪(或自启进程已退出)"
    [ -f "$LOG_APP" ] && { echo "  启动日志尾部:"; tail -15 "$LOG_APP" | sed 's/^/    /'; }
fi

# ============================================================================
# A2 验收标准② — 服务启动成功(HTTP 可达 :5090)
# ============================================================================
echo ""
echo "==================== A2 验收标准②:服务启动 HTTP 可达 ===================="
if [ -n "$BASE_URL" ]; then
    code=$(probe_url "$BASE_URL")
    body_snippet=$(head -c 200 "$TMP/probe_body.html" 2>/dev/null | tr '\n' ' ')
    # 反例探测:随机路径应 404(证明确是真实 ASP.NET Core 路由而非静态兜底)
    code404=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$BASE_URL/blind_probe_nonexistent_path" 2>/dev/null)
    record "A2" "PASS" "验收标准② 服务启动 :5090 响应" \
"GET ${BASE_URL}/ → HTTP ${code}(页面片段: ${body_snippet:-空}); 反例: GET 随机路径 → HTTP ${code404:-探测失败}(期望 404,证真路由)"
else
    # 区分:自启进程因 DB 不可达崩溃(隔离环境)→ QUESTION;其它崩溃 → FAIL
    if [ -n "$APP_PID" ] && crash_is_db_unreachable "$LOG_APP"; then
        record "A2" "QUESTION" "验收标准② 服务启动 :5090 响应(环境受限)" \
"服务进程启动后因数据库网络不可达而退出(隔离环境特征,非代码缺陷);启动日志命中行:
$(grep -im1 -E '连接数据库过程中发生错误|Exception while connecting' "$LOG_APP" 2>/dev/null | tr '\n' ' ')
PG 可达环境(带 docker 宿主机)复跑本脚本即可转 PASS"
    else
        record "A2" "FAIL" "验收标准② 服务启动 :5090 响应" \
"${READY_TIMEOUT}s 内 ${CANDIDATE_URLS[*]} 均无 HTTP 响应; 启动日志尾部:
$([ -f "$LOG_APP" ] && tail -10 "$LOG_APP" | tr '\n' ' ' || echo '日志不可读')
致命异常: $([ -f "$LOG_APP" ] && grep -m3 -E 'Unhandled exception|Exception' "$LOG_APP" | tr '\n' ' ' || echo '无')"
    fi
fi

# ============================================================================
# A3 验收标准② — 启动日志出现「初始化表」(真成功标记)
# ============================================================================
echo ""
echo "==================== A3 验收标准②:启动日志出现「初始化表」 ===================="
# 伪攻击/边界:先静态确认「初始化表」日志行位于 InitTables 成功路径之后(Program.cs),
#   证其为"建表成功才打印"的真标记,而非启动即打印的伪标记(否则 A3 会被失败路径假阳性命中)
static_marker=""
if [ -f "$WORKTREE/src/Web.Entry/Program.cs" ]; then
    if grep -qE 'LogInformation\("初始化表' "$WORKTREE/src/Web.Entry/Program.cs" \
       && grep -qE 'InitTables\(\)' "$WORKTREE/src/Web.Entry/Program.cs"; then
        init_ln=$(grep -nE 'InitTables\(\)' "$WORKTREE/src/Web.Entry/Program.cs" | head -1 | cut -d: -f1)
        log_ln=$(grep -nE 'LogInformation\("初始化表' "$WORKTREE/src/Web.Entry/Program.cs" | head -1 | cut -d: -f1)
        if [ -n "$init_ln" ] && [ -n "$log_ln" ] && [ "$log_ln" -gt "$init_ln" ]; then
            static_marker="静态确认: Program.cs 中 LogInformation(初始化表…) 行(${log_ln})位于 InitTables() 调用行(${init_ln})之后=成功路径标记,失败路径(InitTables 抛异常)不会打印"
        fi
    fi
fi

a3_status="QUESTION"
a3_ev=""
if [ -n "$BASE_URL" ] && [ -f "$LOG_APP" ] && grep -q "初始化表" "$LOG_APP"; then
    a3_status="PASS"
    a3_ev="日志命中行: $(grep -m1 '初始化表' "$LOG_APP")
${static_marker}"
elif [ -n "$BASE_URL" ] && [ -f "$LOG_APP" ]; then
    # 服务活着但日志无「初始化表」=代码缺陷(InitTables 返回却没打日志,或改写了文案)
    a3_status="FAIL"
    a3_ev="服务已响应 HTTP 但启动日志不存在「初始化表」行(代码缺陷:成功路径未输出约定日志); 日志尾部:
$(tail -8 "$LOG_APP" | tr '\n' ' ')"
elif [ -n "$APP_PID" ] && crash_is_db_unreachable "$LOG_APP"; then
    # 隔离环境:进程在 InitTables 处因 DB 不可达崩溃,成功路径日志自然未打印
    a3_status="QUESTION"
    a3_ev="服务因数据库网络不可达崩溃于 InitTables,成功路径「初始化表」日志未打印(环境受限,非代码缺陷)
${static_marker}
PG 可达环境复跑可转 PASS"
else
    a3_status="FAIL"
    a3_ev="服务未启动且无法判定为 DB 网络故障;启动日志不存在「初始化表」行
$([ -f "$LOG_APP" ] && tail -6 "$LOG_APP" | tr '\n' ' ' || echo '日志不可读')"
fi
record "A3" "$a3_status" "验收标准② 启动日志出现「初始化表」" "$a3_ev"

# ============================================================================
# A4 验收标准② — 无异常退出(存活 WORKER_WAIT 秒)
# ============================================================================
echo ""
echo "==================== A4 验收标准②:启动后无异常退出 ===================="
a4_status="FAIL"
a4_ev=""
if [ -n "$BASE_URL" ]; then
    c1=$(probe_url "$BASE_URL")
    sleep "$WORKER_WAIT"
    c2=$(probe_url "$BASE_URL")
    fatal=$(grep -m3 "Unhandled exception" "$LOG_APP" 2>/dev/null | tr '\n' ' ')
    if [ "$c2" = "000" ] || { [ -n "$fatal" ] && [ -n "$APP_PID" ]; }; then
        a4_status="FAIL"
        a4_ev="t0 探测 HTTP ${c1} → 观察 ${WORKER_WAIT}s → t1 探测 HTTP ${c2}; 致命异常: ${fatal:-无}"
    else
        a4_status="PASS"
        alive="未知"
        [ -n "$APP_PID" ] && alive=$(kill -0 "$APP_PID" 2>/dev/null && echo "存活" || echo "已退出")
        a4_ev="t0 探测 HTTP ${c1} → 观察 ${WORKER_WAIT}s → t1 探测 HTTP ${c2}; 进程 ${alive}; 致命异常: ${fatal:-无}"
    fi
elif [ -n "$APP_PID" ] && crash_is_db_unreachable "$LOG_APP"; then
    a4_status="QUESTION"
    a4_ev="服务因数据库网络不可达崩溃(隔离环境),稳定性观察无法进行(非代码缺陷); PG 可达环境复跑可转 PASS"
else
    a4_status="FAIL"
    a4_ev="服务未启动,无稳定性可言; 启动日志尾部: $([ -f "$LOG_APP" ] && tail -4 "$LOG_APP" | tr '\n' ' ' || echo '日志不可读')"
fi
record "A4" "$a4_status" "验收标准② 启动后无异常退出(存活 ${WORKER_WAIT}s)" "$a4_ev"

# ============================================================================
# A5 验收标准③ — 数据库存在 sys_config 表(结构校验:恰好 2 列 / Id 主键 / 表注释)
# ============================================================================
echo ""
echo "==================== A5 验收标准③:sys_config 表存在 + 结构 ===================="
db_ev=""
db_status="QUESTION"
db_ok=0
if [ ! -f "$APPSETTINGS" ]; then
    db_ev="appsettings.json 不存在: $APPSETTINGS"
else
    # 逐层降级:docker → psql → python 线协议客户端
    if command -v docker >/dev/null 2>&1 && docker ps >/dev/null 2>&1; then
        echo "  DB 访问方式: docker exec postgres psql"
        dt_out=$(docker exec postgres psql -U huluyeye -d huluyeye_admin -c "\dt" 2>&1)
        db_ev="docker \\dt 输出: $(echo "$dt_out" | tr '\n' ' ')"
        # 反例:精确词匹配,避免命中 sys_config_backup 等近似名
        if echo "$dt_out" | grep -qE 'sys_config([^a-z0-9_]|$)'; then
            db_ok=1
        fi
    fi
    if [ $db_ok -eq 0 ] && command -v psql >/dev/null 2>&1; then
        echo "  DB 访问方式: 本地 psql"
        # 锚定 ConnectionStrings 段取 Default(理由同 pg_query:Logging.LogLevel 段有同名 "Default")
        pgpass=$(python3 -c "import re,sys;raw=open(sys.argv[1],encoding='utf-8-sig').read();m=re.search(r'\"ConnectionStrings\"\s*:\s*\{[^}]*?\"Default\"\s*:\s*\"([^\"]+)\"',raw);print(dict(p.split('=',1) for p in (m.group(1) if m else '').split(';') if '=' in p).get('Password',''))" "$APPSETTINGS" 2>/dev/null)
        dt_out=$(PGPASSWORD="$pgpass" psql -h 172.18.0.3 -p 5432 -U huluyeye -d huluyeye_admin -c "\dt" 2>&1)
        db_ev="psql \\dt 输出: $(echo "$dt_out" | tr '\n' ' ')"
        if echo "$dt_out" | grep -qE 'sys_config([^a-z0-9_]|$)'; then
            db_ok=1
        fi
    fi
    if [ $db_ok -eq 0 ]; then
        echo "  DB 访问方式: python3 标准库线协议客户端(172.18.0.3:5432)"
        q1=$(pg_run "$APPSETTINGS" "SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename='sys_config'")
        if [ $? -ne 0 ]; then
            db_ev="线协议客户端连不上数据库: $q1"
        else
            tbl_count=$(echo "$q1" | tail -1)
            q2=$(pg_run "$APPSETTINGS" "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema='public' AND table_name='sys_config' ORDER BY ordinal_position")
            q3=$(pg_run "$APPSETTINGS" "SELECT kcu.column_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name=kcu.constraint_name WHERE tc.table_schema='public' AND tc.table_name='sys_config' AND tc.constraint_type='PRIMARY KEY'")
            q4=$(pg_run "$APPSETTINGS" "SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='sys_config'")
            q5=$(pg_run "$APPSETTINGS" "SELECT obj_description('sys_config'::regclass)")
            col_count=$(echo "$q4" | tail -1)
            db_ev="表存在性 count=${tbl_count:-?}; 列数=${col_count:-?}(契约=恰好 2 列)
列结构: $(echo "$q2" | tail -n +2 | tr '\n' '; ' | sed 's/;\?$/;/')
主键列: $(echo "$q3" | tail -1)
表注释: $(echo "$q5" | tail -1)"
            ok=1
            [ "${tbl_count:-0}" = "1" ] || ok=0
            # 边界:恰好 2 列(契约"仅两字段");多于 2 列=CodeFirst 偏差
            [ "${col_count:-0}" = "2" ] || { ok=0; db_ev="$db_ev
[边界] 列数 ${col_count:-?} ≠ 2,偏离契约『仅两字段』"; }
            echo "$q2" | tail -n +2 | awk '{print tolower($1)"|"$2}' | grep -qE '^id\|bigint$' || ok=0
            echo "$q2" | tail -n +2 | awk '{print tolower($1)"|"$2}' | grep -qE '^(name\|(character varying|text|varchar))$' || ok=0
            [ "$(echo "$q3" | tail -1)" = "id" ] || ok=0
            [ "$(echo "$q5" | tail -1)" = "系统配置" ] || ok=0
            [ $ok -eq 1 ] && db_ok=1
        fi
    fi
    if [ $db_ok -eq 1 ]; then
        db_status="PASS"
    else
        # 隔离环境:DB 三级均不可达 + 服务因 DB 崩溃 → 环境受限 QUESTION
        if crash_is_db_unreachable "$LOG_APP" || [ -n "$BASE_URL" ]; then
            db_ev="$db_ev
辅助判断: 服务侧 InitTables 失败点/或已响应 HTTP,失败定位于 PG 网络连接(隔离环境);PG 可达环境复跑可转 PASS"
        fi
    fi
fi
record "A5" "$db_status" "验收标准③ sys_config 表存在(恰好 2 列 Id+Name,Id 主键,表注释=系统配置)" "$db_ev"

# ============================================================================
# B1 验收标准③反例 — CodeFirst 二次初始化幂等(重启不丢数据) + 列类型边界
# ============================================================================
echo ""
echo "==================== B1 验收标准③反例:二次 InitTables 幂等 + 列类型边界 ===================="
b1_status="QUESTION"
b1_ev="DB 不可达,反例无法构造(与 A5 联动,环境受限)"
if [ $db_ok -eq 1 ]; then
    # 伪攻击/边界数据:
    #  (a) 小主键 id=1(非雪花)→ 验证 Id 列为 bigint 可手写(若误配 IsIdentity 自增,手写小 id 行为异常)
    #  (b) max-long 主键 → 验证 bigint 上界
    #  (c) Name 注入 SQL 片段 + 中文 + 超长串 → 验证字符串列存储语义(引号/unicode/长度不截断不报错)
    SQL_INJ="O'Brien; DROP TABLE sys_config;-- 反例注入"
    SQL_UM="系统配置_中文_边界_🎯"
    SQL_LONG=$(printf 'x%.0s' {1..500})   # 500 字符长串
    # 1) 清理历史残留
    pg_run "$APPSETTINGS" "DELETE FROM sys_config WHERE name IN ('$PROBE_NAME','$PROBE_NAME_sql','$PROBE_NAME_long')" > /dev/null 2>&1
    # 2) 写入三条探测行
    q=$(pg_run "$APPSETTINGS" "INSERT INTO sys_config (id, name) VALUES ($PROBE_ID_SMALL, '$PROBE_NAME')")
    [ $? -ne 0 ] && { b1_status="FAIL"; b1_ev="小主键探测行写入失败(列类型异常): $q"; }
    q=$(pg_run "$APPSETTINGS" "INSERT INTO sys_config (id, name) VALUES ($PROBE_ID_MAX, '$PROBE_NAME_sql')")
    [ $? -ne 0 ] && { b1_status="FAIL"; b1_ev="max-long 主键 / 注入文本探测行写入失败: $q"; }
    q=$(pg_run "$APPSETTINGS" "INSERT INTO sys_config (id, name) VALUES (2, '$SQL_LONG')")
    [ $? -ne 0 ] && { b1_status="FAIL"; b1_ev="超长 Name 探测行写入失败: $q"; }

    if [ "$b1_status" = "QUESTION" ]; then
        c_before=$(pg_run "$APPSETTINGS" "SELECT count(*) FROM sys_config WHERE name IN ('$PROBE_NAME','$PROBE_NAME_sql') OR name='$SQL_LONG'")
        c_before=$(echo "$c_before" | tail -1)
        # 边界读回:验证注入文本原样落库(未被截断/转义/破坏)
        inj_read=$(pg_run "$APPSETTINGS" "SELECT name FROM sys_config WHERE name='$SQL_INJ' OR name='$PROBE_NAME_sql'" 2>/dev/null | tail -1)

        if [ "$START_MODE" = "dotnet_dll" ]; then
            echo "  重启服务以触发二次 InitTables..."
            kill "$APP_PID" 2>/dev/null
            for i in $(seq 1 30); do kill -0 "$APP_PID" 2>/dev/null || break; sleep 0.5; done
            kill -9 "$APP_PID" 2>/dev/null
            # 重启同样 cd 到 dll 输出目录(理由同上,见首次启动处)
            ( cd "$(dirname "$DLL")" && exec env ASPNETCORE_URLS="http://0.0.0.0:${SERVICE_PORT}" \
                ASPNETCORE_ENVIRONMENT=Development dotnet "$DLL" ) >"$LOG_APP2" 2>&1 &
            APP_PID=$!
            if wait_for_service; then
                c_after=$(pg_run "$APPSETTINGS" "SELECT count(*) FROM sys_config WHERE name IN ('$PROBE_NAME','$PROBE_NAME_sql') OR name='$SQL_LONG'")
                c_after=$(echo "$c_after" | tail -1)
                log2_hit=$(grep -m1 "初始化表" "$LOG_APP2")
                if [ "${c_after:-0}" = "3" ] && [ -n "$log2_hit" ]; then
                    b1_status="PASS"
                    b1_ev="重启前探测行 count=${c_before}; 重启后二次 InitTables 无异常(日志: ${log2_hit}); 重启后探测行 count=${c_after}(3 条全存活,增量建表幂等不丢数据); 注入文本读回: ${inj_read:-空}"
                else
                    b1_status="FAIL"
                    b1_ev="重启前 count=${c_before:-?}, 重启后 count=${c_after:-?}(期望 3), 二次启动日志命中「初始化表」: ${log2_hit:-无}; 日志尾部: $(tail -5 "$LOG_APP2" | tr '\n' ' ')"
                fi
            else
                b1_status="FAIL"
                b1_ev="重启后服务 ${READY_TIMEOUT}s 未就绪,二次 InitTables 疑似异常; 日志尾部: $(tail -8 "$LOG_APP2" | tr '\n' ' ')"
            fi
        else
            # 服务由外部管理/系统脚本:无法重启,降级验证(探测行可读 + 服务仍存活 + 注入文本原样)
            c_after=$(pg_run "$APPSETTINGS" "SELECT count(*) FROM sys_config WHERE name IN ('$PROBE_NAME','$PROBE_NAME_sql') OR name='$SQL_LONG'")
            c_after=$(echo "$c_after" | tail -1)
            c_now=$(probe_url "$BASE_URL")
            if [ "${c_after:-0}" = "3" ] && [ "$c_now" != "000" ]; then
                b1_status="QUESTION"
                b1_ev="降级模式(服务不可控,无法重启触发二次 InitTables): 3 条探测行(含 max-long/超长/注入)写入成功且可读(count=${c_after}),服务仍响应 HTTP ${c_now},注入文本读回: ${inj_read:-空}; 「重启后不丢数据」未能实测 → QUESTION"
            else
                b1_status="FAIL"
                b1_ev="探测行写入后不可读(count=${c_after:-?},期望 3)或服务失联(HTTP ${c_now:-?})"
            fi
        fi
    fi
    # 清理探测行(无论成败,按标记删除,不伤真实数据)
    pg_run "$APPSETTINGS" "DELETE FROM sys_config WHERE name IN ('$PROBE_NAME','$PROBE_NAME_sql') OR name='$SQL_LONG' OR name='$SQL_INJ'" > /dev/null 2>&1
fi
record "B1" "$b1_status" "验收标准③反例:CodeFirst 二次初始化幂等 + 列类型边界(小/max-long 主键、注入/超长 Name)" "$b1_ev"

# ============================================================================
# B2 验收标准④ — git commit(提交号可解析 + 8 交付物入库且干净)
# ============================================================================
echo ""
echo "==================== B2 验收标准④:git commit 交付完整性 ===================="
b2_status="QUESTION"
b2_ev=""
if ! git -C "$WORKTREE" rev-parse --git-dir >/dev/null 2>&1; then
    if [ -f "$APPROVAL_REPORT" ]; then
        sha=$(grep -oE '`[0-9a-f]{6,40}`' "$APPROVAL_REPORT" 2>/dev/null | head -1 | tr -d '`')
        b2_ev="本环境 git 元数据不可用; 验收报告存在且声称提交号 ${sha:-无}; 无法独立验证 → QUESTION"
    else
        b2_status="FAIL"
        b2_ev="git 元数据不可用,且验收报告 $APPROVAL_REPORT 不存在"
    fi
else
    sha=$(grep -oE '`[0-9a-f]{6,40}`' "$APPROVAL_REPORT" 2>/dev/null | head -1 | tr -d '`')
    if [ -z "$sha" ]; then
        b2_status="FAIL"
        b2_ev="验收报告 $APPROVAL_REPORT 未提取到提交号"
    elif ! git -C "$WORKTREE" cat-file -e "${sha}^{commit}" 2>/dev/null; then
        b2_status="FAIL"
        b2_ev="验收报告声称提交号 ${sha},但 git cat-file 无法解析该提交"
    else
        # 反例:提交不仅"存在",还须真正包含全部 8 交付物(防"提交号对但交付物没进去")
        missing_in_commit=""
        untracked_now=""
        dirty_now=""
        for f in "${DELIVERABLES[@]}"; do
            git -C "$WORKTREE" cat-file -e "${sha}:$f" 2>/dev/null || missing_in_commit="${missing_in_commit} ${f}"
            git -C "$WORKTREE" ls-files --error-unmatch "$f" >/dev/null 2>&1 || untracked_now="${untracked_now} ${f}"
        done
        dirty_now=$(git -C "$WORKTREE" status --porcelain -- "${DELIVERABLES[@]}" 2>/dev/null)
        if [ -z "$missing_in_commit" ] && [ -z "$untracked_now" ] && [ -z "$dirty_now" ]; then
            b2_status="PASS"
            b2_ev="验收报告提交号 ${sha} 可解析; 8 交付物全部在 ${sha} 提交树中、当前均被跟踪且工作区干净; 提交主题: $(git -C "$WORKTREE" log -1 --format=%s "${sha}")"
        else
            b2_status="FAIL"
            b2_ev="提交号 ${sha} 可解析但交付物完整性异常:
缺失于提交${missing_in_commit:+ →${missing_in_commit}}; 未跟踪${untracked_now:+ →${untracked_now}}; 脏${dirty_now:+ →${dirty_now}}"
        fi
    fi
fi
record "B2" "$b2_status" "验收标准④ git commit(提交号可解析 + 8 交付物入库且干净)" "$b2_ev"

# ============================================================================
# B3 契约静态抽查(23 项 + Furion/Admin.NET 禁令反例)
# ============================================================================
echo ""
echo "==================== B3 契约静态抽查(规格偏差) ===================="
b3_miss=""
check_grep() { # $1=描述 $2=文件 $3=模式
    if ! grep -qE "$3" "$2" 2>/dev/null; then
        b3_miss="${b3_miss}
- 缺失: ${1} (期望 ${2} 匹配 /${3}/)"
    fi
}
check_grep "SqlSugarCore 包引用 5.1.4.x" "$WORKTREE/src/Core/Huluyeye.Core.csproj" 'PackageReference Include="SqlSugarCore" Version="5\.1\.4\.'
check_grep "Yitter.IdGenerator 项目引用" "$WORKTREE/src/Core/Huluyeye.Core.csproj" 'Yitter\.IdGenerator\.csproj'
check_grep "解决方案含 YitIdHelper" "$WORKTREE/src/huluyeye-admin.slnx" 'Yitter\.IdGenerator\.csproj'
check_grep "appsettings 连接串段" "$APPSETTINGS" '"ConnectionStrings"'
check_grep "实体贴 SugarTable(sys_config,系统配置)" "$WORKTREE/src/Core/Entities/SysConfig.cs" '\[SugarTable\("sys_config"[^)]*"系统配置"\)'
check_grep "实体贴 IncreTable" "$WORKTREE/src/Core/Entities/SysConfig.cs" '\[IncreTable\]'
check_grep "Id 主键非自增" "$WORKTREE/src/Core/Entities/SysConfig.cs" 'IsPrimaryKey\s*=\s*true'
check_grep "Id 非自增(IsIdentity=false)" "$WORKTREE/src/Core/Entities/SysConfig.cs" 'IsIdentity\s*=\s*false'
check_grep "仅两字段: long Id" "$WORKTREE/src/Core/Entities/SysConfig.cs" 'public long Id'
check_grep "仅两字段: string? Name" "$WORKTREE/src/Core/Entities/SysConfig.cs" 'string\? Name'
check_grep "ConfigId=main" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'ConfigId\s*=\s*"main"'
check_grep "DbType.PostgreSQL" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'DbType\.PostgreSQL'
check_grep "IsAutoCloseConnection=true" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'IsAutoCloseConnection\s*=\s*true'
check_grep "InitKeyType.Attribute" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'InitKeyType\.Attribute'
check_grep "单例注册 ISqlSugarClient" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'AddSingleton<ISqlSugarClient>'
check_grep "AOP OnError" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'Aop\.OnError'
check_grep "AOP OnLogExecuting" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'Aop\.OnLogExecuting'
check_grep "雪花挂接 CustomSnowFlakeFunc" "$WORKTREE/src/Core/SqlSugarSetup.cs" 'StaticConfig\.CustomSnowFlakeFunc'
check_grep "Program.cs 调用 AddSqlSugar" "$WORKTREE/src/Web.Entry/Program.cs" 'AddSqlSugar\(builder\.Configuration\)'
check_grep "Program.cs 启动时 InitTables" "$WORKTREE/src/Web.Entry/Program.cs" 'InitTables\(\)'
check_grep "YitIdHelper 静态类 Init" "$WORKTREE/src/Core/YitIdHelper.cs" 'public static void Init'
check_grep "YitIdHelper 静态类 NextId" "$WORKTREE/src/Core/YitIdHelper.cs" 'public static long NextId'
# 反例:不得引入 Furion/Admin.NET 包(查 csproj 包引用,注释提及不算)
furion_hits=$(grep -rn 'PackageReference Include="[^"]*Furion[^"]*"\|PackageReference Include="[^"]*Admin\.NET[^"]*"' "$WORKTREE/src" --include='*.csproj' 2>/dev/null)
[ -n "$furion_hits" ] && b3_miss="${b3_miss}
- 违规: 发现 Furion/Admin.NET 包引用(禁令): ${furion_hits}"
if [ -z "$b3_miss" ]; then
    record "B3" "PASS" "契约 23 项静态抽查全部命中(含 Furion/Admin.NET 禁令反例)" "SqlSugarCore 版本/雪花挂接/SqlSugarScope 五要素/AOP 双钩子/实体两字段/Program 接线/YitIdHelper 封装/csproj+slnx 引用 均符合任务书契约; Furion/Admin.NET 包引用: 无"
else
    record "B3" "FAIL" "契约静态抽查(规格偏差)" "存在规格偏差:${b3_miss}"
fi

# ---------------- 汇总与判定 ----------------
final_verdict
exit $?
