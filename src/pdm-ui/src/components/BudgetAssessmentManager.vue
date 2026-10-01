<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Plus, Save, ArrowLeft, Trash2 } from '@lucide/vue'
import { saveBudgetAssessment, saveAssessmentLaborRates } from '../api'
import { assessmentItemTotal, assessmentSum, budgetCategories, uppercaseMoney } from '../projectBudget'
import type { AssessmentDirectory, AssessmentGroup } from '../projectBudget'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { useUserDisplayName } from '../userDisplay'
const props = defineProps<{ directory: AssessmentDirectory; token?: string }>()
const emit = defineEmits<{ saved: []; dirty: [value: boolean] }>()
const displayUserName = useUserDisplayName()
const selected = ref<string | null>(null)
const draft = ref<AssessmentGroup[]>([])
const saving = ref(false)
const ratesVisible = ref(false)
const ratesDraft = ref<Record<string, number | null>>({})
const laborCategories = budgetCategories.filter(item => item.labor)
function openRates() {
  ratesDraft.value = Object.fromEntries(laborCategories.map(item => [item.key, props.directory.laborRates?.[item.key] ?? null]))
  ratesVisible.value = true
}
async function saveRates() {
  if (!props.token || !props.directory.ratesProjectId || !props.directory.canEditLaborRates) return
  saving.value = true
  try {
    await saveAssessmentLaborRates(props.directory.ratesProjectId, { rates: ratesDraft.value, expectedRowVersion: props.directory.ratesRowVersion ?? 0 }, props.token)
    ratesVisible.value = false; emit('saved'); ElMessage.success('工时单价已保存')
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '工时单价保存失败') }
  finally { saving.value = false }
}
function selectLaborType(item: AssessmentGroup['items'][number]) {
  item.unitPrice = props.directory.laborRates?.[item.laborCategory ?? ''] ?? null
}
const sheet = computed(() => props.directory.sheets.find(item => item.projectId === selected.value))
const editable = computed(() => !!sheet.value?.canEdit && !saving.value)
const dirty = computed(() => !!sheet.value && JSON.stringify(draft.value) !== JSON.stringify(sheet.value.groups))
watch(dirty, value => emit('dirty', value))
watch(() => props.directory, () => { if (sheet.value) draft.value = JSON.parse(JSON.stringify(sheet.value.groups)); else selected.value = null })
const groupTotal = (group: AssessmentGroup) => assessmentSum(group.items.map(assessmentItemTotal))
const money = (amount: number | null) => amount == null ? '未录入' : `¥ ${amount.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const assessmentRows = computed(() => props.directory.sheets.map(item => ({ ...item, materialTotal: assessmentSum(item.groups.filter(group => group.category !== 'Labor').map(groupTotal)), laborTotal: assessmentSum(item.groups.filter(group => group.category === 'Labor').map(groupTotal)) })))
const materialTotal = computed(() => assessmentSum(assessmentRows.value.map(item => item.materialTotal)))
const laborTotal = computed(() => assessmentSum(assessmentRows.value.map(item => item.laborTotal)))
const categories = [{ key: 'Standard', name: '标准件' }, { key: 'Nonstandard', name: '非标件' }, { key: 'Equipment', name: '外购设备' }, { key: 'Labor', name: '人工成本' }]
function open(id: string) { selected.value = id; draft.value = JSON.parse(JSON.stringify(sheet.value!.groups)) }
async function back() {
  if (dirty.value) { try { await ElMessageBox.confirm('返回列表将放弃未保存修改。', '返回评估列表', { confirmButtonText: '放弃并返回', cancelButtonText: '继续编辑', type: 'warning' }) } catch { return } }
  selected.value = null; draft.value = []; emit('dirty', false)
}
function newId() {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6]! & 15) | 64; bytes[8] = (bytes[8]! & 63) | 128
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
function addGroup() { draft.value.push({ id: newId(), name: '', category: 'Standard', laborCategory: null, items: [] }) }
function addItem(group: AssessmentGroup) { group.items.push({ id: newId(), name: '', model: null, brand: null, note: null, quantity: null, unitPrice: null }) }
async function removeGroup(index: number) { try { await ElMessageBox.confirm('删除该分组及其明细？', '删除分组', { type: 'warning' }); draft.value.splice(index, 1) } catch { /* User cancelled. */ } }
async function save() {
  if (!props.token || !sheet.value || !editable.value) return
  saving.value = true
  try { await saveBudgetAssessment(sheet.value.projectId, { groups: draft.value, expectedRowVersion: sheet.value.rowVersion }, props.token); emit('dirty', false); emit('saved'); ElMessage.success('评估已保存') }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '评估保存失败') }
  finally { saving.value = false }
}
</script>
<template>
  <section class="pdm-panel pdm-budget-assessment">
    <el-dialog v-model="ratesVisible" title="工时单价设置" width="460px" :close-on-click-modal="false">
      <p class="pdm-budget__rates-scope">所有项目共用</p>
      <div class="pdm-budget__scroll"><table><thead><tr><th>人工类型</th><th>工时单价（元/人天）</th></tr></thead><tbody><tr v-for="category in laborCategories" :key="category.key"><td>{{ category.name }}</td><td><el-input-number v-model="ratesDraft[category.key]" :controls="false" :min="0" :max="1000000000" :precision="2" :disabled="saving" :aria-label="`${category.name}工时单价`" /></td></tr></tbody></table></div>
      <template #footer><button class="pdm-secondary-action" :disabled="saving" @click="ratesVisible = false">取消</button><button class="pdm-primary-action" :disabled="saving" @click="saveRates">保存单价</button></template>
    </el-dialog>
    <template v-if="!sheet">
      <header><h3>预算评估</h3><button v-if="directory.canEditLaborRates" class="pdm-secondary-action" :disabled="saving" @click="openRates">工时单价设置</button></header>
      <div class="pdm-budget__scroll"><table class="pdm-budget-assessment__list"><colgroup><col style="width:10%" /><col style="width:20%" /><col style="width:10%" /><col style="width:15%" /><col style="width:15%" /><col style="width:20%" /><col style="width:10%" /></colgroup><thead><tr><th>项目号</th><th>项目名称</th><th>主设</th><th>物料成本</th><th>人工成本</th><th>更新时间</th><th>操作</th></tr></thead><tbody>
        <tr v-for="item in assessmentRows" :key="item.projectId" tabindex="0" :aria-label="`打开${item.projectCode}预算评估`" @click.stop="open(item.projectId)" @keydown.enter.self="open(item.projectId)" @keydown.space.self.prevent="open(item.projectId)"><td :title="item.projectCode">{{ item.projectCode }}</td><td :title="item.projectName">{{ item.projectName }}</td><td>{{ item.designLead ? item.designLead.split('、').map(value => displayUserName(value)).join('、') : '待分配' }}</td><td>{{ money(item.materialTotal) }}</td><td>{{ money(item.laborTotal) }}</td><td>{{ item.updatedAt ? new Date(item.updatedAt).toLocaleString('zh-CN') : '—' }}</td><td><button class="pdm-text-action" @click.stop="open(item.projectId)">{{ item.canEdit ? '编辑评估' : '查看评估' }}</button></td></tr>
      </tbody><tfoot><tr><th colspan="3">汇总</th><td>{{ money(materialTotal) }}</td><td>{{ money(laborTotal) }}</td><td colspan="2">合计：{{ money(assessmentSum([materialTotal, laborTotal])) }}</td></tr></tfoot></table></div>
    </template>
    <template v-else>
      <header><div><h3>{{ sheet.projectCode }} · {{ sheet.projectName }}</h3></div><div class="pdm-budget__actions"><button v-if="directory.canEditLaborRates" class="pdm-secondary-action" :disabled="saving || dirty" @click="openRates">工时单价设置</button><button class="pdm-secondary-action" :disabled="saving" @click="back"><ArrowLeft :size="14" />返回列表</button><button v-if="sheet.canEdit" class="pdm-secondary-action" :disabled="saving" @click="addGroup"><Plus :size="14" />添加分组</button><button v-if="sheet.canEdit" class="pdm-primary-action" :disabled="saving || !dirty" @click="save"><Save :size="14" />{{ saving ? '保存中…' : '保存评估' }}</button></div></header>
      <section v-for="(group, index) in draft" :key="group.id" class="pdm-assessment-group">
        <header><el-input v-model="group.name" :disabled="!editable" placeholder="分组名称" maxlength="100" aria-label="分组名称" /><el-select popper-class="pdm-budget-select-popper" v-model="group.category" :disabled="!editable" aria-label="分组类型"><el-option v-for="category in categories" :key="category.key" :label="category.name" :value="category.key" /></el-select><div class="pdm-budget__actions"><button v-if="sheet.canEdit" class="pdm-secondary-action" :disabled="saving" @click="addItem(group)"><Plus :size="14" />添加明细</button><button v-if="sheet.canEdit" class="pdm-text-action is-danger" :disabled="saving" @click="removeGroup(index)"><Trash2 :size="14" />删除分组</button></div></header>
        <div class="pdm-budget__scroll"><table><thead><tr><th>名称</th><th>{{ group.category === 'Labor' ? '人工类型' : '型号' }}</th><th>品牌</th><th>备注</th><th>{{ group.category === 'Labor' ? '工时（人/天）' : '数量' }}</th><th>{{ group.category === 'Labor' ? '工时单价（元）' : '单价（元）' }}</th><th>总价（元）</th><th v-if="sheet.canEdit">操作</th></tr></thead><tbody><tr v-for="(item, itemIndex) in group.items" :key="item.id">
          <td><el-input v-model="item.name" :disabled="!editable" maxlength="200" aria-label="明细名称" /></td><td><el-select v-if="group.category === 'Labor'" v-model="item.laborCategory" popper-class="pdm-budget-select-popper" :disabled="!editable" placeholder="选择人工类型" aria-label="人工类型" @change="selectLaborType(item)"><el-option v-for="category in laborCategories" :key="category.key" :label="category.name" :value="category.key" /></el-select><el-input v-else v-model="item.model" :disabled="!editable" maxlength="200" aria-label="型号" /></td><td><el-input v-model="item.brand" :disabled="!editable" maxlength="200" aria-label="品牌" /></td><td><el-input v-model="item.note" :disabled="!editable" maxlength="500" aria-label="明细备注" /></td><td><el-input-number v-model="item.quantity" :disabled="!editable" :controls="false" :min="0" :max="1000000" :precision="2" :aria-label="group.category === 'Labor' ? '评估工时' : '评估数量'" /></td><td><span v-if="group.category === 'Labor'">{{ item.unitPrice == null ? '未设置' : item.unitPrice.toFixed(2) }}</span><el-input-number v-else v-model="item.unitPrice" :disabled="!editable" :controls="false" :min="0" :max="1000000000" :precision="2" aria-label="评估单价" /></td><td>{{ money(assessmentItemTotal(item)) }}</td><td v-if="sheet.canEdit"><button class="pdm-text-action is-danger" :disabled="saving" @click="group.items.splice(itemIndex, 1)">删除</button></td>
        </tr></tbody><tfoot><tr><th>分组小计</th><td colspan="5" class="pdm-assessment-subtotal-uppercase">{{ uppercaseMoney(groupTotal(group)) }}</td><td>{{ money(groupTotal(group)) }}</td><td v-if="sheet.canEdit"></td></tr></tfoot></table></div>
      </section>
      <footer>评估合计：{{ money(assessmentSum(draft.map(groupTotal))) }}</footer>
    </template>
  </section>
</template>
