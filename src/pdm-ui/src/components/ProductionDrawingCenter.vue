<script setup lang="ts">
import { readTablePreference, tablePreferenceVersion, writeTablePreference } from '../tablePreferences'
import { ElMessage } from '../statusMessage'
import { computed, onMounted, ref, watch } from 'vue'
import { downloadProductionDrawingFile, downloadGeneratedFile, downloadProductionDrawingArchive, listProductionDrawings, updateProductionDrawingDelivery, PdmApiError } from '../api'
import { columnDefinitions, columnValue, priorityLabel, createProductionDrawingBomDownload, type ColumnKey } from '../productionDrawingBomDownload'
import type { DrawingPriority, ProductionDrawingItem } from '../types'
import { useUserDisplayName } from '../userDisplay'

const defaultColumns: ColumnKey[] = ['drawingNumber', 'name', 'specification', 'revision', 'remark', 'material', 'surfaceTreatment', 'heatTreatment', 'quantityReference', 'quantity', 'priorReleasedQuantity', 'netIssueQuantity', 'drawing', 'priority', 'requiredOn']
const props = defineProps<{ token: string; canManage: boolean; username?: string }>()
const displayUserName = useUserDisplayName()
const rows = ref<ProductionDrawingItem[]>([])
const history = ref(false)
const query = ref('')
const page = ref(1)
const pageSize = ref(50)
const detailLoading = ref(false)
const loadedProjects = new Set<string>()
let requestGeneration = 0
let locatingScan = false
const publishedOnFilter = ref('')
const publisherFilter = ref('')
const divisionFilter = ref('')
const managerFilter = ref('')
const scan = ref('')
const projectId = ref('')
const packageId = ref('')
const versionId = ref('')
const checkedIds = ref<string[]>([])
const loading = ref(false)
const downloading = ref(false)
const downloadStatus = ref('')
function updateDownloadProgress(received: number, total: number) {
  downloadStatus.value = total > 0 ? '正在下载：' + Math.min(100, Math.round(received / total * 100)) + '%' : '正在下载：已接收 ' + (received / 1024 / 1024).toFixed(1) + ' MB'
}
async function runDownload(action: () => Promise<void>) {
  if (downloading.value) return
  downloading.value = true
  downloadStatus.value = '正在准备文件，请勿重复点击…'
  error.value = ''
  try { await action(); downloadStatus.value = '文件已下载，已交给浏览器保存。' }
  catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') downloadStatus.value = '已取消下载。'
    else { console.error('生产图纸下载失败', cause); error.value = cause instanceof PdmApiError ? cause.message : '图纸下载失败，请检查网络后重试；若仍失败，请联系管理员。'; downloadStatus.value = '' }
  } finally { downloading.value = false }
}
const saving = ref(false)
const error = ref('')
const editPriority = ref<DrawingPriority>('Normal')
const editRequiredOn = ref('')
const visibleColumnKeys = ref<ColumnKey[]>([...defaultColumns])
const columnDraft = ref<ColumnKey[]>([...defaultColumns])
const columnSettingsOpen = ref(false)
const visibleColumns = computed(() => columnDefinitions.filter(column => visibleColumnKeys.value.includes(column.key)))
const columnStorageKey = () => `upton-pdm:production-drawing-columns:${props.username || 'anonymous'}`
function restoreColumns() {
  visibleColumnKeys.value = [...defaultColumns]
  try {
    const saved = JSON.parse(readTablePreference(columnStorageKey()) ?? 'null') as { visible?: ColumnKey[] } | null
    const allowed = new Set(columnDefinitions.map(column => column.key))
    const visible = (saved?.visible ?? []).filter((key, index, values) => allowed.has(key) && values.indexOf(key) === index)
    if (visible.length) visibleColumnKeys.value = visible
  } catch { /* 浏览器禁用本地存储时使用默认列。 */ }
}
async function saveColumns() {
  if (!columnDraft.value.length) return
  visibleColumnKeys.value = [...columnDraft.value]
  let synced: boolean
  try { synced = await writeTablePreference(columnStorageKey(), JSON.stringify({ visible: visibleColumnKeys.value })) }
  catch { ElMessage.error('列设置同步失败，当前选择已保留在本机，请重试保存。'); return }
  columnSettingsOpen.value = false
  if (synced) ElMessage.success('列设置已保存到当前账号')
  else ElMessage.warning('列设置已保存在本机；服务器尚不支持账号同步，请升级后重新保存。')
}
const modelFiles = (item: ProductionDrawingItem) => item.modelFiles ?? []
const canDownloadStep = (items: ProductionDrawingItem[]) => items.length > 0
  && items.every(item => modelFiles(item).length > 0 && modelFiles(item).every(file => file.stepReady && !!file.versionId))
const priorityRank = (value: DrawingPriority) => value === 'Urgent' ? 0 : value === 'Priority' ? 1 : 2
const filterOptions = (key: 'publishedBy' | 'division' | 'projectManager') => computed(() =>
  [...new Set(rows.value.map(item => item[key]?.trim()).filter((value): value is string => !!value))]
    .sort((a, b) => a.localeCompare(b, 'zh-CN')))
const publishers = filterOptions('publishedBy')
const divisions = filterOptions('division')
const managers = filterOptions('projectManager')
const filteredRows = computed(() => rows.value.filter(item =>
  (!publishedOnFilter.value || item.publishedAt.slice(0, 10) === publishedOnFilter.value)
  && (!publisherFilter.value || item.publishedBy === publisherFilter.value)
  && (!divisionFilter.value || item.division === divisionFilter.value)
  && (!managerFilter.value || item.projectManager === managerFilter.value)))

const projects = computed(() => {
  const grouped = new Map<string, { id: string; code: string; name: string; latestPublishedAt: string; drawings: ProductionDrawingItem[] }>()
  for (const item of filteredRows.value) {
    if (!grouped.has(item.projectId)) grouped.set(item.projectId, { id: item.projectId, code: item.projectCode, name: item.projectName, latestPublishedAt: item.publishedAt, drawings: [] })
    const entry = grouped.get(item.projectId)!
    entry.drawings.push(item)
    if (item.publishedAt > entry.latestPublishedAt) entry.latestPublishedAt = item.publishedAt
  }
  const term = query.value.trim().toLocaleLowerCase()
  return [...grouped.values()].filter(entry => !term || `${entry.code} ${entry.name}`.toLocaleLowerCase().includes(term)
    || entry.drawings.some(item => `${item.model} ${item.drawingNumber} ${item.name} ${item.releasePackageNumber}`.toLocaleLowerCase().includes(term)))
    .sort((a, b) => b.latestPublishedAt.localeCompare(a.latestPublishedAt) || a.code.localeCompare(b.code, 'zh-CN'))
})
const project = computed(() => projects.value.find(item => item.id === projectId.value))
const packages = computed(() => {
  const grouped = new Map<string, { id: string; number: string; publishedAt: string; drawings: ProductionDrawingItem[] }>()
  for (const item of filteredRows.value.filter(row => row.projectId === projectId.value)) {
    if (!grouped.has(item.releasePackageId)) grouped.set(item.releasePackageId, {
      id: item.releasePackageId, number: item.releasePackageNumber, publishedAt: item.publishedAt, drawings: [],
    })
    grouped.get(item.releasePackageId)!.drawings.push(item)
  }
  return [...grouped.values()].sort((a, b) => b.publishedAt.localeCompare(a.publishedAt))
})
const selectedPackage = computed(() => packages.value.find(item => item.id === packageId.value))
const drawings = computed(() => [...(selectedPackage.value?.drawings ?? [])].sort((a, b) =>
  priorityRank(a.priority) - priorityRank(b.priority)
  || (a.requiredOn ?? '9999-12-31').localeCompare(b.requiredOn ?? '9999-12-31')
  || a.drawingNumber.localeCompare(b.drawingNumber, 'zh-CN')))
const selected = computed(() => drawings.value.find(item => item.versionId === versionId.value))
const checked = computed(() => drawings.value.filter(item => checkedIds.value.includes(item.versionId)))

const pageCount = computed(() => Math.max(1, Math.ceil(drawings.value.length / pageSize.value)))
const pageDrawings = computed(() => drawings.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
watch(pageSize, () => { page.value = 1 })
watch(pageCount, value => { page.value = Math.min(page.value, value) })
function choosePackage(id: string) { packageId.value = id; versionId.value = ''; checkedIds.value = []; page.value = 1 }
async function chooseProject(id: string) {
  projectId.value = id
  const first = filteredRows.value.filter(item => item.projectId === id).sort((a, b) => b.publishedAt.localeCompare(a.publishedAt))[0]
  choosePackage(first?.releasePackageId ?? '')
  await loadProject(id)
}
function choose(item: ProductionDrawingItem) {
  versionId.value = item.versionId
  editPriority.value = item.priority
  editRequiredOn.value = item.requiredOn ?? ''
}
async function loadProject(id: string) {
  if (loadedProjects.has(id)) { detailLoading.value = false; return }
  const generation = requestGeneration
  detailLoading.value = true
  try {
    const detail = await listProductionDrawings(props.token, history.value, id)
    if (generation !== requestGeneration || projectId.value !== id) return
    rows.value = [...rows.value.filter(item => item.projectId !== id), ...detail.filter(item => item.projectId === id)]
    loadedProjects.add(id)
  } catch (cause) { console.error('读取项目图纸失败', cause); error.value = '读取项目图纸失败，请稍后重试。' }
  finally { if (generation === requestGeneration && projectId.value === id) detailLoading.value = false }
}
async function refresh() {
  if (!props.token) return
  const generation = ++requestGeneration
  loading.value = true
  error.value = ''
  try {
    loadedProjects.clear()
    const index = await listProductionDrawings(props.token, history.value, undefined, true)
    if (generation !== requestGeneration) return
    rows.value = index
    if (projectId.value && !rows.value.some(item => item.projectId === projectId.value)) projectId.value = ''
    if (projectId.value && !rows.value.some(item => item.projectId === projectId.value && item.releasePackageId === packageId.value)) chooseProject(projectId.value)
    if (versionId.value && !rows.value.some(item => item.versionId === versionId.value)) versionId.value = ''
    checkedIds.value = checkedIds.value.filter(id => rows.value.some(item => item.versionId === id))
    if (projectId.value) await loadProject(projectId.value)
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '读取生产图纸失败。' }
  finally { if (generation === requestGeneration) loading.value = false }
}
async function locateScan() {
  const parts = scan.value.trim().split('|')
  if (parts.length !== 4 || parts[0] !== 'UPLM-DRAWING') { error.value = '二维码格式不正确。'; return }
  locatingScan = true
  history.value = true
  locatingScan = false
  await refresh()
  let model = ''
  try { model = decodeURIComponent(parts[1]) }
  catch { error.value = '二维码型号编码无效。'; return }
  const found = rows.value.find(item => item.documentId.replaceAll('-', '').toLowerCase() === parts[3].toLowerCase()
    && item.revision === parts[2] && item.model === model)
  if (!found) { error.value = '该图纸版本不存在，或当前账号没有查看权限。'; return }
  query.value = ''
  publishedOnFilter.value = ''
  publisherFilter.value = ''
  divisionFilter.value = ''
  managerFilter.value = ''
  await chooseProject(found.projectId)
  choosePackage(found.releasePackageId)
  choose(found)
  error.value = ''
}
async function downloadBatch(items: ProductionDrawingItem[], format: 'Pdf' | 'Step') {
  if (!project.value || !selectedPackage.value || !items.length || downloading.value) return
  if (format === 'Pdf' && items.some(item => !item.pdfReady)) {
    error.value = '所选图纸中有 PDF 待转换，不能用旧版替代；请稍后重试。'
    return
  }
  if (format === 'Step' && !canDownloadStep(items)) {
    error.value = '所选项目存在未关联同包三维模型或 STEP 待转换项，不能用 SolidWorks 源文件替代。'
    return
  }
  if (items.some(item => !item.isCurrent) && !window.confirm('所选图纸包含已被替代的历史正式版，确定下载？')) return
  await runDownload(() => downloadProductionDrawingArchive(project.value!.id, items.map(item => item.versionId), format, props.token, selectedPackage.value!.number, updateDownloadProgress, items))
}
async function downloadBom(items: ProductionDrawingItem[], selectedOnly: boolean) {
  if (!selectedPackage.value || !items.length || downloading.value) return
  if (items.some(item => !item.bomItems?.length || !item.bomQuantities?.length)) {
    error.value = '所选图纸缺少发布包冻结的BOM数据，无法导出；请刷新后重试。'
    return
  }
  if (items.some(item => !item.isCurrent) && !window.confirm('所选图纸包含历史正式版，确定下载对应版本BOM？')) return
  await runDownload(async () => { const file = await bomDownload(items, selectedOnly); await downloadGeneratedFile(file.blob, file.fileName) })
}
async function bomDownload(items: ProductionDrawingItem[], selectedOnly: boolean) {
  return createProductionDrawingBomDownload(items, props.token, project.value!.id, selectedPackage.value!, selectedOnly)
}
async function downloadPackage() {
  if (!project.value || !selectedPackage.value || !drawings.value.length || detailLoading.value || downloading.value) return
  const items = [...drawings.value]
  if (items.some(item => !item.pdfReady) || !canDownloadStep(items)) {
    error.value = '本包部分PDF或STEP尚未就绪，暂不能下载全部文件，请稍后重试。'
    return
  }
  if (items.some(item => !item.bomItems?.length || !item.bomQuantities?.length)) {
    error.value = '本包缺少冻结的BOM数据，无法下载全部文件，请刷新后重试。'
    return
  }
  if (items.some(item => !item.isCurrent) && !window.confirm('本包包含历史正式版，确定下载对应版本的STEP、PDF和BOM？')) return
  const id = project.value.id
  const number = selectedPackage.value.number
  const versions = items.map(item => item.versionId)
  await runDownload(async () => {
    const file = await bomDownload(items, false)
    const results = await Promise.allSettled([
      downloadProductionDrawingArchive(id, versions, 'Step', props.token, number, updateDownloadProgress, items),
      downloadProductionDrawingArchive(id, versions, 'Pdf', props.token, number, updateDownloadProgress, items),
      downloadGeneratedFile(file.blob, file.fileName),
    ])
    const failed = results.find(result => result.status === 'rejected')
    if (failed?.status === 'rejected') throw failed.reason
  })
}
async function downloadPdf(item: ProductionDrawingItem) {
  if (!item.pdfReady || downloading.value) return
  if (!item.isCurrent && !window.confirm(`${item.drawingNumber} ${item.revision} 已被替代，确定下载历史 PDF？`)) return
  await runDownload(() => downloadProductionDrawingFile(item, 'Pdf', props.token, updateDownloadProgress))
}
async function downloadStep(item: ProductionDrawingItem, model: NonNullable<ProductionDrawingItem['modelFiles']>[number]) {
  if (!model.stepReady || !model.versionId || downloading.value) return
  if (!item.isCurrent && !window.confirm(`${item.drawingNumber} ${item.revision} 已被替代，确定下载历史 STEP？`)) return
  await runDownload(() => downloadProductionDrawingFile(item, 'Step', props.token, updateDownloadProgress, model))
}
async function saveDelivery() {
  const item = selected.value
  if (!item || !editRequiredOn.value || saving.value) return
  saving.value = true
  error.value = ''
  try {
    await updateProductionDrawingDelivery(item, { priority: editPriority.value, requiredOn: editRequiredOn.value }, props.token)
    await refresh()
    const updated = rows.value.find(row => row.releasePackageId === item.releasePackageId && row.versionId === item.versionId)
    if (updated) choose(updated)
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '发图信息保存失败。' }
  finally { saving.value = false }
}
watch(() => props.token, refresh)
watch(history, () => { if (!locatingScan) void refresh() }, { flush: 'sync' })
watch([publishedOnFilter, publisherFilter, divisionFilter, managerFilter], () => {
  if (!projects.value.some(item => item.id === projectId.value)) { projectId.value = ''; choosePackage(''); return }
  if (!packages.value.some(item => item.id === packageId.value)) choosePackage(packages.value[0]?.id ?? '')
  if (!drawings.value.some(item => item.versionId === versionId.value)) versionId.value = ''
  checkedIds.value = checkedIds.value.filter(id => drawings.value.some(item => item.versionId === id))
})
watch(() => props.username, restoreColumns)
watch(tablePreferenceVersion, () => { if (!columnSettingsOpen.value) restoreColumns() })
onMounted(() => { restoreColumns(); void refresh() })
</script>

<template>
  <section class="production-drawings" aria-label="生产图纸中心">
    <div class="production-drawings__layout pdm-project-list-layout">
      <nav class="production-drawings__projects pdm-panel" aria-label="项目列表">
        <div v-for="entry in projects" :key="entry.id" class="production-drawings__project-node"><button type="button" class="production-drawings__project"
          :class="{ 'is-selected': projectId === entry.id }" :aria-expanded="projectId === entry.id" @click="chooseProject(entry.id)">
          <span class="production-drawings__project-title"><i class="production-drawings__nav-arrow" :class="{ 'is-expanded': projectId === entry.id }" aria-hidden="true" /><strong>{{ entry.code }}</strong> · {{ entry.name }}</span>
          <small class="production-drawings__project-meta">
            <span>{{ entry.drawings.length }} 张图纸</span>
            <span>{{ entry.latestPublishedAt.slice(0, 10) }}</span>
          </small>
        </button><div v-if="projectId === entry.id && !detailLoading" class="production-drawings__packages" role="group" aria-label="发布包列表">
          <button v-for="entry in packages" :key="entry.id" type="button" class="production-drawings__package" :title="entry.number"
            :class="{ 'is-selected': packageId === entry.id }" @click="choosePackage(entry.id)">
            <strong>{{ entry.number }}</strong>
            <small>{{ entry.drawings.length }} 张图纸 · {{ entry.drawings.filter(item => item.pdfReady).length }} 张 PDF 可用 · {{ entry.drawings.reduce((count, item) => count + modelFiles(item).filter(model => model.stepReady).length, 0) }} 个 STEP 可用 · {{ entry.publishedAt.slice(0, 10) }}</small>
          </button>
        </div></div>
        <p v-if="!projects.length">暂无符合条件的正式图纸项目</p>
      </nav>
      <section class="production-drawings__main pdm-panel pdm-project-table-panel">
    <div class="production-drawings__filters">
      <input v-model="query" class="production-drawings__query" type="search" placeholder="搜索项目、型号、图号或发布包" aria-label="搜索生产图纸">
      <input v-model="publishedOnFilter" class="production-drawings__published-on" type="date" aria-label="按发布日期筛选">
      <select v-model="publisherFilter" aria-label="按发布人筛选"><option value="">全部发布人</option><option v-for="name in publishers" :key="name" :value="name">{{ displayUserName(name) }}</option></select>
      <select v-model="divisionFilter" aria-label="按事业部筛选"><option value="">全部事业部</option><option v-for="name in divisions" :key="name" :value="name">{{ name }}</option></select>
      <select v-model="managerFilter" aria-label="按项目经理筛选"><option value="">全部项目经理</option><option v-for="name in managers" :key="name" :value="name">{{ displayUserName(name) }}</option></select>
      <label><input v-model="history" type="checkbox"> 查看历史正式版</label>
      <form @submit.prevent="locateScan"><input v-model="scan" placeholder="扫描图纸二维码" aria-label="扫描图纸二维码"><button type="submit" class="pdm-primary-action">定位</button></form>
      <button type="button" class="pdm-secondary-action" :disabled="loading || detailLoading" @click="refresh">刷新</button>
    </div>
    <p v-if="error" class="pdm-inline-error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">正在读取图纸…</p>
      <div v-if="project" class="production-drawings__content">
        <p v-if="detailLoading" role="status">正在读取项目图纸…</p>
        <template v-if="selectedPackage && !detailLoading">
          <div class="production-drawings__selection">
            <label><input type="checkbox" aria-label="全选本包图纸" :checked="checked.length === drawings.length && drawings.length > 0" @change="checkedIds = ($event.target as HTMLInputElement).checked ? drawings.map(item => item.versionId) : []"> 全选本包</label>
            <span>已选 {{ checked.length }} 张</span>
            <button type="button" :disabled="!checked.length || downloading" @click="downloadBatch(checked, 'Step')">下载STEP</button>
            <button type="button" :disabled="!checked.length || downloading" @click="downloadBatch(checked, 'Pdf')">下载PDF</button>
            <button type="button" :disabled="!checked.length || downloading || detailLoading" @click="downloadBom(checked, true)">下载BOM</button>

            <div class="production-drawings__toolbar-actions">
              <span v-if="downloadStatus" class="production-drawings__download-status" role="status" aria-live="polite">{{ downloadStatus }}</span>
              <button type="button" class="pdm-secondary-action" @click="columnDraft = [...visibleColumnKeys]; columnSettingsOpen = true">列设置</button>
              <button type="button" class="pdm-secondary-action" :disabled="downloading || detailLoading || !drawings.length" title="同时下载本包STEP、PDF和BOM" @click="downloadPackage">下载本包</button>
            </div>
          </div>
          <div class="pdm-table-scroll"><table class="pdm-edit-table">
            <thead><tr><th>选择</th><th v-for="column in visibleColumns" :key="column.key" :class="`production-drawings__column--${column.key}`">{{ column.label }}</th></tr></thead>
            <tbody>
              <tr v-for="item in pageDrawings" :key="item.versionId" :class="{ 'is-selected': versionId === item.versionId }" @click="choose(item)">
                <td><input v-model="checkedIds" type="checkbox" :value="item.versionId" :aria-label="`选择 ${item.drawingNumber} ${item.revision}`" @click.stop></td>
                <td v-for="column in visibleColumns" :key="column.key" :class="`production-drawings__column--${column.key}`" :title="columnValue(item, column.key)">
                  <template v-if="column.key === 'drawing'">
                    <button type="button" class="production-drawings__file-link" :disabled="!item.pdfReady || downloading" :aria-label="`下载 ${item.drawingNumber} PDF`" @click.stop="downloadPdf(item)">PDF</button>
                    <template v-if="modelFiles(item).length">
                      <template v-for="model in modelFiles(item)" :key="`${model.documentId}-${model.versionId ?? 'pending'}`">
                        <button v-if="model.stepReady && model.versionId" type="button" class="production-drawings__file-link" :disabled="downloading" :aria-label="`下载 ${model.drawingNumber} STEP`" @click.stop="downloadStep(item, model)">STEP</button>
                        <small v-else :title="model.versionId ? '正式模型版本的 STEP 尚未转换完成' : '该模型没有随此发布包形成正式版本'">{{ model.versionId ? 'STEP待转换' : '未随包发布' }}</small>
                      </template>
                    </template>
                    <small v-else>无关联3D模型</small>
                    <small v-if="item.legacyUnverified">历史图面未校验</small>
                  </template>
                  <template v-else>{{ columnValue(item, column.key) }}<small v-if="column.key === 'revision' && columnValue(item, column.key) !== item.revision">图纸版 {{ item.revision }}</small><small v-if="column.key === 'revision' && !item.isCurrent">已被替代</small></template>
                </td>
              </tr>
            </tbody>
          </table></div>
          <footer class="production-drawings__pagination">
            <span>共 {{ drawings.length }} 条</span>
            <select v-model.number="pageSize" aria-label="生产图纸每页条数"><option v-for="size in [30, 50, 100, 200, 500]" :key="size" :value="size">{{ size }}条/页</option></select>
            <button type="button" aria-label="生产图纸上一页" :disabled="page <= 1" @click="page--">‹</button>
            <span>{{ page }} / {{ pageCount }}</span>
            <button type="button" aria-label="生产图纸下一页" :disabled="page >= pageCount" @click="page++">›</button>
          </footer>
          <aside v-if="selected" class="production-drawings__detail">
            <div><h3>{{ selected.drawingNumber }} · {{ selected.revision }}</h3><p>{{ selected.model }} · {{ selected.name }}</p>
              <p :class="{ 'is-warning': !selected.isCurrent }">{{ selected.isCurrent ? '当前正式版' : '历史正式版，已被替代' }}</p>
              <p v-if="selected.legacyUnverified" class="is-warning">历史图面未校验：源图中的版次和二维码可能与 PLM 记录不同。</p>
              <p>需求日期：{{ selected.requiredOn || '未设置' }}</p></div>
            <div class="production-drawings__actions">
              <button type="button" class="pdm-primary-action" :disabled="!selected.pdfReady || downloading" @click="downloadPdf(selected)">{{ selected.pdfReady ? (selected.isCurrent ? '下载正式 PDF' : '下载历史 PDF') : 'PDF 待转换' }}</button>
              <template v-for="model in modelFiles(selected)" :key="`${model.documentId}-${model.versionId ?? 'pending'}`">
                <button v-if="model.stepReady && model.versionId" type="button" class="pdm-secondary-action" :disabled="downloading" @click="downloadStep(selected, model)">下载 {{ model.drawingNumber }} STEP</button>
                <span v-else class="production-drawings__step-status">{{ model.versionId ? `${model.drawingNumber}：STEP待转换` : `${model.drawingNumber}：未随包发布` }}</span>
              </template>
              <span v-if="!modelFiles(selected).length" class="production-drawings__step-status">未关联三维模型</span>
            </div>
            <form v-if="canManage && selected.isCurrent" class="production-drawings__edit" @submit.prevent="saveDelivery">
              <strong>调整发图信息（不改变图纸版本，留审计记录）</strong>
              <label>紧急程度<select v-model="editPriority"><option value="Normal">普通</option><option value="Priority">优先</option><option value="Urgent">紧急</option></select></label>
              <label>需求日期<input v-model="editRequiredOn" type="date" required></label>
              <button type="submit" class="pdm-secondary-action" :disabled="saving">保存</button>
            </form>
          </aside>
        </template>
      </div>
      <div v-else class="production-drawings__empty">选择左侧项目，查看发布包和图纸。</div>
      </section>
    </div>
    <div v-if="columnSettingsOpen" class="production-drawings__column-backdrop" @click.self="columnSettingsOpen = false">
      <section class="production-drawings__column-dialog" role="dialog" aria-modal="true" aria-label="生产图纸列设置">
        <header><h2>列设置</h2><button type="button" aria-label="关闭列设置" @click="columnSettingsOpen = false">×</button></header>
        <p>字段来自发布包冻结的非标件 BOM；缺失的数据以“—”显示。列设置随当前账号保存，下次打开自动恢复。</p>
        <div class="production-drawings__column-options">
          <label v-for="column in columnDefinitions" :key="column.key"><input v-model="columnDraft" type="checkbox" :value="column.key">{{ column.label }}</label>
        </div>
        <footer><button type="button" class="pdm-secondary-action" @click="columnDraft = [...defaultColumns]">恢复默认</button><button type="button" class="pdm-primary-action" :disabled="!columnDraft.length" @click="saveColumns">保存</button></footer>
      </section>
    </div>
  </section>
</template>

<style scoped>
.production-drawings{padding:20px}.production-drawings__header,.production-drawings__filters,.production-drawings__actions,.production-drawings__toolbar,.production-drawings__selection{display:flex;align-items:center;gap:12px;justify-content:space-between}.production-drawings__header h1{margin:0 0 5px;font-size:var(--pdm-font-size-heading)}.production-drawings__header p,.production-drawings__project-header p,.production-drawings__toolbar p{margin:0;color:var(--pdm-muted)}.production-drawings__filters{margin:18px 0;justify-content:flex-start;flex-wrap:wrap}.production-drawings__filters>input{box-sizing:border-box;flex:0 0 227px;width:227px;min-width:227px}.production-drawings__filters form,.production-drawings__toolbar-actions{display:flex;gap:6px}.production-drawings__filters form button{flex:0 0 80px;width:80px;min-height:34px;height:34px;padding:0}.production-drawings input:not([type=checkbox]),.production-drawings select{height:34px;padding:0 9px;border:1px solid var(--pdm-border);border-radius:6px}.production-drawings__layout{display:grid;grid-template-columns:240px minmax(0,1fr);gap:18px}.production-drawings__projects{display:grid;align-content:start;gap:7px;padding-right:12px;border-right:1px solid var(--pdm-border)}.production-drawings__projects h2{margin:0 0 8px;font-size:var(--pdm-font-size-heading)}.production-drawings__projects h2 small{color:var(--pdm-muted)}.production-drawings__project,.production-drawings__package{display:grid;gap:4px;text-align:left;padding:12px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface);color:inherit;cursor:pointer}.production-drawings__project.is-selected,.production-drawings__package.is-selected{border-color:var(--pdm-theme-accent);background:var(--pdm-theme-accent-soft)}.production-drawings__project span,.production-drawings__project small,.production-drawings__package span,.production-drawings__package small{color:var(--pdm-muted)}.production-drawings__content{min-width:0}.production-drawings__project-header h2{margin:0 0 4px;font-size:14px}.production-drawings__packages{display:flex;gap:8px;overflow-x:auto;padding:15px 0}.production-drawings__package{min-width:240px}.production-drawings__toolbar{margin:4px 0 10px;flex-wrap:wrap}.production-drawings__toolbar h3{margin:0 0 4px;font-size:var(--pdm-font-size-heading)}.production-drawings__selection{justify-content:flex-start;flex-wrap:wrap;margin:10px 0}.production-drawings__selection button{border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);padding:6px 10px;cursor:pointer}.production-drawings__selection button:disabled{opacity:.5;cursor:not-allowed}.production-drawings table{width:100%}.production-drawings tbody tr{cursor:pointer}.production-drawings tbody tr.is-selected{background:var(--pdm-theme-accent-soft)}.production-drawings td small{display:block;color:var(--pdm-muted);font-size:11px}.production-drawings__detail{display:flex;gap:20px;flex-wrap:wrap;align-items:flex-start;justify-content:space-between;margin-top:14px;padding:15px;border:1px solid var(--pdm-border);border-radius:7px}.production-drawings__detail h3{margin:0}.production-drawings__detail p{margin:5px 0}.production-drawings__detail .is-warning{color:var(--pdm-danger)}.production-drawings__actions{justify-content:flex-start;flex-wrap:wrap}.production-drawings__edit{display:grid;gap:8px;min-width:230px}.production-drawings__empty{display:grid;place-items:center;min-height:260px;color:var(--pdm-muted);border:1px dashed var(--pdm-border);border-radius:7px}@media(max-width:950px){.production-drawings__layout{grid-template-columns:1fr}.production-drawings__projects{display:flex;overflow-x:auto;border-right:0;border-bottom:1px solid var(--pdm-border);padding:0 0 12px}.production-drawings__projects h2{min-width:80px}.production-drawings__project{min-width:190px}.production-drawings__toolbar,.production-drawings__detail{align-items:flex-start}}
.production-drawings__project,.production-drawings__package{height:40px;min-height:40px;box-sizing:border-box;gap:0;padding:4px 8px;line-height:15px;overflow:hidden}
.production-drawings__project-title,.production-drawings__package strong,.production-drawings__project small,.production-drawings__package small{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.production-drawings__project .production-drawings__project-title{color:inherit;font-size:14px}
.production-drawings__package,.production-drawings__package strong,.production-drawings__package small{font-size:11px}
.production-drawings__project .production-drawings__project-meta{display:flex;justify-content:space-between;gap:8px}.production-drawings__project-meta span:first-child{min-width:0;overflow:hidden;text-overflow:ellipsis}.production-drawings__project-meta span:last-child{flex:0 0 auto;text-align:right}
.production-drawings__packages{padding:0 0 8px}
.production-drawings__selection{margin:0 0 6px}
.production-drawings__selection>label,.production-drawings__selection>span,.production-drawings__selection>button,.production-drawings__selection .pdm-secondary-action{box-sizing:border-box;min-height:var(--pdm-button-default);height:var(--pdm-button-default);font-size:12px}
.production-drawings__selection>label,.production-drawings__selection>span,.production-drawings__selection>button{display:inline-flex;align-items:center}
.production-drawings__selection>label{gap:4px}.production-drawings__selection>label input{margin:0}
.production-drawings__selection>button{justify-content:center;padding:0 10px}
.production-drawings__selection .production-drawings__toolbar-actions{align-items:center}
.production-drawings__selection .production-drawings__toolbar-actions{margin-left:auto}
@media(max-width:950px){.production-drawings__filters>input{flex-basis:190px;width:190px;min-width:190px}.production-drawings__project{min-width:190px}.production-drawings__selection .production-drawings__toolbar-actions{margin-left:0}}
.production-drawings__filters>.production-drawings__project-code{flex:0 0 104px;width:104px;min-width:104px}.production-drawings__filters>.production-drawings__published-on{flex:0 0 132px;width:132px;min-width:132px}
.production-drawings table{min-width:0;table-layout:fixed}.production-drawings th,.production-drawings td{white-space:nowrap}.production-drawings .pdm-edit-table th,.production-drawings .pdm-edit-table td{text-align:center}.production-drawings td{max-width:220px;overflow:hidden;text-overflow:ellipsis}.production-drawings .production-drawings__column--priority{width:64px;max-width:64px}.production-drawings .production-drawings__column--requiredOn{width:92px;max-width:92px}.production-drawings__file-link{border:0;background:none;color:var(--pdm-primary);cursor:pointer;padding:0 3px}.production-drawings__file-link:disabled{color:var(--pdm-muted);cursor:not-allowed}
.production-drawings__column-backdrop{position:fixed;inset:0;z-index:1000;display:grid;place-items:center;background:var(--pdm-overlay);padding:20px}.production-drawings__column-dialog{width:min(520px,100%);max-height:90vh;overflow:auto;background:var(--pdm-surface);border-radius:8px;padding:20px;box-shadow:0 18px 55px var(--pdm-shadow-strong)}.production-drawings__column-dialog header,.production-drawings__column-dialog footer{display:flex;align-items:center;justify-content:space-between;gap:12px}.production-drawings__column-dialog h2{margin:0;font-size:14px}.production-drawings__column-dialog header button{border:0;background:none;font-size:24px;cursor:pointer}.production-drawings__column-dialog p{color:var(--pdm-muted)}.production-drawings__column-options{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;margin:18px 0}.production-drawings__column-options label{display:flex;align-items:center;gap:6px;white-space:nowrap}.production-drawings__column-dialog footer{justify-content:flex-end}
@media(max-width:600px){.production-drawings__column-options{grid-template-columns:repeat(2,minmax(0,1fr))}}
.production-drawings__step-status{color:var(--pdm-muted);font-size:11px}
.production-drawings{height:100%;min-height:0;box-sizing:border-box;display:flex;flex-direction:column;overflow:hidden;padding:10px 20px}
.production-drawings__filters{flex:0 0 auto;margin:0 0 10px;gap:10px}
.production-drawings__layout{flex:1 1 0;min-height:0;overflow:hidden}
.production-drawings__projects{min-height:0;overflow-y:auto}
.production-drawings__content{min-height:0;min-width:0;display:flex;flex-direction:column;overflow:hidden}
.production-drawings__packages,.production-drawings__selection,.production-drawings__pagination{flex:0 0 auto}
.production-drawings__content>.pdm-table-scroll{flex:1 1 0;min-height:0;overflow:auto}
.production-drawings__pagination{display:flex;align-items:center;justify-content:flex-end;gap:10px;padding-top:10px}
.production-drawings__detail{flex:0 0 auto;max-height:200px;overflow:auto;margin-top:10px}
.production-drawings__empty{min-height:0}
.production-drawings__download-status{max-width:240px;font-size:12px;line-height:17px;overflow-wrap:anywhere}
.production-drawings__project-node { min-width: 0; }
.production-drawings__project-node > .production-drawings__project { width: 100%; }
.production-drawings__projects .production-drawings__packages { display: grid; gap: 4px; padding: 5px 0 0 14px; overflow-x: hidden; }
.production-drawings__projects .production-drawings__package { width: 100%; min-width: 0; padding: 6px 8px; height: auto; text-align: left; }
.production-drawings__projects .production-drawings__package strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.production-drawings__projects .production-drawings__package small { font-size: 10px; line-height: 1.4; }
.production-drawings__selection > button,
.production-drawings__selection .production-drawings__toolbar-actions > button { box-sizing: border-box; width: 60px; min-width: 60px !important; max-width: 60px; height: 30px !important; min-height: 30px !important; flex: 0 0 60px; padding: 0 2px; font-size: 11px; white-space: nowrap; }
@media (max-width: 950px) { .production-drawings__project-node { flex: 0 0 220px; } }
.production-drawings__nav-arrow { display: inline-block; margin-right: 6px; border-top: 4px solid transparent; border-bottom: 4px solid transparent; border-left: 5px solid var(--pdm-muted); transition: transform .15s; }
.production-drawings__nav-arrow.is-expanded { transform: rotate(90deg); }
.production-drawings { padding: 0; }
.production-drawings__layout { grid-template-columns: 200px minmax(0, 1fr); gap: var(--pdm-container-gap); }
.production-drawings__projects { display: grid; align-content: start; padding: 12px; border-right: 1px solid var(--pdm-border); }
.production-drawings__main { min-width: 0; min-height: 0; display: flex; flex-direction: column; padding: 12px; overflow: hidden; }
.production-drawings__content { flex: 1 1 0; }
.production-drawings__empty { flex: 1 1 0; display: grid; place-items: center; }
.production-drawings__project { min-width: 0; }
.production-drawings__project-title { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@media(max-width:700px) {
  .production-drawings__layout { grid-template-columns: minmax(0, 1fr); grid-template-rows: auto minmax(0, 1fr); }
  .production-drawings__projects { max-height: 180px; }
}
.production-drawings__filters form button { box-sizing: border-box; width: 80px; min-width: 80px; height: 30px; min-height: 30px; flex: 0 0 80px; }

/* 制造中心容器间距统一为 5px。 */
.production-drawings__projects,
.production-drawings__main { padding: var(--pdm-container-gap); }
.production-drawings__filters { gap: var(--pdm-container-gap); margin-bottom: var(--pdm-container-gap); }
.production-drawings__toolbar,
.production-drawings__selection { margin-block: var(--pdm-container-gap); }
.production-drawings__detail { gap: var(--pdm-container-gap); padding: var(--pdm-container-gap); margin-top: var(--pdm-container-gap); }
.production-drawings__pagination { gap: var(--pdm-container-gap); padding-top: var(--pdm-container-gap); }
</style>
