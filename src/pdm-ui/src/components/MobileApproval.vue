<script setup lang="ts">
import PersonName from './PersonName.vue'
import { useUserDisplayName as usePersonDisplayName } from '../userDisplay'
const displayUserName = usePersonDisplayName()
import ApprovalNoteEditor from './ApprovalNoteEditor.vue'
import { canSignNode } from '../approvalParticipants'
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref } from 'vue'
import { decideApproval, decideValidationPlanApproval, listReleasePackages, readDocumentPreviewFile, readProjectValidationPlan, downloadValidationPlanAttachment } from '../api'
import type { MyApprovalTask, ProjectValidationPlan, ReleasePackageSummary } from '../types'
import { userErrorMessage } from '../userErrorMessage'
import { useProjectCenterAccess } from '../projectCenterAccess'
const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const props = defineProps<{ task: MyApprovalTask; token: string; username: string; permissions: string[] }>()
const emit = defineEmits<{ done: []; back: [] }>()
const release = ref<ReleasePackageSummary>(), plan = ref<ProjectValidationPlan>(), loading = ref(true), pending = ref(false)
const error = ref(''), comment = ref(''), preview = ref(''), previewName = ref('')
const scope = props.task.qualityAcceptance ? '/quality' : ''
const { write: projectWritable } = useProjectCenterAccess()
const active = computed(() => projectWritable.value && (props.permissions.includes('approval.decide') || props.task.kind === 'validationPlan' && props.permissions.includes('validation-plan.edit')) && (props.task.kind === 'validationPlan'
  ? plan.value?.id === props.task.validationPlanId && plan.value?.approvalTasks.some(task => task.id === props.task.id && canSignNode(task, props.username) && task.decision == null)
  : release.value?.steps.some(step => step.id === props.task.id && step.status === 'current' && (canSignNode(step, props.username) || !step.participants && props.username.toLowerCase() === 'admin'))))
const note = ref<string>()
const nodes = computed(() => plan.value?.approvalTasks ?? release.value?.steps ?? [])
const editNote = computed(() => nodes.value.find(node => node.id === props.task.id)?.participants?.policy?.editBusinessNote === true)
const bindings = computed(() => release.value?.versionBindings ?? [])
const items = computed(() => [...(release.value?.standardBomSnapshot ?? []), ...(release.value?.nonStandardBomSnapshot ?? []), ...(release.value?.electricalBomSnapshot ?? [])])
function closePreview() { if (preview.value) URL.revokeObjectURL(preview.value); preview.value = '' }
async function load() {
  loading.value = true; error.value = ''
  try {
    if (props.task.kind === 'validationPlan') plan.value = await readProjectValidationPlan(props.task.projectId, props.token, scope) ?? undefined
    else release.value = (await listReleasePackages(props.task.projectId, props.token)).find(item => item.id === props.task.releasePackageId)
    if (!plan.value && !release.value) error.value = '该审批资料已变化或不可访问，请返回待办刷新。'
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '审批资料读取失败，请重试。') }
  finally { loading.value = false }
}
async function decide(decision: 'Approved' | 'Rejected') {
  if (!active.value || pending.value || loading.value) return
  if (decision === 'Rejected' && !comment.value.trim()) { error.value = '退回时请填写处理意见。'; return }
  pending.value = true; error.value = ''
  try {
    if (props.task.kind === 'validationPlan') await decideValidationPlanApproval(props.task.id, decision, comment.value.trim(), props.token, scope, editNote.value ? note.value : undefined)
    else await decideApproval(props.task.id, decision, comment.value.trim(), props.token, editNote.value ? note.value : undefined)
    comment.value = ''; emit('done')
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '审批结果未确认，请刷新待办核对后重试。') }
  finally { pending.value = false }
}
async function showDrawing(binding: (typeof bindings.value)[number]) {
  if (pending.value) return
  pending.value = true; error.value = ''
  try {
    const blob = await readDocumentPreviewFile(binding.documentId, binding.versionId, props.token)
    closePreview(); previewName.value = `${binding.fileName || binding.documentId} · ${binding.revision}`
    preview.value = URL.createObjectURL(new Blob([blob], { type: 'application/pdf' }))
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '当前审批版本暂无可用预览，请在电脑端核对资料。') }
  finally { pending.value = false }
}
async function attachment(id: string, name: string) {
  if (pending.value) return
  pending.value = true; error.value = ''
  try { await downloadValidationPlanAttachment(id, name, props.token, scope) }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '审批附件读取失败，请重试。') }
  finally { pending.value = false }
}
defineExpose({ canLeave: () => !pending.value && !comment.value.trim() })
onMounted(load)
onBeforeUnmount(closePreview)
</script>
<template>
  <section class="mobile-approval" aria-label="审批详情" :aria-busy="loading || pending">
    <button :disabled="pending" @click="emit('back')">返回待办</button>
    <h2>{{ task.projectCode }} · {{ task.projectName }}</h2>
    <p>{{ task.releasePackageNumber || `${task.qualityAcceptance ? '验收' : '验证'}计划 第${task.validationPlanRevision}版` }} · {{ task.stepName || task.stage }}</p>
    <p v-if="loading" role="status">正在读取审批资料…</p>
    <p v-if="error" role="alert" class="mobile-error">{{ error }} <button :disabled="pending" @click="load">刷新</button></p>
    <template v-if="release">
      <h3>审批流程</h3><ol><li v-for="step in release.steps" :key="step.id">{{ step.stage }} · <PersonName :name="displayUserName(step.assignee)" /> · {{ step.detail }}<p v-if="step.comment">处理意见：{{ step.comment }}</p></li></ol>
      <p v-if="release.changeReason">变更原因：{{ release.changeReason }}</p>
      <h3>本次审批的图档版本</h3><p v-if="!bindings.length">本次审批未绑定图档。</p>
      <article v-for="binding in bindings" :key="binding.documentId" class="mobile-record"><strong>{{ binding.fileName || binding.documentId }}</strong><p>版本 {{ binding.revision }}</p><button v-if="/\.slddrw$|\.pdf$/i.test(binding.fileName || '')" :disabled="pending" @click="showDrawing(binding)">查看此版本图纸</button></article>
      <h3>BOM审批内容（{{ items.length }}项）</h3><article v-for="(item, index) in items" :key="`${item.id}-${index}`" class="mobile-record"><strong>{{ item.drawingNumber }} · {{ item.name }}</strong><p>数量 {{ item.quantity }}</p></article>
    </template>
    <template v-if="plan">
      <h3>计划内容 · 第{{ plan.revisionNumber }}版</h3>
      <article v-for="item in plan.items" :key="item.id" class="mobile-record"><strong>{{ item.categoryName }} · {{ item.validationContent }}</strong><p>{{ item.validationStandard }}</p><p v-if="item.result">结果：{{ item.result }}</p></article>
      <h3>审批附件</h3><button v-for="file in plan.attachments" :key="file.id" :disabled="pending" @click="attachment(file.id, file.originalFileName)">{{ file.originalFileName }} · 第{{ file.fileVersion }}版</button>
      <ol><li v-for="step in plan.approvalTasks" :key="step.id">{{ step.stepName }} · <PersonName :name="displayUserName(step.assignee)" /><p v-if="step.decisionComment">{{ step.decisionComment }}</p></li></ol>
    </template>
    <ApprovalNoteEditor v-model="note" :identity="task.id" :label="plan ? '计划备注' : '发布备注'" :nodes="nodes.map(node => node.participants)" :editable="!!active && editNote" />
    <form v-if="active" class="mobile-approval-actions" @submit.prevent="decide('Approved')">
      <label>处理意见<textarea v-model="comment" aria-label="审批处理意见" rows="3" maxlength="500" :disabled="pending" /></label>
      <button type="submit" class="pdm-primary-action" :disabled="pending">{{ pending ? '正在处理…' : '通过审批' }}</button>
      <button type="button" :disabled="pending" @click="decide('Rejected')">退回修改</button>
      <button v-if="comment" type="button" :disabled="pending" @click="comment = ''">清空意见</button>
    </form>
    <p v-else-if="!loading && !error">当前事项已处理、尚未轮到当前账号，或当前账号没有审批权限，请返回待办刷新。</p>
    <el-dialog :model-value="!!preview" :title="previewName" width="100%" fullscreen destroy-on-close @close="closePreview"><PdfDrawingViewer v-if="preview" :url="preview" :fill-height="true" /></el-dialog>
  </section>
</template>
