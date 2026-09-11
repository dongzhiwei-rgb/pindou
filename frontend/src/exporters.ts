/**
 * 文件：exporters.ts
 * 用途：在浏览器端生成 PNG、CSV、JSON、ZIP 等拼豆图纸文件。
 * 核心职责：统一安全文件名、Blob 下载行为、批量导出归档和各格式的可移植数据结构。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-08-21
 */

import type { PatternExportPayload, PortableProject } from './types'

type BrowserExportFile = {
  name: string
  blob: Blob
}

type ZipEntry = {
  nameBytes: Uint8Array
  data: ArrayBuffer
  checksum: number
}

// ZIP 文件头需要 CRC32 校验值。查表法只初始化一次，打包大图时不会重复计算多项式。
const crc32Table = createCrc32Table()

export function safeFileName(value: string): string {
  const result = value.trim().replace(/[\\/:*?"<>|]/g, '-').replace(/\s+/g, '-')
  return result || '拼豆图纸'
}

export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  // Safari 等浏览器需要在点击处理完毕后再释放地址，否则可能得到空文件。
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/**
 * 将多个导出结果合并为一次下载。
 *
 * PNG 和 XLSX 本身已经压缩，再压一次收益很小，因此这里使用 ZIP 的“仅存储”模式：
 * 打包速度快、内存峰值稳定，也能被 Windows、macOS、Android 和 iOS 直接解压。
 */
export async function createZipBlob(files: readonly BrowserExportFile[]): Promise<Blob> {
  if (!files.length) throw new Error('没有可打包的导出文件')
  if (files.length > 0xffff) throw new Error('导出文件数量超出ZIP格式限制')

  const encoder = new TextEncoder()
  const entries: ZipEntry[] = []

  for (const file of files) {
    const data = await file.blob.arrayBuffer()
    if (data.byteLength > 0xffffffff) throw new Error(`文件“${file.name}”过大，无法打包`)
    entries.push({
      nameBytes: encoder.encode(file.name),
      data,
      checksum: calculateCrc32(new Uint8Array(data)),
    })
  }

  const localParts: BlobPart[] = []
  const centralParts: BlobPart[] = []
  const { date, time } = getDosDateTime(new Date())
  let localOffset = 0
  let centralSize = 0

  for (const entry of entries) {
    if (entry.nameBytes.byteLength > 0xffff) throw new Error('导出文件名过长，无法打包')

    const localHeader = new Uint8Array(30 + entry.nameBytes.byteLength)
    const localView = new DataView(localHeader.buffer)
    localView.setUint32(0, 0x04034b50, true)
    localView.setUint16(4, 20, true)
    localView.setUint16(6, 0x0800, true) // 文件名使用 UTF-8，保证中文在各系统中正常显示。
    localView.setUint16(8, 0, true) // 0 表示仅存储，不执行二次压缩。
    localView.setUint16(10, time, true)
    localView.setUint16(12, date, true)
    localView.setUint32(14, entry.checksum, true)
    localView.setUint32(18, entry.data.byteLength, true)
    localView.setUint32(22, entry.data.byteLength, true)
    localView.setUint16(26, entry.nameBytes.byteLength, true)
    localView.setUint16(28, 0, true)
    localHeader.set(entry.nameBytes, 30)

    const centralHeader = new Uint8Array(46 + entry.nameBytes.byteLength)
    const centralView = new DataView(centralHeader.buffer)
    centralView.setUint32(0, 0x02014b50, true)
    centralView.setUint16(4, 20, true)
    centralView.setUint16(6, 20, true)
    centralView.setUint16(8, 0x0800, true)
    centralView.setUint16(10, 0, true)
    centralView.setUint16(12, time, true)
    centralView.setUint16(14, date, true)
    centralView.setUint32(16, entry.checksum, true)
    centralView.setUint32(20, entry.data.byteLength, true)
    centralView.setUint32(24, entry.data.byteLength, true)
    centralView.setUint16(28, entry.nameBytes.byteLength, true)
    centralView.setUint16(30, 0, true)
    centralView.setUint16(32, 0, true)
    centralView.setUint16(34, 0, true)
    centralView.setUint16(36, 0, true)
    centralView.setUint32(38, 0, true)
    centralView.setUint32(42, localOffset, true)
    centralHeader.set(entry.nameBytes, 46)

    localParts.push(localHeader.buffer, entry.data)
    centralParts.push(centralHeader.buffer)
    localOffset += localHeader.byteLength + entry.data.byteLength
    centralSize += centralHeader.byteLength
  }

  if (localOffset + centralSize > 0xffffffff) throw new Error('导出文件总大小超出ZIP格式限制')

  const footer = new Uint8Array(22)
  const footerView = new DataView(footer.buffer)
  footerView.setUint32(0, 0x06054b50, true)
  footerView.setUint16(4, 0, true)
  footerView.setUint16(6, 0, true)
  footerView.setUint16(8, entries.length, true)
  footerView.setUint16(10, entries.length, true)
  footerView.setUint32(12, centralSize, true)
  footerView.setUint32(16, localOffset, true)
  footerView.setUint16(20, 0, true)

  return new Blob([...localParts, ...centralParts, footer.buffer], { type: 'application/zip' })
}

// 工程 JSON：统一序列化可移植工程数据（PortableProject），与导入结构完全一致，来回可无损恢复。
export function createJsonBlob(project: PortableProject): Blob {
  return new Blob([JSON.stringify(project, null, 2)], { type: 'application/json' })
}

export function createCsvBlob(payload: PatternExportPayload): Blob {
  const counts = new Map<number, number>()
  payload.cells.forEach(index => {
    if (index >= 0) counts.set(index, (counts.get(index) || 0) + 1)
  })
  const rows = [['品牌', '色卡', '色号', '颜色名称', 'HEX', '数量', '1000颗/包']]
  ;[...counts.entries()]
    .sort((a, b) => b[1] - a[1])
    .forEach(([index, count]) => {
      const color = payload.colors[index]
      rows.push([payload.brandName, payload.paletteName, color.code, color.name, color.hex, String(count), String(Math.ceil(count / 1000))])
  })
  const csv = '\ufeff' + rows.map(row => row.map(value => `"${value.replace(/"/g, '""')}"`).join(',')).join('\r\n')
  return new Blob([csv], { type: 'text/csv;charset=utf-8' })
}

export function createPngBlob(payload: PatternExportPayload): Promise<Blob> {
  const maxSide = Math.max(payload.width, payload.height)
  const cellSize = Math.max(18, Math.min(32, Math.floor(5200 / maxSide)))
  const margin = 46
  const canvas = document.createElement('canvas')
  canvas.width = payload.width * cellSize + margin * 2
  canvas.height = payload.height * cellSize + margin * 2
  const context = canvas.getContext('2d')
  if (!context) return Promise.reject(new Error('无法创建PNG画布'))
  context.fillStyle = '#fffdf8'
  context.fillRect(0, 0, canvas.width, canvas.height)
  context.textAlign = 'center'
  context.textBaseline = 'middle'
  const coordinateFontSize = Math.max(7, Math.min(11, Math.floor(cellSize * 0.38)))
  context.font = `600 ${coordinateFontSize}px system-ui, sans-serif`
  context.fillStyle = '#52645c'

  // 导出图用于照图摆豆，四边都标出每一个格子的完整坐标，旋转图纸或分段制作时无需反推缺失编号。
  for (let x = 0; x < payload.width; x++) {
    const position = margin + (x + 0.5) * cellSize
    context.fillText(String(x + 1), position, margin / 2)
    context.fillText(String(x + 1), position, canvas.height - margin / 2)
  }
  for (let y = 0; y < payload.height; y++) {
    const position = margin + (y + 0.5) * cellSize
    context.fillText(String(y + 1), margin / 2, position)
    context.fillText(String(y + 1), canvas.width - margin / 2, position)
  }

  for (let y = 0; y < payload.height; y++) {
    for (let x = 0; x < payload.width; x++) {
      const index = payload.cells[y * payload.width + x]
      const left = margin + x * cellSize
      const top = margin + y * cellSize
      if (index >= 0) {
        const color = payload.colors[index]
        context.fillStyle = color.hex
        context.fillRect(left, top, cellSize, cellSize)
        if (cellSize >= 20) {
          context.fillStyle = contrastColor(color.hex)
          context.font = `600 ${Math.max(7, Math.floor(cellSize * 0.3))}px system-ui, sans-serif`
          context.fillText(color.code, left + cellSize / 2, top + cellSize / 2)
        }
      }
      context.strokeStyle = '#d9d5cb'
      context.lineWidth = 0.6
      context.strokeRect(left, top, cellSize, cellSize)
    }
  }

  context.strokeStyle = '#ff6b57'
  context.lineWidth = 2.4
  for (let x = payload.boardColumns; x < payload.width; x += payload.boardColumns) {
    context.beginPath()
    context.moveTo(margin + x * cellSize, margin)
    context.lineTo(margin + x * cellSize, margin + payload.height * cellSize)
    context.stroke()
  }
  for (let y = payload.boardRows; y < payload.height; y += payload.boardRows) {
    context.beginPath()
    context.moveTo(margin, margin + y * cellSize)
    context.lineTo(margin + payload.width * cellSize, margin + y * cellSize)
    context.stroke()
  }

  return new Promise((resolve, reject) => {
    canvas.toBlob(blob => {
      if (blob) resolve(blob)
      else reject(new Error('PNG图纸生成失败'))
    }, 'image/png')
  })
}

function contrastColor(hex: string): string {
  const value = hex.replace('#', '')
  const r = Number.parseInt(value.slice(0, 2), 16)
  const g = Number.parseInt(value.slice(2, 4), 16)
  const b = Number.parseInt(value.slice(4, 6), 16)
  return 0.2126 * r + 0.7152 * g + 0.0722 * b < 125 ? '#ffffff' : '#25332d'
}

function createCrc32Table(): Uint32Array {
  const table = new Uint32Array(256)
  for (let index = 0; index < table.length; index++) {
    let value = index
    for (let bit = 0; bit < 8; bit++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1
    table[index] = value >>> 0
  }
  return table
}

function calculateCrc32(data: Uint8Array): number {
  let checksum = 0xffffffff
  for (let index = 0; index < data.length; index++) {
    checksum = crc32Table[(checksum ^ data[index]) & 0xff] ^ (checksum >>> 8)
  }
  return (checksum ^ 0xffffffff) >>> 0
}

function getDosDateTime(value: Date): { date: number; time: number } {
  const year = Math.min(2107, Math.max(1980, value.getFullYear()))
  return {
    date: ((year - 1980) << 9) | ((value.getMonth() + 1) << 5) | value.getDate(),
    time: (value.getHours() << 11) | (value.getMinutes() << 5) | Math.floor(value.getSeconds() / 2),
  }
}
