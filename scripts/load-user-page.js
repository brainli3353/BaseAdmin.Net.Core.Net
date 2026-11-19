// 用户管理列表接口压测 —— 目标:并发100 / QPS300 / P95 延迟 ≤200ms
// 运行: k6 run -e TOKEN="<access_token>" scripts/load-user-page.js
import { check } from 'k6';
import http from 'k6/http';

const TOKEN = __ENV.TOKEN;
const BASE = __ENV.BASE_URL || 'http://127.0.0.1:5090';
const PAGE_SIZE = 20;
const RATE = Number(__ENV.RATE) || 300;           // 目标 QPS(仅 arrival 模式生效)
const DURATION = __ENV.DURATION || '60s';          // 时长,可用 -e DURATION=10s 冒烟
// MODE=arrival:强制 300 QPS 到达率看系统能否接住(接不住→dropped_iterations 上升)
// MODE=vus    :固定 100 个并发真实用户,量实际吞吐与 P95(最贴合"并发数 100")
const MODE = __ENV.MODE || 'arrival';

if (!TOKEN) {
  throw new Error('缺少 TOKEN 环境变量: 请先用 scripts/get-token.ps1 获取 JWT');
}

const scenarios = (MODE === 'vus')
  ? {
      userPageVus: {
        executor: 'constant-vus',   // 固定并发用户数,不加压也不丢请求,纯量吞吐
        vus: 100,                    // 并发 100
        duration: DURATION,
        exec: 'userPage',
      },
    }
  : {
      userPage300: {
        executor: 'constant-arrival-rate',
        rate: RATE,
        timeUnit: '1s',
        duration: DURATION,
        preAllocatedVUs: 100,
        maxVUs: 100,
        exec: 'userPage',
      },
    };

export const options = {
  scenarios,
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    'http_req_duration': ['p(95)<=200'], // P95 延迟 ≤ 200ms
  },
};

export function userPage() {
  const res = http.get(
    `${BASE}/api/sysUser/page?pageIndex=1&pageSize=${PAGE_SIZE}`,
    { headers: { Authorization: `Bearer ${TOKEN}`, 'Accept-Language': 'zh-CN' } }
  );
  check(res, { 'status 200': (r) => r.status === 200 });
  // 无 sleep: arrival-rate 场景由执行器控节奏,保持最高吞吐
}