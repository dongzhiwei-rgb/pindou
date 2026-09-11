#!/usr/bin/env sh
# 文件：deploy-linux.sh
# 用途：在 Linux 服务器上一键构建并启动拼了个豆（强制 HTTPS、默认值校验）。
# 核心职责：检查 Docker Compose、校验 .env 安全值与 HTTPS 证书，然后后台更新容器。
# 版权：@董志伟-联系方式-makabak1204
# 最后修改：2026-08-24

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"

if ! command -v docker >/dev/null 2>&1; then
  echo "未检测到 Docker，请先安装 Docker Engine 和 Docker Compose 插件。" >&2
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "未检测到 docker compose，请先安装 Docker Compose 插件。" >&2
  exit 1
fi

# HTTPS 证书校验：强制 HTTPS，无证书拒绝启动。
if [ ! -f certs/server.crt ] || [ ! -f certs/server.key ]; then
  echo "安全校验失败：未找到 HTTPS 证书（certs/server.crt 与 certs/server.key）。" >&2
  echo "请将域名证书放入 certs/ 目录后重新执行；未配置证书前拒绝启动以保证上线即 HTTPS。" >&2
  exit 1
fi

# 首次部署：生成 .env 并提示修改占位值。
if [ ! -f .env ]; then
  cp .env.example .env
  echo "已从 .env.example 创建 .env，请先修改其中的 CHANGE-ME 占位值后再运行本脚本。"
  echo "安全校验失败：.env 仍为占位值。" >&2
  exit 1
fi

# 安全校验：禁止默认/占位凭据上线。
check_non_default() {
  name="$1"
  value="$2"
  case "$value" in
    ""|CHANGE-ME*|pindou-root|pindou-pass|change-this-admin-token|change-this-api-sign-secret*)
      echo "安全校验失败：$name 仍为默认/占位值，请先在 .env 中设置强随机值。" >&2
      exit 1;;
  esac
}
ADMIN_TOKEN=$(sed -n 's/^LICENSE_ADMIN_TOKEN=//p' .env | tail -n 1)
MYSQL_ROOT=$(sed -n 's/^MYSQL_ROOT_PASSWORD=//p' .env | tail -n 1)
MYSQL_PASS=$(sed -n 's/^MYSQL_PASSWORD=//p' .env | tail -n 1)
check_non_default LICENSE_ADMIN_TOKEN "$ADMIN_TOKEN"
check_non_default MYSQL_ROOT_PASSWORD "$MYSQL_ROOT"
check_non_default MYSQL_PASSWORD "$MYSQL_PASS"

docker compose up -d --build --remove-orphans
docker compose ps

echo "部署完成：https://服务器域名（80 端口自动跳转到 HTTPS）"
