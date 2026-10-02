<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RefreshCw, Save } from '@lucide/vue'
import { getProjectBudget, saveProjectBudget, getBudgetAssessments } from '../api'
import { budgetCategories, calculateBudgetRow, uppercaseMoney } from '../projectBudget'
import type { BudgetLine, ProjectBudget, AssessmentDirectory } from '../projectBudget'
import BudgetAssessmentManager from './BudgetAssessmentManager.vue'
import BudgetNotesDialog from './BudgetNotesDialog.vue'
import BudgetSettlementCards from './BudgetSettlementCards.vue'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ project: { id: string }; token?: string }>()
const subTab = ref<'summary' | 'assessment'>('summary')
const assessmentDirty = ref(false)
const assessments = ref<AssessmentDirectory>({ sheets: [], amounts: {} })
const budget = ref<ProjectBudget | null>(null)
const draft = ref<BudgetLine[]>([])
const orderCategories = ref<Record<string, string>>({})
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const noteCategory = ref<string | null>(null)
const noteSaving = ref(false)
const settlementSaving = ref(false)
const categoryNotes = (category: string) => (budget.value?.notes ?? []).filter(note => note.category === category).slice().reverse()
const latestNote = (category: string) => categoryNotes(category)[0]?.content ?? draft.value.find(line => line.category === category)?.note ?? ''
function openNotes(category: string) { noteCategory.value = category }
function settlementUpdated(result: ProjectBudget) {
  if (budget.value && props.project.id === result.projectId) budget.value = { ...budget.value, settlementLines: result.settlementLines, settlementAmount: result.settlementAmount, bonusRate: result.bonusRate, bonusAmount: result.bonusAmount }
  notesAdded(result)
}
function notesAdded(result: ProjectBudget) {
  if (budget.value && props.project.id === result.projectId) budget.value = { ...budget.value, notes: result.notes, rowVersion: result.rowVersion, updatedBy: result.updatedBy, updatedAt: result.updatedAt }
  const sheet = assessments.value.sheets.find(sheet => sheet.projectId === result.projectId)
  if (sheet) { sheet.rowVersion = result.rowVersion; sheet.notes = result.notes?.filter(note => note.category === 'Assessment') ?? [] }
}
let loadSequence = 0
const rows = computed(() => draft.value.map(input => {
  const orders = budget.value?.orders.filter(order => orderCategories.value[order.key] === input.category) ?? []
  const amount = orders.length && orders.every(order => order.amount != null) ? orders.reduce((sum, order) => sum + order.amount!, 0) : null
  return calculateBudgetRow(input, amount, orders.some(order => order.amount == null))
}))
const groups = computed(() => [
  { title: '物料成本', labor: false, rows: rows.value.filter(row => !budgetCategories.find(category => category.key === row.input.category)?.labor) },
  { title: '人工成本', labor: true, rows: rows.value.filter(row => budgetCategories.find(category => category.key === row.input.category)?.labor) },
])
const total = (values: Array<number | null>) => values.some(value => value != null) ? values.reduce<number>((sum, value) => sum + (value ?? 0), 0) : null
const remaining = (row: { budgetAmount: number | null; actualAmount: number | null }) => row.budgetAmount == null || row.actualAmount == null ? null : Math.round((row.budgetAmount - row.actualAmount) * 100) / 100
const groupTotals = (items: typeof rows.value) => ({ budgetAmount: total(items.map(row => row.budgetAmount)), actualAmount: total(items.map(row => row.actualAmount)), remaining: total(items.map(remaining)), assessmentAmount: total(items.map(row => assessments.value.amounts[row.input.category] ?? null)) })
const usageAlert = (row: { budgetAmount: number | null; actualAmount: number | null }) => row.budgetAmount == null || row.actualAmount == null ? 'Incomplete'
  : row.actualAmount > row.budgetAmount ? 'Overrun' : row.budgetAmount > 0 && row.actualAmount >= row.budgetAmount * .9 ? 'Warning' : 'Normal'
const ratio = (amount: number | null, base: number | null) => amount == null || base == null || base <= 0 ? '—' : `${(amount / base * 100).toFixed(1)}%`
const cards = computed(() => {
  const planned = total(rows.value.map(row => row.budgetAmount))
  const actual = total(rows.value.map(row => row.actualAmount))
  return [
    { title: '预算金额', value: planned, proportion: null, status: '', warning: false },
    { title: '实际金额及占比', value: actual, proportion: ratio(actual, planned), status: usageAlert({ budgetAmount: planned, actualAmount: actual }), warning: planned != null && actual != null && actual > planned },
    { title: '物料成本及占比', value: groupTotals(groups.value[0]!.rows).actualAmount, proportion: ratio(groupTotals(groups.value[0]!.rows).actualAmount, actual), status: 'Material', warning: false },
    { title: '人工成本及占比', value: groupTotals(groups.value[1]!.rows).actualAmount, proportion: ratio(groupTotals(groups.value[1]!.rows).actualAmount, actual), status: 'Labor', warning: false },
  ]
})
const changed = computed(() => !!budget.value && (JSON.stringify(draft.value) !== JSON.stringify(budget.value.rows.map(row => row.input))
  || budget.value.orders.some(order => orderCategories.value[order.key] !== order.category)))
const editable = computed(() => !!budget.value?.canEdit && !loading.value && !saving.value)
const budgetEditable = computed(() => editable.value && (budget.value?.canEditBudget ?? budget.value?.canEdit))
const actualEditable = computed(() => editable.value && (budget.value?.canEditActual ?? budget.value?.canEdit))
const name = (category: string) => budgetCategories.find(item => item.key === category)?.name ?? category
const money = (value: number | null) => value == null ? '未录入' : `¥ ${value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const alertName = (value: string) => ({ Overrun: '超预算', Warning: '接近预算', Incomplete: '待完善', Normal: '正常' }[value] ?? value)
function accept(result: ProjectBudget) {
  budget.value = result
  draft.value = result.rows.map(row => ({ ...row.input }))
  orderCategories.value = Object.fromEntries(result.orders.map(order => [order.key, order.category]))
}
async function load() {
  const sequence = ++loadSequence
  budget.value = null
  draft.value = []
  assessments.value = { sheets: [], amounts: {} }
  error.value = ''
  if (!props.token) { error.value = '请登录后查看项目预算。'; return }
  loading.value = true
  try { const [result, evaluation] = await Promise.all([getProjectBudget(props.project.id, props.token), getBudgetAssessments(props.project.id, props.token)]); if (sequence === loadSequence) { accept(result); assessments.value = evaluation; assessmentDirty.value = false } }
  catch (cause) { if (sequence === loadSequence) error.value = cause instanceof Error ? cause.message : '预算加载失败' }
  finally { if (sequence === loadSequence) loading.value = false }
}
async function save() {
  if (!props.token || !budget.value || saving.value) return
  const projectId = props.project.id
  const sequence = loadSequence
  saving.value = true
  try {
    const result = await saveProjectBudget(projectId, { lines: draft.value, expectedRowVersion: budget.value.rowVersion, orderCategories: orderCategories.value }, props.token)
    const evaluation = await getBudgetAssessments(projectId, props.token)
    if (sequence === loadSequence) { accept(result); assessments.value = evaluation; ElMessage.success('预算已保存') }
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '预算保存失败') }
  finally { saving.value = false }
}
watch(() => [props.project.id, props.token], () => { subTab.value = 'summary'; assessmentDirty.value = false; noteCategory.value = null; load() }, { immediate: true })
</script>

<template>
  <section class="pdm-budget" v-loading="loading">
    <nav class="pdm-budget__tabs pdm-project-subtabs pdm-segmented" aria-label="预算页面" role="tablist"><button role="tab" :aria-selected="subTab === 'summary'" :class="{ 'is-active': subTab === 'summary' }" :disabled="assessmentDirty" @click="subTab = 'summary'">预算汇总</button><button role="tab" :aria-selected="subTab === 'assessment'" :class="{ 'is-active': subTab === 'assessment' }" :disabled="changed" @click="subTab = 'assessment'">预算评估</button></nav>
    <BudgetNotesDialog v-if="budget" :model-value="noteCategory !== null" :project-id="project.id" :category="noteCategory ?? ''" :title="`${noteCategory ? name(noteCategory) : ''}备注`" :notes="budget.notes ?? []" :row-version="budget.rowVersion" :token="token" :busy="saving" @update:model-value="value => { if (!value) noteCategory = null }" @added="notesAdded" @saving="noteSaving = $event" />
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <template v-if="budget">
      <BudgetAssessmentManager v-if="subTab === 'assessment'" :directory="assessments" :token="token" @saved="load" @note-added="notesAdded" @dirty="assessmentDirty = $event" />
      <div v-if="subTab === 'summary'" class="pdm-budget__summary">
      <div class="pdm-budget__cards" :class="{ 'is-settlement-hidden': !budget.canViewSettlement }">
        <BudgetSettlementCards :budget="budget" :token="token" :busy="saving || noteSaving" @updated="settlementUpdated" @saving="settlementSaving = $event" />
        <article v-for="card in cards" :key="card.title" :class="{ 'is-overrun': card.warning }"><div class="pdm-budget__card-heading"><span :title="card.title">{{ card.title }}</span></div><div class="pdm-budget__card-value"><strong>{{ money(card.value) }}</strong><strong v-if="card.proportion !== null" class="pdm-budget__ratio" :data-status="card.status">{{ card.proportion }}</strong></div><span><template v-if="card.value != null"><span v-if="card.title !== '预算金额'" class="pdm-budget__currency">人民币</span>{{ uppercaseMoney(card.value).slice(3) }}</template><template v-else>—</template></span></article>
      </div>
      <section class="pdm-panel pdm-budget__section pdm-budget__costs">
        <div v-for="(group, index) in groups" :key="group.title" class="pdm-budget__group">
        <header><h3>{{ group.title }}</h3><div v-if="index === 0" class="pdm-budget__actions">
          <button type="button" class="pdm-secondary-action" :disabled="loading || saving || noteSaving || settlementSaving || changed" @click="load"><RefreshCw :size="14" />刷新</button>
          <button v-if="budget?.canEdit" type="button" class="pdm-primary-action" :disabled="saving || noteSaving || settlementSaving || loading || !changed" @click="save"><Save :size="14" />{{ saving ? '保存中…' : '保存预算' }}</button>
        </div></header>
        <div class="pdm-budget__scroll">
          <table>
            <colgroup><col style="width: 13%" /><col style="width: 15%" /><col style="width: 15%" /><col style="width: 15%" /><col style="width: 15%" /><col style="width: 9%" /><col style="width: 18%" /></colgroup>
            <thead>
              <tr><th>费用分类</th><th class="budget-plan">{{ group.labor ? '预算工时费用' : '预算金额' }}</th><th>评估金额</th><th class="budget-actual">{{ group.labor ? '实际工时费用' : '实际金额' }}</th><th>{{ group.labor ? '剩余工时费用' : '剩余预算' }}</th><th>预警</th><th>备注</th></tr>
            </thead>
            <tbody>
              <tr v-for="row in group.rows" :key="row.input.category">
                <th>{{ name(row.input.category) }}</th>
                <td class="budget-plan"><el-input-number v-model="row.input.budgetAmount" :controls="false" :min="0" :max="1000000000" :precision="2" :disabled="!budgetEditable" aria-label="预算金额" /></td>
                <td>{{ ['RiskReserve', 'Logistics', 'Other'].includes(row.input.category) ? '—' : money(assessments.amounts[row.input.category] ?? null) }}</td>
                <td class="budget-actual"><el-input-number v-model="row.input.actualAmount" :controls="false" :min="0" :max="1000000000" :precision="2" :disabled="!(row.input.category === 'RiskReserve' ? budgetEditable : actualEditable)" aria-label="实际金额" /></td>
                <td :class="{ 'is-overrun': (remaining(row) ?? 0) < 0 }">{{ money(remaining(row)) }}</td>
                <td><span class="pdm-budget__alert" :data-status="usageAlert(row)">{{ alertName(usageAlert(row)) }}</span></td>
                <td><button type="button" class="pdm-budget__note-preview" :title="latestNote(row.input.category) || '添加备注'" :disabled="saving || noteSaving" :aria-label="`${name(row.input.category)}备注`" @click="openNotes(row.input.category)">{{ latestNote(row.input.category) || '添加备注' }}</button></td>
              </tr>
            </tbody>
            <tfoot><tr><th>合计</th><td>{{ money(groupTotals(group.rows).budgetAmount) }}</td><td>{{ money(groupTotals(group.rows).assessmentAmount) }}</td><td>{{ money(groupTotals(group.rows).actualAmount) }}</td><td :class="{ 'is-overrun': (groupTotals(group.rows).remaining ?? 0) < 0 }">{{ money(groupTotals(group.rows).remaining) }}</td><td><span class="pdm-budget__alert" :data-status="usageAlert(groupTotals(group.rows))">{{ alertName(usageAlert(groupTotals(group.rows))) }}</span></td><td>—</td></tr></tfoot>
          </table>
        </div>
        </div>
      </section>
      <section v-if="budget.orders.length" class="pdm-panel pdm-budget__section">
        <header><h3>采购订单引用</h3></header>
        <div class="pdm-budget__scroll"><table><thead><tr><th>采购订单</th><th>行号</th><th>料号</th><th>名称</th><th>未税金额</th><th>费用分类</th></tr></thead><tbody>
          <tr v-for="order in budget.orders" :key="order.key"><td>{{ order.documentNumber }}</td><td>{{ order.lineNumber }}</td><td>{{ order.materialCode }}</td><td>{{ order.itemName }}</td><td>{{ money(order.amount) }}</td><td><el-select popper-class="pdm-budget-select-popper" v-model="orderCategories[order.key]" :disabled="!budgetEditable" aria-label="订单费用分类"><el-option v-for="category in budgetCategories.filter(item => ['Standard', 'Nonstandard', 'Equipment'].includes(item.key))" :key="category.key" :value="category.key" :label="category.name" /></el-select></td></tr>
        </tbody></table></div>
      </section>
      </div>
    </template>
  </section>
</template>
