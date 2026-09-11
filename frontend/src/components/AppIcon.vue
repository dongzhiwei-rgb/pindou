<!--
  文件：AppIcon.vue
  用途：渲染“拼了个豆”V3 设计系统中的单色线性语义图标。
  核心职责：统一图标线宽、圆角、光学中心和别名映射，保证桌面端与移动端按钮视觉一致。
  版权：@董志伟-联系方式-makabak1204
  最后修改：2026-09-03
-->

<script setup lang="ts">
import { computed } from 'vue'
import {
  studioIconAliases,
  studioIconBodies,
  type StudioIconAlias,
  type StudioIconName,
} from '../icons/studioIcons'

export type AppIconName = StudioIconName | StudioIconAlias

interface OpticalAdjustment {
  x?: number
  y?: number
  scale?: number
}

const props = withDefaults(defineProps<{
  name: AppIconName
  size?: number | string
  strokeWidth?: number
}>(), {
  strokeWidth: 1.8,
})

// 非对称图形需要极小的光学补偿；几何中心不等于人眼感知中心。
const opticalAdjustments: Partial<Record<StudioIconName, OpticalAdjustment>> = {
  license: { x: 0.25, y: 0.3 },
  generate: { x: -0.25 },
  wechat: { x: -0.45 },
  rename: { x: 0.35 },
  undo: { x: 0.25 },
  redo: { x: -0.25 },
  needle: { x: -0.25 },
  ironed: { y: 0.35 },
  cloud: { y: 0.25 },
  search: { x: -0.25, y: 0.2 },
}

const semanticName = computed<StudioIconName>(() => (
  studioIconAliases[props.name as StudioIconAlias] ?? props.name as StudioIconName
))
const body = computed(() => studioIconBodies[semanticName.value])
const sizeValue = computed(() => {
  if (props.size === undefined) return undefined
  return typeof props.size === 'number' ? `${props.size}px` : props.size
})
const opticalTransform = computed(() => {
  const adjustment = opticalAdjustments[semanticName.value] ?? {}
  const scale = adjustment.scale ?? 0.96
  const centerX = 12 + (adjustment.x ?? 0)
  const centerY = 12 + (adjustment.y ?? 0)
  return `translate(${centerX} ${centerY}) scale(${scale}) translate(-12 -12)`
})
</script>

<template>
  <svg
    class="app-icon"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    :stroke-width="strokeWidth"
    stroke-linecap="round"
    stroke-linejoin="round"
    :style="sizeValue ? { width: sizeValue, height: sizeValue } : undefined"
    :data-icon="semanticName"
    aria-hidden="true"
    focusable="false"
  >
    <!-- body 仅来自本地受控常量，不接收用户输入。 -->
    <g :transform="opticalTransform" v-html="body"></g>
  </svg>
</template>

<style>
.app-icon .solid { fill: currentColor; stroke: none; }
</style>
