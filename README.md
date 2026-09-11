<!--
文件：README.md
用途：拼了个豆（PixelBead Studio）项目说明：功能特性、技术架构、目录结构、本地开发、Docker 部署、接口与安全基线。
核心职责：为维护者与协作者提供完整的项目入门与设计说明，保证源码可读、可改、可部署。
版权：@董志伟-联系方式-makabak1204
最后修改：2026-09-11
-->

# 拼了个豆 · PixelBead Studio

一套前后端分离、可一键部署到 Linux 服务器的**拼豆图纸自助生成工作台**：上传任意图片，即可得到可打印、可备料、可联机协作的拼豆图纸。

- **前端**：Vue 3 + TypeScript + Pinia + Canvas 2D
- **后端**：ASP.NET Core 8 + MySQL + SkiaSharp（图像量化）+ Open XML SDK（Excel 导出）
- **部署**：Docker Compose（强制 HTTPS），无 Node/.NET/MySQL 环境需求

---

## 一、功能特性

### 图片转图纸（核心链路）
- 图片上传（粘贴 / 选文件）→ 按图纸比例裁剪 → 透明留白 → 量化转图纸
- 配色算法：像素聚合（默认）与 CIEDE2000 感知色差匹配两种
- Floyd–Steinberg 误差扩散，保留低色资源图的细节
- 连续背景去除（智能去底）

### 画布与编辑
- Canvas 分块渲染：万格图纸流畅滚动/缩放
- 工具：豆笔、取出（橡皮）、复制颜色、撤销/重做、颜色替换、拼接豆板（上限 160×160）
- 显示：真实圆豆 / 色块、完整坐标、网格、自动分板线
- 本地草稿自动保存，刷新不丢图

### 色卡与底板
- 支持 MARD 221/291、COCO、漫漫、盼盼、咪小窝及国际核心色卡（公开对照版）
- 2.6mm、5mm、10mm 底板规格；自定义 8×8 至 160×160 图纸
- 色卡豆径与底板兼容性检测

### 商用授权
- 密钥即账号：激活后生成图纸按期限与次数扣减
- IP 免费试用；单点登录（新登录踢旧会话）
- 剩余时长 / 次数实时显示；后台密钥管理（生成/停用/删除）

### 云端图纸库
- 按密钥跨设备同步「我的图纸」
- 命名、重命名、批量删除、覆盖确认

### 好友联机协作
- 最多 5 人实时协作编辑
- 邀请码入房、权限申请与审批、替换图纸审批
- 格子锁冲突仲裁、成员颜色标记、SSE 实时同步
- 支持踢人、退出回滚进房前快照

### 导出与分享
- 高清 PNG（含坐标/色块/分板线选项）
- Excel 图纸（后端生成）、CSV 色号材料清单、JSON 工程文件（可再导入）
- 熨烫效果预览（6 种表面质感）
- 跨浏览器接力（微信内置浏览器 → 系统浏览器）、微信内保存 PNG

### 体验细节
- 放豆 / 取豆 / 取色三类音效，可独立开关（localStorage 持久化）
- 桌面 / 平板 / 手机响应式；手机竖屏强制横屏创作
- 高频按钮（保存 / 生成 / 撤销）/ 常驻可见，不藏进「更多」抽屉

---

## 二、技术架构

### 系统拓扑

```text
浏览器（桌面 / 移动端 / 微信）
        │  HTTPS 443（Nginx 容器内终止 TLS）
        ▼
┌──────────────── Nginx (web) ────────────────┐
│  前端静态资源（Vue 构建产物）                    │
│  / → 前端; /api → 反代 API; 80 → 301 HTTPS    │
└───────────────┬──────────────────────────────┘
                │  同源 /api（Docker 内网）
                ▼
┌────────── ASP.NET Core 8 (api) ─────────────┐
│  授权/试用/心跳│量化生成│图纸库│联机(SSE)│导出  │
│  管理后台 /admin│ 管理令牌轮换│限流/熔断         │
└───────────────┬──────────────────────────────┘
                │  3306
                ▼
┌────────────── MySQL 8 (db) ─────────────────┐
│  用户/许可证/购买/图纸/联机数据                 │
└──────────────────────────────────────────────┘

持久化卷：db-data（MySQL）、admin-data（管理令牌文件）
```

### 关键设计决策

| 关注点 | 决策 | 说明 |
| --- | --- | --- |
| 跨端刷新 | 匿名 HttpOnly 会话 | SameSite=Lax 防 CSRF；CORS 生产仅本机 |
| 授权安全 | `Security__RejectDefaultSecrets=true` | 默认/占位管理密钥直接拒启 |
| SSE 安全 | 一次性短期票据 | URL 不出现令牌/密钥 |
| 日志安全 | 只记录令牌指纹 | 不输出密钥正文 |
| 图像性能 | 视口分块渲染 | 大画布不卡顿 |
| 联机一致性 | 服务端格级仲裁 + 格子锁 | 撤销只影响本人最后的格子 |

---

## 三、目录结构

```text
pindou-studio/
├─ frontend/                    Vue 3 前端
│  ├─ src/                      源码
│  │  ├─ App.vue                工作台主组件（全部弹窗/顶栏/工具编排）
│  │  ├─ main.ts                入口
│  │  ├─ styles.css / ui-v3.css / studio-*.css   主题与组件样式
│  │  ├─ api.ts                 后端接口封装
│  │  ├─ exporters.ts           PNG / JSON / CSV 导出
│  │  ├─ types.ts               类型定义
│  │  ├─ components/            交互组件（画布/色号选择/熨烫预览/开屏等）
│  │  ├─ stores/                Pinia 状态（editor/collab/sound）
│  │  ├─ icons/                 图标系统定义
│  │  └─ config/                developerContact（开发者联系方式）
│  ├─ public/                   静态资源（admin.html 管理后台 / 图标 / 音效 / 元数据）
│  ├─ index.html / vite.config.ts / tsconfig*.json
│  ├─ Dockerfile / nginx.conf / .dockerignore
│  └─ package.json / pnpm-lock.yaml / package-lock.json
├─ backend/
│  └─ Pindou.Api/               ASP.NET Core 8 后端
│     ├─ Program.cs             应用入口 + 全部路由
│     ├─ Services/              业务服务（授权/量化/联机/图纸/导出/会话等 20+）
│     ├─ Models/                数据模型（许可证/用户/联机/购买等）
│     ├─ Data/Palettes/         色卡 JSON 数据（7 款品牌）
│     ├─ Dockerfile             Linux 多阶段构建镜像
│     └─ Pindou.Api.csproj      项目文件（含 SkiaSharp / OpenXml）
├─ certs/                       HTTPS 证书（server.crt / server.key）占位
├─ docker-compose.yml           Linux 一键编排（db / api / web + 持久化卷）
├─ deploy-linux.sh              Linux 部署脚本（校验证书/.env → 构建启动）
├─ .env.example                 环境变量示例（必须改为强随机值）
├─ global.json                  .NET SDK 版本固定
├─ LINUX_DEPLOY.md              部署与上线前检查清单
├─ API.md                       HTTP 接口文档
├─ AGENTS.md                    项目开发约定（版权头/注释规范）
├─ CHANGELOG.md                 变更记录
├─ THIRD_PARTY_NOTICES.md       第三方资源与色卡数据说明
└─ README.md                    本文档
```

---

## 四、本地开发

### 环境要求
- .NET SDK 8.0（版本详见 `global.json`）
- Node.js 22 + pnpm（or npm）
- MySQL 8（或先以 Docker 起 db）

### 后端

```powershell
cd backend/Pindou.Api
dotnet restore
dotnet run --urls http://localhost:5080
```

开发默认 `ASPNETCORE_ENVIRONMENT=Development`，可无授权运行。

### 前端

```powershell
cd frontend
pnpm install        # 或 npm install
npm run dev         # 启动 Vite 开发服务器
```

- 前端开发服务器默认端口 `8088` 或不指定时 Vite 自动选择；
- 开发态前端 API/SSE 直连 `http://localhost:5080`（前端 `.env.development` / `VITE_API_BASE_URL` 配置）；
- 生产构建走同源 `/api`（Nginx 反代或 Vite 代理）。

### 生产构建验证

```powershell
cd frontend
npm run build       # 跑 vue-tsc 类型检查 + vite build
```

---

## 五、Linux Docker 部署（推荐）

最新发布包：`release/pindou-studio-linux-20260911-v1.tar.gz`（源码包 + SHA-256）。强制 HTTPS，完整步骤见 `LINUX_DEPLOY.md`。

```bash
mkdir -p /opt/pindou-studio
tar -xzf pindou-studio-linux-20260911-v1.tar.gz -C /opt/pindou-studio --strip-components=1
cd /opt/pindou-studio
cp .env.example .env   # 必改：MYSQL_ROOT_PASSWORD / MYSQL_PASSWORD / LICENSE_ADMIN_TOKEN
# 放入 HTTPS 证书 certs/server.crt 与 certs/server.key
sh deploy-linux.sh     # 一键校验并启动
```

要点：
- 服务器需要 Docker Engine 24+ 与 Docker Compose v2，无需 Node/.NET/Nginx/MySQL；
- 防火墙只放行 80（301 跳 HTTPS）与 443；API 仅在 Docker 内网暴露（`expose 8080`）；
- `.env` 校验：部署脚本检测到默认/占位值会拒绝启动；
- 管理后台：`https://域名/admin.html`（HttpOnly Cookie 会话，8 小时），或调用 `/api/admin/licenses`（`X-Admin-Token` 请求头）。

### 本地一键起三件套（本仓库即 compose 根）

```bash
cp .env.example .env   # 修改占位值
mkdir -p certs         # 放入证书
docker compose up -d --build
```

---

## 六、接口概览（详见 API.md）

| 分组 | 接口（示例） | 说明 |
| --- | --- | --- |
| 授权 | `POST /api/license/activate`、`POST /api/license/heartbeat` | 密钥激活 / 心跳续期 |
| 试用 | `GET /api/trial` | IP 试用剩余 |
| 图纸 | `POST /api/patterns/quantize`、`POST /api/patterns/aggregate-quantize` | 图像 → 豆图纸 |
| 云保存 | `POST /api/saves`、`GET /api/saves`、`DELETE /api/saves/:id` | 云端图纸增删查 |
| 联机 | `POST /api/collab/*`（room/join/approve/edits…）、SSE `/api/collab/events` | 房间与实时编辑 |
| 导出 | `POST /api/export/excel` | Excel 图纸生成 |
| 管理 | `POST/GET /api/admin/licenses`、`POST .../revoke` | 授权密钥管理 |
| 配置 | `GET /contact-config.json` 等 | 前端运行配置 |

---

## 七、安全基线

1. **管理凭据强制非默认**：`Security__RejectDefaultSecrets=true` + 部署脚本校验 `.env` 占位值；
2. **匿名 HttpOnly 会话**：SameSite=Lax 防跨站伪造，CORS 生产仅本机；
3. **SSE 一次性票据**：联机事件 URL 不携带任何令牌；
4. **限流与并入门控**：保存/登录/授权接口限流，防滥用；
5. **参数化 SQL**：MySqlConnector 参数化查询，防注入；
6. **请求超时熔断**：避免心跳等常驻请求无限挂起；
7. **管理令牌每日轮换**：写入持久化卷 `admin-data`，重启不回退；
8. **强制 HTTPS**：容器内 Nginx 终止 TLS，80 自动 301。

---

## 八、作者与版权

- 作者：董志伟（makabak1204）
- 许可证 / 商业授权：请联系开发者获取使用授权（前端授权弹窗「联系开发者」）
- 色卡数据与第三方资源声明见 `THIRD_PARTY_NOTICES.md`

---

## 九、路线图

- 自助购买接线（PurchaseStore 已实现，待接入 HTTP + 前端「获取密钥」）
- Redis 后台队列、批量生成、生成进度推送
- 更多品牌官方色卡（黄豆豆 / 小舞 / 卡卡 / DODO / ARTKAL）
- PDF 自动分页、A4 打印校准框、淘宝订单回传
- AI 主体抠图、毛发优化、肤色保护、图纸人工复核工作流