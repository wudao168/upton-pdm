<script setup lang="ts">
import { Cloud, FileSearch, Link2, MoreHorizontal, PencilLine, Rotate3D, RotateCcw, ScanSearch, Square } from '@lucide/vue'
import { ElMessage } from 'element-plus'
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { listDocumentVersions, postDesktopMessage, readDocumentPreviewFile } from '../api'
import type { BomItem, DocumentNode, PreviewMode, SolidWorksOpenMode } from '../types'
import { useUserDisplayName } from '../userDisplay'
import SquareLoader from './SquareLoader.vue'

const StepPreviewViewer = defineAsyncComponent(() => import('./StepPreviewViewer.vue'))

const props = withDefaults(defineProps<{
  selected: DocumentNode
  related: DocumentNode[]
  bomItem?: BomItem
  currentUsername?: string
  canManageLifecycle?: boolean
  canEditDocuments?: boolean
  desktopAvailable?: boolean
  obscured?: boolean
  reviewPanelOpen?: boolean
  reviewStatus?: string
  reviewStatusTone?: 'neutral' | 'pending' | 'warning' | 'success' | 'danger'
  reviewVersionId?: string
  reviewRevision?: string
  canWritebackReviewProperties?: boolean
  accessToken?: string
  projectId?: string
}>(), {
  currentUsername: '',
  canManageLifecycle: false,
  canEditDocuments: false,
  desktopAvailable: false,
  obscured: false,
  reviewPanelOpen: false,
  reviewStatus: '未发起',
  reviewStatusTone: 'neutral',
  reviewVersionId: '',
  reviewRevision: '',
  canWritebackReviewProperties: false,
  accessToken: '',
  projectId: '',
})
const emit = defineEmits<{
  open: [node: DocumentNode, mode: SolidWorksOpenMode, versionId?: string]
  preview: [node: DocumentNode, versionId?: string, revision?: string]
  related: [node: DocumentNode]
  more: []
  whereUsed: []
  obsolete: []
  review: []
}>()
const displayUserName = useUserDisplayName()
const previewSlot = ref<HTMLElement>()
const previewState = ref<'idle' | 'loading' | 'ready' | 'error' | 'unavailable'>('idle')
const previewError = ref('')
const webPreviewFormat = ref<'Step' | 'Pdf'>()
const webPreviewUrl = ref('')
const webStepBuffer = ref<ArrayBuffer>()
const previewSessionActivated = ref(false)
const solidWorksAvailable = ref(false)
const solidWorksPending = ref(false)
const solidWorksMessage = ref('')
const solidWorksError = ref(false)
let resizeObserver: ResizeObserver | undefined
let overlayObserver: MutationObserver | undefined
let previewSyncFrame = 0
let previewSuspended = false
let webPreviewRequest = 0

const mode = computed<PreviewMode>(() => props.selected.kind === 'Drawing' ? 'drawing' : 'model')
const previewKindLabel = computed(() => mode.value === 'drawing' ? '2D工程图' : '3D模型')
const displayedRevision = computed(() => props.reviewVersionId && props.reviewRevision ? props.reviewRevision : props.selected.version)
const selectedDisplayName = computed(() => {
  const name = meaningfulSelectedName.value
  return name || props.selected.drawingNumber?.trim() || props.selected.fileName
})
const meaningfulSelectedName = computed(() => {
  const name = props.selected.name?.trim()
  return name && name !== props.selected.drawingNumber?.trim() ? name : ''
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
    : `${displayUserName(owner)}编辑中`
})
const previewPropertyValue = (value?: string | null) => value?.trim() || '—'
const previewProperties = computed(() => [
  { label: '料号', value: previewPropertyValue(props.selected.drawingNumber) },
  { label: '名称', value: previewPropertyValue(meaningfulSelectedName.value) },
  { label: '型号', value: previewPropertyValue(props.bomItem?.specification) },
  { label: '品牌', value: previewPropertyValue(props.bomItem?.brand) },
  { label: '材质', value: previewPropertyValue(props.bomItem?.material) },
  { label: '表面处理', value: previewPropertyValue(props.bomItem?.surfaceTreatment) },
  { label: '热处理', value: previewPropertyValue(props.bomItem?.heatTreatment) },
])

function activateMarkup(command: string) {
  if (!props.selected.documentId) return
  if (!props.desktopAvailable) {
    ElMessage.info('图形批注工具仅在Windows客户端的eDrawings预览中可用。')
    return
  }
  postDesktopMessage('preview-host-command', { command })
}

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
  const reviewWidth = Math.min(width, Math.min(440, Math.max(360, width * 0.34)))
  postDesktopMessage('review-overlay-bounds', {
    left: right - reviewWidth,
    top,
    width: reviewWidth,
    height,
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
    visible: props.reviewPanelOpen,
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

function clearWebPreview() {
  webPreviewRequest++
  if (webPreviewUrl.value) URL.revokeObjectURL(webPreviewUrl.value)
  webPreviewUrl.value = ''
  webStepBuffer.value = undefined
  webPreviewFormat.value = undefined
}

function normalizedPreviewFormat(value: string | number): 'Step' | 'Pdf' {
  return value === 'Pdf' || value === 1 ? 'Pdf' : 'Step'
}

async function startPreview() {
  if (!props.selected.documentId) return
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
      const version = props.reviewVersionId
        ? versions.find(item => item.id === props.reviewVersionId)
        : versions.find(item => item.revision.display === expectedRevision) ?? versions[0]
      if (!version?.preview) {
        previewState.value = 'unavailable'
        previewError.value = version ? '该历史版本尚未生成STP/PDF预览。' : '该图档尚无可预览版本。'
        return
      }
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
    if (detail.state === 'ready') void nextTick(schedulePreviewBounds)
  }
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

watch(() => props.reviewPanelOpen, () => {
  void nextTick(schedulePreviewBounds)
}, { flush: 'post' })

watch([() => props.selected.id, () => props.reviewVersionId], () => {
  solidWorksPending.value = false
  solidWorksMessage.value = ''
  solidWorksError.value = false
  if (previewSessionActivated.value) void restartPreview()
  else {
    hidePreview()
    clearWebPreview()
    previewState.value = 'idle'
    previewError.value = ''
  }
})

watch(() => props.projectId, (projectId, previousProjectId) => {
  if (!previousProjectId || projectId === previousProjectId) return
  previewSessionActivated.value = false
  hidePreview()
  clearWebPreview()
  previewState.value = 'idle'
  previewError.value = ''
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
})

onBeforeUnmount(() => {
  hidePreview()
  if (previewSyncFrame) window.cancelAnimationFrame(previewSyncFrame)
  resizeObserver?.disconnect()
  overlayObserver?.disconnect()
  clearWebPreview()
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
          <span class="pdm-selected-version" :aria-label="`${reviewVersionId ? '审核冻结版本' : '工作版本'} ${displayedRevision}`">{{ displayedRevision }}</span>
          <span class="pdm-selected-version" :aria-label="`业务状态 ${lifecycleLabel}`">{{ lifecycleLabel }}</span>
          <span class="pdm-selected-status">{{ editStatusLabel }}</span>
          <div v-if="related.length" class="pdm-related-documents" aria-label="关联图档">
            <span><Link2 :size="13" />关联{{ mode === 'model' ? '图纸' : '模型' }}</span>
            <button v-for="document in related" :key="document.id" type="button" :title="document.name" @click="emit('related', document)">{{ document.drawingNumber }}</button>
          </div>
        </div>
        <div class="pdm-preview-actions">
          <div class="pdm-markup-toolbar" aria-label="图形批注工具">
            <span>批注</span>
            <button type="button" aria-label="引线批注" title="带引线文字" :disabled="!selected.documentId" @click="activateMarkup('markup-text-leader')"><PencilLine :size="14" /></button>
            <button type="button" aria-label="云线批注" title="修订云线" :disabled="!selected.documentId" @click="activateMarkup('markup-cloud')"><Cloud :size="14" /></button>
            <button type="button" aria-label="框选批注" title="矩形框" :disabled="!selected.documentId" @click="activateMarkup('markup-rectangle')"><Square :size="14" /></button>
            <button type="button" aria-label="手绘批注" title="自由曲线" :disabled="!selected.documentId" @click="activateMarkup('markup-spline')"><RotateCcw :size="14" /></button>
          </div>
          <button type="button" class="pdm-review-toolbar-button" :class="[{ 'is-active': reviewPanelOpen }, `is-${reviewStatusTone}`]" aria-label="图纸审核" :aria-pressed="reviewPanelOpen" :disabled="!selected.documentId" @click="emit('review')"><ScanSearch :size="15" /><span>图纸审核</span><small>{{ reviewStatus }}</small></button>
          <button type="button" aria-label="使用位置" title="查看该图档被哪些装配体引用" :disabled="!selected.documentId" @click="emit('whereUsed')"><Link2 :size="15" /><span>引用</span></button>
          <button v-if="canManageLifecycle && lifecycleLabel !== '已作废'" type="button" aria-label="作废图档" title="受控作废当前图档" :disabled="!selected.documentId" @click="emit('obsolete')"><span>作废</span></button>
          <button type="button" aria-label="更多操作" title="查看更多图档操作" @click="emit('more')"><MoreHorizontal :size="17" /><span>更多</span></button>
        </div>
        <div class="pdm-solidworks-actions">
          <button
            type="button"
            :class="reviewVersionId && canWritebackReviewProperties ? 'pdm-solidworks-edit' : 'pdm-solidworks-primary'"
            :disabled="!selected.documentId || !solidWorksAvailable || solidWorksPending"
            :title="solidWorksAvailable ? reviewVersionId ? `从PLM获取审核冻结版本${displayedRevision}并只读打开` : `从PLM获取${selected.version}并在SolidWorks中打开；需要修改时请在插件设计树中获取权限` : '当前电脑未安装SolidWorks或UPLM插件'"
            @click="openInSolidWorks(reviewVersionId ? 'SpecificReadOnly' : 'LatestReadOnly', reviewVersionId || undefined)"
          ><Rotate3D :size="15" />{{ reviewVersionId ? '打开审核版（只读）' : '打开最新' }}</button>
          <button
            v-if="!reviewVersionId && canEditDocuments"
            type="button"
            class="pdm-solidworks-edit"
            :disabled="!selected.documentId || !solidWorksAvailable || solidWorksPending"
            :title="solidWorksAvailable ? '由客户端获取PLM最新受控文件和编辑权限，并交给SolidWorks打开' : '当前电脑未安装SolidWorks或UPLM插件'"
            @click="openInSolidWorks('LatestEdit')"
          ><Rotate3D :size="15" />编辑打开</button>
          <button
            v-if="canWritebackReviewProperties"
            type="button"
            class="pdm-solidworks-primary"
            :disabled="!selected.documentId || !solidWorksAvailable || solidWorksPending"
            :title="solidWorksAvailable ? '打开当前工作版，并在SolidWorks插件中直接进入属性回写' : '当前电脑未安装SolidWorks或UPLM插件'"
            @click="openInSolidWorks('PropertyWriteback')"
          ><Rotate3D :size="15" />回写审核标记</button>
        </div>
      </header>
      <p v-if="solidWorksMessage" class="pdm-solidworks-feedback" :class="{ 'is-error': solidWorksError }" role="status">{{ solidWorksMessage }}</p>
    </section>

    <div class="pdm-panel pdm-preview-content">
      <div
        ref="previewSlot"
        class="pdm-real-preview pdm-embedded-preview-slot"
        :class="{ 'has-web-preview': !desktopAvailable && previewState === 'ready' }"
        :data-preview-state="previewState"
        :aria-label="desktopAvailable ? '客户端内嵌eDrawings预览区' : '网页端图档预览状态'"
      >
        <dl class="pdm-preview-properties" aria-label="图档属性">
          <div v-for="property in previewProperties" :key="property.label">
            <dt>{{ property.label }}</dt>
            <dd :title="property.value">{{ property.value }}</dd>
          </div>
        </dl>
        <iframe
          v-if="!desktopAvailable && previewState === 'ready' && webPreviewFormat === 'Pdf' && webPreviewUrl"
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
        <template v-if="previewState === 'unavailable'">
          <FileSearch :size="52" />
          <h3>{{ previewError || '该版本尚无网页预览文件' }}</h3>
          <p>{{ selected.fileName }} · {{ displayedRevision }}</p>
          <small>工作版本签入时不生成预览；最终审批通过后由服务器生成并绑定正式版本：三维使用STP/STEP，工程图使用PDF。历史DWG不会自动删除，但不再作为预览或发布必需文件。</small>
          <button type="button" class="pdm-primary-action" :disabled="!selected.documentId" @click="emit('more')">查看并下载版本</button>
        </template>
        <template v-else-if="previewState !== 'ready'">
          <SquareLoader v-if="previewState === 'loading'" :label="desktopAvailable ? '正在加载 eDrawings' : '正在加载网页预览'" />
          <FileSearch v-else :size="52" />
          <h3>{{ previewState === 'loading' ? desktopAvailable ? '正在加载 eDrawings…' : '正在加载网页预览…' : previewState === 'idle' ? '预览尚未加载' : desktopAvailable ? mode === 'model' ? 'eDrawings 内嵌三维预览' : 'eDrawings 内嵌图纸预览' : mode === 'model' ? 'STP/STEP三维预览' : 'PDF工程图预览' }}</h3>
          <p>{{ selected.fileName }} · {{ displayedRevision }}</p>
          <small>{{ previewState === 'idle' ? '首次进入图档或切换项目时不自动加载；手动加载后，本次停留在图档页期间会随所选图档自动更新。' : '文件通过PLM权限校验和SHA-256校验后下载到独立只读缓存，不会覆盖工作文件。' }}</small>
          <p v-if="previewState === 'error'" class="pdm-preview-error" role="alert">{{ previewError || 'eDrawings加载失败，请重试。' }}</p>
          <button v-if="previewState === 'idle' || previewState === 'error'" type="button" class="pdm-primary-action" :disabled="!selected.documentId" @click="startPreview">
            {{ previewState === 'error' ? '重新加载预览' : '加载预览' }}
          </button>
        </template>
      </div>
      <slot />
    </div>
  </section>
</template>
