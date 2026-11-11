<script setup lang="ts">
import { computed } from 'vue'
import QuantityInputNumber from './QuantityInputNumber.vue'
import { uppercaseMoney, type SettlementLine } from '../projectBudget'
import { applicationSettlementError, applicationSettlementLineAmount, applicationSettlementMaxAmount, applicationSettlementMaxLines,
  applicationSettlementMaxQuantity, applicationSettlementTotal, copyApplicationSettlement, newApplicationSettlementLine } from '../applicationSettlement'

const props = withDefaults(defineProps<{ modelValue?: SettlementLine[] | null; readonly?: boolean; disabled?: boolean }>(), { readonly: false, disabled: false })
const emit = defineEmits<{ 'update:modelValue': [lines: SettlementLine[]] }>()
const lines = computed(() => copyApplicationSettlement(props.modelValue))
const total = computed(() => applicationSettlementTotal(lines.value))
const error = computed(() => applicationSettlementError(lines.value))
const money = (value: number | null) => value == null ? '—' : '¥ ' + value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
function update<K extends keyof SettlementLine>(index: number, key: K, value: SettlementLine[K]) {
  if (props.readonly || props.disabled) return
  const next = copyApplicationSettlement(props.modelValue)
  if (!next[index]) return
  next[index] = { ...next[index], [key]: value }
  emit('update:modelValue', next)
}
function add() {
  if (!props.readonly && !props.disabled && lines.value.length < applicationSettlementMaxLines)
    emit('update:modelValue', [...lines.value, newApplicationSettlementLine()])
}
function remove(index: number) {
  if (!props.readonly && !props.disabled) emit('update:modelValue', lines.value.filter((_, row) => row !== index))
}
</script>

<template>
  <section class="application-settlement" aria-label="项目结算">
    <header>
      <div><h3>项目结算</h3><small>{{ readonly ? '申请结算快照' : '金额单位：元；数量按项填写，小计自动计算' }}</small></div>
      <div class="application-settlement__total"><span>结算总额</span><strong>{{ money(total) }}</strong><small>{{ uppercaseMoney(total) }}</small></div>
    </header>
    <div class="application-settlement__scroll">
      <table class="application-settlement__table pdm-fit-columns">
        <colgroup><col class="application-settlement__sequence"><col><col class="application-settlement__quantity"><col class="application-settlement__price"><col class="application-settlement__subtotal"><col v-if="!readonly" class="application-settlement__operation"></colgroup>
        <thead><tr><th>序号</th><th>内容</th><th>数量</th><th>单价</th><th>小计</th><th v-if="!readonly">操作</th></tr></thead>
        <tbody>
          <tr v-for="(line, index) in lines" :key="index">
            <td>{{ index + 1 }}</td>
            <td><span v-if="readonly" class="application-settlement__content">{{ line.content || '未填写' }}</span><el-input v-else :model-value="line.content" :disabled="disabled" maxlength="200" :aria-label="`结算第${index + 1}行内容`" placeholder="请输入结算内容" @update:model-value="update(index, 'content', $event)" /></td>
            <td><span v-if="readonly">{{ line.quantity ?? '—' }}</span><QuantityInputNumber v-else class="application-settlement__quantity-input" size="small" :model-value="line.quantity" :disabled="disabled" :controls="false" :min="1" :max="applicationSettlementMaxQuantity" :aria-label="`结算第${index + 1}行数量`" placeholder="数量" @update:model-value="update(index, 'quantity', $event ?? null)" /></td>
            <td><span v-if="readonly">{{ money(line.unitPrice) }}</span><el-input-number v-else :model-value="line.unitPrice" :disabled="disabled" :min="0" :max="applicationSettlementMaxAmount" :controls="false" :precision="2" :aria-label="`结算第${index + 1}行单价`" placeholder="请输入单价" @update:model-value="update(index, 'unitPrice', $event ?? null)" /></td>
            <td class="application-settlement__amount">{{ money(applicationSettlementLineAmount(line)) }}</td>
            <td v-if="!readonly"><button type="button" class="pdm-text-action" :disabled="disabled" :aria-label="`删除第${index + 1}行结算明细`" @click="remove(index)">删除</button></td>
          </tr>
          <tr v-if="!lines.length"><td :colspan="readonly ? 5 : 6" class="application-settlement__empty">{{ readonly ? '未录入项目结算明细' : '暂无结算明细，点击下方添加' }}</td></tr>
        </tbody>
      </table>
    </div>
    <div v-if="!readonly" class="application-settlement__actions"><button type="button" class="pdm-secondary-action" :disabled="disabled || lines.length >= applicationSettlementMaxLines" @click="add">添加结算明细</button><small>{{ lines.length }} / {{ applicationSettlementMaxLines }} 行</small></div>
    <p v-if="error" role="alert" class="application-settlement__error">{{ error }}</p>
  </section>
</template>

<style scoped>
.application-settlement{min-width:0;padding:10px;border:1px solid var(--pdm-border);border-radius:8px;background:var(--pdm-surface);color:var(--pdm-text)}
.application-settlement>header{display:flex;align-items:flex-start;justify-content:space-between;gap:12px;margin-bottom:9px}
.application-settlement h3{font-size:13px;margin:0}.application-settlement small{color:var(--pdm-muted);font-size:11px}
.application-settlement__total{max-width:65%;text-align:right}.application-settlement__total>span,.application-settlement__total>small{display:block;line-height:1.5}.application-settlement__total>span{color:var(--pdm-muted);font-size:11px}.application-settlement__total>small{overflow-wrap:anywhere}.application-settlement__total strong{font-size:17px;font-variant-numeric:tabular-nums}
.application-settlement__scroll{overflow-x:auto;min-width:0}.application-settlement table{width:100%;border-collapse:collapse;table-layout:fixed;font-size:12px}
.application-settlement table.application-settlement__table.pdm-fit-columns{min-width:640px!important;max-width:none!important}
.application-settlement th,.application-settlement td{height:32px;padding:3px 5px;border:1px solid var(--pdm-border);text-align:center;vertical-align:middle}.application-settlement th{font-weight:500;background:var(--pdm-table-header)}
.application-settlement__sequence{width:40px}.application-settlement__quantity{width:125px}.application-settlement__price{width:115px}.application-settlement__subtotal{width:125px}.application-settlement__operation{width:55px}
.application-settlement__content{white-space:pre-wrap;overflow-wrap:anywhere}.application-settlement__amount{font-variant-numeric:tabular-nums}.application-settlement__empty{height:44px!important;color:var(--pdm-muted)}
.application-settlement :deep(.el-input-number),.application-settlement :deep(.el-input){width:100%;--el-component-size:28px;min-height:28px}.application-settlement :deep(.el-input__wrapper){padding-inline:6px}.application-settlement :deep(input){text-align:center;font-variant-numeric:tabular-nums}
.application-settlement__actions{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-top:8px}.application-settlement__error{margin:7px 0 0;color:var(--el-color-danger);font-size:12px}
@media(max-width:520px){.application-settlement__total{max-width:60%}.application-settlement>header>div:first-child small{display:block;max-width:150px}}
</style>
