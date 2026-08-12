<script setup lang="ts">
import { Boxes, FileSearch, Maximize, MoreHorizontal } from '@lucide/vue'
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { postDesktopMessage } from '../api'
import type { BomItem, DocumentNode, PreviewMode } from '../types'

const props = defineProps<{ selected: DocumentNode; bom: BomItem[] }>()
const mode = defineModel<PreviewMode>('mode', { required: true })
const emit = defineEmits<{ open: [node: DocumentNode]; preview: [node: DocumentNode]; more: [] }>()
const previewSlot = ref<HTMLElement>()
const previewState = ref<'idle' | 'loading' | 'ready' | 'error'>('idle')
const previewError = ref('')
let resizeObserver: ResizeObserver | undefined

const tabs: Array<{ value: PreviewMode; label: string }> = [
  { value: 'model', label: '3D预览' },
  { value: 'drawing', label: '2D图纸' },
  { value: 'bom', label: '机械BOM' },
]

function reportPreviewBounds() {
  const slot = previewSlot.value
  if (!slot || (mode.value !== 'model' && mode.value !== 'drawing')) return
  const bounds = slot.getBoundingClientRect()
  if (bounds.width < 1 || bounds.height < 1) return
  postDesktopMessage('preview-host-bounds', {
    left: bounds.left,
    top: bounds.top,
    width: bounds.width,
    height: bounds.height,
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
    visible: true,
  })
}

function hidePreview() {
  postDesktopMessage('preview-host-hide')
}

function startPreview() {
  previewState.value = 'loading'
  previewError.value = ''
  reportPreviewBounds()
  emit('preview', props.selected)
}

function fitPreview() {
  if (previewState.value === 'ready') postDesktopMessage('preview-host-fit')
  else startPreview()
}

function onPreviewStatus(event: Event) {
  const detail = (event as CustomEvent<{ state?: string; message?: string }>).detail
  if (!detail?.state) return
  if (detail.state === 'loading' || detail.state === 'ready' || detail.state === 'error') {
    previewState.value = detail.state
    previewError.value = detail.message ?? ''
    if (detail.state === 'ready') void nextTick(reportPreviewBounds)
  }
}

watch(mode, async () => {
  hidePreview()
  previewState.value = 'idle'
  previewError.value = ''
  await nextTick()
  reportPreviewBounds()
})

watch(() => props.selected.id, () => {
  hidePreview()
  previewState.value = 'idle'
  previewError.value = ''
})

onMounted(() => {
  window.addEventListener('resize', reportPreviewBounds)
  window.addEventListener('pdm-preview-status', onPreviewStatus)
  if (typeof ResizeObserver !== 'undefined' && previewSlot.value) {
    resizeObserver = new ResizeObserver(reportPreviewBounds)
    resizeObserver.observe(previewSlot.value)
  }
  void nextTick(reportPreviewBounds)
})

onBeforeUnmount(() => {
  hidePreview()
  resizeObserver?.disconnect()
  window.removeEventListener('resize', reportPreviewBounds)
  window.removeEventListener('pdm-preview-status', onPreviewStatus)
})
</script>

<template>
  <section class="pdm-panel pdm-preview-panel" aria-label="图档预览">
    <header class="pdm-preview-toolbar">
      <div class="pdm-view-tabs" role="tablist" aria-label="资料视图">
        <button
          v-for="tab in tabs"
          :key="tab.value"
          type="button"
          role="tab"
          :aria-selected="mode === tab.value"
          @click="mode = tab.value"
        >{{ tab.label }}</button>
      </div>
      <div class="pdm-preview-actions">
        <button type="button" aria-label="适合窗口" @click="fitPreview"><Maximize :size="15" /></button>
        <button type="button" aria-label="更多操作" @click="emit('more')"><MoreHorizontal :size="17" /></button>
      </div>
    </header>

    <div class="pdm-selected-bar">
      <span class="pdm-selected-bar__title"><Boxes :size="16" /><strong>{{ selected.drawingNumber }} {{ selected.name }}</strong></span>
      <span>{{ selected.fileName }}</span>
      <span>工作版本 <b>{{ selected.version }}</b></span>
      <button type="button" class="pdm-selected-status" @click="emit('open', selected)">{{ selected.checkedOutBy ? `正在编辑 · ${selected.checkedOutBy}` : '打开图档' }}</button>
    </div>

    <div class="pdm-preview-content">
      <div
        v-if="mode === 'model' || mode === 'drawing'"
        ref="previewSlot"
        class="pdm-real-preview pdm-embedded-preview-slot"
        :data-preview-state="previewState"
        aria-label="客户端内嵌eDrawings预览区"
      >
        <FileSearch :size="52" />
        <h3>{{ previewState === 'loading' ? '正在加载 eDrawings…' : mode === 'model' ? 'eDrawings 内嵌三维预览' : 'eDrawings 内嵌图纸预览' }}</h3>
        <p>{{ selected.fileName }} · {{ selected.version }}</p>
        <small>文件通过PDM权限校验和SHA-256校验后下载到独立只读缓存，不会覆盖工作文件。</small>
        <p v-if="previewState === 'error'" class="pdm-preview-error" role="alert">{{ previewError || 'eDrawings加载失败，请重试。' }}</p>
        <button type="button" class="pdm-primary-action" :disabled="previewState === 'loading'" @click="startPreview">
          {{ previewState === 'loading' ? '正在加载…' : previewState === 'error' ? '重新加载内嵌预览' : '在客户端内预览' }}
        </button>
      </div>

      <div v-else class="pdm-bom-view">
        <el-table :data="bom" size="small" height="310" stripe>
          <el-table-column prop="sequence" label="序号" width="60" />
          <el-table-column prop="drawingNumber" label="图号" min-width="110" />
          <el-table-column prop="name" label="名称" min-width="110" />
          <el-table-column prop="quantity" label="数量" width="70" />
          <el-table-column prop="material" label="材料" min-width="80" />
          <el-table-column prop="revision" label="版本" width="66" />
        </el-table>
      </div>
    </div>
  </section>
</template>
