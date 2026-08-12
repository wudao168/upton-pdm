<script setup lang="ts">
import { ref, watch } from 'vue'
import type { BomItem } from '../types'

const props = defineProps<{ mechanical: BomItem[]; electrical: BomItem[]; pending: boolean }>()
const emit = defineEmits<{
  save: [kind: 'Mechanical' | 'Electrical', items: BomItem[]]
  import: [kind: 'Mechanical' | 'Electrical', file: File]
  export: [kind: 'Mechanical' | 'Electrical']
}>()
const kind = ref<'Mechanical' | 'Electrical'>('Mechanical')
const rows = ref<BomItem[]>([])
const fileInput = ref<HTMLInputElement>()

function refreshRows() {
  rows.value = (kind.value === 'Mechanical' ? props.mechanical : props.electrical).map(item => ({ ...item }))
}

watch([kind, () => props.mechanical, () => props.electrical], refreshRows, { immediate: true, deep: true })

function addRow() {
  rows.value.push({ sequence: rows.value.length + 1, drawingNumber: '', name: '', quantity: 1, unit: '件', revision: 'W1', complete: false })
}

function removeRow(index: number) {
  rows.value.splice(index, 1)
  rows.value.forEach((row, rowIndex) => { row.sequence = rowIndex + 1 })
}

function selectImport() {
  fileInput.value?.click()
}

function importSelected(event: Event) {
  const target = event.target as HTMLInputElement
  const file = target.files?.[0]
  if (file) emit('import', kind.value, file)
  target.value = ''
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="BOM维护">
    <header class="pdm-manager-heading">
      <div><h2>BOM维护</h2><p>机械BOM来自结构快照；电气BOM可手工维护或导入标准XLSX。</p></div>
      <div class="pdm-manager-actions">
        <button type="button" class="pdm-secondary-action" @click="selectImport">导入XLSX</button>
        <button type="button" class="pdm-secondary-action" @click="emit('export', kind)">导出XLSX</button>
        <button type="button" class="pdm-secondary-action" @click="addRow">新增物料</button>
        <button type="button" class="pdm-primary-action" :disabled="pending" @click="emit('save', kind, rows)">{{ pending ? '保存中…' : '保存BOM' }}</button>
      </div>
    </header>
    <input ref="fileInput" class="pdm-visually-hidden" type="file" accept=".xlsx" @change="importSelected">
    <div class="pdm-segmented" role="tablist">
      <button type="button" role="tab" :aria-selected="kind === 'Mechanical'" @click="kind = 'Mechanical'">机械BOM（{{ mechanical.length }}）</button>
      <button type="button" role="tab" :aria-selected="kind === 'Electrical'" @click="kind = 'Electrical'">电气BOM（{{ electrical.length }}）</button>
    </div>
    <div class="pdm-table-scroll">
      <table class="pdm-edit-table">
        <thead><tr><th>序号</th><th>图号</th><th>名称</th><th>数量</th><th>单位</th><th>材料</th><th>规格</th><th>版本</th><th>完整</th><th>操作</th></tr></thead>
        <tbody>
          <tr v-for="(row, index) in rows" :key="`${row.drawingNumber}-${index}`">
            <td><input v-model.number="row.sequence" type="number" min="1"></td>
            <td><input v-model.trim="row.drawingNumber" required></td>
            <td><input v-model.trim="row.name" required></td>
            <td><input v-model.number="row.quantity" type="number" min="0.0001" step="0.0001"></td>
            <td><input v-model.trim="row.unit" required></td>
            <td><input v-model.trim="row.material"></td>
            <td><input v-model.trim="row.specification"></td>
            <td><input v-model.trim="row.revision" required></td>
            <td><input v-model="row.complete" type="checkbox" aria-label="物料完整"></td>
            <td><button type="button" class="pdm-table-action is-danger" @click="removeRow(index)">删除</button></td>
          </tr>
          <tr v-if="rows.length === 0"><td colspan="10" class="pdm-empty-info">当前BOM为空，请新增物料或导入标准XLSX。</td></tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
