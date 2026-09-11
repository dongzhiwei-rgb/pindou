<!--
  文件：SplashScreen.vue
  用途：展示符合“拼了个豆”主题的轻量开屏动画。
  核心职责：用 CSS 将散落豆子拼成品牌心形，在不阻塞工作台初始化的前提下完成入场、熨烫高光和退场。
  版权：@董志伟-联系方式-makabak1204
  最后修改：2026-09-11
-->

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import AppIcon from './AppIcon.vue'

type BeadCell = {
  column: number
  row: number
  color: 'coral' | 'green' | 'mint'
  offsetX: number
  offsetY: number
  delay: number
}

const emit = defineEmits<{ leaving: []; finished: [] }>()
const SPLASH_DURATION_MS = 1500
const EXIT_DURATION_MS = 300
const HIGHLIGHT_DURATION_MS = 450
const POST_HIGHLIGHT_HOLD_MS = 350
const HIGHLIGHT_DELAY_MS = SPLASH_DURATION_MS - HIGHLIGHT_DURATION_MS - POST_HIGHLIGHT_HOLD_MS
const leaving = ref(false)
const millisecondsRemaining = ref(SPLASH_DURATION_MS)
// 1.5 秒总时长仍以“2、1”显示整数倒计时；内部保留毫秒精度，确保动画和退场严格同步。
const secondsRemaining = computed(() => Math.ceil(millisecondsRemaining.value / 1000))
const splashStyle = {
  '--splash-duration': `${SPLASH_DURATION_MS}ms`,
  '--highlight-duration': `${HIGHLIGHT_DURATION_MS}ms`,
  '--highlight-delay': `${HIGHLIGHT_DELAY_MS}ms`,
  '--splash-exit-duration': `${EXIT_DURATION_MS}ms`,
}

// 5×5 心形只使用 16 个节点，既能体现逐颗拼合，也避免为短暂动画创建过多 DOM。
const beads: BeadCell[] = [
  { column: 2, row: 1, color: 'coral', offsetX: -118, offsetY: -74, delay: 0 },
  { column: 4, row: 1, color: 'green', offsetX: 112, offsetY: -82, delay: 70 },
  { column: 1, row: 2, color: 'green', offsetX: -146, offsetY: -18, delay: 110 },
  { column: 2, row: 2, color: 'coral', offsetX: -72, offsetY: -126, delay: 30 },
  { column: 3, row: 2, color: 'coral', offsetX: 18, offsetY: -142, delay: 150 },
  { column: 4, row: 2, color: 'coral', offsetX: 82, offsetY: -118, delay: 50 },
  { column: 5, row: 2, color: 'green', offsetX: 152, offsetY: -12, delay: 130 },
  { column: 1, row: 3, color: 'coral', offsetX: -158, offsetY: 52, delay: 170 },
  { column: 2, row: 3, color: 'coral', offsetX: -104, offsetY: 116, delay: 90 },
  { column: 3, row: 3, color: 'mint', offsetX: 0, offsetY: 152, delay: 210 },
  { column: 4, row: 3, color: 'coral', offsetX: 96, offsetY: 122, delay: 190 },
  { column: 5, row: 3, color: 'coral', offsetX: 164, offsetY: 48, delay: 100 },
  { column: 2, row: 4, color: 'green', offsetX: -124, offsetY: 86, delay: 230 },
  { column: 3, row: 4, color: 'coral', offsetX: 12, offsetY: 166, delay: 140 },
  { column: 4, row: 4, color: 'green', offsetX: 132, offsetY: 92, delay: 250 },
  { column: 3, row: 5, color: 'coral', offsetX: 4, offsetY: 184, delay: 270 },
]

let finishTimer = 0
let removeTimer = 0
let countdownTimer = 0

function finish(): void {
  if (leaving.value) return
  leaving.value = true
  emit('leaving')
  window.clearTimeout(finishTimer)
  window.clearInterval(countdownTimer)
  millisecondsRemaining.value = 0
  removeTimer = window.setTimeout(() => emit('finished'), EXIT_DURATION_MS)
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') finish()
}

onMounted(() => {
  // 倒计时和自动退场共用同一总时长，并按实际经过时间计算，避免定时器误差导致动画提前结束。
  const startedAt = performance.now()
  countdownTimer = window.setInterval(() => {
    millisecondsRemaining.value = Math.max(0, SPLASH_DURATION_MS - (performance.now() - startedAt))
  }, 100)
  // 退场属于 1.5 秒总时间轴的一部分：高光结束后稍作停留，再与工作台交叉淡入。
  finishTimer = window.setTimeout(finish, SPLASH_DURATION_MS - EXIT_DURATION_MS)
  window.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  window.clearTimeout(finishTimer)
  window.clearTimeout(removeTimer)
  window.clearInterval(countdownTimer)
  window.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <section
    class="splash-screen"
    :class="{ 'is-leaving': leaving }"
    :style="splashStyle"
    role="status"
    aria-live="polite"
    aria-label="拼了个豆正在启动"
  >
    <div class="splash-grid" aria-hidden="true"></div>
    <button
      class="splash-skip"
      type="button"
      :aria-label="`跳过开屏动画，剩余 ${secondsRemaining} 秒`"
      @click="finish"
    >
      <span>跳过</span>
      <output class="splash-countdown" aria-hidden="true">{{ secondsRemaining }}s</output>
      <AppIcon name="skip" />
    </button>

    <div class="splash-content">
      <div class="bead-scene" aria-hidden="true">
        <div class="bead-heart">
          <i
            v-for="(bead, index) in beads"
            :key="index"
            class="splash-bead"
            :class="`is-${bead.color}`"
            :style="{
              '--column': bead.column,
              '--row': bead.row,
              '--offset-x': `${bead.offsetX}px`,
              '--offset-y': `${bead.offsetY}px`,
              '--delay': `${bead.delay}ms`,
            }"
          ></i>
          <span class="ironing-sweep"></span>
        </div>
      </div>

      <div class="splash-copy">
        <h1>拼了个豆</h1>
        <p>把灵感，一颗颗拼出来</p>
        <small>PINLEGE DOU · 拼豆图纸工作台</small>
      </div>
    </div>
  </section>
</template>

<style scoped>
.splash-screen {
  position: fixed;
  inset: 0;
  z-index: 2147483000;
  display: grid;
  place-items: center;
  overflow: hidden;
  background:
    radial-gradient(circle at 50% 42%, rgba(220, 238, 229, .9) 0, rgba(255, 250, 240, .95) 34%, #f3f0e8 72%);
  color: #14543d;
  isolation: isolate;
  contain: strict;
  will-change: opacity;
  transition:
    opacity var(--splash-exit-duration) cubic-bezier(.22, .75, .25, 1),
    visibility 0s linear var(--splash-exit-duration);
}

.splash-screen.is-leaving {
  visibility: hidden;
  opacity: 0;
  pointer-events: none;
}

.splash-content,
.splash-grid,
.splash-skip {
  transition:
    opacity var(--splash-exit-duration) cubic-bezier(.22, .75, .25, 1),
    transform var(--splash-exit-duration) cubic-bezier(.22, .75, .25, 1);
}

.splash-screen.is-leaving .splash-content {
  opacity: .62;
  transform: translateY(-5px) scale(.992);
}

.splash-screen.is-leaving .splash-grid { opacity: 0; transform: scale(1.015); }
.splash-screen.is-leaving .splash-skip { opacity: 0; transform: translateY(-5px); }

.splash-grid {
  position: absolute;
  inset: -28px;
  z-index: -1;
  background-image: radial-gradient(circle, rgba(20, 84, 61, .1) 1.4px, transparent 1.5px);
  background-size: 25px 25px;
  mask-image: radial-gradient(circle at center, #000 0, transparent 70%);
  animation: grid-breathe .9s ease-out both;
}

.splash-skip {
  position: absolute;
  top: max(18px, env(safe-area-inset-top));
  right: max(20px, env(safe-area-inset-right));
  display: flex;
  align-items: center;
  gap: 8px;
  border: 1px solid rgba(20, 84, 61, .16);
  border-radius: 999px;
  padding: 7px 13px;
  background: rgba(255, 253, 248, .66);
  color: rgba(20, 84, 61, .68);
  font-size: 11px;
  backdrop-filter: blur(8px);
  cursor: pointer;
  transition: background .18s ease, color .18s ease;
}

.splash-skip .app-icon {
  width: 15px;
  height: 15px;
}

.splash-countdown {
  min-width: 22px;
  border-left: 1px solid rgba(20, 84, 61, .16);
  padding-left: 8px;
  color: #14543d;
  font-weight: 800;
  text-align: center;
  font-variant-numeric: tabular-nums;
}

.splash-skip:hover,
.splash-skip:focus-visible {
  background: #fffdf8;
  color: #14543d;
}

.splash-content {
  display: grid;
  place-items: center;
  gap: 26px;
  padding: 28px;
  text-align: center;
}

.bead-scene {
  position: relative;
  width: 190px;
  height: 176px;
  filter: drop-shadow(0 18px 20px rgba(20, 84, 61, .15));
  animation: heart-settle .28s cubic-bezier(.2, .75, .22, 1) .48s both;
}

.bead-heart {
  position: absolute;
  inset: 0;
  transform-origin: center;
  animation: heart-breathe .34s ease-in-out .82s 1 both;
}

.splash-bead {
  --bead-size: 28px;
  position: absolute;
  top: calc((var(--row) - 1) * 32px + 7px);
  left: calc((var(--column) - 1) * 32px + 17px);
  width: var(--bead-size);
  height: var(--bead-size);
  border-radius: 50%;
  background: #ff6b57;
  box-shadow:
    inset 4px 4px 7px rgba(255, 255, 255, .34),
    inset -3px -4px 6px rgba(126, 42, 31, .15),
    0 4px 6px rgba(58, 50, 42, .12);
  opacity: 0;
  transform: translate(var(--offset-x), var(--offset-y)) scale(.2) rotate(70deg);
  animation: bead-arrive .36s cubic-bezier(.2, .9, .28, 1.18) calc(20ms + var(--delay)) forwards;
}

.splash-bead::after {
  content: '';
  position: absolute;
  inset: 9px;
  border-radius: 50%;
  background: rgba(255, 250, 240, .88);
  box-shadow: inset 0 1px 2px rgba(22, 49, 37, .22);
}

.splash-bead.is-green { background: #14543d; }
.splash-bead.is-mint { background: #79b99a; }

.ironing-sweep {
  position: absolute;
  top: -24px;
  left: -50px;
  width: 46px;
  height: 220px;
  border-radius: 50%;
  background: linear-gradient(90deg, transparent, rgba(255, 255, 255, .78), transparent);
  filter: blur(3px);
  opacity: 0;
  transform: translateX(-60px) rotate(14deg);
  animation: ironing-sweep var(--highlight-duration) ease-in-out var(--highlight-delay) both;
}

.splash-copy h1 {
  margin: 0;
  font-size: clamp(32px, 7vw, 46px);
  line-height: 1;
  letter-spacing: .18em;
  text-indent: .18em;
  opacity: 0;
  transform: translateY(12px);
  animation: copy-arrive .32s ease-out .42s forwards;
}

.splash-copy p {
  margin: 13px 0 0;
  color: #455f53;
  font-size: clamp(13px, 3vw, 16px);
  letter-spacing: .22em;
  text-indent: .22em;
  opacity: 0;
  animation: copy-arrive .3s ease-out .57s forwards;
}

.splash-copy small {
  display: block;
  margin-top: 13px;
  color: #7c8b83;
  font-size: 9px;
  font-weight: 700;
  letter-spacing: .18em;
  opacity: 0;
  animation: copy-arrive .28s ease-out .7s forwards;
}

@keyframes bead-arrive {
  0% { opacity: 0; transform: translate(var(--offset-x), var(--offset-y)) scale(.2) rotate(70deg); }
  70% { opacity: 1; transform: translate(0, 0) scale(1.1) rotate(-5deg); }
  100% { opacity: 1; transform: translate(0, 0) scale(1) rotate(0); }
}

@keyframes heart-settle {
  0% { transform: scale(1); }
  55% { transform: scale(1.035, .95); }
  100% { transform: scale(1); }
}

@keyframes ironing-sweep {
  0% { opacity: 0; transform: translateX(-60px) rotate(14deg); }
  20% { opacity: .88; }
  80% { opacity: .7; }
  100% { opacity: 0; transform: translateX(255px) rotate(14deg); }
}

@keyframes copy-arrive {
  to { opacity: 1; transform: translateY(0); }
}

@keyframes grid-breathe {
  from { opacity: 0; transform: scale(1.08); }
  to { opacity: 1; transform: scale(1); }
}

@keyframes heart-breathe {
  0%, 100% { transform: scale(1); }
  50% { transform: scale(.975); }
}

@keyframes bead-gentle-arrive {
  0% { opacity: 0; transform: scale(.72); }
  70% { opacity: 1; transform: scale(1.06); }
  100% { opacity: 1; transform: scale(1); }
}

@media (max-height: 520px) and (orientation: landscape) {
  .splash-content { grid-template-columns: 190px auto; gap: 22px; text-align: left; }
  .splash-copy h1,
  .splash-copy p { text-indent: 0; }
  .bead-scene { transform: scale(.82); }
}

@media (max-width: 420px) {
  .splash-content { gap: 18px; }
  .bead-scene { transform: scale(.88); }
  .splash-copy p { letter-spacing: .15em; text-indent: .15em; }
}

@media (prefers-reduced-motion: reduce) {
  /* 降级模式取消位移和缩放，只保留淡入淡出；局部 important 用于覆盖全站的瞬时降级规则。 */
  .splash-screen,
  .splash-content,
  .splash-grid,
  .splash-skip {
    transition-duration: var(--splash-exit-duration) !important;
    transition-property: opacity !important;
  }

  .splash-screen.is-leaving .splash-content,
  .splash-screen.is-leaving .splash-grid,
  .splash-screen.is-leaving .splash-skip { transform: none; }

  .splash-grid { transform: none; animation: reduced-fade-in .45s ease-out both !important; }
  .bead-scene,
  .bead-heart { animation: none !important; }
  .splash-bead {
    opacity: 0;
    transform: none;
    animation: reduced-fade-in .18s ease-out calc(20ms + var(--delay)) forwards !important;
  }
  .ironing-sweep { display: none; }
  .splash-copy h1 { transform: none; animation: reduced-fade-in .25s ease-out .36s forwards !important; }
  .splash-copy p { animation: reduced-fade-in .24s ease-out .5s forwards !important; }
  .splash-copy small { animation: reduced-fade-in .22s ease-out .62s forwards !important; }
}

@keyframes reduced-fade-in {
  from { opacity: 0; }
  to { opacity: 1; }
}
</style>
