<script setup lang="ts">
import { Check, RotateCcw, X } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, ref } from 'vue'
import { drawingReviewAssignedReviewerLabel, drawingReviewAssignedReviewerPool, drawingReviewPackageStateLabel } from '../drawingReviewLabels'
import type { AddDrawingReviewMarkupInput, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()

const props = withDefaults(defineProps<{
  package?: DrawingReviewPackage
  selectedDocumentId?: string
  currentUsername: string
  pending: boolean
  canAnnotate: boolean
  canDecide: boolean
  allowSelfReview?: boolean
  desktopAvailable: boolean
}>(), {
  allowSelfReview: false,
})

const emit = defineEmits<{
  addMarkup: [packageId: string, input: AddDrawingReviewMarkupInput]
  resolveMarkup: [packageId: string, markupId: string]
  decide: [packageId: string, itemId: string, target: DrawingReviewTarget, decision: DrawingReviewDecision, comment: string]
  decideSupervisor: [packageId: string, decision: DrawingReviewDecision, comment: string]
}>()

const decisionComment = ref('')
const target: DrawingReviewTarget = 'Drawing2D'

const activePackage = computed(() => props.package)
const activeItem = computed(() => activePackage.value?.items.find(item => item.drawingDocumentId === props.selectedDocumentId))
// 审核进行中（待审核或待批准）时可以操作；其它状态结论栏常驻显示但不可操作。
const reviewActive = computed(() => activePackage.value?.state === 'InReview' || activePackage.value?.state === 'PendingSupervisorApproval')
const activeCreator = computed(() => activeItem.value?.drawingCreatedBy)
const assignedReviewerPool = computed(() => drawingReviewAssignedReviewerPool(activePackage.value))
const selfReview = computed(() => Boolean(activeCreator.value)
  && activeCreator.value!.localeCompare(props.currentUsername, undefined, { sensitivity: 'accent' }) === 0)
const selfReviewBlocked = computed(() => selfReview.value && !props.allowSelfReview)
const canActOnTarget = computed(() => props.canDecide
  && activePackage.value?.state === 'InReview'
  && activeItem.value?.drawingState === 'Pending'
  && (assignedReviewerPool.value.length === 0 || assignedReviewerPool.value.includes(props.currentUsername) || props.allowSelfReview)
  && !selfReviewBlocked.value)
const canActAsSupervisor = computed(() => props.canDecide
  && activePackage.value?.state === 'PendingSupervisorApproval'
  && (activePackage.value.supervisor === props.currentUsername || props.allowSelfReview))
const supervisorApproval = computed(() => activePackage.value?.state === 'PendingSupervisorApproval')
const canAct = computed(() => supervisorApproval.value ? canActAsSupervisor.value : canActOnTarget.value)
const itemApproved = computed(() => activeItem.value?.drawingState === 'Approved' || activeItem.value?.drawingState === 'Marked')
const itemChangesRequested = computed(() => activeItem.value?.drawingState === 'ChangesRequested')
const canRevoke = computed(() => props.canDecide
  && !props.pending
  && (itemApproved.value || itemChangesRequested.value)
  && (activePackage.value?.state === 'InReview' || supervisorApproval.value)
  && (assignedReviewerPool.value.length === 0
    || assignedReviewerPool.value.includes(props.currentUsername)
    || activePackage.value?.supervisor === props.currentUsername
    || props.allowSelfReview)
)
const routeLabel = computed(() => supervisorApproval.value
  ? `当前批准人：${activePackage.value?.supervisorName || displayUserName(activePackage.value?.supervisor)}`
  : `当前审核人：${assignedReviewerPool.value.length ? assignedReviewerLabel(activePackage.value) : '具备审核权限的人员均可处理，任一通过即可'}`)
const itemDecided = computed(() => itemApproved.value || itemChangesRequested.value)
const decisionRouteLabel = computed(() => {
  if (!activePackage.value || !activeItem.value) return '当前图档未纳入审核单'
  if (itemApproved.value) return '该2D工程图已通过审核，等待批准'
  if (itemChangesRequested.value) return '该2D工程图已退回修改，其余图纸可继续审核'
  if (!reviewActive.value) return drawingReviewPackageStateLabel(activePackage.value.state)
  return routeLabel.value
})

function assignedReviewerLabel(packageValue: DrawingReviewPackage | undefined) {
  return drawingReviewAssignedReviewerLabel(packageValue, displayUserName)
}

async function decide(decision: DrawingReviewDecision) {
  const packageValue = activePackage.value
  const item = activeItem.value
  if (!packageValue || !item || !canAct.value) return
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
  await ElMessageBox.confirm(decision === 'Approve' ? '确认批准该审核单？' : '确认退回设计修改？', '批准', {
    confirmButtonText: '确认', cancelButtonText: '取消', type: decision === 'Approve' ? 'success' : 'warning',
  })
  emit('decideSupervisor', packageValue.id, decision, decisionComment.value.trim())
  decisionComment.value = ''
}

function submit(decision: DrawingReviewDecision) {
  return supervisorApproval.value ? decideSupervisor(decision) : decide(decision)
}

async function revoke() {
  const packageValue = activePackage.value
  const item = activeItem.value
  if (!packageValue || !item || !canRevoke.value) return
  await ElMessageBox.confirm(
    '撤销后该2D工程图将回到待审核状态，需要重新审核；如已提交批准，将退回审核节点。',
    itemApproved.value ? '撤销审核通过' : '撤销退改',
    { confirmButtonText: '确认撤销', cancelButtonText: '取消', type: 'warning' },
  )
  emit('decide', packageValue.id, item.id, target, 'Revoke', decisionComment.value.trim())
  decisionComment.value = ''
}
</script>

<template>
  <!-- 审核结论栏常驻显示：不可操作时输入与按钮一起禁用，通过后“通过”按钮就地变成“撤销”。 -->
  <section class="drawing-review-decision-bar" aria-label="图纸审核结论">
    <span class="drawing-review-decision-bar__node" :class="supervisorApproval ? 'is-pending' : 'is-warning'">{{ supervisorApproval ? '批准' : '审核' }}</span>
    <textarea v-model="decisionComment" rows="1" placeholder="审核意见；退改时必填" aria-label="审核意见" :disabled="!canAct" />
    <span class="drawing-review-decision-bar__route">{{ decisionRouteLabel }}</span>
    <div class="drawing-review-decision-buttons">
      <button type="button" class="is-reject" :disabled="pending || !canAct" @click="submit('RequestChanges')"><X :size="14" />退改</button>
      <button
        v-if="itemDecided"
        type="button"
        class="is-revoke"
        :title="itemApproved ? '撤销审核通过' : '撤销退改结论'"
        :disabled="!canRevoke"
        @click="revoke()"
      ><RotateCcw :size="14" />撤销</button>
      <button v-else type="button" class="is-approve" :disabled="pending || !canAct" @click="submit('Approve')"><Check :size="14" />{{ supervisorApproval ? '批准' : '通过' }}</button>
    </div>
  </section>
</template>

<style scoped>
.drawing-review-decision-bar{display:flex;flex:1 1 auto;min-width:0;align-items:center;gap:8px;color:var(--pdm-text);font-size:12px}
.drawing-review-decision-bar__node{flex:0 0 auto;padding:4px 7px;border-radius:5px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:12px;font-weight:500;white-space:nowrap}
.drawing-review-decision-bar__node.is-warning{background:#fff4dc;color:#d77a17}
.drawing-review-decision-bar__node.is-pending{background:#e8f0fe;color:#2563eb}
.drawing-review-decision-bar textarea{flex:1 1 160px;min-width:110px;height:28px;box-sizing:border-box;padding:5px 7px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);font:inherit;font-size:12px;resize:vertical}
.drawing-review-decision-bar__route{flex:0 1 auto;min-width:0;overflow:hidden;color:var(--pdm-blue);font-size:12px;text-overflow:ellipsis;white-space:nowrap}
.drawing-review-decision-bar .drawing-review-decision-buttons{flex:0 0 auto;display:flex;gap:5px;margin-left:auto}
.drawing-review-decision-bar .drawing-review-decision-buttons button{display:inline-flex;align-items:center;justify-content:center;gap:5px;width:72px;min-width:72px;min-height:28px;padding:4px 6px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);font:inherit;font-size:12px;cursor:pointer}
.drawing-review-decision-bar .drawing-review-decision-buttons button:disabled{opacity:.45;cursor:not-allowed}
.drawing-review-decision-bar .drawing-review-decision-buttons .is-reject{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}
.drawing-review-decision-bar .drawing-review-decision-buttons .is-approve{border-color:var(--pdm-green);background:var(--pdm-green);color:white}
.drawing-review-decision-bar .drawing-review-decision-buttons .is-revoke{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}
</style>
