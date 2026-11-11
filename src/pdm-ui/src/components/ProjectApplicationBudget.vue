<script setup lang="ts">
import { computed } from 'vue'
import { budgetCategories, uppercaseMoney } from '../projectBudget'
import { applicationBudgetTotal, copyApplicationBudget, type ApplicationBudgetAmounts } from '../applicationBudget'

const props = withDefaults(defineProps<{ modelValue?: ApplicationBudgetAmounts | null; readonly?: boolean }>(), { readonly: false })
const emit = defineEmits<{ 'update:modelValue': [amounts: ApplicationBudgetAmounts] }>()
const amounts = computed(() => copyApplicationBudget(props.modelValue))
const total = computed(() => applicationBudgetTotal(amounts.value))
const groups = [{ title: '物料成本', labor: false }, { title: '人工成本', labor: true }]
const groupRows = Math.max(...groups.map(group => budgetCategories.filter(row => row.labor === group.labor).length))
const money = (value: number | null) => value == null ? '—' : '¥ ' + value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
function update(key: string, value: number | null | undefined) { emit('update:modelValue', { ...amounts.value, [key]: value ?? null }) }
</script>

<template>
  <section class="application-budget" aria-label="立项预算">
    <header><div><h3>立项预算</h3><small>{{ readonly ? '申请预算快照' : '金额单位：元；按已录入金额汇总，无费用请填0' }}</small></div><div class="application-budget__total"><span>预算总额</span><strong>{{ money(total) }}</strong><small>{{ uppercaseMoney(total) }}</small></div></header>
    <div class="application-budget__groups">
      <section v-for="group in groups" :key="group.title" :aria-label="group.title"><h4>{{ group.title }}</h4>
        <table><thead><tr><th>费用分类</th><th>{{ group.labor ? '预算工时费用' : '预算金额' }}</th></tr></thead><tbody>
          <tr v-for="category in budgetCategories.filter(row => row.labor === group.labor)" :key="category.key"><th>{{ category.name }}</th><td>
            <span v-if="readonly">{{ money(amounts[category.key] ?? null) }}</span>
            <el-input-number v-else :model-value="amounts[category.key]" :aria-label="`${category.name}预算金额`" :controls="false" :min="0" :max="1000000000" :precision="2" placeholder="请输入金额" @update:model-value="update(category.key, $event)" />
          </td></tr>
          <tr v-for="space in groupRows - budgetCategories.filter(row => row.labor === group.labor).length" :key="`space-${space}`" class="application-budget__spacer" aria-hidden="true"><td colspan="2"></td></tr>
        </tbody><tfoot><tr><th>合计</th><td>{{ money(applicationBudgetTotal(amounts, group.labor)) }}</td></tr></tfoot></table>
      </section>
    </div>
  </section>
</template>

<style scoped>
.application-budget tbody tr{height:34px}
.application-budget{min-width:0;border:1px solid var(--pdm-border);border-radius:8px;padding:10px;background:var(--pdm-surface)}.application-budget>header{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:9px}.application-budget h3{margin:0;font-size:13px;color:var(--pdm-text)}.application-budget small{font-size:11px;color:var(--pdm-muted)}
.application-budget__total{max-width:65%;min-width:0;text-align:right;white-space:normal}.application-budget__total>strong,.application-budget__total>small{display:block}.application-budget__total>strong{white-space:nowrap}.application-budget__total>small{line-height:1.5;overflow-wrap:anywhere}.application-budget__total>span{display:block;color:var(--pdm-muted);font-size:11px}.application-budget__total strong{ color:var(--pdm-text);font-size:17px;font-variant-numeric:tabular-nums}
.application-budget__groups{display:grid;grid-template-columns:1fr 1fr;gap:12px}.application-budget h4{font-size:12px;margin:0 0 6px}.application-budget table{width:100%;border-collapse:collapse;table-layout:fixed;font-size:12px}.application-budget th,.application-budget td{height:30px;padding:3px 6px;border:1px solid var(--pdm-border);text-align:center}.application-budget th{width:42%;font-weight:500}.application-budget thead,.application-budget tfoot{background:var(--pdm-table-header)}.application-budget tfoot td{font-weight:600;font-variant-numeric:tabular-nums}.application-budget :deep(.el-input-number){width:100%;height:26px;--el-component-size:26px}.application-budget :deep(.el-input__wrapper){padding-inline:7px}.application-budget :deep(input){text-align:center;font-variant-numeric:tabular-nums}.application-budget td>span{display:block;text-align:center;font-variant-numeric:tabular-nums}@media(max-width:520px){.application-budget__groups{grid-template-columns:1fr}.application-budget>header{align-items:flex-start}.application-budget small{display:block;max-width:190px}}
</style>
