<script setup lang="ts">
import PersonName from './PersonName.vue'
import ParallelApprovalStatus from './ParallelApprovalStatus.vue'
import { computed, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { getExpenseRequests, saveExpenseRequest, previewExpenseRequest, decideExpenseRequest, withdrawExpenseRequest } from '../api'
import { createClientUuid } from '../clientId'
import { useUserDisplayName } from '../userDisplay'
import { userErrorMessage } from '../userErrorMessage'
import { requiresWholeQuantity } from '../u9Units'
import { expenseLineAmount, expensePurpose, expenseStatus } from '../expenseRequests'
import type { ExpenseDirectory, ExpenseLine, ExpenseView, SaveExpense } from '../expenseRequests'
import QuantityInputNumber from './QuantityInputNumber.vue'

const props = defineProps<{ projectId: string; token?: string; requestedId?: string }>()
const emit = defineEmits<{ dirty: [value: boolean]; updated: [] }>()
const displayName = useUserDisplayName()
const directory = ref<ExpenseDirectory | null>(null)
const selected = ref<ExpenseView | null>(null)
const draft = ref<SaveExpense | null>(null)
const draftId = ref('')
const baseline = ref('')
const busy = ref(false)
const error = ref('')
const comment = ref('')
const businessNote = ref('')
const route = ref<string[]>([])
const dirty = computed(() => !!draft.value && JSON.stringify(draft.value) !== baseline.value)
const total = computed(() => {
  const lines = draft.value?.lines ?? selected.value?.request.lines ?? []
  return lines.length && lines.every(line => expenseLineAmount(line) != null) ? Math.round(lines.reduce((sum, line) => sum + expenseLineAmount(line)!, 0) * 100) / 100 : null
})
const money = (value: number | null) => value == null ? '待填写' : value.toLocaleString('zh-CN', { style: 'currency', currency: 'CNY' })
const blankLine = (): ExpenseLine => ({ kind: 'Material', materialCode: '', name: '', specification: '', brand: '', unit: '个', quantity: null, unitPrice: null, note: '' })
let loadSequence = 0

async function load() {
  const sequence = ++loadSequence
  if (!props.token) return
  busy.value = true
  error.value = ''
  try {
    const result = await getExpenseRequests(props.projectId, props.token)
    if (sequence !== loadSequence) return
    directory.value = result
    const targetId = props.requestedId || selected.value?.request.id
    const target = result.requests.find(item => item.request.id.replaceAll('-', '') === targetId?.replaceAll('-', ''))
    if (target && !draft.value) select(target)
  } catch (cause) { if (sequence === loadSequence) error.value = userErrorMessage(cause instanceof Error ? cause.message : '费用申请加载失败，请刷新后重试。') }
  finally { if (sequence === loadSequence) busy.value = false }
}
function select(view: ExpenseView) {
  selected.value = view
  draft.value = null
  route.value = []
  comment.value = ''
  businessNote.value = view.request.reason
  error.value = ''
}
function edit(view?: ExpenseView) {
  selected.value = view ?? null
  draftId.value = view?.request.id ?? createClientUuid()
  const request = view?.request
  draft.value = { purpose: request?.purpose ?? 'SitePurchase', department: request?.department ?? 'Procurement', requiredOn: request?.requiredOn ?? null,
    reason: request?.reason ?? '', lines: request ? request.lines.map(line => ({ ...line })) : [blankLine()], expectedRowVersion: request?.rowVersion ?? 0, submit: false }
  baseline.value = JSON.stringify(draft.value)
  route.value = []
  error.value = ''
}
async function closeDraft() {
  if (dirty.value) {
    try { await ElMessageBox.confirm('本次填写尚未保存，确认关闭申请单？', '关闭申请', { confirmButtonText: '关闭', cancelButtonText: '继续填写' }) }
    catch { return }
  }
  draft.value = null
}
async function save(submit: boolean) {
  if (!draft.value || !props.token || busy.value) return
  const projectId = props.projectId
  const sequence = loadSequence
  busy.value = true
  error.value = ''
  try {
    const saved = await saveExpenseRequest(projectId, draftId.value, { ...draft.value, submit }, props.token)
    if (sequence !== loadSequence) return
    select(saved)
    await load()
    emit('updated')
  } catch (cause) { if (sequence === loadSequence) error.value = userErrorMessage(cause instanceof Error ? cause.message : '费用申请保存失败，请刷新后重试。') }
  finally { if (sequence === loadSequence || projectId === props.projectId) busy.value = false }
}
async function preview() {
  if (!draft.value || !props.token || busy.value) return
  const sequence = loadSequence
  busy.value = true
  error.value = ''
  try {
    const result = await previewExpenseRequest(props.projectId, draft.value, props.token)
    if (sequence === loadSequence) route.value = result.steps
  } catch (cause) { if (sequence === loadSequence) error.value = userErrorMessage(cause instanceof Error ? cause.message : '审批路径读取失败，请重试。') }
  finally { if (sequence === loadSequence) busy.value = false }
}
async function decide(approve: boolean) {
  if (!selected.value || !props.token || busy.value) return
  const sequence = loadSequence
  const request = selected.value.request
  busy.value = true
  error.value = ''
  try {
    const saved = await decideExpenseRequest(props.projectId, request.id, { expectedRowVersion: request.rowVersion, approve, comment: comment.value,
      businessNote: request.currentStep?.participants.policy?.editBusinessNote ? businessNote.value : undefined }, props.token)
    if (sequence !== loadSequence) return
    select(saved)
    await load()
    emit('updated')
  } catch (cause) { if (sequence === loadSequence) error.value = userErrorMessage(cause instanceof Error ? cause.message : '审批操作失败，请刷新后重试。') }
  finally { busy.value = false }
}
async function withdraw() {
  if (!selected.value?.canWithdraw || !props.token || busy.value) return
  const request=selected.value.request, projectId=props.projectId, sequence=loadSequence
  try { await ElMessageBox.confirm('撤回后当前审批待办将关闭，已处理记录保留。修改后可以重新提交。', '撤回费用申请', { confirmButtonText:'确认撤回', cancelButtonText:'取消', type:'warning' }) }
  catch { return }
  if (sequence !== loadSequence || selected.value?.request.id !== request.id) return
  busy.value=true; error.value=''
  try {
    const saved=await withdrawExpenseRequest(projectId,request.id,request.rowVersion,props.token)
    if(sequence !== loadSequence)return
    select(saved);await load();emit('updated')
  } catch(cause) { if(sequence === loadSequence)error.value=userErrorMessage(cause instanceof Error ? cause.message : '撤回失败，请刷新后重试。') }
  finally { if(projectId === props.projectId)busy.value=false }
}
watch(dirty, value => emit('dirty', value))
watch(draft, () => { route.value = [] }, { deep: true })
watch([() => props.projectId, () => Boolean(props.token)], () => { directory.value = null; selected.value = null; draft.value = null; void load() }, { immediate: true })
watch(() => props.requestedId, () => { if (!draft.value) void load() })
</script>

<template>
  <section class="expense-requests" aria-label="费用申请" v-loading="busy">
    <header class="expense-toolbar">
      <strong>{{ directory?.projectCode }} {{ directory?.projectName }}</strong>
      <div><button v-if="directory?.canCreate && !draft" type="button" class="pdm-primary-action" :disabled="busy" @click="edit()">新增费用申请</button>
      <button type="button" class="pdm-secondary-action" :disabled="busy || !!draft" @click="load">刷新</button></div>
    </header>
    <p v-if="error" role="alert" class="pdm-inline-error">{{ error }}</p>
    <form v-if="draft" class="pdm-panel expense-form" @submit.prevent="save(true)">
      <fieldset :disabled="busy" class="expense-fields">
        <label>申请用途<select v-model="draft.purpose" aria-label="申请用途"><option value="SitePurchase">现场采购</option><option value="RepairSupplement">维修补订</option></select></label>
        <label>审批部门<select v-model="draft.department" aria-label="审批部门"><option value="Procurement">采购部</option><option value="SupplyChain">供应链</option></select></label>
        <label>需求日期<span class="required">*</span><input :value="draft.requiredOn" @input="draft.requiredOn = ($event.target as HTMLInputElement).value || null" aria-label="需求日期" type="date" required /></label>
        <label class="expense-reason">申请原因<span class="required">*</span><textarea v-model="draft.reason" aria-label="申请原因" maxlength="2000" rows="2" required /></label>
      </fieldset>
      <div class="expense-table-scroll"><table class="expense-lines">
        <thead><tr><th>类型</th><th>物料编码</th><th>物料或服务名称 *</th><th>规格／服务内容</th><th>品牌</th><th>单位 *</th><th>数量 *</th><th>预计单价（元）*</th><th>预计金额</th><th>备注</th><th>操作</th></tr></thead>
        <tbody><tr v-for="(line, index) in draft.lines" :key="index">
          <td><select v-model="line.kind" :aria-label="`第${index + 1}行类型`" :disabled="busy"><option value="Material">物料</option><option value="Service">服务</option></select></td>
          <td><input v-model="line.materialCode" :aria-label="`第${index + 1}行物料编码`" maxlength="120" :disabled="busy" /></td>
          <td><input v-model="line.name" :aria-label="`第${index + 1}行名称`" maxlength="200" required :disabled="busy" /></td>
          <td><input v-model="line.specification" :aria-label="`第${index + 1}行规格或服务内容`" maxlength="1000" :disabled="busy" /></td>
          <td><input v-model="line.brand" :aria-label="`第${index + 1}行品牌`" maxlength="100" :disabled="busy" /></td>
          <td><input v-model="line.unit" :aria-label="`第${index + 1}行单位`" maxlength="30" required :disabled="busy" /></td>
          <td><QuantityInputNumber v-model="line.quantity" :aria-label="`第${index + 1}行数量`" :min="requiresWholeQuantity(line.unit) ? 1 : 0.0001" :max="100000000" :controls="false" :disabled="busy" /></td>
          <td><input :value="line.unitPrice" @input="line.unitPrice = ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value)" :aria-label="`第${index + 1}行预计单价`" type="number" step="0.01" min="0" max="100000000" required :disabled="busy" /></td>
          <td>{{ money(expenseLineAmount(line)) }}</td>
          <td><input v-model="line.note" :aria-label="`第${index + 1}行备注`" maxlength="500" :disabled="busy" /></td>
          <td><button type="button" class="pdm-text-action" :disabled="busy || draft.lines.length <= 1" @click="draft.lines.splice(index, 1)">删除</button></td>
        </tr></tbody>
      </table></div>
      <div class="expense-toolbar"><button type="button" class="pdm-secondary-action" :disabled="busy || draft.lines.length >= 500" @click="draft.lines.push(blankLine())">新增明细</button><strong>预计总金额 {{ money(total) }}</strong></div>
      <p>审批节点和条件按所选部门的审批设置执行，可预览审批流程。</p>
      <p v-if="directory?.departments.find(item => item.code === draft!.department)?.configured === false" role="status">所选部门尚未配置费用审批流程，请联系管理员完成统一审批设置。</p>
      <ol v-if="route.length" aria-label="审批路径"><li v-for="(name, index) in route" :key="index">{{ name }}</li></ol>
      <footer class="expense-actions"><button type="button" class="pdm-secondary-action" :disabled="busy" @click="preview">预览审批流程</button><button type="button" class="pdm-secondary-action" :disabled="busy" @click="save(false)">暂存</button><button type="submit" class="pdm-primary-action" :disabled="busy">提交审批</button><button type="button" class="pdm-secondary-action" :disabled="busy" @click="closeDraft">关闭</button></footer>
    </form>
    <template v-else>
      <div class="expense-table-scroll"><table class="expense-list"><thead><tr><th>申请用途</th><th>申请人</th><th>需求日期</th><th>审批部门</th><th>预计总金额</th><th>状态</th><th>当前节点</th><th>操作</th></tr></thead>
        <tbody><tr v-for="view in directory?.requests ?? []" :key="view.request.id"><td>{{ expensePurpose(view.request.purpose) }}</td><td><PersonName :name="displayName(view.request.applicant)" /></td><td>{{ view.request.requiredOn || '待填写' }}</td><td>{{ view.request.department === 'Procurement' ? '采购部' : '供应链' }}</td><td>{{ view.canViewAmount === false ? '无权查看' : money(view.request.totalAmount) }}</td><td>{{ expenseStatus(view.request.status) }}</td><td>{{ view.request.currentStep?.name || '—' }}</td><td><button type="button" class="pdm-text-action" @click="select(view)">{{ view.canApprove ? '查看并审批' : '查看' }}</button></td></tr>
        <tr v-if="directory && !directory.requests.length"><td colspan="8">暂无费用申请</td></tr></tbody>
      </table></div>
      <article v-if="selected" class="pdm-panel expense-detail">
        <header class="expense-toolbar"><h3>{{ expensePurpose(selected.request.purpose) }}申请</h3><button v-if="selected.canWithdraw" type="button" class="pdm-secondary-action" :disabled="busy" @click="withdraw">撤回申请</button><button v-if="selected.canEdit" type="button" class="pdm-primary-action" :disabled="busy" @click="edit(selected)">编辑申请</button></header>
        <p>项目 {{ selected.request.projectCode }} {{ selected.request.projectName }} · 申请人 <PersonName :name="displayName(selected.request.applicant)" /> · 需求日期 {{ selected.request.requiredOn || '待填写' }}</p>
        <p class="expense-reason-text">{{ selected.request.reason }}</p>
        <div class="expense-table-scroll"><table class="expense-lines"><thead><tr><th>类型</th><th>物料编码</th><th>名称</th><th>规格／服务内容</th><th>品牌</th><th>单位</th><th>数量</th><th>预计单价</th><th>预计金额</th><th>备注</th></tr></thead><tbody><tr v-for="(line, index) in selected.request.lines" :key="index"><td>{{ line.kind === 'Service' ? '服务' : '物料' }}</td><td>{{ line.materialCode }}</td><td>{{ line.name }}</td><td>{{ line.specification }}</td><td>{{ line.brand }}</td><td>{{ line.unit }}</td><td>{{ line.quantity }}</td><td>{{ selected.canViewAmount === false ? '无权查看' : money(line.unitPrice) }}</td><td>{{ selected.canViewAmount === false ? '无权查看' : money(expenseLineAmount(line)) }}</td><td>{{ line.note }}</td></tr></tbody></table></div>
        <p>预计总金额 {{ selected.canViewAmount === false ? '无权查看' : money(selected.request.totalAmount) }}</p>
        <ParallelApprovalStatus v-for="step in selected.request.steps" :key="'departments-' + step.id" :departments="step.participants.departments" /><ol aria-label="审批记录"><li v-for="step in selected.request.steps" :key="step.id"><strong>{{ step.name }}</strong> · <PersonName :name="step.participants.members.map(member => displayName(member)).join('、')" /> · {{ step.decision === 'Approved' ? '已同意' : step.decision === 'Rejected' ? '已退回' : selected.request.currentStep?.id === step.id ? '待审批' : '待流转' }}<span v-if="step.comment"> · {{ step.comment }}</span><ul v-if="step.participants.signatures.length"><li v-for="signature in step.participants.signatures" :key="signature.actor + signature.signedAt"><PersonName :name="displayName(signature.actor)" />已同意 {{ signature.comment }}</li></ul></li></ol>
        <form v-if="selected.canApprove" class="expense-decision" @submit.prevent="decide(true)">
          <label v-if="selected.request.currentStep?.participants.policy?.editBusinessNote">申请原因<textarea v-model="businessNote" maxlength="500" rows="2" :disabled="busy" /></label>
          <label>审批意见<textarea v-model="comment" aria-label="审批意见" maxlength="1000" rows="2" :disabled="busy" :required="selected.request.currentStep?.participants.policy?.requireOpinion" /></label>
          <div class="expense-actions"><button class="pdm-primary-action" type="submit" :disabled="busy">同意</button><button class="pdm-secondary-action" type="button" :disabled="busy" @click="decide(false)">退回修改</button></div>
        </form>
      </article>
    </template>
  </section>
</template>

<style scoped>
.expense-requests {
  display: grid;
  gap: 14px;
  min-width: 0;
  padding: 12px 0;
}
.expense-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.expense-toolbar > div, .expense-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.expense-form, .expense-detail {
  padding: 16px;
  display: grid;
  gap: 14px;
}
.expense-fields {
  border: 0;
  padding: 0;
  margin: 0;
  display: grid;
  grid-template-columns: repeat(3, minmax(150px, 1fr));
  gap: 14px;
}
.expense-fields label, .expense-decision label {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
}
input, select, textarea {
  font: inherit;
  color: var(--pdm-text);
  background: var(--pdm-surface);
  border: 1px solid var(--pdm-border);
  border-radius: 6px;
  padding: 6px 8px;
  box-sizing: border-box;
}
.expense-fields input, .expense-fields select, .expense-fields textarea, .expense-decision textarea { width: 100%; }
.expense-reason { grid-column: 1 / -1; }
.required { color: var(--pdm-danger, #c73333); }
.expense-table-scroll { overflow: auto; max-width: 100%; }
table { width: 100%; border-collapse: collapse; }
th, td {
  padding: 8px;
  border-bottom: 1px solid var(--pdm-border);
  text-align: center;
  vertical-align: middle;
}
th { background: var(--pdm-surface-soft); white-space: nowrap; }
.expense-lines { min-width: 1150px; }
.expense-lines th, .expense-lines td { height: 40px; padding: 4px 8px; box-sizing: border-box; }
.expense-lines input, .expense-lines select { width: 100%; min-width: 65px; height: 30px; padding: 4px 8px; text-align: center; }
.expense-lines select { text-align-last: center; }
.expense-lines td:nth-child(3), .expense-lines td:nth-child(4) { min-width: 155px; }
.expense-lines :deep(.el-input-number) { width: 125px; height: 30px; --el-input-height: 30px; }
.expense-list { min-width: 750px; }
.expense-actions { justify-content: flex-end; }
.expense-reason-text { white-space: pre-wrap; overflow-wrap: anywhere; }
.expense-decision { display: grid; gap: 12px; }
ol, ul { display: grid; gap: 8px; margin: 0; padding-left: 22px; }
h3, p { margin: 0; }
@media (max-width: 700px) {
  .expense-fields { grid-template-columns: 1fr; }
  .expense-toolbar { align-items: flex-start; flex-wrap: wrap; }
  .expense-form, .expense-detail { padding: 10px; }
}
</style>
