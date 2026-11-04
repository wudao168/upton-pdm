<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { copyText } from '../copyText'

export type DetailColumn = { key: string; label: string; parentIndex: number; width?: number }
export type DetailRow = Record<string, string | number | undefined>
const props = withDefaults(defineProps<{ columns: DetailColumn[]; rows: DetailRow[]; label: string; emptyText?: string }>(), { emptyText: '暂无物料明细' })
const root = ref<HTMLElement>()
const measured = ref<number[]>([])
const padding = ref<string[]>([])
const alignment = ref<string[]>([])
const embedded = ref(false)
const count = computed(() => measured.value.length || Math.max(...props.columns.map(column => column.parentIndex)) + 1)
const widths = computed(() => Array.from({ length: count.value }, (_, index) => measured.value[index] || props.columns.find(column => column.parentIndex === index)?.width || (index === 0 ? 32 : 100)))
const total = computed(() => widths.value.reduce((sum, width) => sum + width, 0))
const fields = computed(() => new Map(props.columns.map(column => [column.parentIndex, column])))
let observer: ResizeObserver | undefined, mutation: MutationObserver | undefined, frame = 0
let parent: HTMLTableElement | undefined
let cells: HTMLTableCellElement[] = []
function measure() {
  frame = 0
  const row = Array.from(parent?.tBodies[0]?.rows ?? []).find(row => row.classList.contains('el-table__row'))
  const next = Array.from(row?.cells ?? [])
  if (!next.length) return
  for (const cell of cells) if (!next.includes(cell)) observer?.unobserve(cell)
  for (const cell of next) if (!cells.includes(cell)) observer?.observe(cell)
  cells = next
  const values = cells.map(cell => cell.getBoundingClientRect().width)
  if (values.some(width => width <= 0)) return
  if (values.join('|') !== measured.value.join('|')) measured.value = values
  padding.value = cells.map(cell => { const style = getComputedStyle(cell.querySelector('.cell') ?? cell); return `0 ${style.paddingRight} 0 ${style.paddingLeft}` })
  alignment.value = cells.map(cell => getComputedStyle(cell.querySelector('.cell') ?? cell).textAlign)
}
function schedule() { if (!frame) frame = requestAnimationFrame(measure) }
onMounted(async () => {
  await nextTick()
  parent = root.value?.closest('table') ?? undefined
  if (!parent) return
  embedded.value = true
  if (typeof ResizeObserver !== 'undefined') observer = new ResizeObserver(schedule)
  if (parent.tBodies[0]) { mutation = new MutationObserver(schedule); mutation.observe(parent.tBodies[0], { childList: true }) }
  measure()
})
onBeforeUnmount(() => { observer?.disconnect(); mutation?.disconnect(); cancelAnimationFrame(frame) })
function value(row: DetailRow, key: string) { return row[key] ?? '—' }
</script>

<template>
  <div ref="root" class="pdm-aligned-details" :class="{ 'is-embedded': embedded }">
    <table class="pdm-aligned-detail-table pdm-fit-columns" :aria-label="label" :style="{ width: `${total}px` }">
      <colgroup><col v-for="(width, index) in widths" :key="index" :data-pdm-fixed-width="width" :style="{ width: `${width}px` }"></colgroup>
      <thead><tr><th v-for="(_, index) in widths" :key="index" :data-field="fields.get(index)?.key" :style="{ padding: padding[index], textAlign: alignment[index] as 'left' }">{{ fields.get(index)?.label }}</th></tr></thead>
      <tbody>
        <tr v-for="(row, rowIndex) in rows" :key="rowIndex">
          <td v-for="(_, index) in widths" :key="index" :data-field="fields.get(index)?.key" :style="{ padding: padding[index], textAlign: alignment[index] as 'left' }">
            <template v-if="fields.get(index)">
              <button v-if="['code', 'model'].includes(fields.get(index)!.key) && row[fields.get(index)!.key]" type="button" class="pdm-text-action" :aria-label="`复制${fields.get(index)!.key === 'code' ? '料号' : '型号'} ${row[fields.get(index)!.key]}`" @click.stop="copyText(String(row[fields.get(index)!.key]), fields.get(index)!.key === 'code' ? '料号' : '型号')">{{ value(row, fields.get(index)!.key) }}</button>
              <span v-else :title="String(value(row, fields.get(index)!.key))" v-stocked-material="['code', 'name'].includes(fields.get(index)!.key) ? String(row.code ?? '') : ''">{{ value(row, fields.get(index)!.key) }}</span>
            </template>
          </td>
        </tr>
        <tr v-if="!rows.length"><td :colspan="widths.length">{{ emptyText }}</td></tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.pdm-aligned-details{min-width:0;max-width:100%;overflow:auto}.pdm-aligned-details.is-embedded{max-width:none;overflow:visible}
.pdm-aligned-detail-table{table-layout:fixed;border-collapse:collapse;font-size:var(--pdm-font-size-body);background:var(--pdm-surface)}
.pdm-aligned-detail-table th,.pdm-aligned-detail-table td{box-sizing:border-box;height:30px;padding:0 8px;border:0;border-bottom:1px solid var(--pdm-container-border);text-align:left;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.pdm-aligned-detail-table th{font-weight:500;background:var(--pdm-table-header)}.pdm-aligned-detail-table button{max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
</style>
