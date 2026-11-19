/** 批 CL G31:全局实时通道(SignalR)——登录态生命周期 + 服务端四消息订阅。
 *
 * 服务端契约(src/Core/Hub/IOnlineUserHub.cs,批 F/G/AV/AU 全就位):
 * - onlineUserList(在线列表变动,载荷=全量列表)→ 模块级监听器分发(onlineUser 页实时列表)
 * - forceOffline(强制下线,参数=提示文案)→ 本地清会话+回登录页(服务端黑名单已生效,不再调 API)
 * - publicNotice(通知公告发布推送,载荷=通知实体)→ ElNotification 弹显
 * - receiveMessage(站内消息,载荷=MessageInput 或消息日志快照)→ ElNotification 弹显
 *
 * 连接:同源相对路径 /hubs/onlineUser(dev 走 vite 代理、生产同源托管,无跨域);
 * accessTokenFactory=既有 access token(批 N 双 JWT 同签名密钥);withAutomaticReconnect(官方默认退避)。
 * 生命周期:守卫首次登录态验证成功后 startRealtime(幂等);user.reset(登出/401 失效/强踢)时 stopRealtime。
 * 降级:首连失败静默(仅 console.warn)——实时面离线,REST 查询面不受影响;刷新页面可重连。 */
import {
	HubConnectionBuilder,
	HubConnectionState,
	type HubConnection
} from '@microsoft/signalr'
import { ElMessage, ElNotification } from 'element-plus'
import { i18n } from '@/i18n'
import { getAccessToken } from '@/utils/token'
import { useUserStore } from '@/stores/user'
import router from '@/router'

/** onlineUserList 载荷(与 OnlineUserListMessage 对应;userList 全量=当前租户在线列表) */
export interface OnlineUserListPayload {
	realName?: string
	online: boolean
	userList: unknown[]
}

let conn: HubConnection | null = null
let starting = false
const listListeners = new Set<(m: OnlineUserListPayload) => void>()

/** 订阅在线列表变动;返回取消订阅函数(页面 onUnmounted 调用) */
export function onOnlineUserList(fn: (m: OnlineUserListPayload) => void): () => void {
	listListeners.add(fn)
	return () => {
		listListeners.delete(fn)
	}
}

/** 当前连接状态(调试/测试观测面) */
export function realtimeState(): HubConnectionState {
	return conn ? conn.state : HubConnectionState.Disconnected
}

/** 建立实时连接(幂等:已连接/连接中直接返回;无 token 不建连) */
export function startRealtime(): void {
	if (conn || starting) return
	if (!getAccessToken()) return
	starting = true

	const userStore = useUserStore()
	const t = (key: string, params?: Record<string, unknown>) =>
		i18n.global.t(key, params ?? {}) as string

	const c = new HubConnectionBuilder()
		.withUrl('/hubs/onlineUser', { accessTokenFactory: () => getAccessToken() ?? '' })
		.withAutomaticReconnect()
		.build()

	c.on('onlineUserList', (m: OnlineUserListPayload) => {
		for (const fn of listListeners) fn(m)
	})
	c.on('forceOffline', (msg: string) => {
		stopRealtime()
		ElMessage.error(t('realtime.forceOffline', { msg: msg || '' }))
		userStore.reset()
		void router.push({ path: '/login', replace: true })
	})
	c.on('publicNotice', (n: { title?: string; content?: string } | null) => {
		ElNotification({
			title: t('realtime.noticeTitle'),
			message: n?.title ? `${n.title}` : '',
			type: 'warning',
			duration: 8000
		})
	})
	c.on('receiveMessage', (m: { title?: string; message?: string } | null) => {
		ElNotification({
			title: t('realtime.messageTitle'),
			message: m?.message ?? m?.title ?? '',
			type: 'info',
			duration: 8000
		})
	})

	void c
		.start()
		.then(() => {
			conn = c
		})
		.catch((ex: unknown) => {
			// 首连失败=实时面降级(REST 不受影响);不重试,刷新页面重走守卫可重建
			console.warn('[realtime] start failed, realtime degraded:', ex)
		})
		.finally(() => {
			starting = false
		})
}

/** 断开实时连接(幂等;登出/强踢路径) */
export function stopRealtime(): void {
	starting = false
	if (conn) {
		const c = conn
		conn = null
		void c.stop().catch(() => {})
	}
	listListeners.clear()
}
