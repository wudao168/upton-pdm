<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { readProjectContactInformation, saveProjectContactInformation } from '../api'
import type { ProjectContactEntry, ProjectContactInformationView } from '../projectContactInformation'

const props = defineProps<{ projectId: string; token: string }>()
const view = ref<ProjectContactInformationView | null>(null)
const draft = ref<ProjectContactEntry[]>([])
const staff = ref({ salesPeople: '', presalesPeople: '' })
const loading = ref(false)
const saving = ref(false)
const error = ref('')
let generation = 0
const canEdit = computed(() => Boolean(view.value?.canEdit) && !loading.value && !saving.value)
function apply(value: ProjectContactInformationView) {
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
  staff.value = { salesPeople: '', presalesPeople: '' }
  try { const value = await readProjectContactInformation(props.projectId, props.token); if (current === generation) apply(value) }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '信息加载失败' }
  finally { if (current === generation) loading.value = false }
}
async function save() {
  if (!canEdit.value || !view.value) return false
  if (draft.value.some(entry => entry.isShippingAddress && !entry.address.trim())) { error.value = '标记为收货地址的记录必须填写地址。'; return }
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
    <header><h2>客户信息</h2><span>{{ view?.customerName || '—' }}</span><div class="project-contacts__header-actions"><el-button aria-label="刷新客户信息" :disabled="loading || saving" @click="load">刷新</el-button><el-button v-if="view?.canEdit" aria-label="新增" :disabled="!canEdit" @click="draft.push({ contact: '', phone: '', address: '', isShippingAddress: false })">新增</el-button><el-button v-if="view?.canEdit" type="primary" aria-label="保存客户信息" :disabled="!canEdit" @click="save">保存</el-button></div></header>
    <div class="project-contacts__table-wrap"><table class="project-contacts__table"><thead><tr><th>序号</th><th>用途</th><th>联系人</th><th>联系方式</th><th>职位</th><th>备注</th><th>收货地址</th><th v-if="view?.canEdit">操作</th></tr></thead><tbody>
      <tr v-for="(entry, index) in draft" :key="index" :class="{ 'is-shipping': entry.isShippingAddress }">
        <td>{{ index + 1 }}</td><td><label v-if="view?.canEdit"><input v-model="entry.isShippingAddress" type="checkbox" :aria-label="`标记收货地址 ${index + 1}`" :disabled="!canEdit">收货地址</label><span v-if="entry.isShippingAddress" class="project-contacts__shipping-badge">收货</span><span v-else-if="!view?.canEdit">联系人</span></td><td><input v-if="view?.canEdit" v-model="entry.contact" :aria-label="`联系人 ${index + 1}`" :disabled="!canEdit" maxlength="100"><span v-else>{{ entry.contact || '—' }}</span></td>
        <td><input v-if="view?.canEdit" v-model="entry.phone" :aria-label="`联系方式 ${index + 1}`" :disabled="!canEdit" maxlength="100"><span v-else>{{ entry.phone || '—' }}</span></td>
        <td><input v-if="view?.canEdit" v-model="entry.position" :aria-label="`职位 ${index + 1}`" :disabled="!canEdit" maxlength="100"><span v-else>{{ entry.position || '—' }}</span></td>
        <td><input v-if="view?.canEdit" v-model="entry.remarks" :aria-label="`备注 ${index + 1}`" :disabled="!canEdit" maxlength="500"><span v-else>{{ entry.remarks || '—' }}</span></td>
        <td><input v-if="view?.canEdit" v-model="entry.address" :aria-label="`收货地址 ${index + 1}`" :disabled="!canEdit" maxlength="500" placeholder="可不填写"><span v-else>{{ entry.address || '—' }}</span></td>

        <td v-if="view?.canEdit"><button type="button" class="pdm-text-action" :disabled="!canEdit" :aria-label="`删除联系人 ${index + 1}`" @click="draft.splice(index, 1)">删除</button></td>
      </tr><tr v-if="!draft.length"><td :colspan="view?.canEdit ? 8 : 7">{{ loading ? '加载中…' : '暂无联系人及收货地址' }}</td></tr>
    </tbody></table></div>
    <div v-if="error" class="project-contacts__error" role="alert">{{ error }}<button type="button" class="pdm-text-action" :disabled="saving" @click="load">重新读取</button></div>
  </form>
  <slot :staff="staff" :can-edit="canEdit" :save="save" />
</template>

<style scoped>
.project-contacts{flex:none;font-size:11px;overflow:hidden}
.project-contacts header{min-height:32px;display:flex;align-items:center;gap:12px;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-contacts h2{margin:0;font-size:12px}.project-contacts header>span{color:var(--pdm-muted)}
.project-contacts__header-actions{display:flex;gap:5px;margin-left:auto}
.project-contacts__header-actions :deep(.el-button){width:auto;min-width:60px;height:var(--pdm-button-compact);margin:0;padding:0 6px;font-size:11px}
.project-contacts__table-wrap{overflow:auto;max-height:240px}.project-contacts__table{width:100%;min-width:850px;border-collapse:collapse;table-layout:fixed}
.project-contacts th,.project-contacts td{height:32px;padding:4px 8px;text-align:center;border-right:1px solid var(--pdm-border-soft);border-bottom:1px solid var(--pdm-border-soft);overflow-wrap:anywhere}
.project-contacts th{background:var(--pdm-panel-muted,#f8fafc);font-weight:500}.project-contacts th:first-child{width:40px}.project-contacts th:nth-child(7){width:28%}
.project-contacts tbody tr:nth-child(even){background:var(--pdm-row-stripe,#f1f5f9)}.project-contacts .is-shipping{background:var(--pdm-blue-soft,#eff6ff)!important}
.project-contacts input:not([type=checkbox]){box-sizing:border-box;width:100%;min-width:0;height:26px;padding:0 6px;font:inherit;color:var(--pdm-text);border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,#fff)}
.project-contacts td label{display:inline-flex;align-items:center;gap:3px;white-space:nowrap}.project-contacts__shipping-badge{display:inline-block;margin-left:4px;padding:1px 4px;background:var(--pdm-blue);color:white;border-radius:3px;white-space:nowrap}
.project-contacts__error{padding:8px;color:var(--pdm-danger,#dc2626)}.project-contacts button{font-size:11px}
</style>
