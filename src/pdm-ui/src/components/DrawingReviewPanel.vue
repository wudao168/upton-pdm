<script setup lang="ts">
import { Check, Cloud, History, MessageSquareText, PencilLine, RefreshCw, RotateCcw, Send, Square, X } from '@lucide/vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, ref } from 'vue'
import { postDesktopMessage } from '../api'
import type { AddDrawingReviewMarkupInput, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget } from '../types'

const props = defineProps<{
  packageId: string
  packages: DrawingReviewPackage[]
  selectedDocumentId?: string
  currentUsername: string
  pending: boolean
  canSubmit: boolean
  canAnnotate: boolean
  canDecide: boolean
  desktopAvailable: boolean
}>()

const emit = defineEmits<{
  'update:packageId': [packageId: string]
  close: []
  create: []
  refresh: []
  selectDocument: [documentId: string]
  addMarkup: [packageId: string, input: AddDrawingReviewMarkupInput]
  resolveMarkup: [packageId: string, markupId: string]
  decide: [packageId: string, itemId: string, target: DrawingReviewTarget, decision: DrawingReviewDecision, comment: string]
}>()

const markupText = ref('')
const viewName = ref('')
const markupSeverity = ref<'Note' | 'Blocking'>('Blocking')
const decisionComment = ref('')

const selectedPackageId = computed({
  get: () => props.packageId || props.packages[0]?.id || '',
  set: value => emit('update:packageId', value),
})
const activePackage = computed(() => props.packages.find(item => item.id === selectedPackageId.value) ?? props.packages[0])
const activeItem = computed(() => activePackage.value?.items.find(item => item.modelDocumentId === props.selectedDocumentId || item.drawingDocumentId === props.selectedDocumentId))
const target = computed<DrawingReviewTarget>(() => activeItem.value?.drawingDocumentId === props.selectedDocumentId ? 'Drawing2D' : 'Model3D')
const activeCreator = computed(() => target.value === 'Model3D' ? activeItem.value?.modelCreatedBy : activeItem.value?.drawingCreatedBy)
const activeTargetState = computed(() => target.value === 'Model3D' ? activeItem.value?.modelState : activeItem.value?.drawingState)
const activeRevision = computed(() => target.value === 'Model3D' ? activeItem.value?.modelRevision : activeItem.value?.drawingRevision)
const selfReview = computed(() => Boolean(activeCreator.value)
  && activeCreator.value!.localeCompare(props.currentUsername, undefined, { sensitivity: 'accent' }) === 0)
const canActOnTarget = computed(() => props.canDecide
  && activePackage.value?.state === 'InReview'
  && activeTargetState.value === 'Pending'
  && !selfReview.value)
const activeMarkups = computed(() => (activePackage.value?.markups ?? [])
  .filter(markup => markup.itemId === activeItem.value?.id && markup.target === target.value)
  .sort((left, right) => right.createdAt.localeCompare(left.createdAt)))
const markedTargetCount = computed(() => activePackage.value?.items.reduce((count, item) => count + Number(item.modelState === 'Marked') + Number(item.drawingState === 'Marked'), 0) ?? 0)
const totalTargetCount = computed(() => (activePackage.value?.items.length ?? 0) * 2)

function selectTarget(reviewTarget: DrawingReviewTarget) {
  const item = activeItem.value
  if (!item) return
  emit('selectDocument', reviewTarget === 'Model3D' ? item.modelDocumentId : item.drawingDocumentId)
  markupText.value = ''
  decisionComment.value = ''
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
  return ({ InReview: '审核中', ChangesRequested: '已退改', WritingProperties: '写入审核标记', Approved: '审核完成', Stale: '版本冲突' } as const)[state]
}

function targetStateLabel(state: DrawingReviewPackage['items'][number]['modelState']) {
  return ({ Pending: '待审核', ChangesRequested: '已退改', Approved: '待写标记', Marked: '已审核' } as const)[state]
}
</script>

<template>
  <aside class="drawing-review-panel pdm-panel" aria-label="图纸审核面板">
    <header class="drawing-review-panel__header">
      <div><span>3D / 2D 双审</span><h2>图纸审核</h2></div>
      <button type="button" class="drawing-review-close" aria-label="关闭图纸审核面板" @click="emit('close')">×</button>
    </header>

    <div class="drawing-review-panel__toolbar">
      <label v-if="packages.length"><History :size="14" /><span class="pdm-sr-only">审核历史</span><select v-model="selectedPackageId" aria-label="选择图纸审核单"><option v-for="review in packages" :key="review.id" :value="review.id">{{ review.number }} · {{ packageStateLabel(review.state) }}</option></select></label>
      <button type="button" title="刷新审核状态" :disabled="pending" @click="emit('refresh')"><RefreshCw :size="14" />刷新</button>
    </div>

    <section v-if="!activePackage" class="drawing-review-panel__empty">
      <MessageSquareText :size="34" />
      <strong>尚未发起图纸审核</strong>
      <p>发起后会自动冻结非标件的3D模型和关联2D工程图，二者都通过并写入属性后才可发布BOM。</p>
      <button v-if="canSubmit" type="button" class="is-primary" :disabled="pending" @click="emit('create')"><Send :size="15" />发起本项目图纸审核</button>
    </section>

    <template v-else>
      <section class="drawing-review-package-summary">
        <div><strong>{{ activePackage.number }}</strong><span :class="`is-${activePackage.state.toLowerCase()}`">{{ packageStateLabel(activePackage.state) }}</span></div>
        <p>{{ activePackage.items.length }}组图档 · {{ markedTargetCount }}/{{ totalTargetCount }}项完成 · 发起人 {{ activePackage.createdBy }}</p>
        <button v-if="canSubmit" type="button" :disabled="pending || activePackage.state === 'InReview' || activePackage.state === 'WritingProperties'" @click="emit('create')"><Send :size="14" />发起本项目图纸审核</button>
      </section>

      <section v-if="!activeItem" class="drawing-review-panel__selection-empty">
        <strong>当前图档未纳入此审核单</strong>
        <p>请在左侧设计树选择本审核单中的非标件3D模型或2D工程图；也可切换上方历史审核单。</p>
      </section>

      <template v-else>
        <section class="drawing-review-current">
          <div class="drawing-review-current__title"><span><strong>{{ activeItem.drawingNumber }}</strong><small>{{ activeItem.name }}</small></span><em>冻结版 {{ activeRevision }}</em></div>
          <div class="drawing-review-target-switch" aria-label="3D和2D审核切换">
            <button type="button" :class="{ 'is-active': target === 'Model3D' }" @click="selectTarget('Model3D')"><strong>3D模型</strong><span :class="`is-${activeItem.modelState.toLowerCase()}`">{{ targetStateLabel(activeItem.modelState) }}</span></button>
            <button type="button" :class="{ 'is-active': target === 'Drawing2D' }" @click="selectTarget('Drawing2D')"><strong>2D工程图</strong><span :class="`is-${activeItem.drawingState.toLowerCase()}`">{{ targetStateLabel(activeItem.drawingState) }}</span></button>
          </div>
          <p class="drawing-review-creator">设计者 <strong>{{ activeCreator }}</strong><span v-if="selfReview">本人设计，禁止自审</span></p>
        </section>

        <section class="drawing-review-panel__section">
          <div class="drawing-review-section-title"><PencilLine :size="15" /><strong>图形批注</strong></div>
          <div class="drawing-review-markup-tools">
            <button type="button" title="带引线文字" @click="activateMarkup('markup-text-leader')"><PencilLine :size="14" />引线</button>
            <button type="button" title="修订云线" @click="activateMarkup('markup-cloud')"><Cloud :size="14" />云线</button>
            <button type="button" title="矩形框" @click="activateMarkup('markup-rectangle')"><Square :size="14" />框选</button>
            <button type="button" title="自由曲线" @click="activateMarkup('markup-spline')"><RotateCcw :size="14" />手绘</button>
          </div>
          <p class="drawing-review-tool-tip">图形工具作用于当前eDrawings冻结版本；文字批注会作为正式审核记录保存。</p>
        </section>

        <section class="drawing-review-panel__section">
          <div class="drawing-review-section-title"><MessageSquareText :size="15" /><strong>{{ target === 'Model3D' ? '3D' : '2D' }}批注</strong></div>
          <div v-if="canAnnotate && activePackage.state === 'InReview'" class="drawing-review-markup-form">
            <div><select v-model="markupSeverity"><option value="Blocking">阻断问题</option><option value="Note">建议</option></select><input v-model="viewName" placeholder="视图/图纸页（可选）" /></div>
            <textarea v-model="markupText" rows="3" placeholder="填写尺寸、结构、工艺或表达问题…" />
            <button type="button" :disabled="pending" @click="addMarkup">保存批注</button>
          </div>
          <div class="drawing-review-markup-list">
            <article v-for="markup in activeMarkups" :key="markup.id" :class="[`is-${markup.severity.toLowerCase()}`, { 'is-resolved': markup.state === 'Resolved' }]">
              <header><strong>{{ markup.severity === 'Blocking' ? '阻断' : '建议' }}</strong><span>{{ markup.viewName || (target === 'Model3D' ? '当前3D视图' : '当前图纸') }}</span></header>
              <p>{{ markup.text }}</p>
              <footer><span>{{ markup.createdBy }}</span><button v-if="canAnnotate && markup.state === 'Open'" type="button" @click="emit('resolveMarkup', activePackage.id, markup.id)">关闭</button><span v-else>已关闭</span></footer>
            </article>
            <p v-if="!activeMarkups.length" class="drawing-review-no-markup">当前图档暂无批注</p>
          </div>
        </section>

        <section class="drawing-review-panel__section drawing-review-decision">
          <div class="drawing-review-section-title"><Check :size="15" /><strong>审核结论</strong></div>
          <textarea v-model="decisionComment" rows="2" placeholder="审核意见；退改时必填" />
          <p v-if="selfReview" class="drawing-review-self-warning">当前版本由你生成，系统禁止审核自己的图。</p>
          <div class="drawing-review-decision-buttons"><button type="button" class="is-reject" :disabled="pending || !canActOnTarget" @click="decide('RequestChanges')"><X :size="14" />退改</button><button type="button" class="is-approve" :disabled="pending || !canActOnTarget" @click="decide('Approve')"><Check :size="14" />通过</button></div>
        </section>
      </template>
    </template>
  </aside>
</template>

<style scoped>
.drawing-review-panel{min-width:0;min-height:0;height:100%;display:flex;flex-direction:column;overflow-x:hidden;overflow-y:auto;background:var(--pdm-surface)}
.drawing-review-panel__header{display:flex;align-items:center;justify-content:space-between;padding:12px 13px 9px;border-bottom:1px solid var(--pdm-border-soft)}
.drawing-review-panel__header span{color:var(--pdm-blue);font-size:9px;font-weight:700;letter-spacing:.12em}.drawing-review-panel__header h2{margin:2px 0 0;font-size:15px;font-weight:600}.drawing-review-close{width:28px;height:28px;border:0;border-radius:5px;background:transparent;color:var(--pdm-muted);font-size:20px}.drawing-review-close:hover{background:var(--pdm-surface-muted);color:var(--pdm-text)}
.drawing-review-panel__toolbar{display:flex;gap:5px;padding:8px 10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-panel__toolbar label{min-width:0;display:flex;flex:1;align-items:center;gap:5px}.drawing-review-panel__toolbar select{min-width:0;width:100%}.drawing-review-panel button,.drawing-review-panel select,.drawing-review-panel input,.drawing-review-panel textarea{font:inherit;font-size:10px}.drawing-review-panel button,.drawing-review-panel select{min-height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text)}.drawing-review-panel button{display:inline-flex;align-items:center;justify-content:center;gap:5px;padding:5px 8px}.drawing-review-panel button:disabled{opacity:.45;cursor:not-allowed}.drawing-review-panel .is-primary{border-color:var(--pdm-blue);background:var(--pdm-blue);color:white}
.drawing-review-panel__empty,.drawing-review-panel__selection-empty{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px;padding:28px 18px;color:var(--pdm-muted);text-align:center}.drawing-review-panel__empty{flex:1}.drawing-review-panel__empty strong,.drawing-review-panel__selection-empty strong{color:var(--pdm-text);font-size:12px}.drawing-review-panel__empty p,.drawing-review-panel__selection-empty p{margin:0;line-height:1.6;font-size:10px}
.drawing-review-package-summary{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-package-summary>div{display:flex;align-items:center;justify-content:space-between;gap:6px}.drawing-review-package-summary>div>strong{font-size:11px}.drawing-review-package-summary>div>span{padding:3px 6px;border-radius:8px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:9px}.drawing-review-package-summary>div>.is-approved{background:var(--pdm-green-soft);color:var(--pdm-green)}.drawing-review-package-summary>div>.is-changesrequested,.drawing-review-package-summary>div>.is-stale{background:#fff0ef;color:var(--pdm-danger)}.drawing-review-package-summary p{margin:5px 0 8px;color:var(--pdm-muted);font-size:9px}.drawing-review-package-summary>button{width:100%}
.drawing-review-current{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-current__title{display:flex;align-items:flex-start;justify-content:space-between;gap:7px}.drawing-review-current__title>span{min-width:0;display:flex;flex-direction:column}.drawing-review-current__title strong{font-size:11px}.drawing-review-current__title small,.drawing-review-current__title em{color:var(--pdm-muted);font-size:9px;font-style:normal}.drawing-review-target-switch{display:grid;grid-template-columns:1fr 1fr;gap:5px;margin-top:8px}.drawing-review-target-switch button{height:auto;display:flex;flex-direction:column;align-items:flex-start}.drawing-review-target-switch button.is-active{border-color:var(--pdm-blue);background:var(--pdm-blue-soft);color:var(--pdm-blue)}.drawing-review-target-switch span{font-size:9px;color:var(--pdm-muted)}.drawing-review-target-switch .is-marked{color:var(--pdm-green)}.drawing-review-target-switch .is-changesrequested{color:var(--pdm-danger)}.drawing-review-creator{display:flex;align-items:center;gap:5px;margin:7px 0 0;color:var(--pdm-muted);font-size:9px}.drawing-review-creator span{margin-left:auto;color:var(--pdm-orange);font-weight:600}
.drawing-review-panel__section{padding:10px;border-bottom:1px solid var(--pdm-border-soft)}.drawing-review-panel__section:last-child{border-bottom:0}.drawing-review-section-title{display:flex;align-items:center;gap:6px;margin-bottom:7px}.drawing-review-section-title strong{font-size:11px}.drawing-review-markup-tools{display:grid;grid-template-columns:1fr 1fr;gap:5px}.drawing-review-tool-tip{margin:6px 0 0;color:var(--pdm-muted);font-size:9px;line-height:1.5}.drawing-review-markup-form{display:flex;flex-direction:column;gap:5px}.drawing-review-markup-form>div{display:grid;grid-template-columns:90px 1fr;gap:5px}.drawing-review-markup-form select,.drawing-review-markup-form input,.drawing-review-markup-form textarea,.drawing-review-decision textarea{min-width:0;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:5px;padding:6px;background:var(--pdm-surface);color:var(--pdm-text);resize:vertical}.drawing-review-markup-form>button{border-color:#b9d3f7;background:var(--pdm-blue-soft);color:var(--pdm-blue)}
.drawing-review-markup-list{display:flex;flex-direction:column;gap:5px;margin-top:7px}.drawing-review-markup-list article{padding:7px;border-left:3px solid var(--pdm-orange);border-radius:4px;background:#fff9ec}.drawing-review-markup-list article.is-note{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}.drawing-review-markup-list article.is-resolved{opacity:.55}.drawing-review-markup-list header,.drawing-review-markup-list footer{display:flex;justify-content:space-between;gap:5px;color:var(--pdm-muted);font-size:9px}.drawing-review-markup-list p{margin:5px 0;color:var(--pdm-text);font-size:10px;line-height:1.45}.drawing-review-markup-list footer button{min-height:0;padding:0;border:0;background:transparent;color:var(--pdm-blue)}.drawing-review-no-markup{margin:7px 0;color:var(--pdm-muted);font-size:9px;text-align:center}
.drawing-review-decision{margin-top:auto}.drawing-review-decision textarea{width:100%}.drawing-review-self-warning{margin:6px 0;color:var(--pdm-orange);font-size:9px}.drawing-review-decision-buttons{display:grid;grid-template-columns:1fr 1fr;gap:5px;margin-top:6px}.drawing-review-decision-buttons .is-reject{border-color:#f2b8b5;background:#fff0ef;color:var(--pdm-danger)}.drawing-review-decision-buttons .is-approve{border-color:var(--pdm-green);background:var(--pdm-green);color:white}
@media(max-width:600px){.drawing-review-panel{height:auto;max-height:520px}}
</style>
