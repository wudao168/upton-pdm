<script setup lang="ts">
import { useProjectCenterAccess } from '../projectCenterAccess'
const { write: projectWritable, download: projectDownload, print: projectPrint } = useProjectCenterAccess()
import { documentVersionSummary, documentDrawingVersion, documentPublishedVersion } from '../drawingRevision'
import { findPreviewComponent } from '../previewComponent'
import { Cloud, FileSearch, Maximize2, Minimize2, PencilLine, Rotate3D, RotateCcw, Square } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { listDocumentVersions, postDesktopMessage, readDocumentPreviewFile } from '../api'
import type { BomItem, DocumentNode, DocumentVersionSummary, PreviewMode, SolidWorksOpenMode } from '../types'
import SquareLoader from './SquareLoader.vue'

const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const StepPreviewViewer = defineAsyncComponent(() => import('./StepPreviewViewer.vue'))

const props = withDefaults(defineProps<{
  selected: DocumentNode
  emptyProject?: boolean
  related: DocumentNode[]
  bomItem?: BomItem
  readableDocumentIds?: ReadonlySet<string>
  bomItemForNode?: (node: DocumentNode) => BomItem | undefined
  currentUsername?: string
  canManageLifecycle?: boolean
  canEditDocuments?: boolean
  desktopAvailable?: boolean
  obscured?: boolean
  reviewPanelCollapsed?: boolean
  reviewVersionId?: string
  reviewRevision?: string
  reviewEditable?: boolean
  canWritebackReviewProperties?: boolean
  accessToken?: string
  projectId?: string
  active?: boolean
}>(), {
  currentUsername: '',
  emptyProject: false,
  canManageLifecycle: false,
  canEditDocuments: false,
  desktopAvailable: false,
  obscured: false,
  reviewPanelCollapsed: true,
  reviewVersionId: '',
  reviewRevision: '',
  reviewEditable: false,
  canWritebackReviewProperties: false,
  accessToken: '',
  projectId: '',
  active: true,
})
const emit = defineEmits<{
  open: [node: DocumentNode, mode: SolidWorksOpenMode, versionId?: string]
  preview: [node: DocumentNode, versionId?: string, revision?: string]
  related: [node: DocumentNode]
  more: []
  whereUsed: []
  obsolete: []
}>()
const previewSlot = ref<HTMLElement>()
const previewMaximized = ref(false)
let maximizeButton: HTMLButtonElement | undefined
async function setPreviewMaximized(value: boolean) {
  previewMaximized.value = value
  await nextTick()
  schedulePreviewBounds()
  maximizeButton?.focus({ preventScroll: true })
}
function togglePreviewMaximized(event: MouseEvent) {
  maximizeButton = event.currentTarget as HTMLButtonElement
  void setPreviewMaximized(!previewMaximized.value)
}
function onPreviewKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || !previewMaximized.value) return
  event.preventDefault()
  void setPreviewMaximized(false)
}
const previewState = ref<'idle' | 'loading' | 'ready' | 'error' | 'unavailable'>('idle')
const previewError = ref('')
const webPreviewFormat = ref<'Step' | 'Pdf'>()
const webPreviewUrl = ref('')
const webStepBuffer = ref<ArrayBuffer>()
const webPreviewNotice = ref('')
const previewSessionActivated = ref(false)
const lightweightPreviewState = ref<'loading' | 'ready' | 'unavailable'>('loading')
const lightweightPreviewUrl = ref('')
const lightweightPreviewMessage = ref('')
const solidWorksAvailable = ref(false)
const solidWorksPending = ref(false)
const solidWorksMessage = ref('')
const solidWorksError = ref(false)
const markupSaving = ref(false)
const markupDirty = ref(false)
const lastArchivedAt = ref('')
const versionMetadata = ref<DocumentVersionSummary[]>([])
const componentSelection = ref<{ name: string; configuration: string }>()
const componentNode = computed(() => {
  const selection = componentSelection.value
  if (!selection) return undefined
  const node = findPreviewComponent(props.selected, selection.name, selection.configuration)
  return node?.documentId && props.readableDocumentIds?.has(node.documentId) ? node : undefined
})
const propertyNode = computed(() => componentSelection.value ? componentNode.value : props.selected)
const propertyBomItem = computed(() => componentSelection.value
  ? componentNode.value && props.bomItemForNode?.(componentNode.value)
  : props.bomItem)
const activeVersionMetadata = computed(() => componentSelection.value ? componentNode.value ?? {} : props.reviewVersionId
  ? versionMetadata.value.find(version => version.id === props.reviewVersionId) ?? {}
  : props.selected)
let resizeObserver: ResizeObserver | undefined
let overlayObserver: MutationObserver | undefined
let previewSyncFrame = 0
let previewSuspended = false
let webPreviewRequest = 0
let archiveDateRequest = 0

const mode = computed<PreviewMode>(() => props.selected.kind === 'Drawing' ? 'drawing' : 'model')
const displayedRevision = computed(() => props.reviewVersionId && props.reviewRevision ? props.reviewRevision : props.selected.version)
const meaningfulSelectedName = computed(() => {
  if (props.emptyProject) return ''
  const name = propertyNode.value?.name?.trim()
  return name && name !== propertyNode.value?.drawingNumber?.trim() ? name : ''
})
const previewPropertyValue = (value?: string | null) => value?.trim() || '—'
const formatArchiveDate = (value?: string | null) => {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString('zh-CN', { hour12: false })
}
const propertiesExpanded = ref(true)
watch(() => props.selected.documentId, () => { propertiesExpanded.value = true })
const corePropertyLabels = new Set(['物料编码', '名称', '图纸版本', '发布版本'])
const previewProperties = computed(() => [
  { label: '物料编码', value: previewPropertyValue(propertyBomItem.value?.drawingNumber || propertyNode.value?.drawingNumber) },
  { label: '名称', value: previewPropertyValue(propertyBomItem.value?.name || meaningfulSelectedName.value) },
  { label: '型号', value: previewPropertyValue(propertyBomItem.value?.specification) },
  { label: '品牌', value: previewPropertyValue(propertyBomItem.value?.brand) },
  { label: '材质', value: previewPropertyValue(propertyBomItem.value?.material) },
  { label: '表面处理', value: previewPropertyValue(propertyBomItem.value?.surfaceTreatment) },
  { label: '热处理', value: previewPropertyValue(propertyBomItem.value?.heatTreatment) },
  { label: '图纸版本', value: documentDrawingVersion(activeVersionMetadata.value) },
  { label: '发布版本', value: documentPublishedVersion(activeVersionMetadata.value) },
  { label: '最近存档', value: formatArchiveDate(lastArchivedAt.value) },
])

async function loadLastArchivedAt() {
  const request = ++archiveDateRequest
  lastArchivedAt.value = ''
  const documentId = propertyNode.value?.documentId
  if (!documentId || !props.accessToken) return
  try {
    const versions = await listDocumentVersions(documentId, props.accessToken)
    if (request !== archiveDateRequest) return
    lastArchivedAt.value = versions.reduce((latest, version) => {
      if (!version.createdAt) return latest
      if (!latest) return version.createdAt
      return new Date(version.createdAt).getTime() > new Date(latest).getTime() ? version.createdAt : latest
    }, '')
  } catch {
    if (request === archiveDateRequest) lastArchivedAt.value = ''
  }
}

function activateMarkup(command: string) {
  if (!props.selected.documentId) return
  if (!props.desktopAvailable) {
    ElMessage.info('图形批注工具仅在Windows客户端的eDrawings预览中可用。')
    return
  }
  postDesktopMessage('preview-host-command', { command })
}

function saveMarkup() {
  if (!props.selected.documentId || markupSaving.value) return
  if (!props.desktopAvailable) {
    ElMessage.info('图形批注仅支持在Windows客户端中保存。')
    return
  }
  markupSaving.value = true
  postDesktopMessage('preview-host-save-markup')
}

function reportPreviewBounds() {
  if (!props.desktopAvailable) return
  if (!props.active) {
    suspendPreview()
    return
  }
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
  if (previewSuspended) postDesktopMessage('preview-host-resume')
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
  // 客户端审核栏与网页端保持同一位置与尺寸：右对齐、上沿与工具条底边齐平、下沿与预览区底边齐平。
  const actionsBounds = document.querySelector('.pdm-preview-actions')?.getBoundingClientRect()
  const actionsBottom = actionsBounds && actionsBounds.height > 0 ? actionsBounds.bottom : top
  const reviewTop = Math.max(top, Math.min(bottom, actionsBottom))
  // 收起时只留窄条，展开时固定 300px（预览区更窄时按可用宽度收窄）。
  const reviewWidth = props.reviewPanelCollapsed ? 34 : Math.min(Math.max(0, right - left), 300)
  postDesktopMessage('review-overlay-bounds', {
    left: right - reviewWidth,
    top: reviewTop,
    width: reviewWidth,
    height: Math.max(0, bottom - reviewTop),
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
  postDesktopMessage('review-overlay-suspend')
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
  postDesktopMessage('review-overlay-hide')
}

function requestLightweightPreview() {
  lightweightPreviewUrl.value = ''
  lightweightPreviewMessage.value = ''
  lightweightPreviewState.value = props.selected.documentId ? 'loading' : 'unavailable'
  if (!props.desktopAvailable || !props.selected.documentId) return
  postDesktopMessage('lightweight-preview-request', { documentId: props.selected.documentId })
}

function clearWebPreview() {
  webPreviewRequest++
  if (webPreviewUrl.value) URL.revokeObjectURL(webPreviewUrl.value)
  webPreviewUrl.value = ''
  webStepBuffer.value = undefined
  webPreviewFormat.value = undefined
  webPreviewNotice.value = ''
}

function normalizedPreviewFormat(value: string | number): 'Step' | 'Pdf' {
  return value === 'Pdf' || value === 1 ? 'Pdf' : 'Step'
}

async function startPreview() {
  if (!props.active || !props.selected.documentId) return
  componentSelection.value = undefined
  previewSessionActivated.value = true
  if (!props.desktopAvailable) {
    clearWebPreview()
    previewError.value = ''
    if (!props.accessToken) {
      previewState.value = 'unavailable'
      previewError.value = '登录后可读取受控预览文件。'
      return
    }
    const request = webPreviewRequest
    previewState.value = 'loading'
    try {
      const versions = await listDocumentVersions(props.selected.documentId, props.accessToken)
      if (request !== webPreviewRequest) return
      const expectedRevision = props.reviewRevision || props.selected.snapshotVersion || props.selected.version
      const requested = props.reviewVersionId
        ? versions.find(item => item.id === props.reviewVersionId)
        : versions.find(item => item.revision.display === expectedRevision)
      // 工作版本签入时不生成预览：这里回退到已生成预览的版本（通常是正式版本），并提示实际展示的版本；
      // 审核冻结版本（reviewVersionId）仍然只看它自己，不做回退。
      const fallback = !props.reviewVersionId && !requested?.preview
        ? versions.find(item => item.preview)
        : undefined
      const version = fallback ?? requested ?? versions[0]
      if (!version?.preview) {
        previewState.value = 'unavailable'
        previewError.value = version ? '该图档还没有生成STP/PDF预览。' : '该图档尚无可预览版本。'
        return
      }
      if (requested && version !== requested)
        webPreviewNotice.value = `当前存档版本 ${requested.revision.display} 还没有预览，以下是存档版本 ${version.revision.display} 的预览。`
      const format = normalizedPreviewFormat(version.preview.format)
      const blob = await readDocumentPreviewFile(props.selected.documentId, version.id, props.accessToken)
      if (request !== webPreviewRequest) return
      webPreviewFormat.value = format
      if (format === 'Pdf') {
        webPreviewUrl.value = URL.createObjectURL(new Blob([blob], { type: 'application/pdf' }))
        previewState.value = 'ready'
      } else {
        webStepBuffer.value = await blob.arrayBuffer()
        if (request === webPreviewRequest) previewState.value = 'loading'
      }
    } catch (error) {
      if (request !== webPreviewRequest) return
      previewState.value = 'error'
      previewError.value = error instanceof Error ? error.message : '网页预览加载失败。'
    }
    return
  }
  previewState.value = 'loading'
  previewError.value = ''
  reportPreviewBounds()
  emit('preview', props.selected, props.reviewVersionId || undefined, props.reviewRevision || undefined)
}

async function restartPreview() {
  hidePreview()
  previewSuspended = false
  previewState.value = 'idle'
  previewError.value = ''
  await nextTick()
  void startPreview()
}

function onWebStepReady() {
  previewState.value = 'ready'
}

function onWebStepError(message: string) {
  previewState.value = 'error'
  previewError.value = message
}

function onPreviewStatus(event: Event) {
  const detail = (event as CustomEvent<{ state?: string; message?: string }>).detail
  if (!detail?.state) return
  if (detail.state === 'loading' || detail.state === 'ready' || detail.state === 'error') {
    previewState.value = detail.state
    previewError.value = detail.message ?? ''
    if (detail.state !== 'ready') componentSelection.value = undefined
    if (detail.state === 'ready') void nextTick(schedulePreviewBounds)
  }
}

function onPreviewComponent(event: Event) {
  const detail = (event as CustomEvent<{ documentId?: string; versionId?: string; name?: string; configuration?: string }>).detail
  if (!props.active || !props.desktopAvailable || previewState.value !== 'ready' || props.selected.kind !== 'Assembly'
    || !detail?.documentId || detail.documentId !== props.selected.documentId
    || (props.reviewVersionId && detail.versionId !== props.reviewVersionId)) return
  const name = detail.name?.trim()
  componentSelection.value = name ? { name, configuration: detail.configuration ?? '' } : undefined
}

function onLightweightPreviewStatus(event: Event) {
  const detail = (event as CustomEvent<{ documentId?: string; state?: string; dataUrl?: string; message?: string }>).detail
  if (!detail?.state || detail.documentId !== props.selected.documentId) return
  if (detail.state !== 'ready' && detail.state !== 'unavailable') return
  lightweightPreviewState.value = detail.state
  lightweightPreviewUrl.value = detail.state === 'ready' ? detail.dataUrl ?? '' : ''
  lightweightPreviewMessage.value = detail.message ?? ''
}

function onMarkupStatus(event: Event) {
  const detail = (event as CustomEvent<{ state?: string; message?: string }>).detail
  if (!detail?.state) return
  markupSaving.value = detail.state === 'saving'
  if (detail.state === 'dirty') markupDirty.value = true
  if (detail.state === 'clean' || detail.state === 'saved') markupDirty.value = false
  if (detail.state === 'saved') ElMessage.success(detail.message || '批注已保存。')
  if (detail.state === 'error') ElMessage.error(detail.message || '批注保存失败。')
}

function openInSolidWorks(mode: SolidWorksOpenMode, versionId?: string) {
  if (!props.selected.documentId || !solidWorksAvailable.value || solidWorksPending.value) return
  solidWorksPending.value = true
  solidWorksError.value = false
  solidWorksMessage.value = mode === 'PropertyWriteback'
    ? '正在打开当前工作版并定位到属性回写…'
    : versionId
      ? '正在准备审核冻结版本（只读）…'
      : '正在准备最新受控文件，不获取编辑权限…'
  emit('open', props.selected, mode, versionId)
}

function onSolidWorksCapability(event: Event) {
  solidWorksAvailable.value = Boolean((event as CustomEvent<{ available?: boolean }>).detail?.available)
}

function onSolidWorksStatus(event: Event) {
  const detail = (event as CustomEvent<{ state?: string; message?: string }>).detail
  if (!detail?.state || !props.active) return
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

watch(() => props.reviewPanelCollapsed, () => {
  void nextTick(schedulePreviewBounds)
}, { flush: 'post' })

watch([() => propertyNode.value?.documentId, () => propertyNode.value?.version, () => props.accessToken], () => {
  void loadLastArchivedAt()
}, { immediate: true })

watch([() => props.selected.id, () => props.reviewVersionId], ([selectedId, reviewVersionId], [previousSelectedId, previousReviewVersionId]) => {
  componentSelection.value = undefined
  // 审批只更新审核版本；只有用户主动加载后才打开该版本，不能续用旧预览会话自动打开。
  if (selectedId === previousSelectedId && reviewVersionId !== previousReviewVersionId) previewSessionActivated.value = false
  markupSaving.value = false
  markupDirty.value = false
  solidWorksPending.value = false
  solidWorksMessage.value = ''
  solidWorksError.value = false
  if (!props.selected.documentId) {
    hidePreview()
    clearWebPreview()
    previewState.value = 'idle'
    previewError.value = ''
    requestLightweightPreview()
    return
  }
  // 已经加载过预览时，同一项目内切换图档直接续用当前会话；只有首次加载需要手动触发。
  if (previewSessionActivated.value) {
    if (props.desktopAvailable) {
      if (props.active) void startPreview()
      else {
        hidePreview()
        previewState.value = 'idle'
      }
    }
    else void restartPreview()
    return
  }
  hidePreview()
  clearWebPreview()
  previewState.value = 'idle'
  previewError.value = ''
  requestLightweightPreview()
})

watch(() => props.active, active => {
  if (!active) previewMaximized.value = false
  solidWorksPending.value = false
  solidWorksMessage.value = ''
  solidWorksError.value = false
  if (!props.desktopAvailable) return
  if (active) {
    if (previewSessionActivated.value && previewState.value === 'idle' && props.selected.documentId) {
      void startPreview()
      return
    }
    void nextTick(schedulePreviewBounds)
    return
  }
  suspendPreview()
}, { flush: 'post' })

watch(() => props.projectId, (projectId, previousProjectId) => {
  if (!previousProjectId || projectId === previousProjectId) return
  previewMaximized.value = false
  componentSelection.value = undefined
  previewSessionActivated.value = false
  hidePreview()
  clearWebPreview()
  previewState.value = 'idle'
  previewError.value = ''
})

onMounted(() => {
  window.addEventListener('keydown', onPreviewKeydown, true)
  window.addEventListener('resize', schedulePreviewBounds)
  window.addEventListener('scroll', schedulePreviewBounds, true)
  document.addEventListener('visibilitychange', schedulePreviewBounds)
  window.addEventListener('pdm-preview-status', onPreviewStatus)
  window.addEventListener('pdm-preview-component', onPreviewComponent)
  window.addEventListener('pdm-lightweight-preview-status', onLightweightPreviewStatus)
  window.addEventListener('pdm-preview-markup-status', onMarkupStatus)
  window.addEventListener('pdm-solidworks-capability', onSolidWorksCapability)
  window.addEventListener('pdm-solidworks-status', onSolidWorksStatus)
  postDesktopMessage('solidworks-capability-request')
  requestLightweightPreview()
  if (typeof ResizeObserver !== 'undefined' && previewSlot.value) {
    resizeObserver = new ResizeObserver(schedulePreviewBounds)
    resizeObserver.observe(previewSlot.value)
  }
  if (window.chrome?.webview && typeof MutationObserver !== 'undefined') {
    overlayObserver = new MutationObserver(schedulePreviewBounds)
    overlayObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'aria-hidden'] })
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onPreviewKeydown, true)
  hidePreview()
  if (previewSyncFrame) window.cancelAnimationFrame(previewSyncFrame)
  resizeObserver?.disconnect()
  overlayObserver?.disconnect()
  clearWebPreview()
  window.removeEventListener('resize', schedulePreviewBounds)
  window.removeEventListener('scroll', schedulePreviewBounds, true)
  document.removeEventListener('visibilitychange', schedulePreviewBounds)
  window.removeEventListener('pdm-preview-status', onPreviewStatus)
  window.removeEventListener('pdm-preview-component', onPreviewComponent)
  window.removeEventListener('pdm-lightweight-preview-status', onLightweightPreviewStatus)
  window.removeEventListener('pdm-preview-markup-status', onMarkupStatus)
  window.removeEventListener('pdm-solidworks-capability', onSolidWorksCapability)
  window.removeEventListener('pdm-solidworks-status', onSolidWorksStatus)
})
</script>

<template>
  <section class="pdm-preview-panel" :class="{ 'is-maximized': previewMaximized }" aria-label="图档预览">
    <section class="pdm-panel pdm-preview-control-panel" aria-label="图档查看与操作">
      <header class="pdm-preview-toolbar">
        <section class="pdm-preview-properties-bar" aria-label="图档信息">
          <span class="pdm-preview-properties-bar__title">{{ componentSelection ? '选中零件属性' : '图档属性' }}</span>
          <span v-if="selected.kind === 'Drawing' && /(?:^|-)W\d+$/i.test(displayedRevision)" class="pdm-preview-work-badge">设计工作版 · 不供生产</span>
          <span v-if="componentSelection && !componentNode" class="pdm-preview-component-unavailable">所选零件暂无可查看的关联图档</span>
          <dl v-else class="pdm-preview-properties" aria-label="图档属性">
            <div v-for="property in previewProperties.filter(item => propertiesExpanded || corePropertyLabels.has(item.label))" :key="property.label">
              <dt>{{ property.label }}</dt>
              <dd :title="property.value">{{ property.value }}</dd>
            </div>
          </dl>
          <button v-if="!componentSelection || componentNode" type="button" class="pdm-inspect-link pdm-text-action" :aria-expanded="propertiesExpanded" @click="propertiesExpanded = !propertiesExpanded">{{ propertiesExpanded ? '收起属性' : '更多属性' }}</button>
        </section>
        <div class="pdm-solidworks-actions">
          <button type="button" class="pdm-preview-command pdm-preview-maximize" :aria-label="previewMaximized ? '还原预览' : '全屏预览'" :aria-pressed="previewMaximized" :title="previewMaximized ? '还原预览（Esc）' : '全屏显示预览区'" @click="togglePreviewMaximized"><Minimize2 v-if="previewMaximized" :size="15" /><Maximize2 v-else :size="15" />{{ previewMaximized ? '还原' : '全屏' }}</button>
          <button
            v-if="projectDownload"
            type="button"
            :class="['pdm-preview-command', reviewVersionId && canWritebackReviewProperties ? 'pdm-solidworks-edit' : 'pdm-solidworks-primary']"
            :aria-label="reviewVersionId ? '打开审核版' : '打开最新'"
            :disabled="!selected.documentId || !solidWorksAvailable || solidWorksPending"
            :title="solidWorksAvailable ? reviewVersionId ? `从PLM获取审核冻结版本${displayedRevision}并只读打开` : `从PLM获取${selected.version}并只读打开总装及引用文件；需要修改时请在插件设计树中获取权限` : '当前电脑未安装SolidWorks或UPLM插件'"
            @click="openInSolidWorks(reviewVersionId ? 'SpecificReadOnly' : 'LatestReadOnly', reviewVersionId || undefined)"
          ><Rotate3D :size="15" />{{ reviewVersionId ? '打开审核版' : '打开最新' }}</button>
          <button
            v-if="projectDownload && canWritebackReviewProperties"
            type="button"
            class="pdm-solidworks-primary"
            :disabled="!selected.documentId || !solidWorksAvailable || solidWorksPending"
            :title="solidWorksAvailable ? '打开当前工作版，并在SolidWorks插件中直接进入属性回写' : '当前电脑未安装SolidWorks或UPLM插件'"
            @click="openInSolidWorks('PropertyWriteback')"
          ><Rotate3D :size="15" />回写审核标记</button>
        </div>
      </header>
      <div class="pdm-preview-toolbar pdm-preview-markup-row">
        <div class="pdm-preview-actions">
          <!-- 批注工具在左、审核结论栏紧随其后；网页端由审核栏 Teleport 注入结论栏，客户端直接把结论栏放进同一容器。 -->
          <div v-if="projectWritable" class="pdm-markup-toolbar" aria-label="图形批注工具">
            <span>批注</span>
            <button type="button" aria-label="引线批注" title="带引线文字" :disabled="!selected.documentId" @click="activateMarkup('markup-text-leader')"><PencilLine :size="14" /></button>
            <button type="button" aria-label="云线批注" title="修订云线" :disabled="!selected.documentId" @click="activateMarkup('markup-cloud')"><Cloud :size="14" /></button>
            <button type="button" aria-label="框选批注" title="矩形框" :disabled="!selected.documentId" @click="activateMarkup('markup-rectangle')"><Square :size="14" /></button>
            <button type="button" aria-label="手绘批注" title="自由曲线" :disabled="!selected.documentId" @click="activateMarkup('markup-spline')"><RotateCcw :size="14" /></button>
          </div>
        </div>
        <button
          type="button"
          v-if="projectWritable" class="pdm-solidworks-edit pdm-preview-command pdm-markup-save"
          :class="{ 'is-markup-dirty': markupDirty }"
          aria-label="保存批注"
          title="保存当前版本批注"
          :disabled="!selected.documentId || markupSaving"
          @click="saveMarkup"
        >{{ markupSaving ? '保存中…' : '保存批注' }}</button>
        <!-- 审核结论栏位于保存批注右侧；网页端与客户端都由页面把结论栏渲染进这个容器。 -->
        <div id="drawing-review-decision-host" class="pdm-review-decision-host" aria-label="图纸审核结论"><slot name="decision-bar" /></div>
      </div>
      <p v-if="solidWorksMessage" class="pdm-solidworks-feedback" :class="{ 'is-error': solidWorksError }" role="status">{{ solidWorksMessage }}</p>
    </section>

    <div class="pdm-panel pdm-preview-content">
      <div
        ref="previewSlot"
        class="pdm-real-preview pdm-embedded-preview-slot"
        :class="{ 'has-web-preview': !desktopAvailable && previewState === 'ready', 'is-desktop-preview': desktopAvailable }"
        :data-preview-state="previewState"
        :aria-label="desktopAvailable ? '客户端内嵌eDrawings预览区' : '网页端图档预览状态'"
      >
        <p v-if="!desktopAvailable && webPreviewNotice" class="pdm-preview-notice">{{ webPreviewNotice }}</p>
        <PdfDrawingViewer v-if="!projectPrint && !desktopAvailable && previewState === 'ready' && webPreviewFormat === 'Pdf' && webPreviewUrl" :url="webPreviewUrl" fill-height />
        <iframe
          v-else-if="!desktopAvailable && previewState === 'ready' && webPreviewFormat === 'Pdf' && webPreviewUrl"
          class="pdm-web-preview-frame"
          :src="webPreviewUrl"
          title="PDF工程图预览"
        />
        <StepPreviewViewer
          v-else-if="!desktopAvailable && webPreviewFormat === 'Step' && webStepBuffer"
          :buffer="webStepBuffer"
          @ready="onWebStepReady"
          @error="onWebStepError"
        />
        <div v-if="emptyProject" class="pdm-preview-state-content">
          <FileSearch :size="52" />
          <h3>项目尚未关联CAD图纸</h3>
          <small>存档后可在左侧选择图档，查看模型和工程图预览。</small>
        </div>
        <template v-else-if="previewState === 'unavailable'">
          <div class="pdm-preview-state-content">
            <FileSearch :size="52" />
            <h3>{{ previewError || '该版本尚无网页预览文件' }}</h3>
            <p :title="`存档版本：${displayedRevision}`">{{ selected.fileName }} · {{ documentVersionSummary(activeVersionMetadata) }}</p>
            <small>工作版本签入时不生成预览；最终审批通过后由服务器生成并绑定正式版本：三维使用STP/STEP，工程图使用PDF。历史DWG不会自动删除，但不再作为预览或发布必需文件。</small>
            <button type="button" class="pdm-primary-action" :disabled="!selected.documentId" @click="emit('more')">{{ projectDownload ? '查看并下载版本' : '查看版本' }}</button>
          </div>
        </template>
        <template v-else-if="desktopAvailable && previewState === 'idle'">
          <div class="pdm-lightweight-preview-stage">
            <img
              v-if="lightweightPreviewState === 'ready' && lightweightPreviewUrl"
              class="pdm-lightweight-preview-image"
              :src="lightweightPreviewUrl"
              :alt="`${selected.fileName} 轻量预览`"
            />
            <div v-else class="pdm-lightweight-preview-placeholder">
              <SquareLoader v-if="lightweightPreviewState === 'loading'" label="正在读取本地轻量预览" />
              <FileSearch v-else :size="52" />
            </div>
            <div class="pdm-lightweight-preview-controls">
              <h3>{{ !selected.documentId ? '请选择图纸' : lightweightPreviewState === 'loading' ? '正在读取轻量预览…' : lightweightPreviewState === 'ready' ? '本地轻量预览' : '暂无轻量预览' }}</h3>
              <p v-if="selected.documentId" :title="`存档版本：${displayedRevision}`">{{ selected.fileName }} · {{ documentVersionSummary(activeVersionMetadata) }}</p>
              <small>{{ !selected.documentId ? '在左侧列表选择图纸后显示预览。' : lightweightPreviewState === 'unavailable' ? lightweightPreviewMessage : '当前仅显示本地只读缓存中的静态缩略图，不会启动 SolidWorks 或 eDrawings。' }}</small>
              <button type="button" class="pdm-primary-action" :disabled="!selected.documentId" @click="startPreview">加载交互预览</button>
            </div>
          </div>
        </template>
        <template v-else-if="previewState !== 'ready'">
          <div class="pdm-preview-state-content">
            <SquareLoader v-if="previewState === 'loading'" :label="desktopAvailable ? '正在加载 eDrawings' : '正在加载网页预览'" />
            <FileSearch v-else :size="52" />
            <h3>{{ previewState === 'loading' ? desktopAvailable ? '正在加载 eDrawings…' : '正在加载网页预览…' : previewState === 'idle' ? '预览尚未加载' : desktopAvailable ? mode === 'model' ? 'eDrawings 内嵌三维预览' : 'eDrawings 内嵌图纸预览' : mode === 'model' ? 'STP/STEP三维预览' : 'PDF工程图预览' }}</h3>
            <p :title="`存档版本：${displayedRevision}`">{{ selected.fileName }} · {{ documentVersionSummary(activeVersionMetadata) }}</p>
            <small>{{ previewState === 'idle' ? '首次进入图档或切换项目时需手动加载一次；加载后只要不切换项目就保持预览，切换到其他页签再返回不会重新加载。' : '文件通过PLM权限校验和SHA-256校验后下载到独立只读缓存，不会覆盖工作文件。' }}</small>
            <p v-if="previewState === 'error'" class="pdm-preview-error" role="alert">{{ previewError || 'eDrawings加载失败，请重试。' }}</p>
            <button v-if="previewState === 'idle' || previewState === 'error'" type="button" class="pdm-primary-action" :disabled="!selected.documentId" @click="startPreview">
              {{ previewState === 'error' ? '重新加载预览' : '加载预览' }}
            </button>
          </div>
        </template>
      </div>
      <slot />
    </div>
  </section>
</template>

<style scoped>
:global(.pdm-document-workspace > .pdm-workspace:has(.pdm-preview-panel.is-maximized)){
  position:fixed;inset:0;z-index:1500;grid-template-columns:min(250px,33vw) minmax(0,1fr);
  grid-template-rows:minmax(0,1fr);background:var(--pdm-surface);
}
:global(.pdm-document-workspace > .pdm-workspace:has(.pdm-preview-panel.is-maximized) > .pdm-tree-panel){grid-area:1 / 1}
:global(.pdm-document-workspace > .pdm-workspace:has(.pdm-preview-panel.is-maximized) > .pdm-stage){grid-area:1 / 2}
.pdm-preview-component-unavailable{color:var(--pdm-muted)}
.pdm-preview-maximize{white-space:nowrap}
.pdm-preview-work-badge{display:inline-flex;align-items:center;padding:2px 8px;border-radius:4px;background:var(--pdm-orange-soft);color:var(--pdm-orange);font-size:12px;font-weight:600;white-space:nowrap}
</style>
