<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { decideReleaseDrawing, decideReleaseDrawingNode } from '../api'
import ApprovalNoteEditor from './ApprovalNoteEditor.vue'
import ReleaseReviewBomTable from './ReleaseReviewBomTable.vue'
import type { ReleaseDrawingReview, ReleasePackageSummary } from '../types'
import { userErrorMessage } from '../userErrorMessage'
import { drawingReviewStatus } from '../drawingReviewStatus'
const props = defineProps<{ release: ReleasePackageSummary; token: string; editable: boolean; emergency?: boolean; username?: string; embedded?: boolean; selectedDocumentId?: string; externalPreviewReady?: boolean }>()
const emit = defineEmits<{ updated: [release: ReleasePackageSummary]; selectDocument: [id: string] }>()
const review = ref<ReleaseDrawingReview | null>(), selectedId = ref(''), filter = ref('all'), query = ref('')
const pending = ref(false), error = ref(''), comment = ref('')
const canConfirmPreview = computed(() => props.embedded && props.externalPreviewReady === true)
const labels: Record<string,string> = { Pending: '待确认', Confirmed: '已确认', Issue: '待修改' }
watch(() => [props.release.id, props.release.drawingReview] as const, ([id,value], old) => {
  if (id !== old?.[0] || !review.value || (value?.revision ?? 0) >= review.value.revision) review.value = value
  if (!review.value?.items.some(i => i.documentId === selectedId.value)) selectedId.value = review.value?.items[0]?.documentId ?? ''
}, { immediate: true })
watch(() => props.selectedDocumentId, id => { if(props.embedded) selectedId.value=id ?? '' }, { immediate:true })
watch(selectedId, id=>{if(props.embedded)emit('selectDocument',id)})
const currentTask = computed(() => props.release.steps.find(s => s.status === 'current' && s.id !== 'production-release'))
const businessNote = ref<string>(), notice = ref('')
const all = computed(() => (review.value?.items ?? []).map(item => {
  const own = drawingReviewStatus(item, review.value?.roundId, currentTask.value?.id, props.username)
  return { ...item, status: own.latest, hasIssue: own.hasIssue }
}))
watch(() => currentTask.value?.id, () => { comment.value = '' })
const items = computed(() => all.value.filter(i => (!query.value || i.fileName.toLocaleLowerCase().includes(query.value.toLocaleLowerCase()))
  && (filter.value === 'all' || (filter.value === 'Issue' ? i.hasIssue : i.status === filter.value))))
const selected = computed(() => all.value.find(i => i.documentId === selectedId.value))
const linkedBom = computed(() => ['草稿','已退回'].includes(props.release.state) ? [] : props.release.nonStandardBomSnapshot.filter(i => i.id && selected.value?.bomItemIds.includes(i.id)))
const counts = computed(() => ({ pending: all.value.filter(i => i.required && i.status === 'Pending').length,
  confirmed: all.value.filter(i => i.status === 'Confirmed').length, issues: all.value.filter(i => i.hasIssue).length }))
const inReview = computed(() => ['审批中','工艺审核','待批准'].includes(props.release.state))
const canDecide = computed(() => props.editable && inReview.value && !!currentTask.value && !pending.value)
const canUndo = computed(() => selected.value?.status === 'Issue')
const canReturn = computed(() => canDecide.value && !!selected.value && (canUndo.value || !selected.value.hasIssue))
const cannotApprove = computed(() => {
  if (!canDecide.value) return '当前没有可处理的审批任务。'
  if (!all.value.length) return ''
  if (!selected.value) return '请先选择图纸。'
  if (selected.value.hasIssue) return '请等待设计修改并存档，或撤回本人的退回。'
  if (selected.value.status === 'Confirmed')
    return counts.value.pending || counts.value.issues ? '当前图纸已确认，请继续处理尚未完成的图纸。' : ''
  return canConfirmPreview.value ? '' : '请在 Windows 客户端使用 eDrawings 加载原图。'
})
watch([() => props.release.id, () => selected.value?.documentId, () => selected.value?.token], () => { comment.value = '' })
function receive(value: ReleasePackageSummary) {
  const before = currentTask.value
  if (value.state === '已退回') notice.value = '审核不通过，发布单已退回修改。'
  else if (before && value.steps.find(s => s.id === before.id)?.status !== 'current') notice.value = value.state === '发布中' ? '批准已完成，系统正在继续转图和发布。' : '当前节点已完成，已自动流转至下一节点。'
  else if (before && value.steps.find(s => s.id === before.id)?.participants?.signatures.some(s => s.actor.toLowerCase() === props.username?.toLowerCase())) notice.value = '您的审核已完成，等待本节点其他审批人。'
  review.value = value.drawingReview; emit('updated', value)
}
async function approve() {
  if (cannotApprove.value || !currentTask.value) return
  if (!all.value.length || (selected.value?.status === 'Confirmed' && !counts.value.pending && !counts.value.issues)) { await completeNode(); return }
  const drawing = selected.value
  if (!drawing || drawing.status === 'Confirmed') return
  if (!canConfirmPreview.value) { error.value = '请先使用 eDrawings 加载本次送审原图，再提交通过。'; return }
  const packageId = props.release.id, taskId = currentTask.value.id, roundId = review.value?.roundId
  pending.value = true; error.value = ''
  try {
    const value = await decideReleaseDrawing(packageId, { documentId: drawing.documentId, token: drawing.token, status: 'Confirmed',
      approvalTaskId: taskId, roundId, businessNote: businessNote.value, comment: comment.value.trim(), emergencyReason: props.emergency ? comment.value.trim() || undefined : undefined }, props.token, props.emergency)
    if (props.release.id !== packageId || review.value?.roundId !== roundId) { emit('updated', value); return }
    receive(value)
    comment.value = ''
    await nextTick()
    // 同一节点继续审核尚未通过的必审图纸；节点流转及会签仍由服务端决定。
    if (currentTask.value?.id === taskId && selectedId.value === drawing.documentId) {
      const index = all.value.findIndex(i => i.documentId === drawing.documentId)
      const next = [...all.value.slice(index + 1), ...all.value.slice(0, index)].find(i => i.required && i.status !== 'Confirmed' && !i.hasIssue)
      if (next) { query.value = ''; filter.value = 'all'; selectedId.value = next.documentId; await nextTick() }
    }
  } catch (e) { error.value = userErrorMessage(e instanceof Error ? e.message : '') }
  finally { pending.value = false }
}
async function returnDrawing() {
  const drawing = selected.value
  const task = currentTask.value
  if (!canReturn.value || !drawing || !task) return
  const undo = canUndo.value
  const reason = comment.value.trim()
  if (!undo && !reason) {
    error.value = '请填写退回原因。'
    return
  }
  pending.value = true
  error.value = ''
  try {
    receive(await decideReleaseDrawing(props.release.id, {
      documentId: drawing.documentId, token: drawing.token, status: undo ? 'Pending' : 'Issue',
      approvalTaskId: task.id, roundId: review.value?.roundId, comment: undo ? '' : reason,
      emergencyReason: props.emergency && !undo ? reason : undefined,
    }, props.token, props.emergency))
    comment.value = ''
    notice.value = undo ? '已撤回本次退回。' : '当前图纸已退回，存档新版本后自动进入复审。'
  } catch (e) {
    error.value = userErrorMessage(e instanceof Error ? e.message : '')
  } finally {
    pending.value = false
  }
}

async function completeNode() {
  if (!currentTask.value || !canDecide.value) return
  pending.value = true; error.value = ''
  try { receive(await decideReleaseDrawingNode(props.release.id, { approvalTaskId: currentTask.value.id, roundId: review.value?.roundId, decision: 'Approved', comment: comment.value, businessNote: businessNote.value, emergencyReason: props.emergency ? comment.value.trim() || undefined : undefined }, props.token, props.emergency)) }
  catch (e) { error.value = userErrorMessage(e instanceof Error ? e.message : '') }
  finally { pending.value = false }
}
function move(offset: number) {
  if (pending.value) return
  const index = items.value.findIndex(i => i.documentId === selectedId.value)
  const next = items.value[index + offset]
  if (next) selectedId.value = next.documentId
}
</script>

<template>
  <section class="release-drawing-review" :class="{ 'is-embedded': embedded }" aria-label="图档审核" :aria-busy="pending">
    <p v-if="['草稿','已退回'].includes(release.state)" class="review-hint">以下为上一轮冻结图纸及审核记录；重新提交时会更新审核范围，变更内容须重新确认。</p>
    <p v-if="release.state === '发布失败'" class="review-error" role="alert">{{ userErrorMessage(release.publishError || release.previewError || '发布未完成，请在发布记录中查看原因并重试。') }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p class="review-package-heading"><span>发布包：{{ release.number }}</span><span v-if="currentTask && inReview">当前节点：{{ currentTask.stage }}</span><span v-else>状态：{{ release.state }}</span></p>
    <ApprovalNoteEditor v-model="businessNote" label="发布备注" :identity="release.id + currentTask?.id" :nodes="release.steps.map(s => s.participants)" :editable="!!(editable && !emergency && currentTask?.participants?.policy?.editBusinessNote)" />
    <p v-if="error" class="review-error" role="alert">{{ error }}</p>
    <div class="review-decision">
      <textarea v-model="comment" rows="2" maxlength="500" placeholder="审核意见（选填），退回时须填写原因" aria-label="图纸审核意见" :disabled="!canDecide" />
      <div class="review-actions">
        <button type="button" class="pdm-primary-action" :disabled="!!cannotApprove" :title="cannotApprove" @click="approve">通过</button>
        <button type="button" :disabled="!canReturn" @click="returnDrawing">{{ canUndo ? '撤回退回' : '退回' }}</button>
        <span class="review-navigation"><button type="button" :disabled="pending || !selected || items.findIndex(i => i.documentId === selectedId) >= items.length - 1" @click="move(1)">下一张</button><button type="button" :disabled="pending || !selected || items.findIndex(i => i.documentId === selectedId) <= 0" @click="move(-1)">上一张</button></span>
      </div>
    </div>
    <p v-if="selected?.hasIssue" class="review-hint">待修改，存档新版本后自动复审。</p>
    <details class="review-notes"><summary title="BOM按冻结明细行统计；图纸按送审文件去重，一张图纸可对应多条BOM明细。">冻结BOM（{{ release.nonStandardBomSnapshot.length }}条明细／{{ all.length }}张图纸）</summary><div class="review-full-bom"><ReleaseReviewBomTable :rows="release.nonStandardBomSnapshot" :multiplier="release.wholeSetMultiplier" label="本次冻结BOM明细" /></div></details>
    <div v-if="all.length" class="review-workspace">
      <aside v-if="!embedded">
        <div class="review-filters"><input v-model="query" type="search" placeholder="图号/名称" aria-label="搜索审核图纸"><select v-model="filter" aria-label="图纸审核状态"><option value="all">全部图纸</option><option value="Pending">待确认</option><option value="Confirmed">已确认</option><option value="Issue">待修改</option></select></div>
        <div class="review-drawing-list"><button v-for="item in items" :key="item.documentId" type="button" :class="{ active: selectedId === item.documentId }" :disabled="pending" @click="selectedId = item.documentId"><strong>{{ item.fileName }}</strong><span>{{ item.required ? '必审' : '已发布参照' }} · {{ labels[item.status] }} · {{ item.bomItemIds.length }} 项BOM</span></button><p v-if="!items.length">没有匹配的图纸。</p></div>
        <div v-if="selected" class="review-linked-bom"><strong>对应冻结BOM</strong><ReleaseReviewBomTable :rows="linkedBom" :multiplier="release.wholeSetMultiplier" label="当前图纸对应BOM明细" /></div>
      </aside>
      <main v-if="selected">
        <div v-if="!embedded" class="review-preview-toolbar"><strong>{{ selected.fileName }}</strong></div>
        <p v-if="!embedded" class="review-hint">请在 Windows 客户端的图档页面使用 eDrawings 查看送审原图并完成审核。</p>
        <div v-if="embedded" class="review-linked-bom"><ReleaseReviewBomTable :rows="linkedBom" :multiplier="release.wholeSetMultiplier" label="当前图纸对应BOM明细" /></div>
      </main>
    </div>
    <p v-else>本次发布没有需要审核的图纸。</p>
    <details v-if="selected?.notes.length" class="review-notes"><summary>审核记录（{{ selected.notes.length }}）</summary><p v-for="(note,index) in selected.notes" :key="index"><strong>{{ note.actorName || '姓名待补充' }}</strong> · {{ note.status === 'Pending' ? '撤回退回' : labels[note.status] || note.status }} · {{ new Date(note.at).toLocaleString('zh-CN', {hour12: false}) }}<span v-if="note.versionId !== selected.versionId">（历史版本）</span><br>{{ note.comment || '无补充意见' }}</p></details>
    <details v-if="review?.archivedItems?.length" class="review-notes"><summary>已移出本次范围的审核记录（{{ review.archivedItems.length }}张）</summary><div v-for="item in review.archivedItems" :key="item.documentId"><strong>{{ item.fileName }}</strong><p v-for="(note,index) in item.notes" :key="index">{{ note.actorName || '姓名待补充' }} · {{ labels[note.status] || note.status }} · {{ new Date(note.at).toLocaleString('zh-CN', {hour12: false}) }}<br>{{ note.comment || '无补充意见' }}</p></div></details>
  </section>
</template>

<style scoped>
:global(:root body section.release-drawing-review.is-embedded){box-sizing:border-box;width:100%;max-width:100%;margin:0!important;padding:10px!important;border-radius:0!important;box-shadow:none!important;background:transparent}
.is-embedded .review-workspace{display:block}.is-embedded .review-decision>div{flex-wrap:wrap}.is-embedded .review-hint{margin:8px 0;line-height:1.5}.is-embedded .review-full-bom{font-size:11px}.is-embedded .review-full-bom td{overflow-wrap:anywhere}.is-embedded .review-empty{padding:8px 0}.is-embedded .review-linked-bom{margin:8px 0}

:global(:root body section.release-drawing-review){margin:12px 0;background:var(--pdm-surface);border:0!important;border-radius:var(--pdm-stat-card-radius)!important;padding:16px;box-shadow:var(--pdm-stat-card-shadow)!important}
.release-drawing-review header,.review-preview-toolbar{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap}
.release-drawing-review header span,.review-hint{font-size:12px;color:var(--pdm-muted,#64748b)}.review-error{color:var(--pdm-danger,#d22)}
.review-package-heading{display:grid;gap:4px;overflow-wrap:anywhere}
.review-workspace{display:grid;grid-template-columns:minmax(240px,30%) minmax(0,1fr);gap:12px}.review-workspace aside,.review-workspace main{min-width:0}
.review-filters{display:grid;grid-template-columns:minmax(0,3fr) minmax(0,2fr);gap:6px}.review-filters input,.review-filters select{width:100%;min-width:0;box-sizing:border-box;border:1px solid var(--pdm-border);background:var(--pdm-surface);color:var(--pdm-text);padding:0 8px;font:inherit}
.review-drawing-list{max-height:340px;overflow:auto;margin:8px 0}.review-drawing-list button{display:flex;flex-direction:column;gap:4px;width:100%;height:auto;min-height:54px;padding:8px;text-align:left;border:0;border-radius:8px;background:transparent;align-items:flex-start}.review-drawing-list button.active{background:var(--pdm-accent-soft,#e5f8f1);color:var(--pdm-accent,#009e76)}.review-drawing-list strong{overflow-wrap:anywhere}.review-drawing-list span{font-size:12px}
.review-full-bom{max-height:260px;overflow:auto}.review-full-bom table{width:100%}.review-linked-bom{max-height:200px;overflow:auto}.review-linked-bom table{width:100%}.review-linked-bom th,.review-linked-bom td{text-align:center;padding:6px}.review-linked-bom small{display:block;overflow-wrap:anywhere}
.review-preview-toolbar strong{overflow-wrap:anywhere;max-width:65%;font-size:13px}.review-decision .review-actions{display:flex;flex-wrap:wrap;gap:8px}.review-navigation{display:flex;gap:8px}
.review-pdf{display:flex;flex-direction:column;height:510px;min-height:300px;margin:8px 0;background:var(--pdm-background,#f5f6f8);overflow:hidden}.review-empty{padding:32px 12px;text-align:center}.review-empty button{margin:4px}.review-decision{display:grid;gap:8px}.review-decision textarea{width:100%;min-width:0;max-width:100%;height:50px;min-height:50px;resize:vertical;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:8px}.review-notes{margin-top:12px;font-size:12px}.review-notes p{overflow-wrap:anywhere}
.release-drawing-review button:not(.review-drawing-list button){width:80px;height:28px;border-radius:999px}.review-filters input,.review-filters select,.review-decision input{height:28px;border-radius:999px}.review-filters input{box-shadow:none!important}
@media(max-width:800px){.review-workspace{grid-template-columns:1fr}.review-drawing-list{max-height:160px}.review-linked-bom{max-height:140px}.review-pdf{height:460px}.review-preview-toolbar strong{max-width:100%}.release-drawing-review{padding:10px}}
.is-embedded .review-decision{grid-template-columns:minmax(0,1fr) 80px;align-items:start}
.is-embedded .review-decision textarea{height:136px;min-height:50px}
.is-embedded .review-decision .review-actions{grid-column:2;display:flex;flex-direction:column;flex-wrap:nowrap;gap:8px}
.is-embedded .review-navigation{flex-direction:column;gap:8px}
</style>
