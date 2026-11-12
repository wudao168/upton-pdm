<script setup lang="ts">
import CompanyName from './CompanyName.vue'
import { onMounted, ref } from 'vue'
import { PdmApiError, readCompanyBusinessDetails, saveCompanyBusinessDetails } from '../api'
import type { CompanyBusinessDetailsKind, CompanyBusinessFields } from '../companyBusinessDetails'
import type { ProjectOrganization } from '../types'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ company: ProjectOrganization; kind: CompanyBusinessDetailsKind; token: string; dialogId: number }>()
const emit = defineEmits<{ close: [dialogId: number]; saved: [] }>()
const title = props.kind === 'invoice' ? '开票资料' : '收货信息'
const fields = props.kind === 'invoice'
  ? [['title', '开票抬头', 200], ['taxNumber', '纳税人识别号', 50], ['address', '注册地址', 300], ['phone', '注册电话', 100], ['bank', '开户银行', 200], ['bankAccount', '银行账号', 80]] as const
  : [['recipient', '收货人', 80], ['contact', '联系方式', 100], ['address', '收货地址', 300]] as const
const open = ref(true)
const busy = ref(true)
const error = ref('')
const draft = ref<CompanyBusinessFields>()
let rowVersion = 0
function showError(cause: unknown, fallback: string) {
  error.value = cause instanceof PdmApiError && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : fallback
}
onMounted(async () => {
  try {
    const value = await readCompanyBusinessDetails(props.company.id, props.kind, props.token)
    rowVersion = value.rowVersion
    draft.value = value[props.kind] ?? {}
  } catch (cause) { showError(cause, '公司资料读取失败，请重新打开后重试。') }
  finally { busy.value = false }
})
async function save() {
  if (!draft.value || busy.value) return
  busy.value = true
  error.value = ''
  try {
    await saveCompanyBusinessDetails(props.company.id, props.kind, { rowVersion, [props.kind]: draft.value }, props.token)
    emit('saved')
    ElMessage.success(title + '已保存')
    open.value = false
  } catch (cause) { showError(cause, '公司资料保存失败，请稍后重试。') }
  finally { busy.value = false }
}
</script>

<template>
  <el-dialog v-model="open" :title="title" width="min(600px, calc(100vw - 24px))" :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy" @closed="emit('close', dialogId)">
    <p class="company-detail-name"><CompanyName :name="company.name" /></p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <form v-if="draft" class="company-detail-fields" @submit.prevent="save">
      <label v-for="[key, label, length] in fields" :key="key" :class="{ 'company-detail-wide': key === 'address' || key === 'bankAccount' }">
        {{ label }}<input v-model="draft[key]" :maxlength="length" :placeholder="key === 'title' ? company.name : undefined" :disabled="busy">
      </label>
    </form>
    <p v-else-if="busy">正在加载公司资料…</p>
    <template #footer>
      <button type="button" class="pdm-secondary-action" :disabled="busy" @click="open = false">取消</button>
      <button type="button" class="pdm-primary-action" :disabled="!draft || busy" @click="save">保存</button>
    </template>
  </el-dialog>
</template>

<style scoped>
.company-detail-name{margin:0 0 12px;font-weight:600}
.company-detail-fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}
.company-detail-fields label{display:grid;gap:5px;min-width:0}
.company-detail-fields input{width:100%;min-width:0;border:1px solid var(--pdm-border);padding:0 10px;background:var(--pdm-surface);color:var(--pdm-text)}
.company-detail-wide{grid-column:1/-1}
@media(max-width:520px){.company-detail-fields{grid-template-columns:minmax(0,1fr)}}
</style>
