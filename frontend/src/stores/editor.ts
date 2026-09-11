/**
 * 文件：editor.ts
 * 用途：集中管理图纸参数、色卡、单元格、历史记录和本地草稿。
 * 核心职责：隔离网络量化与画板编辑状态，并使用 shallowRef 控制大图纸的响应式开销。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-09-10
 */

import { computed, ref, shallowRef, triggerRef, watch } from 'vue'
import type { MergeBlock } from '../types'
import { defineStore } from 'pinia'
import { getCatalogOverview, getPalette, quantizeImage } from '../api'
import type { BeadColor, BoardPreset, BrandSummary, CollabSnapshotDto, PaletteDetail, PatternExportPayload, PortableProject, QuantizeResponse } from '../types'

export type GenerationSettings = {
  brandId: string
  paletteId: string
  boardId: string
  width: number
  height: number
  maxColors: number
  dither: boolean
  removeBackground: boolean
  backgroundThreshold: number
  noiseSuppression: number
}

// 清洗色板：色卡条目缺少 hex/code/rgb 时补中性占位，避免画布渲染崩溃（正常数据不会触发）。
function sanitizeColors(list: BeadColor[] | undefined): BeadColor[] {
  if (!Array.isArray(list) || list.length === 0) return list || []
  return list.map(color => {
    if (!color || typeof color !== 'object') {
      return { id: '', brand: '', code: '?', name: '未知色', hex: '#9aa3a0', rgb: [154, 163, 160], lab: [0, 0, 0], source: '', license: '' }
    }
    return {
      ...color,
      hex: typeof color.hex === 'string' && color.hex ? color.hex : '#9aa3a0',
      code: typeof color.code === 'string' && color.code ? color.code : '?',
      name: typeof color.name === 'string' && color.name ? color.name : '未知色',
      rgb: Array.isArray(color.rgb) && color.rgb.length >= 3 ? color.rgb : [154, 163, 160],
    }
  })
}

export const useEditorStore = defineStore('editor', () => {
  // 目录数据
  const brands = ref<BrandSummary[]>([])
  const boards = ref<BoardPreset[]>([])
  const palette = ref<PaletteDetail | null>(null)
  const selectedBrandId = ref('mard')
  const selectedPaletteId = ref('mard-221')
  const selectedBoardId = ref('mini-52')

  // 图纸参数与视图偏好
  const title = ref('我的拼豆图纸')
  const width = ref(32)
  const height = ref(32)
  const maxColors = ref(20)
  const dither = ref(false)
  const removeBackground = ref(false)
  const backgroundThreshold = ref(10)
  const noiseSuppression = ref(2)
  const beadShape = ref<'circle' | 'square'>('square')
  const showCodes = ref(true)
  const showGrid = ref(true)
  const showBoardSplit = ref(true)
  // 色号和坐标作为图纸定位的核心辅助信息，每次进入工作台默认开启。
  const showCoordinates = ref(true)
  const cellSize = ref(20)
  // 工具状态不写入草稿；每次重新访问都从安全的拖拽模式开始，避免误触修改豆子。
  const interactionMode = ref<'paint' | 'pan' | 'pick'>('pan')

  // cells 可能包含 25,600 个元素，shallowRef 可避免逐项代理的额外开销。
  const cells = shallowRef<number[]>([])
  // 画布内容版本用于自动保存等旁路逻辑；监听一个整数可避免每次落豆深度遍历整张图纸。
  const contentRevision = ref(0)
  watch(cells, () => { contentRevision.value++ }, { flush: 'sync' })
  const colors = shallowRef<BeadColor[]>([])
  // 每个原始色卡索引的用量；色号弹窗据此把已使用色号排到最前，清空画布后随 rebuildUsageStats 重置。
  const colorCounts = shallowRef<number[]>([])
  const selectedColorIndex = ref(0)
  // 橡皮使用 -1 表示透明格；单独记住最近的正常颜色，切回画笔时不再误回到浅色 A01。
  const lastPaintColorIndex = ref(0)
  // 历史快照是大数组，使用 shallowRef 避免 Vue 为每个格子创建深层代理。
  const history = shallowRef<number[][]>([])
  const future = shallowRef<number[][]>([])
  const beadCount = ref(0)
  const usedColorCount = ref(0)
  const loading = ref(false)
  const processingInfo = ref('')
  const error = ref('')
  // 密钥期限到（time_expired）时由 App.vue 置为 true，锁定绘制/替换等编辑操作，仅保留清空。
  const editingLocked = ref(false)
  // 同色连通区域合并识别：区域外接矩形内全部同色则合并为一块，凹形区域逐格保留。
  // 仅在生成图纸后用于提示可合并的大块，纯统计、不影响图纸数据与其它业务。
  function computeMergeBlocks(grid: number[], w: number, h: number): MergeBlock[] {
    const visited = new Uint8Array(grid.length)
    const blocks: MergeBlock[] = []
    const queue: number[] = []
    for (let i = 0; i < grid.length; i++) {
      if (visited[i] || grid[i] < 0) continue
      const colorIndex = grid[i]
      queue.length = 0
      queue.push(i)
      visited[i] = 1
      let minX = i % w, maxX = minX, minY = (i / w) | 0, maxY = minY
      let area = 0
      while (queue.length) {
        const idx = queue.pop() as number
        const x = idx % w, y = (idx / w) | 0
        area++
        if (x < minX) minX = x; else if (x > maxX) maxX = x
        if (y < minY) minY = y; else if (y > maxY) maxY = y
        if (x > 0 && !visited[idx - 1] && grid[idx - 1] === colorIndex) { visited[idx - 1] = 1; queue.push(idx - 1) }
        if (x < w - 1 && !visited[idx + 1] && grid[idx + 1] === colorIndex) { visited[idx + 1] = 1; queue.push(idx + 1) }
        if (y > 0 && !visited[idx - w] && grid[idx - w] === colorIndex) { visited[idx - w] = 1; queue.push(idx - w) }
        if (y < h - 1 && !visited[idx + w] && grid[idx + w] === colorIndex) { visited[idx + w] = 1; queue.push(idx + w) }
      }
      const bw = maxX - minX + 1
      const bh = maxY - minY + 1
      if (bw * bh === area) {
        blocks.push({ x: minX, y: minY, w: bw, h: bh, colorIndex })
      } else {
        // 凹形区域无法整块合并：逐格保留，避免画面被破坏。
        for (let y2 = minY; y2 <= maxY; y2++) {
          for (let x2 = minX; x2 <= maxX; x2++) {
            const idx2 = y2 * w + x2
            if (grid[idx2] === colorIndex && visited[idx2]) {
              blocks.push({ x: x2, y: y2, w: 1, h: 1, colorIndex })
            }
          }
        }
      }
    }
    return blocks
  }

  const mergeBlocks = computed(() => hasPattern.value
    ? computeMergeBlocks(cells.value, width.value, height.value)
    : [])

  const mergeStats = computed(() => {
    const blocks = mergeBlocks.value
    const cellCount = blocks.reduce((sum, b) => sum + b.w * b.h, 0)
    return { cells: cellCount, blocks: blocks.length, saved: cellCount - blocks.length }
  })

  const selectedBrand = computed(() => brands.value.find(item => item.id === selectedBrandId.value) || null)
  const selectedBoard = computed(() => boards.value.find(item => item.id === selectedBoardId.value) || boards.value[0] || null)
  const hasPattern = computed(() => cells.value.length === width.value * height.value)
  const boardCount = computed(() => {
    const board = selectedBoard.value
    if (!board) return 0
    return Math.ceil(width.value / board.columns) * Math.ceil(height.value / board.rows)
  })
  const physicalWidth = computed(() => width.value * (selectedBoard.value?.beadSize || 2.6) / 10)
  const physicalHeight = computed(() => height.value * (selectedBoard.value?.beadSize || 2.6) / 10)
  const sizeMismatch = computed(() => {
    const board = selectedBoard.value
    const supported = selectedBrand.value?.palettes.find(p => p.id === selectedPaletteId.value)?.beadSizes || []
    return board ? !supported.includes(board.beadSize) : false
  })

  watch(selectedColorIndex, colorIndex => {
    if (colorIndex >= 0 && colorIndex < colors.value.length) lastPaintColorIndex.value = colorIndex
  })

  function selectPaintTool(): void {
    if (editingLocked.value) return
    interactionMode.value = 'paint'
    if (selectedColorIndex.value >= 0) return
    const lastAvailableIndex = Math.max(0, colors.value.length - 1)
    selectedColorIndex.value = Math.min(lastAvailableIndex, Math.max(0, lastPaintColorIndex.value))
  }

  function selectEraserTool(): void {
    if (editingLocked.value) return
    interactionMode.value = 'paint'
    selectedColorIndex.value = -1
  }

  function rebuildUsageStats(): void {
    const counts = new Array(colors.value.length).fill(0)
    let total = 0
    cells.value.forEach(colorIndex => {
      if (colorIndex < 0) return
      counts[colorIndex] = (counts[colorIndex] || 0) + 1
      total++
    })
    colorCounts.value = counts
    beadCount.value = total
    usedColorCount.value = counts.reduce((sum, count) => sum + (count > 0 ? 1 : 0), 0)
  }

  async function initialize(): Promise<void> {
    error.value = ''
    try {
      const overview = await getCatalogOverview()
      brands.value = overview.brands
      boards.value = overview.boards
      // 有本地草稿时按草稿记录的厂商与色卡加载，保证非默认厂商（如 COCO、漫漫家）的图纸刷新后也能恢复。
      let draftBrand = 'mard'
      let draftPalette = 'mard-221'
      try {
        const draft = JSON.parse(localStorage.getItem('pindou-studio-project') ?? '')
        if (draft && typeof draft === 'object' && draft.brandId && draft.paletteId) {
          draftBrand = draft.brandId
          draftPalette = draft.paletteId
        }
      } catch { /* 草稿损坏时回退默认色卡 */ }
      await loadPalette(draftBrand, draftPalette, false)
      // 首次使用直接进入空白画布；只有已保存的本地草稿才恢复具体图案。
      if (!restoreLocal()) resizeBlank(width.value, height.value)
    } catch (reason) {
      error.value = reason instanceof Error ? reason.message : '初始化失败。'
    }
  }

  async function loadPalette(brandId: string, paletteId: string, clear = true): Promise<void> {
    loading.value = true
    error.value = ''
    try {
      const detail = await getPalette(brandId, paletteId)
      selectedBrandId.value = brandId
      selectedPaletteId.value = paletteId
      palette.value = detail
      colors.value = sanitizeColors(detail.colors)
      maxColors.value = Math.min(maxColors.value, detail.colors.length)
      selectedColorIndex.value = 0
      if (clear) clearPattern()
    } catch (reason) {
      error.value = reason instanceof Error ? reason.message : '色卡加载失败。'
      throw reason
    } finally {
      loading.value = false
    }
  }

  /**
   * 原子生成图纸：可接收弹窗草稿参数，先完成色卡与量化请求，
   * 只在两者均成功后一次性替换编辑器状态。任一请求失败都保留原画布、色卡与历史。
   */
  async function generate(file: File, settings?: GenerationSettings): Promise<QuantizeResponse> {
    loading.value = true
    error.value = ''
    try {
      const target: GenerationSettings = settings ? { ...settings } : {
        brandId: selectedBrandId.value,
        paletteId: selectedPaletteId.value,
        boardId: selectedBoardId.value,
        width: width.value,
        height: height.value,
        maxColors: maxColors.value,
        dither: dither.value,
        removeBackground: removeBackground.value,
        backgroundThreshold: backgroundThreshold.value,
        noiseSuppression: noiseSuppression.value,
      }
      const currentPalette = palette.value
      const detailPromise = currentPalette?.brandId === target.brandId && currentPalette.paletteId === target.paletteId
        ? Promise.resolve(currentPalette)
        : getPalette(target.brandId, target.paletteId)
      const [detail, result] = await Promise.all([
        detailPromise,
        quantizeImage(file, {
          paletteId: target.paletteId,
          width: target.width,
          height: target.height,
          maxColors: target.maxColors,
          dither: target.dither,
          removeBackground: target.removeBackground,
          backgroundThreshold: target.backgroundThreshold,
          noiseSuppression: target.noiseSuppression,
        }),
      ])
      if (result.paletteId !== target.paletteId || result.brandId !== target.brandId) {
        throw new Error('生成结果与所选色卡不匹配，请重试。')
      }
      selectedBrandId.value = target.brandId
      selectedPaletteId.value = target.paletteId
      selectedBoardId.value = target.boardId
      palette.value = detail
      maxColors.value = target.maxColors
      dither.value = target.dither
      removeBackground.value = target.removeBackground
      backgroundThreshold.value = target.backgroundThreshold
      noiseSuppression.value = target.noiseSuppression
      applyResult(result)
      processingInfo.value = `${result.algorithm} · ${result.processingMs}ms · ${result.usedColorCount}色`
      return result
    } catch (reason) {
      error.value = reason instanceof Error ? reason.message : '图纸生成失败。'
      throw reason
    } finally {
      loading.value = false
    }
  }

  /**
   * 原子创建空白画布：先验证并取得目标色卡，再同步提交全部参数与新画布。
   */
  async function createBlank(settings: GenerationSettings): Promise<void> {
    loading.value = true
    error.value = ''
    try {
      const target = { ...settings }
      const currentPalette = palette.value
      const detail = currentPalette?.brandId === target.brandId && currentPalette.paletteId === target.paletteId
        ? currentPalette
        : await getPalette(target.brandId, target.paletteId)
      selectedBrandId.value = target.brandId
      selectedPaletteId.value = target.paletteId
      selectedBoardId.value = target.boardId
      palette.value = detail
      colors.value = sanitizeColors(detail.colors)
      selectedColorIndex.value = 0
      maxColors.value = target.maxColors
      dither.value = target.dither
      removeBackground.value = target.removeBackground
      backgroundThreshold.value = target.backgroundThreshold
      noiseSuppression.value = target.noiseSuppression
      resizeBlank(target.width, target.height)
    } catch (reason) {
      error.value = reason instanceof Error ? reason.message : '空白画布创建失败。'
      throw reason
    } finally {
      loading.value = false
    }
  }

  function applyResult(result: QuantizeResponse): void {
    width.value = result.width
    height.value = result.height
    colors.value = sanitizeColors(result.colors)
    cells.value = result.cells.slice()
    rebuildUsageStats()
    history.value = []
    future.value = []
    selectedColorIndex.value = result.usage[0]?.colorIndex ?? 0
  }

  function beginStroke(): void {
    if (editingLocked.value || !hasPattern.value) return
    // 每一笔只保存一次快照；拖动经过的单元格不会重复写入历史。
    history.value.push(cells.value.slice())
    if (history.value.length > 30) history.value.shift()
    triggerRef(history)
    future.value = []
  }

  function paintCell(index: number, colorIndex = selectedColorIndex.value): void {
    if (editingLocked.value) return
    if (index < 0 || index >= cells.value.length || cells.value[index] === colorIndex) return
    const previousColor = cells.value[index]
    // 豆针只负责向空格放豆，避免后选择的颜色误覆盖已完成区域；改色应使用取出或颜色替换功能。
    if (colorIndex >= 0 && previousColor >= 0) return
    // 用量表与画布一样采用原地更新并显式通知，连续落豆时避免反复复制整张色卡统计数组。
    const counts = colorCounts.value
    if (previousColor >= 0) {
      counts[previousColor] = Math.max(0, (counts[previousColor] || 0) - 1)
      if (counts[previousColor] === 0) usedColorCount.value--
      beadCount.value--
    }
    if (colorIndex >= 0) {
      if (!counts[colorIndex]) usedColorCount.value++
      counts[colorIndex] = (counts[colorIndex] || 0) + 1
      beadCount.value++
    }
    cells.value[index] = colorIndex
    triggerRef(colorCounts)
    triggerRef(cells) // shallowRef 内部数组原地修改后需要手动通知视图。
  }

  /** 把未使用的候选色号（如品牌色卡中的某个色）追加为当前图纸的新色号，返回新索引。 */
  function addColor(color: BeadColor): number {
    colors.value = [...colors.value, color]
    colorCounts.value = [...colorCounts.value, 0]
    return colors.value.length - 1
  }

  /**
   * 联机权威写入：直接把某个格子设为指定色号（含覆盖已有豆子与取出），并同步用量统计。
   * 供远程广播与冲突回滚使用；不写入历史、不触发本地保存，也不参与画笔的「只放空格」限制。
   */
  function applyCellEdit(index: number, colorIndex: number): void {
    if (index < 0 || index >= cells.value.length) return
    const previous = cells.value[index]
    if (previous === colorIndex) return
    const counts = colorCounts.value
    if (previous >= 0) {
      counts[previous] = Math.max(0, (counts[previous] || 0) - 1)
      if (counts[previous] === 0) usedColorCount.value--
      beadCount.value--
    }
    if (colorIndex >= 0) {
      if (!counts[colorIndex]) usedColorCount.value++
      counts[colorIndex] = (counts[colorIndex] || 0) + 1
      beadCount.value++
    }
    cells.value[index] = colorIndex
    triggerRef(colorCounts)
    triggerRef(cells) // shallowRef 内部数组原地修改后需要手动通知视图。
  }

  /**
   * 批量联机权威写入：按顺序应用多条编辑（正常编辑 + 冲突回滚），逐格更新用量统计。
   * 供 SSE 广播与批量提交回滚使用；单次批量比逐条调用更能减少中间态渲染。
   */
  function applyCellEdits(items: Array<{ index: number; colorIndex: number }>): void {
    if (!items || items.length === 0) return
    // 批量权威写入（联机广播/冲突回滚/撤销恢复）：一次性更新 cells 与用量统计，
    // 避免大图批量（如一次撤销几十上百格）逐格触发 triggerRef 造成明显卡顿。
    const cellsArr = cells.value
    const counts = colorCounts.value
    let dirty = false
    for (const item of items) {
      if (!Number.isInteger(item.index) || item.index < 0 || item.index >= cellsArr.length) continue
      const previous = cellsArr[item.index]
      const colorIndex = item.colorIndex
      if (previous === colorIndex) continue
      if (previous >= 0) {
        counts[previous] = Math.max(0, (counts[previous] || 0) - 1)
        if (counts[previous] === 0) usedColorCount.value--
        beadCount.value--
      }
      if (colorIndex >= 0) {
        if (!counts[colorIndex]) usedColorCount.value++
        counts[colorIndex] = (counts[colorIndex] || 0) + 1
        beadCount.value++
      }
      cellsArr[item.index] = colorIndex
      dirty = true
    }
    if (dirty) {
      triggerRef(colorCounts)
      triggerRef(cells)
    }
  }

  /**
   * 联机画布切换：用房主（或备份的本人）快照覆盖当前画布。
   * 不写历史、不触发本地保存；仅用于加入联机与退出后恢复本人数据。
   */
  function applyCollabSnapshot(snapshot: CollabSnapshotDto): void {
    if (!snapshot || typeof snapshot !== 'object') return
    const nextCells = Array.isArray(snapshot.cells) ? snapshot.cells : []
    if (!Number.isInteger(snapshot.width) || !Number.isInteger(snapshot.height)
      || nextCells.length !== snapshot.width * snapshot.height) return
    width.value = snapshot.width
    height.value = snapshot.height
    cells.value = nextCells.slice()
    if (Array.isArray(snapshot.colors) && snapshot.colors.length > 0) {
      colors.value = sanitizeColors(snapshot.colors)
    }
    if (typeof snapshot.title === 'string' && snapshot.title) title.value = snapshot.title
    rebuildUsageStats()
    history.value = []
    future.value = []
    selectedColorIndex.value = nextCells.find(value => value >= 0) ?? 0
    processingInfo.value = ''
    interactionMode.value = 'pan'
  }

  function undo(): void {
    if (editingLocked.value) return
    const previous = history.value.pop()
    if (!previous) return
    future.value.push(cells.value.slice())
    triggerRef(history)
    triggerRef(future)
    cells.value = previous
    rebuildUsageStats()
  }

  function redo(): void {
    if (editingLocked.value) return
    const next = future.value.pop()
    if (!next) return
    history.value.push(cells.value.slice())
    triggerRef(history)
    triggerRef(future)
    cells.value = next
    rebuildUsageStats()
  }

  function replaceColor(from: number, to: number): void {
    if (editingLocked.value || !hasPattern.value || from === to) return
    beginStroke()
    cells.value = cells.value.map(value => value === from ? to : value)
    rebuildUsageStats()
  }

  function clearCanvas(): void {
    if (!hasPattern.value || beadCount.value === 0) return
    beginStroke()
    cells.value = new Array(width.value * height.value).fill(-1)
    rebuildUsageStats()
    // 清空豆板后同步删除本地草稿，避免刷新时旧数据被恢复。
    localStorage.removeItem('pindou-studio-project')
    // 无论从哪个界面入口清空，完成后都回到拖拽模式，避免空画布被继续误画。
    interactionMode.value = 'pan'
    processingInfo.value = '空白豆板'
  }

  function clearPattern(): void {
    cells.value = []
    rebuildUsageStats()
    history.value = []
    future.value = []
    processingInfo.value = ''
  }

  // 拼接豆板：只增不减扩大画布（上限 160×160），原豆子图案保留在左上角，新区域留空；可撤销。
  function expandBoard(newWidth: number, newHeight: number): void {
    if (editingLocked.value) return
    const nextWidth = Math.min(160, Math.max(width.value, newWidth))
    const nextHeight = Math.min(160, Math.max(height.value, newHeight))
    if (nextWidth === width.value && nextHeight === height.value) return
    // 空白画布直接按新尺寸建空白板；否则保留原图案。
    if (!hasPattern.value) {
      resizeBlank(nextWidth, nextHeight)
      return
    }
    beginStroke()
    const next = new Array(nextWidth * nextHeight).fill(-1)
    for (let y = 0; y < height.value; y++) {
      const sourceStart = y * width.value
      for (let x = 0; x < width.value; x++) next[y * nextWidth + x] = cells.value[sourceStart + x]
    }
    cells.value = next
    width.value = nextWidth
    height.value = nextHeight
    rebuildUsageStats()
    processingInfo.value = ''
  }

  function resizeBlank(newWidth: number, newHeight: number): void {
    width.value = Math.min(160, Math.max(8, newWidth))
    height.value = Math.min(160, Math.max(8, newHeight))
    cells.value = new Array(width.value * height.value).fill(-1)
    rebuildUsageStats()
    history.value = []
    future.value = []
    processingInfo.value = '空白豆板'
  }

  function createProjectSnapshot(): PortableProject {
    return {
      version: 1,
      title: title.value,
      brandId: selectedBrandId.value,
      paletteId: selectedPaletteId.value,
      boardId: selectedBoardId.value,
      width: width.value,
      height: height.value,
      maxColors: maxColors.value,
      dither: dither.value,
      removeBackground: removeBackground.value,
      backgroundThreshold: backgroundThreshold.value,
      noiseSuppression: noiseSuppression.value,
      beadShape: beadShape.value,
      showCodes: showCodes.value,
      showGrid: showGrid.value,
      showBoardSplit: showBoardSplit.value,
      showCoordinates: showCoordinates.value,
      cellSize: cellSize.value,
      cells: cells.value.slice(),
      colors: colors.value.map(color => ({ ...color })),
      selectedColorIndex: selectedColorIndex.value,
      lastPaintColorIndex: lastPaintColorIndex.value,
    }
  }

  function saveLocal(): void {
    if (!hasPattern.value) return
    localStorage.setItem('pindou-studio-project', JSON.stringify(createProjectSnapshot()))
  }

  async function restoreSharedProject(project: PortableProject): Promise<void> {
    if (!project || project.version !== 1 || !Number.isInteger(project.width) || !Number.isInteger(project.height))
      throw new Error('接力图纸格式不正确。')
    if (project.width < 8 || project.width > 160 || project.height < 8 || project.height > 160)
      throw new Error('接力图纸尺寸不正确。')
    if (!Array.isArray(project.cells) || project.cells.length !== project.width * project.height)
      throw new Error('接力图纸数据不完整。')
    if (!project.brandId || !project.paletteId) throw new Error('接力图纸缺少色卡信息。')

    await loadPalette(project.brandId, project.paletteId, false)
    // 接力图纸带生成时的实际色板时优先使用，保证 cells 索引与色板对应；否则回退完整色卡校验。
    if (Array.isArray(project.colors) && project.colors.length > 0) {
      colors.value = sanitizeColors(project.colors)
    }
    if (project.cells.some(value => !Number.isInteger(value) || value < -1 || value >= colors.value.length))
      throw new Error('接力图纸包含无效色号。')

    width.value = project.width
    height.value = project.height
    title.value = project.title || '我的拼豆图纸'
    selectedBoardId.value = boards.value.some(board => board.id === project.boardId)
      ? project.boardId
      : selectedBoardId.value
    maxColors.value = Math.min(colors.value.length, Math.max(2, Number(project.maxColors) || 20))
    dither.value = Boolean(project.dither)
    removeBackground.value = Boolean(project.removeBackground)
    backgroundThreshold.value = Math.min(35, Math.max(1, Number(project.backgroundThreshold) || 10))
    noiseSuppression.value = Math.min(3, Math.max(0, Number(project.noiseSuppression) || 0))
    beadShape.value = project.beadShape === 'circle' ? 'circle' : 'square'
    showCodes.value = project.showCodes !== false
    showGrid.value = project.showGrid !== false
    showBoardSplit.value = project.showBoardSplit !== false
    showCoordinates.value = project.showCoordinates !== false
    cellSize.value = Math.min(40, Math.max(2, Number(project.cellSize) || 20))
    cells.value = project.cells.slice()
    rebuildUsageStats()
    history.value = []
    future.value = []
    selectedColorIndex.value = cells.value.find(value => value >= 0) ?? 0
    interactionMode.value = 'pan'
    processingInfo.value = ''
    saveLocal()
  }

  function restoreLocal(): boolean {
    try {
      const raw = localStorage.getItem('pindou-studio-project')
      if (!raw) return false
      const project = JSON.parse(raw)
      if (project.paletteId !== selectedPaletteId.value || !Array.isArray(project.cells)) return false
      width.value = project.width
      height.value = project.height
      title.value = project.title || '我的拼豆图纸'
      selectedBoardId.value = project.boardId || selectedBoardId.value
      if (project.cells.length !== width.value * height.value) return false
      // 用草稿保存的实际色板替代完整色卡，保证 cells 索引与色板一一对应，刷新后不串色。
      if (Array.isArray(project.colors) && project.colors.length > 0) {
        colors.value = sanitizeColors(project.colors)
      } else if ((project.cells as number[]).some((value: number) => value >= colors.value.length)) {
        return false
      }
      cells.value = project.cells
      rebuildUsageStats()
      maxColors.value = Number.isFinite(project.maxColors) ? project.maxColors : maxColors.value
      dither.value = project.dither ?? dither.value
      removeBackground.value = project.removeBackground ?? removeBackground.value
      backgroundThreshold.value = Number.isFinite(project.backgroundThreshold) ? project.backgroundThreshold : backgroundThreshold.value
      noiseSuppression.value = Number.isFinite(project.noiseSuppression)
        ? Math.min(3, Math.max(0, project.noiseSuppression))
        : noiseSuppression.value
      beadShape.value = project.beadShape === 'circle' ? 'circle' : 'square'
      showCodes.value = project.showCodes !== false
      showGrid.value = project.showGrid ?? showGrid.value
      showBoardSplit.value = project.showBoardSplit ?? showBoardSplit.value
      showCoordinates.value = project.showCoordinates !== false
      cellSize.value = Number.isFinite(project.cellSize) ? project.cellSize : cellSize.value
      // 恢复当前选择的豆针色号与最近使用色号；索引必须落在恢复后的色板范围内。
      if (Number.isInteger(project.selectedColorIndex) && project.selectedColorIndex >= -1 && project.selectedColorIndex < colors.value.length) {
        selectedColorIndex.value = project.selectedColorIndex
      } else {
        selectedColorIndex.value = cells.value.find(value => value >= 0) ?? 0
      }
      if (Number.isInteger(project.lastPaintColorIndex) && project.lastPaintColorIndex >= 0 && project.lastPaintColorIndex < colors.value.length) {
        lastPaintColorIndex.value = project.lastPaintColorIndex
      }
      processingInfo.value = ''
      return true
    } catch {
      return false
    }
  }

  // 工程 JSON 导入预检：深度递归校验全部字段与格式，不修改任何状态；返回错误描述或 null。
  function validateProject(raw: unknown): string | null {
    if (!raw || typeof raw !== 'object') return '文件内容不是有效的工程数据。'
    const project = raw as Record<string, unknown>
    if (project.version !== 1) return '不支持的文件版本。'

    const isNumber = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value)
    const isInteger = (value: unknown): value is number => isNumber(value) && Number.isInteger(value)
    const isBoolean = (value: unknown): value is boolean => typeof value === 'boolean'
    const isText = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0

    // 必填标量字段：缺失、类型错误或取值越界一律拒绝。
    const width = project.width
    const height = project.height
    if (!isInteger(width) || !isInteger(height) || width < 8 || width > 160 || height < 8 || height > 160)
      return '豆板尺寸不正确。'
    if (!Array.isArray(project.cells) || project.cells.length !== width * height) return '豆子数据不完整。'
    if (!isText(project.title)) return '缺少图纸名称。'
    if (!isText(project.brandId) || !isText(project.paletteId)) return '缺少色卡信息。'
    if (!isText(project.boardId)) return '缺少底板信息。'
    if (!isInteger(project.maxColors) || project.maxColors < 2) return '缺少有效的颜色数量。'
    if (!isInteger(project.backgroundThreshold) || project.backgroundThreshold < 1 || project.backgroundThreshold > 35)
      return '缺少有效的背景阈值。'
    if (!isInteger(project.noiseSuppression) || project.noiseSuppression < 0 || project.noiseSuppression > 3)
      return '缺少有效的噪点抑制参数。'
    if (project.beadShape !== 'circle' && project.beadShape !== 'square') return '缺少有效的豆子形状。'
    if (!isInteger(project.cellSize) || project.cellSize < 2 || project.cellSize > 40) return '缺少有效的格子大小。'
    if (!isBoolean(project.dither) || !isBoolean(project.removeBackground) || !isBoolean(project.showCodes) ||
        !isBoolean(project.showGrid) || !isBoolean(project.showBoardSplit) || !isBoolean(project.showCoordinates))
      return '缺少必要的视图参数。'

    // 色板深度递归校验：colors 必须存在，每个颜色对象的全部字段与格式都正确。
    if (!Array.isArray(project.colors) || project.colors.length === 0) return '缺少色板数据。'
    for (const item of project.colors) {
      if (!item || typeof item !== 'object') return '色卡数据不完整。'
      const color = item as Record<string, unknown>
      if (!isText(color.id) || !isText(color.brand) || !isText(color.name) ||
          typeof color.source !== 'string' || typeof color.license !== 'string')
        return '色卡数据不完整。'
      if (!isText(color.code)) return '色卡数据不完整。'
      if (typeof color.hex !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(color.hex)) return '色卡数据不完整。'
      if (!Array.isArray(color.rgb) || color.rgb.length < 3 || color.rgb.some((v: unknown) => typeof v !== 'number'))
        return '色卡数据不完整。'
      if (!Array.isArray(color.lab) || color.lab.length < 3 || color.lab.some((v: unknown) => typeof v !== 'number'))
        return '色卡数据不完整。'
    }

    // 可选字段：存在时校验类型与范围。
    if (project.selectedColorIndex !== undefined &&
        (!isInteger(project.selectedColorIndex) || (project.selectedColorIndex as number) < -1))
      return '色号索引不合法。'
    if (project.lastPaintColorIndex !== undefined &&
        (!isInteger(project.lastPaintColorIndex) || (project.lastPaintColorIndex as number) < 0))
      return '色号索引不合法。'

    // cells 色号必须是整数且落在色板范围内。
    const colorCount = project.colors.length
    for (const value of project.cells) {
      if (!isInteger(value) || value < -1) return '包含无效色号。'
      if (value >= colorCount) return '包含无效色号。'
    }
    return null
  }

  // 从「我的图纸」打开一张已保存图纸：先把当前画布压入历史（支持撤销/恢复），再加载目标图纸。
  async function loadSavedProject(project: PortableProject): Promise<void> {
    const problem = validateProject(project)
    if (problem) throw new Error(problem)

    await loadPalette(project.brandId, project.paletteId, false)
    if (Array.isArray(project.colors) && project.colors.length > 0) {
      colors.value = sanitizeColors(project.colors)
    }
    if (project.cells.some(value => !Number.isInteger(value) || value < -1 || value >= colors.value.length))
      throw new Error('图纸包含无效色号。')

    // 支持撤销/恢复：打开前把当前画布压入历史，之后可撤销回原数据。
    if (hasPattern.value) {
      history.value.push(cells.value.slice())
      if (history.value.length > 30) history.value.shift()
      future.value = []
      triggerRef(history)
      triggerRef(future)
    }

    width.value = project.width
    height.value = project.height
    title.value = project.title || '我的拼豆图纸'
    selectedBoardId.value = boards.value.some(board => board.id === project.boardId)
      ? project.boardId
      : selectedBoardId.value
    maxColors.value = Math.min(colors.value.length, Math.max(2, Number(project.maxColors) || 20))
    dither.value = Boolean(project.dither)
    removeBackground.value = Boolean(project.removeBackground)
    backgroundThreshold.value = Math.min(35, Math.max(1, Number(project.backgroundThreshold) || 10))
    noiseSuppression.value = Math.min(3, Math.max(0, Number(project.noiseSuppression) || 0))
    beadShape.value = project.beadShape === 'circle' ? 'circle' : 'square'
    showCodes.value = project.showCodes !== false
    showGrid.value = project.showGrid !== false
    showBoardSplit.value = project.showBoardSplit !== false
    showCoordinates.value = project.showCoordinates !== false
    cellSize.value = Math.min(40, Math.max(2, Number(project.cellSize) || 20))
    cells.value = project.cells.slice()
    rebuildUsageStats()
    selectedColorIndex.value = cells.value.find(value => value >= 0) ?? 0
    interactionMode.value = 'pan'
    processingInfo.value = ''
    saveLocal()
  }

  function exportPayload(): PatternExportPayload {
    const board = selectedBoard.value
    return {
      title: title.value,
      width: width.value,
      height: height.value,
      beadSize: board?.beadSize || 2.6,
      boardColumns: board?.columns || 52,
      boardRows: board?.rows || 52,
      brandName: palette.value?.brandName || selectedBrand.value?.name || '',
      paletteName: palette.value?.paletteName || '',
      colors: colors.value.map(color => ({ code: color.code, name: color.name, hex: color.hex })),
      cells: cells.value.slice(),
    }
  }

  return {
    brands, boards, palette, selectedBrandId, selectedPaletteId, selectedBoardId, title,
    width, height, maxColors, dither, removeBackground, backgroundThreshold, noiseSuppression,
    beadShape, showCodes, showGrid, showBoardSplit, showCoordinates, cellSize, interactionMode,
    cells, contentRevision, colors, selectedColorIndex, lastPaintColorIndex, colorCounts, history, future, loading, processingInfo, error, editingLocked,
    selectedBrand, selectedBoard, hasPattern, usedColorCount, beadCount, boardCount,
    physicalWidth, physicalHeight, sizeMismatch,
    initialize, loadPalette, generate, createBlank, selectPaintTool, selectEraserTool, beginStroke, paintCell, undo, redo, replaceColor,
    clearCanvas, clearPattern, resizeBlank, expandBoard, saveLocal, createProjectSnapshot, restoreSharedProject, loadSavedProject, validateProject, exportPayload,
    applyCellEdit, applyCellEdits, applyCollabSnapshot,
    mergeBlocks, mergeStats, addColor,
  }
})
