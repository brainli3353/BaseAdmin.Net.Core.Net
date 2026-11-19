#!/usr/bin/env bash
# =============================================================================
# doctor.sh —— 新仓环境自检【骨架】
# =============================================================================
# 这是骨架，接入方按需补实检查：
#   - 每一项都是"轻量探测"（curl / TCP 连接），不引入任何第三方依赖；
#   - 全部检查由 env 驱动（见下方「环境变量表」），缺省给合理默认；
#   - 核心纪律：外部资源【未配置或不可达】时输出 `SKIP: <项> 原因`，
#     骨架阶段【不误红】——默认整体 exit 0（详见 STRICT 开关）；
#   - PASS/FAIL/SKIP 三种状态行，输出人可读、可被脚本 grep。
#
# 环境变量表（全部可选，缺省见下）：
#   BASE_URL   应用基址       默认 http://127.0.0.1:${PORT}（仅显式配置才探测应用）
#   PORT       应用监听端口   默认 5090（仅显式配置才做端口占用判定）
#   LOGIN_URL  登录端点       默认 ${BASE_URL}/api/auth/login（BASE_URL 未配则跳过）
#   DB_HOST    数据库主机     默认空（未配置 => SKIP）
#   DB_PORT    数据库端口     默认 5432
#   REDIS_HOST 缓存主机       默认空（未配置 => SKIP）
#   REDIS_PORT 缓存端口       默认 6379
#   OTP_ISSUER OTP issuer     默认空（未配置 => SKIP）
#   OTP_ENABLED OTP 开关      默认空（未配置 => SKIP）
#   DOCTOR_STRICT  1=存在 FAIL 时 exit 1；默认 0=骨架阶段恒 exit 0
#   CURL_TIMEOUT   单次 HTTP 超时秒  默认 5
#   TCP_TIMEOUT    单次 TCP 超时秒   默认 3
# =============================================================================

set -u

# ---- 状态计数与输出（PASS/FAIL/SKIP 三态） --------------------------------
PASS_N=0
FAIL_N=0
SKIP_N=0

pass() { PASS_N=$((PASS_N + 1)); printf 'PASS: %s\n' "$1"; }
fail() { FAIL_N=$((FAIL_N + 1)); printf 'FAIL: %s\n' "$1"; }
skip() { SKIP_N=$((SKIP_N + 1)); printf 'SKIP: %s\n' "$1"; }

# ---- env 缺省 --------------------------------------------------------------
PORT_SET="${PORT:+1}"          # 仅"显式配置了 PORT"才做端口占用判定（空环境全 SKIP）
PORT="${PORT:-5090}"
BASE_URL_SET="${BASE_URL:+1}"  # 仅"显式配置了 BASE_URL"才探测应用（避免误探同端口的无关服务）
BASE_URL="${BASE_URL:-http://127.0.0.1:${PORT}}"
LOGIN_URL_SET="${LOGIN_URL:+1}"
LOGIN_URL="${LOGIN_URL:-${BASE_URL}/api/auth/login}"
DB_HOST="${DB_HOST:-}"
DB_PORT="${DB_PORT:-5432}"
REDIS_HOST="${REDIS_HOST:-}"
REDIS_PORT="${REDIS_PORT:-6379}"
OTP_ISSUER="${OTP_ISSUER:-}"
OTP_ENABLED="${OTP_ENABLED:-}"
DOCTOR_STRICT="${DOCTOR_STRICT:-0}"
CURL_TIMEOUT="${CURL_TIMEOUT:-5}"
TCP_TIMEOUT="${TCP_TIMEOUT:-3}"

# ---- 工具探测 --------------------------------------------------------------
have_curl() { command -v curl >/dev/null 2>&1; }

# TCP 可达探测：连得上回 0，连不上回 1（纯 bash /dev/tcp，零依赖）
tcp_probe() {
    local host="$1" port="$2"
    timeout "${TCP_TIMEOUT}" bash -c "exec 3<>/dev/tcp/${host}/${port}" >/dev/null 2>&1
}

if [ -z "${BASE_URL_SET}" ]; then
    echo "=== doctor 骨架自检（BASE_URL 未配置，应用相关探测跳过）==="
else
    echo "=== doctor 骨架自检（BASE_URL=${BASE_URL}）==="
fi

# ---- ① 应用 health ---------------------------------------------------------
if [ -z "${BASE_URL_SET}" ]; then
    skip "应用 health（原因：BASE_URL 未配置——应用资源未接入，骨架阶段不判失败）"
elif ! have_curl; then
    skip "应用 health（原因：本机无 curl 命令，无法探测）"
else
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "${CURL_TIMEOUT}" "${BASE_URL}/health" 2>/dev/null || echo 000)"
    if [ "${code}" = "000" ]; then
        skip "应用 health（原因：${BASE_URL}/health 不可达——应用未起或未配置）"
    elif [ "${code}" = "200" ]; then
        pass "应用 health（${BASE_URL}/health -> HTTP 200）"
    else
        fail "应用 health（${BASE_URL}/health -> HTTP ${code}，非 200）"
    fi
fi

# ---- ② 登录端点可达 --------------------------------------------------------
if [ -z "${BASE_URL_SET}" ] && [ -z "${LOGIN_URL_SET}" ]; then
    skip "登录端点可达（原因：BASE_URL / LOGIN_URL 均未配置——应用资源未接入，骨架阶段不判失败）"
elif ! have_curl; then
    skip "登录端点可达（原因：本机无 curl 命令，无法探测）"
else
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "${CURL_TIMEOUT}" "${LOGIN_URL}" 2>/dev/null || echo 000)"
    if [ "${code}" = "000" ]; then
        skip "登录端点可达（原因：${LOGIN_URL} 不可达——端点未起或 URL 未配置）"
    else
        # 登录端点对 GET/POST 的合理响应是 400/401/405 等（非 5xx 即可视为"可达"）
        case "${code}" in
            5*) fail "登录端点可达（${LOGIN_URL} -> HTTP ${code}，服务端 5xx）" ;;
            *)  pass "登录端点可达（${LOGIN_URL} -> HTTP ${code}）" ;;
        esac
    fi
fi

# ---- ③ 数据库可达（TCP 探测） ---------------------------------------------
if [ -z "${DB_HOST}" ]; then
    skip "数据库可达（原因：DB_HOST 未配置——外部资源未接入，骨架阶段不判失败）"
elif tcp_probe "${DB_HOST}" "${DB_PORT}"; then
    pass "数据库可达（${DB_HOST}:${DB_PORT} TCP 可连）"
else
    skip "数据库可达（原因：${DB_HOST}:${DB_PORT} TCP 不可达——外部资源未就绪）"
fi

# ---- ④ Redis／缓存档可达（TCP 探测） --------------------------------------
if [ -z "${REDIS_HOST}" ]; then
    skip "Redis/缓存可达（原因：REDIS_HOST 未配置——外部资源未接入，骨架阶段不判失败）"
elif tcp_probe "${REDIS_HOST}" "${REDIS_PORT}"; then
    pass "Redis/缓存可达（${REDIS_HOST}:${REDIS_PORT} TCP 可连）"
else
    skip "Redis/缓存可达（原因：${REDIS_HOST}:${REDIS_PORT} TCP 不可达——外部资源未就绪）"
fi

# ---- ⑤ OTP 配置状态 --------------------------------------------------------
if [ -z "${OTP_ENABLED}" ] && [ -z "${OTP_ISSUER}" ]; then
    skip "OTP 配置状态（原因：OTP_ENABLED / OTP_ISSUER 均未配置——骨架阶段不判失败）"
else
    pass "OTP 配置状态（OTP_ENABLED=${OTP_ENABLED:-<未设>} OTP_ISSUER=${OTP_ISSUER:-<未设>}）"
fi

# ---- ⑥ 端口占用 ------------------------------------------------------------
if [ -z "${PORT_SET}" ]; then
    skip "端口占用（原因：PORT 未显式配置——骨架阶段不判端口）"
elif tcp_probe "127.0.0.1" "${PORT}"; then
    pass "端口占用（127.0.0.1:${PORT} 已被监听，符合应用运行预期）"
else
    fail "端口占用（127.0.0.1:${PORT} 空闲，应用未监听该端口）"
fi

# ---- 汇总与退出码 ----------------------------------------------------------
echo "=== 汇总：PASS=${PASS_N} FAIL=${FAIL_N} SKIP=${SKIP_N} ==="

if [ "${DOCTOR_STRICT}" = "1" ] && [ "${FAIL_N}" -gt 0 ]; then
    echo "（DOCTOR_STRICT=1：存在 FAIL，exit 1）"
    exit 1
fi

echo "（骨架阶段：外部资源未配置/不可达一律 SKIP，整体 exit 0；需要严格判红请设 DOCTOR_STRICT=1）"
exit 0