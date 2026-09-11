/**
 * 文件：sound.ts
 * 用途：轻量音效开关与播放——放豆、取出豆子、打开色号选择器的提示音；开关状态持久化到 localStorage。
 * 核心职责：Audio 懒加载；开关关闭时不播放；浏览器自动播放限制或加载失败时静默降级，不影响编辑功能。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-08-25
 */

import { defineStore } from 'pinia'
import { ref } from 'vue'

const SOUND_ENABLED_KEY = 'pindou-sound-enabled'

export type SoundType = 'down' | 'pick' | 'search'

export const useSoundStore = defineStore('sound', () => {
  const enabled = ref(true)
  try {
    enabled.value = localStorage.getItem(SOUND_ENABLED_KEY) !== 'off'
  } catch {
    // 存储不可用时默认开启。
  }

  // 懒加载的 Audio 实例：首次播放对应音效时才创建。
  const audios: Record<SoundType, HTMLAudioElement | null> = { down: null, pick: null, search: null }

  function play(type: SoundType): void {
    if (!enabled.value) return
    try {
      let audio = audios[type]
      if (!audio) {
        audio = new Audio(`sounds/${type}.wav`)
        audios[type] = audio
      }
      audio.currentTime = 0
      void audio.play().catch(() => { /* 浏览器自动播放限制等：静默忽略 */ })
    } catch {
      // 音效加载失败不影响正常编辑。
    }
  }

  function toggle(): void {
    enabled.value = !enabled.value
    try {
      localStorage.setItem(SOUND_ENABLED_KEY, enabled.value ? 'on' : 'off')
    } catch {
      // 忽略存储失败。
    }
  }

  return { enabled, play, toggle }
})
