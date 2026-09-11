<!--
文件：LINUX_DEPLOY.md
用途：说明拼了个豆 Linux 发布包的上传、配置、启动、更新与排错方法。
核心职责：让服务器运维人员无需本地开发环境即可完成 Docker Compose 部署（强制 HTTPS + 安全基线）。
版权：@董志伟-联系方式-makabak1204
最后修改：2026-09-11
-->

# Linux 服务器部署（强制 HTTPS）

## 服务器要求

- 64 位 Linux，建议 2 核 CPU、2 GB 以上内存；
- Docker Engine 24 或更高版本；
- Docker Compose v2；
- 能访问 Docker Hub 和微软容器仓库；
- 防火墙放行 `80/TCP`（自动跳转 HTTPS）与 `443/TCP`（HTTPS 服务）。

服务器不需要单独安装 Node.js、pnpm、.NET 或 Nginx，这些环境都在容器内完成。

## 证书（必须准备）

系统强制 HTTPS，启动前必须准备域名证书：

1. 将域名证书与私钥分别命名为 `server.crt`、`server.key`；
2. 放入发布包根目录的 `certs/` 文件夹（不存在则创建 `certs/`）；
3. 证书缺失时 `deploy-linux.sh` 会**拒绝启动**并提示。

## 上传与解压

```bash
mkdir -p /opt/pindou-studio
tar -xzf pindou-studio-linux-20260911-v3.tar.gz -C /opt/pindou-studio --strip-components=1
cd /opt/pindou-studio
```

## 配置 .env（必须修改占位值）

```bash
cp .env.example .env
```

必须将以下值改为强随机字符串（部署脚本检测到默认/占位值会拒绝启动）：

- `MYSQL_ROOT_PASSWORD`、`MYSQL_PASSWORD`：数据库密码；
- `LICENSE_ADMIN_TOKEN`：后台管理密钥；
- `LICENSE_ENABLE_AUTH`：建议保持 `true`（开启商用授权与试用）；设为 `false` 则所有用户无授权使用全部功能。

## 启动

```bash
sh deploy-linux.sh
```

启动后访问 `https://你的域名`（80 端口自动 301 跳转到 HTTPS）。API 只在 Docker 内部网络开放，公网只需放行 80/443。

## 商用授权与密钥管理

系统默认按 IP 提供免费试用：试用窗口与免费生成次数由 `License:TrialSeconds` / `License:TrialGenerations` 控制（镜像内默认 7200 秒 / 5 次，需调整时在 docker-compose 中为 api 服务注入环境变量后重新构建），到期后前端引导获取密钥。激活密钥后，生成图纸按密钥的使用期限与剩余次数扣减，不再受 IP 试用限制。

密钥管理入口：

- **后台管理页面**：浏览器访问 `https://你的域名/admin.html`，用「管理密钥」（`.env` 中的 `LICENSE_ADMIN_TOKEN`）登录即可生成、停用、删除、查看密钥；登录态为 HttpOnly Cookie 会话（8 小时），刷新保持登录。
- **API**（兼容 `X-Admin-Token` 请求头）：

```bash
# 生成密钥：有效期 30 天、可生成 200 张图纸
curl -X POST https://你的域名/api/admin/licenses \
  -H 'Content-Type: application/json' \
  -H 'X-Admin-Token: 你的管理令牌' \
  -d '{"validDays":30,"count":200}'

# 查询全部密钥
curl https://你的域名/api/admin/licenses \
  -H 'X-Admin-Token: 你的管理令牌'

# 停用指定密钥
curl -X POST https://你的域名/api/admin/licenses/PDXXXX/revoke \
  -H 'X-Admin-Token: 你的管理令牌'
```

说明：

- 密钥格式为 `PD` 开头的一串大写字母数字，用户在前端「密钥授权」弹窗中粘贴激活；
- 期限为绝对时间模型：剩余时长自密钥首次登录/首次心跳起按服务器时间消耗（离线与后台同样消耗），心跳不带时长累计；
- 每个密钥同时限制有效期与可生成次数，任一耗尽即失效；
- 「获取密钥」按钮目前仅引导联系开发者，购买流程待后续接入（自助购买表结构已建，接口未上线）；
- 管理密钥**每日 5:00 自动轮换**，新密钥写入容器持久化卷 `admin-data`（`/app/data/admin-token.txt`），重启容器不会回退。

## 常用维护命令

```bash
# 查看状态
docker compose ps

# 查看日志
docker compose logs -f --tail=200

# 重启
docker compose restart

# 更新发布包后重新构建
docker compose up -d --build --remove-orphans

# 停止服务（保留镜像）
docker compose down
```

## 上线前检查清单（必读）

部署到公网前逐项确认：

1. **随机强管理密钥**：`.env` 的 `LICENSE_ADMIN_TOKEN` 必须是随机长字符串（`deploy-linux.sh` 检测到默认/占位值会拒绝启动）；
2. **清理持久化旧令牌**：若历史数据卷 `admin-data` 中留有旧默认 `admin-token.txt`，后端启动时以新配置为准自动覆盖（旧默认不会继续生效）；
3. **拒绝默认密钥启动**：compose 已固定 `Security__RejectDefaultSecrets=true`，默认密钥直接拒启；
4. **开启授权**：`LICENSE_ENABLE_AUTH=true`；
5. **仅开放 80/443**：公网防火墙只放行 `80` 与 `443`，后端 5080/8080 不映射到宿主机、不对外开放；
6. **上线验证**：`curl -H 'X-Admin-Token: 默认密钥' .../api/admin/licenses` 必须返回 401；`X-Admin-Token: 新密钥` 必须返回 200。

## 安全说明

- 请求签名已移除（高-01 阶段3），改为**匿名 HttpOnly 会话**：跨站伪造由 SameSite=Lax 会话 Cookie 与 CORS（生产仅本机）防护；业务鉴权由各接口自身的密钥/会话/管理 Cookie 承担；
- 管理认证：后台页面用「管理密钥」登录（HttpOnly Cookie 会话），API 兼容 `X-Admin-Token`；
- SSE 连接使用**一次性短期票据**，URL 不再出现管理令牌/密钥/会话令牌；
- 日志只记录管理令牌的指纹，不输出正文；
- 生产环境务必修改全部默认凭据（部署脚本强制校验）。
