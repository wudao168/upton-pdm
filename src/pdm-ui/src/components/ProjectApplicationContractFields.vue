<script setup lang="ts">
import { computed } from 'vue'
import QuantityInputNumber from './QuantityInputNumber.vue'
import { blankContractLine, maximumContractLines, maximumContractQuantity, type ProjectApplicationContract } from '../projectApplications'

const contract = defineModel<ProjectApplicationContract | null>()
const props = defineProps<{ required?: boolean; disabled?: boolean; readonly?: boolean }>()
const groups = computed(() => contract.value?.groups ?? [])
const rows = computed(() => groups.value.flatMap((group, groupIndex) => [
  { line: group.main, groupIndex, childIndex: null, label: `主项目 ${groupIndex + 1}` },
  ...group.children.map((line, childIndex) => ({ line, groupIndex, childIndex, label: `主项目 ${groupIndex + 1} 子项目 ${childIndex + 1}` })),
]))
const canAdd = computed(() => !props.disabled && rows.value.length < maximumContractLines)

function addMain() {
  if (!contract.value || !canAdd.value) return
  contract.value.groups ??= []
  contract.value.groups.push({ main: blankContractLine(), children: [] })
}
function addChild(groupIndex: number) {
  if (!canAdd.value) return
  contract.value?.groups?.[groupIndex]?.children.push(blankContractLine())
}
function removeRow(groupIndex: number, childIndex: number | null) {
  const group = contract.value?.groups?.[groupIndex]
  if (!contract.value?.groups || !group || props.disabled) return
  if (childIndex != null) { group.children.splice(childIndex, 1); return }
  if (group.children.length) return
  contract.value.groups.splice(groupIndex, 1)
}
</script>

<template>
  <section class="application-contract-fields" aria-label="合同资料">
    <label v-if="!readonly && contract" class="contract-number">
      <span>合同编号 <em v-if="required">*</em></span>
      <input v-model="contract.number" aria-label="合同编号" :aria-required="required" :disabled="disabled" maxlength="100" />
    </label>
    <p v-else class="contract-number">合同编号 <strong>{{ contract?.number || '未填写' }}</strong></p>
    <div class="contract-details-heading">
      <strong>合同明细 <em v-if="!readonly && required">*</em></strong>
    </div>
    <div v-if="rows.length || !contract?.details" class="contract-table-scroll">
      <div class="contract-table-content">
      <table aria-label="合同明细" class="contract-table pdm-fit-columns" :class="{ 'contract-table--editable': !readonly }">
        <thead><tr><th>名称</th><th>规格</th><th>数量</th><th>备注</th><th v-if="!readonly" class="contract-actions-heading">操作</th></tr></thead>
        <tbody>
          <tr v-for="row in rows" :key="row.label" :class="{ 'contract-child': row.childIndex != null }">
            <td>
              <div v-if="readonly" class="contract-row-name">
                <span class="contract-row-type">{{ row.childIndex == null ? row.label : `↳ 子项目 ${row.childIndex + 1}` }}</span>
                <span class="contract-display-name" :title="row.line.name">{{ row.line.name || '—' }}</span>
              </div>
              <div v-else class="contract-name-entry">
                <input v-model="row.line.name" :aria-label="`${row.label}名称`" :placeholder="row.childIndex == null ? '主项目' : '子项目'" :disabled="disabled" maxlength="200" />
              </div>
            </td>
            <td><span v-if="readonly" :title="row.line.specification">{{ row.line.specification || '—' }}</span><input v-else v-model="row.line.specification" :aria-label="`${row.label}规格`" :disabled="disabled" maxlength="200" /></td>
            <td>
              <span v-if="readonly">{{ row.line.quantity ?? '—' }}</span>
              <QuantityInputNumber v-else :key="disabled ? 'disabled' : 'enabled'" :model-value="row.line.quantity" size="small" :controls="false" :min="1" :max="maximumContractQuantity" :aria-label="`${row.label}数量`" :disabled="disabled" @update:model-value="row.line.quantity = $event ?? null" />
            </td>
            <td><span v-if="readonly" :title="row.line.remarks">{{ row.line.remarks || '—' }}</span><input v-else v-model="row.line.remarks" :aria-label="`${row.label}备注`" :disabled="disabled" maxlength="500" /></td>
            <td v-if="!readonly">
              <div class="contract-row-actions">
                <button v-if="row.childIndex == null" type="button" class="pdm-text-action" :aria-label="`为${row.label}添加子项目`" :disabled="!canAdd" @click="addChild(row.groupIndex)">添加子项目</button>
                <button type="button" class="pdm-text-action" :aria-label="`删除${row.label}`" :title="row.childIndex == null && groups[row.groupIndex]?.children.length ? '请先删除此主项目下的子项目明细' : undefined" :disabled="disabled || (row.childIndex == null && !!groups[row.groupIndex]?.children.length)" @click="removeRow(row.groupIndex, row.childIndex)">删除</button>
              </div>
            </td>
          </tr>
          <tr v-if="!rows.length"><td :colspan="readonly ? 4 : 5" class="contract-empty">暂无合同明细</td></tr>
        </tbody>
      </table>
      </div>
    </div>
    <div v-if="!readonly" class="contract-table-footer">
      <button type="button" class="pdm-secondary-action" :disabled="!canAdd" @click="addMain">添加主项目</button>
      <small>{{ rows.length }} / {{ maximumContractLines }} 行</small>
    </div>
    <div v-if="contract?.details" class="contract-legacy"><strong>原合同明细</strong><p>{{ contract.details }}</p></div>
  </section>
</template>

<style scoped>
.application-contract-fields { display: grid; gap: 8px; min-width: 0; font-size: 12px; }
.contract-number { display: flex; align-items: center; gap: 8px; margin: 0; }
.contract-number span { flex: none; white-space: nowrap; }
.contract-number input { flex: 1; max-width: 220px; height: 28px; }
.contract-details-heading, .contract-table-footer { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 6px 12px; }
.contract-table-footer small, .contract-row-type, .contract-empty { color: var(--pdm-muted); }
em { color: var(--el-color-danger); font-style: normal; }
input { min-width: 0; border: 1px solid var(--pdm-border); border-radius: 999px; background: var(--pdm-surface); color: var(--pdm-text); padding: 2px 7px; font: inherit; }
.contract-table-scroll { min-width: 0; overflow-x: auto; }
.contract-table-content { min-width: 510px; }
.contract-table { width: 100%; border-collapse: collapse; }
.contract-table th, .contract-table td { padding: 4px 5px; border-bottom: 1px solid var(--pdm-border-soft); text-align: center; vertical-align: middle; white-space: nowrap; }
.contract-table th { background: var(--pdm-table-header); }
.contract-table th:first-child { width: 34%; }
.contract-table--editable th:first-child { width: 28%; }
.contract-table th:nth-child(3) { width: 76px; }
.contract-table .contract-actions-heading { width: 124px; }
.contract-table input, .contract-table :deep(.el-input-number) { width: 100%; height: 24px; }
.contract-name-entry, .contract-row-name { align-items: center; gap: 6px; position: relative; }
.contract-name-entry { display: grid; grid-template-columns: minmax(0, 1fr); }
.contract-row-name { display: flex; justify-content: center; }
.contract-row-name > :not(.contract-display-name) { flex: none; }
.contract-display-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.contract-child :is(.contract-name-entry, .contract-row-name) { padding-inline-start: 34px; }
.contract-child :is(.contract-name-entry, .contract-row-name)::before {
  content: '';
  position: absolute;
  inset-inline-start: 7px;
  top: -4px;
  width: 18px;
  height: calc(50% + 4px);
  border-inline-start: 1px solid var(--pdm-border);
  border-bottom: 1px solid var(--pdm-border);
}
.contract-child:has(+ .contract-child) :is(.contract-name-entry, .contract-row-name)::after {
  content: '';
  position: absolute;
  inset-inline-start: 7px;
  top: 50%;
  bottom: -5px;
  border-inline-start: 1px solid var(--pdm-border);
}
.contract-row-actions { display: grid; grid-auto-flow: column; grid-auto-columns: max-content; align-items: center; justify-content: center; gap: 6px; }
.contract-row-actions button { height: 24px; white-space: nowrap; }
.contract-legacy p { margin: 4px 0 0; color: var(--pdm-muted); white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
