<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { compareReleaseSnapshots, compareSnapshotTime } from '../bomReleaseComparison'
import type { BomComparisonSnapshot } from '../bomReleaseComparison'
import { u9UnitName } from '../u9Units'
import { bomRevisionLabel } from '../drawingRevision'

const props = defineProps<{ snapshots: BomComparisonSnapshot[] }>()
const differencesOnly = ref(false)
const synchronized = ref(true)
const panels = ref<HTMLElement[]>([])
const selected = computed(() => props.snapshots.slice().sort(compareSnapshotTime))
const comparison = computed(() => compareReleaseSnapshots(selected.value))
const rows = computed(() => comparison.value.filter(row => !differencesOnly.value || row.cells.some(cell => ['added', 'removed', 'changed'].includes(cell.status))))
const counts = computed(() => selected.value.map((_, index) => ({
  added: comparison.value.filter(row => row.cells[index]?.status === 'added').length,
  changed: comparison.value.filter(row => row.cells[index]?.status === 'changed').length,
  removed: comparison.value.filter(row => row.cells[index]?.status === 'removed').length,
})))
function synchronize(event: Event) {
  if (!synchronized.value) return
  const source = event.target as HTMLElement
  for (const panel of panels.value) {
    if (panel === source) continue
    if (Math.abs(panel.scrollTop - source.scrollTop) > 1) panel.scrollTop = source.scrollTop
    if (Math.abs(panel.scrollLeft - source.scrollLeft) > 1) panel.scrollLeft = source.scrollLeft
  }
}
const formatDate = (value: string) => Number.isNaN(Date.parse(value)) ? '日期未记录' : new Date(value).toLocaleDateString('zh-CN')
watch([() => props.snapshots, differencesOnly], () => { for (const panel of panels.value) { panel.scrollTop = 0; panel.scrollLeft = 0 } })
</script>

<template>
  <section class="bom-release-comparison" aria-label="BOM发布包多选对比">
    <template v-if="selected.length">
      <div class="bom-release-comparison__toolbar"><div class="bom-release-comparison__legend"><span class="is-added">新增</span><span class="is-changed">修改</span><span class="is-removed">删除 / 未包含</span><span>左侧为基准 · 只读对比</span></div><div><label><input v-model="differencesOnly" type="checkbox">仅看差异</label><label><input v-model="synchronized" type="checkbox">同步滚动</label></div></div>
      <div class="bom-release-comparison__panels" :style="{ gridTemplateColumns: `repeat(${selected.length}, minmax(0, 1fr))` }">
        <article v-for="(snapshot, index) in selected" :key="snapshot.id" class="bom-release-comparison__panel" :aria-label="`${snapshot.packageNumber || snapshot.label}对比列表`">
          <header><div><strong :title="snapshot.label">{{ snapshot.label }}</strong><small :title="snapshot.packageNumber">{{ snapshot.packageNumber || '历史正式版本' }}</small></div><span v-if="index === 0" class="bom-release-comparison__base">基准</span></header>
          <div class="bom-release-comparison__counts">{{ index === 0 ? '本组对比基准' : `新增 ${counts[index]?.added}　修改 ${counts[index]?.changed}　删除 ${counts[index]?.removed}` }} · {{ formatDate(snapshot.releasedAt) }}</div>
          <div ref="panels" class="bom-release-comparison__scroll" @scroll="synchronize">
            <table class="bom-release-comparison__table pdm-fit-columns"><colgroup><col class="is-code"><col><col class="is-quantity"><col class="is-difference"></colgroup><thead><tr><th>料号</th><th>名称 / 型号</th><th>数量 / 单位</th><th>差异</th></tr></thead><tbody>
              <tr v-for="row in rows" :key="row.key" :class="`is-${row.cells[index]?.status}`">
                <td :title="row.code" v-stocked-material="row.code">{{ row.code }}</td>
                <td v-if="row.cells[index]?.item"><strong :title="row.cells[index]?.item?.name" v-stocked-material="row.code">{{ row.cells[index]?.item?.name }}</strong><small :title="`${row.cells[index]?.item?.specification || '—'} · ${row.cells[index]?.item?.brand || '—'}`">{{ row.cells[index]?.item?.specification || '—' }} · {{ row.cells[index]?.item?.brand || '—' }}</small></td><td v-else><strong>—</strong><small>该发布包未包含</small></td>
                <td class="is-quantity">{{ row.cells[index]?.item?.quantity ?? '—' }}<small v-if="row.cells[index]?.item" :title="`存档版本：${row.cells[index]!.item!.revision}`">{{ u9UnitName(row.cells[index]!.item!.unit) }} · {{ bomRevisionLabel(row.cells[index]!.item!) }}</small></td>
                <td class="is-difference" :title="row.cells[index]?.changes.join('；')"><span>{{ row.cells[index]?.label }}</span></td>
              </tr>
              <tr v-if="!rows.length"><td colspan="4" class="bom-release-comparison__empty">{{ differencesOnly ? '无差异' : '该发布包无BOM明细' }}</td></tr>
            </tbody></table>
          </div>
          <footer>{{ rows.length }} 个对齐行 · 已发布快照</footer>
        </article>
      </div>
    </template>
  </section>
</template>

<style scoped>
.bom-release-comparison{display:flex;flex-direction:column;flex:0 0 auto;min-width:0;margin:8px 0;gap:10px}.bom-release-comparison:has(.bom-release-comparison__panels){flex:1;min-height:280px}.bom-release-comparison input{accent-color:var(--pdm-theme-accent)}.bom-release-comparison strong{font-weight:600}.bom-release-comparison small{display:block;font-size:11px;color:var(--pdm-muted)}.bom-release-comparison__toolbar{display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap}.bom-release-comparison__toolbar>div,.bom-release-comparison__legend{display:flex;gap:16px;flex-wrap:wrap;align-items:center}.bom-release-comparison__legend>span:last-child{color:var(--pdm-muted)}.bom-release-comparison__legend .is-added{color:var(--pdm-green)}.bom-release-comparison__legend .is-changed{color:var(--pdm-orange)}.bom-release-comparison__legend .is-removed{color:var(--pdm-danger)}.bom-release-comparison__panels{display:grid;gap:10px;min-width:0;min-height:0;flex:1}.bom-release-comparison__panel{display:flex;flex-direction:column;min-width:0;min-height:0;border:1px solid var(--pdm-border);border-radius:6px;overflow:hidden}.bom-release-comparison__panel>header{display:flex;align-items:center;justify-content:space-between;gap:6px;padding:10px;background:var(--pdm-surface-muted);min-height:58px}.bom-release-comparison__panel>header>div{min-width:0}.bom-release-comparison__panel>header strong{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.bom-release-comparison__panel>header small{overflow:hidden;white-space:nowrap;text-overflow:ellipsis;margin-top:4px}.bom-release-comparison__base{color:var(--pdm-theme-accent);white-space:nowrap}.bom-release-comparison__counts{height:34px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-size:11px;color:var(--pdm-muted);padding:8px 10px;min-height:32px;border-bottom:1px solid var(--pdm-border)}.bom-release-comparison__scroll{overflow:auto;min-height:0;flex:1;max-height:600px}.bom-release-comparison table{border-collapse:collapse;width:100%;min-width:370px;table-layout:fixed;font-size:12px}.bom-release-comparison col.is-code{width:102px}.bom-release-comparison col.is-quantity{width:76px}.bom-release-comparison col.is-difference{width:88px}.bom-release-comparison th{position:sticky;top:0;background:var(--pdm-table-header);color:var(--pdm-text-soft);font-weight:500;text-align:left;padding:10px 7px;border-bottom:1px solid var(--pdm-border);white-space:nowrap;z-index:1}.bom-release-comparison td{height:68px;padding:7px;border-bottom:1px solid var(--pdm-border);vertical-align:middle}.bom-release-comparison td>strong,.bom-release-comparison td>small{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.bom-release-comparison td>small{margin-top:4px}.bom-release-comparison .is-quantity{text-align:center;font-variant-numeric:tabular-nums}.bom-release-comparison td.is-difference>span{display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;font-size:11px;overflow-wrap:anywhere}.bom-release-comparison tr.is-added{background:color-mix(in srgb,var(--pdm-green) 10%,var(--pdm-surface))}.bom-release-comparison tr.is-changed{background:color-mix(in srgb,var(--pdm-orange) 12%,var(--pdm-surface))}.bom-release-comparison tr.is-removed{background:color-mix(in srgb,var(--pdm-danger) 10%,var(--pdm-surface))}.bom-release-comparison tr.is-added .is-difference{color:var(--pdm-green)}.bom-release-comparison tr.is-changed .is-difference{color:var(--pdm-orange)}.bom-release-comparison tr.is-removed .is-difference{color:var(--pdm-danger)}.bom-release-comparison__panel>footer{font-size:11px;color:var(--pdm-muted);padding:9px 10px;border-top:1px solid var(--pdm-border)}.bom-release-comparison__empty{color:var(--pdm-muted)}@media(max-width:800px){.bom-release-comparison__panels{gap:6px}.bom-release-comparison__panel>header{padding:8px}.bom-release-comparison__counts{padding:6px;font-size:11px}}@media(max-width:620px){.bom-release-comparison__panels{grid-template-columns:1fr!important;overflow:auto}.bom-release-comparison__panel{min-height:300px;overflow:visible}.bom-release-comparison__scroll{max-height:350px}}
.bom-release-comparison .bom-release-comparison__panel .bom-release-comparison__scroll>table{min-width:420px!important;table-layout:fixed!important}.bom-release-comparison .bom-release-comparison__panel .bom-release-comparison__scroll>table td{height:64px!important;padding:8px 7px!important;line-height:18px!important}.bom-release-comparison .bom-release-comparison__panel .bom-release-comparison__scroll>table tr.is-added>td{background:var(--pdm-green-soft)!important}.bom-release-comparison .bom-release-comparison__panel .bom-release-comparison__scroll>table tr.is-changed>td{background:var(--pdm-orange-soft)!important}.bom-release-comparison .bom-release-comparison__panel .bom-release-comparison__scroll>table tr.is-removed>td{background:var(--pdm-danger-soft)!important}
</style>
