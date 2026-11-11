<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { readWorkflowNotifications, saveWorkflowNotification, readWorkflowNotificationRecords } from '../api'
import { userErrorMessage } from '../userErrorMessage'
import type { ApprovalCopyRecipient, OrganizationDirectory, RolePermissionSettings } from '../types'
import ApprovalCopyRecipientsEditor from './ApprovalCopyRecipientsEditor.vue'
import { noticeToCopy, copyToNotice, incompleteCopy, type WorkflowNotificationCatalog, type WorkflowNoticeRecord, type WorkflowNotificationRecipient } from '../workflowNotifications'

const props = defineProps<{ token: string; companyId: string; flowCode: string; directory?: OrganizationDirectory; roles?: RolePermissionSettings[]; active: boolean }>()
const catalog = ref<WorkflowNotificationCatalog | null>(null), enabled = ref(false)
const rows = ref<ApprovalCopyRecipient[]>([])
const pending = ref(false), error = ref(''), notice = ref(''), records = ref<WorkflowNoticeRecord[]>([]), showRecords = ref(false)
let generation = 0
const roles = computed(() => props.roles ?? [])
const trigger = computed(() => props.flowCode === 'expense-approval' ? '费用申请全部审批通过后抄送。' : props.flowCode === 'validation-plan' ? '审批通过后通知；计划责任工程师仍按计划项自动匹配。' : props.flowCode === 'material-code-application' ? '正式料号生成后通知，结果同时返回原申请人。' : '正式发布成功后通知；增补、变更发布使用同一份接收配置。')
async function load() {
  if (!props.token || !props.companyId || !props.active) return
  const version = ++generation, company = props.companyId
  pending.value = true; error.value = ''; notice.value = ''; records.value = []; showRecords.value = false; catalog.value = null; rows.value = []
  try {
    const value = await readWorkflowNotifications(props.token)
    if (version !== generation || company !== props.companyId || value.companyId !== company) return
    catalog.value = value
    const flow = value.flows.find(flow => flow.flowCode === props.flowCode)
    enabled.value = flow?.enabled ?? false
    rows.value = (flow?.recipients ?? []).map(noticeToCopy)
    if (rows.value.some(row => row.roleCode === '__legacy__')) error.value = '原通知使用旧接收配置，请为各行重新选择角色后保存。'
  } catch (cause) { if (version === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '通知设置加载失败') }
  finally { if (version === generation) pending.value = false }
}
watch(() => [props.token, props.companyId, props.flowCode, props.active], load, { immediate: true })
onBeforeUnmount(() => generation++)
async function save() {
  if (!catalog.value || pending.value) return
  if (rows.value.some(row => incompleteCopy(row) || row.roleCode && !roles.value.some(role => role.role === row.roleCode) || row.source === 'Role' && !roles.value.some(role => role.role === row.target)) || enabled.value && !rows.value.length) { error.value = '请完整选择抄送对象及有效角色；启用时至少保留一组抄送对象。'; return }
  const recipients: WorkflowNotificationRecipient[] = rows.value.map(copyToNotice)
  const company = props.companyId, version = generation
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const value = await saveWorkflowNotification(props.flowCode, { enabled: enabled.value, recipients, expectedRowVersion: catalog.value.rowVersion, companyId: company }, props.token)
    if (version === generation && company === props.companyId) { catalog.value = value; notice.value = '完成抄送已保存；审批流程保持不变，新发起的审批使用此配置。' }
  } catch (cause) { if (version === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '通知设置保存失败') }
  finally { if (version === generation) pending.value = false }
}
async function loadRecords() {
  showRecords.value = !showRecords.value
  if (!showRecords.value) return
  const version = generation
  try { const value = await readWorkflowNotificationRecords(props.flowCode, props.token); if (version === generation) records.value = value }
  catch (cause) { if (version === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '通知记录加载失败') }
}
const recordState = (state: string) => ({ Waiting: '等待业务完成', Ready: '正在通知', Attention: '需要处理', Sent: '已通知', Cancelled: '本轮已结束' } as Record<string, string>)[state] ?? '处理中'
</script>

<template>
  <section class="workflow-notifications" aria-label="完成时抄送">
    <header><h3>完成时抄送</h3><label class="notification-check"><input v-model="enabled" type="checkbox" :disabled="pending || !catalog">启用完成抄送</label></header>
    <p class="notification-hint">抄送时机：{{ trigger }}</p>
    <p v-if="error" role="alert" class="pdm-inline-error">{{ error }} <button type="button" @click="load">刷新</button></p>
    <p v-if="notice" role="status" class="notification-success">{{ notice }}</p>
    <p v-if="pending && !catalog" class="notification-hint">正在加载通知设置…</p>
    <ApprovalCopyRecipientsEditor v-if="catalog" v-model="rows" :directory="directory" :roles="roles" :company-id="companyId" :disabled="pending || !enabled" />
    <p class="notification-hint">在所选分支完成业务后抄送，不参与审批判断；新配置不补发历史抄送。原有接收配置继续保留。</p>
    <p class="notification-hint">“发起人部门”使用发起时的主部门；技术助理沿用既有事业部范围。角色及人员在用户设置中维护，收到抄送不增加业务查看权限。</p>    <footer><button type="button" class="pdm-text-action" @click="loadRecords">{{ showRecords ? '收起通知记录' : '查看通知记录' }}</button><button type="button" class="pdm-primary-action" :disabled="pending || !catalog" @click="save">{{ pending ? '正在处理…' : '保存完成抄送' }}</button></footer>
    <div v-if="showRecords" class="notification-records"><p v-if="!records.length" class="notification-hint">暂无新流程的通知记录。</p><div v-for="record in records" :key="record.id"><strong>{{ recordState(record.state) }}</strong><span>{{ record.message }}</span><small>{{ new Date(record.createdAt).toLocaleString() }}</small></div></div>
  </section>
</template>

<style scoped>
.workflow-notifications{margin-top:16px;padding-top:15px;border-top:1px solid var(--pdm-border)}.workflow-notifications header,.workflow-notifications footer{display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap}.workflow-notifications h3{margin:0;font-size:var(--pdm-font-size-heading);font-weight:500}.notification-check{display:flex;align-items:center;gap:6px}.notification-hint{font-size:11px;color:var(--pdm-muted);margin:8px 0;line-height:1.6}.notification-success{color:var(--pdm-green);font-size:12px}.workflow-notifications fieldset{border:0;padding:0;margin:12px 0;min-width:0}.workflow-notifications fieldset:disabled{opacity:.55}.notification-sr{position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%)}.notification-row{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 60px;gap:10px;align-items:center;margin-bottom:9px}.notification-row label{display:flex;flex-direction:column;gap:4px;min-width:0}.notification-row select{width:100%;min-width:0;min-height:32px;padding:5px 8px;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--pdm-text);font:inherit}.notification-columns{font-size:12px;color:var(--pdm-muted);margin-bottom:5px}.notification-mobile-label{display:none}.workflow-notifications footer{padding-top:10px;border-top:1px solid var(--pdm-border-soft)}.notification-records{margin-top:10px}.notification-records>div{display:flex;gap:10px;flex-wrap:wrap;padding:7px 0;border-bottom:1px solid var(--pdm-border-soft);font-size:11px}.notification-records small{color:var(--pdm-muted)}@media(max-width:600px){.notification-columns{display:none}.notification-mobile-label{display:block;font-size:11px;color:var(--pdm-muted)}.notification-row{grid-template-columns:minmax(0,1fr) 55px;padding-bottom:9px;border-bottom:1px solid var(--pdm-border-soft)}.notification-row>label{grid-column:1}.notification-row>button{grid-column:2;grid-row:1 / span 2}.notification-row select{font-size:16px}}
</style>
