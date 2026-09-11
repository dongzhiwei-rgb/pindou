<!--
文件：API.md
用途：拼了个豆（Pindou Studio）后端 HTTP 接口完整文档。
适用范围：本地开发、Docker、Nginx 反向代理等所有部署形态。
版权：@董志伟-联系方式-makabak1204
最后修改：2026-08-26
-->

# 拼了个豆 · HTTP 接口文档

## 1. 概述

- **Base URL**：开发环境 `http://localhost:5080`；生产环境经 Nginx 反向代理，通常走前端同源 `/api`。
- **数据格式**：除图片上传与导出外，请求/响应均为 `application/json`；图片生成使用 `multipart/form-data`。
- **时间**：响应中的时间戳均为 UTC。
- **版本**：本文件对应后端 ASP.NET Core 8，最后一次核对日期见文件头。

### 1.1 鉴权方式

| 场景 | 方式 |
| --- | --- |
| 匿名访问 | 中间件自动签发 24 小时 HttpOnly 会话 Cookie（`pindou_session`），不阻断访问，仅提供请求身份 |
| 商用授权 / 云端存档 | 请求头 `X-License-Key`（密钥即账号）、`X-Session-Token`（登录后获得的会话令牌） |
| 管理接口 | 优先校验 HttpOnly 管理会话 Cookie（`pindou_admin`，由 `POST /api/admin/login` 签发，8 小时有效）；兼容请求头 `X-Admin-Token`（固定管理密钥，便于脚本调用） |
| SSE 长连接 | URL 只携带一次性短期票据（`ticket`），由 `POST /api/license/tickets` / `POST /api/collab/ticket` 申请，凭据不进 URL |
| 试用 | 无密钥，按客户端 IP 自动识别，无需额外鉴权 |

### 1.2 授权开关（License:EnableAuth）

授权与试用合并为一个开关 `EnableAuth`，可由配置文件或管理接口 `/api/admin/switches` 在运行期控制：

| EnableAuth | 行为 |
| --- | --- |
| false | 免授权：所有用户可用全部功能，`licensingDisabled=true`，前端隐藏授权/试用入口 |
| true | 有密钥按密钥校验并扣减次数；无密钥按 IP 试用（试用窗口/次数由 `License:TrialSeconds`、`License:TrialGenerations` 控制） |

### 1.3 限流说明

- 命名策略（在各端点通过 `RequireRateLimiting` 声明）：

| 策略 | 参数 | 应用接口 |
| --- | --- | --- |
| `api` | 固定窗口 60 次/分钟/IP，队列 4 | quantize、xlsx、handoffs、管理接口、license/tickets、collab 业务接口 |
| `login` | 固定窗口 30 次/5 分钟/IP，无队列 | `POST /api/license/login`、`POST /api/admin/login` |
| `save` | 固定窗口 60 次/分钟/（账号+IP），无队列 | `POST /api/saves` |
| `collab-edit` | 令牌桶 40 容量、每秒补 20/IP，队列 20 | `POST /api/collab/edits`、`POST /api/collab/history` |

- 心跳、SSE、目录、试用查询、会话开始/登出、存档读取、开关查询等高频/长连接请求不受 `api` 策略限制。

### 1.4 通用错误格式

非 2xx 响应体统一为：

```json
{ "message": "人类可读的错误描述", "code": "可选业务错误码" }
```

| 状态码 | 含义 |
| --- | --- |
| 400 | 参数缺失或非法 |
| 401 | 未授权（管理凭据错误、SSE 票据无效或会话失效） |
| 402 | 授权/试用受限，`code` 为 `LICENSE_REQUIRED` |
| 404 | 资源不存在（色卡、密钥、存档、接力链接） |
| 409 | 业务冲突：账号在别处登录（`code=DEVICE_CONFLICT`）、联机权限/审批冲突等 |
| 429 | 触发限流（见 1.3） |
| 503 | 服务繁忙/并发容量满/依赖（数据库）不可用，`Retry-After: 1` |

### 1.5 授权状态枚举（LicenseStatus.Status）

| 取值 | 含义 |
| --- | --- |
| `active` | 密钥有效 |
| `time_expired` | 使用期限已到但生成次数仍有剩余；仍可生成/查看/导出，仅画布编辑锁定 |
| `exhausted` | 生成次数已用完 |
| `revoked` | 密钥已被管理员吊销，不可再使用 |
| `invalid` | 密钥无效 |
| `device_conflict` | 账号已在其他设备登录（接口返回 409） |

---

## 2. 接口总览

| 方法 | 路径 | 用途 | 鉴权/限流 |
| --- | --- | --- | --- |
| GET | `/api/health` | 健康检查 | 无 |
| GET | `/api/catalog/brands` | 厂商与色卡列表 | 无 |
| GET | `/api/catalog/boards` | 底板预设 | 无 |
| GET | `/api/catalog/overview` | 厂商+底板批量目录 | 无 |
| GET | `/api/catalog/brands/{brandId}/palettes/{paletteId}` | 色卡详情 | 无 |
| POST | `/api/patterns/quantize` | 图片生成拼豆图纸 | 密钥/IP试用；`api` |
| POST | `/api/exports/xlsx` | 导出 Excel 图纸 | `api` |
| POST | `/api/handoffs` | 创建跨浏览器接力 | `api` |
| GET | `/api/handoffs/{token}` | 读取接力图纸（一次性） | 无 |
| GET | `/api/license/info` | 查询密钥状态 | X-License-Key |
| GET | `/api/license/trial` | 查询授权/试用状态 | 无 |
| GET | `/api/license/trial/heartbeat` | 试用心跳 | 无 |
| POST | `/api/license/login` | 登录/激活密钥（新登录踢旧会话） | `login` |
| POST | `/api/license/session` | 授权会话开始 | body 密钥+会话令牌 |
| POST | `/api/license/heartbeat` | 授权心跳 | body 密钥+会话令牌 |
| POST | `/api/license/logout` | 登出（清除会话，标记离线） | body 密钥+会话令牌 |
| POST | `/api/license/tickets` | 申请 SSE 票据（kick/states/switches） | kick 需密钥+会话令牌；states 需管理鉴权；`api` |
| GET | `/api/license/events?ticket=` | 会话接管事件 SSE | 一次性票据 |
| GET | `/api/license/switches/events` | 开关状态变更 SSE | 无（公开） |
| GET | `/api/license/states/events?ticket=` | 密钥状态 SSE（管理页用） | 一次性管理票据 |
| GET | `/api/saves` | 读取云端画布存档 | X-License-Key + X-Session-Token |
| POST | `/api/saves` | 写入云端画布存档 | body 密钥；`save` |
| POST | `/api/admin/login` | 管理登录（签发 HttpOnly Cookie） | `login` |
| POST | `/api/admin/logout` | 管理登出 | 管理 Cookie |
| GET | `/api/admin/session` | 管理会话检查 | 无（返回 authenticated） |
| GET | `/api/admin/switches` | 查询授权开关 | 无 |
| PUT | `/api/admin/switches` | 修改授权开关 | 管理鉴权；`api` |
| POST | `/api/admin/licenses` | 批量生成密钥 | 管理鉴权；`api` |
| POST | `/api/admin/licenses/revoke` | 批量吊销密钥（逻辑失效，保留记录） | 管理鉴权；`api` |
| POST | `/api/admin/licenses/delete` | 批量删除密钥（物理移除） | 管理鉴权；`api` |
| GET | `/api/admin/licenses` | 分页查询密钥列表 | 管理鉴权；`api` |
| GET | `/api/admin/licenses/{key}/detail` | 密钥使用详情 | 管理鉴权；`api` |
| DELETE | `/api/admin/licenses/{key}` | 删除单个密钥（物理移除） | 管理鉴权；`api` |
| POST | `/api/collab/host` | 房主创建联机房间 | 房主授权；`api` |
| POST | `/api/collab/refresh` | 刷新邀请码 | 联机令牌；`api` |
| POST | `/api/collab/apply` | 好友凭邀请码申请加入 | 申请人资格；`api` |
| POST | `/api/collab/decide` | 房主审批申请 | 联机令牌；`api` |
| GET | `/api/collab/apply/result/{applyId}` | 轮询审批结果 | `api` |
| POST | `/api/collab/kick` | 房主踢出成员 | 联机令牌；`api` |
| POST | `/api/collab/permission` | 开关成员编辑权限 | 联机令牌；`api` |
| POST | `/api/collab/savepermission` | 开关成员共享（保存导出）权限 | 联机令牌；`api` |
| POST | `/api/collab/leave` | 成员退出 / 房主结束 | 联机令牌；`api` |
| POST | `/api/collab/edits` | 提交编辑（服务端仲裁） | 联机令牌；`collab-edit` |
| POST | `/api/collab/history` | 权威撤销/恢复 | 联机令牌；`collab-edit` |
| POST | `/api/collab/clear` | 房主清空共享画布 | 联机令牌（房主）；`api` |
| POST | `/api/collab/permission-apply` | 成员申请权限（编辑/共享） | 联机令牌；`api` |
| POST | `/api/collab/permission-decide` | 房主审批权限申请 | 联机令牌；`api` |
| POST | `/api/collab/resync` | 房主同步整张画布 | 联机令牌（房主）；`api` |
| POST | `/api/collab/replace-request` | 成员申请替换图纸 | 联机令牌；`api` |
| POST | `/api/collab/replace-decide` | 房主审批替换申请 | 联机令牌；`api` |
| POST | `/api/collab/ticket` | 申请联机 SSE 票据 | 联机令牌；`api` |
| GET | `/api/collab/events?ticket=` | 联机事件 SSE | 一次性联机票据 |

---

## 3. 健康检查与目录

### 3.1 GET /api/health

健康检查，用于探活/负载均衡。

**响应 200**

```json
{ "status": "ok", "service": "拼了个豆 API", "utc": "2026-08-23T04:00:00Z" }
```

### 3.2 GET /api/catalog/brands

返回全部厂商及其色卡摘要。

**响应 200**：`BrandSummary[]`

```json
[
  {
    "id": "mard",
    "name": "MARD 漫漫豆",
    "country": "中国",
    "kind": "品牌拼豆",
    "heatRank": 1,
    "palettes": [
      { "id": "mard-221", "name": "221 色基础款", "colorCount": 221, "beadSizes": [2.6, 5.0], "verified": true, "version": "2026-08", "note": "" }
    ]
  }
]
```

### 3.3 GET /api/catalog/brands/{brandId}/palettes/{paletteId}

返回单个色卡的完整颜色表（含 RGB/Lab）。

**路径参数**：`brandId`、`paletteId`（如 `mard` / `mard-221`）

**响应 200**：`PaletteDetail`

```json
{
  "brandId": "mard", "brandName": "MARD 漫漫豆", "paletteId": "mard-221",
  "paletteName": "221 色基础款", "colorCount": 221, "verified": true,
  "version": "2026-08", "note": "",
  "colors": [
    { "id": "mard-221-001", "brand": "MARD", "code": "001", "name": "白色", "hex": "#FFFFFF", "rgb": [255,255,255], "lab": [100,0,0], "source": "对照表", "license": "内部数据" }
  ]
}
```

**错误**：`404` 色卡不存在。

### 3.4 GET /api/catalog/boards

返回底板预设列表。

**响应 200**：`BoardPreset[]`

```json
[
  { "id": "mini-52", "name": "迷你豆整板 52×52", "columns": 52, "rows": 52, "beadSize": 2.6, "category": "迷你豆", "note": "适合2.6mm豆，一块可覆盖52×52图纸。" }
]
```

---

## 4. 图纸生成与导出

### 4.1 POST /api/patterns/quantize

上传图片，生成指定色卡、指定规格的拼豆图纸。**限流接口。**

**请求**：`multipart/form-data`

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| image | file | 是 | 图片文件，≤ 15MB |
| paletteId | string | 是 | 目标色卡 ID，如 `mard-221` |
| width | int | 是 | 横向颗数，8–160 |
| height | int | 是 | 纵向颗数，8–160 |
| maxColors | int | 是 | 最大颜色数，2 到 `min(96, 色卡颜色数, 宽×高)` |
| dither | bool | 是 | 是否误差扩散抖动 |
| removeBackground | bool | 是 | 是否自动去除背景 |
| backgroundThreshold | number | 是 | 背景容差，2–30 |
| noiseSuppression | int | 是 | 杂色抑制强度，0–3 |
| algorithm | string | 否 | `pixel-aggregate`（默认，像素聚合）/ `ciede2000`（兼容旧算法） |

**请求头**（可选）：`X-License-Key`、`X-Session-Token`

**授权逻辑**：见 1.2。开关开启时，携带有效密钥扣减一次生成次数；无密钥则按 IP 校验试用窗口与生成次数。

**curl 示例**

```bash
curl -X POST http://localhost:5080/api/patterns/quantize \
  -H "X-License-Key: PDABCDEFGHJKMNPQR" \
  -F "image=@cat.jpg" \
  -F "paletteId=mard-221" \
  -F "width=48" -F "height=48" \
  -F "maxColors=24" -F "dither=false" \
  -F "removeBackground=true" -F "backgroundThreshold=8" \
  -F "noiseSuppression=1" -F "algorithm=pixel-aggregate"
```

**响应 200**：`QuantizeResponse`

```json
{
  "width": 48, "height": 48,
  "brandId": "mard", "paletteId": "mard-221",
  "colors": [ { "id": "...", "brand": "MARD", "code": "001", "name": "白色", "hex": "#FFFFFF", "rgb": [255,255,255], "lab": [100,0,0], "source": "...", "license": "..." } ],
  "cells": [0, 1, 2, 0],
  "usage": [ { "colorIndex": 0, "code": "001", "name": "白色", "hex": "#FFFFFF", "count": 512 } ],
  "usedColorCount": 12,
  "beadCount": 2304,
  "processingMs": 86,
  "algorithm": "pixel-aggregate"
}
```

说明：`cells` 为 `width×height` 的一维数组，元素为 `colors` 数组下标。

**错误**：`400` 参数不合法；`402` + `code=LICENSE_REQUIRED`（密钥不可用/试用受限）；`409` + `code=DEVICE_CONFLICT`（账号在别处登录）；`429` 限流。

### 4.2 POST /api/exports/xlsx

根据图纸数据生成 Excel 图纸文件（.xlsx）。**限流接口。**

**请求**：`application/json`，`PatternExportRequest`

```json
{
  "title": "我的图纸",
  "width": 48, "height": 48, "beadSize": 5.0,
  "boardColumns": 29, "boardRows": 29,
  "brandName": "MARD", "paletteName": "221 色基础款",
  "colors": [ { "code": "001", "name": "白色", "hex": "#FFFFFF" } ],
  "cells": [0, 1, 2, 0]
}
```

**校验**：`width`/`height` ∈ 1–160；`cells.length == width × height`；`colors` 非空且 `cells` 中所有下标小于 `colors.length`。

**响应 200**：`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`，文件名 `{title}.xlsx`。

**错误**：`400` 数据尺寸/色卡不完整；`429` 限流。

---

## 5. 跨浏览器接力

用于微信内置浏览器 → 系统浏览器的图纸传递。数据在服务端内存保留 **10 分钟**，**读取一次即失效**，快照 ≤ 2MB。

### 5.1 POST /api/handoffs

**请求**：`application/json`

```json
{ "project": { "width": 48, "height": 48 } }
```

**响应 200**

```json
{ "token": "abc123...", "expiresAt": "2026-08-23T04:10:00Z" }
```

**错误**：`400` 数据格式不正确或超过 2MB。

### 5.2 GET /api/handoffs/{token}

**响应 200**：`application/json`，直接返回 `project` 原始 JSON（`Cache-Control: no-store`）。

**错误**：`404` 链接已失效或已使用。

---

## 6. 授权与试用

### 6.1 GET /api/license/info

只读查询密钥状态，不扣减次数。

**请求头**：`X-License-Key`

**响应 200**：`LicenseStatus`

```json
{ "status": "active", "remainingSeconds": 3599, "remainingCount": 4, "message": null, "kickInSeconds": null }
```

### 6.2 GET /api/license/trial

页面加载时调用。有密钥返回密钥校验结果；无密钥返回当前 IP 的试用状态；授权开关关闭时返回 `licensingDisabled=true`。

**请求头**（可选）：`X-License-Key`

**响应 200**：`TrialInfoResponse`，三种形态：

```json
// ① 携带密钥且授权开启
{ "licensed": true, "license": { "status": "active", "remainingSeconds": 3599, "remainingCount": 4, "message": null, "kickInSeconds": null }, "trial": null }

// ② 授权开关关闭（免授权）
{ "licensed": false, "licensingDisabled": true, "trial": null }

// ③ 授权开启且无密钥（按 IP 试用）
{ "licensed": false, "licensingDisabled": false, "trial": { "remainingSeconds": 7199, "remainingGenerations": 5, "totalMinutes": 120, "expired": false } }
```

### 6.3 GET /api/license/trial/heartbeat

试用心跳。仅当前台在线时由主标签页定期调用，刷新 IP 的在线状态；试用剩余时间按绝对时间模型由服务器计算（自首次访问 `first_seen_at` 起按服务器时钟消耗），不依赖心跳累计。

**响应 200**：`TrialStatus`（同 6.2 形态 ③ 的 `trial` 字段结构）。

### 6.4 POST /api/license/login

登录并激活密钥（密钥即账号）。新登录会使该密钥在旧设备立即下线，并返回会话令牌。

**请求**：`application/json`

```json
{ "key": "PDABCDEFGHJKMNPQR", "sessionToken": "（可选，兼容字段，服务端当前不使用）" }
```

**响应 200**：`LoginResult`

```json
{ "status": "active", "remainingSeconds": 3599, "remainingCount": 4, "message": null, "isNewUser": true, "hasSave": false, "sessionToken": "16位会话令牌" }
```

**错误**：`400` 密钥为空；状态非 `active`/`time_expired` 时返回对应状态（如 `invalid`、`exhausted`）。

### 6.5 POST /api/license/session

授权会话开始：校验单点会话并刷新心跳/活跃时间。剩余时长按绝对时间模型由服务器计算（计时起点 `started_at` 在首次登录 `/api/license/login` 时设置），会话开始不再重置计时。

**请求**：`application/json`

```json
{ "key": "PDABCDEFGHJKMNPQR", "sessionToken": "16位会话令牌" }
```

**响应 200**：`LicenseStatus` + `kickInSeconds`（即时下线模式下恒为 `null`）。

**错误**：`409` 会话冲突（`device_conflict`，账号在别处登录）。

### 6.6 POST /api/license/heartbeat

授权心跳：按会话令牌校验在线并刷新心跳/活跃时间（用于在线状态判定）；剩余时长按绝对时间模型由服务器计算（自 `started_at` 起按服务器时钟消耗），心跳不累计时长。首次心跳会补设计时起点（`COALESCE(started_at, UTC_TIMESTAMP())`）。

**请求 / 响应**：同 6.5。

---

## 7. SSE 长连接

三个 SSE 接口，均返回 `text/event-stream`。客户端断开后服务端自动清理订阅；各端点有并发连接上限（每端点 100）。

### 7.1 GET /api/license/events?ticket={ticket}

监听会话接管事件。同一密钥新设备登录时，服务端向旧会话推送 `session-kicked` 事件，旧设备应立即停止业务请求并提示下线。

**连接方式**：先 `POST /api/license/tickets`（`purpose=kick`，携带 `X-License-Key` 与 `X-Session-Token`）换取一次性票据，再以 `?ticket=` 建立 SSE；票据 60 秒有效、单次使用。

**事件示例**

```
event: session-kicked
data: {"message":"该账号已在其他设备登录，本设备已下线"}

```

**错误**：`401` 会话无效。

### 7.2 GET /api/license/switches/events

监听授权开关变更。建立连接后立即推送一次当前状态；管理员通过 `PUT /api/admin/switches` 修改开关时广播 `switch-changed` 事件，前端无需轮询即可更新。

**事件示例**

```
event: switch-changed
data: {"enableAuth":false}

```

---

### 7.3 GET /api/license/states/events?ticket={ticket}

后台管理页专用：密钥上线/下线/停用/删除/心跳变化时推送 `licenses-changed`。先 `POST /api/license/tickets`（`purpose=states`，需管理鉴权）换取一次性票据；建立后先推一次初始信号，此后每 5 秒无条件推送一次（管理页据此批量刷新当前页）。

**事件示例**

```
event: licenses-changed
data: {"changed":true}

```

---

## 8. 云端存档

按密钥（账号）读写画布存档，需要有效会话令牌。

### 8.1 GET /api/saves

**请求头**：`X-License-Key`、`X-Session-Token`

**响应 200**

```json
{ "payload": "云端图纸库 JSON：{ \"kind\":\"pindou-library\", \"items\":[{ \"id\":\"current\", \"name\":\"正在编辑\", \"savedAt\":\"...\", \"project\":{ ...PortableProject } }] }" }
```

> 兼容说明：旧版单张 PortableProject 存档（无 `kind` 字段）仍可被前端识别并包装为图纸库。

**错误**：`400` 缺少密钥；`404` 暂无存档；`409` + `code=DEVICE_CONFLICT` 会话冲突。

### 8.2 POST /api/saves

**请求**：`application/json`

```json
{ "key": "PDABCDEFGHJKMNPQR", "payload": "云端图纸库 JSON（{ \"kind\":\"pindou-library\", \"items\":[...] }），由前端统一序列化" }
```

**请求头**：`X-Session-Token`

**响应 200**：`{ "message": "已保存" }`

**错误**：`400` 数据不完整；`409` + `code=DEVICE_CONFLICT` 会话冲突。

---

## 9. 管理接口

以下接口需管理鉴权：优先校验 HttpOnly 管理会话 Cookie（`pindou_admin`，登录 `POST /api/admin/login` 后自动携带，8 小时有效）；脚本/工具可改用请求头 `X-Admin-Token`（固定管理密钥，`appsettings.json` 的 `License:AdminToken`，生产必须修改默认值，且 `Security__RejectDefaultSecrets=true` 时会拒绝默认密钥启动）。管理页 `admin.html` 使用 Cookie 会话，不再把管理令牌存入浏览器存储。

### 9.1 POST /api/admin/licenses

批量生成密钥。一次请求生成 `number` 把密钥（后端单条多值 INSERT 写入），返回全部新密钥。

**请求**：`application/json`

```json
{ "totalHours": 24, "count": 5, "number": 10 }
```

`totalHours` 为期限小时数（优先，自动转秒）；`totalSeconds` 为兼容的精确秒数；两者至少提供一个。`count` 为**每个密钥**的可生成次数。`number` 为本次生成的密钥数量（可选，默认 1，上限 100）。

**响应 200**

```json
{ "keys": ["PDABCDEFGHJKMNPQR...", "PDXXXXYYYYZZZZ..."] }
```

**错误**：`400` 参数不合法（次数非正整数、期限无效、数量超范围）；`401` 令牌错误。

### 9.2 POST /api/admin/licenses/revoke

批量**吊销**密钥：逻辑失效（`is_revoked=1`），记录保留并标记「已吊销」；之后任何校验都返回 `revoked` 状态。

**请求**：`application/json`

```json
{ "keys": ["PDABCDEFGHJKMNPQR...", "PDXXXXYYYYZZZZ..."] }
```

**校验**：`keys` 非空，单次最多 100 个；自动去重并忽略空值。

**响应 200**

```json
{ "revoked": 10 }
```

**错误**：`400` 未提供密钥列表或超过 100 个；`401` 令牌错误。

### 9.3 POST /api/admin/licenses/delete

批量**删除**密钥：物理移除记录，从列表中彻底消失（不可恢复）。

**请求**：`application/json`

```json
{ "keys": ["PDABCDEFGHJKMNPQR...", "PDXXXXYYYYZZZZ..."] }
```

**校验**：`keys` 非空，单次最多 100 个；自动去重并忽略空值。

**响应 200**

```json
{ "deleted": 10 }
```

**错误**：`400` 未提供密钥列表或超过 100 个；`401` 令牌错误。

### 9.4 GET /api/admin/licenses

分页查询密钥列表，按创建时间倒序。

**查询参数**

| 参数 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| page | int | 1 | 页码，从 1 起 |
| pageSize | int | 20 | 每页条数，1–100 |

**响应 200**

```json
{
  "items": [
    { "key": "PDABCDEFGHJKMNPQR", "userId": null, "totalSeconds": 86400, "usedSeconds": 0, "remainingCount": 5, "isRevoked": false, "createdAt": "2026-08-23T04:00:00Z" }
  ],
  "total": 128,
  "page": 1,
  "pageSize": 20
}
```

`items` 为当前页 `LicenseEntry[]`；`total` 为全部密钥总数（用于计算总页数）。

### 9.5 DELETE /api/admin/licenses/{key}

删除单个密钥（物理移除）。

**响应 200**：`{ "message": "密钥已删除。" }`

**错误**：`404` 未找到该密钥；`401` 令牌错误。

### 9.6 GET /api/admin/switches

查询当前授权开关（无需鉴权）。

**响应 200**

```json
{ "enableAuth": false }
```

### 9.7 PUT /api/admin/switches

修改授权开关，立即生效并广播给所有在线前端。

**请求**：`application/json`

```json
{ "enableAuth": true }
```

**响应 200**：当前快照（同 9.6）。

**错误**：`400` 请求体格式不正确；`401` 令牌错误。

**curl 示例**

```bash
# 开启授权（含试用）
curl -X PUT http://localhost:5080/api/admin/switches \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: change-this-admin-token" \
  -d '{"enableAuth":true}'

# 关闭授权（免授权，所有人可用全部功能）
curl -X PUT http://localhost:5080/api/admin/switches \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: change-this-admin-token" \
  -d '{"enableAuth":false}'
```

---

## 10. 好友联机（Collab）接口

联机为内存态房间（上限 300 间 / 每间 5 人）。成员身份凭证 `token`（192 位随机）只通过请求体传递；SSE 一律用一次性票据连接。编辑采用「客户端乐观落笔 + 服务端仲裁回滚」，同一格子同一时刻仅一人可写（格子锁 8 秒 TTL，并按格子版本阻止撤销覆盖他人后续编辑）。

### 10.1 房间与成员

| 接口 | 说明 |
| --- | --- |
| `POST /api/collab/host` | 房主携带当前豆板快照创建房间，返回 roomId/inviteCode/hostToken/memberId/hostName/colorIndex/room；授权开启时房主须持有效密钥 |
| `POST /api/collab/refresh` | 房主刷新 6 位邀请码（旧码与旧链接作废，已加入成员不受影响） |
| `POST /api/collab/apply` | 好友凭邀请码申请加入（复制链接/手动输入同入口），返回 applyId；试用到期或无有效密钥不可申请 |
| `POST /api/collab/decide` | 房主审批申请（原子抢占防并发超员；同意后下发成员身份与豆板快照） |
| `GET /api/collab/apply/result/{applyId}` | 好友轮询审批结果：pending=true 仍在等待；approved/rejected/expired 为终态 |
| `POST /api/collab/kick` | 房主踢出成员 |
| `POST /api/collab/leave` | 成员退出；房主退出即关闭房间并踢出全部好友 |
| `POST /api/collab/permission` / `savepermission` | 房主单独开关成员「编辑 / 共享（保存导出）」权限（编辑默认关闭、共享默认开启） |

### 10.2 编辑同步与历史

| 接口 | 说明 |
| --- | --- |
| `POST /api/collab/edits` | 提交一批编辑（单批 ≤600 格），服务端仲裁冲突并广播 edits/reverts/locks，返回全局编辑序号 seq |
| `POST /api/collab/history` | 服务端权威撤销/恢复：只处理本人历史，以格子版本阻止覆盖他人后续编辑 |
| `POST /api/collab/clear` | 房主清空共享画布并广播 clear 事件 |
| `POST /api/collab/resync` | 房主生成/替换整块画布后同步给成员（广播 resync 事件） |

### 10.3 权限与替换图纸申请

| 接口 | 说明 |
| --- | --- |
| `POST /api/collab/permission-apply` | 成员申请编辑/共享权限（30 秒冷却 + 30 秒有效期） |
| `POST /api/collab/permission-decide` | 房主审批权限申请（同意后广播权限变更并通知申请成员） |
| `POST /api/collab/replace-request` | 成员申请替换整张联机图纸（1 分钟冷却 + 1 分钟有效期，携带待替换快照） |
| `POST /api/collab/replace-decide` | 房主审批替换申请（同意后整体替换房间快照并广播 resync） |

### 10.4 联机 SSE

| 接口 | 说明 |
| --- | --- |
| `POST /api/collab/ticket` | 用成员令牌换一次性票据（body `{ "token": ... }`） |
| `GET /api/collab/events?ticket=` | 联机事件流：state（初始快照）/room/edits/resync/clear/permission/savepermission/kicked/closed/perm_apply/perm_decided/replace_apply 等；同一成员最多 3 条连接，断线指数退避重连 |

---

## 11. 附：核心数据契约

| 类型 | 字段 | 说明 |
| --- | --- | --- |
| BeadColor | id, brand, code, name, hex, rgb[], lab[], source, license | 单个豆色 |
| PaletteDetail | brandId, brandName, paletteId, paletteName, colorCount, verified, version, note, colors[] | 完整色卡 |
| QuantizeResponse | width, height, brandId, paletteId, colors[], cells[], usage[], usedColorCount, beadCount, processingMs, algorithm | 图纸生成结果 |
| PatternExportRequest | title, width, height, beadSize, boardColumns, boardRows, brandName, paletteName, colors[], cells[] | Excel 导出 |
| TrialStatus | remainingSeconds, remainingGenerations, totalMinutes, expired | IP 试用状态 |
| LicenseStatus | status, remainingSeconds, remainingCount, message, kickInSeconds | 密钥状态 |
| LoginResult | status, remainingSeconds, remainingCount, message, isNewUser, hasSave, sessionToken | 登录结果 |
