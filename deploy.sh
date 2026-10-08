#!/usr/bin/env bash
# 一键部署 BBDownT 网页版：生成访问令牌、构建镜像并在后台启动
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v docker >/dev/null 2>&1; then
  echo "未检测到 Docker，请先安装：curl -fsSL https://get.docker.com | sh" >&2
  exit 1
fi

if [ ! -f .env ]; then
  token=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
  printf 'BBDOWNT_API_TOKEN=%s\nBBDOWNT_PORT=23333\n' "$token" > .env
  chmod 600 .env
  echo "已生成 .env（访问令牌和端口）"
fi

mkdir -p data downloads
docker compose up -d --build

set -a; . ./.env; set +a
ip=$(hostname -I 2>/dev/null | awk '{print $1}')
echo
echo "部署完成"
echo "  访问地址：http://${ip:-服务器IP}:${BBDOWNT_PORT:-23333}"
echo "  访问令牌：${BBDOWNT_API_TOKEN}"
echo "  下载目录：$(pwd)/downloads"
echo "记得在云服务器安全组/防火墙放行 ${BBDOWNT_PORT:-23333} 端口。"
