<script setup lang="ts">
import { ArrowLeft, Check, Cloud, History, MessageSquareText, PencilLine, RefreshCw, RotateCcw, Search, Send, Square, X } from '@lucide/vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, ref, watch } from 'vue'
import { postDesktopMessage } from '../api'
import type { AddDrawingReviewMarkupInput, BomKind, DrawingReviewCandidate, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget } from '../types'

const props = withDefaults(defineProps<{
  packageId: string
  packages: DrawingReviewPackage[]
  candidates?: DrawingReviewCandidate[]
  selectedDocumentId?: string
  currentUsername: string
  pending: boolean
  canSubmit: boolean
  canManageWithdraw?: boolean
  canAnnotate: boolean
  canDecide: boolean
  allowSelfReview?: boolean
  desktopAvailable: boolean
  overlayHosted?: boolean
}>(), {
  candidates: () => [],
  canManageWithdraw: false,
  allowSelfReview: false,
  overlayHosted: false,
})

const emit = defineEmits<{
  'update:packageId': [packageId: string]
  close: []
  create: [modelDocumentIds: string[]]
  refresh: []
  refreshCandidates: []
  withdraw: [packageId: string, reason: string]
  selectDocument: [documentId: string]
  addMarkup: [packageId: string, input: AddDrawingReviewMarkupInput]
  resolveMarkup: [packageId: string, markupId: string]
  decide: [packageId: string, itemId: string, target: DrawingReviewTarget, decision: DrawingReviewDecision, comment: string]
}>()

const markupText = ref('')
const viewName = ref('')
const markupSeverity = ref<'Note' | 'Blocking'>('Blocking')
const decisionComment = ref('')
const scopeOpen = ref(false)
const scopeMode = ref<'all' | 'category' | 'manual'>('all')
const scopeCategory = ref<BomKind>('NonStandard')
const scopeSearch = ref('')
const selectedCandidateIds = ref<string[]>([])
const panelOpacityStorageKey = 'upton-pdm-drawing-review-opacity'
const savedPanelOpacity = window.localStorage.getItem(panelOpacityStorageKey)
const parsedPanelOpacity = savedPanelOpacity === null ? 72 : Number(savedPanelOpacity)
const panelOpacity = ref(Number.isFinite(parsedPanelOpacity) ? Math.min(95, Math.max(5, parsedPanelOpacity)) : 72)
const panelOpacityStyle = computed(() => ({ '--drawing-review-opacity': `${panelOpacity.value}%` }))

const selectedPackageId = computed({
  get: () => props.packageId || props.packages[0]?.id || '',
  set: value => emit('update:packageId', value),
})
const activePackage = computed(() => props.packages.find(item => item.id === selectedPackageId.value) ?? props.packages[0])
const activeItem = computed(() => activePackage.value?.items.find(item => item.modelDocumentId === props.selectedDocumentId || item.drawingDocumentId === props.selectedDocumentId))
const target = computed<DrawingReviewTarget>(() => activeItem.value?.drawingDocumentId && activeItem.value.drawingDocumentId === props.selectedDocumentId ? 'Drawing2D' : 'Model3D')
const activeCreator = computed(() => target.value === 'Model3D' ? activeItem.value?.modelCreatedBy : activeItem.value?.drawingCreatedBy)
const activeTargetState = computed(() => target.value === 'Model3D' ? activeItem.value?.modelState : activeItem.value?.drawingState)
const selfReview = computed(() => Boolean(activeCreator.value)
  && activeCreator.value!.localeCompare(props.currentUsername, undefined, { sensitivity: 'accent' }) === 0)
const selfReviewBlocked = computed(() => selfReview.value && !props.allowSelfReview)
const canActOnTarget = computed(() => props.canDecide
  && activePackage.value?.state === 'InReview'
  && activeTargetState.value === 'Pending'
  && !selfReviewBlocked.value)
const activeMarkups = computed(() => (activePackage.value?.markups ?? [])
  .filter(markup => markup.itemId === activeItem.value?.id && markup.target === target.value)
  .sort((left, right) => right.createdAt.localeCompare(left.createdAt)))
const markedTargetCount = computed(() => activePackage.value?.items.reduce((count, item) => count + Number(item.modelState === 'Marked') + Number(Boolean(item.drawingDocumentId) && item.drawingState === 'Marked'), 0) ?? 0)
const totalTargetCount = computed(() => activePackage.value?.items.reduce((count, item) => count + 1 + Number(Boolean(item.drawingDocumentId)), 0) ?? 0)
const canWithdrawActive = computed(() => Boolean(activePackage.value
  && activePackage.value.state === 'InReview'
  && props.canSubmit
  && (props.canManageWithdraw || activePackage.value.createdBy.localeCompare(props.currentUsername, undefined, { sensitivity: 'accent' }) === 0)))
const filteredCandidates = computed(() => {
  const keyword = scopeSearch.value.trim().toLocaleLowerCase()
  return props.candidates.filter((candidate) => {
    if (scopeMode.value === 'category' && !candidate.bomKinds.includes(scopeCategory.value)) return false
    return !keyword || `${candidate.drawingNumber} ${candidate.name}`.toLocaleLowerCase().includes(keyword)
  })
})
const selectedCandidateCount = computed(() => selectedCandidateIds.value.length)

watch(() => props.candidates, () => {
  if (scopeOpen.value && scopeMode.value !== 'manual') applyScopeMode(scopeMode.value)
}, { deep: true })

function selectTarget(reviewTarget: DrawingReviewTarget) {
  const item = activeItem.value
  if (!item) return
  if (reviewTarget === 'Drawing2D' && !item.drawingDocumentId) return
  emit('selectDocument', reviewTarget === 'Model3D' ? item.modelDocumentId : item.drawingDocumentId!)
  markupText.value = ''
  decisionComment.value = ''
}

function savePanelOpacity() {
  window.localStorage.setItem(panelOpacityStorageKey, String(panelOpacity.value))
}

function applyScopeMode(mode: 'all' | 'category' | 'manual') {
  scopeMode.value = mode
  if (mode === 'manual') return
  selectedCandidateIds.value = props.candidates
    .filter(candidate => candidate.state === 'Ready' && candidate.modelDocumentId && (mode === 'all' || candidate.bomKinds.includes(scopeCategory.value)))
    .map(candidate => candidate.modelDocumentId!)
}

function openScopeSelection() {
  scopeOpen.value = true
  scopeSearch.value = ''
  emit('refreshCandidates')
  applyScopeMode('all')
}

function toggleCandidate(candidate: DrawingReviewCandidate) {
  if (!candidate.selectable || !candidate.modelDocumentId) return
  scopeMode.value = 'manual'
  selectedCandidateIds.value = selectedCandidateIds.value.includes(candidate.modelDocumentId)
    ? selectedCandidateIds.value.filter(id => id !== candidate.modelDocumentId)
    : [...selectedCandidateIds.value, candidate.modelDocumentId]
}

function submitScope() {
  if (!selectedCandidateIds.value.length) {
    ElMessage.warning('请至少选择一组3D/2D图档。')
    return
  }
  emit('create', selectedCandidateIds.value)
  scopeOpen.value = false
}

function candidateStateLabel(candidate: DrawingReviewCandidate) {
  return ({ Ready: '待审核', InReview: '审核中', ApprovedCurrent: '已审核', Unavailable: '不可发起' } as const)[candidate.state]
}

function bomKindLabel(kind: BomKind) {
  return ({ Standard: '标准件', NonStandard: '非标件', Electrical: '电气', Unclassified: '未分类', Mechanical: '机械' } as const)[kind]
}

async function withdrawActiveReview() {
  const packageValue = activePackage.value
  if (!packageValue || !canWithdrawActive.value) return
  try {
    const result = await ElMessageBox.prompt('撤销后将立即释放本审核单内图档的编辑锁，审核记录仍会保留。', '撤销图纸审核', {
      confirmButtonText: '确认撤销',
      cancelButtonText: '取消',
      inputPlaceholder: '请填写撤销原因',
      inputValidator: value => Boolean(value?.trim()) || '请填写撤销原因',
      type: 'warning',
    })
    emit('withdraw', packageValue.id, result.value.trim())
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') throw error
  }
}

function activateMarkup(command: string) {
  if (!activeItem.value) return
  if (!props.desktopAvailable) {
    ElMessage.info('图形批注工具仅在Windows客户端的eDrawings预览中可用。')
    return
  }
  postDesktopMessage('preview-host-command', { command })
}

function addMarkup() {
  const packageValue = activePackage.value
  const item = activeItem.value
  const text = markupText.value.trim()
  if (!packageValue || !item || !text) {
    ElMessage.warning('请填写批注内容。')
    return
  }
  emit('addMarkup', packageValue.id, {
    itemId: item.id,
    target: target.value,
    viewName: viewName.value.trim() || undefined,
    text,
    severity: markupSeverity.value,
  })
  markupText.value = ''
}

async function decide(decision: DrawingReviewDecision) {
  const packageValue = activePackage.value
  const item = activeItem.value
  if (!packageValue || !item || !canActOnTarget.value) return
  if (decision === 'RequestChanges' && !decisionComment.value.trim()) {
    ElMessage.warning('退改必须填写说明。')
    return
  }
  await ElMessageBox.confirm(
    decision === 'Approve' ? `确认通过${target.value === 'Model3D' ? '3D模型' : '2D工程图'}审核？` : '确认退回设计修改？',
    '图纸审核确认',
    { confirmButtonText: '确认', cancelButtonText: '取消', type: decision === 'Approve' ? 'success' : 'warning' },
  )
  emit('decide', packageValue.id, item.id, target.value, decision, decisionComment.value.trim())
  decisionComment.value = ''
}

function packageStateLabel(state: DrawingReviewPackage['state']) {
  return ({ InReview: '审核中', ChangesRequested: '已退改', WritingProperties: '写入审核标记', Approved: '审核完成', Stale: '版本冲突', Withdrawn: '已撤销' } as const)[state]
}

function targetStateLabel(state: DrawingReviewPackage['items'][number]['modelState']) {
  return ({ Pending: '待审核', ChangesRequested: '已退改', Approved: '待写标记', Marked: '已审核', NotRequired: '无需审核' } as const)[state]
}
</script>

<template>
  <aside class="drawing-review-panel pdm-panel" :class="{ 'is-overlay-hosted': overlayHosted }" :style="panelOpacityStyle" aria-label="图纸审核面板">
    <header class="drawing-review-panel__header">
      <button v-if="scopeOpen" type="button" class="drawing-review-back" aria-label="返回图纸审核" @click="scopeOpen = false"><ArrowLeft :size="16" /></button>
      <div class="drawing-review-panel__title"><span>3D / 2D 双审</span><h2>{{ scopeOpen ? '选择审核范围' : '图纸审核' }}</h2></div>
      <label v-if="!scopeOpen" class="drawing-review-opacity"><span>透明度</span><input v-model.number="panelOpacity" type="range" min="5" max="95" step="1" aria-label="调整审核栏透明度" @change="savePanelOpacity" /><output>{{ panelOpacity }}%</output></label>
      <button type="button" class="drawing-review-close" aria-label="关闭图纸审核面板" @click="scopeOpen ? scopeOpen = false : emit('close')">×</button>
    </header>

    <template v-if="scopeOpen">
      <section class="drawing-review-scope">
        <div class="drawing-review-scope__modes" aria-label="审核范围方式">
          <button type="button" :class="{ 'is-active': scopeMode === 'all' }" @click="applyScopeMode('all')">全部待审</button>
          <button type="button" :class="{ 'is-active': scopeMode === 'category' }" @click="applyScopeMode('category')">按BOM分类</button>
          <button type="button" :class="{ 'is-active': scopeMode === 'manual' }" @click="applyScopeMode('manual')">手工选择</button>
        </div>
        <select v-if="scopeMode === 'category'" v-model="scopeCategory" aria-label="选择BOM分类" @change="applyScopeMode('category')">
          <option value="Standard">标准件</option><option value="NonStandard">非标件</option><option value="Electrical">电气</option><option value="Unclassified">未分类</option>
        </select>
        <label class="drawing-review-scope__search"><Search :size="14" /><input v-model="scopeSearch" placeholder="搜索图号或名称" /></label>
        <p class="drawing-review-scope__hint">3D及其关联2D按组选择；已审核当前版本默认不选，可手工勾选重新审核。</p>
      </section>
      <section class="drawing-review-candidate-list" aria-label="图纸审核候选范围">
        <button
          v-for="candidate in filteredCandidates"
          :key="candidate.candidateId"
          type="button"
          class="drawing-review-candidate"
          :class="[`is-${candidate.state.toLowerCase()}`, { 'is-selected': !!candidate.modelDocumentId && selectedCandidateIds.includes(candidate.modelDocumentId) }]"
          :disabled="!candidate.selectable"
          @click="toggleCandidate(candidate)"
        >
          <span class="drawing-review-candidate__check">{{ candidate.modelDocumentId && selectedCandidateIds.includes(candidate.modelDocumentId) ? '✓' : '' }}</span>
          <span class="drawing-review-candidate__copy"><strong>{{ candidate.drawingNumber }}</strong><small>{{ candidate.name }}</small><em>{{ candidate.bomKinds.length ? candidate.bomKinds.map(bomKindLabel).join(' / ') : '设计树装配体' }}</em></span>
          <span class="drawing-review-candidate__versions"><strong>3D {{ candidate.modelRevision }}</strong><small>{{ candidate.drawingDocumentId ? `2D ${candidate.drawingRevision || '—'}` : '仅3D' }}</small><em>{{ candidateStateLabel(candidate) }}</em></span>
          <span v-if="candidate.reason" class="drawing-review-candidate__reason">{{ candidate.reason }}</span>
        </button>
        <p v-if="!filteredCandidates.length" class="drawing-review-no-candidate">当前范围没有可显示的图档</p>
      </section>
      <footer class="drawing-review-scope__footer"><span>已选择 {{ selectedCandidateCount }} 组</span><button type="button" class="is-primary" :disabled="pending || !selectedCandidateCount" @click="submitScope"><Send :size="14" />发起审核</button></footer>
    </template>

    <template v-else>
    <div class="drawing-review-panel__toolbar">
      <label v-if="packages.length"><History :size="14" /><span class="pdm-sr-only">审核历史</span><select v-model="selectedPackageId" aria-label="选择图纸审核单"><option v-for="review in packages" :key="review.id" :value="review.id">{{ review.number }} · {{ packageStateLabel(review.state) }}</option></select></label>
      <button type="button" title="刷新审核状态" :disabled="pending" @click="emit('refresh')"><RefreshCw :size="14" />刷新</button>
    </div>

    <section v-if="!activePackage" class="drawing-review-panel__empty">
      <MessageSquareText :size="34" />
      <strong>尚未发起图纸审核</strong>
      <p>发起前可按全部待审、BOM分类或手工勾选范围；3D及其关联2D会作为一组冻结。</p>
      <button v-if="canSubmit" type="button" class="is-primary" :disabled="pending" @click="openScopeSelection"><Send :size="15" />选择范围并发起审核</button>
    </section>

    <template v-else>
      <section class="drawing-review-package-summary">
        <div><strong>{{ activePackage.number }}</strong><span :class="`is-${activePackage.state.toLowerCase()}`">{{ packageStateLabel(activePackage.state) }}</span></div>
        <p>{{ activePackage.items.length }}组图档 · {{ markedTargetCount }}/{{ totalTargetCount }}项完成 · 发起人 {{ activePackage.createdBy }}</p>
        <p v-if="activePackage.state === 'Withdrawn'" class="drawing-review-withdrawn">{{ activePackage.withdrawnBy }} 撤销：{{ activePackage.withdrawalReason }}</p>
        <div class="drawing-review-package-actions"><button v-if="canWithdrawActive" type="button" class="is-danger" :disabled="pending" @click="withdrawActiveReview"><X :size="14" />撤销审核</button><button v-if="canSubmit" type="button" :disabled="pending" @click="openScopeSelection"><Send :size="14" />发起其他图档</button></div>
      </section>

      <section v-if="!activeItem" class="drawing-review-panel__selection-empty">
        <strong>当前图档未纳入此审核单</strong>
        <p>请在左侧设计树选择本审核单中的3D模型或2D工程图；也可切换上方历史审核单。</p>
      </section>

      <template v-else>
        <section class="drawing-review-panel__section drawing-review-decision">
          <div class="drawing-review-section-title"><Check :size="15" /><strong>审核结论</strong></div>
          <textarea v-model="decisionComment" rows="2" placeholder="审核意见；退改时必填" />
          <p v-if="selfReviewBlocked" class="drawing-review-self-warning">当前版本由你生成，系统禁止审核自己的图。</p>
          <div class="drawing-review-decision-buttons"><button type="button" class="is-reject" :disabled="pending || !canActOnTarget" @click="decide('RequestChanges')"><X :size="14" />退改</button><button type="button" class="is-approve" :disabled="pending || !canActOnTarget" @click="decide('Approve')"><Check :size="14" />通过</button></div>
        </section>

        <section class="drawing-review-current">
          <div class="drawing-review-target-switch" :class="{ 'is-single': !activeItem.drawingDocumentId }" aria-label="3D和2D审核切换">
            <button type="button" :class="{ 'is-active': target === 'Model3D' }" @click="selectTarget('Model3D')"><strong>3D模型</strong><span :class="`is-${activeItem.modelState.toLowerCase()}`">{{ targetStateLabel(activeItem.modelState) }}</span></button>
            <button v-if="activeItem.drawingDocumentId" type="button" :class="{ 'is-active': target === 'Drawing2D' }" @click="selectTarget('Drawing2D')"><strong>2D工程图</strong><span :class="`is-${activeItem.drawingState.toLowerCase()}`">{{ targetStateLabel(activeItem.drawingState) }}</span></button>
          </div>
        </section>

        <section class="drawing-review-panel__section">
          <div class="drawing-review-section-title"><PencilLine :size="15" /><strong>图形批注</strong></div>
          <div class="drawing-review-markup-tools">
            <button type="button" title="带引线文字" @click="activateMarkup('markup-text-leader')"><PencilLine :size="14" />引线</button>
            <button type="button" title="修订云线" @click="activateMarkup('markup-cloud')"><Cloud :size="14" />云线</button>
            <button type="button" title="矩形框" @click="activateMarkup('markup-rectangle')"><Square :size="14" />框选</button>
            <button type="button" title="自由曲线" @click="activateMarkup('markup-spline')"><RotateCcw :size="14" />手绘</button>
          </div>
        </section>

        <section class="drawing-review-panel__section">
          <div class="drawing-review-section-title"><MessageSquareText :size="15" /><strong>{{ target === 'Model3D' ? '3D' : '2D' }}批注</strong></div>
          <div v-if="canAnnotate && activePackage.state === 'InReview'" class="drawing-review-markup-form">
            <div><select v-model="markupSeverity"><option value="Blocking">必须整改</option><option value="Note">优化建议</option></select><input v-model="viewName" placeholder="视图/图纸页（可选）" /></div>
            <textarea v-model="markupText" rows="3" placeholder="填写尺寸、结构、工艺或表达问题…" />
            <button type="button" :disabled="pending" @click="addMarkup">保存批注</button>
          </div>
          <div class="drawing-review-markup-list">
            <article v-for="markup in activeMarkups" :key="markup.id" :class="[`is-${markup.severity.toLowerCase()}`, { 'is-resolved': markup.state === 'Resolved' }]">
              <header><strong>{{ markup.severity === 'Blocking' ? '必须整改' : '优化建议' }}</strong><span>{{ markup.viewName || (target === 'Model3D' ? '当前3D视图' : '当前图纸') }}</span></header>
              <p>{{ markup.text }}</p>
              <footer><span>{{ markup.createdBy }}</span><button v-if="canAnnotate && markup.state === 'Open'" type="button" @click="emit('resolveMarkup', activePackage.id, markup.id)">标记已处理</button><span v-else>已处理</span></footer>
            </article>
            <p v-if="!activeMarkups.length" class="drawing-review-no-markup">当前图档暂无批注</p>
          </div>
        </section>
      </template>
    </template>
    </template>
  </aside>
</template>

<style scoped>
.drawing-review-panel{position:absolute;z-index:20;inset:0 0 0 auto;box-sizing:border-box;width:clamp(300px,24vw,350px);min-width:0;min-height:0;height:100%;display:flex;flex-direction:column;overflow-x:hidden;overflow-y:auto;background:rgba(255,255,255,.72);background:color-mix(in srgb,var(--pdm-surface) var(--drawing-review-opacity,72%),transparent);border-color:color-mix(in srgb,var(--pdm-border) 82%,transparent);box-shadow:-10px 0 28px rgba(15,23,42,.16);backdrop-filter:blur(12px) saturate(125%);-webkit-backdrop-filter:blur(12px) saturate(125%)}
.drawing-review-panel.is-overlay-hosted{position:relative;inset:auto;width:100%;height:100%;border-radius:0;backdrop-filter:none;-webkit-backdrop-filter:none}
.drawing-review-panel__header{display:flex;align-items:center;gap:8px;padding:12px 13px 9px;border-bottom:1px solid var(--pdm-border-soft)}
.drawing-review-panel__title{flex:0 0 auto}.drawing-review-panel__title span{color:var(--pdm-blue);font-size:9px;font-weight:700;letter-spacing:.12em}.drawing-review-panel__header h2{margin:2px 0 0;font-size:15px;font-weight:600}.drawing-review-opacity{min-width:0;display:grid;grid-template-columns:auto minmax(52px,1fr) 30px;align-items:center;gap:4px;margin-left:auto;color:var(--pdm-muted);font-size:9px;white-space:nowrap}.drawing-review-opacity input{width:100%;margin:0;accent-color:var(--pdm-blue);cursor:pointer}.drawing-review-opacity output{text-align:right}.drawing-review-close{width:28px;height:28px;flex:0 0 28px;border:0;border-radius:5px;background:transparent;color:var(--pdm-muted);font-size:20px}.drawing-review-close:hover{background:var(--pdm-surface-muted);color:var(--pdm-text)}
.drawing-review-back{width:28px;flex:0 0 28px;padding:0!important;border:0!important;background:transparent!important}.drawing-review-scope{display:flex;flex-direction:column;gap:7px;padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-scope__modes{display:grid;grid-template-columns:repeat(3,1fr);gap:4px}.drawing-review-scope__modes button{padding:4px}.drawing-review-scope__modes button.is-active{border-color:var(--pdm-blue);background:var(--pdm-blue-soft);color:var(--pdm-blue)}.drawing-review-scope>select{width:100%}.drawing-review-scope__search{display:flex;align-items:center;gap:5px;padding:0 7px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface)}.drawing-review-scope__search input{min-width:0;width:100%;height:30px;border:0;outline:0;background:transparent;color:var(--pdm-text);font-size:10px}.drawing-review-scope__hint{margin:0;color:var(--pdm-muted);font-size:9px;line-height:1.45}.drawing-review-candidate-list{min-height:0;flex:1;overflow:auto;padding:6px}.drawing-review-candidate{position:relative;width:100%;min-height:62px!important;display:grid!important;grid-template-columns:20px minmax(0,1fr) 66px;align-items:center!important;justify-content:stretch!important;gap:6px;margin-bottom:5px;padding:7px!important;text-align:left;border-color:var(--pdm-border-soft)!important}.drawing-review-candidate.is-selected{border-color:var(--pdm-blue)!important;background:var(--pdm-blue-soft)!important}.drawing-review-candidate:disabled{opacity:.68}.drawing-review-candidate__check{width:17px;height:17px;display:flex;align-items:center;justify-content:center;border:1px solid var(--pdm-border);border-radius:3px;background:var(--pdm-surface);color:var(--pdm-blue);font-weight:700}.drawing-review-candidate.is-selected .drawing-review-candidate__check{border-color:var(--pdm-blue)}.drawing-review-candidate__copy,.drawing-review-candidate__versions{min-width:0;display:flex;flex-direction:column;gap:2px}.drawing-review-candidate__copy strong,.drawing-review-candidate__copy small{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.drawing-review-candidate__copy strong{font-size:10px}.drawing-review-candidate__copy small,.drawing-review-candidate__copy em,.drawing-review-candidate__versions small,.drawing-review-candidate__versions em{color:var(--pdm-muted);font-size:8px;font-style:normal}.drawing-review-candidate__versions{align-items:flex-end}.drawing-review-candidate__versions strong{font-size:9px}.drawing-review-candidate.is-approvedcurrent .drawing-review-candidate__versions em{color:var(--pdm-green)}.drawing-review-candidate.is-inreview .drawing-review-candidate__versions em,.drawing-review-candidate.is-unavailable .drawing-review-candidate__versions em{color:var(--pdm-orange)}.drawing-review-candidate__reason{grid-column:2/4;color:var(--pdm-muted);font-size:8px}.drawing-review-no-candidate{padding:28px 8px;color:var(--pdm-muted);text-align:center;font-size:10px}.drawing-review-scope__footer{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:9px 10px;border-top:1px solid var(--pdm-border-soft);font-size:10px}.drawing-review-scope__footer button{min-width:100px}.drawing-review-package-actions{display:grid!important;grid-template-columns:1fr 1fr;gap:5px}.drawing-review-package-actions button{width:100%}.drawing-review-package-actions .is-danger{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}.drawing-review-withdrawn{padding:5px 6px;border-radius:4px;background:#fff0ef;color:var(--pdm-danger)!important}
.drawing-review-panel__toolbar{display:flex;gap:5px;padding:8px 10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-panel__toolbar label{min-width:0;display:flex;flex:1;align-items:center;gap:5px}.drawing-review-panel__toolbar select{min-width:0;width:100%}.drawing-review-panel button,.drawing-review-panel select,.drawing-review-panel input,.drawing-review-panel textarea{font:inherit;font-size:10px}.drawing-review-panel button,.drawing-review-panel select{min-height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text)}.drawing-review-panel button{display:inline-flex;align-items:center;justify-content:center;gap:5px;padding:5px 8px}.drawing-review-panel button:disabled{opacity:.45;cursor:not-allowed}.drawing-review-panel .is-primary{border-color:var(--pdm-blue);background:var(--pdm-blue);color:white}
.drawing-review-panel__empty,.drawing-review-panel__selection-empty{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px;padding:28px 18px;color:var(--pdm-muted);text-align:center}.drawing-review-panel__empty{flex:1}.drawing-review-panel__empty strong,.drawing-review-panel__selection-empty strong{color:var(--pdm-text);font-size:12px}.drawing-review-panel__empty p,.drawing-review-panel__selection-empty p{margin:0;line-height:1.6;font-size:10px}
.drawing-review-package-summary{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-package-summary>div{display:flex;align-items:center;justify-content:space-between;gap:6px}.drawing-review-package-summary>div>strong{font-size:11px}.drawing-review-package-summary>div>span{padding:3px 6px;border-radius:8px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:9px}.drawing-review-package-summary>div>.is-approved{background:var(--pdm-green-soft);color:var(--pdm-green)}.drawing-review-package-summary>div>.is-changesrequested,.drawing-review-package-summary>div>.is-stale{background:#fff0ef;color:var(--pdm-danger)}.drawing-review-package-summary p{margin:5px 0 8px;color:var(--pdm-muted);font-size:9px}.drawing-review-package-summary>button{width:100%}
.drawing-review-current{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-target-switch{display:grid;grid-template-columns:1fr 1fr;gap:5px}.drawing-review-target-switch.is-single{grid-template-columns:1fr}.drawing-review-target-switch button{height:auto;display:flex;flex-direction:column;align-items:flex-start}.drawing-review-target-switch button.is-active{border-color:var(--pdm-blue);background:var(--pdm-blue-soft);color:var(--pdm-blue)}.drawing-review-target-switch span{font-size:9px;color:var(--pdm-muted)}.drawing-review-target-switch .is-marked{color:var(--pdm-green)}.drawing-review-target-switch .is-changesrequested{color:var(--pdm-danger)}
.drawing-review-panel__section{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-panel__section:last-child{border-bottom:0}.drawing-review-section-title{display:flex;align-items:center;gap:6px;margin-bottom:7px}.drawing-review-section-title strong{font-size:11px}.drawing-review-markup-tools{display:grid;grid-template-columns:1fr 1fr;gap:5px}.drawing-review-markup-form{display:flex;flex-direction:column;gap:5px}.drawing-review-markup-form>div{display:grid;grid-template-columns:90px 1fr;gap:5px}.drawing-review-markup-form select,.drawing-review-markup-form input,.drawing-review-markup-form textarea,.drawing-review-decision textarea{min-width:0;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:5px;padding:6px;background:var(--pdm-surface);color:var(--pdm-text);resize:vertical}.drawing-review-markup-form>button{border-color:#b9d3f7;background:var(--pdm-blue-soft);color:var(--pdm-blue)}
.drawing-review-markup-list{display:flex;flex-direction:column;gap:5px;margin-top:7px}.drawing-review-markup-list article{padding:7px;border-left:3px solid var(--pdm-orange);border-radius:4px;background:#fff9ec}.drawing-review-markup-list article.is-note{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}.drawing-review-markup-list article.is-resolved{opacity:.55}.drawing-review-markup-list header,.drawing-review-markup-list footer{display:flex;justify-content:space-between;gap:5px;color:var(--pdm-muted);font-size:9px}.drawing-review-markup-list p{margin:5px 0;color:var(--pdm-text);font-size:10px;line-height:1.45}.drawing-review-markup-list footer button{min-height:0;padding:0;border:0;background:transparent;color:var(--pdm-blue)}.drawing-review-no-markup{margin:7px 0;color:var(--pdm-muted);font-size:9px;text-align:center}
.drawing-review-decision textarea{width:100%}.drawing-review-self-warning{margin:6px 0;color:var(--pdm-orange);font-size:9px}.drawing-review-decision-buttons{display:grid;grid-template-columns:1fr 1fr;gap:5px;margin-top:6px}.drawing-review-decision-buttons .is-reject{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}.drawing-review-decision-buttons .is-approve{border-color:var(--pdm-green);background:var(--pdm-green);color:white}
@media(max-width:600px){.drawing-review-panel{inset:6px 6px 6px auto;width:calc(100% - 12px);height:calc(100% - 12px);max-height:none}}
</style>
