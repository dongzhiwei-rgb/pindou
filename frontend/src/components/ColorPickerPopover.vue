<!--
  文件：ColorPickerPopover.vue
  用途：提供可搜索、可键盘操作并能适配移动端/全屏模式的色号选择面板。
  核心职责：筛选厂家色卡、计算安全弹出位置、维护焦点和向父组件返回原始颜色索引。
  版权：@董志伟-联系方式-makabak1204
  最后修改：2026-09-04
-->

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { CSSProperties } from 'vue'
import AppIcon from './AppIcon.vue'
import type { BeadColor } from '../types'
import { useSoundStore } from '../stores/sound'

const props = defineProps<{
  colors: BeadColor[]
  modelValue: number
  brandName?: string
  paletteName?: string
  /** 仅展示指定的原始色卡索引；省略时展示完整色卡。 */
  allowedIndices?: number[]
  /** 按原始色卡索引提供使用数量；仅颜色替换等需要用量信息的场景传入。 */
  itemCounts?: Record<number, number> | number[]
  /** 未使用的候选色号（如完整品牌色卡），并入「未使用」分区；选中后由父组件追加为新色号。 */
  extraColors?: BeadColor[]
  /** 移动端底部导航使用调色盘主图标，完整面板仍显示实际选中色号。 */
  navigation?: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: number]
}>()

// 音效：打开色号选择弹窗时播放提示音，开关由底部信息栏「声音」控制。
const sound = useSoundStore()

const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const searchInput = ref<HTMLInputElement | null>(null)
const open = ref(false)
const query = ref('')
const popoverStyle = ref<CSSProperties>({})
// 原生 Popover 会进入浏览器 top layer，可覆盖原生 dialog 与 fullscreen；
// 老浏览器或调用失败时移除 popover 属性，继续使用原有 fixed 定位面板。
const nativePopoverEnabled = ref(
  typeof HTMLElement !== 'undefined' && typeof HTMLElement.prototype.showPopover === 'function',
)
let positionFrame = 0

// 预先生成搜索文本，输入时只做字符串匹配，避免重复拼接 200 多个色号。
// 当前图纸色板使用自身索引；未使用候选保留它在完整品牌色卡中的原始索引，
// 并编码为 colors.length + paletteIndex。父级据此可准确取回 palette.colors[paletteIndex]，
// 不会因过滤掉已使用色号后展示数组发生位移而选错颜色。
const indexedColors = computed(() => {
  const allowed = props.allowedIndices ? new Set(props.allowedIndices) : null
  const current = props.colors.map((color, index) => ({
    color,
    index,
    searchText: `${color.code} ${color.name} ${color.hex}`.toLowerCase(),
  }))
  const seen = new Set(props.colors.map(color => color.id))
  const extra = (props.extraColors || []).flatMap((color, paletteIndex) => {
    if (seen.has(color.id)) return []
    return [{
      color,
      index: props.colors.length + paletteIndex,
      searchText: `${color.code} ${color.name} ${color.hex}`.toLowerCase(),
    }]
  })

  return [...current, ...extra]
    .filter(item => !allowed || allowed.has(item.index))
})

const filteredColors = computed(() => {
  const keyword = query.value.trim().toLowerCase()
  return keyword
    ? indexedColors.value.filter(item => item.searchText.includes(keyword))
    : indexedColors.value
})

// 判断某个原始色卡索引是否已在画布上使用（兼容数组与稀疏对象两种用量来源）。
function isUsed(index: number): boolean {
  return (props.itemCounts?.[index] ?? 0) > 0
}

// 将色号拆成「已使用 / 未使用」两个独立分区，各自保持原始色卡顺序。
const usedColors = computed(() => filteredColors.value.filter(item => isUsed(item.index)))
const unusedColors = computed(() => filteredColors.value.filter(item => !isUsed(item.index)))

const selectedColor = computed(() => props.modelValue >= 0 ? props.colors[props.modelValue] : null)
const selectedCount = computed(() => props.itemCounts?.[props.modelValue])

/**
 * 桌面端将面板贴在按钮下方；空间不足时向上展开。
 * 手机端使用底部面板，避免被窄工具栏或软键盘挤出屏幕。
 */
function updatePosition(): void {
  const button = trigger.value
  if (!button || !open.value) return

  const forcedLandscape = root.value?.closest<HTMLElement>('.force-landscape')
  const viewportWidth = forcedLandscape?.clientWidth || window.innerWidth
  const viewportHeight = forcedLandscape?.clientHeight || window.innerHeight
  const edge = 8

  const useFullViewportPanel = (): void => {
    popoverStyle.value = {
      left: `${edge}px`,
      top: `${edge}px`,
      right: 'auto',
      bottom: 'auto',
      width: `${Math.max(280, viewportWidth - edge * 2)}px`,
      height: `${Math.max(200, viewportHeight - edge * 2)}px`,
      minHeight: '0',
      maxHeight: `${Math.max(200, viewportHeight - edge * 2)}px`,
      margin: '0',
      padding: '0',
    }
  }

  if (forcedLandscape || viewportWidth <= 640 || viewportHeight <= 520) {
    useFullViewportPanel()
    return
  }

  const rect = button.getBoundingClientRect()
  const desktopEdge = 12
  const gap = 8
  const width = Math.min(720, viewportWidth - desktopEdge * 2)
  const roomBelow = viewportHeight - rect.bottom - desktopEdge - gap
  const roomAbove = rect.top - desktopEdge - gap
  if (Math.max(roomBelow, roomAbove) < 260) {
    useFullViewportPanel()
    return
  }
  const openBelow = roomBelow >= 360 || roomBelow >= roomAbove
  const availableHeight = Math.min(620, openBelow ? roomBelow : roomAbove)
  // 优先在触发按钮右侧展开（贴合左侧窄工具栏的 PS 式侧栏弹出），右侧放不下时再回退为贴边对齐。
  const maxLeft = viewportWidth - width - desktopEdge
  const left = rect.right + gap + width <= viewportWidth - desktopEdge
    ? rect.right + gap
    : Math.min(Math.max(desktopEdge, rect.left), maxLeft)
  const top = openBelow
    ? rect.bottom + gap
    : Math.max(desktopEdge, rect.top - availableHeight - gap)

  popoverStyle.value = {
    left: `${Math.round(left)}px`,
    top: `${Math.round(top)}px`,
    right: 'auto',
    bottom: 'auto',
    width: `${Math.round(width)}px`,
    maxHeight: `${Math.round(availableHeight)}px`,
    margin: '0',
    padding: '0',
  }
}

// resize 与滚动可能连续触发，用一帧一次的方式合并位置计算。
function schedulePosition(): void {
  if (!open.value || positionFrame) return
  positionFrame = window.requestAnimationFrame(() => {
    positionFrame = 0
    updatePosition()
  })
}

async function toggle(): Promise<void> {
  if (open.value) {
    close()
    return
  }
  open.value = true

  // 打开色号选择弹窗提示音。
  sound.play('search')
  query.value = ''
  await nextTick()
  updatePosition()
  showNativePopover()
  searchInput.value?.focus()
}

function choose(index: number): void {
  emit('update:modelValue', index)
  close()
  trigger.value?.focus()
}

function close(): void {
  hideNativePopover()
  open.value = false
}

/**
 * 原生 Popover 负责进入 top layer；定位仍完全使用既有内联样式。
 * 若浏览器虽然暴露 API 但当前上下文拒绝打开，则立即去掉 popover 属性，
 * Vue 下一帧会把同一面板作为普通 fixed 元素展示，功能不会被阻断。
 */
function showNativePopover(): void {
  const element = panel.value
  if (!element || !nativePopoverEnabled.value) return
  try {
    if (!element.matches(':popover-open')) element.showPopover()
  } catch {
    nativePopoverEnabled.value = false
  }
}

function hideNativePopover(): void {
  const element = panel.value
  if (!element || !nativePopoverEnabled.value) return
  try {
    if (element.matches(':popover-open')) element.hidePopover()
  } catch {
    // 面板可能已被浏览器因全屏切换或页面卸载自动移出 top layer。
  }
}

// 浏览器自身关闭 top-layer popover（例如页面切换）时同步 Vue 状态，避免触发器仍显示展开。
function onNativePopoverToggle(event: Event): void {
  const state = (event as ToggleEvent).newState
  if (state === 'closed' && open.value) open.value = false
}

function onDocumentPointerDown(event: PointerEvent): void {
  const target = event.target as Node | null
  if (!target) return
  // 面板使用 Teleport 脱离工具栏层叠上下文，触发器和面板都要视作组件内部。
  if (root.value?.contains(target) || panel.value?.contains(target)) return
  close()
}

function onDocumentKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) {
    event.preventDefault()
    close()
    trigger.value?.focus()
  }
}

onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointerDown)
  document.addEventListener('keydown', onDocumentKeydown)
  window.addEventListener('resize', schedulePosition)
  window.addEventListener('scroll', schedulePosition, true)
})

onBeforeUnmount(() => {
  hideNativePopover()
  document.removeEventListener('pointerdown', onDocumentPointerDown)
  document.removeEventListener('keydown', onDocumentKeydown)
  window.removeEventListener('resize', schedulePosition)
  window.removeEventListener('scroll', schedulePosition, true)
  if (positionFrame) window.cancelAnimationFrame(positionFrame)
})

// 切换厂家或色卡后关闭旧面板，避免选到已失效的数组索引。
watch(() => props.colors, close)
</script>

<template>
  <div ref="root" class="color-picker-popover">
    <button
      ref="trigger"
      class="color-picker-button"
      type="button"
      aria-haspopup="dialog"
      :aria-expanded="open"
      title="选择或搜索色号"
      data-testid="color-picker-trigger"
      @click="toggle"
    >
      <AppIcon v-if="navigation" class="color-picker-navigation-icon" name="palette" />
      <i v-else :style="selectedColor ? { background: selectedColor.hex } : {}"></i>
      <span>{{ selectedColor?.code || '选择色号' }}</span>
      <em v-if="selectedCount !== undefined" class="color-picker-count">{{ selectedCount }}颗</em>
      <AppIcon class="color-picker-chevron" name="chevron-down" />
    </button>

    <Teleport to="body">
      <div
        v-if="open"
        ref="panel"
        class="color-popover-panel"
        :style="popoverStyle"
        :popover="nativePopoverEnabled ? 'manual' : undefined"
        role="dialog"
        aria-modal="false"
        aria-labelledby="color-popover-title"
        data-testid="color-picker-panel"
        @toggle="onNativePopoverToggle"
      >
      <header class="color-popover-header">
        <div>
          <small>{{ brandName }} · {{ paletteName }}</small>
          <h2 id="color-popover-title">选择豆子色号</h2>
        </div>
        <button type="button" aria-label="关闭色号选择" title="关闭 Esc" @click="close"><AppIcon name="close" /></button>
      </header>

      <label class="color-popover-search">
        <AppIcon name="search" />
        <input ref="searchInput" v-model="query" type="search" placeholder="搜索色号、名称或 HEX，例如 A01" />
        <small>{{ filteredColors.length }}/{{ indexedColors.length }}</small>
      </label>

      <div class="color-popover-grid">
        <template v-if="usedColors.length">
          <h3 class="color-popover-group-title is-first">已使用<small>{{ usedColors.length }}</small></h3>
          <button
            v-for="item in usedColors"
            :key="item.color.id"
            v-memo="[item.index === modelValue, itemCounts?.[item.index]]"
            type="button"
            :class="{ selected: item.index === modelValue }"
            :aria-pressed="item.index === modelValue"
            :aria-label="`${item.color.code} ${item.color.name}${itemCounts?.[item.index] !== undefined ? ` ${itemCounts[item.index]}颗` : ''}`"
            :title="`${item.color.code} ${item.color.name} ${item.color.hex}${itemCounts?.[item.index] !== undefined ? ` ${itemCounts[item.index]}颗` : ''}`"
            @click="choose(item.index)"
          >
            <i :style="{ background: item.color.hex }"></i>
            <span>
              <b>{{ item.color.code }}</b>
              <small>{{ item.color.name }}<template v-if="itemCounts?.[item.index] !== undefined"> · {{ itemCounts[item.index] }}颗</template></small>
            </span>
          </button>
        </template>
        <template v-if="unusedColors.length">
          <h3 class="color-popover-group-title" :class="{ 'is-first': !usedColors.length }">未使用<small>{{ unusedColors.length }}</small></h3>
          <button
            v-for="item in unusedColors"
            :key="item.color.id"
            v-memo="[item.index === modelValue, itemCounts?.[item.index]]"
            type="button"
            :class="{ selected: item.index === modelValue }"
            :aria-pressed="item.index === modelValue"
            :aria-label="`${item.color.code} ${item.color.name}${itemCounts?.[item.index] !== undefined ? ` ${itemCounts[item.index]}颗` : ''}`"
            :title="`${item.color.code} ${item.color.name} ${item.color.hex}${itemCounts?.[item.index] !== undefined ? ` ${itemCounts[item.index]}颗` : ''}`"
            @click="choose(item.index)"
          >
            <i :style="{ background: item.color.hex }"></i>
            <span>
              <b>{{ item.color.code }}</b>
              <small>{{ item.color.name }}<template v-if="itemCounts?.[item.index] !== undefined"> · {{ itemCounts[item.index] }}颗</template></small>
            </span>
          </button>
        </template>
        <p v-if="!usedColors.length && !unusedColors.length">没有找到匹配的色号</p>
      </div>
      </div>
    </Teleport>
  </div>
</template>
