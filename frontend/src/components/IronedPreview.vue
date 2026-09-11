<!--
  文件：IronedPreview.vue
  用途：全屏展示拼豆充分熨烫后的视觉效果，并保存透明 PNG 效果图。
  核心职责：生成熔合豆粒纹理、支持鼠标/触摸拖拽缩放，并兼容浏览器与微信保存流程。
  版权：@董志伟-联系方式-makabak1204
  最后修改：2026-08-26
-->

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import AppIcon from './AppIcon.vue'
import { downloadBlob, safeFileName } from '../exporters'
import { useEditorStore } from '../stores/editor'

// 密钥次数用完时禁止保存效果图（下载），查看不受影响。
const props = defineProps<{ downloadDisabled?: boolean }>()
const emit = defineEmits<{ close: []; notify: [message: string] }>()
const store = useEditorStore()
const { cells, colors, width, height, title } = storeToRefs(store)
const viewport = ref<HTMLElement | null>(null)
const effectCanvas = ref<HTMLCanvasElement | null>(null)
const offsetX = ref(0)
const offsetY = ref(0)
const scale = ref(1)
const ready = ref(false)
const saving = ref(false)
const savePreviewUrl = ref('')
type FusionStyle = 'standard' | 'small-hole' | 'single-flat' | 'double-flat'
type SurfaceStyle = 'matte' | 'gloss' | 'towel' | 'grid' | 'glitter' | 'velvet'
const NORMAL_FUSION_STYLE: FusionStyle = 'single-flat'
const surfaceStyle = ref<SurfaceStyle>('matte')
const surfaceOptions: Array<{ value: SurfaceStyle; label: string }> = [
  { value: 'matte', label: '常规哑光' },
  { value: 'gloss', label: '亮面烫' },
  { value: 'towel', label: '毛巾烫' },
  { value: 'grid', label: '网格烫' },
  { value: 'glitter', label: '细闪烫' },
  { value: 'velvet', label: '丝绒烫' },
]
const pointers = new Map<number, { x: number; y: number }>()
let lastPointerX = 0
let lastPointerY = 0
let pinchStartDistance = 0
let pinchStartScale = 1
let pinchStartOffsetX = 0
let pinchStartOffsetY = 0
let pinchStartCenterX = 0
let pinchStartCenterY = 0
let renderFrame = 0
let hasFittedPattern = false

const canvasTransform = computed(() => ({
  transform: `translate3d(${offsetX.value}px, ${offsetY.value}px, 0) scale(${scale.value})`,
}))
const effectDescription = computed(() => {
  const surface = surfaceOptions.find(option => option.value === surfaceStyle.value)?.label || ''
  return `正常融合 · ${surface}`
})

function clampScale(value: number): number {
  return Math.min(6, Math.max(0.1, value))
}

function pointerDistance(): number {
  const points = [...pointers.values()]
  if (points.length < 2) return 0
  return Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y)
}

function pointerCenter(): { x: number; y: number } {
  const points = [...pointers.values()]
  if (points.length < 2) return points[0] || { x: 0, y: 0 }
  return { x: (points[0].x + points[1].x) / 2, y: (points[0].y + points[1].y) / 2 }
}

function beginPinch(): void {
  const center = pointerCenter()
  pinchStartDistance = pointerDistance()
  pinchStartScale = scale.value
  pinchStartOffsetX = offsetX.value
  pinchStartOffsetY = offsetY.value
  pinchStartCenterX = center.x
  pinchStartCenterY = center.y
}

function pointerDown(event: PointerEvent): void {
  if ((event.target as HTMLElement | null)?.closest('.ironed-preview-actions')) return
  event.preventDefault()
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY })
  if (pointers.size >= 2) beginPinch()
  else {
    lastPointerX = event.clientX
    lastPointerY = event.clientY
  }
  viewport.value?.setPointerCapture(event.pointerId)
}

function pointerMove(event: PointerEvent): void {
  if (!pointers.has(event.pointerId)) return
  event.preventDefault()
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY })

  if (pointers.size >= 2 && pinchStartDistance > 0) {
    const center = pointerCenter()
    const nextScale = clampScale(pinchStartScale * pointerDistance() / pinchStartDistance)
    const anchorX = (pinchStartCenterX - pinchStartOffsetX) / pinchStartScale
    const anchorY = (pinchStartCenterY - pinchStartOffsetY) / pinchStartScale
    offsetX.value = center.x - anchorX * nextScale
    offsetY.value = center.y - anchorY * nextScale
    scale.value = nextScale
    return
  }

  offsetX.value += event.clientX - lastPointerX
  offsetY.value += event.clientY - lastPointerY
  lastPointerX = event.clientX
  lastPointerY = event.clientY
}

function pointerUp(event: PointerEvent): void {
  pointers.delete(event.pointerId)
  const remaining = [...pointers.values()][0]
  if (remaining) {
    lastPointerX = remaining.x
    lastPointerY = remaining.y
  }
  pinchStartDistance = 0
}

function wheelZoom(event: WheelEvent): void {
  event.preventDefault()
  const element = viewport.value
  if (!element) return
  const rect = element.getBoundingClientRect()
  const pointerX = event.clientX - rect.left
  const pointerY = event.clientY - rect.top
  const contentX = (pointerX - offsetX.value) / scale.value
  const contentY = (pointerY - offsetY.value) / scale.value
  const nextScale = clampScale(scale.value * Math.exp(-event.deltaY * 0.0015))
  offsetX.value = pointerX - contentX * nextScale
  offsetY.value = pointerY - contentY * nextScale
  scale.value = nextScale
}

function fitPattern(): void {
  const container = viewport.value
  const canvas = effectCanvas.value
  if (!container || !canvas || !canvas.width || !canvas.height) return
  const padding = 56
  scale.value = clampScale(Math.min(
    (container.clientWidth - padding * 2) / canvas.width,
    (container.clientHeight - padding * 2) / canvas.height,
    1,
  ))
  offsetX.value = (container.clientWidth - canvas.width * scale.value) / 2
  offsetY.value = (container.clientHeight - canvas.height * scale.value) / 2
}

function mixColor(hex: string, target: number, amount: number): string {
  const value = hex.replace('#', '').padEnd(6, '0')
  const channels = [0, 2, 4].map(start => Number.parseInt(value.slice(start, start + 2), 16))
  const mixed = channels.map(channel => Math.round(channel + (target - channel) * amount))
  return `rgb(${mixed[0]}, ${mixed[1]}, ${mixed[2]})`
}

const fusionSettings: Record<FusionStyle, {
  radius: number
  holeRadius: number
  bridgeHalf: number
  bridgeThickness: number
  junctionMinimum: number
  junctionHalf: number
  overlap: number
}> = {
  standard: { radius: 30.5, holeRadius: 7.2, bridgeHalf: 0.13, bridgeThickness: 0.3, junctionMinimum: 5, junctionHalf: 0, overlap: 0 },
  'small-hole': { radius: 32.5, holeRadius: 3.8, bridgeHalf: 0.18, bridgeThickness: 0.5, junctionMinimum: 4, junctionHalf: 0.09, overlap: 0.015 },
  'single-flat': { radius: 33.5, holeRadius: 0, bridgeHalf: 0.34, bridgeThickness: 0.55, junctionMinimum: 3, junctionHalf: 0.28, overlap: 0.04 },
  'double-flat': { radius: 34.5, holeRadius: 0, bridgeHalf: 0.29, bridgeThickness: 0.84, junctionMinimum: 3, junctionHalf: 0.22, overlap: 0.055 },
}

function drawExposedBeadCaps(
  context: CanvasRenderingContext2D,
  left: number,
  top: number,
  size: number,
  color: string,
  hasLeft: boolean,
  hasRight: boolean,
  hasTop: boolean,
  hasBottom: boolean,
  straightLeft: boolean,
  straightRight: boolean,
  straightTop: boolean,
  straightBottom: boolean,
): void {
  const right = left + size
  const bottom = top + size
  const midX = left + size / 2
  const midY = top + size / 2
  const joinRadius = Math.max(0.45, size * 0.018)
  const straightDepth = size * 0.045
  const diagonalDepth = size * 0.08
  const diagonalCornerRadius = size * 0.55
  const lineWidth = Math.max(0.45, size * 0.01)

  /**
   * 连续直边使用三次曲线形成宽缓凸起；斜阶梯边使用由弦长和弓高计算出的真实圆弧段。
   * 两类边缘拥有独立的几何算法，不再只是共用曲线后调整深浅。
   */
  function fillAndStroke(shade: string, traceArc: () => void, closeShape: () => void): void {
    context.beginPath()
    traceArc()
    closeShape()
    context.closePath()
    context.fillStyle = color
    context.fill()

    context.beginPath()
    traceArc()
    context.strokeStyle = shade
    context.lineWidth = lineWidth
    context.stroke()
  }

  function traceStraightHorizontal(baseY: number, direction: -1 | 1): void {
    context.moveTo(left, baseY)
    const control = size * 0.22
    context.bezierCurveTo(left + control, baseY, midX - control, baseY + direction * straightDepth, midX, baseY + direction * straightDepth)
    context.bezierCurveTo(midX + control, baseY + direction * straightDepth, right - control, baseY, right, baseY)
  }

  function traceStraightVertical(baseX: number, direction: -1 | 1): void {
    context.moveTo(baseX, top)
    const control = size * 0.22
    context.bezierCurveTo(baseX, top + control, baseX + direction * straightDepth, midY - control, baseX + direction * straightDepth, midY)
    context.bezierCurveTo(baseX + direction * straightDepth, midY + control, baseX, bottom - control, baseX, bottom)
  }

  function circularRadius(depth: number): number {
    const halfChord = size / 2
    return (halfChord * halfChord + depth * depth) / (depth * 2)
  }

  function traceDiagonalHorizontal(baseY: number, direction: -1 | 1): void {
    const radius = circularRadius(diagonalDepth)
    const centerY = baseY - direction * (radius - diagonalDepth)
    const startAngle = Math.atan2(baseY - centerY, left - midX)
    const endAngle = Math.atan2(baseY - centerY, right - midX)
    context.moveTo(left, baseY)
    context.arc(midX, centerY, radius, startAngle, endAngle, direction > 0)
  }

  function traceDiagonalVertical(baseX: number, direction: -1 | 1): void {
    const radius = circularRadius(diagonalDepth)
    const centerX = baseX - direction * (radius - diagonalDepth)
    const startAngle = Math.atan2(top - midY, baseX - centerX)
    const endAngle = Math.atan2(bottom - midY, baseX - centerX)
    context.moveTo(baseX, top)
    context.arc(centerX, midY, radius, startAngle, endAngle, direction < 0)
  }

  const diagonalTopLeft = !hasTop && !hasLeft && !straightTop && !straightLeft
  const diagonalTopRight = !hasTop && !hasRight && !straightTop && !straightRight
  const diagonalBottomRight = !hasBottom && !hasRight && !straightBottom && !straightRight
  const diagonalBottomLeft = !hasBottom && !hasLeft && !straightBottom && !straightLeft
  const roundedTop = diagonalTopLeft || diagonalTopRight
  const roundedRight = diagonalTopRight || diagonalBottomRight
  const roundedBottom = diagonalBottomRight || diagonalBottomLeft
  const roundedLeft = diagonalBottomLeft || diagonalTopLeft

  /**
   * 斜边扇贝由完整圆豆外周形成，而不是由横、竖两段曲线在角点拼接。
   * 只有检测到斜阶梯凸角的豆子才进入此分支，连续横边和竖边不受影响。
   */
  function drawDiagonalBead(enabled: boolean): void {
    if (!enabled) return
    context.fillStyle = color
    context.beginPath()
    context.arc(midX, midY, diagonalCornerRadius, 0, Math.PI * 2)
    context.fill()
  }

  if (!hasTop && !roundedTop) {
    fillAndStroke(
      'rgba(255,255,255,.05)',
      () => {
        if (straightTop) traceStraightHorizontal(top, -1)
        else traceDiagonalHorizontal(top, -1)
      },
      () => {
        context.lineTo(right, midY)
        context.lineTo(left, midY)
      },
    )
  }
  if (!hasLeft && !roundedLeft) {
    fillAndStroke(
      'rgba(255,255,255,.03)',
      () => {
        if (straightLeft) traceStraightVertical(left, -1)
        else traceDiagonalVertical(left, -1)
      },
      () => {
        context.lineTo(midX, bottom)
        context.lineTo(midX, top)
      },
    )
  }
  if (!hasRight && !roundedRight) {
    fillAndStroke(
      'rgba(0,0,0,.04)',
      () => {
        if (straightRight) traceStraightVertical(right, 1)
        else traceDiagonalVertical(right, 1)
      },
      () => {
        context.lineTo(midX, bottom)
        context.lineTo(midX, top)
      },
    )
  }
  if (!hasBottom && !roundedBottom) {
    fillAndStroke(
      'rgba(0,0,0,.055)',
      () => {
        if (straightBottom) traceStraightHorizontal(bottom, 1)
        else traceDiagonalHorizontal(bottom, 1)
      },
      () => {
        context.lineTo(right, midY)
        context.lineTo(left, midY)
      },
    )
  }

  drawDiagonalBead(diagonalTopLeft || diagonalTopRight || diagonalBottomRight || diagonalBottomLeft)

  // 仅在斜阶梯转向点补一个很小的圆角，防止端点形成像素尖刺。
  const junctions: Array<[number, number]> = []
  if (!hasTop && !straightTop && !roundedTop) junctions.push([left, top], [right, top])
  if (!hasLeft && !straightLeft && !roundedLeft) junctions.push([left, top], [left, bottom])
  if (!hasRight && !straightRight && !roundedRight) junctions.push([right, top], [right, bottom])
  if (!hasBottom && !straightBottom && !roundedBottom) junctions.push([left, bottom], [right, bottom])
  context.fillStyle = color
  junctions.forEach(([x, y]) => {
    context.beginPath()
    context.arc(x, y, joinRadius, 0, Math.PI * 2)
    context.fill()
  })
}

function createBeadSprite(hex: string, settings: typeof fusionSettings[FusionStyle]): HTMLCanvasElement {
  const sprite = document.createElement('canvas')
  sprite.width = 72
  sprite.height = 72
  const context = sprite.getContext('2d')
  if (!context) return sprite

  // 不同熔合程度共享同一光照，只改变豆面扩张、连接宽度和中心孔闭合程度。
  const body = context.createRadialGradient(27, 23, 4, 36, 37, 34)
  body.addColorStop(0, mixColor(hex, 255, 0.16))
  body.addColorStop(0.6, hex)
  body.addColorStop(1, mixColor(hex, 0, 0.1))
  context.fillStyle = body
  context.beginPath()
  context.arc(36, 36, settings.radius, 0, Math.PI * 2)
  context.fill()

  context.strokeStyle = 'rgba(255, 255, 255, .14)'
  context.lineWidth = 1
  context.beginPath()
  context.arc(36, 36, Math.max(1, settings.radius - 0.7), 0, Math.PI * 2)
  context.stroke()

  if (settings.holeRadius > 0) {
    // 有孔模式使用真实透明孔，保存 PNG 后也能保留对应的熨烫结构。
    context.save()
    context.globalCompositeOperation = 'destination-out'
    context.beginPath()
    context.arc(36, 36, settings.holeRadius, 0, Math.PI * 2)
    context.fill()
    context.restore()
    context.strokeStyle = 'rgba(0,0,0,.15)'
    context.lineWidth = 1
    context.beginPath()
    context.arc(36, 36, settings.holeRadius + 0.45, 0, Math.PI * 2)
    context.stroke()
  } else {
    // 无孔模式仅留下极弱的压痕高光，避免整张效果图出现重复黑点。
    context.strokeStyle = 'rgba(255,255,255,.1)'
    context.lineWidth = 0.8
    context.beginPath()
    context.arc(35.5, 35.5, 1.5, Math.PI * 0.9, Math.PI * 1.7)
    context.stroke()
  }
  return sprite
}

function applyPlasticMaterial(context: CanvasRenderingContext2D, canvas: HTMLCanvasElement): void {
  context.save()
  context.globalCompositeOperation = 'source-atop'

  // 熨烫后的塑料面以左上方柔光和右下方轻微阴影塑造厚度，不为每颗豆重复绘制高光圆点。
  const directionalLight = context.createLinearGradient(0, 0, canvas.width, canvas.height)
  directionalLight.addColorStop(0, 'rgba(255,255,255,.11)')
  directionalLight.addColorStop(0.44, 'rgba(255,255,255,.025)')
  directionalLight.addColorStop(0.72, 'rgba(0,0,0,.018)')
  directionalLight.addColorStop(1, 'rgba(0,0,0,.075)')
  context.fillStyle = directionalLight
  context.fillRect(0, 0, canvas.width, canvas.height)

  // 使用固定的小纹理平铺模拟熨烫纸留下的细微塑料颗粒，避免大图逐像素生成造成卡顿。
  const grainTile = document.createElement('canvas')
  grainTile.width = 40
  grainTile.height = 40
  const grain = grainTile.getContext('2d')
  if (grain) {
    for (let index = 0; index < 34; index++) {
      const x = (index * 17 + 5) % 40
      const y = (index * 29 + 9) % 40
      grain.fillStyle = index % 3 === 0 ? 'rgba(255,255,255,.045)' : 'rgba(0,0,0,.025)'
      grain.fillRect(x, y, index % 5 === 0 ? 1.2 : 0.7, 0.7)
    }
    const grainPattern = context.createPattern(grainTile, 'repeat')
    if (grainPattern) {
      context.fillStyle = grainPattern
      context.fillRect(0, 0, canvas.width, canvas.height)
    }
  }
  context.restore()
}

function applySurfaceTexture(context: CanvasRenderingContext2D, canvas: HTMLCanvasElement, style: SurfaceStyle): void {
  context.save()
  context.globalCompositeOperation = 'source-atop'

  if (style === 'matte') {
    const matteLight = context.createLinearGradient(0, 0, canvas.width, canvas.height)
    matteLight.addColorStop(0, 'rgba(255,255,255,.06)')
    matteLight.addColorStop(0.55, 'rgba(255,255,255,0)')
    matteLight.addColorStop(1, 'rgba(0,0,0,.025)')
    context.fillStyle = matteLight
    context.fillRect(0, 0, canvas.width, canvas.height)
    context.restore()
    return
  }

  if (style === 'gloss') {
    const sheen = context.createLinearGradient(0, 0, canvas.width, canvas.height)
    sheen.addColorStop(0, 'rgba(255,255,255,.22)')
    sheen.addColorStop(0.42, 'rgba(255,255,255,.04)')
    sheen.addColorStop(0.58, 'rgba(0,0,0,.04)')
    sheen.addColorStop(1, 'rgba(255,255,255,.12)')
    context.fillStyle = sheen
    context.fillRect(0, 0, canvas.width, canvas.height)
    context.restore()
    return
  }

  // 使用小型可重复纹理，而不是遍历整张大图逐点绘制，160×160 图纸也能快速切换效果。
  const tile = document.createElement('canvas')
  tile.width = 48
  tile.height = 48
  const tileContext = tile.getContext('2d')
  if (!tileContext) {
    context.restore()
    return
  }

  if (style === 'towel') {
    for (let index = 0; index < 44; index++) {
      const x = (index * 17 + 7) % 48
      const y = (index * 29 + 11) % 48
      tileContext.strokeStyle = index % 3 === 0 ? 'rgba(0,0,0,.07)' : 'rgba(255,255,255,.14)'
      tileContext.lineWidth = 0.8
      tileContext.beginPath()
      tileContext.moveTo(x, y + 2)
      tileContext.quadraticCurveTo(x + 2, y - 2, x + 4, y + 1)
      tileContext.stroke()
    }
  } else if (style === 'grid') {
    tileContext.strokeStyle = 'rgba(0,0,0,.09)'
    tileContext.lineWidth = 1
    for (let position = 0; position <= 48; position += 8) {
      tileContext.beginPath()
      tileContext.moveTo(position, 0)
      tileContext.lineTo(position, 48)
      tileContext.moveTo(0, position)
      tileContext.lineTo(48, position)
      tileContext.stroke()
    }
  } else if (style === 'glitter') {
    for (let index = 0; index < 22; index++) {
      const x = (index * 19 + 5) % 48
      const y = (index * 31 + 9) % 48
      const radius = index % 4 === 0 ? 1.4 : 0.75
      tileContext.fillStyle = index % 3 === 0 ? 'rgba(235,242,255,.78)' : 'rgba(255,255,255,.5)'
      tileContext.beginPath()
      tileContext.arc(x, y, radius, 0, Math.PI * 2)
      tileContext.fill()
    }
  } else if (style === 'velvet') {
    tileContext.lineWidth = 0.65
    for (let position = -48; position < 96; position += 5) {
      tileContext.strokeStyle = position % 10 === 0 ? 'rgba(255,255,255,.08)' : 'rgba(0,0,0,.055)'
      tileContext.beginPath()
      tileContext.moveTo(position, 0)
      tileContext.lineTo(position + 34, 48)
      tileContext.stroke()
    }
  }

  const pattern = context.createPattern(tile, 'repeat')
  if (pattern) {
    context.fillStyle = pattern
    context.fillRect(0, 0, canvas.width, canvas.height)
  }
  context.restore()
}

function renderEffect(): void {
  const canvas = effectCanvas.value
  if (!canvas) return
  ready.value = false
  const settings = fusionSettings[NORMAL_FUSION_STYLE]
  const maxSide = Math.max(width.value, height.value)
  // 小图提高单格像素密度，大图限制最长边约 3200px，在高清轮廓和内存占用之间保持平衡。
  const beadSize = Math.max(6, Math.min(42, Math.floor(3200 / Math.max(1, maxSide))))
  const isFlatMelt = settings.holeRadius === 0
  // 平熔只发生在豆子相接的位置；最外圈没有邻居挤压，仍会留下轻微圆弧凸起。
  // 画布四周预留透明空间，避免外凸轮廓被 Canvas 边界裁掉。
  const renderPadding = isFlatMelt ? Math.ceil(beadSize * 0.12) : 0
  canvas.width = width.value * beadSize + renderPadding * 2
  canvas.height = height.value * beadSize + renderPadding * 2
  const context = canvas.getContext('2d')
  if (!context) return
  context.clearRect(0, 0, canvas.width, canvas.height)
  context.imageSmoothingEnabled = true

  // 先绘制相邻豆子的熔合颈部，再覆盖圆润豆面。只连接真实相邻的豆子，透明区域仍保持透明。
  const bridgeHalf = beadSize * settings.bridgeHalf
  const bridgeThickness = beadSize * settings.bridgeThickness
  cells.value.forEach((colorIndex, index) => {
    if (colorIndex < 0) return
    const x = index % width.value
    const y = Math.floor(index / width.value)
    const color = colors.value[colorIndex]?.hex || '#ffffff'
    const rightIndex = x + 1 < width.value ? cells.value[index + 1] : -1
    if (rightIndex >= 0) {
      const seamX = renderPadding + (x + 1) * beadSize
      const top = renderPadding + y * beadSize + (beadSize - bridgeThickness) / 2
      context.fillStyle = color
      context.fillRect(seamX - bridgeHalf, top, bridgeHalf, bridgeThickness)
      context.fillStyle = colors.value[rightIndex]?.hex || '#ffffff'
      context.fillRect(seamX, top, bridgeHalf, bridgeThickness)
    }

    const bottomIndex = y + 1 < height.value ? cells.value[index + width.value] : -1
    if (bottomIndex >= 0) {
      const seamY = renderPadding + (y + 1) * beadSize
      const left = renderPadding + x * beadSize + (beadSize - bridgeThickness) / 2
      context.fillStyle = color
      context.fillRect(left, seamY - bridgeHalf, bridgeThickness, bridgeHalf)
      context.fillStyle = colors.value[bottomIndex]?.hex || '#ffffff'
      context.fillRect(left, seamY, bridgeThickness, bridgeHalf)
    }
  })

  // 四颗豆汇合处最容易出现规则的菱形洞；三颗以上相邻时按各自颜色补齐交界象限。
  // 两颗斜对角豆不会被连接，仍能保持图案外轮廓和真正的透明镂空。
  const junctionHalf = beadSize * settings.junctionHalf
  for (let y = 1; y < height.value; y++) {
    for (let x = 1; x < width.value; x++) {
      const topLeft = cells.value[(y - 1) * width.value + x - 1]
      const topRight = cells.value[(y - 1) * width.value + x]
      const bottomLeft = cells.value[y * width.value + x - 1]
      const bottomRight = cells.value[y * width.value + x]
      const junctionColors = [topLeft, topRight, bottomLeft, bottomRight]
      if (junctionColors.filter(colorIndex => colorIndex >= 0).length < settings.junctionMinimum) continue

      const seamX = renderPadding + x * beadSize
      const seamY = renderPadding + y * beadSize
      const quadrants = [
        [topLeft, seamX - junctionHalf, seamY - junctionHalf],
        [topRight, seamX, seamY - junctionHalf],
        [bottomLeft, seamX - junctionHalf, seamY],
        [bottomRight, seamX, seamY],
      ] as const
      quadrants.forEach(([colorIndex, left, top]) => {
        if (colorIndex < 0) return
        context.fillStyle = colors.value[colorIndex]?.hex || '#ffffff'
        context.fillRect(left, top, junctionHalf, junctionHalf)
      })
    }
  }

  if (isFlatMelt) {
    // 圆豆在内部通过熔合桥连片；外边缘只保留低起伏的圆润凸边，避免出现锯齿轮廓。
    const beadRadius = beadSize * 0.5
    const occupied = (x: number, y: number): boolean => x >= 0 && x < width.value && y >= 0 && y < height.value && cells.value[y * width.value + x] >= 0
    const topExposed = (x: number, y: number): boolean => occupied(x, y) && !occupied(x, y - 1)
    const bottomExposed = (x: number, y: number): boolean => occupied(x, y) && !occupied(x, y + 1)
    const leftExposed = (x: number, y: number): boolean => occupied(x, y) && !occupied(x - 1, y)
    const rightExposed = (x: number, y: number): boolean => occupied(x, y) && !occupied(x + 1, y)
    cells.value.forEach((colorIndex, index) => {
      if (colorIndex < 0) return
      const x = index % width.value
      const y = Math.floor(index / width.value)
      const hasLeft = x > 0 && cells.value[index - 1] >= 0
      const hasRight = x + 1 < width.value && cells.value[index + 1] >= 0
      const hasTop = y > 0 && cells.value[index - width.value] >= 0
      const hasBottom = y + 1 < height.value && cells.value[index + width.value] >= 0
      const straightTop = !hasTop && (topExposed(x - 1, y) || topExposed(x + 1, y))
      const straightBottom = !hasBottom && (bottomExposed(x - 1, y) || bottomExposed(x + 1, y))
      const straightLeft = !hasLeft && (leftExposed(x, y - 1) || leftExposed(x, y + 1))
      const straightRight = !hasRight && (rightExposed(x, y - 1) || rightExposed(x, y + 1))
      const left = renderPadding + x * beadSize
      const top = renderPadding + y * beadSize
      const centerX = renderPadding + (x + 0.5) * beadSize
      const centerY = renderPadding + (y + 0.5) * beadSize
      const color = colors.value[colorIndex]?.hex || '#ffffff'
      context.fillStyle = color
      context.beginPath()
      context.arc(centerX, centerY, beadRadius, 0, Math.PI * 2)
      context.fill()
      drawExposedBeadCaps(
        context,
        left,
        top,
        beadSize,
        color,
        hasLeft, hasRight, hasTop, hasBottom,
        straightLeft, straightRight, straightTop, straightBottom,
      )
    })

    // 斜阶梯的凹交界由三颗豆围成；补入小圆形熔合点，消除圆角之间残留的方形缺口。
    // 仅处理恰好三颗豆的网格交点，不影响连续横边、竖边和内部四色交界。
    const diagonalJoinRadius = beadSize * 0.23
    for (let y = 1; y < height.value; y++) {
      for (let x = 1; x < width.value; x++) {
        const topLeft = cells.value[(y - 1) * width.value + x - 1]
        const topRight = cells.value[(y - 1) * width.value + x]
        const bottomLeft = cells.value[y * width.value + x - 1]
        const bottomRight = cells.value[y * width.value + x]
        const quadrants = [topLeft, topRight, bottomRight, bottomLeft]
        if (quadrants.filter(colorIndex => colorIndex >= 0).length !== 3) continue

        const missingIndex = quadrants.findIndex(colorIndex => colorIndex < 0)
        const oppositeIndex = quadrants[(missingIndex + 2) % 4]
        context.fillStyle = colors.value[oppositeIndex]?.hex || '#ffffff'
        context.beginPath()
        context.arc(renderPadding + x * beadSize, renderPadding + y * beadSize, diagonalJoinRadius, 0, Math.PI * 2)
        context.fill()
      }
    }
  } else {
    const sprites = new Map<number, HTMLCanvasElement>()
    cells.value.forEach((colorIndex, index) => {
      if (colorIndex < 0) return
      let sprite = sprites.get(colorIndex)
      if (!sprite) {
        sprite = createBeadSprite(colors.value[colorIndex]?.hex || '#ffffff', settings)
        sprites.set(colorIndex, sprite)
      }
      const x = index % width.value
      const y = Math.floor(index / width.value)
      const overlap = beadSize * settings.overlap
      context.drawImage(
        sprite,
        renderPadding + x * beadSize - overlap,
        renderPadding + y * beadSize - overlap,
        beadSize + overlap * 2,
        beadSize + overlap * 2,
      )
    })
  }
  applyPlasticMaterial(context, canvas)
  applySurfaceTexture(context, canvas, surfaceStyle.value)
  ready.value = true
  if (!hasFittedPattern) {
    hasFittedPattern = true
    fitPattern()
  }
}

function scheduleEffectRender(): void {
  releaseSavePreview()
  if (renderFrame) window.cancelAnimationFrame(renderFrame)
  renderFrame = window.requestAnimationFrame(() => {
    renderFrame = 0
    renderEffect()
  })
}

function releaseSavePreview(): void {
  if (!savePreviewUrl.value) return
  URL.revokeObjectURL(savePreviewUrl.value)
  savePreviewUrl.value = ''
}

async function saveEffectImage(): Promise<void> {
  const source = effectCanvas.value
  if (!source || !ready.value || saving.value) return
  saving.value = true
  try {
    const margin = Math.max(48, Math.floor(Math.max(source.width, source.height) * 0.045))
    const output = document.createElement('canvas')
    output.width = source.width + margin * 2
    output.height = source.height + margin * 2
    const context = output.getContext('2d')
    if (!context) throw new Error('无法创建效果图')
    // Canvas 默认背景透明，只绘制豆子层即可保留 PNG 的 Alpha 通道。
    context.drawImage(source, margin, margin)
    const blob = await new Promise<Blob>((resolve, reject) => {
      output.toBlob(result => result ? resolve(result) : reject(new Error('效果图生成失败')), 'image/png')
    })

    if (/MicroMessenger/i.test(navigator.userAgent)) {
      // 微信会拦截异步任务完成后的 window.open，改为在当前全屏层展示图片供用户长按保存。
      releaseSavePreview()
      savePreviewUrl.value = URL.createObjectURL(blob)
      emit('notify', '效果图已生成，请长按图片保存到相册')
    } else {
      downloadBlob(blob, `${safeFileName(title.value)}-${effectDescription.value}-透明.png`)
      emit('notify', '效果图已下载到浏览器默认位置')
    }
  } catch (reason) {
    emit('notify', reason instanceof Error ? reason.message : '效果图保存失败，请重试')
  } finally {
    saving.value = false
  }
}

watch(surfaceStyle, scheduleEffectRender)

onMounted(async () => {
  await nextTick()
  scheduleEffectRender()
  window.addEventListener('resize', fitPattern)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', fitPattern)
  if (renderFrame) window.cancelAnimationFrame(renderFrame)
  releaseSavePreview()
})
</script>

<template>
  <div
    ref="viewport"
    class="ironed-preview"
    :class="{ dragging: pointers.size > 0 }"
    @pointerdown="pointerDown"
    @pointermove="pointerMove"
    @pointerup="pointerUp"
    @pointercancel="pointerUp"
    @wheel="wheelZoom"
  >
    <canvas ref="effectCanvas" class="ironed-effect-canvas" :style="canvasTransform" aria-label="真实拼豆熨烫效果" />
    <div class="ironed-effect-controls" aria-label="熨烫效果设置" @pointerdown.stop>
      <label>
        <span>表面效果</span>
        <select v-model="surfaceStyle" aria-label="选择表面效果">
          <option v-for="option in surfaceOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </label>
      <small>{{ effectDescription }}</small>
    </div>
    <div v-if="savePreviewUrl" class="ironed-save-guide" role="dialog" aria-modal="true" aria-label="保存效果图" @pointerdown.stop>
      <div class="ironed-save-card">
        <strong>效果图已生成</strong>
        <img :src="savePreviewUrl" alt="可长按保存的透明熨烫效果图" />
        <span>长按图片，选择“保存图片”即可保存到手机相册。</span>
        <small>图片为透明背景 PNG，保存逻辑与导出位置保持一致。</small>
        <button type="button" @click="releaseSavePreview"><AppIcon name="confirm" />我知道了</button>
      </div>
    </div>
    <div class="ironed-preview-actions" @pointerdown.stop>
      <button type="button" :disabled="!ready || saving || props.downloadDisabled" @click="saveEffectImage"><AppIcon name="save-image" />{{ saving ? '保存中…' : '保存效果图' }}</button>
      <button class="return-button" type="button" @click="emit('close')"><AppIcon name="cancel" />返回</button>
    </div>
  </div>
</template>

<style scoped>
.ironed-preview {
  --preview-safe-edge: max(12px, env(safe-area-inset-top), env(safe-area-inset-right), env(safe-area-inset-bottom), env(safe-area-inset-left));
  position: absolute;
  inset: 0;
  overflow: hidden;
  touch-action: none;
  cursor: grab;
  user-select: none;
  background:
    radial-gradient(circle at 48% 42%, rgba(255, 255, 255, .1), transparent 36%),
    linear-gradient(145deg, #303631, #171a18);
}
.ironed-preview.dragging { cursor: grabbing; }
.ironed-effect-canvas {
  position: absolute;
  top: 0;
  left: 0;
  display: block;
  transform-origin: 0 0;
  filter: drop-shadow(0 18px 26px rgba(0, 0, 0, .34));
  will-change: transform;
}
.ironed-effect-controls {
  position: absolute;
  bottom: var(--preview-safe-edge);
  left: var(--preview-safe-edge);
  z-index: 3;
  display: flex;
  max-width: calc(100% - var(--preview-safe-edge) - var(--preview-safe-edge));
  align-items: flex-end;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid rgba(255, 255, 255, .2);
  border-radius: 13px;
  background: rgba(20, 28, 23, .78);
  box-shadow: 0 8px 24px rgba(0, 0, 0, .24);
  color: #fff;
  cursor: default;
  backdrop-filter: blur(10px);
  touch-action: manipulation;
}
.ironed-effect-controls label { display: flex; min-width: 112px; flex-direction: column; gap: 4px; }
.ironed-effect-controls label > span { color: rgba(255,255,255,.7); font-size: 8px; font-weight: 700; }
.ironed-effect-controls select { min-height: 32px; border: 1px solid rgba(255,255,255,.16); border-radius: 8px; padding: 5px 28px 5px 8px; outline: 0; background: #fffdf8; color: #30453b; font-size: 10px; font-weight: 700; touch-action: manipulation; }
.ironed-effect-controls select:focus-visible { box-shadow: 0 0 0 3px rgba(255,107,87,.34); }
.ironed-effect-controls small { max-width: 110px; padding-bottom: 7px; overflow: hidden; color: rgba(255,255,255,.64); font-size: 8px; text-overflow: ellipsis; white-space: nowrap; }
.ironed-save-guide {
  position: absolute;
  inset: 0;
  z-index: 4;
  display: grid;
  place-items: center;
  padding: max(18px, var(--preview-safe-edge));
  background: rgba(8, 15, 11, .72);
  cursor: default;
  touch-action: auto;
}
.ironed-save-card {
  display: flex;
  width: min(480px, 100%);
  max-height: calc(100% - 20px);
  flex-direction: column;
  align-items: stretch;
  gap: 8px;
  overflow: auto;
  padding: 16px;
  border: 1px solid rgba(255, 255, 255, .18);
  border-radius: 16px;
  background: #fffdf8;
  color: #30453b;
  box-shadow: 0 24px 70px rgba(0, 0, 0, .38);
  text-align: center;
  user-select: text;
}
.ironed-save-card strong { font-size: 16px; }
.ironed-save-card img { display: block; width: 100%; min-height: 120px; max-height: min(48vh, 430px); object-fit: contain; border: 1px solid #e3dfd6; border-radius: 11px; background: repeating-conic-gradient(#eee 0 25%, #fff 0 50%) 50% / 16px 16px; touch-action: auto; -webkit-touch-callout: default; }
.ironed-save-card span { font-size: 12px; font-weight: 700; line-height: 1.5; }
.ironed-save-card small { color: #718078; font-size: 9px; line-height: 1.5; }
.ironed-save-card button { align-self: center; border: 0; border-radius: 99px; padding: 9px 20px; background: #14543d; color: #fff; cursor: pointer; font-size: 11px; font-weight: 800; }
.ironed-preview-actions {
  position: absolute;
  top: var(--preview-safe-edge);
  right: var(--preview-safe-edge);
  z-index: 2;
  display: flex;
  gap: 8px;
}
.ironed-preview-actions button {
  border: 1px solid rgba(255, 255, 255, .2);
  border-radius: 99px;
  padding: 9px 15px;
  background: #14543d;
  color: #fff;
  box-shadow: 0 8px 24px rgba(0, 0, 0, .24);
  cursor: pointer;
  font-size: 11px;
  font-weight: 800;
}
.ironed-preview-actions .return-button { background: rgba(255, 255, 255, .94); color: #9b3f32; }
.ironed-preview-actions button:disabled { cursor: wait; opacity: .55; }

@media (max-width: 620px) {
  .ironed-effect-controls { gap: 5px; padding: 6px 8px; }
  .ironed-effect-controls label { min-width: 98px; }
  .ironed-effect-controls small { display: none; }
}
</style>
