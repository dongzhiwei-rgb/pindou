/**
 * 文件：main.ts
 * 用途：创建 Vue 应用并挂载全局状态与样式。
 * 核心职责：保持启动过程精简，所有业务初始化由根组件和编辑器状态仓库负责。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-09-02
 */

import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import './styles.css'
import './ui-v3.css'

createApp(App).use(createPinia()).mount('#app')
