<script setup lang="ts">
import { FileSearch, Link2, MoreHorizontal, Rotate3D } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { postDesktopMessage } from '../api'
import type { BomItem, DocumentNode, PreviewMode, SolidWorksOpenMode } from '../types'

const props = withDefaults(defineProps<{ selected: DocumentNode; related: DocumentNode[]; bomItem?: BomItem; currentUsername?: string; canManageLifecycle?: boolean; desktopAvailable?: boolean; obscured?: boolean }>(), { currentUsername: '', canManageLifecycle: false, desktopAvailable: false, obscured: false })
const emit = defineEmits<{ open: [node: DocumentNode, mode: SolidWorksOpenMode, versionId?: string]; preview: [node: DocumentNode]; related: [node: DocumentNode]; more: []; whereUsed: []; obsolete: [] }>()
const previewSlot = ref<HTMLElement>()
const previewState = ref<'idle' | 'loading' | 'ready' | 'error' | 'unavailable'>(props.desktopAvailable ? 'idle' : 'unavailable')
const previewError = ref('')
const solidWorksAvailable = ref(false)
const solidWorksPending = ref(false)
const solidWorksMessage = ref('')
const solidWorksError = ref(false)
let resizeObserver: ResizeObserver | undefined
let overlayObserver: MutationObserver | undefined
let previewSyncFrame = 0
let previewSuspended = false

const mode = computed<PreviewMode>(() => props.selected.kind === 'Drawing' ? 'drawing' : 'model')
const previewKindLabel = computed(() => mode.value === 'drawing' ? '2D工程图' : '3D模型')
const selectedDisplayName = computed(() => {
  const drawingNumber = props.selected.drawingNumber?.trim()
  const name = props.selected.name?.trim()
  if (!drawingNumber) return name || props.selected.fileName
  if (!name || name === drawingNumber) return drawingNumber
  return `${drawingNumber} · ${name}`
})
const lifecycleLabel = computed(() => {
  const value = props.selected.lifecycleState
  if (typeof value === 'number') return ['工作中', '审批中', '已发布', '已作废'][value] ?? String(value)
  return ({ Work: '工作中', InReview: '审批中', Released: '已发布', Obsolete: '已作废' } as Record<string, string>)[value ?? ''] ?? value ?? '工作中'
})
const editStatusLabel = computed(() => {
  const owner = props.selected.checkedOutBy?.trim()
  if (!owner) return '正常'
  return owner.localeCompare(props.currentUsername.trim(), undefined, { sensitivity: 'accent' }) === 0
    ? '可编辑'
    : `${owner}编辑中`
})
const previewProperties = computed(() => [
  { label: '物料/图号', value: props.selected.drawingNumber?.trim() },
  { label: '名称', value: props.selected.name?.trim() },
  { label: '规格/型号', value: props.bomItem?.specification?.trim() },
  { label: '材质', value: props.bomItem?.material?.trim() },
  { label: '品牌', value: props.bomItem?.brand?.trim() },
  { label: '表面处理', value: props.bomItem?.surfaceTreatment?.trim() },
  { label: '版本', value: props.selected.version?.trim() },
  { label: '状态', value: lifecycleLabel.value },
].filter((item): item is { label: string; value: string } => Boolean(item.value)))

function reportPreviewBounds() {
  if (!props.desktopAvailable) return
  const slot = previewSlot.value
  if (!slot) return
  if (props.obscured) {
    suspendPreview()
    return
  }
  const bounds = slot.getBoundingClientRect()
  if (document.visibilityState === 'hidden' || isPreviewObscured(bounds)) {
    suspendPreview()
    return
  }
  const left = Math.max(0, bounds.left)
  const top = Math.max(0, bounds.top)
  const right = Math.min(window.innerWidth, bounds.right)
  const bottom = Math.min(window.innerHeight, bounds.bottom)
  const width = Math.max(0, right - left)
  const height = Math.max(0, bottom - top)
  if (width < 80 || height < 80) {
    suspendPreview()
    return
  }
  previewSuspended = false
  postDesktopMessage('preview-host-bounds', {
    left,
    top,
    width,
    height,
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
    visible: true,
  })
}

function isPreviewObscured(slotBounds: DOMRect) {
  return [...document.querySelectorAll<HTMLElement>('.el-overlay, .el-popper, .el-message-box__wrapper, .pdm-dialog-backdrop')].some(element => {
    const style = window.getComputedStyle(element)
    if (style.display === 'none' || style.visibility === 'hidden') return false
    const bounds = element.getBoundingClientRect()
    return bounds.width > 0 && bounds.height > 0
      && bounds.left < slotBounds.right && bounds.right > slotBounds.left
      && bounds.top < slotBounds.bottom && bounds.bottom > slotBounds.top
  })
}

function suspendPreview() {
  if (previewSuspended) return
  previewSuspended = true
  postDesktopMessage('preview-host-suspend')
}

function schedulePreviewBounds() {
  if (typeof window === 'undefined') return
  if (previewSyncFrame) return
  previewSyncFrame = window.requestAnimationFrame(() => {
    previewSyncFrame = 0
    if (typeof window === 'undefined' || typeof document === 'undefined') return
    reportPreviewBounds()
  })
}

function hidePreview() {
  postDesktopMessage('preview-host-hide')
}

function startPreview() {
  if (!props.selected.documentId) return
  if (!props.desktopAvailable) {
    previewState.value = 'unavailable'
    previewError.value = ''
    return
  }
  previewState.value = 'loading'
  previewError.value = ''
  reportPreviewBounds()
  emit('preview', props.selected)
}

async function restartPreview() {
  hidePreview()
  previewSuspended = false
  previewState.value = 'idle'
  previewError.value = ''
  await nextTick()
  startPreview()
}

function onPreviewStatus(event: Event) {
  const detail = (event as CustomEvent<{ state?: string; message?: string }>).detail
  if (!detail?.state) return
  if (detail.state === 'loading' || detail.state === 'ready' || detail.state === 'error') {
    previewState.value = detail.state
    previewError.value = detail.message ?? ''
    if (detail.state === 'ready') void nextTick(schedulePreviewBounds)
  }
}

function openInSolidWorks(mode: SolidWorksOpenMode) {
  if (!props.selected.documentId || !solidWorksAvailable.value || solidWorksPending.value) return
  solidWorksPending.value = true
  solidWorksError.value = false
  solidWorksMessage.value = '正在准备最新受控文件，不获取编辑权限…'
  emit('open', props.selected, mode)
}

function onSolidWorksCapability(event: Event) {
  solidWorksAvailable.value = Boolean((event as CustomEvent<{ available?: boolean }>).detail?.available)
}

function onSolidWorksStatus(event: Event) {
  const detail = (event as CustomEvent<{ state?: string; message?: string }>).detail
  if (!detail?.state) return
  solidWorksPending.value = detail.state === 'loading'
  solidWorksError.value = detail.state === 'error'
  solidWorksMessage.value = detail.message ?? ''
}

watch(() => props.obscured, obscured => {
  if (obscured) {
    suspendPreview()
    return
  }
  void nextTick(schedulePreviewBounds)
}, { flush: 'post' })

watch(() => props.selected.id, () => {
  solidWorksPending.value = false
  solidWorksMessage.value = ''
  solidWorksError.value = false
  void restartPreview()
})

onMounted(() => {
  window.addEventListener('resize', schedulePreviewBounds)
  window.addEventListener('scroll', schedulePreviewBounds, true)
  document.addEventListener('visibilitychange', schedulePreviewBounds)
  window.addEventListener('pdm-preview-status', onPreviewStatus)
  window.addEventListener('pdm-solidworks-capability', onSolidWorksCapability)
  window.addEventListener('pdm-solidworks-status', onSolidWorksStatus)
  postDesktopMessage('solidworks-capability-request')
  if (typeof ResizeObserver !== 'undefined' && previewSlot.value) {
    resizeObserver = new ResizeObserver(schedulePreviewBounds)
    resizeObserver.observe(previewSlot.value)
  }
  if (window.chrome?.webview && typeof MutationObserver !== 'undefined') {
    overlayObserver = new MutationObserver(schedulePreviewBounds)
    overlayObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'aria-hidden'] })
  }
  void nextTick(startPreview)
})

onBeforeUnmount(() => {
  hidePreview()
  if (previewSyncFrame) window.cancelAnimationFrame(previewSyncFrame)
  resizeObserver?.disconnect()
  overlayObserver?.disconnect()
  window.removeEventListener('resize', schedulePreviewBounds)
  window.removeEventListener('scroll', schedulePreviewBounds, true)
  document.removeEventListener('visibilitychange', schedulePreviewBounds)
  window.removeEventListener('pdm-preview-status', onPreviewStatus)
  window.removeEventListener('pdm-solidworks-capability', onSolidWorksCapability)
  window.removeEventListener('pdm-solidworks-status', onSolidWorksStatus)
})
</script>

<template>
  <section class="pdm-preview-panel" aria-label="图档预览">
    <section class="pdm-panel pdm-preview-control-panel" aria-label="图档查看与操作">
      <header class="pdm-preview-toolbar">
        <div class="pdm-preview-document-switcher">
          <span class="pdm-preview-kind">{{ previewKindLabel }}</span>
          <strong class="pdm-preview-document-name" :title="selectedDisplayName">{{ selectedDisplayName }}</strong>
          <span class="pdm-selected-file" :title="selected.fileName">{{ selected.fileName }}</span>
          <span class="pdm-selected-version" :aria-label="`工作版本 ${selected.version}`">{{ selected.version }}</span>
          <span class="pdm-selected-version" :aria-label="`业务状态 ${lifecycleLabel}`">{{ lifecycleLabel }}</span>
          <span class="pdm-selected-status">{{ editStatusLabel }}</span>
          <div v-if="related.length" class="pdm-related-documents" aria-label="关联图档">
            <span><Link2 :size="13" />关联{{ mode === 'model' ? '图纸' : '模型' }}</span>
            <button v-for="document in related" :key="document.id" type="button" :title="document.name" @click="emit('related', document)">{{ document.drawingNumber }}</button>
          </div>
        </div>
        <div class="pdm-preview-actions">
          <button type="button" aria-label="使用位置" title="查看该图档被哪些装配体引用" :disabled="!selected.documentId" @click="emit('whereUsed')"><Link2 :size="15" /><span>引用</span></button>
          <button v-if="canManageLifecycle && lifecycleLabel !== '已作废'" type="button" aria-label="作废图档" title="受控作废当前图档" :disabled="!selected.documentId" @click="emit('obsolete')"><span>作废</span></button>
          <button type="button" aria-label="更多操作" title="查看更多图档操作" @click="emit('more')"><MoreHorizontal :size="17" /><span>更多</span></button>
        </div>
        <div class="pdm-solidworks-actions">
          <button
            type="button"
            class="pdm-solidworks-primary"
            :disabled="!selected.documentId || !solidWorksAvailable || solidWorksPending"
            :title="solidWorksAvailable ? `从PDM获取${selected.version}并在SolidWorks中打开；需要修改时请在插件设计树中获取权限` : '当前电脑未安装SolidWorks或UPTON PDM插件'"
            @click="openInSolidWorks('LatestReadOnly')"
          ><Rotate3D :size="15" />打开最新</button>
        </div>
      </header>
      <p v-if="solidWorksMessage" class="pdm-solidworks-feedback" :class="{ 'is-error': solidWorksError }" role="status">{{ solidWorksMessage }}</p>
    </section>

    <div class="pdm-panel pdm-preview-content">
      <div
        ref="previewSlot"
        class="pdm-real-preview pdm-embedded-preview-slot"
        :data-preview-state="previewState"
        :aria-label="desktopAvailable ? '客户端内嵌eDrawings预览区' : '网页端图档预览状态'"
      >
        <dl class="pdm-preview-properties" aria-label="图档属性">
          <div v-for="property in previewProperties" :key="property.label">
            <dt>{{ property.label }}</dt>
            <dd :title="property.value">{{ property.value }}</dd>
          </div>
        </dl>
        <template v-if="previewState === 'unavailable'">
          <FileSearch :size="52" />
          <h3>网页端暂不支持原生SolidWorks图档预览</h3>
          <p>{{ selected.fileName }} · {{ selected.version }}</p>
          <small>eDrawings预览依赖Windows客户端中的本地组件；网页端可查看版本信息并下载受控文件。</small>
          <button type="button" class="pdm-primary-action" :disabled="!selected.documentId" @click="emit('more')">查看并下载版本</button>
        </template>
        <template v-else>
          <FileSearch :size="52" />
          <h3>{{ previewState === 'loading' ? '正在加载 eDrawings…' : mode === 'model' ? 'eDrawings 内嵌三维预览' : 'eDrawings 内嵌图纸预览' }}</h3>
          <p>{{ selected.fileName }} · {{ selected.version }}</p>
          <small>文件通过PDM权限校验和SHA-256校验后下载到独立只读缓存，不会覆盖工作文件。</small>
          <p v-if="previewState === 'error'" class="pdm-preview-error" role="alert">{{ previewError || 'eDrawings加载失败，请重试。' }}</p>
          <button v-if="previewState === 'error'" type="button" class="pdm-primary-action" :disabled="!selected.documentId" @click="startPreview">
            重新加载内嵌预览
          </button>
        </template>
      </div>
    </div>
  </section>
</template>
