<script setup lang="ts">
import { vQuantity } from '../quantityInput'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { ElDatePicker } from 'element-plus'
import ProductionDrawingCenter from './ProductionDrawingCenter.vue'
import ProductionDrawingQr from './ProductionDrawingQr.vue'
import ProductionOperationPanel from './ProductionOperationPanel.vue'
import ProductionBatchDialog from './ProductionBatchDialog.vue'
import ProductionAllocationDialog from './ProductionAllocationDialog.vue'
import ProductionScheduleBatchDialog from './ProductionScheduleBatchDialog.vue'
import ProductionLineDrawingDialog from './ProductionLineDrawingDialog.vue'
import ProductionSupplierManager from './ProductionSupplierManager.vue'
import ProductionSupplierPicker from './ProductionSupplierPicker.vue'
import ProductionRevisionDisposition from './ProductionRevisionDisposition.vue'
import { assignProductionLot, listProductionTrackingProjects, readProductionCapacity,
  readProductionTrackingRelease, readProductionTrackingSupplierChoices, receiveProductionTrackingRelease,
  resolveProductionScan, saveProductionCapacity, scheduleProductionLot,
  updateProductionLineDelivery, updateProductionLotPromise, resolveProductionRevision,
  readProductionLotQr, listProductionDrawings, downloadProductionDrawingArchive, downloadGeneratedFile } from '../api'
import { lineState, lineTotal, movementTotal, productionPriority, productionRequestId, allocationChangeBlockReason, unassignedQuantity, isProductionLineOpen, isProductionLotOpen, selfProductionState, externalProductionState, lotStage, productionMovementLabels,
  type ProductionCapacityView, type ProductionLine, type ProductionLot,
  type ProductionProject, type ProductionReleaseRecord, type ProductionReleaseState,
  type ProductionReleaseSummary, type ProductionRoute, type ProductionScanResult,
  type ProductionScheduleSlot } from '../productionTracking'
import { userErrorMessage } from '../userErrorMessage'
import { ElMessage } from '../statusMessage'
import { rememberedTab } from '../pagePreference'
import type { ProductionSupplierChoices } from '../productionSuppliers'
import { drawingsForProductionLine } from '../productionDrawingSelection'
import { createProductionDrawingBomDownload } from '../productionDrawingBomDownload'

const props = defineProps<{ token: string; username: string; permissions: string[] }>()
const scanPageHref = `${window.location.pathname}?productionScan=1`
const showAllocationEdit = ref(false)
const tab = rememberedTab('production.tab', 'tracking', ['tracking','external','schedule','capacity','execution','drawings','suppliers'])
const projects = ref<ProductionProject[]>([])
const record = ref<ProductionReleaseRecord | null>(null)
const allCategories = ref(false), allRecords = ref<ProductionReleaseRecord[]>([])
const trackingOnlyUnassigned = ref(false), trackingOnlyOpen = ref(true)
const externalOnlyOpen = ref(true), selfOnlyOpen = ref(true)
const currentOnlyOpen = computed({
  get: () => tab.value === 'external' ? externalOnlyOpen.value : tab.value === 'schedule' ? selfOnlyOpen.value : trackingOnlyOpen.value,
  set: (value: boolean) => { if (tab.value === 'external') externalOnlyOpen.value = value; else if (tab.value === 'schedule') selfOnlyOpen.value = value; else trackingOnlyOpen.value = value },
})
const capacity = ref<ProductionCapacityView | null>(null)
const selectedProjectId = ref(''), selectedReleaseId = ref(''), selectedLineId = ref(''), selectedLotId = ref('')
const projectSearch = ref(''), lineSearch = ref(''), lineFilter = ref<'all' | 'open' | 'urgent'>('all')
const externalSupplier = ref(''), externalCategory = ref(''), externalStage = ref('')
const externalDateType = ref<'required' | 'promised' | 'published'>('required'), externalStart = ref(''), externalEnd = ref('')
const externalDateRange = computed<string[] | null>({
  get: () => externalStart.value || externalEnd.value ? [externalStart.value, externalEnd.value] : null,
  set: value => { externalStart.value = value?.[0] ?? ''; externalEnd.value = value?.[1] ?? '' },
})
const externalSuppliers = ref<ProductionSupplierChoices>({ companyId: '', categories: [], suppliers: [] })
const invalidExternalDates = computed(() => Boolean(externalStart.value && externalEnd.value && externalStart.value > externalEnd.value))
const selfResource = ref(''), selfAssignee = ref(''), selfStage = ref('')
const selfDateType = ref<'required' | 'promised' | 'published'>('required'), selfStart = ref(''), selfEnd = ref('')
const selfDateRange = computed<string[] | null>({
  get: () => selfStart.value || selfEnd.value ? [selfStart.value, selfEnd.value] : null,
  set: value => { selfStart.value = value?.[0] ?? ''; selfEnd.value = value?.[1] ?? '' },
})
const invalidSelfDates = computed(() => Boolean(selfStart.value && selfEnd.value && selfStart.value > selfEnd.value))
const trackingSupplier = ref(''), trackingCategory = ref(''), trackingStage = ref('')
const trackingDateType = ref<'required' | 'promised' | 'published'>('required'), trackingStart = ref(''), trackingEnd = ref('')
const trackingDateRange = computed<string[] | null>({
  get: () => trackingStart.value || trackingEnd.value ? [trackingStart.value, trackingEnd.value] : null,
  set: value => { trackingStart.value = value?.[0] ?? ''; trackingEnd.value = value?.[1] ?? '' },
})
const invalidTrackingDates = computed(() => Boolean(trackingStart.value && trackingEnd.value && trackingStart.value > trackingEnd.value))
const linePage = ref(1), linePageSize = 50
const scheduleFilter = ref<'all' | 'unplanned' | 'planned'>('all'), schedulePage = ref(1)
const checkedLineIds = ref<string[]>([])
const checkedScheduleIds = ref<string[]>([]), showScheduleBatch = ref(false)
const drawingContext = ref<{ state: NonNullable<ProductionReleaseRecord['state']>; line: ProductionLine; action: 'preview' | 'download' }>()
const detailColumnWeights = [1, 1, 3, 1, 2, 2, 2, 2, 1, 2, 2, 2, 2]
const batchMode = ref<'delivery' | 'assign' | null>(null)
const includeComplete = ref(false), statusFilter = ref('')
const pending = ref(false), error = ref(''), scanCode = ref('')
const downloading = ref(false), downloadStatus = ref('')
const scanCandidates = ref<ProductionScanResult[]>([])
const assignRoute = ref<ProductionRoute>('Self'), assignQuantity = ref(1), assignSupplier = ref('')
const assignSupplierId = ref('')
const assignAssignee = ref(''), assignPromisedOn = ref(''), assignRequestId = ref(productionRequestId())
const executionLotId = ref('')
const deliveryPriority = ref('Normal'), deliveryRequiredOn = ref(''), deliveryReason = ref('')
const deliveryRequestId = ref(productionRequestId()), promiseOn = ref(''), promiseReason = ref('')
const promiseRequestId = ref(productionRequestId())
const revisionPanel = ref<InstanceType<typeof ProductionRevisionDisposition>>()
const cardUrl = ref(''), cardLot = ref<ProductionLot | null>(null)
const scheduleSlots = ref<ProductionScheduleSlot[]>([]), schedulePromisedOn = ref('')
const scheduleRequestId = ref(productionRequestId()), scheduleReason = ref('')
const resourceGroup = ref('CNC组'), availableDate = ref(new Date().toLocaleDateString('sv-SE')), availableHours = ref(8)
const selectedProject = computed(() => projects.value.find(item => item.id === selectedProjectId.value))
const selectedSummary = computed(() => selectedProject.value?.packages.find(item => item.releasePackageId === selectedReleaseId.value))
const releaseSources = computed(() => allCategories.value ? allRecords.value.map(item => item.state) : record.value ? [record.value.state] : [])
const externalBase = computed(() => record.value && externalProductionState(record.value.state))
const selfBase = computed(() => record.value && selfProductionState(record.value.state))
const selfSources = computed(() => releaseSources.value.flatMap(source => selfProductionState(source) ?? []))
const externalSources = computed(() => releaseSources.value.flatMap(source => externalProductionState(source) ?? []))
const selfResourceOptions = computed(() => [...new Set(selfSources.value.flatMap(source => source.lots.flatMap(lot => lot.schedule.map(slot => slot.resourceGroupId))))])
const selfAssigneeOptions = computed(() => [...new Set(selfSources.value.flatMap(source => source.lots.map(lot => lot.assignee?.trim()).filter((value): value is string => !!value)))])
const selfStageOptions = computed(() => [...new Set(selfSources.value.flatMap(source => source.lots.map(lot => lotStage(source, lot))))])
const supplierKey = (lot: ProductionLot) => lot.supplierId || lot.supplier || 'unassigned'
const supplierForLot = (lot: ProductionLot) => externalSuppliers.value.suppliers.find(item => lot.supplierId ? item.id === lot.supplierId : item.name === lot.supplier)
const externalSupplierOptions = computed(() => [...new Map(externalSources.value.flatMap(source => source.lots)
  .filter(lot => !externalCategory.value || supplierForLot(lot)?.categoryIds.includes(externalCategory.value))
  .map(lot => [supplierKey(lot), { value: supplierKey(lot), label: lot.supplier || supplierForLot(lot)?.name || '未指定供应商' }])).values()])
const externalStageOptions = computed(() => [...new Set(externalSources.value.flatMap(source => source.lots.map(lot => lotStage(source, lot))))])
const trackingSupplierOptions = computed(() => {
  const suppliers = [...new Map(releaseSources.value.flatMap(source => source.lots)
    .filter(lot => lot.route === 'External' && trackingCategory.value !== 'self'
      && (!trackingCategory.value || supplierForLot(lot)?.categoryIds.includes(trackingCategory.value)))
    .map(lot => [supplierKey(lot), { value: supplierKey(lot), label: lot.supplier || supplierForLot(lot)?.name || '未指定供应商' }])).values()]
  return trackingCategory.value && trackingCategory.value !== 'self' ? suppliers : [{ value: 'self', label: '自制' }, ...suppliers]
})
const trackingStageOptions = computed(() => {
  return [...new Set(releaseSources.value.flatMap(source => source.lines.flatMap(line => {
    const lots = source.lots.filter(lot => lot.lineId === line.id)
    return lots.length ? lots.map(lot => lotStage(source, lot)) : [lineState(source, line)]
  })))]
})
function withinDates(value: string | null | undefined, start: string, end: string) {
  if (start && end && start > end) return false
  if (!start && !end) return true
  const date = value?.slice(0, 10)
  return Boolean(date && (!start || date >= start) && (!end || date <= end))
}
function matchesExternalLot(lot: ProductionLot, source = externalBase.value) {
  if (!source || externalOnlyOpen.value && !isProductionLotOpen(source, lot)) return false
  if (invalidExternalDates.value || (externalSupplier.value && supplierKey(lot) !== externalSupplier.value)) return false
  if (externalCategory.value && !supplierForLot(lot)?.categoryIds.includes(externalCategory.value)) return false
  if (externalStage.value && lotStage(source, lot) !== externalStage.value) return false
  const date = externalDateType.value === 'promised' ? lot.promisedOn : externalDateType.value === 'published'
    ? source.publishedAt : source.lines.find(line => line.id === lot.lineId)?.requiredOn
  return withinDates(date, externalStart.value, externalEnd.value)
}
function matchesSelfLot(lot: ProductionLot, source = selfBase.value) {
  if (!source || selfOnlyOpen.value && !isProductionLotOpen(source, lot)) return false
  if (selfResource.value && (selfResource.value === 'unassigned' ? lot.schedule.length > 0 : !lot.schedule.some(slot => slot.resourceGroupId === selfResource.value))) return false
  if (selfAssignee.value && (selfAssignee.value === 'unassigned' ? Boolean(lot.assignee?.trim()) : lot.assignee?.trim() !== selfAssignee.value)) return false
  if (selfStage.value && lotStage(source, lot) !== selfStage.value) return false
  const date = selfDateType.value === 'promised' ? lot.promisedOn : selfDateType.value === 'published'
    ? source.publishedAt : source.lines.find(line => line.id === lot.lineId)?.requiredOn
  return withinDates(date, selfStart.value, selfEnd.value)
}
function matchesTrackingLot(lot: ProductionLot, source = record.value?.state) {
  if (!source) return false
  if (trackingSupplier.value && (lot.route === 'Self' ? trackingSupplier.value !== 'self' : supplierKey(lot) !== trackingSupplier.value)) return false
  if (trackingCategory.value && (lot.route === 'Self' ? trackingCategory.value !== 'self'
    : trackingCategory.value === 'self' || !supplierForLot(lot)?.categoryIds.includes(trackingCategory.value))) return false
  if (trackingStage.value && lotStage(source, lot) !== trackingStage.value) return false
  const date = trackingDateType.value === 'promised' ? lot.promisedOn : trackingDateType.value === 'published'
    ? source.publishedAt : source.lines.find(line => line.id === lot.lineId)?.requiredOn
  return withinDates(date, trackingStart.value, trackingEnd.value)
}
function matchesTrackingLine(line: ProductionLine, source = record.value?.state) {
  if (!source) return false
  const lots = source.lots.filter(lot => lot.lineId === line.id)
  if (lots.length) return lots.some(lot => matchesTrackingLot(lot, source))
  if (trackingSupplier.value || trackingCategory.value || trackingStage.value && lineState(source, line) !== trackingStage.value) return false
  const date = trackingDateType.value === 'promised' ? undefined : trackingDateType.value === 'published' ? source.publishedAt : line.requiredOn
  return withinDates(date, trackingStart.value, trackingEnd.value)
}
const state = computed(() => {
  if (!record.value) return null
  if (tab.value === 'schedule') return selfBase.value && (selfProductionState(selfBase.value, matchesSelfLot)
    ?? { ...selfBase.value, lots: [], lines: [], movements: [] })
  if (tab.value !== 'external') return record.value.state
  return externalBase.value && (externalProductionState(externalBase.value, matchesExternalLot)
    ?? { ...externalBase.value, lots: [], lines: [], movements: [] })
})
function resetExternalFilters() {
  externalSupplier.value = ''; externalCategory.value = ''; externalStage.value = ''
  externalDateType.value = 'required'; externalStart.value = ''; externalEnd.value = ''
  lineSearch.value = ''; lineFilter.value = 'all'
  externalOnlyOpen.value = true
}
function resetSelfFilters() {
  selfResource.value = ''; selfAssignee.value = ''; selfStage.value = ''
  selfDateType.value = 'required'; selfStart.value = ''; selfEnd.value = ''
  lineSearch.value = ''; scheduleFilter.value = 'all'
  selfOnlyOpen.value = true
}
function resetTrackingFilters() {
  trackingSupplier.value = ''; trackingCategory.value = ''; trackingStage.value = ''
  trackingDateType.value = 'required'; trackingStart.value = ''; trackingEnd.value = ''
  lineSearch.value = ''; lineFilter.value = 'all'
  trackingOnlyUnassigned.value = false; trackingOnlyOpen.value = true
}
const selectedLine = computed(() => state.value?.lines.find(item => item.id === selectedLineId.value))
const lineLots = computed(() => state.value?.lots.filter(lot => lot.lineId === selectedLineId.value && (tab.value !== 'tracking' || matchesTrackingLot(lot))) ?? [])
const selectedLot = computed(() => lineLots.value.find(lot => lot.id === selectedLotId.value))
const allocationBlocked = computed(() => state.value && selectedLot.value ? allocationChangeBlockReason(state.value, selectedLot.value) : '')
const unassigned = computed(() => state.value && selectedLine.value ? unassignedQuantity(state.value, selectedLine.value) : 0)
const qualifiedCount = computed(() => lineRows.value.filter(({ source, line }) => lineTotal(source, line.id, 'Pass') >= line.requiredQuantity).length)
const issuedCount = computed(() => lineRows.value.filter(({ source, line }) => lineTotal(source, line.id, 'Issue') >= line.requiredQuantity).length)
const currentOpenCount = computed(() => lineRows.value.filter(({ source, line }) => isProductionLineOpen(source, line)).length)
const can = (permission: string) => props.permissions.includes(permission)
const canReadDrawings = computed(() => can('project.content.view'))
const canAssign = computed(() => can('production.assign'))
const canManageSuppliers = computed(() => can('production.supplier.manage'))
const canReadSuppliers = computed(() => can('production.supplier.view') || canManageSuppliers.value)
const supplierOnly = computed(() => canReadSuppliers.value && !canReadDrawings.value && !['production.assign','production.schedule','production.execute','production.inspect','production.issue','production.capacity.manage','production.drawing.manage'].some(can))
watch([canReadSuppliers, supplierOnly], ([allowed, only]) => { if (!allowed && tab.value === 'suppliers') tab.value = 'tracking'; else if (only) tab.value = 'suppliers' }, { immediate: true })
const canAdjustDelivery = computed(() => canAssign.value || can('production.drawing.manage'))



const canSchedule = computed(() => can('production.schedule'))
const canManageCapacity = computed(() => can('production.capacity.manage'))
const movementLabels = productionMovementLabels
const visibleProjects = computed(() => projects.value.map(project => ({ ...project, packages: project.packages.filter(item => {
  if (!includeComplete.value && ['已完结', '变更已结案', '领料完成', '已领用', '已领用／含报废', '已取消', '已取消／转版', '已取消剩余流程', '已报废', '已转版'].includes(item.status)) return false
  if (statusFilter.value && item.status !== statusFilter.value) return false
  const query = projectSearch.value.trim().toLowerCase()
  return !query || `${project.code} ${project.name} ${item.packageNumber}`.toLowerCase().includes(query)
}) })).filter(project => project.packages.length > 0))
const visiblePackageTotals = computed(() => visibleProjects.value.flatMap(project => project.packages)
  .reduce((total, item) => ({ packages: total.packages + 1, lines: total.lines + item.totalLines,
    quantity: total.quantity + (item.totalQuantity ?? 0) }), { packages: 0, lines: 0, quantity: 0 }))
const overviewSources = computed(() => tab.value === 'schedule'
  ? selfSources.value.flatMap(source => selfProductionState(source, lot => matchesSelfLot(lot, source)) ?? [])
  : tab.value === 'external' ? externalSources.value.flatMap(source => externalProductionState(source, lot => matchesExternalLot(lot, source)) ?? [])
    : releaseSources.value)
const lineRows = computed(() => (allCategories.value ? overviewSources.value : state.value ? [state.value] : [])
  .flatMap(source => source.lines.map(line => ({ source, line, key: JSON.stringify([source.releasePackageId, line.id]) }))))
const visibleLineRows = computed(() => lineRows.value.filter(({ source, line }) => {
  if (tab.value === 'tracking' && (!matchesTrackingLine(line, source)
    || trackingOnlyUnassigned.value && unassignedQuantity(source, line) <= 1e-9
    || trackingOnlyOpen.value && !isProductionLineOpen(source, line))) return false
  if (lineFilter.value === 'open' && !isProductionLineOpen(source, line)) return false
  if (lineFilter.value === 'urgent' && line.priority !== 'Urgent') return false
  const query = lineSearch.value.trim().toLowerCase()
  return !query || `${line.drawingNumber} ${line.materialCode ?? ''} ${line.name} ${line.material ?? ''} ${allCategories.value ? `${source.projectCode} ${source.projectName} ${source.packageNumber}` : ''}`.toLowerCase().includes(query)
}))
const visibleLines = computed(() => visibleLineRows.value.map(item => item.line))
const linePageCount = computed(() => Math.max(1, Math.ceil(visibleLines.value.length / linePageSize)))
const pagedLineRows = computed(() => visibleLineRows.value.slice((linePage.value - 1) * linePageSize,
  linePage.value * linePageSize))
const checkedLines = computed(() => {
  const ids = new Set(checkedLineIds.value)
  return state.value?.lines.filter(line => ids.has(line.id)) ?? []
})
const allVisibleChecked = computed(() => visibleLines.value.length > 0
  && visibleLines.value.every(line => checkedLineIds.value.includes(line.id)))
const someVisibleChecked = computed(() => !allVisibleChecked.value
  && visibleLines.value.some(line => checkedLineIds.value.includes(line.id)))
const selectedLotMovements = computed(() => state.value?.movements.filter(item => item.lotId === selectedLotId.value) ?? [])
const recentMovements = computed(() => state.value?.movements.filter(item =>
  lineLots.value.some(lot => lot.id === item.lotId)).slice(-8).reverse() ?? [])
const resourceNames = computed(() => Array.from(new Set([
  'CNC组', '车床组', '铣床组', '磨床组', ...(capacity.value?.calendar.days.map(item => item.resourceGroupId) ?? []),
])))
const selfLots = computed(() => (allCategories.value ? overviewSources.value : state.value ? [state.value] : []).flatMap(source => source.lots.filter(lot => lot.route === 'Self').map(lot => ({
  ...lot, source, key: JSON.stringify([source.releasePackageId, lot.id]), line: source.lines.find(line => line.id === lot.lineId),
}))))
const plannedLots = computed(() => selfLots.value.filter(lot => lot.schedule.length > 0).length)
const visibleScheduleLots = computed(() => selfLots.value.filter(lot => {
  if (scheduleFilter.value === 'unplanned' && lot.schedule.length) return false
  if (scheduleFilter.value === 'planned' && !lot.schedule.length) return false
  const query = lineSearch.value.trim().toLowerCase()
  return !query || `${lot.line?.drawingNumber} ${lot.line?.materialCode ?? ''} ${lot.line?.name} ${lot.line?.material ?? ''} ${allCategories.value ? `${lot.source.projectCode} ${lot.source.projectName} ${lot.source.packageNumber}` : ''}`.toLowerCase().includes(query)
}))
const schedulePageCount = computed(() => Math.max(1, Math.ceil(visibleScheduleLots.value.length / linePageSize)))
const pagedScheduleLots = computed(() => visibleScheduleLots.value.slice((schedulePage.value - 1) * linePageSize, schedulePage.value * linePageSize))
const mayScheduleLot = (lot: ProductionLot) => !lot.frozen && (lot.schedule.length > 0 || Boolean(state.value && (movementTotal(state.value, lot.id, 'ProductionReceived') > 0 || movementTotal(state.value, lot.id, 'Start') > 0)))
const selectableScheduleLots = computed(() => visibleScheduleLots.value.filter(lot => canReadDrawings.value || canSchedule.value && mayScheduleLot(lot)))
const checkedSelfLots = computed(() => selfLots.value.filter(lot => checkedScheduleIds.value.includes(lot.id)))
const checkedScheduleLots = computed(() => selfLots.value.filter(lot => checkedScheduleIds.value.includes(lot.id) && mayScheduleLot(lot)))
const checkedDrawingLines = computed(() => tab.value === 'external' || tab.value === 'tracking' ? checkedLines.value
  : [...new Map(checkedSelfLots.value.filter(lot => lot.line).map(lot => [lot.line!.id, lot.line!])).values()])
const allScheduleChecked = computed(() => selectableScheduleLots.value.length > 0 && selectableScheduleLots.value.every(lot => checkedScheduleIds.value.includes(lot.id)))
const someScheduleChecked = computed(() => !allScheduleChecked.value && selectableScheduleLots.value.some(lot => checkedScheduleIds.value.includes(lot.id)))
let generation = 0, refreshGeneration = 0
onBeforeUnmount(() => { generation++; refreshGeneration++; if (cardUrl.value) URL.revokeObjectURL(cardUrl.value) })
const dateLabel = (value?: string | null) => value?.slice(0, 10) || '待确认'
const isLateDate = (promisedOn?: string | null, requiredOn?: string | null) =>
  Boolean(promisedOn && requiredOn && promisedOn.slice(0, 10) > requiredOn.slice(0, 10))
const lineForecastOn = (line: ProductionLine, source = state.value) => source?.lots.filter(lot => lot.lineId === line.id)
  .map(lot => lot.promisedOn).sort().at(-1)
const progress = (value: number, total: number) => total > 0 ? Math.round(value * 100 / total) : 0
const priorityClass = (value: string) => value === 'Urgent' ? 'urgent' : value === 'Priority' ? 'priority' : 'normal'
const routeLabel = (value: ProductionRoute) => value === 'Self' ? '自制' : '外协'
const lineMetric = (line: ProductionLine, kind: string, source = state.value) => source ? lineTotal(source, line.id, kind) : 0


function reportError(message: string) {
  error.value = userErrorMessage(message)
  ElMessage.error(message)
}

function toggleVisibleLines(event: Event) {
  const ids = new Set(checkedLineIds.value)
  for (const line of visibleLines.value) {
    if ((event.target as HTMLInputElement).checked) ids.add(line.id)
    else ids.delete(line.id)
  }
  checkedLineIds.value = [...ids]
}
function toggleScheduleLots(event: Event) {
  const ids = new Set(checkedScheduleIds.value)
  for (const lot of selectableScheduleLots.value) {
    if ((event.target as HTMLInputElement).checked) ids.add(lot.id)
    else ids.delete(lot.id)
  }
  checkedScheduleIds.value = [...ids]
}
function scheduleBatchUpdated(updated: ProductionReleaseRecord, lotId: string) {
  if (updated.state.releasePackageId !== selectedReleaseId.value) return
  record.value = updated
  checkedScheduleIds.value = checkedScheduleIds.value.filter(id => id !== lotId)
}
async function scheduleBatchCompleted(count: number) {
  showScheduleBatch.value = false
  await nextTick()
  ElMessage.success(`批量排程已完成，共 ${count} 个批次`)
  await refresh()
}
function showLineDrawing(line: ProductionLine | undefined, action: 'preview' | 'download', source = state.value) {
  if (canReadDrawings.value && line && source) drawingContext.value = { state: source, line, action }
}
async function downloadCheckedDrawings(format: 'Pdf' | 'Step' | 'Bom' | 'Package') {
  if (!canReadDrawings.value || !state.value || (format !== 'Package' && !checkedDrawingLines.value.length) || downloading.value) return
  const snapshot = state.value, lines = [...checkedDrawingLines.value], token = props.token
  downloading.value = true; downloadStatus.value = format === 'Package' ? '正在读取本包正式图纸…' : '正在核对所选物料的正式图纸…'; error.value = ''
  try {
    const available = await listProductionDrawings(token, true, snapshot.projectId)
    const packageId = snapshot.formalReleasePackageId || snapshot.releasePackageId
    const matched = format === 'Package' ? [] : lines.map(line => ({ line, drawings: drawingsForProductionLine(available, snapshot, line) }))
    const missing = matched.filter(item => !item.drawings.length)
    if (missing.length) throw new Error(`所选物料 ${missing.slice(0, 3).map(item => item.line.drawingNumber || item.line.name).join('、')}${missing.length > 3 ? '等' : ''} 在本发布包中没有可用的正式图纸，请到图纸资料核对。`)
    const drawings = [...new Map((format === 'Package' ? available.filter(item => item.projectId === snapshot.projectId && item.releasePackageId === packageId)
      : matched.flatMap(item => item.drawings)).map(item => [item.versionId, item])).values()]
    if (!drawings.length) throw new Error('此发布包没有可用的正式图纸，请到图纸资料核对。')
    if (format !== 'Bom' && drawings.length > 200) throw new Error('一次最多下载 200 张正式图纸，请分批勾选。')
    if ((format === 'Pdf' || format === 'Package') && drawings.some(item => !item.pdfReady)) throw new Error('所选图纸中有 PDF 待转换，不能用旧版替代；请稍后重试。')
    if ((format === 'Step' || format === 'Package') && drawings.some(item => !item.modelFiles?.length || item.modelFiles.some(model => !model.stepReady || !model.versionId)))
      throw new Error('所选图纸存在未关联同包三维模型或 STEP 待转换项，不能用 SolidWorks 源文件替代。')
    if ((format === 'Bom' || format === 'Package') && drawings.some(item => !item.bomItems?.length || !item.bomQuantities?.length))
      throw new Error('所选图纸缺少发布包冻结的BOM数据，无法导出；请刷新后重试。')
    if (drawings.some(item => !item.isCurrent) && !window.confirm('所选图纸包含已被替代的历史正式版，确定下载？')) { downloadStatus.value = '已取消下载。'; return }
    const packageNumber = drawings[0].releasePackageNumber || snapshot.packageNumber
    if (format === 'Bom' || format === 'Package') {
      downloadStatus.value = '正在核对发布包冻结的 BOM…'
      const file = await createProductionDrawingBomDownload(drawings, token, snapshot.projectId, { id: packageId, number: packageNumber }, format === 'Bom')
      if (format === 'Bom') await downloadGeneratedFile(file.blob, file.fileName)
      else {
        downloadStatus.value = '正在下载本包 STEP、PDF 和 BOM…'
        const results = await Promise.allSettled([
          downloadProductionDrawingArchive(snapshot.projectId, drawings.map(item => item.versionId), 'Step', token, packageNumber),
          downloadProductionDrawingArchive(snapshot.projectId, drawings.map(item => item.versionId), 'Pdf', token, packageNumber),
          downloadGeneratedFile(file.blob, file.fileName),
        ])
        const failed = results.find(result => result.status === 'rejected')
        if (failed?.status === 'rejected') throw failed.reason
      }
      downloadStatus.value = format === 'Bom' ? '所选物料 BOM 已交给浏览器保存。' : '本包 STEP、PDF 和 BOM 已交给浏览器保存。'
      return
    }
    downloadStatus.value = `正在打包 ${drawings.length} 张图纸的 ${format === 'Pdf' ? 'PDF' : 'STEP'}…`
    await downloadProductionDrawingArchive(snapshot.projectId, drawings.map(item => item.versionId), format, token, snapshot.packageNumber,
      (received, total) => { downloadStatus.value = total > 0 ? `正在下载：${Math.min(100, Math.round(received / total * 100))}%` : `正在下载：已接收 ${(received / 1024 / 1024).toFixed(1)} MB` })
    downloadStatus.value = `${format === 'Pdf' ? 'PDF' : 'STEP'} 图纸压缩包已交给浏览器保存。`
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') downloadStatus.value = '已取消下载。'
    else { downloadStatus.value = ''; reportError(userErrorMessage(cause instanceof Error ? cause.message : '图纸批量下载失败，请检查网络后重试。')) }
  } finally { downloading.value = false }
}
function batchUpdated(updated: ProductionReleaseRecord, lineId: string) {
  if (updated.state.releasePackageId !== selectedReleaseId.value) return
  record.value = updated
  checkedLineIds.value = checkedLineIds.value.filter(id => id !== lineId)
}
async function batchCompleted(count: number) {
  const action = batchMode.value === 'delivery' ? '调整' : '分配'
  batchMode.value = null
  await nextTick()
  ElMessage.success(`批量${action}已完成，共 ${count} 项`)
  await refresh()
}
async function allocationUpdated(updated: ProductionReleaseRecord) {
  if (updated.state.releasePackageId !== selectedReleaseId.value) return
  record.value = updated
  showAllocationEdit.value = false
  await nextTick()
  ElMessage.success('批次分配已修改，尚未开始生产的数量已按新方式安排')
  await refresh()
}

async function loadRelease(id: string) {
  const current = ++generation
  selectedReleaseId.value = id
  record.value = null
  if (!id) return
  try {
    const [value, choices] = await Promise.all([readProductionTrackingRelease(id, props.token),
      tab.value === 'external' || tab.value === 'tracking' ? readProductionTrackingSupplierChoices(id, props.token) : Promise.resolve(null)])
    if (current !== generation) return
    if (choices) externalSuppliers.value = choices
    record.value = value
    if (!value.state.lines.some(item => item.id === selectedLineId.value)) selectedLineId.value = ''
    if (!value.state.lots.some(item => item.id === selectedLotId.value && item.lineId === selectedLineId.value))
      selectedLotId.value = value.state.lots.find(item => item.lineId === selectedLineId.value)?.id ?? ''
    error.value = ''
  } catch (cause) { if (current === generation) reportError(cause instanceof Error ? cause.message : '') }
}
async function refresh() {
  if (!props.token) return
  const current = ++refreshGeneration, activeTab = tab.value
  pending.value = true
  error.value = ''
  try {
    if (activeTab === 'capacity') { await loadCapacity(); return }
    if (activeTab === 'suppliers' || activeTab === 'drawings') return
    const values = activeTab === 'external' ? await listProductionTrackingProjects(props.token, false, true) : await listProductionTrackingProjects(props.token, activeTab === 'schedule')
    if (current !== refreshGeneration || tab.value !== activeTab) return
    projects.value = values
    if (allCategories.value) {
      const records: ProductionReleaseRecord[] = [], choices: ProductionSupplierChoices[] = []
      const ids = [...new Set(values.flatMap(project => project.packages.map(item => item.releasePackageId)))]
      for (let index = 0; index < ids.length; index += 4) {
        const group = await Promise.all(ids.slice(index, index + 4).map(async id => {
          const [record, suppliers] = await Promise.all([readProductionTrackingRelease(id, props.token), activeTab === 'schedule' ? Promise.resolve(null) : readProductionTrackingSupplierChoices(id, props.token)])
          return { record, suppliers }
        }))
        if (current !== refreshGeneration || !allCategories.value) return
        records.push(...group.map(item => item.record)); choices.push(...group.flatMap(item => item.suppliers ?? []))
      }
      allRecords.value = records
      externalSuppliers.value = { companyId: choices[0]?.companyId ?? '',
        suppliers: [...new Map(choices.flatMap(item => item.suppliers).map(item => [item.id, item])).values()],
        categories: [...new Map(choices.flatMap(item => item.categories).map(item => [item.id, item])).values()] }
      record.value = records[0] ?? null
      if (activeTab === 'schedule') await loadCapacity()
      return
    }
    if (!values.some(item => item.id === selectedProjectId.value)) selectedProjectId.value = values[0]?.id ?? ''
    const project = values.find(item => item.id === selectedProjectId.value)
    const id = project?.packages.some(item => item.releasePackageId === selectedReleaseId.value)
      ? selectedReleaseId.value : project?.packages[0]?.releasePackageId ?? ''
    await loadRelease(id)
    if (activeTab === 'schedule') await loadCapacity()
  } catch (cause) { if (current === refreshGeneration && tab.value === activeTab) reportError(cause instanceof Error ? cause.message : '') }
  finally { if (current === refreshGeneration) pending.value = false }
}
watch(() => props.token, () => { if (tab.value === 'execution') { selectedLineId.value = ''; selectedLotId.value = ''; executionLotId.value = '' }; void refresh() }, { immediate: true })
watch([lineSearch, lineFilter, selectedReleaseId, externalSupplier, externalCategory, externalStage, externalDateType, externalStart, externalEnd, trackingSupplier, trackingCategory, trackingStage, trackingDateType, trackingStart, trackingEnd, trackingOnlyUnassigned, trackingOnlyOpen, externalOnlyOpen], () => { linePage.value = 1 })
watch([trackingOnlyUnassigned, currentOnlyOpen], () => { checkedLineIds.value = checkedLineIds.value.filter(id => visibleLines.value.some(line => line.id === id)) })
watch(externalCategory, () => { if (!externalSupplierOptions.value.some(item => item.value === externalSupplier.value)) externalSupplier.value = '' })
watch(trackingCategory, () => { if (!trackingSupplierOptions.value.some(item => item.value === trackingSupplier.value)) trackingSupplier.value = '' })
watch([trackingSupplier, trackingCategory, trackingStage, trackingDateType, trackingStart, trackingEnd], () => {
  if (tab.value === 'tracking') checkedLineIds.value = checkedLineIds.value.filter(id => record.value?.state.lines.some(line => line.id === id && matchesTrackingLine(line)))
})
watch(visibleLines, lines => {
  if (!record.value || tab.value !== 'external' && tab.value !== 'tracking') return
  if (!lines.some(line => line.id === selectedLineId.value)) { selectedLineId.value = ''; selectedLotId.value = '' }
  else if (!lineLots.value.some(lot => lot.id === selectedLotId.value)) selectedLotId.value = lineLots.value[0]?.id ?? ''
})
watch([lineSearch, scheduleFilter, selectedReleaseId, selfResource, selfAssignee, selfStage, selfDateType, selfStart, selfEnd, selfOnlyOpen], () => { schedulePage.value = 1 })
watch(schedulePageCount, count => { schedulePage.value = Math.min(schedulePage.value, count) })
watch(selectedReleaseId, () => { selectedLineId.value = ''; selectedLotId.value = ''; checkedLineIds.value = []; batchMode.value = null; checkedScheduleIds.value = []; showScheduleBatch.value = false; drawingContext.value = undefined; showAllocationEdit.value = false }, { flush: 'sync' })
watch(() => state.value?.lots, lots => {
  if (lots) checkedScheduleIds.value = checkedScheduleIds.value.filter(id => lots.some(lot => lot.id === id && lot.route === 'Self'))
})
watch(() => state.value?.lines, lines => {
  if (lines) checkedLineIds.value = checkedLineIds.value.filter(id => lines.some(line => line.id === id))
})
watch(tab, value => {
  allCategories.value = false; allRecords.value = []
  checkedLineIds.value = []; checkedScheduleIds.value = []
  if (!downloading.value) downloadStatus.value = ''
  if (value === 'suppliers' && !canReadSuppliers.value) { tab.value = 'tracking'; return }
  if (supplierOnly.value && value !== 'suppliers') { tab.value = 'suppliers'; return }
  if (value === 'execution' || value === 'external') { selectedLineId.value = ''; selectedLotId.value = ''; executionLotId.value = ''; scanCandidates.value = []; scanCode.value = '' }
  if (value === 'capacity') void loadCapacity()
  else if (value === 'schedule' || value === 'external' || value === 'tracking' || value === 'execution') { projects.value = []; statusFilter.value = ''; void refresh() }
})
watch(selectedLineId, id => {
  if (!state.value?.lots.some(item => item.id === selectedLotId.value && item.lineId === id))
    selectedLotId.value = state.value?.lots.find(item => item.lineId === id)?.id ?? ''
})
watch([tab, selectedLot, visibleScheduleLots], ([activeTab, lot]) => {
  if (activeTab !== 'schedule' || !record.value) return
  if (allCategories.value) { selectedLineId.value = ''; selectedLotId.value = ''; scheduleSlots.value = []; return }
  if (lot?.route !== 'Self' || !visibleScheduleLots.value.some(item => item.id === lot.id)) {
    const first = visibleScheduleLots.value[0]
    if (first) { chooseScheduleLot(first); return }
    selectedLineId.value = ''; selectedLotId.value = ''; scheduleSlots.value = []; schedulePromisedOn.value = ''
    return
  }
  scheduleSlots.value = lot?.route === 'Self' ? (lot.schedule.length ? lot.schedule.map(item => ({ ...item })) : [
    { resourceGroupId: resourceGroup.value, date: availableDate.value, hours: lot.quantity },
  ]) : []
  schedulePromisedOn.value = lot?.route === 'Self' ? lot.promisedOn ?? availableDate.value : ''
  scheduleReason.value = ''
  scheduleRequestId.value = productionRequestId()
})
watch(() => [selectedLine.value?.id, selectedLine.value?.priority, selectedLine.value?.requiredOn], () => {
  deliveryPriority.value = selectedLine.value?.priority ?? 'Normal'
  deliveryRequiredOn.value = selectedLine.value?.requiredOn ?? ''
})
watch(() => [selectedLot.value?.id, selectedLot.value?.promisedOn], () => { promiseOn.value = selectedLot.value?.promisedOn ?? '' })

async function chooseProject(id: string) {
  await chooseRelease(id, projects.value.find(item => item.id === id)?.packages[0]?.releasePackageId ?? '')
}
async function chooseRelease(projectId: string, id: string) {
  if (allCategories.value) { allCategories.value = false; allRecords.value = []; refreshGeneration++; pending.value = false }
  selectedProjectId.value = projectId
  await loadRelease(id)
}
function openAllCategories() {
  allCategories.value = true; allRecords.value = []
  generation++; selectedProjectId.value = ''; selectedReleaseId.value = ''; record.value = null
  selectedLineId.value = ''; selectedLotId.value = ''; checkedLineIds.value = []
  projectSearch.value = ''; statusFilter.value = ''; includeComplete.value = true
  void refresh()
}
async function openOverviewLine(source: ProductionReleaseState, line: ProductionLine) {
  await chooseRelease(source.projectId, source.releasePackageId)
  if (record.value?.state.releasePackageId === source.releasePackageId) selectedLineId.value = line.id
}
async function openOverviewLot(source: ProductionReleaseState, lot: ProductionLot) {
  await chooseRelease(source.projectId, source.releasePackageId)
  if (record.value?.state.releasePackageId === source.releasePackageId) chooseScheduleLot(lot)
}
async function run(task: () => Promise<ProductionReleaseRecord>, success: string) {
  if (pending.value) return
  pending.value = true; error.value = ''
  try {
    const updated = await task()
    record.value = updated
    ElMessage.success(success)
    await refresh()
  } catch (cause) { reportError(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
function receive() {
  if (!selectedReleaseId.value) return
  void run(() => receiveProductionTrackingRelease(selectedReleaseId.value, props.token), '发布包已接收')
}
function assign() {
  if (!selectedLine.value || !selectedReleaseId.value) return
  if (assignRoute.value === 'External' && !assignSupplierId.value) { ElMessage.warning('请从供应商台账选择供应商。'); return }
  const input = { lineId: selectedLine.value.id, route: assignRoute.value, quantity: assignQuantity.value,
    supplier: assignSupplier.value, supplierId: assignRoute.value === 'External' ? assignSupplierId.value : undefined, assignee: assignAssignee.value, promisedOn: assignPromisedOn.value || null,
    requestId: assignRequestId.value }
  void run(() => assignProductionLot(selectedReleaseId.value, input, props.token), '生产批次已分配')
    .then(() => { if (!error.value) { assignRequestId.value = productionRequestId(); assignQuantity.value = 1 } })
}
function saveDelivery() {
  if (!selectedLine.value || !selectedReleaseId.value) return
  const input = { lineId: selectedLine.value.id, priority: deliveryPriority.value,
    requiredOn: deliveryRequiredOn.value || null, reason: deliveryReason.value,
    requestId: deliveryRequestId.value }
  void run(() => updateProductionLineDelivery(selectedReleaseId.value, input, props.token), '紧急程度和需求日期已更新')
    .then(() => { if (!error.value) { deliveryRequestId.value = productionRequestId(); deliveryReason.value = '' } })
}
function savePromise() {
  if (!selectedLot.value || !selectedReleaseId.value) return
  const input = { lotId: selectedLot.value.id, promisedOn: promiseOn.value,
    reason: promiseReason.value, requestId: promiseRequestId.value }
  void run(() => updateProductionLotPromise(selectedReleaseId.value, input, props.token), '供应商承诺交期已更新')
    .then(() => { if (!error.value) { promiseRequestId.value = productionRequestId(); promiseReason.value = '' } })
}
async function openRevision(lot: ProductionLot) {
  selectedLineId.value = lot.lineId
  selectedLotId.value = lot.id
  await nextTick()
  await revisionPanel.value?.focus()
}
async function openLineRevision(line: ProductionLine) {
  selectedLineId.value = line.id
  await nextTick()
  const lot = lineLots.value.find(item => item.frozen)
  if (lot) await openRevision(lot)
}
function resolveRevision(input: Parameters<typeof resolveProductionRevision>[1]) {
  if (!selectedReleaseId.value || !selectedLot.value?.frozen || selectedLot.value.id !== input.lotId
    || !(canAssign.value || canSchedule.value && selectedLot.value.route === 'Self')) return
  const success = input.action === 'TransferNew' ? '已转入新版发布包，请在发布跟踪中打开新版包并重新分配批次'
    : input.action === 'CancelRemaining' ? '已取消指定数量并解除本次变更冻结，其余未取消数量可继续办理'
    : '已确认继续旧版并解除本次变更冻结'
  void run(() => resolveProductionRevision(selectedReleaseId.value, input, props.token), success)
}
async function loadCapacity() {
  try { capacity.value = await readProductionCapacity(props.token) }
  catch (cause) { reportError(cause instanceof Error ? cause.message : '') }
}
async function saveCapacity() {
  if (!capacity.value || pending.value) return
  pending.value = true; error.value = ''
  try {
    await saveProductionCapacity({ resourceGroupId: resourceGroup.value, date: availableDate.value,
      availableHours: availableHours.value, expectedRowVersion: capacity.value.calendar.rowVersion }, props.token)
    await loadCapacity(); if (!error.value) ElMessage.success('资源组可用工时已保存')
  } catch (cause) { reportError(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
function chooseScheduleLot(lot: ProductionLot) {
  selectedLineId.value = lot.lineId
  selectedLotId.value = lot.id
  tab.value = 'schedule'
}
function saveSchedule() {
  if (!selectedReleaseId.value || selectedLot.value?.route !== 'Self' || selectedLot.value.frozen || !canSchedule.value) return
  const input = { lotId: selectedLot.value.id, slots: scheduleSlots.value, promisedOn: schedulePromisedOn.value,
    changeReason: scheduleReason.value,
    requestId: scheduleRequestId.value }
  void run(() => scheduleProductionLot(selectedReleaseId.value, input, props.token), '排产已确认')
    .then(() => { if (!error.value) { scheduleRequestId.value = productionRequestId(); scheduleReason.value = '' } })
}
async function locateScan() {
  if (!scanCode.value.trim()) return
  error.value = ''
  try {
    scanCandidates.value = await resolveProductionScan(scanCode.value.trim(), props.token)
    if (!scanCandidates.value.length) reportError('该图纸没有可查看的生产任务。')
    else if (scanCandidates.value.length === 1) await chooseScan(scanCandidates.value[0])
  } catch (cause) { reportError(cause instanceof Error ? cause.message : '') }
}
async function chooseScan(item: ProductionScanResult) {
  const project = projects.value.find(project => project.packages.some(entry => entry.releasePackageId === item.releasePackageId))
  if (!project) { reportError('该任务不在当前授权项目中。'); return }
  await chooseRelease(project.id, item.releasePackageId)
  executionLotId.value = item.lotId ?? ''
  selectedLineId.value = item.lineId
  selectedLotId.value = item.lotId ?? state.value?.lots.find(lot => lot.lineId === item.lineId)?.id ?? ''
  scanCandidates.value = []; scanCode.value = ''; tab.value = 'execution'
}
async function openLotCard(lot: ProductionLot) {
  if (!state.value) return
  error.value = ''
  try {
    const blob = await readProductionLotQr(state.value.releasePackageId, lot.id, props.token)
    if (cardUrl.value) URL.revokeObjectURL(cardUrl.value)
    cardUrl.value = URL.createObjectURL(blob)
    cardLot.value = lot
  } catch (cause) { reportError(cause instanceof Error ? cause.message : '') }
}
function closeLotCard() {
  if (cardUrl.value) URL.revokeObjectURL(cardUrl.value)
  cardUrl.value = ''; cardLot.value = null
}
function printLotCard() {
  if (!cardLot.value || !state.value || !cardUrl.value) return
  const frame = document.createElement('iframe')
  frame.style.cssText = 'position:fixed;left:-10000px;top:0;width:450px;height:450px;border:0'
  document.body.appendChild(frame)
  const documentInFrame = frame.contentDocument
  if (!documentInFrame) { frame.remove(); return }
  documentInFrame.title = '生产批次流转卡'
  documentInFrame.body.style.cssText = 'font:16px Microsoft YaHei,sans-serif;padding:18px;text-align:center'
  const heading = documentInFrame.createElement('h2')
  heading.textContent = `${state.value.packageNumber} · ${state.value.lines.find(line => line.id === cardLot.value?.lineId)?.name ?? '生产批次'}`
  const details = documentInFrame.createElement('p')
  details.textContent = `${state.value.projectCode} · ${cardLot.value.route === 'Self' ? '自制' : '外协'} · ${cardLot.value.quantity} 件`
  const qr = documentInFrame.createElement('img')
  qr.alt = '生产批次二维码'; qr.width = 260; qr.height = 260; qr.src = cardUrl.value
  const code = documentInFrame.createElement('p')
  code.textContent = `批次 ${cardLot.value.id}`
  documentInFrame.body.append(heading, details, qr, code)
  qr.onload = () => { frame.contentWindow?.focus(); frame.contentWindow?.print() }
  if (frame.contentWindow) frame.contentWindow.onafterprint = () => frame.remove()
  setTimeout(() => frame.remove(), 120000)
}
</script>

<template>
  <section class="production-workbench" aria-label="制造中心">
    <nav class="production-workbench__tabs pdm-tabs" aria-label="制造中心页签">
      <button v-if="!supplierOnly" type="button" :aria-current="tab === 'tracking' ? 'page' : undefined" @click="tab = 'tracking'">发布跟踪</button>
      <button v-if="!supplierOnly" type="button" :aria-current="tab === 'external' ? 'page' : undefined" @click="tab = 'external'">外协追溯</button>
      <button v-if="!supplierOnly" type="button" :aria-current="tab === 'schedule' ? 'page' : undefined" @click="tab = 'schedule'">自制追溯</button>
      <button v-if="!supplierOnly" type="button" :aria-current="tab === 'capacity' ? 'page' : undefined" @click="tab = 'capacity'">产能分析</button>
      <button v-if="!supplierOnly" type="button" :aria-current="tab === 'execution' ? 'page' : undefined" @click="tab = 'execution'">生产执行</button>
      <button v-if="!supplierOnly" type="button" :aria-current="tab === 'drawings' ? 'page' : undefined" @click="tab = 'drawings'">图纸资料</button>
      <button v-if="canReadSuppliers" type="button" :aria-current="tab === 'suppliers' ? 'page' : undefined" @click="tab = 'suppliers'">供应商库</button>
    </nav>
    <template v-if="tab === 'tracking' || tab === 'external' || tab === 'schedule'">
      <div class="production-workbench__grid" :class="{ 'production-workbench__grid--schedule': tab === 'schedule' }">
        <aside class="production-workbench__tree pdm-panel" aria-label="项目与发布包">
          <div class="production-workbench__toolbar">
            <select v-model="statusFilter" aria-label="筛选生产状态"><option value="">全部状态</option><option v-for="name in ['待接收','待分配','部分分配','待生产经理接图','排产','机加','包装','质检','质检完成','领料完成','已暂停','已取消','已取消剩余流程','分批流转','待生产签收','待排产','外协待交货','待质检收料','质检中','质检完成／合格待领','已领用','已领用／含报废','已排产待执行','执行中','已齐套待领用','变更待处理','无新增生产需求']" :key="name" :value="name">{{ name }}</option></select>
            <label><input v-model="includeComplete" type="checkbox">已完结</label>
            <button type="button" class="pdm-secondary-action" :disabled="pending" @click="refresh">刷新</button>
          </div>
          <div class="production-workbench__tree-search">
            <input v-model="projectSearch" type="search" placeholder="项目号、名称或发布包" aria-label="搜索生产发布包">
            <span class="production-workbench__tree-totals" role="status" aria-label="筛选发布包统计" title="当前搜索和状态筛选结果：发布包数、物料条目数、需求总数量"><span>{{ visiblePackageTotals.packages }}包</span><span>{{ visiblePackageTotals.lines }}项</span><span>{{ visiblePackageTotals.quantity.toLocaleString('zh-CN', { maximumFractionDigits: 6 }) }}个</span></span>
          </div>
          <button type="button" class="production-workbench__all-categories" :class="{ selected: allCategories }" :aria-pressed="allCategories" @click="openAllCategories">全部分类</button>
          <div v-for="project in visibleProjects" :key="project.id" class="production-workbench__tree-project">
            <button type="button" class="production-workbench__project" :class="{ selected: selectedProjectId === project.id }" @click="chooseProject(project.id)"><strong>{{ project.code }}</strong><span>{{ project.name }}</span><small>{{ project.packages.length }} 包</small></button>
            <button v-for="item in project.packages" :key="item.releasePackageId" type="button" class="production-workbench__package" :class="{ selected: selectedReleaseId === item.releasePackageId }" @click="chooseRelease(project.id, item.releasePackageId)">
              <span class="production-workbench__package-header"><strong :title="item.packageNumber">{{ item.packageNumber.replace(/^RP-.+-(\d{8}-[^-]+)$/, '$1') }}</strong><em>{{ item.status }}</em></span>
              <span class="production-workbench__package-meta">
                <small class="production-workbench__package-counts"><span>质检齐套 {{ item.qualifiedLines }}/{{ item.totalLines }} 项</span><span>领用 {{ item.issuedLines }}/{{ item.totalLines }} 项</span></small>
                <small class="production-workbench__package-required">需 {{ dateLabel(item.firstRequiredOn) }}</small>
              </span>
              <small v-if="item.urgentLines || item.atRiskLines" class="production-workbench__package-alerts"><b v-if="item.urgentLines">紧急 {{ item.urgentLines }} 项</b><b v-if="item.atRiskLines">交期风险 {{ item.atRiskLines }} 项</b></small>
            </button>
          </div>
          <p v-if="!visibleProjects.length" class="production-workbench__empty">{{ pending ? '正在读取生产发布包…' : tab === 'schedule' ? '暂无已分配自制批次的发布包' : tab === 'external' ? '暂无已分配外协批次的发布包' : '暂无符合条件的生产发布包' }}</p>
        </aside>
        <template v-if="tab === 'tracking' || tab === 'external'">
        <main class="production-workbench__lines pdm-panel" :aria-label="tab === 'external' ? '外协物料明细' : '发布包物料明细'">
          <template v-if="state">
            <header v-if="allCategories"><div><h2>全部分类</h2><p>所有项目与发布包 · {{ allRecords.length }} 个发布包 · {{ lineRows.length }} 项物料</p></div></header>
            <header v-else><div><h2>{{ state.packageNumber }} <span class="production-workbench__state">{{ selectedSummary?.status }}</span></h2><p>{{ state.projectCode }} · {{ state.projectName }} · 发布 {{ dateLabel(state.publishedAt) }}</p></div><button v-if="tab === 'tracking' && canAssign && !state.receivedAt" type="button" class="pdm-primary-action" :disabled="pending" @click="receive">供应链接收</button></header>

            <div v-if="tab === 'tracking'" class="production-workbench__trace-filters" aria-label="发布跟踪筛选" title="筛选当前发布包的自制或外协物料，日期包含起止当天">
              <label><input v-model="lineSearch" type="search" :placeholder="allCategories ? '搜索项目、发布包、图号或名称' : '搜索图号、名称或材质'" aria-label="搜索本包物料"></label>
              <label><select v-model="trackingSupplier" aria-label="筛选供应商"><option value="">全部供应商</option><option v-for="item in trackingSupplierOptions" :key="item.value" :value="item.value">{{ item.label }}</option></select></label>
              <label><select v-model="trackingCategory" aria-label="筛选供应商类型"><option value="">全部类型</option><option value="self">自制</option><option v-for="item in externalSuppliers.categories" :key="item.id" :value="item.id">{{ item.name }}</option></select></label>
              <label><select v-model="trackingStage" aria-label="筛选当前环节"><option value="">全部环节</option><option v-for="item in trackingStageOptions" :key="item" :value="item">{{ item }}</option></select></label>
              <label><select v-model="trackingDateType" aria-label="筛选日期类型"><option value="required">需求日期</option><option value="promised">预计完成日期</option><option value="published">发布日期</option></select></label>
              <label><el-date-picker v-model="trackingDateRange" type="daterange" value-format="YYYY-MM-DD" format="YYYY-MM-DD" aria-label="筛选日期区间" range-separator="-" start-placeholder="开始日期" end-placeholder="结束日期" unlink-panels clearable /></label>
            </div>
            <p v-if="tab === 'tracking' && invalidTrackingDates" class="production-workbench__filter-error" role="alert">开始日期不能晚于结束日期。</p>
            <div v-if="tab === 'external'" class="production-workbench__external-filters production-workbench__trace-filters" aria-label="外协追溯筛选" title="筛选当前发布包的外协批次，日期包含起止当天">
              <label><input v-model="lineSearch" type="search" placeholder="搜索图号、名称或材质" aria-label="搜索本包物料"></label>
              <label><select v-model="externalSupplier" aria-label="筛选供应商"><option value="">全部供应商</option><option v-for="item in externalSupplierOptions" :key="item.value" :value="item.value">{{ item.label }}</option></select></label>
              <label><select v-model="externalCategory" aria-label="筛选供应商类型"><option value="">全部类型</option><option v-for="item in externalSuppliers.categories" :key="item.id" :value="item.id">{{ item.name }}</option></select></label>
              <label><select v-model="externalStage" aria-label="筛选当前环节"><option value="">全部环节</option><option v-for="item in externalStageOptions" :key="item" :value="item">{{ item }}</option></select></label>
              <label><select v-model="externalDateType" aria-label="筛选日期类型"><option value="required">需求日期</option><option value="promised">预计完成日期</option><option value="published">发布日期</option></select></label>
              <label><el-date-picker v-model="externalDateRange" type="daterange" value-format="YYYY-MM-DD" format="YYYY-MM-DD" aria-label="筛选日期区间" range-separator="-" start-placeholder="开始日期" end-placeholder="结束日期" unlink-panels clearable /></label>
            </div>
            <p v-if="tab === 'external' && invalidExternalDates" class="production-workbench__filter-error" role="alert">开始日期不能晚于结束日期。</p>
            <div class="production-workbench__bulk-tools" :aria-label="tab === 'external' ? '外协批量操作' : '发布包批量操作'">
              <nav class="pdm-tabs"><button type="button" :aria-pressed="lineFilter === 'all'" @click="lineFilter = 'all'">全部 {{ lineRows.length }}</button><button type="button" :aria-pressed="lineFilter === 'open'" @click="lineFilter = 'open'">未完成 {{ currentOpenCount }}</button><button type="button" :aria-pressed="lineFilter === 'urgent'" @click="lineFilter = 'urgent'">紧急 {{ lineRows.filter(item => item.line.priority === 'Urgent').length }}</button></nav>
              <label v-if="tab === 'tracking'" class="production-workbench__quick-filter"><input v-model="trackingOnlyUnassigned" type="checkbox">仅未分配</label><label class="production-workbench__quick-filter"><input v-model="currentOnlyOpen" type="checkbox">仅未完工</label>
              <span v-if="allCategories">筛选 {{ visibleLineRows.length }} 项</span>
              <template v-if="!allCategories">
              <span>已选 {{ checkedLines.length }} 项</span>
              </template>
              <button v-if="tab === 'tracking'" type="button" class="pdm-secondary-action" @click="resetTrackingFilters">重置筛选</button>
              <button v-if="tab === 'external'" type="button" class="pdm-secondary-action" @click="resetExternalFilters">重置筛选</button>
              <template v-if="!allCategories">
              <button type="button" class="pdm-secondary-action" :disabled="!checkedLines.length || pending" @click="checkedLineIds = []">清空选择</button>
              <template v-if="canReadDrawings"><button type="button" class="pdm-secondary-action" :disabled="!checkedDrawingLines.length || pending || downloading" :aria-busy="downloading" @click="downloadCheckedDrawings('Pdf')">批量下载 PDF</button><button type="button" class="pdm-secondary-action" :disabled="!checkedDrawingLines.length || pending || downloading" :aria-busy="downloading" @click="downloadCheckedDrawings('Step')">批量下载 STEP</button></template>
              <template v-if="tab === 'tracking' && canReadDrawings"><button type="button" class="pdm-secondary-action" :disabled="!checkedDrawingLines.length || pending || downloading" :aria-busy="downloading" @click="downloadCheckedDrawings('Bom')">批量下载 BOM</button><button type="button" class="pdm-secondary-action" :disabled="pending || downloading" :aria-busy="downloading" title="同时下载本包 STEP、PDF 和 BOM，不受勾选及列表筛选影响" @click="downloadCheckedDrawings('Package')">下载本包</button></template>
              <button v-if="tab === 'tracking' && canAdjustDelivery" type="button" class="pdm-secondary-action" :disabled="!checkedLines.length || pending" @click="batchMode = 'delivery'">批量调整</button>
              <button v-if="tab === 'tracking' && canAssign" type="button" class="pdm-secondary-action" :disabled="!checkedLines.length || pending || !state.receivedAt" @click="batchMode = 'assign'">批量分配</button>
              </template>
            </div>
            <p v-if="downloadStatus" class="production-workbench__download-status" role="status">{{ downloadStatus }}</p>
            <div class="production-workbench__table-wrap"><table class="pdm-fit-columns">
              <colgroup><col v-for="(weight, index) in detailColumnWeights" :key="index" :style="{ width: `${weight / 23 * 100}%` }"></colgroup>
              <thead><tr><th><template v-if="allCategories">查看</template><input v-else type="checkbox" aria-label="全选当前筛选物料（跨页）" :checked="allVisibleChecked" :indeterminate="someVisibleChecked" :disabled="!visibleLines.length || pending || tab === 'external' && !canReadDrawings" @change="toggleVisibleLines"></th><th>等级</th><th>图号／名称</th><th>版次</th><th>材质</th><th>表面处理</th><th>热处理</th><th>合格／需求</th><th>已领</th><th>当前环节</th><th>需求日期</th><th>预计完成</th><th>图纸</th></tr></thead>
              <tbody><tr v-for="{ source, line, key } in pagedLineRows" :key="key" :class="{ selected: !allCategories && selectedLineId === line.id }" @click="allCategories ? openOverviewLine(source, line) : selectedLineId = line.id">
                <td><button v-if="allCategories" type="button" class="production-workbench__open-package" :aria-label="`打开发布包 ${source.packageNumber} 的物料 ${line.drawingNumber}`" @click.stop="openOverviewLine(source, line)">查看</button><input v-else v-model="checkedLineIds" type="checkbox" :value="line.id" :aria-label="`选择物料 ${line.drawingNumber} ${line.revision}`" :disabled="pending || tab === 'external' && !canReadDrawings" @click.stop></td>
                <td><span class="production-workbench__priority" :class="priorityClass(line.priority)">{{ productionPriority(line.priority) }}</span></td>
                <td :title="`${line.drawingNumber || '未填写型号'} · ${line.name} · 物料编码：${line.materialCode || '—'}${allCategories ? ` · ${source.projectCode} · ${source.projectName} · ${source.packageNumber}` : ''}`"><strong v-stocked-material="line.materialCode">{{ line.drawingNumber || '—' }}</strong><small v-stocked-material="line.materialCode">{{ line.name }}</small><small v-if="allCategories">{{ source.projectCode }} · {{ source.packageNumber }}</small></td><td>{{ line.displayRevision || line.revision || '—' }}</td>
                <td :title="line.material || ''">{{ line.material || '—' }}</td><td :title="line.surfaceTreatment || ''">{{ line.surfaceTreatment || '—' }}</td><td :title="line.heatTreatment || ''">{{ line.heatTreatment || '—' }}</td>
                <td>{{ lineMetric(line, 'Pass', source) }} / {{ line.requiredQuantity }}</td><td>{{ lineMetric(line, 'Issue', source) }}</td><td><button v-if="lineState(source, line) === '变更待处理'" type="button" class="production-workbench__revision-link" :aria-label="`处理图纸变更 ${line.drawingNumber}`" title="点击查看冻结原因并处理变更" @click.stop="allCategories ? openOverviewLine(source, line).then(() => openLineRevision(line)) : openLineRevision(line)">变更待处理</button><template v-else>{{ lineState(source, line) }}</template></td>
                <td :title="dateLabel(line.requiredOn)">{{ dateLabel(line.requiredOn) }}</td><td :class="{ 'production-workbench__late-date': isLateDate(lineForecastOn(line, source), line.requiredOn) }">{{ dateLabel(lineForecastOn(line, source)) }}</td>
                <td class="production-workbench__drawing-links" @click.stop><button type="button" :aria-label="`预览图纸 ${line.drawingNumber}`" :disabled="!canReadDrawings" :title="canReadDrawings ? undefined : '需要查看项目内容权限'" @click="showLineDrawing(line, 'preview', source)">预览</button><button type="button" :aria-label="`下载图纸 ${line.drawingNumber}`" :disabled="!canReadDrawings" :title="canReadDrawings ? undefined : '需要查看项目内容权限'" @click="showLineDrawing(line, 'download', source)">下载</button></td>
              </tr></tbody>
            </table><p v-if="!visibleLines.length" class="production-workbench__empty">当前筛选下没有物料</p></div>
            <footer><span>{{ allCategories ? `全部分类 ${lineRows.length} 项；点击物料进入所属发布包办理。` : `本次发布 ${state.lines.length} 项；点击物料查看批次、质量记录和领料进度。` }}</span><nav aria-label="物料分页"><button type="button" :disabled="linePage <= 1" @click="linePage--">上一页</button><span>{{ linePage }}／{{ linePageCount }}</span><button type="button" :disabled="linePage >= linePageCount" @click="linePage++">下一页</button></nav></footer>
          </template><p v-else class="production-workbench__empty">{{ allCategories ? pending ? '正在读取全部项目与发布包…' : error || '暂无可显示的物料，请刷新后重试。' : '请选择左侧发布包' }}</p>
        </main>
        <aside class="production-workbench__right" aria-label="生产详细状态与发布包汇总">
          <section class="pdm-panel production-workbench__detail">
            <template v-if="selectedLine && state"><div class="production-workbench__detail-heading"><div><header><h2>{{ tab === 'external' ? '外协物料 · 详细状态' : '选中物料 · 详细状态' }}</h2><span class="production-workbench__priority" :class="priorityClass(selectedLine.priority)">{{ productionPriority(selectedLine.priority) }}</span></header><h3 v-stocked-material="selectedLine.materialCode">{{ selectedLine.name }}</h3><p>{{ selectedLine.drawingNumber }} · {{ selectedLine.displayRevision || selectedLine.revision || '无版次' }} · {{ lineState(state, selectedLine) }}</p></div><ProductionDrawingQr v-if="canReadDrawings" :token="token" :state="state" :line="selectedLine" /></div><dl><dt>需求数量</dt><dd>{{ selectedLine.requiredQuantity }} {{ selectedLine.unit }}</dd><dt>质检合格</dt><dd>{{ lineMetric(selectedLine, 'Pass') }} {{ selectedLine.unit }}</dd><dt>已领用</dt><dd>{{ lineMetric(selectedLine, 'Issue') }} {{ selectedLine.unit }}</dd><dt>需求日期</dt><dd>{{ dateLabel(selectedLine.requiredOn) }}</dd><dt>材质</dt><dd>{{ selectedLine.material || '—' }}</dd><dt>表面处理</dt><dd>{{ selectedLine.surfaceTreatment || '—' }}</dd><dt>热处理</dt><dd>{{ selectedLine.heatTreatment || '—' }}</dd></dl>
              <details v-if="tab === 'tracking' && canAdjustDelivery" class="production-workbench__operation"><summary>调整紧急程度／需求日期</summary><label>紧急程度<select v-model="deliveryPriority"><option value="Normal">普通</option><option value="Priority">加急</option><option value="Urgent">紧急</option></select></label><label>需求日期<input v-model="deliveryRequiredOn" type="date"></label><label>调整原因<input v-model="deliveryReason" placeholder="填写协调原因"></label><button type="button" class="pdm-secondary-action" :disabled="pending" @click="saveDelivery">保存调整</button></details>
              <h4>生产批次</h4><div class="production-workbench__lots"><button v-for="lot in lineLots" :key="lot.id" type="button" :class="{ selected: selectedLotId === lot.id }" @click="selectedLotId = lot.id"><strong>{{ routeLabel(lot.route) }} · {{ lot.quantity }} {{ selectedLine.unit }}</strong><small>{{ lot.route === 'External' ? lot.supplier : lot.assignee || '待排产' }} · 预计 {{ dateLabel(lot.promisedOn) }}</small></button><p v-if="!lineLots.length">供应链尚未分配生产批次</p></div>
              <dl v-if="tab === 'external' && selectedLot?.route === 'External'" aria-label="外协批次信息"><dt>供应商</dt><dd>{{ selectedLot.supplier || '—' }}</dd><dt>外协批次数量</dt><dd>{{ selectedLot.quantity }}</dd><dt>负责人</dt><dd>{{ selectedLot.assignee || '—' }}</dd><dt>承诺交期</dt><dd :class="{ 'production-workbench__late-date': isLateDate(selectedLot.promisedOn, selectedLine.requiredOn) }">{{ dateLabel(selectedLot.promisedOn) }}</dd><dt>外协已交货</dt><dd>{{ movementTotal(state, selectedLot.id, 'ExternalDelivered') }}</dd></dl>
              <template v-if="tab === 'tracking' && canAssign && state.receivedAt && (selectedLot || checkedLines.length)"><button type="button" class="pdm-secondary-action production-workbench__allocation-edit" :disabled="pending || !checkedLines.length && Boolean(allocationBlocked)" :title="!checkedLines.length && allocationBlocked || undefined" @click="checkedLines.length ? batchMode = 'assign' : showAllocationEdit = true">{{ checkedLines.length ? '批量修改所选分配' : '修改选中批次分配' }}</button><small v-if="!checkedLines.length && allocationBlocked" class="production-workbench__allocation-blocked">{{ allocationBlocked }}</small></template>
              <template v-if="tab === 'tracking' && canAssign && state.receivedAt"><details class="production-workbench__operation"><summary>新增分配自制／外协</summary><p>未分配数量 {{ unassigned }}；已有批次请使用“修改选中批次分配”。</p><label>方式<select v-model="assignRoute"><option value="Self">自制</option><option value="External">外协</option></select></label><label>本次数量<input v-quantity v-model.number="assignQuantity" type="number" min="0.001" step="any"></label><ProductionSupplierPicker v-if="assignRoute === 'External'" v-model="assignSupplierId" :token="token" :disabled="pending" @selected="assignSupplier = $event?.name ?? ''" /><label>负责人<input v-model="assignAssignee" placeholder="填写负责人"></label><label>承诺日期<input v-model="assignPromisedOn" type="date"></label><button type="button" class="pdm-primary-action" :disabled="pending || unassigned <= 0" @click="assign">确认新增分配</button></details></template>
              <template v-if="selectedLot"><ProductionRevisionDisposition v-if="selectedLot.frozen" ref="revisionPanel" :key="selectedLot.id" :state="state" :lot="selectedLot" :unit="selectedLine.unit" :can-resolve="canAssign || canSchedule && selectedLot.route === 'Self'" :pending="pending" @resolve="resolveRevision" /><details v-if="canAssign && selectedLot.route === 'External'" class="production-workbench__operation"><summary>更新外协承诺交期</summary><label>承诺日期<input v-model="promiseOn" type="date"></label><label>调整原因<input v-model="promiseReason" placeholder="填写供应商反馈或调整原因"></label><button type="button" class="pdm-secondary-action" :disabled="pending" @click="savePromise">保存承诺日期</button></details><details v-if="tab === 'tracking'" class="production-workbench__operation"><summary>登记本次流转／操作记录</summary><ProductionOperationPanel v-if="record" :token="token" :username="username" :record="record" :line-id="selectedLine.id" @updated="record = $event" /></details><details v-else class="production-workbench__operation"><summary>完整外协流转记录</summary><ol class="production-workbench__events"><li v-for="item in state.movements.filter(item => item.lotId === selectedLot?.id).slice().reverse()" :key="item.id"><time>{{ new Date(item.occurredAt).toLocaleString('zh-CN', { hour12: false }) }}</time><span>{{ item.operationName || movementLabels[item.kind] || '流程办理' }} {{ item.quantity }} · {{ item.actorName || item.actor }}</span><small v-if="item.workstationName">{{ item.workstationName }}</small><small v-if="item.fromStage || item.toStage">{{ item.fromStage }} → {{ item.toStage }}</small><small v-if="item.note">{{ item.note }}</small></li></ol><p v-if="!state.movements.some(item => item.lotId === selectedLot?.id)">暂无流转记录</p></details><button v-if="canSchedule && selectedLot.route === 'Self' && !selectedLot.frozen" type="button" class="pdm-secondary-action" @click="chooseScheduleLot(selectedLot)">安排自制生产</button></template>
              <button v-if="selectedLot" type="button" class="pdm-secondary-action" @click="openLotCard(selectedLot)">查看／打印批次流转卡</button>
              <h4>最近记录</h4><ol class="production-workbench__events"><li v-for="item in recentMovements" :key="item.id"><time>{{ item.occurredAt.slice(5, 16).replace('T', ' ') }}</time><span>{{ item.operationName || movementLabels[item.kind] || '流程办理' }} {{ item.quantity }} · {{ item.actor }}</span><small v-if="item.kind === 'AllocationChanged'">{{ item.note }}</small></li><li v-if="!recentMovements.length">暂无流转记录</li></ol>
            </template><p v-else class="production-workbench__empty">点击物料查看详细状态</p>
          </section>
          <section class="pdm-panel production-workbench__summary"><h2>{{ allCategories ? '全部分类汇总' : '本发布包汇总' }}</h2><template v-if="state"><p>质检齐套 <strong>{{ qualifiedCount }}/{{ lineRows.length }} 项</strong></p><div class="production-workbench__bar"><span :style="{ width: `${progress(qualifiedCount, lineRows.length)}%` }" /></div><p>领用交付 <strong>{{ issuedCount }}/{{ lineRows.length }} 项</strong></p><p>未领用 {{ currentOpenCount }} 项 · 紧急 {{ lineRows.filter(item => item.line.priority === 'Urgent').length }} 项</p></template><p v-else>请选择发布包</p></section>
        </aside>
        </template>
        <template v-else>
          <main class="production-workbench__lines pdm-panel" aria-label="自制批次明细">
            <template v-if="state">
              <header v-if="allCategories"><div><h2>全部分类</h2><p>所有项目与发布包 · {{ allRecords.length }} 个发布包 · {{ selfLots.length }} 个自制批次</p></div></header>
              <header v-else><div><h2>{{ state.packageNumber }} <span class="production-workbench__state">自制追溯</span></h2><p>{{ state.projectCode }} · {{ state.projectName }} · 发布 {{ dateLabel(state.publishedAt) }}</p></div></header>

              <div class="production-workbench__trace-filters" aria-label="自制追溯筛选" title="筛选当前发布包的自制批次，日期包含起止当天">
                <label><input v-model="lineSearch" type="search" :placeholder="allCategories ? '搜索项目、发布包、图号或名称' : '搜索图号、名称或材质'" aria-label="搜索自制批次"></label>
                <label><select v-model="selfResource" aria-label="筛选资源组"><option value="">全部资源组</option><option value="unassigned">未分配资源组</option><option v-for="item in selfResourceOptions" :key="item" :value="item">{{ item }}</option></select></label>
                <label><select v-model="selfAssignee" aria-label="筛选负责人"><option value="">全部负责人</option><option value="unassigned">未指定负责人</option><option v-for="item in selfAssigneeOptions" :key="item" :value="item">{{ item }}</option></select></label>
                <label><select v-model="selfStage" aria-label="筛选当前环节"><option value="">全部环节</option><option v-for="item in selfStageOptions" :key="item" :value="item">{{ item }}</option></select></label>
                <label><select v-model="selfDateType" aria-label="筛选日期类型"><option value="required">需求日期</option><option value="promised">预计完成日期</option><option value="published">发布日期</option></select></label>
                <label><el-date-picker v-model="selfDateRange" type="daterange" value-format="YYYY-MM-DD" format="YYYY-MM-DD" aria-label="筛选日期区间" range-separator="-" start-placeholder="开始日期" end-placeholder="结束日期" unlink-panels clearable /></label>
              </div>
              <p v-if="invalidSelfDates" class="production-workbench__filter-error" role="alert">开始日期不能晚于结束日期。</p>
              <div class="production-workbench__bulk-tools" aria-label="排程批量操作">
                <nav class="pdm-tabs" aria-label="排程状态筛选"><button type="button" :aria-pressed="scheduleFilter === 'all'" @click="scheduleFilter = 'all'">全部 {{ selfLots.length }}</button><button type="button" :aria-pressed="scheduleFilter === 'unplanned'" @click="scheduleFilter = 'unplanned'">待排产 {{ selfLots.length - plannedLots }}</button><button type="button" :aria-pressed="scheduleFilter === 'planned'" @click="scheduleFilter = 'planned'">已排产 {{ plannedLots }}</button></nav>
                <label class="production-workbench__quick-filter"><input v-model="currentOnlyOpen" type="checkbox">仅未完工</label>
                <span v-if="allCategories">筛选 {{ visibleScheduleLots.length }} 个批次</span><span v-else>已选 {{ checkedSelfLots.length }} 个批次</span>
                <button type="button" class="pdm-secondary-action" @click="resetSelfFilters">重置筛选</button>
                <template v-if="!allCategories"><button type="button" class="pdm-secondary-action" :disabled="!checkedSelfLots.length" @click="checkedScheduleIds = []">清空选择</button><template v-if="canReadDrawings"><button type="button" class="pdm-secondary-action" :disabled="!checkedDrawingLines.length || pending || downloading" :aria-busy="downloading" @click="downloadCheckedDrawings('Pdf')">批量下载 PDF</button><button type="button" class="pdm-secondary-action" :disabled="!checkedDrawingLines.length || pending || downloading" :aria-busy="downloading" @click="downloadCheckedDrawings('Step')">批量下载 STEP</button></template><button v-if="canSchedule" type="button" class="pdm-secondary-action" :disabled="!checkedScheduleLots.length || checkedScheduleLots.length !== checkedSelfLots.length || pending" :title="checkedScheduleLots.length !== checkedSelfLots.length ? '所选批次包含尚未签收或已冻结的批次，请调整勾选后排程' : undefined" @click="showScheduleBatch = true">批量排程</button></template>
              </div>
              <p v-if="downloadStatus" class="production-workbench__download-status" role="status">{{ downloadStatus }}</p>
              <div class="production-workbench__table-wrap"><table class="pdm-fit-columns">
                <colgroup><col v-for="(weight, index) in detailColumnWeights" :key="index" :style="{ width: `${weight / 23 * 100}%` }"></colgroup>
                <thead><tr><th><template v-if="allCategories">查看</template><input v-else type="checkbox" aria-label="全选当前筛选自制批次（跨页）" :checked="allScheduleChecked" :indeterminate="someScheduleChecked" :disabled="!selectableScheduleLots.length || pending" @change="toggleScheduleLots"></th><th>等级</th><th>图号／名称</th><th>版次</th><th>材质</th><th>表面处理</th><th>热处理</th><th>合格／需求</th><th>已领</th><th>当前环节</th><th>需求日期</th><th>预计完成</th><th>图纸</th></tr></thead>
                <tbody><tr v-for="lot in pagedScheduleLots" :key="lot.key" :class="{ selected: !allCategories && selectedLotId === lot.id }" @click="allCategories ? openOverviewLot(lot.source, lot) : chooseScheduleLot(lot)">
                  <td><button v-if="allCategories" type="button" class="production-workbench__open-package" :aria-label="`打开发布包 ${lot.source.packageNumber} 的自制批次 ${lot.line?.drawingNumber} ${lot.id}`" @click.stop="openOverviewLot(lot.source, lot)">查看</button><input v-else v-model="checkedScheduleIds" type="checkbox" :value="lot.id" :aria-label="`勾选排程批次 ${lot.line?.drawingNumber} ${lot.id}`" :disabled="pending || !canReadDrawings && (!canSchedule || !mayScheduleLot(lot))" :title="canReadDrawings ? '勾选后可批量下载本包正式图纸' : lot.frozen ? '图纸变更冻结的批次不能排程' : !mayScheduleLot(lot) ? '请先由生产岗位扫码签收' : undefined" @click.stop></td>
                  <td><span class="production-workbench__priority" :class="priorityClass(lot.line?.priority ?? 'Normal')">{{ productionPriority(lot.line?.priority ?? 'Normal') }}</span></td>
                  <td :title="`${lot.line?.drawingNumber || '未填写型号'} · ${lot.line?.name} · 物料编码：${lot.line?.materialCode || '—'}${allCategories ? ` · ${lot.source.projectCode} · ${lot.source.projectName} · ${lot.source.packageNumber}` : ''}`"><button type="button" v-stocked-material="lot.line?.materialCode" class="production-workbench__schedule-select" :aria-label="`选择排程批次 ${lot.line?.drawingNumber} ${lot.id}`" :aria-pressed="!allCategories && selectedLotId === lot.id" @click.stop="allCategories ? openOverviewLot(lot.source, lot) : chooseScheduleLot(lot)">{{ lot.line?.drawingNumber || '—' }}</button><small v-stocked-material="lot.line?.materialCode">{{ lot.line?.name }}</small><small v-if="allCategories">{{ lot.source.projectCode }} · {{ lot.source.packageNumber }}</small></td>
                  <td>{{ lot.line?.displayRevision || lot.line?.revision || '—' }}</td><td :title="lot.line?.material || ''">{{ lot.line?.material || '—' }}</td><td :title="lot.line?.surfaceTreatment || ''">{{ lot.line?.surfaceTreatment || '—' }}</td><td :title="lot.line?.heatTreatment || ''">{{ lot.line?.heatTreatment || '—' }}</td><td>{{ lot.line ? lineMetric(lot.line, 'Pass', lot.source) : 0 }} / {{ lot.line?.requiredQuantity ?? 0 }}</td><td>{{ lot.line ? lineMetric(lot.line, 'Issue', lot.source) : 0 }}</td><td><button v-if="lot.frozen" type="button" class="production-workbench__revision-link" :aria-label="`处理图纸变更 ${lot.line?.drawingNumber} ${lot.id}`" title="点击在右侧处理变更" @click.stop="allCategories ? openOverviewLot(lot.source, lot).then(() => openRevision(lot)) : openRevision(lot)">变更待处理</button><template v-else>{{ lotStage(lot.source, lot) }}</template></td><td :title="dateLabel(lot.line?.requiredOn)">{{ dateLabel(lot.line?.requiredOn) }}</td><td :class="{ 'production-workbench__late-date': isLateDate(lot.promisedOn, lot.line?.requiredOn) }">{{ dateLabel(lot.promisedOn) }}</td>
                  <td class="production-workbench__drawing-links" @click.stop><button type="button" :aria-label="`预览图纸 ${lot.line?.drawingNumber}`" :disabled="!canReadDrawings" :title="canReadDrawings ? undefined : '需要查看项目内容权限'" @click="showLineDrawing(lot.line, 'preview', lot.source)">预览</button><button type="button" :aria-label="`下载图纸 ${lot.line?.drawingNumber}`" :disabled="!canReadDrawings" :title="canReadDrawings ? undefined : '需要查看项目内容权限'" @click="showLineDrawing(lot.line, 'download', lot.source)">下载</button></td>
                </tr></tbody>
              </table><p v-if="!visibleScheduleLots.length" class="production-workbench__empty">{{ selfLots.length ? '当前筛选下没有自制批次' : '当前发布包暂无自制批次，请先由供应链分配。' }}</p></div>
              <footer><span>共 {{ visibleScheduleLots.length }} 个自制批次；{{ allCategories ? '点击批次进入所属发布包办理。' : '选择批次后在右侧安排生产。' }}</span><nav aria-label="自制批次分页"><button type="button" :disabled="schedulePage <= 1" @click="schedulePage--">上一页</button><span>{{ schedulePage }}／{{ schedulePageCount }}</span><button type="button" :disabled="schedulePage >= schedulePageCount" @click="schedulePage++">下一页</button></nav></footer>
            </template><p v-else class="production-workbench__empty">{{ allCategories ? pending ? '正在读取全部项目与发布包…' : error || '暂无可显示的自制批次，请刷新后重试。' : '请选择左侧发布包' }}</p>
          </main>
          <aside class="production-workbench__right production-workbench__schedule-right" aria-label="排程安排">
            <section class="pdm-panel production-workbench__detail">
              <div class="production-workbench__detail-heading"><div><header><h2>排程安排</h2><span v-if="selectedLine && selectedLot?.route === 'Self'" class="production-workbench__priority" :class="priorityClass(selectedLine.priority)">{{ productionPriority(selectedLine.priority) }}</span></header>
                <template v-if="selectedLot?.route === 'Self' && selectedLine"><h3 v-stocked-material="selectedLine.materialCode">{{ selectedLine.name }}</h3><p>{{ selectedLine.drawingNumber }} · {{ selectedLine.displayRevision || selectedLine.revision || '无版次' }}</p></template></div>
                <ProductionDrawingQr v-if="canReadDrawings && state && selectedLot?.route === 'Self' && selectedLine" :token="token" :state="state" :line="selectedLine" />
              </div>
              <template v-if="selectedLot?.route === 'Self' && selectedLine">
                <dl><dt>批次数量</dt><dd>{{ selectedLot.quantity }}</dd><dt>排程状态</dt><dd>{{ selectedLot.frozen ? '变更待处理' : selectedLot.schedule.length ? '已排产' : '待排产' }}</dd><dt>已排工时</dt><dd>{{ Number(selectedLot.schedule.reduce((total, slot) => total + slot.hours, 0).toFixed(2)) }} 小时</dd><dt>需求日期</dt><dd>{{ dateLabel(selectedLine.requiredOn) }}</dd><dt>负责人</dt><dd>{{ selectedLot.assignee || '—' }}</dd><dt>材质</dt><dd>{{ selectedLine.material || '—' }}</dd><dt>表面处理</dt><dd>{{ selectedLine.surfaceTreatment || '—' }}</dd><dt>热处理</dt><dd>{{ selectedLine.heatTreatment || '—' }}</dd></dl>
                <ProductionRevisionDisposition v-if="selectedLot.frozen && state" ref="revisionPanel" :key="selectedLot.id" :state="state" :lot="selectedLot" :unit="selectedLine.unit" :can-resolve="canAssign || canSchedule" :pending="pending" @resolve="resolveRevision" />
                <p v-if="!mayScheduleLot(selectedLot) && !selectedLot.frozen">请先由生产岗位扫码签收，再安排排程。</p><form class="production-workbench__schedule-form" @submit.prevent="saveSchedule"><fieldset :disabled="pending || !canSchedule || !mayScheduleLot(selectedLot)">
                  <div v-for="(slot, index) in scheduleSlots" :key="index" class="production-workbench__slot">
                    <label>资源组<select v-model="slot.resourceGroupId" aria-label="选择资源组"><option v-for="name in resourceNames" :key="name">{{ name }}</option></select></label>
                    <label>排产日期<input v-model="slot.date" type="date" aria-label="排产日期" required></label><label>预计工时（小时）<input v-model.number="slot.hours" type="number" min="0.1" step="0.1" aria-label="预计工时" required></label>
                    <button type="button" class="pdm-secondary-action" :aria-label="`删除第${index + 1}条排程`" @click="scheduleSlots.splice(index, 1)">删除</button>
                  </div>
                  <button type="button" class="pdm-secondary-action" @click="scheduleSlots.push({ resourceGroupId: resourceNames[0] || 'CNC组', date: availableDate, hours: 1 })">增加日期／资源</button>
                  <label class="production-workbench__field">预计完成日期<input v-model="schedulePromisedOn" type="date" required></label><label v-if="selectedLot.schedule.length" class="production-workbench__field">调整原因<input v-model="scheduleReason" placeholder="填写排程调整原因" required></label>
                  <button v-if="canSchedule" type="submit" class="pdm-primary-action" :disabled="!scheduleSlots.length">确认排产</button>
                </fieldset></form><p>{{ canSchedule ? '确认后占用计划工时，并核对该资源组全部项目的已排工时。' : '当前账号没有排程权限，仅可查看安排。' }}</p>
                <details :key="selectedLot.id" class="production-workbench__operation production-workbench__history" aria-label="完整操作记录"><summary>完整操作记录（{{ selectedLotMovements.length }}）</summary>
                  <ol class="production-workbench__events"><li v-for="item in selectedLotMovements" :key="item.id">
                    <strong>{{ item.operationName || movementLabels[item.kind] || '流程办理' }} · {{ item.quantity }} {{ selectedLine.unit }}</strong>
                    <time>{{ new Date(item.occurredAt).toLocaleString('zh-CN', { hour12: false }) }}</time>
                    <span>{{ item.actorName || item.actor }}（{{ item.actor }}）</span><small v-if="item.actorRoles">操作角色：{{ item.actorRoles }}</small>
                    <span v-if="item.workstationName">{{ item.workstationName }}{{ item.resourceGroup ? ` · ${item.resourceGroup}` : '' }}</span>
                    <span v-if="item.fromStage || item.toStage">{{ item.fromStage }} → {{ item.toStage }}</span><span v-if="item.recipient">实际领用人：{{ item.recipient }}</span>
                    <small v-if="item.note">{{ item.note }}</small><small>记录编号：{{ item.id }}</small>
                  </li></ol><p v-if="!selectedLotMovements.length">暂无操作记录</p>
                </details>
              </template><p v-else class="production-workbench__empty">选择中间自制批次，查看或安排排程。</p>
            </section>
          <section class="pdm-panel production-workbench__summary"><h2>{{ allCategories ? '全部分类汇总' : '本发布包汇总' }}</h2><template v-if="state"><p>质检齐套 <strong>{{ qualifiedCount }}/{{ lineRows.length }} 项</strong></p><div class="production-workbench__bar"><span :style="{ width: `${progress(qualifiedCount, lineRows.length)}%` }" /></div><p>领用交付 <strong>{{ issuedCount }}/{{ lineRows.length }} 项</strong></p><p>未领用 {{ currentOpenCount }} 项 · 紧急 {{ lineRows.filter(item => item.line.priority === 'Urgent').length }} 项</p></template><p v-else>请选择发布包</p></section>
          </aside>
        </template>
      </div>
    </template>
    <section v-else-if="tab === 'capacity'" class="production-workbench__secondary pdm-panel"><header><h2>内部资源组产能</h2><span>计划负荷由每日可用工时及已确认排程计算。</span><button type="button" class="pdm-secondary-action production-workbench__refresh" :disabled="pending" @click="refresh">刷新</button></header><form v-if="canManageCapacity && capacity" class="production-workbench__capacity-form" @submit.prevent="saveCapacity"><label>资源组<input v-model="resourceGroup" list="production-resource-groups"><datalist id="production-resource-groups"><option v-for="name in resourceNames" :key="name" :value="name" /></datalist></label><label>日期<input v-model="availableDate" type="date" required></label><label>当天可用工时<input v-model.number="availableHours" type="number" min="0" max="1000" step="0.5"></label><button type="submit" class="pdm-primary-action" :disabled="pending">保存可用工时</button></form><div class="production-workbench__table-wrap"><table><thead><tr><th>资源组</th><th>日期</th><th>可用工时</th><th>已排工时</th><th>计划负荷</th></tr></thead><tbody><tr v-for="item in capacity?.usage ?? []" :key="`${item.resourceGroupId}-${item.date}`"><td>{{ item.resourceGroupId }}</td><td>{{ item.date }}</td><td>{{ item.availableHours }} 小时</td><td>{{ item.committedHours }} 小时</td><td :class="{ 'production-workbench__danger': item.committedHours > item.availableHours }">{{ item.availableHours > 0 ? `${progress(item.committedHours, item.availableHours)}%` : '未开放产能' }}</td></tr></tbody></table><p v-if="!capacity?.usage.length" class="production-workbench__empty">尚未维护资源组可用工时；请先由生产负责人设置。</p></div></section>
    <section v-else-if="tab === 'execution'" class="production-workbench__secondary pdm-panel"><header><h2>生产现场扫码执行</h2><span>扫描图纸或批次流转卡后，确认本次操作与数量。</span></header>
      <div class="production-workbench__execution-actions">
        <form @submit.prevent="locateScan"><input v-model="scanCode" aria-label="扫描正式图纸或批次流转卡" placeholder="扫描图纸或批次码"><button type="submit" class="pdm-secondary-action">定位</button></form>
        <a :href="scanPageHref" target="_blank" rel="noopener" class="pdm-secondary-action">移动扫码页</a>
        <button type="button" class="pdm-secondary-action" :disabled="pending" @click="refresh">刷新</button>
      </div>
      <div v-if="scanCandidates.length > 1" class="production-workbench__scan-choices" role="dialog" aria-label="选择生产批次"><strong>请选择对应的发布包和批次</strong><button v-for="item in scanCandidates" :key="`${item.releasePackageId}-${item.lineId}-${item.lotId}`" type="button" @click="chooseScan(item)">{{ item.packageNumber }} · {{ item.drawingNumber }} · {{ item.name }} · {{ item.revision }}</button><button type="button" @click="scanCandidates = []">取消</button></div>
      <div v-if="record && selectedLine" class="production-workbench__execution"><h3>{{ state?.packageNumber }} · {{ selectedLine.name }}</h3><ProductionOperationPanel :token="token" :username="username" :record="record" :line-id="selectedLine.id" :preferred-lot-id="executionLotId || undefined" @updated="record = $event" /></div><p v-else class="production-workbench__empty">请扫描图纸或批次流转卡，定位生产任务。</p></section>
    <ProductionSupplierManager v-else-if="tab === 'suppliers' && canReadSuppliers" :token="token" :can-manage="canManageSuppliers" />
    <div v-else class="production-workbench__drawing"><ProductionDrawingCenter :token="token" :username="username" :can-manage="can('production.drawing.manage')" /></div>
    <div v-if="cardUrl && cardLot && state" class="production-workbench__card-backdrop" role="dialog" aria-modal="true" aria-label="生产批次流转卡" @click.self="closeLotCard"><div class="production-workbench__card pdm-panel"><header><h2>生产批次流转卡</h2><button type="button" aria-label="关闭流转卡" @click="closeLotCard">×</button></header><strong>{{ state.packageNumber }} · {{ state.lines.find(line => line.id === cardLot?.lineId)?.name }}</strong><p>{{ state.projectCode }} · {{ routeLabel(cardLot.route) }} · {{ cardLot.quantity }} 件</p><img :src="cardUrl" alt="生产批次二维码"><small>扫描后定位该批次，并确认本次操作与数量。</small><div><button type="button" class="pdm-primary-action" @click="printLotCard">打印流转卡</button><button type="button" class="pdm-secondary-action" @click="closeLotCard">关闭</button></div></div></div>
    <ProductionBatchDialog v-if="batchMode && record" :token="token" :mode="batchMode" :record="record" :lines="checkedLines" @close="batchMode = null" @updated="batchUpdated" @completed="batchCompleted" />
    <ProductionAllocationDialog v-if="showAllocationEdit && record && selectedLot && canAssign" :token="token" :record="record" :lot="selectedLot" @close="showAllocationEdit = false" @updated="allocationUpdated" />
    <ProductionScheduleBatchDialog v-if="showScheduleBatch && record" :token="token" :record="record" :lots="checkedScheduleLots" :resources="resourceNames" :date="availableDate" @close="showScheduleBatch = false" @updated="scheduleBatchUpdated" @completed="scheduleBatchCompleted" />
    <ProductionLineDrawingDialog v-if="drawingContext" :token="token" :state="drawingContext.state" :line="drawingContext.line" :action="drawingContext.action" @close="drawingContext = undefined" />
  </section>
</template>

<style scoped>
.production-workbench{height:100%;min-height:0;display:flex;flex-direction:column;padding:0;box-sizing:border-box;gap:var(--pdm-container-gap);color:var(--pdm-text);background:var(--pdm-bg)}
.production-workbench h2{font-size:14px;margin:0}.production-workbench h3{font-size:14px;margin:8px 0}.production-workbench h4{font-size:12px;margin:14px 0 7px}.production-workbench p{margin:4px 0;color:var(--pdm-muted)}
.production-workbench input,.production-workbench select{height:30px;min-width:0;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);padding:0 8px;background:var(--pdm-surface);font:inherit;color:inherit;box-sizing:border-box}
.production-workbench button{font:inherit;cursor:pointer}.production-workbench button:disabled{cursor:not-allowed;opacity:.55}
.production-workbench__execution-actions,.production-workbench__tabs,.production-workbench__toolbar,.production-workbench__line-tools,.production-workbench__lines header{display:flex;align-items:center;justify-content:space-between;gap:10px}
.production-workbench__execution-actions form{display:flex;gap:6px}.production-workbench__execution-actions form input{width:240px}.production-workbench__tabs{justify-content:flex-start;border-bottom:1px solid var(--pdm-border);gap:18px}.production-workbench__tabs button{border:0;background:none;min-height:36px;padding:6px 9px;color:var(--pdm-text-soft)}.production-workbench__tabs button[aria-current=page]{color:var(--pdm-blue);font-weight:700;border-bottom:2px solid var(--pdm-blue)}
.production-workbench__toolbar{flex-wrap:wrap;justify-content:flex-start;font-size:12px}.production-workbench__toolbar label{display:flex;align-items:center;gap:7px}.production-workbench__toolbar label input{height:auto}
.production-workbench__grid{flex:1;min-height:0;display:grid;grid-template-columns:320px minmax(0,1fr) 320px;gap:var(--pdm-container-gap)}.production-workbench__tree,.production-workbench__lines,.production-workbench__detail,.production-workbench__summary{padding:10px;min-width:0;box-sizing:border-box}.production-workbench__tree{overflow:auto}.production-workbench__tree-search{display:flex;align-items:center;gap:8px;margin:8px 0}.production-workbench__tree-search>input{flex:1;min-width:0;width:0}.production-workbench__tree-totals{display:flex;flex:0 1 auto;flex-wrap:wrap;justify-content:flex-end;gap:2px 6px;max-width:65%;font-size:12px;color:var(--pdm-muted);font-variant-numeric:tabular-nums}.production-workbench__tree-totals>span{white-space:nowrap}.production-workbench__project,.production-workbench__package{display:block;width:100%;border:0;text-align:left;background:transparent;padding:8px;border-radius:5px}.production-workbench__project{display:flex;align-items:center;gap:5px;border-top:1px solid var(--pdm-border);white-space:nowrap}.production-workbench__project span{overflow:hidden;text-overflow:ellipsis}.production-workbench__project small{margin-left:auto;flex:none}.production-workbench__package{margin:2px 0 2px 8px;width:calc(100% - 8px);border-left:2px solid transparent}.production-workbench__package.selected{background:var(--pdm-blue-soft);border-color:var(--pdm-blue)}.production-workbench__package span{display:flex;align-items:center;justify-content:space-between;gap:4px}.production-workbench__package em{font-style:normal;color:var(--pdm-blue);white-space:nowrap}.production-workbench__package small{display:block;color:var(--pdm-text-soft);margin-top:3px}.production-workbench__package b{color:var(--pdm-danger)}
.production-workbench__lines{display:flex;flex-direction:column;overflow:hidden}.production-workbench__lines header{flex:0 0 auto;min-height:56px}.production-workbench__lines header p{font-size:11px}.production-workbench__state{font-size:12px;color:var(--pdm-blue);margin-left:7px}.production-workbench__line-tools{flex:0 0 auto;padding:8px 0;border-top:1px solid var(--pdm-border)}.production-workbench__line-tools nav{display:flex;gap:3px}.production-workbench__line-tools button{border:0;background:none;padding:5px 7px}.production-workbench__line-tools button[aria-pressed=true]{color:var(--pdm-blue);font-weight:700;border-bottom:2px solid var(--pdm-blue)}.production-workbench__line-tools input{width:190px}
.production-workbench__bulk-tools{display:flex;align-items:center;flex-wrap:wrap;gap:8px;padding:0 0 8px;font-size:12px}.production-workbench__bulk-tools>span{margin-right:auto;color:var(--pdm-muted)}.production-workbench__lines input[type=checkbox]{width:16px;height:16px;margin:0;padding:0;vertical-align:middle}
.production-workbench__table-wrap{flex:1;min-height:0;overflow:auto}.production-workbench__lines table th,.production-workbench__lines table td{text-align:center;vertical-align:middle}.production-workbench__lines td small{overflow:hidden;text-overflow:ellipsis}.production-workbench table{border-collapse:collapse;width:100%;font-size:12px}.production-workbench th{position:sticky;top:0;background:var(--pdm-table-header);text-align:left;z-index:1;white-space:nowrap}.production-workbench td,.production-workbench th{border-bottom:1px solid var(--pdm-border);padding:7px 6px}.production-workbench td{white-space:nowrap}.production-workbench td small{display:block;color:var(--pdm-muted)}.production-workbench tbody tr{cursor:pointer}.production-workbench tbody tr:hover,.production-workbench tbody tr.selected{background:var(--pdm-blue-soft)}.production-workbench__lines footer{padding-top:8px;color:var(--pdm-muted);font-size:11px}
.production-workbench__priority{display:inline-block;font-size:11px;padding:2px 5px;border-radius:4px;background:var(--pdm-surface-muted)}.production-workbench__priority.urgent{background:var(--pdm-danger-soft);color:var(--pdm-danger)}.production-workbench__priority.priority{background:var(--pdm-orange-soft);color:var(--pdm-orange)}
.production-workbench__right{display:flex;flex-direction:column;gap:var(--pdm-container-gap);min-height:0}.production-workbench__detail{flex:1;min-height:0;overflow:auto}.production-workbench__detail header{display:flex;justify-content:space-between}.production-workbench__detail dl{display:grid;grid-template-columns:auto 1fr;gap:5px 10px;font-size:12px}.production-workbench__detail dt{color:var(--pdm-muted)}.production-workbench__detail dd{margin:0;text-align:right}.production-workbench__lots{display:grid;gap:4px}.production-workbench__lots button{border:1px solid var(--pdm-border);background:var(--pdm-surface);text-align:left;padding:7px;border-radius:5px}.production-workbench__lots button.selected{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}.production-workbench__lots small{display:block}.production-workbench__operation{margin:9px 0;border-top:1px solid var(--pdm-border);padding-top:7px}.production-workbench__operation summary{cursor:pointer;font-weight:700}.production-workbench__operation label,.production-workbench__field{display:grid;gap:4px;margin:7px 0;font-size:12px}.production-workbench__operation button{margin-top:4px}.production-workbench__events{padding-left:15px;font-size:11px}.production-workbench__events li{margin:4px 0}.production-workbench__events time{margin-right:6px;color:var(--pdm-muted)}
.production-workbench__summary{flex:0 0 auto;max-height:180px}.production-workbench__summary p{font-size:12px;display:flex;justify-content:space-between}.production-workbench__bar{height:8px;background:var(--pdm-surface-muted);border-radius:8px;overflow:hidden}.production-workbench__bar span{display:block;height:100%;background:var(--pdm-blue)}.production-workbench__empty{padding:24px;text-align:center;color:var(--pdm-muted)}
.production-workbench__secondary{flex:1;min-height:0;overflow:auto;padding:15px}.production-workbench__secondary header{display:flex;align-items:center;flex-wrap:wrap;gap:16px;margin-bottom:15px}.production-workbench__secondary header span{color:var(--pdm-muted)}.production-workbench__field{max-width:220px}.production-workbench__capacity-form{display:flex;gap:12px;align-items:end;margin-bottom:20px}.production-workbench__capacity-form label{display:grid;gap:5px}.production-workbench__danger{color:var(--pdm-danger);font-weight:700}.production-workbench__execution{max-width:700px}.production-workbench__execution-form{display:grid;gap:12px;max-width:360px}.production-workbench__execution-form label{display:grid;gap:5px}.production-workbench__execution-form button{min-height:44px}.production-workbench__drawing{flex:1;min-height:0}.production-workbench__scan-choices{margin-bottom:15px;max-width:100%;box-sizing:border-box;display:grid;gap:6px;padding:15px;width:360px;background:var(--pdm-surface);border:1px solid var(--pdm-border);box-shadow:0 12px 24px var(--pdm-shadow)}.production-workbench__scan-choices button{border:1px solid var(--pdm-border);background:var(--pdm-surface);padding:7px;text-align:left}

.production-workbench__schedule-select{max-width:100%;padding:0;border:0;background:none;color:inherit;font-weight:700!important;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.production-workbench__schedule-form fieldset{min-width:0;padding:0;margin:0;border:0}.production-workbench__slot{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:8px;margin:10px 0;padding:9px;border:1px solid var(--pdm-border);border-radius:5px}.production-workbench__slot label{display:grid;min-width:0;gap:4px;font-size:12px}.production-workbench__slot label:first-child{grid-column:1/-1}.production-workbench__slot input,.production-workbench__slot select{width:100%}.production-workbench__slot>button{grid-column:2;justify-self:end}.production-workbench__schedule-right .production-workbench__field{max-width:none}.production-workbench__schedule-right dd{min-width:0;overflow-wrap:anywhere}.production-workbench__schedule-right p{font-size:12px;line-height:1.5}
.production-workbench__refresh{margin-left:auto}.production-workbench__execution-actions{justify-content:flex-start;flex-wrap:wrap;margin-bottom:15px}.production-workbench__execution-actions>a{display:inline-flex;align-items:center;text-decoration:none;color:var(--pdm-text);white-space:nowrap}
.production-workbench__card-backdrop{position:fixed;inset:0;z-index:2500;display:grid;place-items:center;background:var(--pdm-overlay);padding:15px}.production-workbench__card{width:min(430px,100%);padding:18px;text-align:center}.production-workbench__card header{display:flex;justify-content:space-between;align-items:center}.production-workbench__card header button{border:0;background:none;font-size:24px}.production-workbench__card>strong,.production-workbench__card>small{display:block;margin:12px}.production-workbench__card img{display:block;width:260px;height:260px;margin:12px auto}.production-workbench__card>div:last-child{display:flex;justify-content:center;gap:9px}
.production-workbench{font-size:13px}.production-workbench h2,.production-workbench h3{font-size:15px}.production-workbench table{font-size:13px}.production-workbench td,.production-workbench th{padding:9px 7px}.production-workbench__package small,.production-workbench__lines header p,.production-workbench__detail dl,.production-workbench__summary p{font-size:12px}.production-workbench__tabs button{font-size:13px}
@media(max-width:1100px){.production-workbench__grid{grid-template-columns:190px minmax(0,1fr)}.production-workbench__right{grid-column:1/-1;display:grid;grid-template-columns:2fr 1fr}.production-workbench__detail{max-height:360px}.production-workbench__summary{max-height:none}}@media(max-width:700px){.production-workbench__execution-actions form input{width:175px}.production-workbench__tabs{overflow:auto;flex:0 0 auto}.production-workbench__tabs button{white-space:nowrap}.production-workbench__grid{grid-template-columns:1fr;overflow:auto}.production-workbench__tree{max-height:185px}.production-workbench__lines{min-height:420px}.production-workbench__right{display:flex}.production-workbench__secondary{overflow:auto}.production-workbench__capacity-form{flex-wrap:wrap}}
.production-workbench__allocation-edit{margin-top:8px}.production-workbench__allocation-edit:disabled{cursor:not-allowed}.production-workbench__allocation-blocked{display:block;margin:5px 0;color:var(--pdm-muted)}.production-workbench__events li>small{display:block;overflow-wrap:anywhere;color:var(--pdm-muted)}.production-workbench__drawing-links button:disabled{cursor:not-allowed}.production-workbench__drawing-links button{border:0;padding:2px 4px;background:none;color:var(--pdm-blue);font-size:12px}.production-workbench__lines footer{display:flex;align-items:center;justify-content:space-between;gap:12px}.production-workbench__lines footer nav{display:flex;align-items:center;gap:5px;flex:none}.production-workbench__lines footer button{border:1px solid var(--pdm-border);background:var(--pdm-surface);border-radius:4px;padding:3px 6px}
@media(max-width:1100px){.production-workbench__schedule-right{display:flex}.production-workbench__schedule-right .production-workbench__detail{max-height:none}}@media(max-width:700px){.production-workbench__schedule-right{min-height:420px}.production-workbench__grid--schedule .production-workbench__line-tools{flex-wrap:wrap}.production-workbench__grid--schedule .production-workbench__lines footer{flex-wrap:wrap}}
@media(max-width:1600px){.production-workbench__drawing-links button{display:block;max-width:100%;margin:0 auto;padding:2px 0}}
/* 发布包卡片：编号单行省略，需求日期与领用行、上方状态共用右边缘。 */
.production-workbench__package-header { min-width:0; }
.production-workbench__package-header strong { flex:1; min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.production-workbench__package-header em { flex:none; font-weight:700; }
.production-workbench__package .production-workbench__package-meta { align-items:flex-end; gap:6px; }
.production-workbench__package .production-workbench__package-counts { display:flex; flex:1; min-width:0; flex-wrap:wrap; gap:0 6px; }
.production-workbench__package-counts>span { display:block; max-width:100%; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.production-workbench__package-counts>span+span::before { content:'·'; margin-right:6px; }
.production-workbench__package-required { flex:none; white-space:nowrap; text-align:right; }
.production-workbench__package-alerts b+b { margin-left:6px; }
@media(min-width:701px) and (max-width:1100px) {
  .production-workbench__package .production-workbench__package-meta { display:grid; grid-template-columns:minmax(0,1fr) auto; align-items:baseline; row-gap:3px; }
  .production-workbench__package .production-workbench__package-counts { display:contents; font-size:var(--pdm-font-size-caption); }
  .production-workbench__package-counts>span:first-child { grid-column:1/-1; }
  .production-workbench__package-counts>span:last-child { grid-column:1; grid-row:2; }
  .production-workbench__package-counts>span+span::before { content:none; }
  .production-workbench__package .production-workbench__package-required { grid-column:2; grid-row:2; margin-top:0; font-size:var(--pdm-font-size-caption); }
}
@media(max-width:700px){.production-workbench__detail:has(> .production-workbench__empty){min-height:120px}}

/* 制造中心容器间距统一为 5px。 */
.production-workbench__tree,
.production-workbench__lines,
.production-workbench__detail,
.production-workbench__summary,
.production-workbench__secondary { padding: var(--pdm-container-gap); }
.production-workbench__tree-search { margin: 0 0 var(--pdm-container-gap); }
.production-workbench__line-tools { padding-block: var(--pdm-container-gap); }
.production-workbench .production-workbench__line-tools > .pdm-tabs { margin-block: 0 !important; }
.production-workbench__bulk-tools { gap: var(--pdm-container-gap); padding-bottom: var(--pdm-container-gap); }
.production-workbench__secondary > header,
.production-workbench__capacity-form,
.production-workbench__execution-actions { gap: var(--pdm-container-gap); margin-bottom: var(--pdm-container-gap); }
.production-workbench__execution-form { gap: var(--pdm-container-gap); }
.production-workbench__scan-choices { gap: var(--pdm-container-gap); padding: var(--pdm-container-gap); margin-bottom: var(--pdm-container-gap); }

/* 发布包筛选与刷新统一放在左侧容器顶部。 */
.production-workbench__tree > .production-workbench__toolbar {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  gap: var(--pdm-container-gap);
  margin-bottom: var(--pdm-container-gap);
}
.production-workbench__tree > .production-workbench__toolbar select { width: 100%; }
.production-workbench__tree > .production-workbench__toolbar label { gap: var(--pdm-container-gap); white-space: nowrap; }
@media (min-width: 701px) and (max-width: 1100px) {
  .production-workbench__tree > .production-workbench__toolbar { grid-template-columns: minmax(0, 1fr) auto; }
  .production-workbench__tree > .production-workbench__toolbar select { grid-column: 1 / -1; }
}
.production-workbench__late-date{color:var(--pdm-danger)}
.production-workbench__download-status{flex:0 0 auto;margin:0 0 var(--pdm-container-gap);font-size:11px;color:var(--pdm-muted);overflow-wrap:anywhere}
.production-workbench__trace-filters{display:grid;grid-template-columns:repeat(6,minmax(176px,1fr));gap:var(--pdm-container-gap);align-items:end;flex:0 0 auto;min-width:0;overflow-x:auto;padding-bottom:var(--pdm-container-gap)}
.production-workbench__trace-filters label{display:grid;gap:3px;min-width:0;font-size:11px;white-space:nowrap}
.production-workbench__trace-filters input,.production-workbench__trace-filters select{width:100%;min-width:0;height:28px;box-sizing:border-box}
.production-workbench__trace-filters :deep(.el-date-editor){width:100%;min-width:0;height:28px;min-height:28px;box-sizing:border-box;padding-inline:2px}
.production-workbench__trace-filters :deep(.el-range-input){flex:1;min-width:0;width:0;font-size:12px}
.production-workbench__trace-filters :deep(.el-range-separator){flex:none;padding-inline:1px}
.production-workbench__trace-filters :deep(.el-range__icon),.production-workbench__trace-filters :deep(.el-range__close-icon){flex:0 0 12px;width:12px;font-size:12px}
.production-workbench__filter-error{flex:0 0 auto;margin:0 0 var(--pdm-container-gap);font-size:11px;color:var(--pdm-danger)}
.production-workbench__revision-link{padding:0;border:0;background:none;color:var(--pdm-danger);font:inherit;text-decoration:underline;text-underline-offset:3px;cursor:pointer}
@media(max-width:700px){.production-workbench__detail:has(.production-revision){min-height:420px;flex:none}}
.production-workbench__detail-heading{display:flex;align-items:flex-start;gap:8px}.production-workbench__detail-heading>div:first-child{min-width:0;flex:1}.production-workbench__detail-heading>.production-drawing-qr{flex:0 0 96px}.production-workbench__detail-heading header{justify-content:flex-start;gap:8px}.production-workbench__detail-heading h2{flex:none}.production-workbench__detail-heading p{overflow-wrap:anywhere}
.production-workbench__history li>*{display:block}

.production-workbench__tree{display:grid;grid-template-columns:minmax(0,1fr) auto auto;align-content:start;column-gap:var(--pdm-container-gap)}
.production-workbench__tree>.production-workbench__toolbar,.production-workbench__tree>.production-workbench__tree-search{grid-column:1/-1;grid-template-columns:subgrid;column-gap:var(--pdm-container-gap)}
.production-workbench__tree>.production-workbench__toolbar>select{grid-column:1}
.production-workbench__tree>.production-workbench__tree-search{display:grid}
.production-workbench__tree-search>input{grid-column:1;width:100%}
.production-workbench__tree-totals{grid-column:2/-1;max-width:none}
.production-workbench__tree>.production-workbench__tree-project,.production-workbench__tree>.production-workbench__empty{grid-column:1/-1}
.production-workbench__project{background:rgb(232,238,247);color:rgb(30,41,59)}
.production-workbench__project.selected{box-shadow:inset 3px 0 0 var(--pdm-theme-accent)}
.production-workbench__package.selected{border-left:0;padding-left:10px;border-radius:0;background:var(--pdm-surface-muted);box-shadow:inset 5px 0 0 var(--shell-brand)}
.production-workbench__package.selected strong{color:var(--pdm-blue)}
.production-workbench__bulk-tools{flex:0 0 auto;flex-wrap:nowrap;overflow-x:auto;min-width:0;white-space:nowrap}
.production-workbench__bulk-tools>nav.pdm-tabs{flex:0 0 auto;max-width:none;margin:0!important;align-self:center}
.production-workbench__bulk-tools>button{flex:0 0 auto}
.production-workbench__quick-filter{display:inline-flex;align-items:center;gap:4px;flex:0 0 auto;white-space:nowrap}
.production-workbench__all-categories{grid-column:1/-1;width:100%;padding:8px 10px;border:1px solid var(--pdm-blue);border-radius:5px;background:var(--pdm-blue);color:var(--pdm-on-primary,#fff);font:inherit;text-align:center;cursor:pointer}
.production-workbench__all-categories.selected{font-weight:700}
.production-workbench__open-package{padding:0;border:0;background:none;color:var(--pdm-blue);font:inherit;cursor:pointer}
@media(min-width:701px) and (max-width:1100px){
.production-workbench__tree>.production-workbench__toolbar>select,.production-workbench__tree-search>input{grid-column:1/-1}
.production-workbench__tree>.production-workbench__toolbar>label{grid-column:1/3}
.production-workbench__tree>.production-workbench__toolbar>button{grid-column:3}
.production-workbench__tree-totals{grid-column:1/-1;justify-content:flex-end}
}
</style>
