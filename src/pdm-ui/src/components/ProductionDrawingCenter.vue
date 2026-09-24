<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { downloadDocumentPreviewFile, downloadProductionDrawingArchive, listProductionDrawings, readDocumentVersionFile, updateProductionDrawingDelivery } from '../api'
import type { BomItem, DrawingPriority, ProductionDrawingItem } from '../types'
import { useUserDisplayName } from '../userDisplay'
import { u9UnitName } from '../u9Units'

type ColumnKey = 'kind' | 'wearPart' | 'impact' | 'unit' | 'drawingNumber' | 'name' | 'parentDrawingNumber' | 'specification' | 'revision' | 'remark' | 'brand' | 'material' | 'surfaceTreatment' | 'heatTreatment' | 'weight' | 'quantity' | 'quantityReference' | 'drawing' | 'issue' | 'dataStatus' | 'priority' | 'requiredOn' | 'deliveryStatus'
const columnDefinitions: { key: ColumnKey; label: string }[] = [
  { key: 'kind', label: '物料分类' }, { key: 'wearPart', label: '易损件' }, { key: 'impact', label: '关键' }, { key: 'unit', label: '单位' },
  { key: 'drawingNumber', label: '物料编码' }, { key: 'name', label: '名称' }, { key: 'parentDrawingNumber', label: '上级物料编码' }, { key: 'specification', label: '型号' }, { key: 'revision', label: '版本' },
  { key: 'remark', label: '备注' }, { key: 'brand', label: '品牌' }, { key: 'material', label: '材质' }, { key: 'surfaceTreatment', label: '表面处理' }, { key: 'heatTreatment', label: '热处理' },
  { key: 'weight', label: '重量' }, { key: 'quantity', label: '数量' }, { key: 'quantityReference', label: '发布 总/源' }, { key: 'drawing', label: '图纸' },
  { key: 'issue', label: '问题' }, { key: 'dataStatus', label: '资料状态' }, { key: 'priority', label: '紧急程度' }, { key: 'requiredOn', label: '需求日期' }, { key: 'deliveryStatus', label: '交付状态' },
]
const defaultColumns: ColumnKey[] = ['drawingNumber', 'name', 'specification', 'revision', 'remark', 'material', 'surfaceTreatment', 'heatTreatment', 'quantity', 'drawing']
const props = defineProps<{ token: string; canManage: boolean; username?: string }>()
const displayUserName = useUserDisplayName()
const rows = ref<ProductionDrawingItem[]>([])
const history = ref(false)
const query = ref('')
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
    const saved = JSON.parse(window.localStorage.getItem(columnStorageKey()) ?? 'null') as { visible?: ColumnKey[] } | null
    const allowed = new Set(columnDefinitions.map(column => column.key))
    const visible = (saved?.visible ?? []).filter((key, index, values) => allowed.has(key) && values.indexOf(key) === index)
    if (visible.length) visibleColumnKeys.value = visible
  } catch { /* 浏览器禁用本地存储时使用默认列。 */ }
}
function saveColumns() {
  if (!columnDraft.value.length) return
  visibleColumnKeys.value = [...columnDraft.value]
  try { window.localStorage.setItem(columnStorageKey(), JSON.stringify({ visible: visibleColumnKeys.value })) }
  catch { /* 当前会话仍可使用列设置。 */ }
  columnSettingsOpen.value = false
}
const bomItems = (item: ProductionDrawingItem) => item.bomItems ?? []
function joinedBomValue(item: ProductionDrawingItem, key: keyof BomItem) {
  const values = bomItems(item).map(bom => bom[key]).filter(value => value !== null && value !== undefined && String(value).trim() !== '')
  return [...new Set(values.map(String))].join('、')
}
function quantityValue(item: ProductionDrawingItem) {
  const items = bomItems(item)
  return items.length ? String(Number(items.reduce((total, bom) => total + bom.quantity, 0).toFixed(4))) : '—'
}
function columnValue(item: ProductionDrawingItem, key: ColumnKey): string {
  switch (key) {
    case 'kind': return bomItems(item).length ? '非标件' : '—'
    case 'wearPart': return bomItems(item).length ? [...new Set(bomItems(item).map(bom => bom.isWearPart ? '是' : '否'))].join('、') : '—'
    case 'impact': return joinedBomValue(item, 'impactStage').replaceAll('Assembly', '装配').replaceAll('Commissioning', '调试') || '—'
    case 'unit': return bomItems(item).length ? [...new Set(bomItems(item).map(bom => u9UnitName(bom.unit)))].join('、') : '—'
    case 'drawingNumber': return joinedBomValue(item, 'drawingNumber') || item.drawingNumber
    case 'name': return joinedBomValue(item, 'name') || item.name
    case 'specification': return joinedBomValue(item, 'specification') || item.model
    case 'revision': return joinedBomValue(item, 'revision') || item.revision
    case 'quantity': return quantityValue(item)
    case 'quantityReference': return bomItems(item).length ? `${quantityValue(item)} / 源未固化` : '—'
    case 'issue': return bomItems(item).some(bom => bom.manualUnmatched || bom.pendingRemoval || bom.releaseExcluded) ? '需核对' : '—'
    case 'dataStatus': return bomItems(item).length ? bomItems(item).every(bom => bom.complete) ? '齐全' : '待完善' : '—'
    case 'priority': return priorityLabel(item.priority)
    case 'requiredOn': return item.requiredOn || '—'
    case 'deliveryStatus': return item.pdfReady ? '可拿图' : '待转换'
    case 'drawing': return ''
    default: return joinedBomValue(item, key) || '—'
  }
}
const priorityLabel = (value: DrawingPriority) => value === 'Urgent' ? '紧急' : value === 'Priority' ? '优先' : '普通'
const priorityRank = (value: DrawingPriority) => value === 'Urgent' ? 0 : value === 'Priority' ? 1 : 2
const filterOptions = (key: 'publishedBy' | 'division' | 'projectManager') => computed(() =>
  [...new Set(rows.value.map(item => item[key]?.trim()).filter((value): value is string => !!value))]
    .sort((a, b) => a.localeCompare(b, 'zh-CN')))
const publishers = filterOptions('publishedBy')
const divisions = filterOptions('division')
const managers = filterOptions('projectManager')
const filteredRows = computed(() => rows.value.filter(item =>
  (!publisherFilter.value || item.publishedBy === publisherFilter.value)
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

function choosePackage(id: string) { packageId.value = id; versionId.value = ''; checkedIds.value = [] }
function chooseProject(id: string) {
  projectId.value = id
  const first = filteredRows.value.filter(item => item.projectId === id).sort((a, b) => b.publishedAt.localeCompare(a.publishedAt))[0]
  choosePackage(first?.releasePackageId ?? '')
}
function choose(item: ProductionDrawingItem) {
  versionId.value = item.versionId
  editPriority.value = item.priority
  editRequiredOn.value = item.requiredOn ?? ''
}
async function refresh() {
  if (!props.token) return
  loading.value = true
  error.value = ''
  try {
    rows.value = await listProductionDrawings(props.token, history.value)
    if (projectId.value && !rows.value.some(item => item.projectId === projectId.value)) projectId.value = ''
    if (projectId.value && !rows.value.some(item => item.projectId === projectId.value && item.releasePackageId === packageId.value)) chooseProject(projectId.value)
    if (versionId.value && !rows.value.some(item => item.versionId === versionId.value)) versionId.value = ''
    checkedIds.value = checkedIds.value.filter(id => rows.value.some(item => item.versionId === id))
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '读取生产图纸失败。' }
  finally { loading.value = false }
}
async function locateScan() {
  const parts = scan.value.trim().split('|')
  if (parts.length !== 4 || parts[0] !== 'UPLM-DRAWING') { error.value = '二维码格式不正确。'; return }
  history.value = true
  await refresh()
  let model = ''
  try { model = decodeURIComponent(parts[1]) }
  catch { error.value = '二维码型号编码无效。'; return }
  const found = rows.value.find(item => item.documentId.replaceAll('-', '').toLowerCase() === parts[3].toLowerCase()
    && item.revision === parts[2] && item.model === model)
  if (!found) { error.value = '该图纸版本不存在，或当前账号没有查看权限。'; return }
  query.value = ''
  publisherFilter.value = ''
  divisionFilter.value = ''
  managerFilter.value = ''
  chooseProject(found.projectId)
  choosePackage(found.releasePackageId)
  choose(found)
  error.value = ''
}
async function downloadBatch(items: ProductionDrawingItem[], format: 'Pdf' | 'Source') {
  if (!project.value || !items.length || downloading.value) return
  if (format === 'Pdf' && items.some(item => !item.pdfReady)) {
    error.value = '所选图纸中有 PDF 待转换，不能用旧版替代；可先下载正式源图。'
    return
  }
  if (items.some(item => !item.isCurrent) && !window.confirm('所选图纸包含已被替代的历史正式版，确定下载？')) return
  downloading.value = true
  error.value = ''
  try { await downloadProductionDrawingArchive(project.value.id, items.map(item => item.versionId), format, props.token) }
  catch (cause) { error.value = cause instanceof Error ? cause.message : '批量下载失败。' }
  finally { downloading.value = false }
}
async function downloadPdf(item: ProductionDrawingItem) {
  if (!item.pdfReady) return
  if (!item.isCurrent && !window.confirm(`${item.drawingNumber} ${item.revision} 已被替代，确定下载历史 PDF？`)) return
  try { await downloadDocumentPreviewFile(item.documentId, item.versionId, `${item.drawingNumber}_${item.revision}.pdf`, props.token) }
  catch (cause) { error.value = cause instanceof Error ? cause.message : 'PDF 下载失败。' }
}
async function downloadSource(item: ProductionDrawingItem) {
  if (!item.isCurrent && !window.confirm(`${item.drawingNumber} ${item.revision} 已被替代，确定下载历史源图？`)) return
  try {
    const blob = await readDocumentVersionFile(item.documentId, item.versionId, props.token, true)
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${item.drawingNumber}_${item.revision}.slddrw`
    link.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 60000)
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '源图下载失败。' }
}
async function saveDelivery() {
  const item = selected.value
  if (!item || !editRequiredOn.value || saving.value) return
  saving.value = true
  error.value = ''
  try {
    await updateProductionDrawingDelivery(item, { priority: editPriority.value, requiredOn: editRequiredOn.value }, props.token)
    await refresh()
    const updated = rows.value.find(row => row.versionId === item.versionId)
    if (updated) choose(updated)
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '发图信息保存失败。' }
  finally { saving.value = false }
}
watch(() => props.token, refresh)
watch(history, refresh)
watch([publisherFilter, divisionFilter, managerFilter], () => {
  if (!projects.value.some(item => item.id === projectId.value)) { projectId.value = ''; choosePackage(''); return }
  if (!packages.value.some(item => item.id === packageId.value)) choosePackage(packages.value[0]?.id ?? '')
  if (!drawings.value.some(item => item.versionId === versionId.value)) versionId.value = ''
  checkedIds.value = checkedIds.value.filter(id => drawings.value.some(item => item.versionId === id))
})
watch(() => props.username, restoreColumns)
onMounted(() => { restoreColumns(); void refresh() })
</script>

<template>
  <section class="pdm-panel production-drawings" aria-label="生产图纸中心">
    <div class="production-drawings__filters">
      <input v-model="query" type="search" placeholder="搜索项目、型号、图号或发布包" aria-label="搜索生产图纸">
      <select v-model="publisherFilter" aria-label="按发布人筛选"><option value="">全部发布人</option><option v-for="name in publishers" :key="name" :value="name">{{ displayUserName(name) }}</option></select>
      <select v-model="divisionFilter" aria-label="按事业部筛选"><option value="">全部事业部</option><option v-for="name in divisions" :key="name" :value="name">{{ name }}</option></select>
      <select v-model="managerFilter" aria-label="按项目经理筛选"><option value="">全部项目经理</option><option v-for="name in managers" :key="name" :value="name">{{ displayUserName(name) }}</option></select>
      <label><input v-model="history" type="checkbox"> 查看历史正式版</label>
      <form @submit.prevent="locateScan"><input v-model="scan" placeholder="扫描图纸二维码" aria-label="扫描图纸二维码"><button type="submit" class="pdm-primary-action">定位</button></form>
    </div>
    <p v-if="error" class="pdm-inline-error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">正在读取图纸…</p>
    <div v-if="!loading || rows.length" class="production-drawings__layout">
      <nav class="production-drawings__projects" aria-label="项目列表">
        <button v-for="entry in projects" :key="entry.id" type="button" class="production-drawings__project"
          :class="{ 'is-selected': projectId === entry.id }" @click="chooseProject(entry.id)">
          <span class="production-drawings__project-title"><strong>{{ entry.code }}</strong> · {{ entry.name }}</span>
          <small>{{ new Set(entry.drawings.map(item => item.releasePackageId)).size }} 个发布包 · {{ entry.drawings.length }} 张正式图纸</small>
        </button>
        <p v-if="!projects.length">暂无符合条件的正式图纸项目</p>
      </nav>
      <div v-if="project" class="production-drawings__content">
        <div class="production-drawings__packages" role="group" aria-label="发布包列表">
          <button v-for="entry in packages" :key="entry.id" type="button" class="production-drawings__package"
            :class="{ 'is-selected': packageId === entry.id }" @click="choosePackage(entry.id)">
            <strong>{{ entry.number }}</strong>
            <small>{{ entry.drawings.length }} 张图纸 · {{ entry.drawings.filter(item => item.pdfReady).length }} 张 PDF 可用 · {{ entry.publishedAt.slice(0, 10) }}</small>
          </button>
        </div>
        <template v-if="selectedPackage">
          <div class="production-drawings__selection">
            <label><input type="checkbox" aria-label="全选本包图纸" :checked="checked.length === drawings.length && drawings.length > 0" @change="checkedIds = ($event.target as HTMLInputElement).checked ? drawings.map(item => item.versionId) : []"> 全选本包</label>
            <span>已选 {{ checked.length }} 张</span>
            <button type="button" :disabled="!checked.length || downloading" @click="downloadBatch(checked, 'Source')">批量下载源图</button>
            <button type="button" :disabled="!checked.length || downloading" @click="downloadBatch(checked, 'Pdf')">批量下载 PDF</button>
            <div class="production-drawings__toolbar-actions">
              <button type="button" class="pdm-secondary-action" @click="columnDraft = [...visibleColumnKeys]; columnSettingsOpen = true">列设置</button>
              <button type="button" class="pdm-secondary-action" :disabled="downloading || !drawings.length" @click="downloadBatch(drawings, 'Source')">下载本包源图</button>
              <button type="button" class="pdm-secondary-action" :disabled="downloading || !drawings.length" @click="downloadBatch(drawings, 'Pdf')">下载本包 PDF</button>
            </div>
          </div>
          <div class="pdm-table-scroll"><table class="pdm-edit-table">
            <thead><tr><th>选择</th><th v-for="column in visibleColumns" :key="column.key">{{ column.label }}</th></tr></thead>
            <tbody>
              <tr v-for="item in drawings" :key="item.versionId" :class="{ 'is-selected': versionId === item.versionId }" @click="choose(item)">
                <td><input v-model="checkedIds" type="checkbox" :value="item.versionId" :aria-label="`选择 ${item.drawingNumber} ${item.revision}`" @click.stop></td>
                <td v-for="column in visibleColumns" :key="column.key" :title="column.key === 'quantityReference' && item.bomItems?.length ? '发布包冻结了BOM数量，但未冻结设计树来源数量。' : columnValue(item, column.key)">
                  <template v-if="column.key === 'drawing'">
                    <button type="button" class="production-drawings__file-link" :disabled="!item.pdfReady" :aria-label="`下载 ${item.drawingNumber} PDF`" @click.stop="downloadPdf(item)">PDF</button>
                    <button type="button" class="production-drawings__file-link" :aria-label="`下载 ${item.drawingNumber} 源图`" @click.stop="downloadSource(item)">源图</button>
                    <small v-if="item.legacyUnverified">历史图面未校验</small>
                  </template>
                  <template v-else>{{ columnValue(item, column.key) }}<small v-if="column.key === 'revision' && columnValue(item, column.key) !== item.revision">图纸版 {{ item.revision }}</small><small v-if="column.key === 'revision' && !item.isCurrent">已被替代</small></template>
                </td>
              </tr>
            </tbody>
          </table></div>
          <aside v-if="selected" class="production-drawings__detail">
            <div><h3>{{ selected.drawingNumber }} · {{ selected.revision }}</h3><p>{{ selected.model }} · {{ selected.name }}</p>
              <p :class="{ 'is-warning': !selected.isCurrent }">{{ selected.isCurrent ? '当前正式版' : '历史正式版，已被替代' }}</p>
              <p v-if="selected.legacyUnverified" class="is-warning">历史图面未校验：源图中的版次和二维码可能与 PLM 记录不同。</p>
              <p>需求日期：{{ selected.requiredOn || '未设置' }}</p></div>
            <div class="production-drawings__actions">
              <button type="button" class="pdm-primary-action" :disabled="!selected.pdfReady" @click="downloadPdf(selected)">{{ selected.pdfReady ? (selected.isCurrent ? '下载正式 PDF' : '下载历史 PDF') : 'PDF 待转换' }}</button>
              <button type="button" class="pdm-secondary-action" @click="downloadSource(selected)">{{ selected.isCurrent ? '下载正式源图' : '下载历史源图' }}</button>
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
    </div>
    <div v-if="columnSettingsOpen" class="production-drawings__column-backdrop" @click.self="columnSettingsOpen = false">
      <section class="production-drawings__column-dialog" role="dialog" aria-modal="true" aria-label="生产图纸列设置">
        <header><h2>列设置</h2><button type="button" aria-label="关闭列设置" @click="columnSettingsOpen = false">×</button></header>
        <p>字段来自发布包冻结的非标件 BOM；缺失的数据以“—”显示。</p>
        <div class="production-drawings__column-options">
          <label v-for="column in columnDefinitions" :key="column.key"><input v-model="columnDraft" type="checkbox" :value="column.key">{{ column.label }}</label>
        </div>
        <footer><button type="button" class="pdm-secondary-action" @click="columnDraft = [...defaultColumns]">恢复默认</button><button type="button" class="pdm-primary-action" :disabled="!columnDraft.length" @click="saveColumns">保存</button></footer>
      </section>
    </div>
  </section>
</template>

<style scoped>
.production-drawings{padding:20px}.production-drawings__header,.production-drawings__filters,.production-drawings__actions,.production-drawings__toolbar,.production-drawings__selection{display:flex;align-items:center;gap:12px;justify-content:space-between}.production-drawings__header h1{margin:0 0 5px;font-size:22px}.production-drawings__header p,.production-drawings__project-header p,.production-drawings__toolbar p{margin:0;color:var(--pdm-muted)}.production-drawings__filters{margin:18px 0;justify-content:flex-start;flex-wrap:wrap}.production-drawings__filters>input{box-sizing:border-box;flex:0 0 227px;width:227px;min-width:227px}.production-drawings__filters form,.production-drawings__toolbar-actions{display:flex;gap:6px}.production-drawings__filters form button{flex:0 0 80px;width:80px;min-height:34px;height:34px;padding:0}.production-drawings input:not([type=checkbox]),.production-drawings select{height:34px;padding:0 9px;border:1px solid var(--pdm-border);border-radius:6px}.production-drawings__layout{display:grid;grid-template-columns:240px minmax(0,1fr);gap:18px}.production-drawings__projects{display:grid;align-content:start;gap:7px;padding-right:12px;border-right:1px solid var(--pdm-border)}.production-drawings__projects h2{margin:0 0 8px;font-size:16px}.production-drawings__projects h2 small{color:var(--pdm-muted)}.production-drawings__project,.production-drawings__package{display:grid;gap:4px;text-align:left;padding:12px;border:1px solid var(--pdm-border);border-radius:7px;background:#fff;color:inherit;cursor:pointer}.production-drawings__project.is-selected,.production-drawings__package.is-selected{border-color:#5b91e6;background:#f1f7ff}.production-drawings__project span,.production-drawings__project small,.production-drawings__package span,.production-drawings__package small{color:var(--pdm-muted)}.production-drawings__content{min-width:0}.production-drawings__project-header h2{margin:0 0 4px;font-size:18px}.production-drawings__packages{display:flex;gap:8px;overflow-x:auto;padding:15px 0}.production-drawings__package{min-width:240px}.production-drawings__toolbar{margin:4px 0 10px;flex-wrap:wrap}.production-drawings__toolbar h3{margin:0 0 4px;font-size:16px}.production-drawings__selection{justify-content:flex-start;flex-wrap:wrap;margin:10px 0}.production-drawings__selection button{border:1px solid var(--pdm-border);border-radius:5px;background:#fff;padding:6px 10px;cursor:pointer}.production-drawings__selection button:disabled{opacity:.5;cursor:not-allowed}.production-drawings table{width:100%}.production-drawings tbody tr{cursor:pointer}.production-drawings tbody tr.is-selected{background:#eff6ff}.production-drawings td small{display:block;color:var(--pdm-muted);font-size:11px}.production-drawings__detail{display:flex;gap:20px;flex-wrap:wrap;align-items:flex-start;justify-content:space-between;margin-top:14px;padding:15px;border:1px solid var(--pdm-border);border-radius:7px}.production-drawings__detail h3{margin:0}.production-drawings__detail p{margin:5px 0}.production-drawings__detail .is-warning{color:var(--pdm-danger)}.production-drawings__actions{justify-content:flex-start;flex-wrap:wrap}.production-drawings__edit{display:grid;gap:8px;min-width:230px}.production-drawings__empty{display:grid;place-items:center;min-height:260px;color:var(--pdm-muted);border:1px dashed var(--pdm-border);border-radius:7px}@media(max-width:950px){.production-drawings__layout{grid-template-columns:1fr}.production-drawings__projects{display:flex;overflow-x:auto;border-right:0;border-bottom:1px solid var(--pdm-border);padding:0 0 12px}.production-drawings__projects h2{min-width:80px}.production-drawings__project{min-width:190px}.production-drawings__toolbar,.production-drawings__detail{align-items:flex-start}}
.production-drawings__project,.production-drawings__package{height:40px;min-height:40px;box-sizing:border-box;gap:0;padding:4px 8px;line-height:15px;overflow:hidden}
.production-drawings__project-title,.production-drawings__package strong,.production-drawings__project small,.production-drawings__package small{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.production-drawings__project .production-drawings__project-title{color:inherit}
.production-drawings__packages{padding:0 0 8px}
.production-drawings__selection{margin:0 0 6px}
.production-drawings__selection>label,.production-drawings__selection>span,.production-drawings__selection>button,.production-drawings__selection .pdm-secondary-action{box-sizing:border-box;min-height:34px;height:34px;font-size:12px}
.production-drawings__selection>label,.production-drawings__selection>span,.production-drawings__selection>button{display:inline-flex;align-items:center}
.production-drawings__selection>label{gap:4px}.production-drawings__selection>label input{margin:0}
.production-drawings__selection>button{justify-content:center;padding:0 10px}
.production-drawings__selection .production-drawings__toolbar-actions{align-items:center}
.production-drawings__selection .production-drawings__toolbar-actions{margin-left:auto}
@media(max-width:950px){.production-drawings__filters>input{flex-basis:190px;width:190px;min-width:190px}.production-drawings__project{min-width:190px}.production-drawings__selection .production-drawings__toolbar-actions{margin-left:0}}
.production-drawings table{min-width:1100px}.production-drawings th,.production-drawings td{white-space:nowrap}.production-drawings td{max-width:220px;overflow:hidden;text-overflow:ellipsis}.production-drawings__file-link{border:0;background:none;color:var(--pdm-primary);cursor:pointer;padding:0 6px 0 0}.production-drawings__file-link:disabled{color:var(--pdm-muted);cursor:not-allowed}
.production-drawings__column-backdrop{position:fixed;inset:0;z-index:1000;display:grid;place-items:center;background:#0006;padding:20px}.production-drawings__column-dialog{width:min(520px,100%);max-height:90vh;overflow:auto;background:#fff;border-radius:8px;padding:20px;box-shadow:0 18px 55px #0003}.production-drawings__column-dialog header,.production-drawings__column-dialog footer{display:flex;align-items:center;justify-content:space-between;gap:12px}.production-drawings__column-dialog h2{margin:0;font-size:18px}.production-drawings__column-dialog header button{border:0;background:none;font-size:24px;cursor:pointer}.production-drawings__column-dialog p{color:var(--pdm-muted)}.production-drawings__column-options{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;margin:18px 0}.production-drawings__column-options label{display:flex;align-items:center;gap:6px;white-space:nowrap}.production-drawings__column-dialog footer{justify-content:flex-end}
@media(max-width:600px){.production-drawings__column-options{grid-template-columns:repeat(2,minmax(0,1fr))}}
</style>
