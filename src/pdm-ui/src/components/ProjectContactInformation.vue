<script setup lang="ts">
import CompanyName from './CompanyName.vue'
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { readProjectContactInformation, saveProjectContactInformation } from '../api'
import type { PdmCustomer } from '../types'
import type { ProjectContactEntry, ProjectContactInformationView } from '../projectContactInformation'

const props = defineProps<{ projectId: string; token: string; customer?: PdmCustomer; customerCode?: string; customerName?: string }>()
const view = ref<ProjectContactInformationView | null>(null)
const draft = ref<ProjectContactEntry[]>([])
const editingRows = ref(new Set<number>())
const staff = ref({ salesPeople: '', presalesPeople: '' })
const loading = ref(false)
const saving = ref(false)
const error = ref('')
let generation = 0
const canEdit = computed(() => Boolean(view.value?.canEdit) && !loading.value && !saving.value)
const customerSource = computed(() => {
  const source = props.customer?.sourceSystem
  if (!source) return '—'
  if (/^u9c?$/i.test(source)) return 'U9C'
  return source === 'legacy' ? '历史档案' : source
})
const customerSyncTime = computed(() => {
  if (!props.customer?.lastSyncedAt) return '—'
  const time = new Date(props.customer.lastSyncedAt)
  return Number.isNaN(time.getTime()) ? '—' : time.toLocaleString('zh-CN', { hour12: false })
})
function apply(value: ProjectContactInformationView) {
  editingRows.value.clear()
  view.value = value
  const info = value.information
  staff.value = { salesPeople: info.salesPeople ?? '', presalesPeople: info.presalesPeople ?? '' }
  const legacy: ProjectContactEntry[] = []
  if (info.customerContact || info.customerPhone) legacy.push({ contact: info.customerContact, phone: info.customerPhone, address: '', isShippingAddress: false })
  if (info.shippingAddress || info.shippingContact || info.shippingPhone) legacy.push({ contact: info.shippingContact, phone: info.shippingPhone, address: info.shippingAddress, isShippingAddress: Boolean(info.shippingAddress) })
  draft.value = (info.entries ?? legacy).map(entry => ({ ...entry }))
}
async function load() {
  const current = ++generation
  loading.value = true
  error.value = ''
  view.value = null
  draft.value = []
  editingRows.value.clear()
  saving.value = false
  staff.value = { salesPeople: '', presalesPeople: '' }
  try { const value = await readProjectContactInformation(props.projectId, props.token); if (current === generation) apply(value) }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '信息加载失败' }
  finally { if (current === generation) loading.value = false }
}
function addEntry() {
  if (!canEdit.value) return
  draft.value.push({ contact: '', phone: '', address: '', isShippingAddress: false })
  editingRows.value.add(draft.value.length - 1)
}
function editEntry(index: number) {
  if (canEdit.value) editingRows.value.add(index)
}
function removeEntry(index: number) {
  if (!canEdit.value) return
  draft.value.splice(index, 1)
  editingRows.value = new Set([...editingRows.value].filter(row => row !== index).map(row => row > index ? row - 1 : row))
}
async function save() {
  if (!canEdit.value || !view.value) return false
  if (draft.value.some(entry => entry.isShippingAddress && !entry.address.trim())) { error.value = '用途为收货地址的记录必须填写地址。'; return }
  if (draft.value.some(entry => !entry.contact.trim() && !entry.phone.trim() && !entry.address.trim())) { error.value = '请填写联系人信息或地址。'; return }
  const current = generation
  saving.value = true
  error.value = ''
  try {
    const value = await saveProjectContactInformation({ ...view.value.information, entries: draft.value, ...staff.value }, props.token)
    if (current === generation) { apply(value); ElMessage.success('客户信息已保存'); return true }
  } catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '保存失败'; return false }
  finally { if (current === generation) saving.value = false }
}
watch(() => [props.projectId, props.token], () => { void load() }, { immediate: true })
</script>

<template>
  <form class="project-contacts pdm-panel" aria-label="客户及发货信息" @submit.prevent>
    <header><h2>客户信息</h2><div class="project-contacts__header-actions"><el-button aria-label="刷新客户信息" :disabled="loading || saving" @click="load">刷新</el-button><el-button v-if="view?.canEdit" aria-label="新增" :disabled="!canEdit" @click="addEntry">新增</el-button><el-button v-if="view?.canEdit" type="primary" aria-label="保存客户信息" :disabled="!canEdit" @click="save">保存</el-button></div></header>
    <dl class="project-contacts__customer" aria-label="客户档案信息">
      <div><dt>客户名称</dt><dd><CompanyName :name="customer?.name || view?.customerName || customerName || '—'" /></dd></div>
      <div><dt>客户编码</dt><dd>{{ customerCode || customer?.code || '—' }}</dd></div>
      <div><dt>客户状态</dt><dd>{{ customer ? (customer.isActive ? '正常' : '已停用') : '—' }}</dd></div>
      <div><dt>客户来源</dt><dd>{{ customerSource }}</dd></div>
      <div><dt>最近同步</dt><dd>{{ customerSyncTime }}</dd></div>
    </dl>
    <div class="project-contacts__table-wrap"><table class="project-contacts__table" style="min-width:1250px !important"><colgroup><col style="width:60px !important"><col span="5"><col style="width:500px !important"><col v-if="view?.canEdit" style="width:160px !important"></colgroup><thead><tr><th>序号</th><th>用途</th><th>联系人</th><th>联系方式</th><th>职位</th><th>备注</th><th>收货地址</th><th v-if="view?.canEdit">操作</th></tr></thead><tbody>
      <tr v-for="(entry, index) in draft" :key="index" :class="{ 'is-shipping': entry.isShippingAddress, 'is-editing': editingRows.has(index) }">
        <td>{{ index + 1 }}</td><td><select v-if="view?.canEdit && editingRows.has(index)" v-model="entry.isShippingAddress" :aria-label="`地址用途 ${index + 1}`" :disabled="!canEdit"><option :value="false">办公地址</option><option :value="true">收货地址</option></select><span v-else>{{ entry.isShippingAddress ? '收货地址' : '办公地址' }}</span></td><td><input v-if="view?.canEdit && editingRows.has(index)" v-model="entry.contact" :aria-label="`联系人 ${index + 1}`" :disabled="!canEdit" maxlength="100"><span v-else>{{ entry.contact || '—' }}</span></td>
        <td><input v-if="view?.canEdit && editingRows.has(index)" v-model="entry.phone" :aria-label="`联系方式 ${index + 1}`" :disabled="!canEdit" maxlength="100"><span v-else>{{ entry.phone || '—' }}</span></td>
        <td><input v-if="view?.canEdit && editingRows.has(index)" v-model="entry.position" :aria-label="`职位 ${index + 1}`" :disabled="!canEdit" maxlength="100"><span v-else>{{ entry.position || '—' }}</span></td>
        <td><input v-if="view?.canEdit && editingRows.has(index)" v-model="entry.remarks" :aria-label="`备注 ${index + 1}`" :disabled="!canEdit" maxlength="500"><span v-else>{{ entry.remarks || '—' }}</span></td>
        <td><input v-if="view?.canEdit && editingRows.has(index)" v-model="entry.address" :aria-label="`收货地址 ${index + 1}`" :disabled="!canEdit" maxlength="500" placeholder="可不填写"><span v-else>{{ entry.address || '—' }}</span></td>

        <td v-if="view?.canEdit"><div class="project-contacts__row-actions"><button v-if="!editingRows.has(index)" type="button" class="pdm-text-action" :disabled="!canEdit" :aria-label="`编辑联系人 ${index + 1}`" @click="editEntry(index)">编辑</button><button type="button" class="pdm-text-action" :disabled="!canEdit" :aria-label="`删除联系人 ${index + 1}`" @click="removeEntry(index)">删除</button></div></td>
      </tr><tr v-if="!draft.length"><td :colspan="view?.canEdit ? 8 : 7">{{ loading ? '加载中…' : '暂无联系人及收货地址' }}</td></tr>
    </tbody></table></div>
    <div v-if="error" class="project-contacts__error" role="alert">{{ error }}<button type="button" class="pdm-text-action" :disabled="saving" @click="load">重新读取</button></div>
  </form>
  <slot :staff="staff" :can-edit="canEdit" :save="save" />
</template>

<style scoped>
.project-contacts{flex:none;font-size:11px;overflow:hidden}
.project-contacts header{min-height:32px;display:flex;align-items:center;gap:12px;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-contacts h2{margin:0;font-size:14px}.project-contacts header>span{color:var(--pdm-muted)}
.project-contacts__header-actions{display:flex;gap:5px;margin-left:auto}
.project-contacts__header-actions :deep(.el-button){width:auto;min-width:60px;height:var(--pdm-button-compact);margin:0;padding:0 6px;font-size:11px}
.project-contacts__customer{display:grid;grid-template-columns:minmax(220px,2fr) repeat(3,minmax(100px,1fr)) minmax(160px,1.4fr);gap:12px;margin:0;padding:12px;border-bottom:1px solid var(--pdm-border-soft)}
.project-contacts__customer>div{min-width:0}.project-contacts__customer dt{color:var(--pdm-muted);margin-bottom:4px}.project-contacts__customer dd{margin:0;color:var(--pdm-text);font-size:13px;overflow-wrap:anywhere}
@media(max-width:1100px){.project-contacts__customer{grid-template-columns:repeat(2,minmax(0,1fr))}.project-contacts__customer>div:first-child{grid-column:1/-1}}
.project-contacts__table-wrap{overflow:auto;max-height:240px}.project-contacts__table{width:100%;min-width:1250px;border-collapse:collapse;table-layout:fixed}
.project-contacts th,.project-contacts td{height:32px;padding:4px 8px;text-align:center;border-right:1px solid var(--pdm-border-soft);border-bottom:1px solid var(--pdm-border-soft);overflow-wrap:anywhere}
.project-contacts th{background:var(--pdm-panel-muted,var(--pdm-bg));font-weight:500}.project-contacts th:first-child{width:40px}.project-contacts th:nth-child(7){width:500px}
.project-contacts tbody tr:nth-child(even){background:var(--pdm-row-stripe,var(--pdm-surface-muted))}.project-contacts .is-shipping{background:var(--pdm-blue-soft,var(--pdm-theme-accent-soft))!important}
.project-contacts input:not([type=checkbox]),.project-contacts select{box-sizing:border-box;width:100%;min-width:0;height:26px;padding:0 6px;font:inherit;text-align:center;text-align-last:center;color:var(--pdm-text);border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,#fff)}
.project-contacts__row-actions{display:flex;align-items:center;justify-content:center;gap:5px}
.project-contacts__error{padding:8px;color:var(--pdm-danger,var(--pdm-danger))}.project-contacts button{font-size:11px}
</style>

<style scoped>
:root body .project-contacts__table { min-width:1250px !important; table-layout:fixed !important; }
:root body .project-contacts__table th { width:auto !important; }
:root body .project-contacts__table th:first-child { width:60px !important; }
:root body .project-contacts__table th:nth-child(7) { box-sizing:border-box; width:500px !important; min-width:500px !important; }
:root body .project-contacts__table :is(input,select) { text-align:center !important; text-align-last:center !important; }
</style>
