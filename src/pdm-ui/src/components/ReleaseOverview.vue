<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { listReleasePreviewItems } from '../api'
import { userErrorMessage } from '../userErrorMessage'
import { useProjectCenterAccess } from '../projectCenterAccess'
import { requiresReleasePreview, releasePreviewNeedsAttention, releasePreviewStateLabel, releasePreviewStateTag } from '../releasePreviewState'
import { releaseFailureMessage, releasePublishingMessage } from '../releaseFailureMessage'
import type { BomVersion, ManufacturingBomBaseline, ReleasePackageSummary, ReleasePreviewItemResult, ReleaseScope } from '../types'

const props = defineProps<{
  releasePackages: ReleasePackageSummary[]
  versions: BomVersion[]
  baselines: ManufacturingBomBaseline[]
  pending?: boolean
  projectId?: string
  token?: string
  canManageRelease?: boolean
}>()
const emit = defineEmits<{ open: [releasePackageId: string]; retryPreview: [releasePackageId: string]; previewAttention: [projectId: string, hasIssue: boolean] }>()

const { write: projectWritable } = useProjectCenterAccess()
const previewItems = ref<ReleasePreviewItemResult[]>([])
const previewLoading = ref(false)
const previewPackageFilter = ref('')
const retryRequestedId = ref('')
let previewRequestId = 0
onBeforeUnmount(() => { previewRequestId++ })

function requestConversionRetry(releasePackageId: string) {
  retryRequestedId.value = releasePackageId
  emit('retryPreview', releasePackageId)
}

const streams = [
  { kind: 'Standard', label: '标准件BOM', scopes: ['StandardLongLead', 'StandardFormal', 'StandardSupplement'] as ReleaseScope[] },
  { kind: 'NonStandard', label: '非标件BOM + 图纸', scopes: ['NonStandardLongLead', 'NonStandardWithDrawing', 'NonStandardSupplement', 'Nameplate'] as ReleaseScope[] },
  { kind: 'Electrical', label: '电气件BOM', scopes: ['ElectricalLongLead', 'ElectricalFormal', 'ElectricalSupplement'] as ReleaseScope[] },
].map(stream => computed(() => {
  const packages = props.releasePackages.filter(item => stream.scopes.includes(item.scope))
    .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? ''))
  const publishedPackages = packages.filter(item => item.state === '已发布')
  const recentBomPackageIds = new Set(publishedPackages.filter(item => item.scope !== 'Nameplate').slice(0, 5).map(item => item.id))
  const versions = props.versions.filter(item => item.kind === stream.kind).sort((left, right) => right.versionNumber - left.versionNumber)
  return {
    ...stream,
    packages,
    active: packages.filter(item => item.state !== '已发布'),
    // 铭牌原来展示全部历史；合并后仍保留，不挤占最近五项BOM记录。
    published: publishedPackages.filter(item => item.scope === 'Nameplate' || recentBomPackageIds.has(item.id) || releasePreviewNeedsAttention(item)),
    latest: packages.find(item => item.state === '已发布'),
    version: versions.find(item => item.state === 'Released'),
  }
}))

// 按发布包汇总转图结果，进度以实际可用的转出文件计。
const previewPackages = computed(() => props.releasePackages
  .filter(item => item.state === '已发布' && requiresReleasePreview(item) && item.previewState && item.previewState !== 'None')
  .sort((left, right) => (right.publishedAt ?? right.createdAt ?? '').localeCompare(left.publishedAt ?? left.createdAt ?? '')))
const previewAttentionCount = computed(() => props.releasePackages.filter(release => release.state === '已发布' && releasePreviewNeedsAttention(release)).length)
const previewRows = computed(() => {
  const itemsByPackage = new Map<string, ReleasePreviewItemResult[]>()
  for (const item of previewItems.value) {
    const items = itemsByPackage.get(item.releasePackageId)
    if (items) items.push(item)
    else itemsByPackage.set(item.releasePackageId, [item])
  }

  return previewPackages.value
    .filter(release => !previewPackageFilter.value || release.id === previewPackageFilter.value)
    .map(release => {
      const items = itemsByPackage.get(release.id) ?? []
      const counts = { '成功': 0, '失败': 0, '转换中': 0, '排队中': 0 }
      for (const item of items) counts[previewItemState(item, release)]++
      const total = items.length
      const succeeded = counts['成功']
      const failed = counts['失败']
      const running = counts['转换中']
      const queued = counts['排队中']
      const unfinished = items.filter(item => !item.succeeded)
      const incompleteAfterCompletion = release.previewState === 'Succeeded' && unfinished.length > 0
      const needsAttention = releasePreviewNeedsAttention(release)
      const waitingForServer = release.previewState === 'WaitingForServer'
      const canRetry = (needsAttention || incompleteAfterCompletion)
        && (unfinished.length === 0 || unfinished.some(hasReleasedDrawing))
      const reasons = new Set<string>()
      if (failed || incompleteAfterCompletion || needsAttention || waitingForServer) {
        for (const item of unfinished) {
          if (item.error && item.error !== '正在转换…') reasons.add(releaseFailureMessage(item.error))
        }
        if (!reasons.size && release.previewError) reasons.add(releaseFailureMessage(release.previewError))
        if (!reasons.size) reasons.add(incompleteAfterCompletion ? '还有图纸未转出，请重试。' : releasePreviewStateLabel(release))
      }
      const problem = [...reasons].filter(Boolean).join('；')
      const status = total && succeeded === total ? '已完成'
        : failed ? '有失败项'
          : incompleteAfterCompletion ? '转出不全'
            : running ? '转换中'
              : waitingForServer ? releasePreviewStateTag(release)
                : queued ? '排队中' : releasePreviewStateTag(release) || '待确认'
      const detail = `成功 ${succeeded}、转换中 ${running}、排队中 ${queued}、失败 ${failed}`
      return { release, total, succeeded, failed, status, detail, problem, canRetry, percent: total ? Math.floor(succeeded * 100 / total) : null }
    })
})
const previewSummary = computed(() => {
  const counts = previewRows.value.reduce((summary, row) => ({
    total: summary.total + row.total,
    succeeded: summary.succeeded + row.succeeded,
    failed: summary.failed + row.failed,
  }), { total: 0, succeeded: 0, failed: 0 })
  return `${previewRows.value.length} 个发布包 · 共 ${counts.total} 项 · 成功 ${counts.succeeded} · 失败 ${counts.failed}`
})

function hasReleasedDrawing(item: ReleasePreviewItemResult) {
  return !item.error?.includes('没有随本次发布转为正式版本')
}

function previewItemState(item: ReleasePreviewItemResult, release: ReleasePackageSummary) {
  if (item.succeeded) return '成功'
  if (!hasReleasedDrawing(item)) return '失败'
  if (releasePreviewNeedsAttention(release)) return '失败'
  const state = release.previewState
  if (state === 'Running') return '转换中'
  if (state === 'Pending' || state === 'WaitingForServer') return '排队中'
  if (state === 'Failed') return '失败'
  if (item.error === '正在转换…') return '转换中'
  if (!item.error || item.error === '尚未生成预览。') return '排队中'
  return '失败'
}

async function loadPreviewItems() {
  const requestId = ++previewRequestId
  const projectId = props.projectId
  const token = props.token
  if (!projectId || !token) { previewItems.value = []; previewLoading.value = false; return }
  previewLoading.value = true
  try {
    const items = await listReleasePreviewItems(projectId, undefined, token)
    if (requestId !== previewRequestId) return
    previewItems.value = items
    emit('previewAttention', projectId, items.some(item => !item.succeeded))
  }
  catch (error) {
    if (requestId === previewRequestId) ElMessage.error(userErrorMessage(error instanceof Error ? error.message : '图纸转出状态加载失败'))
  }
  finally {
    if (requestId === previewRequestId) previewLoading.value = false
  }
}

watch([() => props.projectId, () => props.token, () => props.releasePackages.map(item => `${item.id}:${item.state}:${item.previewState}:${item.previewUpdatedAt}`).sort().join('|')],
  ([projectId, token], [previousProjectId, previousToken]) => {
    if (projectId !== previousProjectId || token !== previousToken) {
      previewItems.value = []
      previewPackageFilter.value = ''
    }
    void loadPreviewItems()
  }, { immediate: true })

function scopeLabel(scope: ReleaseScope) {
  return ({
    StandardLongLead: '长交期BOM', StandardFormal: '正式', StandardSupplement: '增补/变更',
    Nameplate: '铭牌机加件',
    ElectricalLongLead: '长交期BOM', ElectricalFormal: '正式', ElectricalSupplement: '增补/变更', NonStandardLongLead: '长交期BOM', NonStandardWithDrawing: 'BOM+图纸', NonStandardSupplement: '增补/变更', LegacyCombined: '历史组合',
  } as Record<ReleaseScope, string>)[scope]
}

function visibleChangeNumber(release: ReleasePackageSummary) {
  // 长交期等发布没有独立变更单号，这里必须留空：状态行不允许出现占位符“—”。
  return release.changeNumber && release.changeNumber !== release.number ? release.changeNumber : ''
}

</script>

<template>
  <section class="pdm-release-overview" aria-label="发布总览">
    <p v-if="previewAttentionCount" class="pdm-release-preview-attention" role="alert">有 {{ previewAttentionCount }} 个非标件发布包的图纸转换未完成，请在对应发布包旁处理或重试。</p>
    <div class="pdm-release-overview-grid">
      <article v-for="streamRef in streams" :key="streamRef.value.kind" class="pdm-panel pdm-release-stream-card">
        <header class="pdm-release-overview-heading"><div class="pdm-release-heading-text"><h3>{{ streamRef.value.label }}</h3><small>当前正式版 {{ streamRef.value.version?.label || '—' }}</small></div><span :class="streamRef.value.active.length ? 'is-active' : 'is-clear'">{{ streamRef.value.active.length ? `在途 ${streamRef.value.active.length}` : '无在途' }}</span></header>
        <div v-if="streamRef.value.active.length" class="pdm-release-overview-active">
          <strong>待处理</strong>
          <div v-for="release in streamRef.value.active" :key="release.id">
          <button type="button" class="pdm-release-package-row" @click="emit('open', release.id)">
            <span class="pdm-release-package-description"><span :title="release.number">{{ release.number }}</span><small :title="visibleChangeNumber(release)">{{ scopeLabel(release.scope) }}<template v-if="visibleChangeNumber(release)"> · {{ visibleChangeNumber(release) }}</template></small></span>
            <em class="pdm-release-package-status" :class="{ 'is-error': release.state === '发布失败' || release.previewState === 'Failed' }">{{ release.state === '发布中' && release.previewState === 'Failed' ? '转图失败' : release.state }}</em>
          </button>
          <small v-if="release.state === '发布失败'" class="pdm-release-failure-reason">失败原因：{{ releaseFailureMessage(release.publishError) }}</small>
          <small v-if="release.state === '发布中'" class="pdm-release-publishing-reason">{{ releasePublishingMessage(release) }}<template v-if="release.previewUpdatedAt"> 最近转图状态更新：{{ new Date(release.previewUpdatedAt).toLocaleString('zh-CN', { hour12: false }) }}</template></small>
          <button v-if="canManageRelease && requiresReleasePreview(release) && release.state === '发布中' && ['Pending', 'WaitingForServer', 'Failed'].includes(release.previewState ?? '')" type="button" class="pdm-secondary-action pdm-release-retry-conversion" :disabled="pending" @click="requestConversionRetry(release.id)">{{ pending && retryRequestedId === release.id ? '正在提交…' : '重试转图' }}</button>
          <small v-if="retryRequestedId === release.id" class="pdm-release-retry-feedback" role="status">{{ pending ? '正在提交重试请求…' : '已请求重试。当前状态每5秒自动读取；提交失败时请查看顶部提示。' }}</small>
          </div>
        </div>
        <div class="pdm-release-overview-history">
          <strong>最近发布</strong>
          <div v-for="release in streamRef.value.published" :key="release.id" class="pdm-release-published-package" :data-release-package-id="release.id">
            <button type="button" class="pdm-release-package-row" @click="emit('open', release.id)"><span class="pdm-release-package-description"><span :title="release.number">{{ release.number }}</span><small :title="visibleChangeNumber(release)">{{ scopeLabel(release.scope) }}<template v-if="visibleChangeNumber(release)"> · {{ visibleChangeNumber(release) }}</template></small></span><span class="pdm-release-row-tail pdm-release-package-status"><span class="pdm-release-published-state">已发布</span><em v-if="releasePreviewStateTag(release)" :class="{ 'is-error': releasePreviewNeedsAttention(release) }" :title="releasePreviewStateTag(release)">{{ releasePreviewStateTag(release) }}</em></span></button>
            <div v-if="releasePreviewNeedsAttention(release)" class="pdm-release-package-retry">
              <span>{{ releasePreviewStateLabel(release) }}</span>
              <small v-if="release.previewError">{{ releaseFailureMessage(release.previewError) }}</small>
              <button v-if="canManageRelease" type="button" class="pdm-secondary-action pdm-release-retry-conversion" :disabled="pending" @click.stop="requestConversionRetry(release.id)">{{ pending && retryRequestedId === release.id ? '正在提交…' : '重试转图' }}</button>
              <small v-if="retryRequestedId === release.id" role="status">{{ pending ? '正在提交重试请求…' : '已发起重试请求，请以最新状态及顶部处理提示为准。' }}</small>
            </div>
          </div>
          <p v-if="!streamRef.value.latest">尚无已发布记录。</p>
        </div>
      </article>
      <section class="pdm-panel pdm-release-preview-list" aria-label="图纸转出">
        <header class="pdm-release-overview-heading">
          <div class="pdm-release-heading-text">
            <h3>图纸转出</h3>
            <small :title="previewSummary">{{ previewSummary }}</small>
          </div>
          <div class="pdm-release-preview-actions">
            <label class="pdm-release-preview-filter">发布包
              <select v-model="previewPackageFilter" aria-label="按发布包筛选">
                <option value="">全部（{{ previewPackages.length }}）</option>
                <option v-for="release in previewPackages" :key="release.id" :value="release.id">{{ release.number }}</option>
              </select>
            </label>
            <button type="button" class="pdm-secondary-action" :disabled="previewLoading" @click="loadPreviewItems">刷新</button>
          </div>
        </header>
        <p v-if="previewLoading && !previewItems.length" class="pdm-release-preview-empty" role="status">正在读取图纸转出状态…</p>
        <div v-else-if="previewRows.length" class="pdm-release-preview-table-wrap">
          <table class="pdm-edit-table pdm-release-preview-table" data-pdm-min-width="480">
            <colgroup>
              <col style="width: 31% !important">
              <col style="width: 56px !important">
              <col style="width: 95px !important">
              <col style="width: 66px !important">
              <col>
              <col style="width: 48px !important">
            </colgroup>
            <thead><tr><th>发布包</th><th>转出总数</th><th>转出进度</th><th>状态</th><th>问题</th><th>重试</th></tr></thead>
            <tbody>
              <tr v-for="row in previewRows" :key="row.release.id">
                <td :title="row.release.number">{{ row.release.number }}</td>
                <td>{{ row.total }}</td>
                <td :title="row.detail">
                  <div v-if="row.percent !== null" class="pdm-release-preview-progress">
                    <span>{{ row.succeeded }} / {{ row.total }}（{{ row.percent }}%）</span>
                    <progress :value="row.succeeded" :max="row.total" :aria-label="`${row.release.number} 转出进度`" />
                  </div>
                  <span v-else>—</span>
                </td>
                <td>
                  <span :class="row.failed ? 'pdm-release-preview-failed' : row.status === '已完成' ? 'pdm-release-preview-ok' : 'pdm-release-preview-pending'">{{ row.status }}</span>
                  <small v-if="row.failed">{{ row.failed }} 项失败</small>
                </td>
                <td><span class="pdm-release-preview-problem" :title="row.problem || undefined">{{ row.problem || '—' }}</span></td>
                <td>
                  <button v-if="row.canRetry && canManageRelease && projectWritable" type="button" class="pdm-text-action pdm-release-preview-retry"
                    :aria-label="`重试转图 ${row.release.number}`" :disabled="pending || previewLoading" @click="requestConversionRetry(row.release.id)">
                    {{ pending && retryRequestedId === row.release.id ? '提交中' : '重试' }}
                  </button>
                  <span v-else>—</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="pdm-release-preview-empty">暂无图纸转出记录。</p>
      </section>
    </div>
  </section>
</template>

<style scoped>
.pdm-release-preview-attention{flex:0 0 auto;margin:0;padding:10px 12px;border:1px solid var(--pdm-danger);border-radius:6px;background:var(--pdm-danger-soft);color:var(--pdm-danger)}
.pdm-release-published-package{display:grid;gap:4px;min-width:0}
.pdm-release-package-retry{display:flex;flex-wrap:wrap;align-items:center;gap:5px;padding:6px 8px;border-radius:5px;background:var(--pdm-danger-soft);color:var(--pdm-danger);font-size:12px}
.pdm-release-package-retry small{flex:1 1 100%;color:inherit;overflow-wrap:anywhere;white-space:normal}
.pdm-release-overview-active>div>button{width:100%}.pdm-release-overview-active button.pdm-release-retry-conversion{width:auto;margin-top:6px;border:1px solid var(--pdm-blue); color:var(--pdm-text)}.pdm-release-active-description{display:grid;gap:5px;min-width:0;overflow-wrap:anywhere}.pdm-release-publishing-reason{white-space:normal;font-size:12px;color:var(--pdm-text-soft)}.pdm-release-failure-reason{color:var(--pdm-danger);white-space:pre-wrap;font-size:12px}.pdm-release-overview-active em{flex-shrink:0}.pdm-release-overview-active em.is-error{color:var(--pdm-danger)}.pdm-release-published-state{padding:1px 6px;border-radius:999px;background:var(--pdm-green-soft);color:var(--pdm-green);font-size:11px}
.pdm-release-overview{display:flex;flex-direction:column;flex:1 1 auto;min-height:0;gap:var(--pdm-container-gap);overflow:hidden}
.pdm-release-overview-grid{flex:1 1 0;min-height:0;display:grid;grid-template-columns:repeat(2,minmax(0,1fr));grid-template-rows:repeat(2,minmax(0,1fr));gap:var(--pdm-container-gap)}
.pdm-release-stream-card{padding:0;min-height:0;overflow:auto}.pdm-release-stream-card>header{position:sticky;top:0;z-index:2}.pdm-release-stream-card header>span{padding:4px 8px;border-radius:999px}.pdm-release-stream-card .is-active{background:var(--pdm-theme-accent-soft); color:var(--pdm-text)}.pdm-release-stream-card .is-clear{background:var(--pdm-green-soft);color:var(--pdm-green)}.pdm-release-overview-active,.pdm-release-overview-history{display:grid;gap:6px;padding:10px 12px}.pdm-release-overview-active{background:var(--pdm-theme-accent-soft);border-bottom:1px solid var(--pdm-border)}.pdm-release-overview-active button,.pdm-release-overview-history button{padding:7px;border:0;border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);text-align:left}.pdm-release-overview-active button{display:flex;justify-content:space-between;gap:8px}.pdm-release-overview-history button{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:8px}.pdm-release-overview-history button>span:first-child{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-release-overview-active em{font-style:normal; color:var(--pdm-text)}.pdm-release-overview-history small,.pdm-release-overview-history p{color:var(--pdm-muted)}
.pdm-release-overview-history .pdm-release-row-tail{display:inline-flex;align-items:center;justify-content:flex-end;gap:6px;white-space:nowrap}.pdm-release-overview-history .pdm-release-row-tail small{white-space:nowrap}.pdm-release-overview-history .pdm-release-row-tail em{font-style:normal;padding:1px 6px;border-radius:999px;background:var(--pdm-surface-soft);color:var(--pdm-muted);font-size:11px;white-space:nowrap}.pdm-release-overview-history .pdm-release-row-tail em.is-error{background:var(--pdm-danger-soft);color:var(--pdm-danger)}
.pdm-release-stream-card .pdm-release-package-row{box-sizing:border-box;display:grid;grid-template-columns:minmax(0,6fr) minmax(0,1fr);align-items:center;gap:5px;width:100%;min-width:0;min-height:46px;padding:5px;border:0;border-radius:0;background:transparent;box-shadow:none;text-align:left;font-size:12px;line-height:18px;cursor:pointer}
.pdm-release-package-row .pdm-release-package-description{display:grid;grid-template-rows:18px 18px;min-width:0;gap:0}
.pdm-release-package-description>span,.pdm-release-package-description>small{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px;line-height:18px}
.pdm-release-package-row .pdm-release-package-status{display:grid;justify-items:center;align-content:center;gap:0;min-width:0;margin:0;font-size:11px;font-style:normal;text-align:center;white-space:nowrap}
.pdm-release-package-status>*{box-sizing:border-box;max-width:100%;overflow:hidden;text-overflow:ellipsis}
.pdm-release-package-row .pdm-release-row-tail :is(span,em){padding:0 2px;font-size:11px;line-height:18px}
.pdm-release-stream-card .pdm-release-package-row:hover,.pdm-release-stream-card .pdm-release-package-row:focus-visible{background:var(--pdm-theme-accent-soft);outline:0}
.pdm-release-package-row:focus-visible .pdm-release-package-description>span{text-decoration:underline}
.pdm-release-failure-reason,.pdm-release-publishing-reason{display:block;padding:0 5px;overflow-wrap:anywhere}
@media (max-width:1000px) {
  .pdm-release-overview { overflow:auto; }
  .pdm-release-overview-grid { flex:1 0 auto; grid-template-columns:minmax(0,1fr); grid-template-rows:none; grid-auto-rows:280px; }
}
.pdm-release-preview-list{display:flex;flex-direction:column;min-width:0;min-height:0;padding:0;overflow:hidden}
.pdm-release-overview-heading {
  box-sizing: border-box;
  height: 60px;
  flex: none;
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--pdm-control-gap, 8px);
  padding: 0 12px;
  border-bottom: 1px solid var(--pdm-border);
  background: var(--pdm-surface);
}
.pdm-release-heading-text { min-width: 0; }
.pdm-release-heading-text h3 { margin: 0; }
.pdm-release-heading-text small { display: block; color: var(--pdm-muted); }
.pdm-release-heading-text :is(h3, small) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.pdm-release-preview-actions { display: grid; grid-auto-flow: column; align-items: center; gap: 6px; }
.pdm-release-preview-actions button{box-sizing:border-box;width:auto;min-width:80px;height:var(--pdm-button-default);padding:2px 3px;overflow:hidden;border:1px solid var(--pdm-border);border-radius:999px;background:var(--pdm-surface);color:var(--pdm-text);font-size:11px;white-space:nowrap;text-overflow:ellipsis}
.pdm-release-preview-actions button:disabled{opacity:.55}
.pdm-release-preview-filter{display:inline-flex;align-items:center;gap:6px;color:var(--pdm-muted);font-size:12px;white-space:nowrap}
.pdm-release-preview-filter select{box-sizing:border-box;width:clamp(84px,8.3vw,140px);min-width:0;height:28px;padding:3px 6px;border:1px solid var(--pdm-border);border-radius:999px;background:var(--pdm-surface);color:var(--pdm-text);font-size:11px;white-space:nowrap;text-overflow:ellipsis}.pdm-release-preview-filter select:focus{border-color:var(--pdm-blue);outline:2px solid var(--pdm-theme-accent-focus)}
.pdm-release-preview-table-wrap{flex:1 1 auto;min-height:0;overflow:auto}.pdm-release-baseline-table-wrap{flex:1 1 auto;min-height:0;overflow:auto}.pdm-release-baseline-table-wrap .pdm-edit-table th,.pdm-release-baseline-table-wrap .pdm-edit-table td{white-space:nowrap}
.pdm-release-preview-table th,.pdm-release-preview-table td{padding:6px 8px;font-size:12px;white-space:nowrap}
.pdm-release-preview-ok{padding:1px 6px;border-radius:999px;background:var(--pdm-green-soft);color:var(--pdm-green)}
.pdm-release-preview-pending{ color:var(--pdm-text)}.pdm-release-preview-failed{padding:1px 6px;border-radius:999px;background:var(--pdm-danger-soft);color:var(--pdm-danger)}
.pdm-release-preview-empty{margin:0;padding:16px 12px;color:var(--pdm-muted);font-size:12px}

.pdm-release-preview-table { width: 100%; min-width: 480px; table-layout: fixed; }
.pdm-release-preview-table :is(th, td) { padding-inline: 4px; }
.pdm-release-preview-problem { display: -webkit-box; overflow: hidden; -webkit-box-orient: vertical; -webkit-line-clamp: 2; white-space: normal; overflow-wrap: anywhere; }
.pdm-release-preview-retry { white-space: nowrap; }
.pdm-release-preview-table :is(th, td) { text-align: center; vertical-align: middle; }
.pdm-release-preview-table td:first-child { white-space: normal; overflow-wrap: anywhere; }
.pdm-release-preview-progress { display: grid; justify-items: center; gap: 4px; white-space: nowrap; }
.pdm-release-preview-progress progress { width: min(100%, 116px); height: 5px; accent-color: var(--pdm-theme-accent); }
</style>
