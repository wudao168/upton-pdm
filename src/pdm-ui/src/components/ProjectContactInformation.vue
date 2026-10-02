<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { readProjectContactInformation, saveProjectContactInformation } from '../api'
import type { ProjectContactInformationView } from '../projectContactInformation'

const props = defineProps<{ projectId: string; token: string }>()
const view = ref<ProjectContactInformationView | null>(null)
const draft = ref({ customerContact: '', customerPhone: '', shippingAddress: '', shippingContact: '', shippingPhone: '' })
const loading = ref(false)
const saving = ref(false)
const error = ref('')
let generation = 0
const canEdit = computed(() => Boolean(view.value?.canEdit) && !loading.value && !saving.value)
type ContactSection = 'customer' | 'shipping'
function apply(value: ProjectContactInformationView, section?: ContactSection) {
  view.value = value
  const { customerContact, customerPhone, shippingAddress, shippingContact, shippingPhone } = value.information
  if (section !== 'shipping') Object.assign(draft.value, { customerContact, customerPhone })
  if (section !== 'customer') Object.assign(draft.value, { shippingAddress, shippingContact, shippingPhone })
}
async function load(section?: ContactSection) {
  const current = ++generation
  loading.value = true
  saving.value = false
  error.value = ''
  if (!section) {
    view.value = null
    draft.value = { customerContact: '', customerPhone: '', shippingAddress: '', shippingContact: '', shippingPhone: '' }
  }
  try {
    const value = await readProjectContactInformation(props.projectId, props.token)
    if (current === generation) apply(value, section)
  } catch (cause) {
    if (current === generation) error.value = cause instanceof Error ? cause.message : '信息加载失败'
  } finally {
    if (current === generation) loading.value = false
  }
}
async function save(section: ContactSection) {
  if (!canEdit.value || !view.value) return
  const current = generation
  saving.value = true
  error.value = ''
  try {
    const fields = section === 'customer'
      ? { customerContact: draft.value.customerContact, customerPhone: draft.value.customerPhone }
      : { shippingAddress: draft.value.shippingAddress, shippingContact: draft.value.shippingContact, shippingPhone: draft.value.shippingPhone }
    const value = await saveProjectContactInformation({ ...view.value.information, ...fields }, props.token)
    if (current === generation) { apply(value, section); ElMessage.success(section === 'customer' ? '客户信息已保存' : '发货信息已保存') }
  } catch (cause) {
    if (current === generation) error.value = cause instanceof Error ? cause.message : '保存失败'
  } finally {
    if (current === generation) saving.value = false
  }
}
watch(() => [props.projectId, props.token], () => load(), { immediate: true })
</script>

<template>
  <form class="project-contacts" aria-label="客户及发货信息" @submit.prevent>
    <div class="project-contacts__cards">
      <section class="pdm-panel project-contacts__card" aria-label="客户信息">
        <header><h2>客户信息</h2><div class="project-contacts__header-actions"><el-button size="small" aria-label="刷新客户信息" :disabled="loading || saving" @click="load('customer')">刷新</el-button><el-button v-if="view?.canEdit" type="primary" size="small" aria-label="保存客户信息" :disabled="!canEdit" @click="save('customer')">保存</el-button></div></header>
        <div class="project-contacts__fields">
          <label><span>客户名称</span><input aria-label="客户名称" :value="view?.customerName || ''" readonly placeholder="自动带出" /></label>
          <label><span>联系人</span><input v-model="draft.customerContact" aria-label="客户联系人" :disabled="!canEdit" maxlength="100" /></label>
          <label><span>联系方式</span><input v-model="draft.customerPhone" aria-label="客户联系方式" :disabled="!canEdit" maxlength="100" /></label>
        </div>
      </section>
      <section class="pdm-panel project-contacts__card" aria-label="发货信息">
        <header><h2>发货信息</h2><div class="project-contacts__header-actions"><el-button size="small" aria-label="刷新发货信息" :disabled="loading || saving" @click="load('shipping')">刷新</el-button><el-button v-if="view?.canEdit" type="primary" size="small" aria-label="保存发货信息" :disabled="!canEdit" @click="save('shipping')">保存</el-button></div></header>
        <div class="project-contacts__fields">
          <label><span>收货地址</span><input v-model="draft.shippingAddress" aria-label="收货地址" :disabled="!canEdit" maxlength="500" :title="draft.shippingAddress" /></label>
          <label><span>联系人</span><input v-model="draft.shippingContact" aria-label="发货联系人" :disabled="!canEdit" maxlength="100" /></label>
          <label><span>联系方式</span><input v-model="draft.shippingPhone" aria-label="发货联系方式" :disabled="!canEdit" maxlength="100" /></label>
        </div>
      </section>
    </div>
    <div v-if="loading || error" class="project-contacts__feedback">
      <span v-if="loading" role="status">加载中…</span>
      <span v-else-if="error" class="project-contacts__error" role="alert">{{ error }}</span>
      <button v-if="error" type="button" class="pdm-text-action" :disabled="saving" @click="load()">重新读取</button>
    </div>
  </form>
</template>

<style scoped>
.project-contacts{flex:none;font-size:11px}
.project-contacts__cards{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:5px}
.project-contacts__card{min-width:0;overflow:hidden}
.project-contacts header{height:32px;display:flex;align-items:center;justify-content:space-between;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-contacts h2{margin:0;font-size:12px}
.project-contacts__header-actions{display:flex;gap:5px}
.project-contacts__header-actions :deep(.el-button){width:60px;min-width:60px!important;height:24px;margin:0;padding:0 6px}
.project-contacts__fields{display:grid;gap:6px;padding:8px 12px}
.project-contacts label{display:grid;grid-template-columns:60px minmax(0,1fr);align-items:center;gap:8px}
.project-contacts input{box-sizing:border-box;min-width:0;width:100%;height:26px;padding:0 8px;font:inherit;color:var(--pdm-text);border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,#fff)}
.project-contacts input:focus{outline:1px solid var(--pdm-primary,#2563eb)}
.project-contacts input:disabled,.project-contacts input:read-only{background:var(--pdm-panel-muted,#f8fafc);color:var(--pdm-text)}
.project-contacts__feedback{min-height:32px;display:flex;align-items:center;gap:12px;color:var(--pdm-muted)}
.project-contacts__feedback>span{flex:1}.project-contacts button{font-size:11px}
.project-contacts__error{color:var(--pdm-danger,#dc2626)}
</style>
