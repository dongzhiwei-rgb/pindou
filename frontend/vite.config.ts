/**
 * 文件：vite.config.ts
 * 用途：配置 Vue 前端开发服务器、局域网访问、受信域名和后端代理。
 * 核心职责：固定本地端口并把 /api 请求转发至 C# 服务，确保本机与局域网访问行为一致。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-08-21
 */

import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

const allowedHosts = ['275wt13op336.vicp.fun', '192.168.3.7']

export default defineConfig({
  plugins: [vue()],
  server: {
    host: '0.0.0.0',
    port: 8088,
    strictPort: true,
    allowedHosts,
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  preview: {
    host: '0.0.0.0',
    port: 8088,
    strictPort: true,
    allowedHosts,
  },
})
