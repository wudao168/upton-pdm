<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { downloadReleasePreviewArchive, listReleasePreviewItems, PdmApiError, retryReleasePreviewItem, retryReleasePreviewItems } from '../api'
import { releasePreviewStateTag } from '../releasePreviewState'
import type { BomVersion, ManufacturingBomBaseline, ReleasePackageSummary, ReleasePreviewItemResult, ReleaseScope } from '../types'

const PREVIEW_PAGE_SIZE = 50

const props = defineProps<{
  releasePackages: ReleasePackageSummary[]
  versions: BomVersion[]
  baselines: ManufacturingBomBaseline[]
  pending?: boolean
  projectId?: string
  token?: string
  canManageRelease?: boolean
}>()
const emit = defineEmits<{ open: [releasePackageId: string]; retryPreview: [releasePackageId: string] }>()

const previewItems = ref<ReleasePreviewItemResult[]>([])
const previewLoading = ref(false)
const previewBusy = ref(false)
const previewPackageFilter = ref('')
const previewPage = ref(1)
const selectedPreviewKeys = ref<string[]>([])

const streams = [
  { kind: 'Standard', label: '标准件BOM', scopes: ['StandardLongLead', 'StandardFormal', 'StandardSupplement'] as ReleaseScope[] },
  { kind: 'NonStandard', label: '非标件BOM + 图纸', scopes: ['NonStandardLongLead', 'NonStandardWithDrawing', 'NonStandardSupplement'] as ReleaseScope[] },
  { kind: 'Electrical', label: '电气BOM', scopes: ['ElectricalLongLead', 'ElectricalFormal', 'ElectricalSupplement'] as ReleaseScope[] },
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

// 图纸转出（转图）明细：与发布解耦的独立列表，支持按发布包筛选、单项重试、多选打包下载。
const previewPackages = computed(() => props.releasePackages
  .filter(item => item.state === '已发布' && item.previewState && item.previewState !== 'None')
  .sort((left, right) => (right.publishedAt ?? right.createdAt ?? '').localeCompare(left.publishedAt ?? left.createdAt ?? '')))
const filteredPreviewItems = computed(() => previewPackageFilter.value
  ? previewItems.value.filter(item => item.releasePackageId === previewPackageFilter.value)
  : previewItems.value)
const previewPageCount = computed(() => Math.max(1, Math.ceil(filteredPreviewItems.value.length / PREVIEW_PAGE_SIZE)))
const pagedPreviewItems = computed(() => filteredPreviewItems.value.slice((previewPage.value - 1) * PREVIEW_PAGE_SIZE, previewPage.value * PREVIEW_PAGE_SIZE))
const selectedPreviewItems = computed(() => filteredPreviewItems.value.filter(item => selectedPreviewKeys.value.includes(previewItemKey(item))))
const selectedFailedItems = computed(() => selectedPreviewItems.value.filter(item => !item.succeeded))
const selectedDownloadableItems = computed(() => selectedPreviewItems.value.filter(item => item.succeeded))
const failedPreviewItems = computed(() => filteredPreviewItems.value.filter(item => !item.succeeded))
const allFailedSelected = computed(() => failedPreviewItems.value.length > 0
  && failedPreviewItems.value.every(item => selectedPreviewKeys.value.includes(previewItemKey(item))))
const downloadablePreviewItems = computed(() => filteredPreviewItems.value.filter(item => item.succeeded))
const allPageSelected = computed(() => pagedPreviewItems.value.length > 0 && pagedPreviewItems.value.every(item => selectedPreviewKeys.value.includes(previewItemKey(item))))
const previewSummary = computed(() => {
  const total = filteredPreviewItems.value.length
  const failed = filteredPreviewItems.value.filter(item => !item.succeeded).length
  return { total, failed, succeeded: total - failed }
})

function previewItemKey(item: ReleasePreviewItemResult) {
  return `${item.releasePackageId}:${item.documentId}`
}

function previewFormatLabel(item: ReleasePreviewItemResult) {
  return item.format === 'Pdf' ? 'PDF' : 'STEP'
}

/** 转图重试需要发布管理权限（release.manage），没有权限时按钮置灰并给出提示。 */
const retryPermissionHint = computed(() => props.canManageRelease === false ? '需要“发布管理”权限才能重试转图' : '')

/** “重试整包”常驻显示，未选择发布包或没有权限时置灰。 */
const retryPackageHint = computed(() => previewPackageFilter.value ? retryPermissionHint.value : '先选择某个发布包才能整包重试')

function retryErrorMessage(error: unknown) {
  if (error instanceof PdmApiError && error.status === 403) return '没有重试转图的权限：需要“发布管理”权限（release.manage），请联系管理员。'
  return error instanceof Error ? error.message : '单项转出失败'
}

function formatSize(bytes = 0) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 ** 2).toFixed(1)} MB`
}

async function loadPreviewItems() {
  if (!props.projectId || !props.token) { previewItems.value = []; return }
  previewLoading.value = true
  try {
    previewItems.value = await listReleasePreviewItems(props.projectId, undefined, props.token)
    selectedPreviewKeys.value = selectedPreviewKeys.value.filter(key => previewItems.value.some(item => previewItemKey(item) === key))
    if (previewPage.value > previewPageCount.value) previewPage.value = previewPageCount.value
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '图纸转出明细加载失败')
  }
  finally {
    previewLoading.value = false
  }
}

async function retryPreviewItem(item: ReleasePreviewItemResult) {
  if (!props.token) return
  previewBusy.value = true
  try {
    await retryReleasePreviewItem(item.releasePackageId, item.documentId, props.token)
    await loadPreviewItems()
    ElMessage.success(`${item.drawingNumber} 已重新转出`)
  }
  catch (error) {
    ElMessage.error(retryErrorMessage(error))
    await loadPreviewItems()
  }
  finally {
    previewBusy.value = false
  }
}

/** 批量重试：同一发布包里的勾选失败项合成一次转换请求（引用树只下发一次），比逐条重试快得多。 */
async function retrySelectedPreviewItems() {
  const targets = selectedFailedItems.value
  if (!props.token || targets.length === 0) return
  previewBusy.value = true
  let succeededCount = 0
  const failures: string[] = []
  try {
    const byPackage = new Map<string, ReleasePreviewItemResult[]>()
    for (const item of targets) byPackage.set(item.releasePackageId, [...(byPackage.get(item.releasePackageId) ?? []), item])
    for (const [releasePackageId, items] of byPackage) {
      try {
        const result = await retryReleasePreviewItems(releasePackageId, items.map(item => item.documentId), props.token)
        succeededCount += result.items.filter(item => item.succeeded).length
        failures.push(...result.items.filter(item => !item.succeeded).map(item => `${item.drawingNumber}：${item.error ?? '仍未转出'}`))
      }
      catch (error) {
        failures.push(`${items[0].releasePackageNumber}：${retryErrorMessage(error)}`)
      }
    }
  }
  finally {
    await loadPreviewItems()
    previewBusy.value = false
  }
  if (failures.length) ElMessage.error(`重试完成：成功 ${succeededCount} 项，失败 ${failures.length} 项。${failures.slice(0, 3).join('；')}`)
  else ElMessage.success(`已重新转出 ${succeededCount} 项`)
}

async function downloadPreviewItems(items: ReleasePreviewItemResult[]) {
  const targets = items.filter(item => item.succeeded)
  if (!props.projectId || !props.token || targets.length === 0) return
  previewBusy.value = true
  try {
    await downloadReleasePreviewArchive(props.projectId, previewPackageFilter.value || undefined, targets.map(item => item.documentId), props.token)
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '打包下载失败')
  }
  finally {
    previewBusy.value = false
  }
}

function togglePreviewItem(item: ReleasePreviewItemResult, selected: boolean) {
  const key = previewItemKey(item)
  selectedPreviewKeys.value = selected
    ? [...new Set([...selectedPreviewKeys.value, key])]
    : selectedPreviewKeys.value.filter(existing => existing !== key)
}

function togglePreviewPage(selected: boolean) {
  const pageKeys = pagedPreviewItems.value.map(previewItemKey)
  selectedPreviewKeys.value = selected
    ? [...new Set([...selectedPreviewKeys.value, ...pageKeys])]
    : selectedPreviewKeys.value.filter(key => !pageKeys.includes(key))
}

/** 一键选中（或取消选中）当前筛选下的全部失败项，便于批量重试。 */
function toggleSelectFailedItems() {
  const failedKeys = failedPreviewItems.value.map(previewItemKey)
  selectedPreviewKeys.value = allFailedSelected.value
    ? selectedPreviewKeys.value.filter(key => !failedKeys.includes(key))
    : [...new Set([...selectedPreviewKeys.value, ...failedKeys])]
}

watch(() => [props.projectId, props.token, props.releasePackages.map(item => `${item.id}:${item.previewState}:${item.previewUpdatedAt}`).join('|')], () => { void loadPreviewItems() }, { immediate: true })
watch(previewPackageFilter, () => { previewPage.value = 1; selectedPreviewKeys.value = [] })
onMounted(() => { void loadPreviewItems() })

function scopeLabel(scope: ReleaseScope) {
  return ({
    StandardLongLead: '长交期BOM', StandardFormal: '正式', StandardSupplement: '增补/变更',
    ElectricalLongLead: '长交期BOM', ElectricalFormal: '正式', ElectricalSupplement: '增补/变更', NonStandardLongLead: '长交期BOM', NonStandardWithDrawing: 'BOM+图纸', NonStandardSupplement: '增补/变更', LegacyCombined: '历史组合',
  } as Record<ReleaseScope, string>)[scope]
}

function visibleChangeNumber(release: ReleasePackageSummary) {
  // 长交期等发布没有独立变更单号，这里必须留空：状态行不允许出现占位符“—”。
  return release.changeNumber && release.changeNumber !== release.number ? release.changeNumber : ''
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
          <button v-for="release in streamRef.value.packages.filter(item => item.state === '已发布').slice(0, 5)" :key="release.id" type="button" @click="emit('open', release.id)"><span>{{ release.number }} · {{ scopeLabel(release.scope) }}</span><span class="pdm-release-row-tail"><small v-if="visibleChangeNumber(release)">{{ visibleChangeNumber(release) }}</small><em v-if="releasePreviewStateTag(release)" :class="{ 'is-error': release.previewState === 'Failed' }">{{ releasePreviewStateTag(release) }}</em></span></button>
          <p v-if="!streamRef.value.latest">尚无已发布记录。</p>
        </div>
      </article>
    </div>
    <div class="pdm-release-overview-bottom">
      <section class="pdm-panel pdm-release-preview-list" aria-label="图纸转出">
        <header>
          <div>
            <h3>图纸转出</h3>
            <small>共 {{ previewSummary.total }} 项：成功 {{ previewSummary.succeeded }}、失败 {{ previewSummary.failed }}；勾选后可重试或打包下载。</small>
          </div>
          <div class="pdm-release-preview-actions">
            <label class="pdm-release-preview-filter">发布包
              <select v-model="previewPackageFilter" aria-label="按发布包筛选">
                <option value="">全部（{{ previewPackages.length }}）</option>
                <option v-for="previewPackage in previewPackages" :key="previewPackage.id" :value="previewPackage.id">{{ previewPackage.number }}</option>
              </select>
            </label>
            <button type="button" :disabled="previewBusy || !failedPreviewItems.length" :title="allFailedSelected ? '取消选中当前筛选下的全部失败项' : '选中当前筛选下的全部失败项'" @click="toggleSelectFailedItems">选中失败（{{ failedPreviewItems.length }}）</button>
            <button type="button" :disabled="previewBusy || !selectedDownloadableItems.length" :title="selectedPreviewItems.length > selectedDownloadableItems.length ? '只打包下载勾选中已转出的 STEP/PDF' : ''" @click="downloadPreviewItems(selectedDownloadableItems)">下载选中（{{ selectedDownloadableItems.length }}）</button>
            <button type="button" :disabled="previewBusy || pending || !selectedFailedItems.length || canManageRelease === false" :title="retryPermissionHint" @click="retrySelectedPreviewItems">重试选中（{{ selectedFailedItems.length }}）</button>
            <button type="button" :disabled="previewBusy || !downloadablePreviewItems.length" @click="downloadPreviewItems(downloadablePreviewItems)">全部下载</button>
            <button type="button" :disabled="!previewPackageFilter || pending || previewBusy || canManageRelease === false" :title="retryPackageHint" @click="emit('retryPreview', previewPackageFilter)">重试整包</button>
          </div>
        </header>
        <p v-if="previewLoading" class="pdm-release-preview-empty">正在读取图纸转出明细…</p>
        <template v-else-if="filteredPreviewItems.length">
          <div class="pdm-release-preview-table-wrap">
            <table class="pdm-edit-table pdm-release-preview-table">
              <thead><tr>
                <th class="pdm-release-preview-check"><input type="checkbox" aria-label="全选当前页" :checked="allPageSelected" @change="togglePreviewPage(($event.target as HTMLInputElement).checked)"></th>
                <th>发布包</th><th>图号</th><th>名称/文件</th><th>类型</th><th>状态</th><th>大小</th><th>说明</th><th>操作</th>
              </tr></thead>
              <tbody>
                <tr v-for="item in pagedPreviewItems" :key="previewItemKey(item)">
                  <td class="pdm-release-preview-check"><input type="checkbox" :aria-label="`选择 ${item.drawingNumber}`" :checked="selectedPreviewKeys.includes(previewItemKey(item))" @change="togglePreviewItem(item, ($event.target as HTMLInputElement).checked)"></td>
                  <td>{{ item.releasePackageNumber }}</td>
                  <td>{{ item.drawingNumber }}</td>
                  <td><span class="pdm-release-preview-name" :title="item.fileName">{{ item.fileName }}</span></td>
                  <td>{{ previewFormatLabel(item) }}</td>
                  <td><span :class="item.succeeded ? 'pdm-release-preview-ok' : 'pdm-release-preview-failed'">{{ item.succeeded ? '成功' : '失败' }}</span></td>
                  <td>{{ item.succeeded ? formatSize(item.fileLength) : '—' }}</td>
                  <td class="pdm-release-preview-reason" :title="item.error || ''">{{ item.error || '' }}</td>
                  <td><div class="pdm-row-actions">
                    <button v-if="!item.succeeded" type="button" :disabled="previewBusy || pending || canManageRelease === false" :title="retryPermissionHint" @click="retryPreviewItem(item)">重试</button>
                    <button v-else type="button" :disabled="previewBusy" @click="downloadPreviewItems([item])">下载</button>
                  </div></td>
                </tr>
              </tbody>
            </table>
          </div>
          <footer class="pdm-release-preview-pager">
            <small>共 {{ filteredPreviewItems.length }} 条，单页最多 {{ PREVIEW_PAGE_SIZE }} 条，第 {{ previewPage }} / {{ previewPageCount }} 页</small>
            <div>
              <button type="button" :disabled="previewPage <= 1" @click="previewPage -= 1">上一页</button>
              <button type="button" :disabled="previewPage >= previewPageCount" @click="previewPage += 1">下一页</button>
            </div>
          </footer>
        </template>
        <p v-else class="pdm-release-preview-empty">暂无图纸转出记录：非标件BOM+图纸发布后会自动转出 STEP/PDF。</p>
      </section>
      <section class="pdm-panel pdm-release-baseline-list">
        <header><h3>制造BOM基线</h3><small>仅当三条正式流都有有效版本时生成；长交期输出不改变基线。</small></header>
        <div class="pdm-release-baseline-table-wrap">
          <table class="pdm-edit-table"><thead><tr><th>基线</th><th>变更单号</th><th>生成时间</th></tr></thead><tbody><tr v-for="baseline in baselines" :key="baseline.id"><td>{{ baseline.label }}</td><td>{{ baselineChangeNumber(baseline) }}</td><td>{{ new Date(baseline.createdAt).toLocaleString() }}</td></tr><tr v-if="!baselines.length"><td colspan="3" class="pdm-empty-info">暂无制造BOM基线。</td></tr></tbody></table>
        </div>
      </section>
    </div>
  </section>
</template>

<style scoped>
.pdm-release-overview{display:flex;flex-direction:column;flex:1 1 auto;min-height:0;gap:12px}.pdm-release-overview-grid{flex:0 0 auto;display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}.pdm-release-stream-card{padding:0;overflow:hidden}.pdm-release-stream-card>header{display:flex;align-items:center;justify-content:space-between;padding:12px;border-bottom:1px solid var(--pdm-border)}.pdm-release-stream-card h3{margin:0}.pdm-release-stream-card header small{color:var(--pdm-muted)}.pdm-release-stream-card header>span{padding:4px 8px;border-radius:999px}.pdm-release-stream-card .is-active{background:#fff7ed;color:var(--pdm-orange)}.pdm-release-stream-card .is-clear{background:#ecfdf5;color:var(--pdm-green)}.pdm-release-overview-active,.pdm-release-overview-history{display:grid;gap:6px;padding:10px 12px}.pdm-release-overview-active{background:#fffbeb;border-bottom:1px solid var(--pdm-border)}.pdm-release-overview-active button,.pdm-release-overview-history button{padding:7px;border:0;border-radius:5px;background:#fff;color:var(--pdm-text);text-align:left}.pdm-release-overview-active button{display:flex;justify-content:space-between;gap:8px}.pdm-release-overview-history button{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:8px}.pdm-release-overview-history button>span:first-child{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-release-overview-active em{font-style:normal;color:var(--pdm-orange)}.pdm-release-overview-history small,.pdm-release-overview-history p{color:var(--pdm-muted)}.pdm-release-baseline-list{overflow:hidden}.pdm-release-baseline-list>header{padding:12px}.pdm-release-baseline-list h3{margin:0}.pdm-release-baseline-list small{color:var(--pdm-muted)}@media(max-width:1000px){.pdm-release-overview-grid{grid-template-columns:1fr}}
.pdm-release-overview-history .pdm-release-row-tail{display:inline-flex;align-items:center;justify-content:flex-end;gap:6px;white-space:nowrap}.pdm-release-overview-history .pdm-release-row-tail small{white-space:nowrap}.pdm-release-overview-history .pdm-release-row-tail em{font-style:normal;padding:1px 6px;border-radius:999px;background:var(--pdm-surface-soft);color:var(--pdm-muted);font-size:11px;white-space:nowrap}.pdm-release-overview-history .pdm-release-row-tail em.is-error{background:#fef2f2;color:var(--pdm-danger)}
.pdm-release-overview-bottom{flex:1 1 auto;min-height:0;display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;align-items:stretch}.pdm-release-preview-list{grid-column:1 / span 3}.pdm-release-baseline-list{grid-column:4 / span 1;display:flex;flex-direction:column;min-height:0}
.pdm-release-preview-list{display:flex;flex-direction:column;min-height:0;overflow:hidden}
.pdm-release-preview-list>header{display:flex;flex-wrap:wrap;align-items:flex-end;justify-content:space-between;gap:10px;padding:12px}
.pdm-release-preview-list h3{margin:0}
.pdm-release-preview-list small{color:var(--pdm-muted)}
.pdm-release-preview-actions{display:flex;flex-wrap:wrap;align-items:center;gap:6px;align-self:flex-end}
.pdm-release-preview-actions button{box-sizing:border-box;width:95px;min-width:95px;height:26px;padding:2px 3px;overflow:hidden;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;color:var(--pdm-text);font-size:11px;white-space:nowrap;text-overflow:ellipsis}
.pdm-release-preview-actions button:disabled{opacity:.55}
.pdm-release-preview-filter{display:inline-flex;align-items:center;gap:6px;color:var(--pdm-muted);font-size:12px}
.pdm-release-preview-filter select{box-sizing:border-box;width:200px;height:26px;padding:3px 6px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;color:var(--pdm-text);font-size:11px;white-space:nowrap;text-overflow:ellipsis}.pdm-release-preview-filter select:focus{border-color:var(--pdm-blue);outline:2px solid var(--pdm-theme-accent-focus)}
.pdm-release-preview-table-wrap{flex:1 1 auto;min-height:0;overflow:auto;border-top:1px solid var(--pdm-border)}.pdm-release-baseline-table-wrap{flex:1 1 auto;min-height:0;overflow:auto}.pdm-release-baseline-table-wrap .pdm-edit-table th,.pdm-release-baseline-table-wrap .pdm-edit-table td{white-space:nowrap}
.pdm-release-preview-table th,.pdm-release-preview-table td{padding:6px 8px;font-size:12px;white-space:nowrap}
.pdm-release-preview-check{width:34px;text-align:center}
.pdm-release-preview-name{display:inline-block;max-width:220px;overflow:hidden;text-overflow:ellipsis;vertical-align:bottom}
.pdm-release-preview-ok{padding:1px 6px;border-radius:999px;background:#ecfdf5;color:var(--pdm-green)}
.pdm-release-preview-failed{padding:1px 6px;border-radius:999px;background:#fef2f2;color:var(--pdm-danger)}
.pdm-release-preview-reason{max-width:260px;overflow:hidden;color:var(--pdm-danger);text-overflow:ellipsis}
.pdm-release-preview-empty{margin:0;padding:16px 12px;border-top:1px solid var(--pdm-border);color:var(--pdm-muted);font-size:12px}
.pdm-release-preview-pager{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:8px 12px;border-top:1px solid var(--pdm-border);background:var(--pdm-surface-soft);color:var(--pdm-muted)}
.pdm-release-preview-pager button{padding:4px 9px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;color:var(--pdm-text)}
.pdm-release-preview-pager button:disabled{opacity:.55}
@media(max-width:1200px){.pdm-release-overview-bottom{grid-template-columns:minmax(0,1fr)}.pdm-release-preview-list,.pdm-release-baseline-list{grid-column:auto}}
</style>
