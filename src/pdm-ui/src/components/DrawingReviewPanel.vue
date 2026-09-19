<script setup lang="ts">
import { ArrowLeft, ChevronLeft, ChevronRight, History, MessageSquareText, RefreshCw, Search, Send, X } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, ref, watch } from 'vue'
import { drawingReviewAssignedReviewerLabel, drawingReviewCandidateStateTone, drawingReviewPackageStateLabel, drawingReviewPackageStateTone, drawingReviewTargetStateLabel, drawingReviewTargetStateTone } from '../drawingReviewLabels'
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
  collapsed?: boolean
}>(), {
  candidates: () => [],
  reviewerOptions: () => [],
  canManageWithdraw: false,
  allowSelfReview: false,
  overlayHosted: false,
  collapsed: false,
})

const emit = defineEmits<{
  'update:packageId': [packageId: string]
  'update:collapsed': [collapsed: boolean]
  create: [modelDocumentIds: string[] | null, assignedReviewers: string[]]
  refresh: []
  refreshCandidates: []
  withdraw: [packageId: string, reason: string]
  selectDocument: [documentId: string]
  addMarkup: [packageId: string, input: AddDrawingReviewMarkupInput]
  resolveMarkup: [packageId: string, markupId: string]
  decide: [packageId: string, itemId: string, target: DrawingReviewTarget, decision: DrawingReviewDecision, comment: string]
  decideSupervisor: [packageId: string, decision: DrawingReviewDecision, comment: string]
}>()

const scopeOpen = ref(false)
const collapsed = ref(props.collapsed)
watch(() => props.collapsed, value => { collapsed.value = value })
const scopeSearch = ref('')
const overviewState = ref<'All' | DrawingReviewCandidate['state']>('All')
const selectedCandidateIds = ref<string[]>([])
const assignedReviewers = ref<string[]>([])

const selectedPackageId = computed({
  get: () => props.packageId || props.packages[0]?.id || '',
  set: value => emit('update:packageId', value),
})
const activePackage = computed(() => props.packages.find(item => item.id === selectedPackageId.value) ?? props.packages[0])
const activeItem = computed(() => activePackage.value?.items.find(item => item.drawingDocumentId === props.selectedDocumentId))
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
  { value: 'Ready', label: '待提交', count: props.candidates.filter(candidate => candidate.state === 'Ready').length },
  { value: 'InReview', label: '待审核', count: props.candidates.filter(candidate => candidate.state === 'InReview').length },
  { value: 'ApprovedCurrent', label: '已批准', count: props.candidates.filter(candidate => candidate.state === 'ApprovedCurrent').length },
  { value: 'Unavailable', label: '不可发起', count: props.candidates.filter(candidate => candidate.state === 'Unavailable').length },
] as const))
const selectedCandidateCount = computed(() => selectedCandidateIds.value.length)
const candidateEmptyMessage = computed(() => props.candidates.length === 0
  ? '当前没有有效非标件BOM候选；请先将3D模型归入非标件BOM，并保持唯一关联的2D工程图。'
  : '没有符合搜索条件的图档。')

watch(() => props.candidates, () => {
  if (scopeOpen.value) selectAllReadyCandidates()
}, { deep: true })

function toggleCollapsed() {
  collapsed.value = !collapsed.value
  emit('update:collapsed', collapsed.value)
}

function selectAllReadyCandidates() {
  selectedCandidateIds.value = props.candidates
    .filter(candidate => candidate.state === 'Ready' && candidate.modelDocumentId)
    .map(candidate => candidate.modelDocumentId!)
}

function openScopeSelection() {
  scopeOpen.value = true
  scopeSearch.value = ''
  assignedReviewers.value = props.reviewerOptions.length === 1 ? [props.reviewerOptions[0]!.username] : []
  emit('refreshCandidates')
  selectAllReadyCandidates()
}

function toggleCandidate(candidate: DrawingReviewCandidate) {
  if (!candidate.selectable || !candidate.modelDocumentId) return
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
  if (!selectedCandidateIds.value.length) {
    ElMessage.warning('请至少选择一张2D工程图。')
    return
  }
  emit('create', [...selectedCandidateIds.value], [...assignedReviewers.value])
  scopeOpen.value = false
}

function candidateStateLabel(candidate: DrawingReviewCandidate) {
  return ({ Ready: '待提交', InReview: '待审核', ApprovedCurrent: '已批准', Unavailable: '不可发起' } as const)[candidate.state]
}

function overviewStateLabel(candidate: DrawingReviewCandidate) {
  // 状态表按“下一步等谁”显示：待审核 → 待批准 → 已批准。
  if (candidate.state !== 'InReview') return candidateStateLabel(candidate)
  const packageValue = activeReviewPackageFor(candidate)
  const item = packageValue?.items.find(value =>
    Boolean(candidate.drawingDocumentId) && value.drawingDocumentId === candidate.drawingDocumentId
    || Boolean(candidate.modelDocumentId) && value.modelDocumentId === candidate.modelDocumentId)
  return item ? drawingReviewTargetStateLabel(item.drawingState) : candidateStateLabel(candidate)
}

function overviewStateTone(candidate: DrawingReviewCandidate) {
  if (candidate.state !== 'InReview') return drawingReviewCandidateStateTone(candidate.state)
  const packageValue = activeReviewPackageFor(candidate)
  const item = packageValue?.items.find(value =>
    Boolean(candidate.drawingDocumentId) && value.drawingDocumentId === candidate.drawingDocumentId
    || Boolean(candidate.modelDocumentId) && value.modelDocumentId === candidate.modelDocumentId)
  return item ? drawingReviewTargetStateTone(item.drawingState) : drawingReviewCandidateStateTone(candidate.state)
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
  if (packageValue.state === 'PendingSupervisorApproval')
    return packageValue.supervisorName || displayUserName(packageValue.supervisor)
  return packageValue.assignedReviewers?.length || packageValue.assignedReviewer
    ? assignedReviewerLabel(packageValue)
    : ''
}

function assignedReviewerLabel(packageValue: DrawingReviewPackage | undefined) {
  return drawingReviewAssignedReviewerLabel(packageValue, displayUserName)
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

const packageStateLabel = drawingReviewPackageStateLabel
const packageStateTone = computed(() => activePackage.value ? drawingReviewPackageStateTone(activePackage.value.state) : 'neutral')
</script>

<template>
  <aside class="drawing-review-panel pdm-panel" :class="{ 'is-overlay-hosted': overlayHosted, 'is-collapsed': collapsed }" aria-label="图纸审核面板">
    <header class="drawing-review-panel__header">
      <button v-if="collapsed" type="button" class="drawing-review-collapse" aria-label="展开图纸审核栏" title="展开图纸审核栏" @click="toggleCollapsed"><ChevronLeft :size="16" /></button>
      <template v-else>
        <button v-if="scopeOpen" type="button" class="drawing-review-back" aria-label="返回图纸审核" @click="scopeOpen = false"><ArrowLeft :size="16" /></button>
        <div class="drawing-review-panel__title"><h2>{{ scopeOpen ? '选择审核范围' : '2D图纸审核' }}</h2></div>
        <button v-if="scopeOpen" type="button" class="is-primary drawing-review-submit" title="发起图纸审核" :disabled="pending || !selectedCandidateCount || !assignedReviewers.length" @click="submitScope"><Send :size="14" />发起审核</button>
        <button type="button" class="drawing-review-collapse" aria-label="折叠图纸审核栏" title="折叠图纸审核栏" @click="toggleCollapsed"><ChevronRight :size="16" /></button>
      </template>
    </header>

    <template v-if="!collapsed">
    <template v-if="scopeOpen">
      <section class="drawing-review-scope">
        <label class="drawing-review-scope__search"><Search :size="14" /><input v-model="scopeSearch" placeholder="搜索图号或名称" /></label>
        <div class="drawing-review-scope__reviewer">
          <span>指定审核人</span>
          <el-select
            v-model="assignedReviewers"
            multiple
            collapse-tags
            collapse-tags-tooltip
            filterable
            class="drawing-review-reviewer-select"
            placeholder="请选择审核人"
            aria-label="指定图纸审核人"
          >
            <el-option v-for="reviewer in reviewerOptions" :key="reviewer.username" :label="reviewer.label" :value="reviewer.username" />
          </el-select>
        </div>
        <p v-if="!reviewerOptions.length" class="drawing-review-scope__empty-hint">当前没有可选的审核人员，请先在角色权限中配置“审核2D图纸”。</p>
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
          <em class="drawing-review-candidate__state" :class="`is-${drawingReviewCandidateStateTone(candidate.state)}`">{{ candidateStateLabel(candidate) }}</em>
        </button>
        <p v-if="!filteredCandidates.length" class="drawing-review-no-candidate">{{ candidateEmptyMessage }}</p>
      </section>
      <footer class="drawing-review-scope__footer"><span>已选择 {{ selectedCandidateCount }} 张</span></footer>
    </template>

    <template v-else>
    <div class="drawing-review-panel__toolbar">
      <label><span>状态</span><select v-model="overviewState" aria-label="筛选图纸审核状态"><option v-for="option in overviewStateOptions" :key="option.value" :value="option.value">{{ option.label }}（{{ option.count }}）</option></select></label>
      <button type="button" title="刷新审核状态" :disabled="pending" @click="emit('refresh')"><RefreshCw :size="14" />刷新</button>
      <button v-if="canSubmit" type="button" class="drawing-review-toolbar__create" title="发起图纸审核" :disabled="pending" @click="openScopeSelection"><Send :size="14" />发起审核</button>
    </div>

    <section class="drawing-review-overview" aria-label="图纸审核状态表">
      <header><strong>图纸型号</strong><span>审核状态</span></header>
      <button
        v-for="candidate in overviewCandidates"
        :key="candidate.candidateId"
        type="button"
        class="drawing-review-overview__row"
        :class="[`is-${overviewStateTone(candidate)}`, { 'is-active': selectedDocumentId === candidate.drawingDocumentId || selectedDocumentId === candidate.modelDocumentId }]"
        :title="candidate.reason || candidate.drawingNumber"
        @click="selectOverviewCandidate(candidate)"
      >
        <strong>{{ candidate.drawingNumber }}</strong><em :title="candidateReviewerLabel(candidate) || undefined">{{ overviewStateLabel(candidate) }}</em>
      </button>
      <p v-if="!overviewCandidates.length">当前筛选状态下没有图纸</p>
    </section>

    <details v-if="packages.length" class="drawing-review-history">
      <summary><History :size="13" />审核历史</summary>
      <select v-model="selectedPackageId" aria-label="选择图纸审核单"><option v-for="review in packages" :key="review.id" :value="review.id">{{ review.number }} · {{ packageStateLabel(review.state) }}</option></select>
    </details>

    <section v-if="!activePackage" class="drawing-review-panel__empty">
      <MessageSquareText :size="34" />
      <strong>尚未提交图纸审核</strong>
      <p>发起前可全选或手工勾选；系统只冻结非标 BOM 中唯一关联的2D工程图。</p>
      <button v-if="canSubmit" type="button" class="is-primary" :disabled="pending" @click="openScopeSelection"><Send :size="15" />选择范围并发起审核</button>
    </section>

    <template v-else>
      <section class="drawing-review-package-summary">
        <div><strong>{{ activePackage.number }}</strong><span :class="`is-${packageStateTone}`">{{ packageStateLabel(activePackage.state) }}</span></div>
        <p>{{ activePackage.items.length }}张2D图纸 · {{ markedTargetCount }}/{{ totalTargetCount }}项完成 · 发起人 {{ displayUserName(activePackage.createdBy) }}</p>
        <p class="drawing-review-route">审核：{{ assignedReviewerLabel(activePackage) }} → 批准：{{ activePackage.supervisorName || displayUserName(activePackage.supervisor) }}</p>
        <p v-if="activePackage.state === 'Withdrawn'" class="drawing-review-withdrawn">{{ activePackage.withdrawnBy }} 撤销：{{ activePackage.withdrawalReason }}</p>
        <div class="drawing-review-package-actions"><button v-if="canWithdrawActive" type="button" class="is-danger" :disabled="pending" @click="withdrawActiveReview"><X :size="14" />撤销审核</button></div>
      </section>

      <section v-if="!activeItem" class="drawing-review-panel__selection-empty">
        <strong>当前图档未纳入此审核单</strong>
        <p>请在左侧设计树选择本审核单中的2D工程图；也可切换上方历史审核单。</p>
      </section>

    </template>
    </template>
    </template>
  </aside>
</template>

<style scoped>
.drawing-review-panel{font-size:12px;position:absolute;z-index:20;inset:0 0 0 auto;box-sizing:border-box;width:300px;min-width:0;min-height:0;height:100%;display:flex;flex-direction:column;overflow-x:hidden;overflow-y:auto;background:var(--pdm-surface);border-color:var(--pdm-border);box-shadow:-10px 0 28px rgba(15,23,42,.16)}
.drawing-review-panel.is-overlay-hosted{position:relative;inset:auto;width:100%;height:100%;border-radius:0;backdrop-filter:none;-webkit-backdrop-filter:none}
.drawing-review-panel__header{display:flex;align-items:center;gap:8px;padding:12px 13px 9px;border-bottom:1px solid var(--pdm-border-soft)}
.drawing-review-panel__title{flex:0 1 auto;min-width:0}.drawing-review-panel__title span{color:var(--pdm-blue);font-size:12px;font-weight:700;letter-spacing:.12em}.drawing-review-panel__header h2{margin:2px 0 0;overflow:hidden;font-size:15px;font-weight:600;text-overflow:ellipsis;white-space:nowrap}
.drawing-review-back{width:28px;flex:0 0 28px;padding:0!important;border:0!important;background:transparent!important}.drawing-review-panel.is-collapsed{width:34px;min-width:34px}.drawing-review-panel.is-collapsed>.drawing-review-panel__header{padding:8px 4px;justify-content:center;border-bottom:0}.drawing-review-panel__header .drawing-review-submit{margin-left:auto;width:80px;min-width:80px;padding:5px 4px}.drawing-review-panel__header .drawing-review-submit:disabled{border-color:var(--pdm-border);background:var(--pdm-surface-muted);color:var(--pdm-muted);opacity:1}.drawing-review-panel__header .drawing-review-collapse{width:24px;min-width:24px;height:24px;min-height:24px;flex:0 0 24px;padding:0;border:0;background:transparent;color:var(--pdm-muted)}.drawing-review-panel__header .drawing-review-collapse:hover{background:var(--pdm-surface-muted);color:var(--pdm-text)}.drawing-review-scope{display:flex;flex-direction:column;gap:7px;padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-scope>select{width:100%}.drawing-review-scope__search{display:flex;align-items:center;gap:5px;padding:0 7px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface)}.drawing-review-scope__search input{min-width:0;width:100%;height:30px;border:0;outline:0;background:transparent;color:var(--pdm-text);font-size:12px}.drawing-review-candidate-list{min-height:0;flex:1;overflow:auto;padding:6px}.drawing-review-candidate{position:relative;width:100%;min-height:62px!important;display:grid!important;grid-template-columns:20px minmax(0,1fr) 66px;align-items:center!important;justify-content:stretch!important;gap:6px;margin-bottom:5px;padding:7px!important;text-align:left;border-color:var(--pdm-border-soft)!important}.drawing-review-candidate.is-selected{border-color:var(--pdm-blue)!important;background:var(--pdm-blue-soft)!important}.drawing-review-candidate:disabled{opacity:.68}.drawing-review-candidate__check{width:17px;height:17px;display:flex;align-items:center;justify-content:center;border:1px solid var(--pdm-border);border-radius:3px;background:var(--pdm-surface);color:var(--pdm-blue);font-weight:700}.drawing-review-candidate.is-selected .drawing-review-candidate__check{border-color:var(--pdm-blue)}.drawing-review-candidate__copy,.drawing-review-candidate__versions{min-width:0;display:flex;flex-direction:column;gap:2px}.drawing-review-candidate__copy strong,.drawing-review-candidate__copy small{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.drawing-review-candidate__copy strong{font-size:12px}.drawing-review-candidate__copy small,.drawing-review-candidate__copy em,.drawing-review-candidate__versions small,.drawing-review-candidate__versions em{color:var(--pdm-muted);font-size:12px;font-style:normal}.drawing-review-candidate__versions{align-items:flex-end}.drawing-review-candidate__versions strong{font-size:12px}.drawing-review-candidate.is-approvedcurrent .drawing-review-candidate__versions em{color:var(--pdm-green)}.drawing-review-candidate.is-inreview .drawing-review-candidate__versions em,.drawing-review-candidate.is-unavailable .drawing-review-candidate__versions em{color:var(--pdm-orange)}.drawing-review-candidate__reason{grid-column:2/4;color:var(--pdm-muted);font-size:12px}.drawing-review-no-candidate{padding:28px 8px;color:var(--pdm-muted);text-align:center;font-size:12px}.drawing-review-scope__footer{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:9px 10px;border-top:1px solid var(--pdm-border-soft);font-size:12px}.drawing-review-package-actions{display:grid!important;grid-template-columns:1fr;gap:5px}.drawing-review-package-actions button{width:100%}.drawing-review-package-actions .is-danger{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}.drawing-review-withdrawn{padding:5px 6px;border-radius:4px;background:#fff0ef;color:var(--pdm-danger)!important}
.drawing-review-scope__reviewer{display:grid;grid-template-columns:70px minmax(0,1fr);align-items:center;gap:6px;font-size:12px}.drawing-review-scope__reviewer select{width:100%}.drawing-review-reviewer-select{width:100%;min-width:0}.drawing-review-scope__reviewer :deep(.el-select__wrapper){min-height:30px;padding:2px 8px;border-radius:5px;font-size:12px}.drawing-review-scope__reviewer :deep(.el-select__selection){gap:2px;flex-wrap:wrap}.drawing-review-scope__reviewer :deep(.el-tag){height:18px;padding:0 5px;font-size:12px}.drawing-review-scope__empty-hint{margin:0;color:var(--pdm-muted);font-size:12px;line-height:1.45}.drawing-review-route{color:var(--pdm-blue)!important}
.drawing-review-candidate{height:30px!important;min-height:30px!important;grid-template-columns:20px minmax(0,1fr) 52px;gap:6px;margin-bottom:3px;padding:0 7px!important;overflow:hidden}.drawing-review-candidate__number{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px}.drawing-review-candidate__state{color:var(--pdm-muted);font-size:12px;font-style:normal;text-align:right;white-space:nowrap}.drawing-review-candidate.is-approvedcurrent .drawing-review-candidate__state{color:var(--pdm-green)}.drawing-review-candidate.is-inreview .drawing-review-candidate__state,.drawing-review-candidate.is-unavailable .drawing-review-candidate__state{color:var(--pdm-orange)}
.drawing-review-panel__toolbar{display:flex;gap:5px;padding:8px 10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-panel__toolbar label{min-width:0;display:flex;flex:1;align-items:center;gap:5px}.drawing-review-panel__toolbar label>span{flex:0 0 auto;white-space:nowrap}.drawing-review-panel__toolbar select{min-width:0;width:100%}.drawing-review-panel__toolbar .drawing-review-toolbar__create{width:80px;min-width:80px;padding:5px 4px}.drawing-review-panel button,.drawing-review-panel select,.drawing-review-panel input,.drawing-review-panel textarea{font:inherit;font-size:12px}.drawing-review-panel button,.drawing-review-panel select{min-height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text)}.drawing-review-panel button{display:inline-flex;align-items:center;justify-content:center;gap:5px;padding:5px 8px}.drawing-review-panel button:disabled{opacity:.45;cursor:not-allowed}.drawing-review-panel .is-primary{border-color:var(--pdm-blue);background:var(--pdm-blue);color:white}
.drawing-review-overview{flex:0 0 auto;padding:0 10px 7px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-overview>header,.drawing-review-overview__row{box-sizing:border-box;width:100%;display:grid!important;grid-template-columns:minmax(0,1fr) 96px;align-items:center;gap:6px}.drawing-review-overview>header{height:25px;padding:0 8px;color:var(--pdm-muted);font-size:12px}.drawing-review-overview>header span{text-align:right}.drawing-review-overview__row{height:30px!important;min-height:30px!important;margin:0 0 3px;padding:0 8px!important;overflow:hidden;text-align:left}.drawing-review-overview__row strong{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.drawing-review-overview__row em{overflow:hidden;color:var(--pdm-muted);font-style:normal;text-align:right;text-overflow:ellipsis;white-space:nowrap}.drawing-review-overview__reviewer{margin-right:4px;color:var(--pdm-text)}.drawing-review-overview__row.is-active{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}.drawing-review-overview__row.is-approvedcurrent em{color:var(--pdm-green)}.drawing-review-overview__row.is-inreview em,.drawing-review-overview__row.is-unavailable em{color:var(--pdm-orange)}.drawing-review-overview>p{margin:0;padding:14px 8px;color:var(--pdm-muted);text-align:center;font-size:12px}.drawing-review-history{padding:6px 10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-history summary{display:flex;align-items:center;gap:5px;color:var(--pdm-muted);font-size:12px;cursor:pointer}.drawing-review-history select{width:100%;margin-top:5px}
.drawing-review-panel__empty,.drawing-review-panel__selection-empty{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px;padding:28px 18px;color:var(--pdm-muted);text-align:center}.drawing-review-panel__empty{flex:1}.drawing-review-panel__empty strong,.drawing-review-panel__selection-empty strong{color:var(--pdm-text);font-size:12px}.drawing-review-panel__empty p,.drawing-review-panel__selection-empty p{margin:0;line-height:1.6;font-size:12px}
.drawing-review-package-summary{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-package-summary>div{display:flex;align-items:center;justify-content:space-between;gap:6px}.drawing-review-package-summary>div>strong{font-size:12px}.drawing-review-package-summary>div>span{padding:3px 6px;border-radius:8px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:12px}.drawing-review-package-summary>div>.is-approved{background:var(--pdm-green-soft);color:var(--pdm-green)}.drawing-review-package-summary>div>.is-changesrequested,.drawing-review-package-summary>div>.is-stale{background:#fff0ef;color:var(--pdm-danger)}.drawing-review-package-summary p{margin:5px 0 8px;color:var(--pdm-muted);font-size:12px}.drawing-review-package-summary>button{width:100%}
@media(max-width:600px){.drawing-review-panel{inset:6px 6px 6px auto;width:calc(100% - 12px);height:calc(100% - 12px);max-height:none}}
.drawing-review-overview__row.is-neutral em{color:var(--pdm-muted)}
.drawing-review-overview__row.is-warning em{color:var(--pdm-orange)}
.drawing-review-overview__row.is-pending em{color:#2563eb}
.drawing-review-overview__row.is-success em{color:var(--pdm-green)}
.drawing-review-overview__row.is-danger em{color:var(--pdm-danger)}
.drawing-review-overview__row.is-warning{border-left:3px solid var(--pdm-orange)}
.drawing-review-overview__row.is-pending{border-left:3px solid #2563eb}
.drawing-review-overview__row.is-success{border-left:3px solid var(--pdm-green)}
.drawing-review-overview__row.is-danger{border-left:3px solid var(--pdm-danger)}
.drawing-review-overview__row.is-neutral{border-left:3px solid var(--pdm-border)}
.drawing-review-candidate__state.is-neutral{color:var(--pdm-muted)}
.drawing-review-candidate__state.is-warning{color:var(--pdm-orange)}
.drawing-review-candidate__state.is-pending{color:#2563eb}
.drawing-review-candidate__state.is-success{color:var(--pdm-green)}
.drawing-review-candidate__state.is-danger{color:var(--pdm-danger)}
.drawing-review-package-summary>div>span.is-warning{background:#fff4dc;color:var(--pdm-orange)}
.drawing-review-package-summary>div>span.is-pending{background:#e8f0fe;color:#2563eb}
.drawing-review-package-summary>div>span.is-success{background:var(--pdm-green-soft);color:var(--pdm-green)}
.drawing-review-package-summary>div>span.is-danger{background:#fff0ef;color:var(--pdm-danger)}
.drawing-review-package-summary>div>span.is-neutral{background:var(--pdm-surface-muted);color:var(--pdm-muted)}
</style>
