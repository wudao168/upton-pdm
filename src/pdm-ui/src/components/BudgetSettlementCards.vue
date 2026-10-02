<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ProjectBudget, SettlementLine } from '../projectBudget'
import { uppercaseMoney } from '../projectBudget'
import { saveProjectSettlement, saveProjectBonusRate } from '../api'
import { ElMessage } from '../statusMessage'
const props = defineProps<{ budget: ProjectBudget; token?: string; busy?: boolean }>()
const emit = defineEmits<{ updated: [result: ProjectBudget]; saving: [value: boolean] }>()
const mode = ref<'settlement' | 'bonus' | null>(null)
const lines = ref<SettlementLine[]>([])
const percent = ref<number | null>(.5)
const saving = ref(false)
const money = (amount: number | null | undefined) => amount == null ? '未录入' : `¥ ${amount.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const amount = (line: SettlementLine) => line.quantity == null || line.unitPrice == null ? null : Math.round(line.quantity * line.unitPrice * 100) / 100
const sum = computed(() => lines.value.some(line => amount(line) != null) ? lines.value.reduce((total, line) => total + (amount(line) ?? 0), 0) : null)
const previewBonus = computed(() => props.budget.settlementAmount == null || percent.value == null ? null : props.budget.settlementAmount < 200000 ? 0 : Math.round(props.budget.settlementAmount * percent.value) / 100)
const canEdit = computed(() => mode.value === 'settlement' ? !!props.budget.canEditSettlement : !!props.budget.canEditBonus)
function open(value: 'settlement' | 'bonus') {
  if (props.busy || saving.value || (value === 'bonus' && !props.budget.canEditBonus) || (value === 'settlement' && !props.budget.canViewSettlement)) return
  lines.value = (props.budget.settlementLines ?? []).map(line => ({ ...line }))
  percent.value = (props.budget.bonusRate ?? .005) * 100
  mode.value = value
}
function add() { lines.value.push({ content: '', unit: null, quantity: null, unitPrice: null, note: null }) }
async function save() {
  if (!props.token || !canEdit.value || saving.value || props.busy || percent.value == null) return
  const projectId = props.budget.projectId
  saving.value = true; emit('saving', true)
  try {
    const result = mode.value === 'settlement'
      ? await saveProjectSettlement(projectId, { lines: lines.value, expectedRowVersion: props.budget.rowVersion }, props.token)
      : await saveProjectBonusRate(projectId, { rate: percent.value / 100, expectedRowVersion: props.budget.rowVersion }, props.token)
    if (projectId === props.budget.projectId) { emit('updated', result); mode.value = null; ElMessage.success('已保存') }
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '保存失败') }
  finally { saving.value = false; emit('saving', false) }
}
watch(() => props.budget.projectId, () => { mode.value = null })
</script>
<template>
  <article v-if="budget.canViewSettlement" class="pdm-budget__editable-card" role="button" tabindex="0" aria-label="结算金额明细" @click="open('settlement')" @keydown.enter="open('settlement')" @keydown.space.prevent="open('settlement')"><div class="pdm-budget__card-heading"><span>结算金额</span></div><div class="pdm-budget__card-value"><strong>{{ money(budget.settlementAmount) }}</strong></div><span><template v-if="budget.settlementAmount != null">{{ uppercaseMoney(budget.settlementAmount).slice(3) }}</template><template v-else>—</template></span></article>
  <article :class="{ 'pdm-budget__editable-card': budget.canEditBonus }" :role="budget.canEditBonus ? 'button' : undefined" :tabindex="budget.canEditBonus ? 0 : undefined" :aria-label="budget.canEditBonus ? '项目奖金比例' : undefined" @click="open('bonus')" @keydown.enter="open('bonus')" @keydown.space.prevent="open('bonus')"><div class="pdm-budget__card-heading"><span>项目奖金</span></div><div class="pdm-budget__card-value"><strong>{{ money(budget.bonusAmount) }}</strong><strong v-if="budget.canEditBonus" class="pdm-budget__ratio" data-status="Material">{{ Number(((budget.bonusRate ?? .005) * 100).toFixed(4)) }}%</strong></div><span><template v-if="budget.bonusAmount != null">{{ uppercaseMoney(budget.bonusAmount).slice(3) }}</template><template v-else>—</template></span></article>
  <el-dialog :model-value="mode !== null" :title="mode === 'settlement' ? '结算金额明细' : '项目奖金'" :width="mode === 'settlement' ? '1000px' : '460px'" :close-on-click-modal="false" :close-on-press-escape="!saving" :show-close="!saving" @update:model-value="(value: boolean) => { if (!value) mode = null }">
    <template v-if="mode === 'settlement'">
      <div class="pdm-budget__actions pdm-settlement-actions"><button v-if="canEdit" class="pdm-secondary-action" :disabled="saving" @click="add">添加明细</button></div>
      <div class="pdm-budget__scroll pdm-settlement-table"><table><colgroup><col style="width:6%" /><col style="width:24%" /><col style="width:8%" /><col style="width:10%" /><col style="width:14%" /><col style="width:16%" /><col style="width:16%" /><col v-if="canEdit" style="width:6%" /></colgroup><thead><tr><th>序号</th><th>委托工作内容</th><th>单位</th><th>数量</th><th>单价（元）</th><th>金额（元）</th><th>备注</th><th v-if="canEdit">操作</th></tr></thead><tbody><tr v-for="(line, index) in lines" :key="index"><td>{{ index + 1 }}</td><td><el-input v-model="line.content" :disabled="!canEdit || saving" maxlength="200" aria-label="委托工作内容" /></td><td><el-input v-model="line.unit" :disabled="!canEdit || saving" maxlength="20" aria-label="结算单位" /></td><td><el-input-number v-model="line.quantity" :disabled="!canEdit || saving" :controls="false" :min="0" :max="1000000" :precision="2" aria-label="结算数量" /></td><td><el-input-number v-model="line.unitPrice" :disabled="!canEdit || saving" :controls="false" :min="0" :max="1000000000" :precision="2" aria-label="结算单价" /></td><td>{{ money(amount(line)) }}</td><td><el-input v-model="line.note" :disabled="!canEdit || saving" maxlength="500" aria-label="结算备注" /></td><td v-if="canEdit"><button class="pdm-text-action is-danger" :disabled="saving" @click="lines.splice(index, 1)">删除</button></td></tr><tr v-if="!lines.length"><td :colspan="canEdit ? 8 : 7">暂无结算明细</td></tr></tbody><tfoot><tr><th colspan="5">合计：{{ uppercaseMoney(sum) }}</th><td>{{ money(sum) }}</td><td></td><td v-if="canEdit"></td></tr></tfoot></table></div>
    </template>
    <div v-else class="pdm-bonus-settings"><p>结算金额：{{ money(budget.settlementAmount) }}</p><label>奖金比例 <el-input-number v-model="percent" :controls="false" :min="0" :max="1" :precision="4" :disabled="!canEdit || saving" aria-label="奖金比例" /> %</label><p>项目奖金：{{ money(previewBonus) }}</p><p class="pdm-budget__note-empty">结算金额低于200,000元时奖金为0；达到200,000元后按结算金额 × 奖金比例计算，默认0.5%，最高1%。</p></div>
    <template #footer><button class="pdm-secondary-action" :disabled="saving" @click="mode = null">关闭</button><button v-if="canEdit" class="pdm-primary-action" :disabled="saving || busy || (mode === 'bonus' && percent == null)" @click="save">{{ saving ? '保存中…' : '保存' }}</button></template>
  </el-dialog>
</template>
