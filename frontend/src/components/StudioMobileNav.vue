<!--
  文件：StudioMobileNav.vue
  用途：提供 V3 移动端工作台底部高频操作导航。
  核心职责：集中呈现拖拽、豆笔、镊子、复制颜色和颜色入口，并复用现有色号选择器。
  版权：@董志伟-联系方式-makabak1204
  最后修改：2026-09-11
-->

<script setup lang="ts">
import AppIcon from './AppIcon.vue'
import ColorPickerPopover from './ColorPickerPopover.vue'
import type { BeadColor } from '../types'

defineProps<{
  interactionMode: string
  selectedColorIndex: number
  colors: BeadColor[]
  colorCounts: number[] | Record<number, number>
  extraColors?: BeadColor[]
  brandName?: string
  paletteName?: string
  editingDisabled: boolean
}>()

const emit = defineEmits<{
  pan: []
  paint: []
  erase: []
  copy: []
  'update:color': [value: number]
}>()
</script>

<template>
  <nav class="studio-mobile-nav" aria-label="移动端快捷编辑">
    <button type="button" :class="{ active: interactionMode === 'pan' }" @click="emit('pan')">
      <AppIcon name="pan" /><span>拖拽</span>
    </button>
    <button type="button" :class="{ active: interactionMode === 'paint' && selectedColorIndex >= 0 }" :disabled="editingDisabled" @click="emit('paint')">
      <AppIcon name="needle" /><span>豆笔</span>
    </button>
    <button type="button" :class="{ active: interactionMode === 'paint' && selectedColorIndex === -1 }" :disabled="editingDisabled" @click="emit('erase')">
      <AppIcon name="tweezer" /><span>镊子</span>
    </button>
    <button type="button" :class="{ active: interactionMode === 'pick' }" :disabled="editingDisabled" title="复制豆板颜色" aria-label="复制豆板颜色" @click="emit('copy')">
      <AppIcon name="copy-color" /><span>复制</span>
    </button>
    <div class="studio-mobile-color" :class="{ active: interactionMode === 'paint' && selectedColorIndex >= 0 }">
      <ColorPickerPopover
        :model-value="selectedColorIndex"
        :colors="colors"
        :item-counts="colorCounts"
        :extra-colors="extraColors"
        :brand-name="brandName"
        :palette-name="paletteName"
        navigation
        @update:model-value="emit('update:color', $event)"
      />
      <span>颜色</span>
    </div>
  </nav>
</template>
