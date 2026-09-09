<script setup lang="ts">
import { computed, onMounted, onUnmounted } from 'vue'
import { globalStatus, registerStatusHost } from '../statusMessage'

let unregister: (() => void) | undefined
onMounted(() => { unregister = registerStatusHost() })
onUnmounted(() => unregister?.())
const labels = { success: '成功', warning: '提醒', error: '失败', info: '提示' }
const label = computed(() => globalStatus.value ? labels[globalStatus.value.type] : '')
const message = computed(() => globalStatus.value?.message || '')
const idleLetters = [
  { name: 'U', path: 'M 10 8 V 38 C 10 52 20 58 32 58 C 44 58 54 52 54 38 V 8', stroke: 'url(#pdm-status-u)', spin: false },
  { name: 'P', path: 'M 12 58 V 8 H 34 C 48 8 54 16 54 27 C 54 38 46 44 34 44 H 12', stroke: 'url(#pdm-status-p)', spin: false },
  { name: 'T', path: 'M 8 10 H 56 M 32 10 V 58', stroke: 'url(#pdm-status-t)', spin: false },
  { name: 'O', path: 'M 32 32 m 0 -27 a 27 27 0 1 1 0 54 a 27 27 0 1 1 0 -54', stroke: 'url(#pdm-status-o)', spin: true },
  { name: 'N', path: 'M 10 58 V 8 L 54 58 V 8', stroke: 'url(#pdm-status-n)', spin: false },
] as const
</script>

<template>
  <div v-if="!globalStatus" class="pdm-global-status is-idle" role="img" aria-label="暂无操作状态">
    <span class="pdm-global-status__loader loader" aria-hidden="true">
      <svg width="0" height="0" viewBox="0 0 64 64" class="absolute">
        <defs>
          <linearGradient id="pdm-status-u" x1="0" y1="62" x2="0" y2="2" gradientUnits="userSpaceOnUse">
            <stop stop-color="#00e0ed" />
            <stop offset="1" stop-color="#00da72" />
          </linearGradient>
          <linearGradient id="pdm-status-p" x1="0" y1="62" x2="0" y2="2" gradientUnits="userSpaceOnUse">
            <stop stop-color="#973bed" />
            <stop offset="1" stop-color="#007cff" />
          </linearGradient>
          <linearGradient id="pdm-status-t" x1="0" y1="64" x2="0" y2="0" gradientUnits="userSpaceOnUse">
            <stop stop-color="#ffc800" />
            <stop offset="1" stop-color="#ff00ff" />
          </linearGradient>
          <linearGradient id="pdm-status-o" x1="0" y1="64" x2="0" y2="0" gradientUnits="userSpaceOnUse">
            <stop stop-color="#973bed" />
            <stop offset="1" stop-color="#007cff" />
            <animateTransform attributeName="gradientTransform" type="rotate" dur="16s" repeatCount="indefinite" values="0 32 32;-270 32 32;-270 32 32;-540 32 32;-540 32 32;-810 32 32;-810 32 32;-1080 32 32;-1080 32 32" keyTimes="0;0.125;0.25;0.375;0.5;0.625;0.75;0.875;1" />
          </linearGradient>
          <linearGradient id="pdm-status-n" x1="0" y1="62" x2="0" y2="2" gradientUnits="userSpaceOnUse">
            <stop stop-color="#00e0ed" />
            <stop offset="1" stop-color="#00da72" />
          </linearGradient>
        </defs>
      </svg>
      <svg v-for="letter in idleLetters" :key="letter.name" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 64 64" class="inline-block pdm-global-status__letter" :data-letter="letter.name">
        <path :d="letter.path" :stroke="letter.stroke" stroke-linejoin="round" stroke-linecap="round" stroke-width="8" pathLength="360" :class="letter.spin ? 'spin' : 'dash'" />
      </svg>
    </span>
  </div>
  <el-popover v-else placement="bottom-start" trigger="click" :width="440" popper-class="pdm-global-status-detail">
    <template #reference>
      <button type="button" class="pdm-global-status" :class="`is-${globalStatus?.type || 'idle'}`" :title="message" :aria-label="`${label}：${message}，点击查看完整状态`">
        <span class="pdm-global-status__label">{{ label }}</span>
        <span class="pdm-global-status__text" role="status" aria-live="polite" aria-atomic="true">{{ message }}</span>
      </button>
    </template>
    <strong>{{ label }}</strong>
    <p>{{ message }}</p>
  </el-popover>
</template>

<style scoped>
.pdm-global-status.is-idle { cursor: default; }
/* From Uiverse.io by SelfMadeSystem; adapted from YOU to UPTON and scaled for the header. */
.absolute { position: absolute; }
.inline-block { display: inline-block; }
.loader { display: flex; margin: 0.25em 0; }
.w-2 { width: 0.5em; }
.pdm-global-status__loader { width: 128px; height: 24px; flex: 0 0 128px; align-items: center; justify-content: center; gap: 2px; }
.pdm-global-status__letter { width: 22px; height: 22px; flex: 0 0 22px; overflow: visible; }
.dash { animation: dashArray 4s ease-in-out infinite, dashOffset 4s linear infinite; }
.spin { animation: spinDashArray 4s ease-in-out infinite, spin 16s ease-in-out infinite, dashOffset 4s linear infinite; transform-origin: center; }
@keyframes dashArray { 0% { stroke-dasharray: 0 1 359 0; } 50% { stroke-dasharray: 0 359 1 0; } 100% { stroke-dasharray: 359 1 0 0; } }
@keyframes spinDashArray { 0% { stroke-dasharray: 270 90; } 50% { stroke-dasharray: 0 360; } 100% { stroke-dasharray: 270 90; } }
@keyframes dashOffset { 0% { stroke-dashoffset: 365; } 100% { stroke-dashoffset: 5; } }
@keyframes spin { 0% { rotate: 0deg; } 12.5%, 25% { rotate: 270deg; } 37.5%, 50% { rotate: 540deg; } 62.5%, 75% { rotate: 810deg; } 87.5%, 100% { rotate: 1080deg; } }
@media (prefers-reduced-motion: reduce) { .dash, .spin { animation: none; } }
</style>
