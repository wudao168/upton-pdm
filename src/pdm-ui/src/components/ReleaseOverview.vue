<script setup lang="ts">
import { computed } from 'vue'
import type { BomVersion, ManufacturingBomBaseline, ReleasePackageSummary, ReleaseScope } from '../types'

const props = defineProps<{
  releasePackages: ReleasePackageSummary[]
  versions: BomVersion[]
  baselines: ManufacturingBomBaseline[]
}>()
const emit = defineEmits<{ open: [releasePackageId: string] }>()

const streams = [
  { kind: 'Standard', label: '标准件BOM', scopes: ['StandardLongLead', 'StandardFormal', 'StandardSupplement'] as ReleaseScope[] },
  { kind: 'NonStandard', label: '非标件BOM + 图纸', scopes: ['NonStandardWithDrawing'] as ReleaseScope[] },
  { kind: 'Electrical', label: '电气BOM', scopes: ['ElectricalFormal', 'ElectricalSupplement'] as ReleaseScope[] },
  { kind: 'LegacyCombined', label: '历史组合发布', scopes: ['LegacyCombined'] as ReleaseScope[] },
].map(stream => computed(() => {
  const packages = props.releasePackages.filter(item => stream.scopes.includes(item.scope))
    .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? ''))
  const versions = props.versions.filter(item => item.kind === stream.kind).sort((left, right) => right.versionNumber - left.versionNumber)
  return {
    ...stream,
    packages,
    active: packages.filter(item => item.state !== '已发布'),
    latest: packages.find(item => item.state === '已发布'),
    version: versions.find(item => item.state === 'Released'),
  }
}))

function scopeLabel(scope: ReleaseScope) {
  return ({
    StandardLongLead: '长交期', StandardFormal: '正式', StandardSupplement: '增补/变更',
    ElectricalFormal: '正式', ElectricalSupplement: '增补/变更', NonStandardWithDrawing: 'BOM+图纸', LegacyCombined: '历史组合',
  } as Record<ReleaseScope, string>)[scope]
}

function visibleChangeNumber(release: ReleasePackageSummary) {
  return release.changeNumber && release.changeNumber !== release.number ? release.changeNumber : '—'
}

function baselineChangeNumber(baseline: ManufacturingBomBaseline) {
  const releasePackage = props.releasePackages.find(item => item.id === baseline.releasePackageId)
  return baseline.changeNumber && baseline.changeNumber !== releasePackage?.number ? baseline.changeNumber : '—'
}
</script>

<template>
  <section class="pdm-release-overview" aria-label="发布总览">
    <div class="pdm-release-overview-grid">
      <article v-for="streamRef in streams" :key="streamRef.value.kind" class="pdm-panel pdm-release-stream-card">
        <header><div><h3>{{ streamRef.value.label }}</h3><small>当前正式版 {{ streamRef.value.version?.label || '—' }}</small></div><span :class="streamRef.value.active.length ? 'is-active' : 'is-clear'">{{ streamRef.value.active.length ? `在途 ${streamRef.value.active.length}` : '无在途' }}</span></header>
        <div v-if="streamRef.value.active.length" class="pdm-release-overview-active">
          <strong>待处理</strong>
          <button v-for="release in streamRef.value.active" :key="release.id" type="button" @click="emit('open', release.id)"><span>{{ release.number }} · {{ scopeLabel(release.scope) }}</span><em>{{ release.state }}</em></button>
        </div>
        <div class="pdm-release-overview-history">
          <strong>最近发布</strong>
          <button v-for="release in streamRef.value.packages.filter(item => item.state === '已发布').slice(0, 5)" :key="release.id" type="button" @click="emit('open', release.id)"><span>{{ release.number }} · {{ scopeLabel(release.scope) }}</span><small>{{ visibleChangeNumber(release) }}</small></button>
          <p v-if="!streamRef.value.latest">尚无已发布记录。</p>
        </div>
      </article>
    </div>
    <section class="pdm-panel pdm-release-baseline-list">
      <header><h3>制造BOM基线</h3><small>仅当三条正式流都有有效版本时生成；长交期输出不改变基线。</small></header>
      <table class="pdm-edit-table"><thead><tr><th>基线</th><th>变更单号</th><th>生成人</th><th>生成时间</th></tr></thead><tbody><tr v-for="baseline in baselines" :key="baseline.id"><td>{{ baseline.label }}</td><td>{{ baselineChangeNumber(baseline) }}</td><td>{{ baseline.createdBy }}</td><td>{{ new Date(baseline.createdAt).toLocaleString() }}</td></tr><tr v-if="!baselines.length"><td colspan="4" class="pdm-empty-info">暂无制造BOM基线。</td></tr></tbody></table>
    </section>
  </section>
</template>

<style scoped>
.pdm-release-overview{display:grid;gap:12px}.pdm-release-overview-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}.pdm-release-stream-card{padding:0;overflow:hidden}.pdm-release-stream-card>header{display:flex;align-items:center;justify-content:space-between;padding:12px;border-bottom:1px solid var(--pdm-border)}.pdm-release-stream-card h3{margin:0}.pdm-release-stream-card header small{color:var(--pdm-muted)}.pdm-release-stream-card header>span{padding:4px 8px;border-radius:999px}.pdm-release-stream-card .is-active{background:#fff7ed;color:#c2410c}.pdm-release-stream-card .is-clear{background:#ecfdf5;color:#047857}.pdm-release-overview-active,.pdm-release-overview-history{display:grid;gap:6px;padding:10px 12px}.pdm-release-overview-active{background:#fffbeb;border-bottom:1px solid var(--pdm-border)}.pdm-release-overview-active button,.pdm-release-overview-history button{display:flex;justify-content:space-between;gap:8px;padding:7px;border:0;border-radius:5px;background:#fff;color:var(--pdm-text);text-align:left}.pdm-release-overview-active em{font-style:normal;color:#b45309}.pdm-release-overview-history small,.pdm-release-overview-history p{color:var(--pdm-muted)}.pdm-release-baseline-list{overflow:hidden}.pdm-release-baseline-list>header{padding:12px}.pdm-release-baseline-list h3{margin:0}.pdm-release-baseline-list small{color:var(--pdm-muted)}@media(max-width:1000px){.pdm-release-overview-grid{grid-template-columns:1fr}}
</style>
