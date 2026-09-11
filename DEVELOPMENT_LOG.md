# 拼了个豆 · 开发日志

## 2026-08-28：概念稿全量界面组件化

### 实现范围

- 新增最终加载的 `frontend/src/studio-components.css`，不修改生成、编辑、撤销恢复、联机、授权、保存、导入和导出业务链路，仅统一界面组件结构和视觉表现。
- 覆盖系统全部 `<dialog>`、设置抽屉、功能菜单、色号 Popover、联机申请/成员浮层、熨烫预览保存引导、Toast 和错误提示。
- 使用 CSS 令牌固定品牌色、表面、边框、阴影、圆角、键盘焦点和动效；标题区采用与概念稿一致的彩色豆点识别，确认操作使用珊瑚红、次要操作使用纸白与森林绿、警告和错误使用独立语义色。
- 桌面端保持居中模态与右侧属性面板；移动端统一底部抽屉、粘性操作区与安全区内边距，390px 以下自动缩减字号和操作内边距，色号列表变为单列。
- Toast 根据消息内容映射为信息/成功/警告/错误/联机语义，并通过原生 Popover 进入页面顶层；错误提示增加明确关闭入口。
- 联机通知图标复用 `AppIcon` 中央图标表；熨烫预览专属组件同步更新表面与操作样式。

### 可靠性与验证

- 新样式始终最后加载，用单独文件收敛历史 CSS，便于灰度比较和独立回退。
- 色号面板继续使用 VisualViewport 定位与顶层 Popover，不受工具栏、模糊背景和滚动容器裁切。
- 已执行 `npm run build`，Vue TypeScript 检查和 Vite 生产构建均通过。
- 本地开发首页请求返回 HTTP 200；本次环境未提供浏览器客户端自动化工具，因此未虚构新的像素级截图回归结论，后续可直接在已启动的 `http://localhost:8088/` 人工审查。
- 回退快照：`F:\001\rollback\ui-system-20260828-before.zip`。

- 版权：@董志伟-联系方式-makabak1204
- 最后更新：2026-08-28
- 项目：拼豆图纸生成网站（商用授权系统）

---

## 2026-08-28 概念稿图标与弹层精修

- 建立增量快照 `rollback/ui-redesign-20260828-before-icons.zip`，用于独立回退本阶段的图标、菜单和弹层改造。
- 新建 `frontend/src/icons/studioIcons.ts`，集中维护完整语义图标表；`AppIcon.vue` 只负责安全渲染，所有图标共用一致的几何网格、线宽和光学中心。
- 颜色选择面板升级为原生顶层 Popover，结合 VisualViewport、滚动与尺寸变化重新定位，从结构上绕过工具轨 `overflow` 和 `backdrop-filter` 形成的裁剪与层叠上下文。
- 桌面顶栏图纸上下文改为视口中心定位；右上命令菜单与右下属性卡共用同一列，菜单宽度、按钮密度、图标尺寸和折叠分区按概念稿继续校准。
- 移动端功能面板避让底部五项快捷编辑栏，标题增加轻量下拉提示；常用色卡入口改为圆形加号，图纸尺寸补齐形状、宽高和总孔数信息。
- 验证：`npm run build` 通过，包含 `vue-tsc -b` 和 Vite 生产构建。

---

## 2026-08-27 概念稿级前端 UI 重构

- 建立视觉重构前完整源码快照 `rollback/ui-redesign-20260827-before.zip`，重构采用独立覆盖样式层，业务逻辑与数据结构不迁移。
- 桌面端完成品牌顶栏、作品上下文、左侧分组工具轨、右上功能菜单、画布底部控制条和右侧生成参数抽屉重构。
- 移动端完成品牌精简、核心生成入口、横向拇指工具栏、底部功能面板、生成参数底部抽屉及安全区适配。
- 统一第二代单色主题语义图标；实体豆板改为圆形柱钉，真实圆豆按空心圆柱结构绘制同色侧壁、哑光顶面、中心孔、孔内豆针和接触阴影，同时保留可见分块与 DPR 清晰渲染。
- 快捷颜色从当前厂家色卡抽取八个代表色，完整选色、搜索与新增色号仍复用既有 ColorPickerPopover 数据流。
- 二次校准设计令牌，基础样式、独立熨烫预览和开屏动画均统一使用概念稿配色；圆豆外径调整为豆针中心距，保证相邻豆子完全相切且无镜面反光。
- 快捷编辑重新按桌面三分组、移动五入口布局；更多菜单承担低频文件/显示操作，核心生成入口保持独立。
- 第二阶段按概念稿重新确认空间比例：桌面属性面板改为右下锚定的常驻非模态卡片，菜单浮层占用右上扩展区；顶部授权入口迁入“更多”，使头部只保留设计稿中的图纸上下文、保存、菜单和生成动作。
- 图纸设置新增颜色/图纸/显示页签并绑定现有状态；移动端底部工具栏改为五等分拇指区，设置面板避让工具栏和安全区，窄屏不再依赖固定按钮宽度。
- 常用图标移除会造成视觉偏心的装饰点，豆笔、手掌、镊子、菜单及文件类图标重新校准 24×24 安全区；豆子接触阴影收进格子，哑光顶沿改为完整低对比圆环，相切关系保持不变。
- 建立阶段二增量快照 `rollback/ui-redesign-20260827-phase2-before.zip`；开发服务运行于 `http://localhost:8088/`，最新 TypeScript 检查和 Vite 生产构建通过。
- 验证：前端 TypeScript 检查与 Vite 生产构建通过；桌面/移动端实际浏览器回归无控制台错误，生成参数抽屉与通用弹窗未发现遮挡。

---

## 2026-08-27 多人联机并发一致性加固

- 编辑、撤销和恢复的 HTTP 结果与 SSE 广播统一按 `EditSeq` 应用，旧响应不再覆盖新状态；序号缺口立即停止本地队列并通过完整 state 恢复。
- 房间新增 `BoardRevision`，清空/替换/重同步时递增；新前端提交编辑时携带版本，服务端拒绝旧版本请求。
- 整板变化会清理客户端待提交编辑和锁显示；服务端锁清扫与续锁改为共用 `SyncRoot`。
- 成员转只读或离开房间后立即释放其格子锁；空画布清空同样建立版本屏障。
- 验证：ASP.NET Core 测试 13/13 通过，Vue 类型检查与 Vite 生产构建通过。

---

## 项目概况

- 架构：前后端分离
  - 前端：Vue 3 + Vite（端口 8088）
  - 后端：ASP.NET Core 8（端口 5080）
  - 数据库：MySQL（本地免安装版 F:\mysql\mysql-8.0.46-winx64，端口 3306）
- 部署：Docker Compose（含 MySQL 服务），支持 Linux 发布与局域网访问
- 服务地址（本机联调）：
  - 前端局域网：http://192.168.3.7:8088
  - 后端：http://localhost:5080

---

## 已完成功能

### 1. 密钥管理（LicenseStore.cs）
- 密钥包含「使用期限」（见下方绝对时间模型）+「生成图纸次数」，管理员可批量生成 / 查询 / 吊销 / 删除
- 计费为绝对时间模型：剩余时长 = 总期限 − 自首次登录（或首次心跳）设置的 started_at 起按服务器时钟流逝的秒数；离线、后台、关闭浏览器同样消耗，心跳不再累加时长、仅刷新在线状态
- 管理接口按「小时/天」创建密钥（totalHours 优先自动转秒，totalSeconds 兼容精确秒），支持开发测试用短时长

### 2. 试用机制（IpTrialTracker.cs）
- 无密钥访客按 IP 限制：试用窗口与免费生成次数由配置 License:TrialSeconds / License:TrialGenerations 控制（当前基础配置 7200 秒 / 5 次；开发环境 60 秒便于测试）
- 试用同为绝对时间模型：剩余时间自首次访问（first_seen_at）起按服务器时钟消耗；试用心跳仅刷新在线状态
- 试用到期后任何操作（含点击屏幕）均弹出「获取密钥」提示窗

### 3. 账号体系（密钥即账号）
- 不再使用 UUID / 设备 ID：密钥即账号，登录只需输入密钥
- 同一密钥可在不同设备间切换使用，单点登录（同一时间仅一台设备在线，新登录即时踢旧会话）
- 云端存档按密钥保存，任何设备用同一密钥登录即可恢复
- 业务场景区分：
  - 试用未登录 → 画布数据保存到本地浏览器持久存储，不触发云端同步
  - 登录后 → 画布数据自动同步云端（1.5 秒防抖保存）
  - 老用户重登、云端有存档 → 弹窗确认「是否同步云端数据（当前数据将清空）」

### 4. 单设备在线（新登录挤旧 + SSE 即时通知）
- 同一密钥同一时间仅一台设备在线；策略为「新登录挤旧」：
  - 密钥允许在不同设备（不同 UUID）间切换使用，不再永久绑定单一设备
  - 新设备用同一密钥登录时覆盖账号当前会话令牌，旧令牌立即失效
  - 通过 SSE 长连接（/api/license/events）向旧会话推送 `session-kicked`，前端立即清除授权并提示下线
  - 登录接口返回实际生效账号（UserId），前端同步 deviceId，保证会话校验一致（不误踢自己）

### 5. 旧会话立即下线
- 新设备登录成功后，旧设备的生成图纸 / 云端存档操作立即被拒绝（409 DEVICE_CONFLICT）
- 核心业务接口（quantize / saves GET+POST）执行唯一会话令牌校验
- SSE 负责即时页面提示；5 秒心跳及业务接口校验作为断线兜底

### 6. 期限到期但次数可用（time_expired）
- 需求：密钥期限（时长）用完但生成次数还有剩余时，仍可生成图纸、查看熨烫效果与导出
- 后端 Evaluate 新增 time_expired 状态（剩余时长 0 但次数 > 0）
- ConsumeAsync 去掉「used_seconds < total_seconds」限制，仅按剩余次数扣减
- quantize 接口放行 time_expired（其余状态仍返回 402/409）；登录时也放行 time_expired
- 前端：登录放行 time_expired；画布编辑锁定（豆针/取出/复制/取色/颜色替换/撤销/重做禁用），仅保留「清空」；授权面板显示对应提示

### 7. 剩余时长实时倒计时
- 密钥剩余时长按 天/时/分/秒 实时倒计时（本地每秒递减，心跳响应校准基准值）
- 弹窗内始终显示倒计时；页面外部（顶部授权按钮）仅当剩余 ≤ 10 分钟时开始显示
- 生成图纸（上传图片）成功后立即刷新剩余次数，无需等下一次心跳

### 8. 拼接豆板（只增不减）
- 功能弹窗「功能」区新增「拼接豆板」：可扩大画布规格，只增不减，上限 160×160
- 扩大后原豆子图案保留在左上角，新区域为空白，不影响已有图案
- 拼接计入历史，支持撤销/恢复

### 9. 图纸库（保存 / 我的图纸）
- 工具栏新增「保存」图标按钮（位于快捷编辑工具栏撤销按钮上方）：弹窗输入图纸名称，默认「图纸N」（按已保存命名图纸数量累加）
- 保存豆板与豆子数据：有密钥保存到云端图纸库，无密钥保存到本地（localStorage 多份）
- 功能弹窗「功能」区新增「我的图纸」：列表展示已保存图纸（名称 / 规格 / 豆数 / 保存时间）
- 点击图纸同步到当前画布回显；当前画布有豆子时提示「是否打开新图纸，当前豆板数据将被替换」，确认后再回显
- 打开新图纸前把当前画布压入历史，支持撤销/恢复
- 云端存档升级为图纸库格式（{ kind:'pindou-library', items:[...] }），自动同步当前画布为「正在编辑」条目；读取兼容旧版单张存档
- 「我的图纸」支持单删与批量删除（勾选多选），删除需确认弹窗，删除后不可恢复；云端删除保留「正在编辑」条目
- 画布无豆子数据时保存按钮禁用，不可保存
- 清空画布确认文案统一为「确认清空豆板」（按钮提示 / 成功提示同步）

### 10. 交互与文案规范
- 用户可见「画布」描述统一改为「豆板」（提示语 / 弹窗 / 按钮 / 状态栏）
- 点击元素不出现蓝色选中高亮：全局 user-select:none + -webkit-tap-highlight-color 透明（前端 styles.css 与后台 admin.html 均生效）；输入框 / 需复制的区域（如熨烫保存卡片）单独放行 user-select:text

---

## 联调验证记录（2026-08-22）

- 新登录挤旧即时下线：A 在线 → B 用同一密钥登录 → A 的旧令牌立即返回 device_conflict，SSE 推送 `session-kicked` 并提示下线 ✅
- 登录返回实际账号：换浏览器/清缓存用全新 UUID 登录，返回原账号且会话校验通过（不再一登录就下线）✅
- time_expired：短时长密钥到期后画布编辑锁定、仍可生成/熨烫/导出；页面提示已验证 ✅
- 剩余时长倒计时：前端本地每秒递减 + 心跳校准；授权面板与顶部入口显示已验证 ✅

---

## 待办

- [x] 前端界面联调：即时下线提示、剩余时长倒计时、time_expired 编辑锁定
- [x] 前端双会话「新登录挤旧 + 立即下线」联调（API 与真实双浏览器流程）
- [x] 打包 Linux + Docker Compose 发布包（release/pindou-studio-linux-20260826.tar.gz）
- [ ] 试用参数按公网策略收口：当前默认 7200 秒 / 5 次（绝对时间模型），需调整时改 License:TrialSeconds / TrialGenerations 并重新打包
- [ ] 实际上线部署：替换域名证书与 .env 强口令后执行 deploy-linux.sh

---

## 安全检查加固（2026-08-24，依据《安全检查报告-20260824.txt》）

- 严重-01：新增 Security:RejectDefaultSecrets 配置，为 true 且检测到默认 AdminToken/ApiSign 时拒绝启动
- 严重-02：跨浏览器接力存储加容量上限（条目 2000 / 单 IP 100 / 总字节 256MB），创建接口限流，超限返回 503
- 高-01：nonce 改为签名验证成功后才登记，并设 20000 条容量上限，防字典无限增长
- 高-03：图片增加总像素限制（宽×高 ≤ 1600 万）；quantize 增加全局并发信号量（4），超限返回 503
- 高-04：登出接口必须匹配当前会话令牌，不匹配返回 409 DEVICE_CONFLICT
- 中-01：三个 SSE 长连接端点加并发连接上限（每端点 100），超限返回 503
- 中-02：云存档 payload 加 2MB 业务级大小限制
- 中-03：管理查询 / 详情 / 单删接口补上按 IP 限流
- 中-05：管理密钥轮换日志只记录令牌指纹，不再输出令牌正文
- 中-07：后端响应头加 Permissions-Policy；release nginx 加 CSP 与 Permissions-Policy
- 验证：编译 0 警告 0 错误；健康 200；正常图片量化 200；管理正常 token 200 / 错误 401；登出错误会话 409；nonce 重放 401；超大图片（4001×4000）400；超大云存档 400

### 11. 管理令牌 HttpOnly Cookie 会话（中-04 落地）
- 新增 Services/AdminSessionStore.cs：服务端内存会话（8 小时、容量 5000），登录签发、鉴权校验、退出撤销
- 新增管理接口：POST /api/admin/login（验证明文管理密钥后签发 HttpOnly Cookie）、POST /api/admin/logout、GET /api/admin/session（会话检查）
- Cookie：HttpOnly + SameSite=Lax + Path=/ + Max-Age 8h；Secure 仅在 HTTPS 请求时启用（本地 HTTP 不设，避免失效）
- 管理鉴权：优先校验 HttpOnly Cookie 会话，兼容 X-Admin-Token（固定管理密钥，便于脚本/工具）
- SSE /api/license/states/events：优先 Cookie 会话，兼容 query 令牌
- admin.html：不再把管理令牌存入 localStorage；改为「登录」（输入密钥 → 签发 Cookie）→ 退出；页面刷新通过 /api/admin/session 判断会话保持登录；退出关闭密钥状态 SSE 并清空列表
- 验证：登录 200 + HttpOnly Set-Cookie；Cookie 访问管理接口 200；无凭据 401；错误密钥 401；X-Admin-Token 兼容 200；登出后刷新回到未登录态；刷新保持登录

### 12. SSE 连接票据（高-02 落地，凭据不再进 URL）
- 新增 Services/SseTicketStore.cs：一次性、60 秒有效、单次使用、容量 5000，可携带关联数据（如会话令牌）
- 新增接口 POST /api/license/tickets（body: purpose）：kick（需用户会话）/ states（需管理鉴权）/ switches（公开），签发后返回 ticket
- SSE 端点改造：/api/license/events 凭 ticket 恢复会话令牌（不再传 key/sessionToken）；/api/license/states/events 凭 ticket 校验（不再传 token）；/api/license/switches/events 公开保持不变
- 前端：api.ts createKickEventSource 先申请 kick 票据再建 EventSource；admin.html 登录后先申请 states 票据再建密钥状态 SSE，URL 不再出现凭据
- 验证：states 票据带管理 200 / 无凭据 401；switches 公开 200；SSE 用票据 200+事件、同一票据二次 401、无票 401；admin.html 登录后票据-SSE 正常、刷新重新申请票据、主前端公开 SSE 正常

### 13. 部署加固（高-05 + 中-06）
- 高-05 Linux 部署：
  - .env.example 删除可预测默认值，改为 CHANGE-ME 占位，新增 API_SIGN_SECRET / LICENSE_ENABLE_AUTH
  - docker-compose.yml：api 显式 License__EnableAuth（默认 true）、ApiSign__Secret、License__AdminToken 全部来自 .env；web 映射 80+443、挂载 ./certs 证书、构建注入 VITE_API_SIGN_SECRET
  - frontend/Dockerfile 注入 VITE_API_SIGN_SECRET；frontend/nginx.conf 补 CSP 与 Permissions-Policy
  - deploy-linux.sh：启动前校验 .env 非默认/占位值、校验 certs/server.crt+key 存在，不满足即拒绝启动（强制 HTTPS、容器内 Nginx 终止 TLS）
- 中-06 管理令牌持久化：
  - backend Dockerfile 为 app 用户创建 /app/data 可写目录；compose 挂 admin-data 卷
  - AdminTokenStore 令牌文件路径可配置（Data:AdminTokenFile）；Docker 使用持久化卷，本地默认写入用户 LocalApplicationData/PindouStudio，避免密钥进入源码和发布包；写入失败记录警告不再静默
- 验证：后端编译 0 警告 0 错误；本地健康 200、admin-token 正常（本地未配置 Data:AdminTokenFile 不受影响）

### 14. 高-01 阶段1：签名定位与导出接口补强
- 定位明确：HMAC 签名密钥写在前端，故签名仅是「防裸调 / 防重放 / 防篡改」的加固层，**不是服务端鉴权**；所有敏感接口（管理 / 云端存档 / 授权扣减 / 登录）均有独立业务鉴权（管理 Cookie+X-Admin-Token、密钥+会话令牌）
- 阶段1 补强：/api/exports/xlsx 为唯一「仅签名+限流」的敏感资源接口，新增请求体 1MB 大小限制与反序列化容错（保持大小写不敏感反序列化），配合限流与签名防裸调
- 验证：正常导出 200（Excel 生成）；>1MB 请求体返回 400「导出数据过大」
- 阶段2+3 已落地（详见第 15 节）

### 15. 认证重构：匿名 HttpOnly 会话 + 移除 HMAC 签名（高-01 阶段2/3）
- 阶段2：新增 Services/AnonymousSessionStore.cs（24 小时、容量 10 万），新增匿名会话中间件：对 /api 请求在 Cookie 缺失/失效时自动签发 HttpOnly 会话 Cookie（SameSite=Lax、Secure 依 HTTPS、Max-Age 24h）；会话不阻断访问，仅提供请求身份
- 阶段3：移除后端 HMAC 签名校验中间件、ApiSignGuard/nonce 缓存；移除前端 api.ts / admin.html 的签名逻辑（signedFetch→fetch、signedEventSource→EventSource）；部署配置移除 ApiSign（appsettings / docker-compose / frontend Dockerfile / .env.example / deploy-linux.sh）
- 安全边界：跨站伪造由 SameSite=Lax 会话 Cookie + CORS（生产仅 localhost）防护；业务鉴权仍由各接口自身的密钥/会话/管理 Cookie 承担（管理接口无凭据 401）
- 验证：无签名访问 catalog 200 并签发 pindou_session(HttpOnly)；管理无凭据 401；主前端加载/授权开关 SSE/豆针绘制正常；后台登录/密钥列表/states SSE(一次性 ticket) 正常；全程无应用错误

### 16. 安全复测残留问题修复（2026-08-24）
- 高-01：启动检查改为校验最终实际生效密钥（AdminTokenStore.Token），不再只读配置；AdminTokenStore 初始化时若文件仍保存旧默认密钥而配置已换成强密钥，以配置为准并覆写文件，杜绝旧文件静默覆盖新配置
- 中-01：三个 SSE 通道由 Channel.CreateUnbounded 改为 CreateBounded（容量 5、DropOldest 保留最新状态），防止客户端不消费时消息积压
- 中-02：POST /api/saves 新增独立限流策略 save（按 X-License-Key + IP 组合分区，每分钟 60 次、无队列直接 429）
- 附带重要修复：显式调用 app.UseRouting() 置于 UseRateLimiter 之前——此前 EndpointRoutingMiddleware 未先运行，RateLimitingMiddleware 读不到端点上的 RequireRateLimiting 元数据，导致所有接口限流静默失效；修复后实测 login 30 次/5 分钟、save 60 次/分钟均在超限后返回 429
- 验证：后端编译 0 警告 0 错误；启动日志出现「检测到默认管理密钥（实际生效）」警告；saves 连续 60 次正常/第 61 次起 429；SSE 端点正常

### 17. 默认管理密钥局域网风险修复与公网上线清单（2026-08-24）
- 本地：管理员密钥首次运行自动随机生成到用户 LocalApplicationData/PindouStudio/admin-token.txt，不再写入 appsettings.json 或源码目录；后端绑定 127.0.0.1:5080（局域网不可直连后端，前端走 8088/Vite 代理）
- 公网：compose 强制 Security__RejectDefaultSecrets=true；api 仅 expose 8080 不映射宿主端口，web 仅映射 80/443；deploy 校验非默认密钥
- 上线前清单写入 LINUX_DEPLOY.md（随机密钥/清理旧令牌/拒绝默认/开授权/仅 80:443/验证默认401新200）
- 验证：本机+新密钥 200、本机+默认密钥 401、局域网直连 5080 连接失败、局域网 8088 代理+新密钥 200/默认 401、后台页可达

### 18. 请求挂起根因修复与连接池加固（2026-08-25）
- 前端请求超时改为覆盖完整响应体读取，解决 fetch 已收到响应头、后续 json/blob 仍可能永久挂起的问题
- 新增普通 API 在途请求登记与统一取消；pagehide、组件销毁时主动中止旧请求
- localhost 开发时 API/SSE 直连 5080，与 8088 的 Vite HMR 连接隔离；局域网与生产仍保持原同源策略
- 联系方式运行时配置增加 8 秒加载超时，静态服务器异常不再阻塞工作台初始化
- MySQL 连接池：最小 0、最大 50、空闲 60 秒、连接寿命 300 秒、Keepalive 30 秒；连接/命令超时分别为 5/10 秒
- 修复 CanvasSaveStore、LicenseStore、IpTrialTracker、UserStore、PurchaseStore 在 OpenAsync 失败时未显式释放连接对象的窗口
- Kestrel 最大并发连接 256、Keep-Alive 60 秒；普通 API 全局并发 48、单路由并发 16，容量满时零排队并立即返回 503
- Nginx 上游单实例 max_conns=128、空闲池 32、空闲回收 30 秒、单连接最多复用 1000 次，并补齐连接/发送超时
- 验证：前端构建通过；后端 Debug/Release 构建 0 警告 0 错误；健康接口 200，30 次请求 P95 7ms；32 个同路由并发请求为 16×200 + 16×503；SSE 保活周期连接数稳定；压力后页面与服务正常
- 完整排查、配置和验证边界见：`心跳请求挂起隐患分析与修复清单.txt` 第八节

### 19. 好友联机功能完善（2026-08-25）
- 邀请码统一为 6 位：生成 / 复制 / 输入长度一致，采用不易混淆字符集（大写字母去 I/O、数字去 0/1）；刷新邀请码时校验唯一性，避免与现有房间冲突
- 豆子移除权限控制（CellOwner 归属）：成员不能移除房主放置的豆子，房主可移除任意成员的豆子，成员之间可相互移除；初始豆板无归属按房主所有处理
- 共享权限变更轻提示：房主开启 / 关闭成员的「共享（保存/导出）」权限时，成员端立即收到提示（与编辑权限提示并列）
- 右上角铃铛通知按钮：房主联机中时悬浮页面右上角顶层（z-index 9999），不遮挡正文；有新联机申请时红点 + 铃铛摇晃动效；点击展开轻弹窗展示待审批申请（同意 / 拒绝）
- 成员列表轻弹窗：点击顶部「联机中」按钮，在按钮正下方弹出窄宽度成员列表（约 300px），支持房主单独开关编辑 / 共享权限与踢出；弹窗内提供「联机详情与邀请码」入口
- 取消申请按钮优化：由通用 secondary 样式改为主题化的浅红描边按钮（带 × 图标与悬停反馈）
- 刷新后恢复联机状态：联机会话（令牌 / 阶段 / 邀请码）持久化到 sessionStorage；页面加载时自动重建阶段并重连 SSE，服务端 state 事件补齐房间、成员与画布快照；房间已失效时自动清理本地状态避免无限重连
- 修复编辑批量应用：editor store 新增 applyCellEdits 批量权威写入，修复 SSE 广播与冲突回滚调用不存在方法的问题

### 20. 全站语义图标与移动端窄屏体验（2026-08-26）
- 新增 AppIcon 统一语义图标组件，将图标图形、颜色、线宽和尺寸从按钮文字样式中解耦；补充搜索图标并为非对称图形增加光学重心补偿。
- 全站操作按钮接入语义图标，纯图标按钮补齐 title/aria-label；弹窗确认、取消和危险操作保留文字，避免仅凭图形产生误操作。
- 修复全屏按钮文字被旧 span 图标样式放大问题，进入/退出全屏按钮恢复紧凑尺寸，图标与文字水平居中。
- 开屏跳过图标重画为居中双箭头；“豆针”用户文案统一为“豆笔”。
- 移动端快捷工具（拖拽、豆笔、镊子、复制颜色）点击后在底部显示轻提示，桌面端不显示。
- 420px 以下顶部隐藏品牌文字，仅保留豆粒标志；“生成拼豆图纸”缩写为“生成图纸”；通用弹窗底部改为说明一行、按钮下一行等宽并排。
- 回退快照：`rollback/icon-refresh-20260826/`；前端构建通过，1440×900、390×844、339px 三档布局检查无横向溢出。
- Linux Docker Compose v2 发布包：`release/pindou-studio-linux-20260826-v2.tar.gz`，旧包保留作为回退版本。

## 历史问题与解决记录

- MySQL 8 不支持 ADD COLUMN IF NOT EXISTS：改为直接执行 ALTER TABLE，并对重复列做容错处理
- PowerShell 调用 curl 时 JSON 双引号被剥离导致 400：改用 Invoke-RestMethod
- 开发环境试用配置不生效：通过 launchSettings.json 设置 ASPNETCORE_ENVIRONMENT=Development 并读取开发配置
- 编辑文件权限受限：通过 node 脚本 / PowerShell 写入目标文件，绕过工作目录限制
- 编译失败：Pindou.Api.exe 进程锁文件，先停止进程再编译
- 端口占用：8088（前端 vite）、5080（后端）被占用时定位并清理对应进程
- MySQL 未随系统启动：联调前需手动启动 F:\mysql\mysql-8.0.46-winx64\bin\mysqld.exe（后台常驻，端口 3306）
- 密钥时长最小单位为小时：将管理接口改为秒级（TotalSeconds）
- 同 IP 多账号串号：ResolveByIpAsync 无条件按 IP 覆盖传入 UUID；改为仅全新 UUID 才按 IP 恢复
- 登录接口未放行 time_expired：期限到但次数可用的密钥无法登录；已修复
- 一登录就立即下线：登录按 IP 恢复账号后前端仍用本地 deviceId 校验；已修复为登录返回实际生效账号（UserId）
- 活跃优先策略不符合需求：同密钥不同 UUID 登录时旧设备不被踢；改为「新登录挤旧」
- 用户要求取消宽限：新设备登录后直接覆盖唯一会话令牌，并通过 SSE 立即提示旧设备下线
- 同 IP 测试串号伪影：localhost 联调所有请求同 IP 导致 ResolveByIp 串号；真实局域网双设备内网 IP 不同不串号，可用 X-Forwarded-For 模拟多 IP 验证
- 旧设备被挤下线需等心跳或刷新：新增 SSE 长连接（/api/license/events），新设备登录后推送 `session-kicked`；前端立即下线并提示，心跳校验兜底

## 2026-08-28 概念 UI 最终校准

- 以桌面端和移动端概念图为唯一视觉基准新增 `frontend/src/studio-concept.css`，采用末位加载的方式集中校准页面比例、功能层级和响应式行为，避免分散修改旧样式造成回归。
- 桌面端保持“顶部项目导航 + 左侧三段工具轨 + 中央豆板 + 右下设置卡 + 底部画布控制条”的结构；移动端保持“紧凑顶栏 + 画板 + 安全区底部工具 + 底部抽屉”的结构。
- 豆板仍使用现有高 DPI Canvas 渲染：圆豆为套在豆针上的哑光空心圆柱俯视模型，外径等于针距，相邻豆子完全相切；本轮未修改编辑算法和业务状态。
- 设计令牌与 `UI_DESIGN_SPEC.md` 同步，明确颜色 Hex、字体栈、字号、4px 栅格、断点、交互状态、圆角、阴影和动效。
- 交互式设计稿同步更新，能够切换查看工作台、弹窗、组件、图标和规范，作为后续新增功能的视觉验收基线。
- 回退快照：`rollback/ui-concept-recalibration-20260828-before.zip`。

### 图标资产与偏离方向修正

- 生成 56 个独立 SVG 图标和统一 Sprite，输出目录为 `frontend/public/icons/studio/`；`design-review/button-icon-review.html` 可直接逐项查看默认、选中和珊瑚红主操作状态。
- `AppIcon.vue` 已切换为加载上述 SVG 资产的 CSS Mask 渲染方式，继续支持 `currentColor`、响应式尺寸和原有语义别名。
- `studio-concept.css` 增加“概念图复刻校准 v2”，不再引入新视觉方向，直接锁定桌面/移动端结构比例、白纸表面、边框、阴影和工具栏尺寸。
- 前端生产构建通过；开发服务器和全部 57 个 SVG 请求（含 Sprite）返回 200。

### 指定组件规范条二次纠偏

- 用户确认之前的卡片式图标审查方向偏离，后续统一以“品牌色 / 图标风格 / 按钮样式 / 控件样式”横向规范条为唯一依据。
- 独立 SVG 导出线宽改为 1.65px；图标本身只保留黑色圆角线稿，不附加卡片外框。
- 全站主按钮、次要按钮、输入/下拉、开关、复选、单选和分段选择的视觉令牌已同步到 `studio-concept.css`。
- 新规范产物：`design-review/concept-component-strip.svg`、`design-review/concept-component-strip-preview.png`、`design-review/button-icon-review.html`。
- 图标一致性复核：新增 `scripts/verify-studio-icons.mjs`，当前 56 个独立 SVG 全部通过 24×24 / 1.65px / round 几何契约；资产目录补充 README，后续新增图标必须从 `studioIcons.ts` 导出。
