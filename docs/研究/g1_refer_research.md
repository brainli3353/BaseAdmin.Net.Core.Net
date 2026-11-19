# 参照仓 AdminNetPro「通知公告＋站内消息」实现调研（批 G 移植唯一依据）

> 建立：2026-09-15（批 G 4.7/4.8；4.7/4.8 在 81 功能点清单定位为「依赖 SignalR，批 F 后」——批 F 闭合后前置齐备）
> 事实源：`../AdminNetPro/Admin.NET.Pro.HHBase/Admin.NET/`（只读红线 #7）；批 F 已落地的 OnlineUserHub/SysOnlineUserService/事件总线为本批依赖基座（docs/研究/f1_refer_research.md）。

## 0. 移植落点映射表（速查）

| 参照仓（Admin.NET） | 本仓（BaseAdminCore） | 备注 |
|---|---|---|
| `Entity/SysNotice.cs` | `src/Core/Entities/SysNotice.cs` | EntityTenant（参照同） |
| `Entity/SysNoticeUser.cs` | `src/Core/Entities/SysNoticeUser.cs` | 参照 `EntityBaseId`（仅 Id）→ 本仓 `EntityBase`（＋审计/软删，对齐 SysUserRole 等中间表口径） |
| `Entity/SysLogMsg.cs` | `src/Core/Entities/SysLogMsg.cs` | 参照 `EntityTenantId`＋`[LogTable]` → 本仓 `EntityTenant`＋`[LogTable]`（对齐 SysLogLogin 口径）；**去 Device 字段**（本仓无设备指纹，批 F 同口径） |
| `Enum/NoticeTypeEnum.cs` 等 3 枚举 | `src/Core/Enums/` 同名 | 值域同 |
| `Enum/MessageTypeEnum.cs` | `src/Core/Enums/MessageTypeEnum.cs` | 值域同 |
| `Service/Notice/SysNoticeService.cs` | `src/Core/Services/Notice/SysNoticeService.cs`＋Dto/ | Furion 动态 API → 本仓显式 Controller |
| `Service/Message/SysMessageService.cs` | `src/Core/Services/Message/SysMessageService.cs`＋Dto/ | 纯推送模型（无独立消息实体）＋消息日志走事件总线 |
| `AppEventSubscriber.CreateMsgLog`（事件订阅） | `src/Core/Services/Message/MsgLogSubscriber.cs` | 本仓事件总线 `IEventSubscriber` 模式（对齐 OpLogSubscriber） |
| `Service/OnlineUser/SysOnlineUserService.PublicNotice` | `SysOnlineUserService.PublicNoticeAsync`（批 F 服务新增方法） | 广播通知到在线用户 |
| `Hub/IOnlineUserHub.PublicNotice/ReceiveMessage` | 批 F 裁掉的 2 方法**本批补回**（消息面 2→4） | docs/研究/f1_refer_research.md §9 裁决⑨的落地兑现 |
| 无独立 Controller（Furion IDynamicApiController） | `SysNoticeController`/`SysMessageController`（/api 路由＋`[MenuPerm]`） | 本仓控制器口径 |

## 1. SysNotice 实体字段（参照字面量）

| 字段 | 类型/长度 | 说明 |
|---|---|---|
| Title | string 256 | 标题 |
| Content | 大文本（text） | 内容 |
| Type | 1 通知 / 2 公告 | `NoticeTypeEnum`（NOTICE=1/ANNOUNCEMENT=2），索引 `i_{table}_t` |
| PublicUserId | long | 发布人 Id |
| PublicUserName | string 32 | 发布人姓名 |
| PublicOrgId | long | 发布机构 Id |
| PublicOrgName | string 64 | 发布机构名称 |
| PublicTime | DateTime? | 发布时间 |
| CancelTime | DateTime? | 撤回时间 |
| Status | 0 草稿 / 1 发布 / 2 撤回 / 3 删除 | `NoticeStatusEnum`，默认 DRAFT |

- 基类 `EntityTenant`（参照同）；`[SensitiveDetection]`（Furion 特性）不移植（零 Furion 口径）。

## 2. SysNoticeUser 实体字段（参照字面量）

| 字段 | 说明 |
|---|---|
| NoticeId | 通知公告 Id（OneToOne 导航参照有——本仓不用导航属性，PageReceived 用 LeftJoin/Includes 手动映射，批 E 导航泄露教训） |
| UserId | 接收用户 Id |
| ReadTime | DateTime? 阅读时间 |
| ReadStatus | 0 未读 / 1 已读（`NoticeUserStatusEnum`，默认 UNREAD） |

## 3. SysNoticeService 九方法（参照逻辑逐条）

| 方法 | 参照逻辑 | 本仓口径 |
|---|---|---|
| `Page` | Title 模糊/Type 过滤/**非超管仅本人发布**（`CreateUserId == UserId`）/CreateTime 降序 | 同 |
| `AddNotice` | `InitNoticeInfo`（发布人 Id/姓名/机构 Id 自填）＋插入 | 同；机构名称本仓自填（UserManager.OrgId 查机构名，参照仅填 OrgId 由导航补名） |
| `UpdateNotice` | **仅发布人本人可改**（`CreateUserId != UserId` → D7003） | 同 |
| `DeleteNotice` | 仅发布人本人＋**已发布禁删**（D7001）＋级联删 SysNoticeUser | 同（软删口径：本仓 DeleteAsync=软删，SysNoticeUser 同软删） |
| `Public` | 发布人校验→Status=PUBLIC＋PublicTime→**userIds 空=通知所有人**（查全量用户 Id）→先删后插 SysNoticeUser（幂等重发）→广播在线 `PublicNotice(notice, userIdList)` | 同；「所有人」= 全租户用户（参照查全库用户——本仓租户过滤下 `AsQueryable` 自动限本租户，**强于参照**） |
| `SetRead` | 更新本人该通知 ReadStatus=READ＋ReadTime | 同 |
| `PageReceived` | Includes(SysNotice)＋本人 UserId＋Title/Type 过滤（经导航字段）＋CreateTime 降序 | 本仓 `LeftJoin SysNotice` 手动映射（不用导航） |
| `GetUnReadList` | 本人 UNREAD 列表（Includes 取通知） | 同（LeftJoin） |
| `GetLastNotice` / `GetNotice` | 最新通知 / 详情 | 同 |

- 错误码：参照用 `Oops.Oh(ErrorCodeEnum.D7001/D7003)`；本仓口径 `AppException`（消息文本稳定可检索，#10）：「仅发布人本人可操作」/「已发布的通知公告不可删除」。

## 4. SysMessageService 两方法（参照逻辑逐条）

| 方法 | 参照逻辑 |
|---|---|
| `SendAllUser(MessageInput)` | `Clients.All.ReceiveMessage(input)` 广播（不落日志） |
| `SendUser(MessageInput)` | 在线表 HashGetAll → 查发送人快照＋接收人在线连接集 → 逐个 `Clients.Client(cid).ReceiveMessage(logMsg)`（**载荷=SysLogMsg 全量**，非原始 input）→ 每接收人一条 `SysLogMsg` 经事件总线 `PublishAsync(nameof(AppEventSubscriber.CreateMsgLog), logMsg)` 落日志库 |

- **纯推送模型**：无独立消息实体（消息日志 SysLogMsg 即唯一持久面，[LogTable] 日志库）；接收人离线 → 不推送不落日志（参照口径，**消息不存储不补发**——自行裁决①，与 4.7 通知公告的落库存储形成互补）。
- `MessageInput`：MessageType/Title/Message/SendUserId/ReceiveUserIds。

## 5. SysLogMsg 实体字段（参照字面量，去 Device）

MessageType(64)/Title(64)/Message(text)/ReceiveUserId/ReceiveUserName(32)/ReceiveIp(256)/ReceiveBrowser(128)/ReceiveOs(128)/SendUserId/SendUserName(32)/SendIp(256)/SendBrowser(128)/SendOs(128)/SendTime——接收方/发送方各 4 快照字段（自在线表快照，发送即定格）。`EntityTenant`＋`[LogTable]`＋`[IncreTable]`。

## 6. 事件总线接线（本仓口径）

- `MsgLogSubscriber : IEventSubscriber`（事件名 `"msg_log"`）→ 消费 `SysLogMsg` → 仓储 InsertAsync（对齐 OpLogSubscriber 模式：后台异步落库、失败重试由总线兜底）。
- 参照 `AppEventSubscriber.CreateMsgLog`（Furion 事件订阅者聚合类）→ 本仓独立订阅者文件（#6 单文件单类）。

## 7. Hub 消息面补回（批 F 裁决⑨兑现）

`IOnlineUserHub` 增 2 客户端方法（2→4，恢复参照全消息面）：
- `Task PublicNotice(SysNotice notice)`——通知发布广播（载荷=SysNotice 实体，camelCase）
- `Task ReceiveMessage(object context)`——站内消息单播（载荷=SysLogMsg 全量，参照口径）

- **不加**参照 Hub 的 client→server 方法（SendMessageToUser/SendMessageToAllUser）——本仓消息发送入口仅服务端（Controller/程序），无 UI 点对点会话需求（自行裁决②）。

## 8. 菜单/权限

| Id | Name | 说明 |
|---|---|---|
| 10117 | sysNotice 菜单 | 通知公告 |
| 10118–10127 | 10 按钮 | page/add/update/delete/public/setRead/pageReceived/unReadList/last/detail |
| 10128 | sysMessage 菜单 | 站内消息 |
| 10129–10130 | 2 按钮 | sendUser/sendAll |

- `unReadList`/`last`/`detail`/`pageReceived`/`setRead` 属**用户自读面**（参照动态 API 无独立权限码）——本仓口径：自读面端点**豁免 [MenuPerm]**（同登录即可端点先例：auth/me、sysDictData/list），管理面 6 端点（page/add/update/delete/public＋message 两 send）全 [MenuPerm]。（自行裁决③）

## 9. 本批自行裁决清单（呈报葫芦爷爷）

| # | 裁决 | 依据 |
|---|---|---|
| ① | 站内消息纯推送不落消息实体（日志即持久面；离线不补发） | 参照同口径；4.7 通知公告承担需存储的语义 |
| ② | Hub 不加 client→server 发送方法 | 无 UI 点对点会话需求；发送入口仅服务端（最小必要 #8） |
| ③ | 自读面 5 端点豁免 [MenuPerm]（登录即可） | 参照动态 API 无独立权限码；同登录即可端点先例 |
| ④ | SysNoticeUser 用 EntityBase（＋审计/软删） | 参照 EntityBaseId 仅 Id；本仓中间表统一 EntityBase 口径 |
| ⑤ | SysLogMsg 去 Device 字段 | 本仓无设备指纹（批 F 同口径） |
| ⑥ | 「通知所有人」= 全租户用户（参照查全库） | 租户过滤器自动限域，强于参照 |
| ⑦ | 导航属性不用（PageReceived/GetUnReadList 用 LeftJoin 手动映射） | 批 E 导航泄露教训 |
| ⑧ | 错误信息用 AppException 文本（不用 Furion Oops 错误码） | 本仓 #10 口径 |
