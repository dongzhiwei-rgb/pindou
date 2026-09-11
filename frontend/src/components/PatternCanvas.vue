<!--
  文件：PatternCanvas.vue
  用途：高性能绘制和编辑拼豆网格画板。
  核心职责：仅渲染可见单元格，处理画笔/橡皮/复制颜色、撤销笔画、拖拽以及滚轮/双指缩放。
  版权：@董志伟-联系方式-makabak1204
  最后修改：2026-09-03
-->

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { COLLAB_COLORS, useCollabStore } from '../stores/collab'
import { useEditorStore } from '../stores/editor'
import { useSoundStore } from '../stores/sound'
import AppIcon from './AppIcon.vue'

const store = useEditorStore()
const {
  cells, contentRevision, colors, width, height, cellSize, beadShape, showCodes, showGrid, showBoardSplit, showCoordinates,
  selectedBoard, selectedColorIndex, hasPattern, interactionMode, editingLocked,
} = storeToRefs(store)
// 好友联机：格子锁描框、成员色选中框、编辑权限与批量提交共用联机状态仓库。
const collab = useCollabStore()
const { canEditLocal, myMember, lockFrames, locksVersion, isCollabing } = storeToRefs(collab)
// 音效：放豆 / 取出 / 打开色号选择器的提示音，开关由底部信息栏「声音」控制。
const sound = useSoundStore()
const canvas = ref<HTMLCanvasElement | null>(null)
const scrollContainer = ref<HTMLDivElement | null>(null)
const hover = ref(-1)
const coordinateIndex = ref(-1)
const painting = ref(false)
const panning = ref(false)
const paintValue = ref(-1)
const BASE_CELL_SIZE = 20
const MIN_CELL_SIZE = 2
const MAX_CELL_SIZE = 40
// PC 端（鼠标设备）键盘平移画布：WASD / 方向键，按住 Shift 加速；不切换交互模式，保持当前画笔/橡皮等工具。
const isFinePointer = typeof window !== 'undefined' && !!window.matchMedia?.('(pointer: fine)').matches
const KEYBOARD_PAN_STEP = 60
// 俯视圆豆的外圆直径等于网格步长，相邻豆子严格相切；材质层全部向内绘制，避免越界叠脏。
const BEAD_RADIUS_RATIO = 0.5
const displayWidth = computed(() => hasPattern.value ? width.value * cellSize.value : 360)
const displayHeight = computed(() => hasPattern.value ? height.value * cellSize.value : 360)
const viewportGutterX = ref(80)
const viewportGutterY = ref(80)
const panSpaceStyle = computed(() => ({
  width: `${displayWidth.value + viewportGutterX.value * 2}px`,
  height: `${displayHeight.value + viewportGutterY.value * 2}px`,
  '--canvas-gutter-x': `${viewportGutterX.value}px`,
  '--canvas-gutter-y': `${viewportGutterY.value}px`,
  '--pattern-width': `${displayWidth.value}px`,
  '--pattern-height': `${displayHeight.value}px`,
}))
type CoordinateLabel = { value: number; position: string }

/**
 * 坐标标签始终保留首尾，中间优先按 5、10、20、50 格分段。
 * 当前缩放较小时自动增大间隔，保证相邻数字至少留出约 28px，避免大图纸边缘挤成一排。
 */
function buildCoordinateLabels(total: number): CoordinateLabel[] {
  if (!hasPattern.value || total <= 0) return []
  const minimumPixels = 28
  const interval = [5, 10, 20, 50, 100].find(value => value * cellSize.value >= minimumPixels) || 100
  const values = [1]

  for (let value = interval; value < total; value += interval) {
    const previous = values[values.length - 1]
    if ((value - previous) * cellSize.value < minimumPixels) continue
    if ((total - value) * cellSize.value < minimumPixels) continue
    values.push(value)
  }
  if (total > 1) values.push(total)

  return values.map(value => ({
    value,
    position: `${(value - 0.5) * cellSize.value}px`,
  }))
}

const horizontalCoordinates = computed(() => buildCoordinateLabels(width.value))
const verticalCoordinates = computed(() => buildCoordinateLabels(height.value))
// 色卡只有在切换厂家或版本时变化，不应在每次滚动、绘制或指针移动时重新计算对比色。
const cachedColorStyles = computed(() => colors.value.map(color => {
  // 防御：色卡条目缺 hex 时用中性占位，避免绘制循环崩溃（正常数据不会触发）。
  const hex = typeof color?.hex === 'string' && color.hex ? color.hex : '#9aa3a0'
  const contrast = contrastColor(hex)
  return { contrast, hole: withAlpha(contrast, 0.22) }
}))
// 悬停框使用独立 DOM 图层移动，避免鼠标经过每个格子时重绘可视区域内的全部豆子。
// 联机时选中框边框用本人成员颜色；非联机沿用系统默认色。
const myFrameColor = computed(() => {
  if (!isCollabing.value || !myMember.value) return 'var(--green)'
  return COLLAB_COLORS[myMember.value.colorIndex] || COLLAB_COLORS[0]
})
const hoverStyle = computed(() => {
  if (hover.value < 0 || !hasPattern.value) return { display: 'none' }
  const x = hover.value % width.value
  const y = Math.floor(hover.value / width.value)
  return {
    left: `${viewportGutterX.value + x * cellSize.value + 1}px`,
    top: `${viewportGutterY.value + y * cellSize.value + 1}px`,
    width: `${Math.max(1, cellSize.value - 2)}px`,
    height: `${Math.max(1, cellSize.value - 2)}px`,
    borderColor: myFrameColor.value,
  }
})
const coordinateTooltip = computed(() => {
  if (!showCoordinates.value || coordinateIndex.value < 0 || !hasPattern.value) return null
  const x = coordinateIndex.value % width.value
  const y = Math.floor(coordinateIndex.value / width.value)
  if (x < 0 || x >= width.value || y < 0 || y >= height.value) return null
  return {
    label: `X ${x + 1} · Y ${y + 1}`,
    style: {
      left: `${viewportGutterX.value + (x + 0.5) * cellSize.value}px`,
      top: `${viewportGutterY.value + y * cellSize.value - 6}px`,
    },
  }
})
let drawFrame = 0
let viewportObserver: ResizeObserver | null = null
let coordinateHideTimer = 0
let panStartX = 0
let panStartY = 0
let panStartScrollLeft = 0
let panStartScrollTop = 0
let pinchStartDistance = 0
let pinchStartCellSize = BASE_CELL_SIZE
let renderOriginX = 0
let renderOriginY = 0
let renderCssWidth = 1
let renderCssHeight = 1
let strokeStarted = false
const panPointers = new Map<number, { x: number; y: number }>()

function pointerDistance(): number {
  const points = [...panPointers.values()]
  if (points.length < 2) return 0
  return Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y)
}

/**
 * 将画板四周的可移动区域设为三分之一视口大小。
 * 左上角可到达视口的三分之一处，右下角按相反方向保持对称。
 */
function updateViewportGutters(centerPattern = false): void {
  const container = scrollContainer.value
  if (!container) return

  const focusX = centerPattern
    ? displayWidth.value / 2
    : container.scrollLeft + container.clientWidth / 2 - viewportGutterX.value
  const focusY = centerPattern
    ? displayHeight.value / 2
    : container.scrollTop + container.clientHeight / 2 - viewportGutterY.value
  const stage = container.closest<HTMLElement>('.center-stage')
  const compactWorkspace = window.matchMedia('(max-width: 820px)').matches && !stage?.classList.contains('is-fullscreen')
  const nextGutterX = Math.max(80, container.clientWidth / 3)
  // 竖屏工作台需要让完整画板停在顶部 48px，同时保留底部拖拽余量；
  // 因此短画板使用更大的纵向留白，其他场景仍采用三分之一视口留白。
  const nextGutterY = compactWorkspace && displayHeight.value < container.clientHeight
    ? Math.max(80, container.clientHeight - displayHeight.value + 48)
    : Math.max(80, container.clientHeight / 3)

  if (!centerPattern && nextGutterX === viewportGutterX.value && nextGutterY === viewportGutterY.value) return
  viewportGutterX.value = nextGutterX
  viewportGutterY.value = nextGutterY

  nextTick(() => {
    container.scrollLeft = viewportGutterX.value + focusX - container.clientWidth / 2
    // 移动端设计稿采用“画板靠近顶部、控制区留在底部”的阅读顺序；
    // 这里只改变初次居中位置，三分之一视口的拖拽余量保持不变。
    container.scrollTop = compactWorkspace && centerPattern
      ? Math.max(0, viewportGutterY.value - 48)
      : viewportGutterY.value + focusY - container.clientHeight / 2
    scheduleDraw()
  })
}

function centerPatternInViewport(): void {
  updateViewportGutters(true)
}

/**
 * 按当前可视区域计算能完整容纳画板的单格尺寸，并把画板居中。
 * 生成大图纸时主动调用，避免用户进入拖拽模式后还要先手动缩小寻找画板边缘。
 */
async function fitPatternInViewport(): Promise<void> {
  const container = scrollContainer.value
  if (!container || !hasPattern.value) return
  const horizontalPadding = showCoordinates.value ? 64 : 32
  const verticalPadding = showCoordinates.value ? 52 : 32
  // 桌面工作台保留设计稿中的画布呼吸区；“适应画布”只负责完整显示，
  // 不把常见 32×32 图纸放大到占满整个舞台。移动端仍按可用宽高自适应。
  const fitLimit = isFinePointer ? 14 : MAX_CELL_SIZE
  const fittedSize = Math.floor(Math.min(
    (container.clientWidth - horizontalPadding) / Math.max(1, width.value),
    (container.clientHeight - verticalPadding) / Math.max(1, height.value),
    fitLimit,
  ))
  cellSize.value = Math.max(MIN_CELL_SIZE, fittedSize)
  await nextTick()
  centerPatternInViewport()
}

/**
 * 由底部进度条调整缩放时保持当前视口中心对应的图纸位置不动，
 * 避免大图纸在滑动过程中突然跳回左上角。
 */
async function setZoomFromCenter(nextSize: number): Promise<void> {
  const container = scrollContainer.value
  const previousSize = cellSize.value
  const normalizedSize = Math.min(MAX_CELL_SIZE, Math.max(MIN_CELL_SIZE, Math.round(nextSize)))
  if (!container || normalizedSize === previousSize) return
  const centerX = container.clientWidth / 2
  const centerY = container.clientHeight / 2
  const patternX = (container.scrollLeft + centerX - viewportGutterX.value) / previousSize
  const patternY = (container.scrollTop + centerY - viewportGutterY.value) / previousSize
  cellSize.value = normalizedSize
  await nextTick()
  container.scrollLeft = viewportGutterX.value + patternX * normalizedSize - centerX
  container.scrollTop = viewportGutterY.value + patternY * normalizedSize - centerY
  scheduleDraw()
}

defineExpose({ fitPatternInViewport, setZoomFromCenter })

// 多次指针移动只安排一帧重绘，避免大图纸在一次事件循环里重复刷新。
function scheduleDraw(): void {
  if (drawFrame) return
  drawFrame = window.requestAnimationFrame(() => {
    drawFrame = 0
    draw()
  })
}

function visibleCellRange(): { startX: number; endX: number; startY: number; endY: number } {
  const container = scrollContainer.value
  if (!container || !hasPattern.value) return { startX: 0, endX: width.value, startY: 0, endY: height.value }

  // 只为滚动视口内的格子绘制细节，额外保留一格避免边缘闪烁。
  const left = container.scrollLeft - viewportGutterX.value
  const top = container.scrollTop - viewportGutterY.value
  const right = left + container.clientWidth
  const bottom = top + container.clientHeight
  const size = cellSize.value
  return {
    startX: Math.max(0, Math.floor(left / size) - 1),
    endX: Math.min(width.value, Math.ceil(right / size) + 1),
    startY: Math.max(0, Math.floor(top / size) - 1),
    endY: Math.min(height.value, Math.ceil(bottom / size) + 1),
  }
}

/**
 * 使用少量实色图层模拟真实拼豆的圆柱外沿、投影、高光和中心孔。
 * 不使用逐颗径向渐变，避免大图纸在拖动或缩放时反复创建渐变对象造成卡顿。
 */
function drawRealisticBead(
  context: CanvasRenderingContext2D,
  hex: string,
  holeShade: string,
  left: number,
  top: number,
  size: number,
): void {
  const centerX = left + size / 2
  const centerY = top + size / 2
  const radius = Math.max(1.5, size * BEAD_RADIUS_RATIO)

  context.fillStyle = hex
  context.beginPath()
  context.arc(centerX, centerY, radius, 0, Math.PI * 2)
  context.fill()

  if (size >= 7) {
    // 外沿和下缘阴影均向内收，既表现空心塑料圆柱厚度，也保持相邻外圆零间隙。
    context.strokeStyle = 'rgba(24, 31, 27, .22)'
    context.lineWidth = Math.max(0.5, size * 0.042)
    context.beginPath()
    context.arc(centerX, centerY, Math.max(1, radius - context.lineWidth / 2), 0, Math.PI * 2)
    context.stroke()
    context.strokeStyle = 'rgba(24, 31, 27, .12)'
    context.lineWidth = Math.max(0.5, size * 0.055)
    context.beginPath()
    context.arc(centerX, centerY, Math.max(1, radius - context.lineWidth * 0.75), Math.PI * 0.05, Math.PI * 0.88)
    context.stroke()
  }

  if (size >= 14) {
    // 塑料表面只保留低对比柔光，不使用镜面高光。
    context.strokeStyle = 'rgba(255, 255, 255, .24)'
    context.lineWidth = Math.max(0.7, size * 0.045)
    context.lineCap = 'round'
    context.beginPath()
    context.arc(centerX, centerY, radius * 0.72, Math.PI * 1.08, Math.PI * 1.55)
    context.stroke()
  }

  if (size >= 10) {
    context.fillStyle = holeShade
    context.beginPath()
    context.arc(centerX, centerY, size * 0.13, 0, Math.PI * 2)
    context.fill()
    context.fillStyle = '#fffdf8'
    context.beginPath()
    context.arc(centerX, centerY, size * 0.084, 0, Math.PI * 2)
    context.fill()
  }
}

function draw(): void {
  const element = canvas.value
  if (!element) return
  const visible = visibleCellRange()
  if (visible.startX >= visible.endX || visible.startY >= visible.endY) return

  // Canvas 只覆盖当前视口附近的格子，不再为整张大图分配位图；高缩放时也能使用设备像素比清晰绘制。
  renderOriginX = visible.startX * cellSize.value
  renderOriginY = visible.startY * cellSize.value
  renderCssWidth = hasPattern.value
    ? Math.max(1, (visible.endX - visible.startX) * cellSize.value)
    : 360
  renderCssHeight = hasPattern.value
    ? Math.max(1, (visible.endY - visible.startY) * cellSize.value)
    : 360
  element.style.left = `${viewportGutterX.value + renderOriginX}px`
  element.style.top = `${viewportGutterY.value + renderOriginY}px`

  // 位图预算现在只约束可见分块，普通屏幕可稳定使用 2× DPR，超大视口仍会自动保护内存。
  const cssWidth = renderCssWidth
  const cssHeight = renderCssHeight
  const pixelBudgetRatio = Math.sqrt(12_000_000 / (cssWidth * cssHeight))
  const ratio = Math.max(0.5, Math.min(window.devicePixelRatio || 1, 2, pixelBudgetRatio, 8192 / cssWidth, 8192 / cssHeight))
  const pixelWidth = Math.max(1, Math.floor(cssWidth * ratio))
  const pixelHeight = Math.max(1, Math.floor(cssHeight * ratio))

  if (element.width !== pixelWidth) element.width = pixelWidth
  if (element.height !== pixelHeight) element.height = pixelHeight
  element.style.width = `${cssWidth}px`
  element.style.height = `${cssHeight}px`
  const context = element.getContext('2d')
  if (!context) return
  context.imageSmoothingEnabled = false
  context.setTransform(ratio, 0, 0, ratio, 0, 0)
  if (!hasPattern.value) {
    context.clearRect(0, 0, cssWidth, cssHeight)
    context.fillStyle = '#ffffff'
    context.fillRect(0, 0, cssWidth, cssHeight)
    return
  }

  context.clearRect(0, 0, cssWidth, cssHeight)
  context.fillStyle = '#ffffff'
  context.fillRect(0, 0, cssWidth, cssHeight)
  const drawCodes = showCodes.value && cellSize.value >= 18
  const colorStyles = cachedColorStyles.value

  if (drawCodes) {
    context.font = `600 ${Math.max(7, Math.floor(cellSize.value * 0.31))}px system-ui, sans-serif`
    context.textAlign = 'center'
    context.textBaseline = 'middle'
  }

  for (let y = visible.startY; y < visible.endY; y++) {
    for (let x = visible.startX; x < visible.endX; x++) {
      const index = y * width.value + x
      const colorIndex = cells.value[index]
      const left = x * cellSize.value - renderOriginX
      const top = y * cellSize.value - renderOriginY
      if (colorIndex < 0) {
        context.fillStyle = '#ffffff'
        context.fillRect(left, top, cellSize.value, cellSize.value)
        if (cellSize.value >= 5) {
          context.fillStyle = '#dedcd6'
          context.beginPath()
          context.arc(left + cellSize.value / 2, top + cellSize.value / 2, Math.max(0.6, cellSize.value * 0.13), 0, Math.PI * 2)
          context.fill()
        }
      } else {
        const color = colors.value[colorIndex]
        if (!color) continue
        if (beadShape.value === 'circle') {
          drawRealisticBead(context, color.hex, colorStyles[colorIndex]?.hole || 'rgba(0,0,0,.22)', left, top, cellSize.value)
        } else {
          context.fillStyle = color.hex
          context.fillRect(left + 0.6, top + 0.6, cellSize.value - 1.2, cellSize.value - 1.2)
        }
        if (drawCodes) {
          context.fillStyle = colorStyles[colorIndex]?.contrast || '#27342f'
          context.fillText(color.code, left + cellSize.value / 2, top + cellSize.value / 2)
        }
      }
    }
  }

  if (showGrid.value && cellSize.value >= 7) {
    context.strokeStyle = cellSize.value < 12 ? 'rgba(77,78,69,.08)' : 'rgba(77,78,69,.13)'
    context.lineWidth = 0.5
    context.beginPath()
    for (let x = visible.startX; x <= visible.endX; x++) {
      const localX = x * cellSize.value - renderOriginX
      context.moveTo(localX, 0)
      context.lineTo(localX, cssHeight)
    }
    for (let y = visible.startY; y <= visible.endY; y++) {
      const localY = y * cellSize.value - renderOriginY
      context.moveTo(0, localY)
      context.lineTo(cssWidth, localY)
    }
    context.stroke()
  }

  if (showBoardSplit.value && selectedBoard.value) {
    context.strokeStyle = '#ff6b57'
    context.lineWidth = 2.2
    context.setLineDash([8, 5])
    const firstBoardX = Math.max(selectedBoard.value.columns, Math.ceil(visible.startX / selectedBoard.value.columns) * selectedBoard.value.columns)
    const firstBoardY = Math.max(selectedBoard.value.rows, Math.ceil(visible.startY / selectedBoard.value.rows) * selectedBoard.value.rows)
    for (let x = firstBoardX; x < visible.endX; x += selectedBoard.value.columns) {
      const localX = x * cellSize.value - renderOriginX
      context.beginPath()
      context.moveTo(localX, 0)
      context.lineTo(localX, cssHeight)
      context.stroke()
    }
    for (let y = firstBoardY; y < visible.endY; y += selectedBoard.value.rows) {
      const localY = y * cellSize.value - renderOriginY
      context.beginPath()
      context.moveTo(0, localY)
      context.lineTo(cssWidth, localY)
      context.stroke()
    }
    context.setLineDash([])
  }

  // 联机格子锁描框：被成员占用编辑的格子以该成员颜色描边，按颜色分批绘制减少状态切换。
  if (isCollabing.value && lockFrames.value.length > 0) {
    const size = cellSize.value
    const byColor = new Map<number, Array<[number, number]>>()
    for (const frame of lockFrames.value) {
      const x = frame.cell % width.value
      const y = Math.floor(frame.cell / width.value)
      if (x < visible.startX || x >= visible.endX || y < visible.startY || y >= visible.endY) continue
      let list = byColor.get(frame.colorIndex)
      if (!list) { list = []; byColor.set(frame.colorIndex, list) }
      list.push([x, y])
    }
    byColor.forEach((cells, colorIndex) => {
      context.strokeStyle = COLLAB_COLORS[colorIndex] || COLLAB_COLORS[0]
      context.lineWidth = Math.max(1.5, size * 0.11)
      context.beginPath()
      for (const [x, y] of cells) {
        const localX = x * size - renderOriginX + 0.75
        const localY = y * size - renderOriginY + 0.75
        context.rect(localX, localY, size - 1.5, size - 1.5)
      }
      context.stroke()
    })
  }

}

function getCell(event: PointerEvent): number {
  const element = canvas.value
  if (!element) return -1
  const rect = element.getBoundingClientRect()
  if (!rect.width || !rect.height) return -1
  // Canvas 会随可见分块移动，先还原分块内 CSS 坐标，再加上图纸原点偏移得到真实格子。
  const localX = (event.clientX - rect.left) / rect.width * renderCssWidth
  const localY = (event.clientY - rect.top) / rect.height * renderCssHeight
  const x = Math.floor((renderOriginX + localX) / cellSize.value)
  const y = Math.floor((renderOriginY + localY) / cellSize.value)
  if (x < 0 || y < 0 || x >= width.value || y >= height.value) return -1
  return y * width.value + x
}

/**
 * 画笔直接修改 shallowRef 内的数组时，组件监听器不一定能从相同的数组引用判断出变化。
 * 在输入层主动安排下一帧重绘，既保证落笔即时可见，也能把高频拖画合并成每帧一次绘制。
 */
function paintCellAndRender(index: number): void {
  if (index < 0 || cells.value[index] === paintValue.value) return
  // 联机成员无编辑权限时禁止落笔；同一格正被他人编辑时拦截，避免与服务端冲突回滚。
  if (!canEditLocal.value) return
  if (collab.isCellLockedByOther(index)) return
  // 正值表示放豆，只允许写入空格；-1 表示取出，仍可移除已有豆子。
  if (paintValue.value >= 0 && cells.value[index] >= 0) return
  if (!strokeStarted) {
    collab.beginEditOperation()
    store.beginStroke()
    strokeStarted = true
  }
  store.paintCell(index, paintValue.value)
  // 联机时把本次编辑并入批量队列，由服务端仲裁后广播。
  collab.queueEdit(index, paintValue.value)
  // 音效：正值放豆（down），-1 取出（pick）。
  sound.play(paintValue.value >= 0 ? 'down' : 'pick')
  scheduleDraw()
}

function pointerDown(event: PointerEvent): void {
  if (!hasPattern.value) return
  event.preventDefault()
  if (interactionMode.value === 'pick') {
    if (editingLocked.value) return
    const colorIndex = cells.value[getCell(event)]
    if (colorIndex >= 0) {
      selectedColorIndex.value = colorIndex
      interactionMode.value = 'paint'
    }
    return
  }
  if (interactionMode.value === 'pan') {
    if (event.button !== 0) return
    const container = scrollContainer.value
    if (!container) return
    hover.value = -1
    coordinateIndex.value = -1
    if (coordinateHideTimer) window.clearTimeout(coordinateHideTimer)
    panPointers.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (panPointers.size >= 2) {
      panning.value = false
      pinchStartDistance = pointerDistance()
      pinchStartCellSize = cellSize.value
    } else {
      panning.value = true
      panStartX = event.clientX
      panStartY = event.clientY
      panStartScrollLeft = container.scrollLeft
      panStartScrollTop = container.scrollTop
    }
    canvas.value?.setPointerCapture(event.pointerId)
    return
  }

  painting.value = true
  paintValue.value = event.button === 2 || event.shiftKey ? -1 : selectedColorIndex.value
  strokeStarted = false
  const index = getCell(event)
  paintCellAndRender(index)
  // 移动端触摸落豆时即时显示当前格坐标，抬起后再延迟隐藏；PC 仍以悬浮方式显示。
  if (event.pointerType !== 'mouse' && showCoordinates.value) {
    coordinateIndex.value = index
    if (coordinateHideTimer) window.clearTimeout(coordinateHideTimer)
  }
  canvas.value?.setPointerCapture(event.pointerId)
}

function pointerMove(event: PointerEvent): void {
  // PC 使用悬浮查看格子坐标；移动端绘制拖动时跟随手指更新；拖动画板时暂停，避免提示框抖动。
  if ((event.pointerType === 'mouse' && !panning.value) || (painting.value && event.pointerType !== 'mouse')) {
    coordinateIndex.value = getCell(event)
  }

  if (interactionMode.value === 'pan' && panPointers.has(event.pointerId)) {
    panPointers.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (panPointers.size >= 2) {
      const distance = pointerDistance()
      if (pinchStartDistance > 0) {
        cellSize.value = Math.min(MAX_CELL_SIZE, Math.max(MIN_CELL_SIZE, Math.round(pinchStartCellSize * distance / pinchStartDistance)))
      }
      return
    }
  }

  if (panning.value) {
    const container = scrollContainer.value
    if (!container) return
    container.scrollLeft = panStartScrollLeft - (event.clientX - panStartX)
    container.scrollTop = panStartScrollTop - (event.clientY - panStartY)
    return
  }

  const index = getCell(event)
  if (hover.value !== index) hover.value = index
  if (painting.value) paintCellAndRender(index)
}

function pointerUp(event: PointerEvent, allowTapCoordinate = true): void {
  if (interactionMode.value === 'pan') {
    const isMobileTap = allowTapCoordinate
      && event.pointerType !== 'mouse'
      && panPointers.size === 1
      && Math.hypot(event.clientX - panStartX, event.clientY - panStartY) <= 8
    panPointers.delete(event.pointerId)
    const remaining = [...panPointers.values()][0]
    const container = scrollContainer.value
    if (remaining && container) {
      panning.value = true
      panStartX = remaining.x
      panStartY = remaining.y
      panStartScrollLeft = container.scrollLeft
      panStartScrollTop = container.scrollTop
    } else {
      panning.value = false
    }

    // 移动端拖拽模式下，短按显示坐标；真正拖动或双指缩放不会触发。
    if (isMobileTap && showCoordinates.value) {
      coordinateIndex.value = getCell(event)
      if (coordinateHideTimer) window.clearTimeout(coordinateHideTimer)
      coordinateHideTimer = window.setTimeout(() => {
        coordinateIndex.value = -1
        coordinateHideTimer = 0
      }, 2600)
    }
    return
  }

  if (!painting.value) return
  painting.value = false
  collab.endEditOperation()
  strokeStarted = false
  // 移动端手指抬起后延迟隐藏坐标提示；PC 悬浮逻辑不受影响。
  if (event.pointerType !== 'mouse' && showCoordinates.value && coordinateIndex.value >= 0) {
    if (coordinateHideTimer) window.clearTimeout(coordinateHideTimer)
    coordinateHideTimer = window.setTimeout(() => {
      coordinateIndex.value = -1
      coordinateHideTimer = 0
    }, 2600)
  }
}

function pointerLeave(event: PointerEvent): void {
  hover.value = -1
  if (event.pointerType === 'mouse') coordinateIndex.value = -1
}

async function wheelZoom(event: WheelEvent): Promise<void> {
  // 滚轮上下始终为缩放：不依赖交互模式（画笔/橡皮等任意模式下滚动即缩放），保持鼠标指向位置不动。
  if (!hasPattern.value || !event.deltaY) return
  const container = scrollContainer.value
  if (!container) return
  event.preventDefault()

  const previousSize = cellSize.value
  const nextSize = Math.min(MAX_CELL_SIZE, Math.max(MIN_CELL_SIZE, previousSize + (event.deltaY < 0 ? 2 : -2)))
  if (nextSize === previousSize) return

  // 缩放后尽量保持鼠标指向的图纸位置不动，减少反复拖回目标区域。
  const rect = container.getBoundingClientRect()
  const pointerX = event.clientX - rect.left
  const pointerY = event.clientY - rect.top
  const contentX = container.scrollLeft + pointerX - viewportGutterX.value
  const contentY = container.scrollTop + pointerY - viewportGutterY.value
  cellSize.value = nextSize
  await nextTick()
  const ratio = nextSize / previousSize
  container.scrollLeft = viewportGutterX.value + contentX * ratio - pointerX
  container.scrollTop = viewportGutterY.value + contentY * ratio - pointerY
}

function keydown(event: KeyboardEvent): void {
  const target = event.target as HTMLElement | null
  if (target?.matches('input, textarea, select, [contenteditable="true"]')) return

  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
    event.preventDefault()
    if (editingLocked.value) return
    if (isCollabing.value) {
      event.shiftKey ? void collab.redoOwnEdit() : void collab.undoOwnEdit()
      return
    }
    event.shiftKey ? store.redo() : store.undo()
  } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') {
    event.preventDefault()
    if (editingLocked.value) return
    if (isCollabing.value) {
      void collab.redoOwnEdit()
      return
    }
    store.redo()
  } else if (event.key.toLowerCase() === 'e') {
    store.selectEraserTool()
  } else if (event.key.toLowerCase() === 'b') {
    store.selectPaintTool()
  }

  // WASD / 方向键平移画布（仅 PC 端鼠标设备）：不切换交互模式，保持当前画笔/橡皮等工具，实现高效编辑。
  if (isFinePointer && !event.ctrlKey && !event.metaKey && !event.altKey) {
    const step = event.shiftKey ? KEYBOARD_PAN_STEP * 2 : KEYBOARD_PAN_STEP
    let dx = 0
    let dy = 0
    switch (event.key) {
      case 'w': case 'W': case 'ArrowUp': dy = -step; break
      case 's': case 'S': case 'ArrowDown': dy = step; break
      case 'a': case 'A': case 'ArrowLeft': dx = -step; break
      case 'd': case 'D': case 'ArrowRight': dx = step; break
      default: return
    }
    const container = scrollContainer.value
    if (!container || !hasPattern.value) return
    event.preventDefault()
    container.scrollLeft += dx
    container.scrollTop += dy
  }
}

/**
 * 双指缩放由画板自己的 Pointer Events 处理，禁止浏览器同时缩放整个网页。
 * touch-action 能覆盖现代浏览器；下面的非被动监听用于兼容 iOS Safari 和微信内置浏览器。
 */
function preventBrowserPinch(event: TouchEvent): void {
  if (event.touches.length > 1) event.preventDefault()
}

function preventBrowserGesture(event: Event): void {
  event.preventDefault()
}

// cells 是 shallowRef，联机撤销/恢复会批量原地修改数组；监听内容版本可稳定触发下一帧重绘，
// 不依赖数组引用变化，也不会为大图纸增加深度监听开销。
watch([contentRevision, colors, width, height, cellSize, beadShape, showCodes, showGrid, showBoardSplit, selectedBoard], scheduleDraw, { deep: false })
// 联机格子锁变化时重绘成员颜色描框（锁为非响应式 Map，由版本号驱动）。
watch(locksVersion, () => scheduleDraw())
watch([width, height, hasPattern], async () => {
  coordinateIndex.value = -1
  await nextTick()
  centerPatternInViewport()
})
watch(interactionMode, () => {
  panPointers.clear()
  panning.value = false
  painting.value = false
  strokeStarted = false
  coordinateIndex.value = -1
  if (coordinateHideTimer) window.clearTimeout(coordinateHideTimer)
})
watch(showCoordinates, visible => {
  if (!visible) coordinateIndex.value = -1
})
onMounted(() => {
  window.addEventListener('keydown', keydown)
  if (scrollContainer.value) {
    scrollContainer.value.addEventListener('touchmove', preventBrowserPinch, { passive: false })
    scrollContainer.value.addEventListener('gesturestart', preventBrowserGesture, { passive: false })
    scrollContainer.value.addEventListener('gesturechange', preventBrowserGesture, { passive: false })
    viewportObserver = new ResizeObserver(() => updateViewportGutters())
    viewportObserver.observe(scrollContainer.value)
  }
  centerPatternInViewport()
  scheduleDraw()
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', keydown)
  scrollContainer.value?.removeEventListener('touchmove', preventBrowserPinch)
  scrollContainer.value?.removeEventListener('gesturestart', preventBrowserGesture)
  scrollContainer.value?.removeEventListener('gesturechange', preventBrowserGesture)
  viewportObserver?.disconnect()
  if (drawFrame) window.cancelAnimationFrame(drawFrame)
  if (coordinateHideTimer) window.clearTimeout(coordinateHideTimer)
})

function contrastColor(hex: string): string {
  const value = String(hex || '').replace('#', '')
  const r = Number.parseInt(value.slice(0, 2), 16)
  const g = Number.parseInt(value.slice(2, 4), 16)
  const b = Number.parseInt(value.slice(4, 6), 16)
  return Number.isFinite(r) && Number.isFinite(g) && Number.isFinite(b) &&
    0.2126 * r + 0.7152 * g + 0.0722 * b < 120 ? '#ffffff' : '#27342f'
}

function withAlpha(hex: string, alpha: number): string {
  if (hex === '#ffffff') return `rgba(255,255,255,${alpha})`
  return `rgba(0,0,0,${alpha})`
}
</script>

<template>
  <div
    ref="scrollContainer"
    class="canvas-scroll"
    :class="{ 'pan-enabled': interactionMode === 'pan', 'pick-enabled': interactionMode === 'pick', 'is-panning': panning }"
    @wheel="wheelZoom"
    @scroll.passive="scheduleDraw"
  >
    <div class="canvas-pan-space" :style="panSpaceStyle">
      <div v-if="!hasPattern" class="canvas-empty">
        <span class="empty-mark"><AppIcon name="add" /></span>
        <h1>在线拼豆图纸自动生成</h1>
        <p>上传图片生成拼豆图纸，也可以新建空白豆板在线编辑</p>
        <small>支持多品牌拼豆色卡、用料统计与图纸导出</small>
      </div>
      <canvas
        ref="canvas"
        class="pattern-canvas"
        :class="{ 'is-empty': !hasPattern }"
        aria-label="拼豆图纸编辑豆板"
        @pointerdown="pointerDown"
        @pointermove="pointerMove"
        @pointerup="pointerUp"
        @pointercancel="pointerUp($event, false)"
        @pointerleave="pointerLeave"
        @contextmenu.prevent
      />
      <div v-if="hasPattern && showCoordinates" class="canvas-coordinate-axis horizontal" aria-hidden="true">
        <span
          v-for="coordinate in horizontalCoordinates"
          :key="coordinate.value"
          class="canvas-coordinate-label"
          :style="{ left: coordinate.position }"
        >{{ coordinate.value }}</span>
      </div>
      <div v-if="hasPattern && showCoordinates" class="canvas-coordinate-axis vertical" aria-hidden="true">
        <span
          v-for="coordinate in verticalCoordinates"
          :key="coordinate.value"
          class="canvas-coordinate-label"
          :style="{ top: coordinate.position }"
        >{{ coordinate.value }}</span>
      </div>
      <div
        v-if="coordinateTooltip"
        class="canvas-cell-coordinate"
        :style="coordinateTooltip.style"
        aria-hidden="true"
      >{{ coordinateTooltip.label }}</div>
      <div class="canvas-hover-cell" :style="hoverStyle" aria-hidden="true"></div>
    </div>
  </div>
</template>
