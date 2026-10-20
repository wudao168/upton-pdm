<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { readWorkflowNotifications, saveWorkflowNotification, readWorkflowNotificationRecords } from '../api'
import { userErrorMessage } from '../userErrorMessage'
import type { OrganizationDirectory, RolePermissionSettings } from '../types'
import { notificationDepartments, notificationDepartmentPath, submitterDepartmentOption, type WorkflowNotificationCatalog, type WorkflowNoticeRecord, type WorkflowNotificationRecipient } from '../workflowNotifications'

const props = defineProps<{ token: string; companyId: string; flowCode: string; directory?: OrganizationDirectory; roles?: RolePermissionSettings[]; active: boolean }>()
const catalog = ref<WorkflowNotificationCatalog | null>(null), enabled = ref(false)
const rows = ref<Array<{ department: string; roleCode: string }>>([])
const pending = ref(false), error = ref(''), notice = ref(''), records = ref<WorkflowNoticeRecord[]>([]), showRecords = ref(false)
let generation = 0
const departments = computed(() => notificationDepartments(props.directory, props.companyId))
const roles = computed(() => props.roles ?? [])
const trigger = computed(() => props.flowCode === 'validation-plan' ? '审批通过后通知；计划责任工程师仍按计划项自动匹配。' : props.flowCode === 'material-code-application' ? '正式料号生成后通知，结果同时返回原申请人。' : '正式发布成功后通知；增补、变更发布使用同一份接收配置。')
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
    rows.value = (flow?.recipients ?? []).map(row => ({ department: row.departmentSource === 'SubmitterDepartment' ? submitterDepartmentOption : row.departmentId ?? '', roleCode: row.roleCode ?? '' }))
    if (flow?.recipients.some(row => !row.roleCode)) error.value = '原通知使用旧接收配置，请为各行重新选择角色后保存。'
  } catch (cause) { if (version === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '通知设置加载失败') }
  finally { if (version === generation) pending.value = false }
}
watch(() => [props.token, props.companyId, props.flowCode, props.active], load, { immediate: true })
onBeforeUnmount(() => generation++)
async function save() {
  if (!catalog.value || pending.value) return
  if (rows.value.some(row => !roles.value.some(role => role.role === row.roleCode)) || enabled.value && !rows.value.length) { error.value = '请为每行选择角色；部门可以不选，启用通知时至少保留一组接收对象。'; return }
  const recipients: WorkflowNotificationRecipient[] = rows.value.map(row => ({ departmentSource: row.department === submitterDepartmentOption ? 'SubmitterDepartment' : row.department ? 'Fixed' : 'CompanyRole', departmentId: row.department === submitterDepartmentOption || !row.department ? null : row.department, roleCode: row.roleCode }))
  const company = props.companyId, version = generation
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const value = await saveWorkflowNotification(props.flowCode, { enabled: enabled.value, recipients, expectedRowVersion: catalog.value.rowVersion, companyId: company }, props.token)
    if (version === generation && company === props.companyId) { catalog.value = value; notice.value = '通知设置已保存；审批流程保持不变，新发起的审批使用此配置。' }
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
  <section class="workflow-notifications" aria-label="通知接收设置">
    <header><h3>通知接收设置</h3><label class="notification-check"><input v-model="enabled" type="checkbox" :disabled="pending || !catalog">启用通知</label></header>
    <p class="notification-hint">通知时机：{{ trigger }}</p>
    <p v-if="error" role="alert" class="pdm-inline-error">{{ error }} <button type="button" @click="load">刷新通知设置</button></p>
    <p v-if="notice" role="status" class="notification-success">{{ notice }}</p>
    <p v-if="pending && !catalog" class="notification-hint">正在加载通知设置…</p>
    <fieldset v-if="catalog" :disabled="pending || !enabled">
      <legend class="notification-sr">接收部门与角色</legend>
      <div class="notification-row notification-columns" aria-hidden="true"><span>部门（可不选）</span><span>角色</span><span></span></div>
      <div v-for="(row, index) in rows" :key="index" class="notification-row">
        <label><span class="notification-mobile-label">部门（可不选）</span><select v-model="row.department" :aria-label="`接收对象${index + 1}部门`"><option value="">不限部门（当前公司）</option><option :value="submitterDepartmentOption">提交人部门（跟随审批流）</option><option v-for="department in departments" :key="department.id" :value="department.id">{{ notificationDepartmentPath(directory, department.id) }}</option><option v-if="row.department && row.department !== submitterDepartmentOption && !departments.some(department => department.id === row.department)" :value="row.department" disabled>原部门已失效，请重新选择</option></select></label>
        <label><span class="notification-mobile-label">角色</span><select v-model="row.roleCode" :aria-label="`接收对象${index + 1}角色`"><option value="" disabled>{{ roles.length ? '请选择角色' : '暂无角色，请先维护用户角色' }}</option><option v-for="role in roles" :key="role.role" :value="role.role">{{ role.name }}</option><option v-if="row.roleCode && !roles.some(role => role.role === row.roleCode)" :value="row.roleCode" disabled>原角色已失效，请重新选择</option></select></label>
        <button type="button" class="pdm-secondary-action" :aria-label="`删除接收对象${index + 1}`" @click="rows.splice(index, 1)">删除</button>
      </div>
      <button type="button" class="pdm-secondary-action" @click="rows.push({ department: '', roleCode: '' })">＋ 添加接收对象</button>
    </fieldset>
    <p class="notification-hint">选择部门：通知该部门内拥有所选角色的人员；不选部门：通知当前公司内拥有该角色的全部有效人员。多组匹配同一人员只通知一次。</p>
    <p class="notification-hint">角色及人员在“用户设置”中维护；收到通知不改变业务详情的查看权限。</p>
    <p class="notification-hint">“提交人部门”沿用审批发起时的主部门关系；新配置不补发历史通知。</p>
    <footer><button type="button" class="pdm-text-action" @click="loadRecords">{{ showRecords ? '收起通知记录' : '查看通知记录' }}</button><button type="button" class="pdm-primary-action" :disabled="pending || !catalog" @click="save">{{ pending ? '正在处理…' : '保存通知设置' }}</button></footer>
    <div v-if="showRecords" class="notification-records"><p v-if="!records.length" class="notification-hint">暂无新流程的通知记录。</p><div v-for="record in records" :key="record.id"><strong>{{ recordState(record.state) }}</strong><span>{{ record.message }}</span><small>{{ new Date(record.createdAt).toLocaleString() }}</small></div></div>
  </section>
</template>

<style scoped>
.workflow-notifications{margin-top:16px;padding-top:15px;border-top:1px solid var(--pdm-border)}.workflow-notifications header,.workflow-notifications footer{display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap}.workflow-notifications h3{margin:0;font-size:var(--pdm-font-size-heading);font-weight:500}.notification-check{display:flex;align-items:center;gap:6px}.notification-hint{font-size:11px;color:var(--pdm-muted);margin:8px 0;line-height:1.6}.notification-success{color:var(--pdm-green);font-size:12px}.workflow-notifications fieldset{border:0;padding:0;margin:12px 0;min-width:0}.workflow-notifications fieldset:disabled{opacity:.55}.notification-sr{position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%)}.notification-row{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 60px;gap:10px;align-items:center;margin-bottom:9px}.notification-row label{display:flex;flex-direction:column;gap:4px;min-width:0}.notification-row select{width:100%;min-width:0;min-height:32px;padding:5px 8px;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--pdm-text);font:inherit}.notification-columns{font-size:12px;color:var(--pdm-muted);margin-bottom:5px}.notification-mobile-label{display:none}.workflow-notifications footer{padding-top:10px;border-top:1px solid var(--pdm-border-soft)}.notification-records{margin-top:10px}.notification-records>div{display:flex;gap:10px;flex-wrap:wrap;padding:7px 0;border-bottom:1px solid var(--pdm-border-soft);font-size:11px}.notification-records small{color:var(--pdm-muted)}@media(max-width:600px){.notification-columns{display:none}.notification-mobile-label{display:block;font-size:11px;color:var(--pdm-muted)}.notification-row{grid-template-columns:minmax(0,1fr) 55px;padding-bottom:9px;border-bottom:1px solid var(--pdm-border-soft)}.notification-row>label{grid-column:1}.notification-row>button{grid-column:2;grid-row:1 / span 2}.notification-row select{font-size:16px}}
</style>
