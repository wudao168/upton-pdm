<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import {
  fallbackGlobalStatusContent,
  loadGlobalStatusContent,
  selectGlobalStatusContent,
  type GlobalStatusContentLibrary,
  type ResolvedGlobalStatusContent,
} from '../globalStatusContent'
import { globalStatus, registerStatusHost } from '../statusMessage'

let unregister: (() => void) | undefined
let rotationTimer: ReturnType<typeof setInterval> | undefined
let contentLibrary: GlobalStatusContentLibrary | undefined
const idleContent = ref<ResolvedGlobalStatusContent>(fallbackGlobalStatusContent)

const labels = { success: '成功', warning: '提醒', error: '失败', info: '提示' }
const label = computed(() => globalStatus.value ? labels[globalStatus.value.type] : '')
const message = computed(() => globalStatus.value?.message || '')
const idleTitle = computed(() => `${idleContent.value.item.text} — ${idleContent.value.attribution}`)

function refreshIdleContent() {
  if (contentLibrary) idleContent.value = selectGlobalStatusContent(contentLibrary, new Date())
}

onMounted(() => {
  unregister = registerStatusHost()
  loadGlobalStatusContent().then(library => {
    contentLibrary = library
    refreshIdleContent()
  }).catch(() => {
    idleContent.value = fallbackGlobalStatusContent
  })
  rotationTimer = setInterval(refreshIdleContent, 10 * 60_000)
})

onUnmounted(() => {
  unregister?.()
  if (rotationTimer) clearInterval(rotationTimer)
})
</script>

<template>
  <el-popover v-if="!globalStatus" placement="bottom-start" trigger="click" :width="440" popper-class="pdm-global-status-detail">
    <template #reference>
      <button type="button" class="pdm-global-status is-idle" :title="idleTitle" :aria-label="idleTitle">
        <span class="pdm-global-status__text">{{ idleContent.item.text }}</span>
        <span class="pdm-global-status__source">{{ idleContent.attribution }}</span>
      </button>
    </template>
    <strong>{{ idleContent.item.text }}</strong>
    <p>{{ idleContent.attribution }}</p>
    <p>
      来源：
      <a v-if="idleContent.sourceUrl" :href="idleContent.sourceUrl" target="_blank" rel="noreferrer">{{ idleContent.sourceName }}</a>
      <span v-else>{{ idleContent.sourceName }}</span>
      <span> · {{ idleContent.license }}</span>
    </p>
  </el-popover>
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
.pdm-global-status { line-height: 20px; }
.pdm-global-status.is-idle { cursor: pointer; }
.pdm-global-status.is-idle .pdm-global-status__text {
  min-width: 0;
  flex: 1 1 auto;
  line-height: 20px;
}
.pdm-global-status__source {
  max-width: 42%;
  flex: 0 1 auto;
  overflow: hidden;
  color: #0f8f83;
  font-size: 12px;
  font-weight: 400;
  line-height: 20px;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pdm-global-status-detail a { color: #0f8f83; }
</style>
