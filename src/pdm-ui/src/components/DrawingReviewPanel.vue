<script setup lang="ts">
import { ArrowLeft, Check, Cloud, History, MessageSquareText, PencilLine, RefreshCw, RotateCcw, Search, Send, Square, X } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { postDesktopMessage } from '../api'
import type { AddDrawingReviewMarkupInput, DrawingReviewCandidate, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()

const props = withDefaults(defineProps<{
  packageId: string
  packages: DrawingReviewPackage[]
  candidates?: DrawingReviewCandidate[]
  reviewerOptions?: Array<{ username: string; label: string }>
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
  reviewerOptions: () => [],
  canManageWithdraw: false,
  allowSelfReview: false,
  overlayHosted: false,
})

const emit = defineEmits<{
  'update:packageId': [packageId: string]
  close: []
  create: [modelDocumentIds: string[] | null, assignedReviewer: string]
  refresh: []
  refreshCandidates: []
  withdraw: [packageId: string, reason: string]
  selectDocument: [documentId: string]
  addMarkup: [packageId: string, input: AddDrawingReviewMarkupInput]
  resolveMarkup: [packageId: string, markupId: string]
  decide: [packageId: string, itemId: string, target: DrawingReviewTarget, decision: DrawingReviewDecision, comment: string]
  decideSupervisor: [packageId: string, decision: DrawingReviewDecision, comment: string]
}>()

const markupText = ref('')
const viewName = ref('')
const markupSeverity = ref<'Note' | 'Blocking'>('Blocking')
const decisionComment = ref('')
const scopeOpen = ref(false)
const scopeMode = ref<'all' | 'manual'>('all')
const scopeSearch = ref('')
const overviewState = ref<'All' | DrawingReviewCandidate['state']>('All')
const selectedCandidateIds = ref<string[]>([])
const assignedReviewer = ref('')
const annotationHostAvailable = ref(false)
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
const activeItem = computed(() => activePackage.value?.items.find(item => item.drawingDocumentId === props.selectedDocumentId))
const target: DrawingReviewTarget = 'Drawing2D'
const activeCreator = computed(() => activeItem.value?.drawingCreatedBy)
const activeTargetState = computed(() => activeItem.value?.drawingState)
const selfReview = computed(() => Boolean(activeCreator.value)
  && activeCreator.value!.localeCompare(props.currentUsername, undefined, { sensitivity: 'accent' }) === 0)
const selfReviewBlocked = computed(() => selfReview.value && !props.allowSelfReview)
const canActOnTarget = computed(() => props.canDecide
  && activePackage.value?.state === 'InReview'
  && activeTargetState.value === 'Pending'
  && (!activePackage.value.assignedReviewer || activePackage.value.assignedReviewer === props.currentUsername || props.allowSelfReview)
  && !selfReviewBlocked.value)
const canActAsSupervisor = computed(() => props.canDecide
  && activePackage.value?.state === 'PendingSupervisorApproval'
  && (activePackage.value.supervisor === props.currentUsername || props.allowSelfReview))
const activeMarkups = computed(() => (activePackage.value?.markups ?? [])
  .filter(markup => markup.itemId === activeItem.value?.id && markup.target === target)
  .sort((left, right) => right.createdAt.localeCompare(left.createdAt)))
const markedTargetCount = computed(() => activePackage.value?.items.reduce((count, item) => count + Number(item.drawingState === 'Approved' || item.drawingState === 'Marked'), 0) ?? 0)
const totalTargetCount = computed(() => activePackage.value?.items.length ?? 0)
const canWithdrawActive = computed(() => Boolean(activePackage.value
  && (activePackage.value.state === 'InReview' || activePackage.value.state === 'PendingSupervisorApproval')
  && props.canSubmit
  && (props.canManageWithdraw || activePackage.value.createdBy.localeCompare(props.currentUsername, undefined, { sensitivity: 'accent' }) === 0)))
const filteredCandidates = computed(() => {
  const keyword = scopeSearch.value.trim().toLocaleLowerCase()
  return props.candidates.filter((candidate) => {
    return !keyword || `${candidate.drawingNumber} ${candidate.name}`.toLocaleLowerCase().includes(keyword)
  })
})
const overviewCandidates = computed(() => props.candidates.filter(candidate => overviewState.value === 'All' || candidate.state === overviewState.value))
const overviewStateOptions = computed(() => ([
  { value: 'All', label: '全部', count: props.candidates.length },
  { value: 'Ready', label: '待审核', count: props.candidates.filter(candidate => candidate.state === 'Ready').length },
  { value: 'InReview', label: '审核中', count: props.candidates.filter(candidate => candidate.state === 'InReview').length },
  { value: 'ApprovedCurrent', label: '已审核', count: props.candidates.filter(candidate => candidate.state === 'ApprovedCurrent').length },
  { value: 'Unavailable', label: '不可发起', count: props.candidates.filter(candidate => candidate.state === 'Unavailable').length },
] as const))
const selectedCandidateCount = computed(() => selectedCandidateIds.value.length)
const candidateEmptyMessage = computed(() => props.candidates.length === 0
  ? '当前没有有效非标件BOM候选；请先将3D模型归入非标件BOM，并保持唯一关联的2D工程图。'
  : '没有符合搜索条件的图档。')

watch(() => props.candidates, () => {
  if (scopeOpen.value && scopeMode.value !== 'manual') applyScopeMode(scopeMode.value)
}, { deep: true })

onMounted(() => nextTick(() => { annotationHostAvailable.value = Boolean(document.getElementById('drawing-review-annotation-host')) }))

function savePanelOpacity() {
  window.localStorage.setItem(panelOpacityStorageKey, String(panelOpacity.value))
}

function applyScopeMode(mode: 'all' | 'manual') {
  scopeMode.value = mode
  if (mode === 'manual') return
  selectedCandidateIds.value = props.candidates
    .filter(candidate => candidate.state === 'Ready' && candidate.modelDocumentId)
    .map(candidate => candidate.modelDocumentId!)
}

function openScopeSelection() {
  scopeOpen.value = true
  scopeSearch.value = ''
  assignedReviewer.value = props.reviewerOptions.length === 1 ? props.reviewerOptions[0]!.username : ''
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

function selectOverviewCandidate(candidate: DrawingReviewCandidate) {
  const matchingPackages = props.packages.filter(review => review.items.some(item => item.drawingDocumentId === candidate.drawingDocumentId))
  const matchingPackage = matchingPackages.find(review => review.state === 'InReview' || review.state === 'WritingProperties') ?? matchingPackages[0]
  if (matchingPackage) emit('update:packageId', matchingPackage.id)
  if (candidate.drawingDocumentId) emit('selectDocument', candidate.drawingDocumentId)
  else if (candidate.modelDocumentId) emit('selectDocument', candidate.modelDocumentId)
}

function submitScope() {
  const modelDocumentIds = scopeMode.value === 'all' ? null : selectedCandidateIds.value
  if (modelDocumentIds !== null && !modelDocumentIds.length) {
    ElMessage.warning('请至少选择一张2D工程图。')
    return
  }
  if (!assignedReviewer.value) {
    ElMessage.warning('请选择指定审核人。')
    return
  }
  emit('create', modelDocumentIds, assignedReviewer.value)
  scopeOpen.value = false
}

function candidateStateLabel(candidate: DrawingReviewCandidate) {
  return ({ Ready: '待审核', InReview: '审核中', ApprovedCurrent: '已审核', Unavailable: '不可发起' } as const)[candidate.state]
}

function activeReviewPackageFor(candidate: DrawingReviewCandidate) {
  return props.packages.find(packageValue =>
    (packageValue.state === 'InReview' || packageValue.state === 'PendingSupervisorApproval' || packageValue.state === 'WritingProperties')
    && packageValue.items.some(item =>
      Boolean(candidate.drawingDocumentId) && item.drawingDocumentId === candidate.drawingDocumentId
      || Boolean(candidate.modelDocumentId) && item.modelDocumentId === candidate.modelDocumentId))
}

function candidateReviewerLabel(candidate: DrawingReviewCandidate) {
  if (candidate.state !== 'InReview') return ''
  const packageValue = activeReviewPackageFor(candidate)
  if (!packageValue) return ''
  const supervisorStage = packageValue.state === 'PendingSupervisorApproval'
  const name = supervisorStage ? packageValue.supervisorName : packageValue.assignedReviewerName
  const username = supervisorStage ? packageValue.supervisor : packageValue.assignedReviewer
  return name || (username ? displayUserName(username) : '')
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
    target,
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
    decision === 'Approve' ? '确认通过2D工程图审核？' : '确认退回设计修改？',
    '图纸审核确认',
    { confirmButtonText: '确认', cancelButtonText: '取消', type: decision === 'Approve' ? 'success' : 'warning' },
  )
  emit('decide', packageValue.id, item.id, target, decision, decisionComment.value.trim())
  decisionComment.value = ''
}

async function decideSupervisor(decision: DrawingReviewDecision) {
  const packageValue = activePackage.value
  if (!packageValue || !canActAsSupervisor.value) return
  if (decision === 'RequestChanges' && !decisionComment.value.trim()) {
    ElMessage.warning('退改必须填写说明。')
    return
  }
  await ElMessageBox.confirm(decision === 'Approve' ? '确认机械主管最终批准？' : '确认退回设计修改？', '机械主管批准', {
    confirmButtonText: '确认', cancelButtonText: '取消', type: decision === 'Approve' ? 'success' : 'warning',
  })
  emit('decideSupervisor', packageValue.id, decision, decisionComment.value.trim())
  decisionComment.value = ''
}

function packageStateLabel(state: DrawingReviewPackage['state']) {
  return ({ InReview: '指定人员审核', PendingSupervisorApproval: '待机械主管批准', ChangesRequested: '已退改', WritingProperties: '写入审核标记', Approved: '审核完成', Stale: '版本冲突', Withdrawn: '已撤销' } as const)[state]
}

function targetStateLabel(state: DrawingReviewPackage['items'][number]['modelState']) {
  return ({ Pending: '待审核', ChangesRequested: '已退改', Approved: '待写标记', Marked: '已审核', NotRequired: '无需审核' } as const)[state]
}
</script>

<template>
  <aside class="drawing-review-panel pdm-panel" :class="{ 'is-overlay-hosted': overlayHosted }" :style="panelOpacityStyle" aria-label="图纸审核面板">
    <header class="drawing-review-panel__header">
      <button v-if="scopeOpen" type="button" class="drawing-review-back" aria-label="返回图纸审核" @click="scopeOpen = false"><ArrowLeft :size="16" /></button>
      <div class="drawing-review-panel__title"><span>非标 BOM · 2D</span><h2>{{ scopeOpen ? '选择审核范围' : '2D图纸审核' }}</h2></div>
      <label v-if="!scopeOpen" class="drawing-review-opacity"><span>透明度</span><input v-model.number="panelOpacity" type="range" min="5" max="95" step="1" aria-label="调整审核栏透明度" @change="savePanelOpacity" /><output>{{ panelOpacity }}%</output></label>
      <button type="button" class="drawing-review-close" aria-label="关闭图纸审核面板" @click="scopeOpen ? scopeOpen = false : emit('close')">×</button>
    </header>

    <template v-if="scopeOpen">
      <section class="drawing-review-scope">
        <div class="drawing-review-scope__modes is-two-column" aria-label="审核范围方式">
          <button type="button" :class="{ 'is-active': scopeMode === 'all' }" @click="applyScopeMode('all')">全部待审</button>
          <button type="button" :class="{ 'is-active': scopeMode === 'manual' }" @click="applyScopeMode('manual')">手工选择</button>
        </div>
        <label class="drawing-review-scope__search"><Search :size="14" /><input v-model="scopeSearch" placeholder="搜索图号或名称" /></label>
        <label class="drawing-review-scope__reviewer"><span>指定审核人</span><select v-model="assignedReviewer" aria-label="指定图纸审核人"><option value="" disabled>请选择</option><option v-for="reviewer in reviewerOptions" :key="reviewer.username" :value="reviewer.username">{{ reviewer.label }}</option></select></label>
        <p class="drawing-review-scope__hint">只显示有效非标 BOM 中唯一关联的2D工程图；已审核当前版本默认不选。</p>
      </section>
      <section class="drawing-review-candidate-list" aria-label="图纸审核候选范围">
        <button
          v-for="candidate in filteredCandidates"
          :key="candidate.candidateId"
          type="button"
          class="drawing-review-candidate"
          :class="[`is-${candidate.state.toLowerCase()}`, { 'is-selected': !!candidate.modelDocumentId && selectedCandidateIds.includes(candidate.modelDocumentId) }]"
          :disabled="!candidate.selectable"
          :title="candidate.reason || candidate.name"
          @click="toggleCandidate(candidate)"
        >
          <span class="drawing-review-candidate__check">{{ candidate.modelDocumentId && selectedCandidateIds.includes(candidate.modelDocumentId) ? '✓' : '' }}</span>
          <strong class="drawing-review-candidate__number">{{ candidate.drawingNumber }}</strong>
          <em class="drawing-review-candidate__state">{{ candidateStateLabel(candidate) }}</em>
        </button>
        <p v-if="!filteredCandidates.length" class="drawing-review-no-candidate">{{ candidateEmptyMessage }}</p>
      </section>
      <footer class="drawing-review-scope__footer"><span>{{ scopeMode === 'all' ? `全部待审（当前${selectedCandidateCount}张）` : `已选择 ${selectedCandidateCount} 张` }}</span><button type="button" class="is-primary" :disabled="pending || !selectedCandidateCount || !assignedReviewer" @click="submitScope"><Send :size="14" />发起审核</button></footer>
    </template>

    <template v-else>
    <div class="drawing-review-panel__toolbar">
      <label><span>状态</span><select v-model="overviewState" aria-label="筛选图纸审核状态"><option v-for="option in overviewStateOptions" :key="option.value" :value="option.value">{{ option.label }}（{{ option.count }}）</option></select></label>
      <button type="button" title="刷新审核状态" :disabled="pending" @click="emit('refresh')"><RefreshCw :size="14" />刷新</button>
    </div>

    <section class="drawing-review-overview" aria-label="图纸审核状态表">
      <header><strong>图纸型号</strong><span>审核状态</span></header>
      <button
        v-for="candidate in overviewCandidates"
        :key="candidate.candidateId"
        type="button"
        class="drawing-review-overview__row"
        :class="[`is-${candidate.state.toLowerCase()}`, { 'is-active': selectedDocumentId === candidate.drawingDocumentId || selectedDocumentId === candidate.modelDocumentId }]"
        :title="candidate.reason || candidate.drawingNumber"
        @click="selectOverviewCandidate(candidate)"
      >
        <strong>{{ candidate.drawingNumber }}</strong><em :title="candidateReviewerLabel(candidate) || undefined"><span v-if="candidateReviewerLabel(candidate)" class="drawing-review-overview__reviewer">{{ candidateReviewerLabel(candidate) }}</span>{{ candidateStateLabel(candidate) }}</em>
      </button>
      <p v-if="!overviewCandidates.length">当前筛选状态下没有图纸</p>
    </section>

    <details v-if="packages.length" class="drawing-review-history">
      <summary><History :size="13" />审核历史</summary>
      <select v-model="selectedPackageId" aria-label="选择图纸审核单"><option v-for="review in packages" :key="review.id" :value="review.id">{{ review.number }} · {{ packageStateLabel(review.state) }}</option></select>
    </details>

    <section v-if="!activePackage" class="drawing-review-panel__empty">
      <MessageSquareText :size="34" />
      <strong>尚未发起图纸审核</strong>
      <p>发起前可全选或手工勾选；系统只冻结非标 BOM 中唯一关联的2D工程图。</p>
      <button v-if="canSubmit" type="button" class="is-primary" :disabled="pending" @click="openScopeSelection"><Send :size="15" />选择范围并发起审核</button>
    </section>

    <template v-else>
      <section class="drawing-review-package-summary">
        <div><strong>{{ activePackage.number }}</strong><span :class="`is-${activePackage.state.toLowerCase()}`">{{ packageStateLabel(activePackage.state) }}</span></div>
        <p>{{ activePackage.items.length }}张2D图纸 · {{ markedTargetCount }}/{{ totalTargetCount }}项完成 · 发起人 {{ displayUserName(activePackage.createdBy) }}</p>
        <p v-if="activePackage.assignedReviewer" class="drawing-review-route">指定审核人 {{ activePackage.assignedReviewerName || displayUserName(activePackage.assignedReviewer) }} → 机械主管 {{ activePackage.supervisorName || displayUserName(activePackage.supervisor) }}</p>
        <p v-if="activePackage.state === 'Withdrawn'" class="drawing-review-withdrawn">{{ activePackage.withdrawnBy }} 撤销：{{ activePackage.withdrawalReason }}</p>
        <div class="drawing-review-package-actions"><button v-if="canWithdrawActive" type="button" class="is-danger" :disabled="pending" @click="withdrawActiveReview"><X :size="14" />撤销审核</button><button v-if="canSubmit" type="button" :disabled="pending" @click="openScopeSelection"><Send :size="14" />发起其他图档</button></div>
      </section>

      <section v-if="!activeItem" class="drawing-review-panel__selection-empty">
        <strong>当前图档未纳入此审核单</strong>
        <p>请在左侧设计树选择本审核单中的2D工程图；也可切换上方历史审核单。</p>
      </section>

      <Teleport to="#drawing-review-annotation-host" :disabled="overlayHosted || !annotationHostAvailable">
      <details v-if="activeItem" open class="drawing-review-annotation-card">
        <summary><MessageSquareText :size="14" /><strong>审核批注</strong><span>{{ packageStateLabel(activePackage.state) }}</span></summary>
        <section class="drawing-review-panel__section drawing-review-decision">
          <div class="drawing-review-section-title"><Check :size="15" /><strong>{{ activePackage.state === 'PendingSupervisorApproval' ? '机械主管批准' : '指定人员审核' }}</strong></div>
          <textarea v-model="decisionComment" rows="2" placeholder="审核意见；退改时必填" />
          <p v-if="selfReviewBlocked" class="drawing-review-self-warning">当前版本由你生成，系统禁止审核自己的图。</p>
          <p v-if="activePackage.state === 'InReview' && activePackage.assignedReviewer" class="drawing-review-assignee">当前审核人：{{ activePackage.assignedReviewerName || displayUserName(activePackage.assignedReviewer) }}</p>
          <p v-if="activePackage.state === 'PendingSupervisorApproval'" class="drawing-review-assignee">当前批准人：{{ activePackage.supervisorName || displayUserName(activePackage.supervisor) }}</p>
          <div v-if="activePackage.state === 'PendingSupervisorApproval'" class="drawing-review-decision-buttons"><button type="button" class="is-reject" :disabled="pending || !canActAsSupervisor" @click="decideSupervisor('RequestChanges')"><X :size="14" />退改</button><button type="button" class="is-approve" :disabled="pending || !canActAsSupervisor" @click="decideSupervisor('Approve')"><Check :size="14" />批准</button></div>
          <div v-else class="drawing-review-decision-buttons"><button type="button" class="is-reject" :disabled="pending || !canActOnTarget" @click="decide('RequestChanges')"><X :size="14" />退改</button><button type="button" class="is-approve" :disabled="pending || !canActOnTarget" @click="decide('Approve')"><Check :size="14" />通过</button></div>
        </section>

        <section class="drawing-review-current">
          <div class="drawing-review-target-switch is-single" aria-label="2D图纸审核状态">
            <button type="button" class="is-active"><strong>2D工程图</strong><span :class="`is-${activeItem.drawingState.toLowerCase()}`">{{ targetStateLabel(activeItem.drawingState) }}</span></button>
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
          <div class="drawing-review-section-title"><MessageSquareText :size="15" /><strong>2D批注</strong></div>
          <div v-if="canAnnotate && activePackage.state === 'InReview'" class="drawing-review-markup-form">
            <div><select v-model="markupSeverity"><option value="Blocking">必须整改</option><option value="Note">优化建议</option></select><input v-model="viewName" placeholder="视图/图纸页（可选）" /></div>
            <textarea v-model="markupText" rows="3" placeholder="填写尺寸、结构、工艺或表达问题…" />
            <button type="button" :disabled="pending" @click="addMarkup">保存批注</button>
          </div>
          <div class="drawing-review-markup-list">
            <article v-for="markup in activeMarkups" :key="markup.id" :class="[`is-${markup.severity.toLowerCase()}`, { 'is-resolved': markup.state === 'Resolved' }]">
              <header><strong>{{ markup.severity === 'Blocking' ? '必须整改' : '优化建议' }}</strong><span>{{ markup.viewName || '当前图纸' }}</span></header>
              <p>{{ markup.text }}</p>
              <footer><span>{{ displayUserName(markup.createdBy) }}</span><button v-if="canAnnotate && markup.state === 'Open'" type="button" @click="emit('resolveMarkup', activePackage.id, markup.id)">标记已处理</button><span v-else>已处理</span></footer>
            </article>
            <p v-if="!activeMarkups.length" class="drawing-review-no-markup">当前图档暂无批注</p>
          </div>
        </section>
      </details>
      </Teleport>
    </template>
    </template>
  </aside>
</template>

<style scoped>
.drawing-review-panel{position:absolute;z-index:20;inset:0 0 0 auto;box-sizing:border-box;width:clamp(300px,24vw,350px);min-width:0;min-height:0;height:100%;display:flex;flex-direction:column;overflow-x:hidden;overflow-y:auto;background:rgba(255,255,255,.72);background:color-mix(in srgb,var(--pdm-surface) var(--drawing-review-opacity,72%),transparent);border-color:color-mix(in srgb,var(--pdm-border) 82%,transparent);box-shadow:-10px 0 28px rgba(15,23,42,.16);backdrop-filter:blur(12px) saturate(125%);-webkit-backdrop-filter:blur(12px) saturate(125%)}
.drawing-review-panel.is-overlay-hosted{position:relative;inset:auto;width:100%;height:100%;border-radius:0;backdrop-filter:none;-webkit-backdrop-filter:none}
.drawing-review-panel__header{display:flex;align-items:center;gap:8px;padding:12px 13px 9px;border-bottom:1px solid var(--pdm-border-soft)}
.drawing-review-panel__title{flex:0 0 auto}.drawing-review-panel__title span{color:var(--pdm-blue);font-size:9px;font-weight:700;letter-spacing:.12em}.drawing-review-panel__header h2{margin:2px 0 0;font-size:15px;font-weight:600}.drawing-review-opacity{min-width:0;display:grid;grid-template-columns:auto minmax(52px,1fr) 30px;align-items:center;gap:4px;margin-left:auto;color:var(--pdm-muted);font-size:9px;white-space:nowrap}.drawing-review-opacity input{width:100%;margin:0;accent-color:var(--pdm-blue);cursor:pointer}.drawing-review-opacity output{text-align:right}.drawing-review-close{width:28px;height:28px;flex:0 0 28px;border:0;border-radius:5px;background:transparent;color:var(--pdm-muted);font-size:20px}.drawing-review-close:hover{background:var(--pdm-surface-muted);color:var(--pdm-text)}
.drawing-review-back{width:28px;flex:0 0 28px;padding:0!important;border:0!important;background:transparent!important}.drawing-review-scope{display:flex;flex-direction:column;gap:7px;padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-scope__modes{display:grid;grid-template-columns:repeat(3,1fr);gap:4px}.drawing-review-scope__modes.is-two-column{grid-template-columns:repeat(2,1fr)}.drawing-review-scope__modes button{padding:4px}.drawing-review-scope__modes button.is-active{border-color:var(--pdm-blue);background:var(--pdm-blue-soft);color:var(--pdm-blue)}.drawing-review-scope>select{width:100%}.drawing-review-scope__search{display:flex;align-items:center;gap:5px;padding:0 7px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface)}.drawing-review-scope__search input{min-width:0;width:100%;height:30px;border:0;outline:0;background:transparent;color:var(--pdm-text);font-size:10px}.drawing-review-scope__hint{margin:0;color:var(--pdm-muted);font-size:9px;line-height:1.45}.drawing-review-candidate-list{min-height:0;flex:1;overflow:auto;padding:6px}.drawing-review-candidate{position:relative;width:100%;min-height:62px!important;display:grid!important;grid-template-columns:20px minmax(0,1fr) 66px;align-items:center!important;justify-content:stretch!important;gap:6px;margin-bottom:5px;padding:7px!important;text-align:left;border-color:var(--pdm-border-soft)!important}.drawing-review-candidate.is-selected{border-color:var(--pdm-blue)!important;background:var(--pdm-blue-soft)!important}.drawing-review-candidate:disabled{opacity:.68}.drawing-review-candidate__check{width:17px;height:17px;display:flex;align-items:center;justify-content:center;border:1px solid var(--pdm-border);border-radius:3px;background:var(--pdm-surface);color:var(--pdm-blue);font-weight:700}.drawing-review-candidate.is-selected .drawing-review-candidate__check{border-color:var(--pdm-blue)}.drawing-review-candidate__copy,.drawing-review-candidate__versions{min-width:0;display:flex;flex-direction:column;gap:2px}.drawing-review-candidate__copy strong,.drawing-review-candidate__copy small{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.drawing-review-candidate__copy strong{font-size:10px}.drawing-review-candidate__copy small,.drawing-review-candidate__copy em,.drawing-review-candidate__versions small,.drawing-review-candidate__versions em{color:var(--pdm-muted);font-size:8px;font-style:normal}.drawing-review-candidate__versions{align-items:flex-end}.drawing-review-candidate__versions strong{font-size:9px}.drawing-review-candidate.is-approvedcurrent .drawing-review-candidate__versions em{color:var(--pdm-green)}.drawing-review-candidate.is-inreview .drawing-review-candidate__versions em,.drawing-review-candidate.is-unavailable .drawing-review-candidate__versions em{color:var(--pdm-orange)}.drawing-review-candidate__reason{grid-column:2/4;color:var(--pdm-muted);font-size:8px}.drawing-review-no-candidate{padding:28px 8px;color:var(--pdm-muted);text-align:center;font-size:10px}.drawing-review-scope__footer{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:9px 10px;border-top:1px solid var(--pdm-border-soft);font-size:10px}.drawing-review-scope__footer button{min-width:100px}.drawing-review-package-actions{display:grid!important;grid-template-columns:1fr 1fr;gap:5px}.drawing-review-package-actions button{width:100%}.drawing-review-package-actions .is-danger{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}.drawing-review-withdrawn{padding:5px 6px;border-radius:4px;background:#fff0ef;color:var(--pdm-danger)!important}
.drawing-review-scope__reviewer{display:grid;grid-template-columns:70px minmax(0,1fr);align-items:center;gap:6px;font-size:10px}.drawing-review-scope__reviewer select{width:100%}.drawing-review-route,.drawing-review-assignee{color:var(--pdm-blue)!important}.drawing-review-annotation-card{box-sizing:border-box;width:100%;max-height:calc(100vh - 330px);overflow:auto;border:1px solid rgba(148,163,184,.55);border-radius:6px;background:rgba(255,255,255,.95);box-shadow:0 4px 14px rgba(15,23,42,.12);color:var(--pdm-text);font-size:10px}.drawing-review-annotation-card>summary{height:34px;display:flex;align-items:center;gap:6px;padding:0 10px;cursor:pointer;list-style:none}.drawing-review-annotation-card>summary::-webkit-details-marker{display:none}.drawing-review-annotation-card>summary>span{margin-left:auto;color:var(--pdm-blue);font-size:9px}.drawing-review-annotation-card .drawing-review-panel__section,.drawing-review-annotation-card .drawing-review-current{padding:8px}.drawing-review-annotation-card button,.drawing-review-annotation-card select{min-height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);font:inherit}.drawing-review-annotation-card button{display:inline-flex;align-items:center;justify-content:center;gap:5px;padding:5px 8px}.drawing-review-annotation-card button:disabled{opacity:.45}.drawing-review-annotation-card textarea,.drawing-review-annotation-card input{font:inherit;font-size:10px}
.drawing-review-candidate{height:30px!important;min-height:30px!important;grid-template-columns:20px minmax(0,1fr) 52px;gap:6px;margin-bottom:3px;padding:0 7px!important;overflow:hidden}.drawing-review-candidate__number{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:10px}.drawing-review-candidate__state{color:var(--pdm-muted);font-size:9px;font-style:normal;text-align:right;white-space:nowrap}.drawing-review-candidate.is-approvedcurrent .drawing-review-candidate__state{color:var(--pdm-green)}.drawing-review-candidate.is-inreview .drawing-review-candidate__state,.drawing-review-candidate.is-unavailable .drawing-review-candidate__state{color:var(--pdm-orange)}
.drawing-review-panel__toolbar{display:flex;gap:5px;padding:8px 10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-panel__toolbar label{min-width:0;display:flex;flex:1;align-items:center;gap:5px}.drawing-review-panel__toolbar select{min-width:0;width:100%}.drawing-review-panel button,.drawing-review-panel select,.drawing-review-panel input,.drawing-review-panel textarea{font:inherit;font-size:10px}.drawing-review-panel button,.drawing-review-panel select{min-height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text)}.drawing-review-panel button{display:inline-flex;align-items:center;justify-content:center;gap:5px;padding:5px 8px}.drawing-review-panel button:disabled{opacity:.45;cursor:not-allowed}.drawing-review-panel .is-primary{border-color:var(--pdm-blue);background:var(--pdm-blue);color:white}
.drawing-review-overview{flex:0 0 auto;padding:0 10px 7px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-overview>header,.drawing-review-overview__row{box-sizing:border-box;width:100%;display:grid!important;grid-template-columns:minmax(0,1fr) 96px;align-items:center;gap:6px}.drawing-review-overview>header{height:25px;padding:0 8px;color:var(--pdm-muted);font-size:9px}.drawing-review-overview>header span{text-align:right}.drawing-review-overview__row{height:30px!important;min-height:30px!important;margin:0 0 3px;padding:0 8px!important;overflow:hidden;text-align:left}.drawing-review-overview__row strong{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.drawing-review-overview__row em{overflow:hidden;color:var(--pdm-muted);font-style:normal;text-align:right;text-overflow:ellipsis;white-space:nowrap}.drawing-review-overview__reviewer{margin-right:4px;color:var(--pdm-text)}.drawing-review-overview__row.is-active{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}.drawing-review-overview__row.is-approvedcurrent em{color:var(--pdm-green)}.drawing-review-overview__row.is-inreview em,.drawing-review-overview__row.is-unavailable em{color:var(--pdm-orange)}.drawing-review-overview>p{margin:0;padding:14px 8px;color:var(--pdm-muted);text-align:center;font-size:9px}.drawing-review-history{padding:6px 10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-history summary{display:flex;align-items:center;gap:5px;color:var(--pdm-muted);font-size:9px;cursor:pointer}.drawing-review-history select{width:100%;margin-top:5px}
.drawing-review-panel__empty,.drawing-review-panel__selection-empty{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px;padding:28px 18px;color:var(--pdm-muted);text-align:center}.drawing-review-panel__empty{flex:1}.drawing-review-panel__empty strong,.drawing-review-panel__selection-empty strong{color:var(--pdm-text);font-size:12px}.drawing-review-panel__empty p,.drawing-review-panel__selection-empty p{margin:0;line-height:1.6;font-size:10px}
.drawing-review-package-summary{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-package-summary>div{display:flex;align-items:center;justify-content:space-between;gap:6px}.drawing-review-package-summary>div>strong{font-size:11px}.drawing-review-package-summary>div>span{padding:3px 6px;border-radius:8px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:9px}.drawing-review-package-summary>div>.is-approved{background:var(--pdm-green-soft);color:var(--pdm-green)}.drawing-review-package-summary>div>.is-changesrequested,.drawing-review-package-summary>div>.is-stale{background:#fff0ef;color:var(--pdm-danger)}.drawing-review-package-summary p{margin:5px 0 8px;color:var(--pdm-muted);font-size:9px}.drawing-review-package-summary>button{width:100%}
.drawing-review-current{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-target-switch{display:grid;grid-template-columns:1fr 1fr;gap:5px}.drawing-review-target-switch.is-single{grid-template-columns:1fr}.drawing-review-target-switch button{height:auto;display:flex;flex-direction:column;align-items:flex-start}.drawing-review-target-switch button.is-active{border-color:var(--pdm-blue);background:var(--pdm-blue-soft);color:var(--pdm-blue)}.drawing-review-target-switch span{font-size:9px;color:var(--pdm-muted)}.drawing-review-target-switch .is-marked{color:var(--pdm-green)}.drawing-review-target-switch .is-changesrequested{color:var(--pdm-danger)}
.drawing-review-panel__section{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-panel__section:last-child{border-bottom:0}.drawing-review-section-title{display:flex;align-items:center;gap:6px;margin-bottom:7px}.drawing-review-section-title strong{font-size:11px}.drawing-review-markup-tools{display:grid;grid-template-columns:1fr 1fr;gap:5px}.drawing-review-markup-form{display:flex;flex-direction:column;gap:5px}.drawing-review-markup-form>div{display:grid;grid-template-columns:90px 1fr;gap:5px}.drawing-review-markup-form select,.drawing-review-markup-form input,.drawing-review-markup-form textarea,.drawing-review-decision textarea{min-width:0;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:5px;padding:6px;background:var(--pdm-surface);color:var(--pdm-text);resize:vertical}.drawing-review-markup-form>button{border-color:#b9d3f7;background:var(--pdm-blue-soft);color:var(--pdm-blue)}
.drawing-review-markup-list{display:flex;flex-direction:column;gap:5px;margin-top:7px}.drawing-review-markup-list article{padding:7px;border-left:3px solid var(--pdm-orange);border-radius:4px;background:#fff9ec}.drawing-review-markup-list article.is-note{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}.drawing-review-markup-list article.is-resolved{opacity:.55}.drawing-review-markup-list header,.drawing-review-markup-list footer{display:flex;justify-content:space-between;gap:5px;color:var(--pdm-muted);font-size:9px}.drawing-review-markup-list p{margin:5px 0;color:var(--pdm-text);font-size:10px;line-height:1.45}.drawing-review-markup-list footer button{min-height:0;padding:0;border:0;background:transparent;color:var(--pdm-blue)}.drawing-review-no-markup{margin:7px 0;color:var(--pdm-muted);font-size:9px;text-align:center}
.drawing-review-decision textarea{width:100%}.drawing-review-self-warning{margin:6px 0;color:var(--pdm-orange);font-size:9px}.drawing-review-decision-buttons{display:grid;grid-template-columns:1fr 1fr;gap:5px;margin-top:6px}.drawing-review-decision-buttons .is-reject{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}.drawing-review-decision-buttons .is-approve{border-color:var(--pdm-green);background:var(--pdm-green);color:white}
@media(max-width:600px){.drawing-review-panel{inset:6px 6px 6px auto;width:calc(100% - 12px);height:calc(100% - 12px);max-height:none}}
</style>
