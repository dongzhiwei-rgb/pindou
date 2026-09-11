# 文件：Dockerfile
# 用途：构建 Vue 前端并使用 Nginx 提供静态资源服务。
# 核心职责：隔离 Node 构建环境与轻量运行环境，保持服务器部署产物稳定。
# 版权：@董志伟-联系方式-makabak1204
# 最后修改：2026-08-21

FROM node:22-alpine AS build
WORKDIR /app
RUN corepack enable
COPY package.json pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY . .
ARG VITE_API_BASE_URL=
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN pnpm run build

FROM nginx:1.27-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
