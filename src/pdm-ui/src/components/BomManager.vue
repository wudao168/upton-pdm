<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { applyForBomMaterialCodes, linkBomMaterial, listMaterials, previewBomSourceReclassification, reclassifyBomItemsFromSource, resolveBomMaterialCodes } from '../api'
import type { BatchUpdateBomItemsInput, BomClassification, BomEmptyDeclaration, BomItem, BomKind, BomSourceReclassificationPreview, BomValidationField, BomValidationRules, BomVersion, CreateReleasePackageInput, DocumentModelDrawingRelation, ManagedDocument, ManufacturingBomBaseline, MaterialCodeResolution, PdmMaterial, ProjectSummary, ReleasePackageSummary, ReleaseScope } from '../types'
import { u9UnitName, u9UnitOptions } from '../u9Units'
import BomHierarchyOverview from './BomHierarchyOverview.vue'
import ReleaseCenter from './ReleaseCenter.vue'

type BomView = 'Overview' | 'Source' | BomKind
type BomDisplayMode = 'Summary' | 'Structure'
type BomKindFilter = 'All' | BomClassification
type BomComparisonFilter = 'All' | 'Released' | 'Added' | 'Modified' | 'Removed'
type BomComparisonStatus = Exclude<BomComparisonFilter, 'All' | 'Removed'>
type EditableBomField = 'kind' | 'drawingNumber' | 'name' | 'parentDrawingNumber' | 'specification' | 'remark' | 'brand' | 'material' | 'surfaceTreatment' | 'quantity'
type EditableBomRow = BomItem & { _clientKey?: string; _quickEntry?: boolean; _sourceItemIds?: string[] }
type BomRowEntry = { row: EditableBomRow; index: number; depth: number; hasChildren: boolean; expanded: boolean; structureKey: string }
type PendingMaterialLink = { kind: BomKind; materialId: string; materialCode: string; sequence: number; clientKey: string }
type BomComparisonChange = { field: string; label: string; previous: string; current: string }
type BomComparisonEntry = { status: BomComparisonStatus; current: EditableBomRow; previous?: BomItem; changes: BomComparisonChange[] }

const props = withDefaults(defineProps<{
  sourceData?: BomItem[]
  standard: BomItem[]
  nonStandard: BomItem[]
  unclassified?: BomItem[]
  electrical: BomItem[]
  documents?: ManagedDocument[]
  documentRelations?: DocumentModelDrawingRelation[]
  validationRules?: BomValidationRules
  releaseChangeReasonTypes?: string[]
  declarations: BomEmptyDeclaration[]
  versions?: BomVersion[]
  baselines?: ManufacturingBomBaseline[]
  pending: boolean
  editable?: boolean
  token?: string
  projectId?: string
  project?: ProjectSummary
  projects?: ProjectSummary[]
  releasePackages?: ReleasePackageSummary[]
  username?: string
  uploadProgress?: number
  operationError?: string
  canManageRelease?: boolean
  canDecideApproval?: boolean
  canEmergencyDecide?: boolean
  requestedReleasePackageId?: string
}>(), {
  editable: false,
  sourceData: () => [],
  unclassified: () => [],
  documents: () => [],
  documentRelations: () => [],
  versions: () => [],
  baselines: () => [],
  token: '',
  projectId: '',
  projects: () => [],
  releasePackages: () => [],
  username: '',
  uploadProgress: 0,
  operationError: '',
  canManageRelease: false,
  canDecideApproval: false,
  canEmergencyDecide: false,
  requestedReleasePackageId: '',
  validationRules: () => ({
    standard: ['drawingNumber', 'name', 'unit', 'specification', 'quantity', 'revision'],
    nonStandard: ['name', 'unit', 'material', 'quantity', 'revision'],
    electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'],
  }),
  releaseChangeReasonTypes: () => ['设计变更', '客户需求', '物料替代', '质量整改', '生产反馈', '其他'],
})
const emit = defineEmits<{
  save: [kind: BomKind, items: BomItem[]]
  import: [kind: BomKind, file: File]
  export: [kind: BomKind]
  generate: [discardUnsavedChanges: boolean]
  resolve: [itemId: string, action: 'classify' | 'retain' | 'remove', targetKind?: BomKind]
  batchRetain: [itemIds: string[]]
  batchUpdate: [input: BatchUpdateBomItemsInput]
  batchDelete: [itemIds: string[], reason: string]
  batchRestore: [itemIds: string[], mode: 'Original' | 'AsManual']
  restoreSource: [itemIds: string[]]
  releaseCreate: [input: CreateReleasePackageInput]
  releaseUpload: [releasePackageId: string, file: File]
  releaseSubmit: [releasePackageId: string]
  releaseWithdraw: [releasePackageId: string]
  releaseDecide: [taskId: string, decision: 'Approved' | 'Rejected', comment: string]
  releaseTransfer: [taskId: string, targetUsername: string, comment: string]
  releaseEmergencyDecide: [taskId: string, decision: 'Approved' | 'Rejected', reason: string]
  releaseRequestHandled: []
  materialCodeChanged: []
  dirtyChange: [dirty: boolean]
}>()
const kind = ref<BomView>('Source')
const displayMode = ref<BomDisplayMode>('Summary')
const expandedStructurePaths = ref(new Set<string>())
const selectedVersionId = ref('current')
const versionSelectionTouched = ref(false)
const selectedBaselineId = ref('')
const comparisonOpen = ref(false)
const comparisonFilter = ref<BomComparisonFilter>('All')
const rows = ref<EditableBomRow[]>([])
const fileInput = ref<HTMLInputElement>()
const selectedIds = ref<string[]>([])
const batchOpen = ref(false)
const reclassifyPreviewOpen = ref(false)
const reclassifyPending = ref(false)
const reclassifyPreview = ref<BomSourceReclassificationPreview | null>(null)
const reclassifyItemIds = ref<string[]>([])
const recycleBinOpen = ref(false)
const recycleBinSelectedIds = ref<string[]>([])
const batchValidation = ref('')
const editingCell = ref<{ itemId: string; field: EditableBomField; initialValue: string | number | BomClassification } | null>(null)
const inlineValue = ref<string | number | BomClassification>('')
const stagedEditKeys = ref(new Set<string>())
const searchQuery = ref('')
const kindFilter = ref<BomKindFilter>('All')
const brandFilter = ref('')
const materialFilter = ref('')
const showPendingOnly = ref(false)
const bomPage = ref(1)
const bomPageSize = ref(50)
const materialReferenceOpen = ref(false)
const materialReferenceQuery = ref('')
const materialReferenceBrandInput = ref('')
const materialReferenceBrandFilter = ref('')
const materialReferenceLoading = ref(false)
const materialReferenceReason = ref('')
const materialReferenceResults = ref<PdmMaterial[]>([])
const materialReferencePage = ref(1)
const materialReferencePageSize = ref(20)
const pendingMaterialLink = ref<PendingMaterialLink | null>(null)
const materialCodeResolutions = ref<Record<string, MaterialCodeResolution>>({})
const materialCodeResolving = ref(false)
const resolvedMaterialSignatures = new Map<string, string>()
const autoLinkedDraftRows = new Set<string>()
const releaseDrawerOpen = ref(false)
const selectedReleasePackageId = ref('')
const draggedRowIndex = ref<number | null>(null)
const dragOverRowIndex = ref<number | null>(null)
const dragOverPosition = ref<'before' | 'after' | null>(null)
const quickEntryRow = ref<EditableBomRow | null>(null)
let nextClientKey = 0
let discardDraftsOnNextSourceRefresh = false
let promotingQuickEntry = false
let saveRequested = false
let restoreSourceRefreshIds = new Set<string>()
let acknowledgedPublishedRows = new Set<string>()

const stagedEditCount = computed(() => stagedEditKeys.value.size)
const hasStagedEdits = computed(() => stagedEditCount.value > 0)

function createBatchDraft() {
  return {
    kindEnabled: false, targetKind: 'Standard' as BomClassification,
    unitEnabled: false, unit: '001', drawingNumberEnabled: false, drawingNumber: '', nameEnabled: false, name: '',
    specificationEnabled: false, specification: '', remarkEnabled: false, remark: '', brandEnabled: false, brand: '',
    materialEnabled: false, material: '', surfaceTreatmentEnabled: false, surfaceTreatment: '', weightEnabled: false, weight: '',
    quantityEnabled: false, quantity: 1, revisionEnabled: false, revision: '',
  }
}
const batchDraft = ref(createBatchDraft())

const sourceDataRows = computed(() => props.sourceData)
const sourceDisplayRows = computed(() => aggregateSourceRows(sourceDataRows.value))
function distinctMaterialCount(items: BomItem[]) {
  return new Set(items.map((row, index) => {
    const aggregationKey = summaryAggregationKey(row)
    if (aggregationKey) return aggregationKey
    return row.id ? `id:${row.id.toLocaleLowerCase()}` : `row:${index}`
  })).size
}
const sourceDataTotalCount = computed(() => sourceDisplayRows.value.length)
const standardRows = computed(() => props.standard.filter(item => !item.manuallyExcluded && !item.pendingClassification))
const nonStandardRows = computed(() => props.nonStandard.filter(item => !item.manuallyExcluded && !item.pendingClassification))
const electricalRows = computed(() => props.electrical.filter(item => !item.manuallyExcluded))
const standardSummaryRows = computed(() => aggregateSourceRows(standardRows.value))
const nonStandardSummaryRows = computed(() => aggregateSourceRows(nonStandardRows.value))
const electricalSummaryRows = computed(() => aggregateSourceRows(electricalRows.value))
const maintainedKindById = computed(() => new Map(
  [
    ...sourceDataRows.value.filter(item => item.kind === 'Virtual' && !item.pendingClassification).map(item => [item.id, 'Virtual'] as const),
    ...standardRows.value.map(item => [item.id, 'Standard'] as const),
    ...nonStandardRows.value.map(item => [item.id, 'NonStandard'] as const),
    ...electricalRows.value.map(item => [item.id, 'Electrical'] as const),
  ].filter((entry): entry is readonly [string, Exclude<BomClassification, 'Unclassified'>] => !!entry[0]),
))
const categoryVersions = computed(() => kind.value === 'Source' || kind.value === 'Overview' ? [] : props.versions
  .filter(version => version.kind === kind.value)
  .sort((left, right) => right.versionNumber - left.versionNumber))
const activeDraftVersion = computed(() => categoryVersions.value.find(version => version.state === 'Draft'))
const selectedVersion = computed(() => selectedVersionId.value === 'current' ? undefined : categoryVersions.value.find(version => version.id === selectedVersionId.value))
const currentCategoryRows = computed(() => kind.value === 'Standard' ? standardRows.value : kind.value === 'NonStandard' ? nonStandardRows.value : electricalRows.value)
const rawSourceRows = computed(() => kind.value === 'Source'
  ? sourceDataRows.value
  : (selectedVersion.value?.items.filter(item => !item.manuallyExcluded && !item.pendingClassification) ?? currentCategoryRows.value))
const summaryRowCount = computed(() => aggregateSourceRows(rawSourceRows.value).length)
const sourceRows = computed(() => displayMode.value === 'Summary' ? aggregateSourceRows(rawSourceRows.value) : rawSourceRows.value)
const isSourceView = computed(() => kind.value === 'Source')
const isOverviewView = computed(() => kind.value === 'Overview')
const canClassifySourceView = computed(() => props.editable && isSourceView.value)
const canEditCurrentView = computed(() => props.editable && !isSourceView.value && !isOverviewView.value && selectedVersionId.value === 'current')
const canShowQuickEntry = computed(() => canEditCurrentView.value && !!props.projectId && comparisonFilter.value === 'All')
const canSelectCurrentView = computed(() => canClassifySourceView.value || canEditCurrentView.value)
const latestReleasedVersion = computed(() => categoryVersions.value.find(version => version.state === 'Released'))
const latestPublishedComparisonPackage = computed(() => props.releasePackages
  .filter(item => item.state === '已发布' && releasePackageMatchesKind(item, kind.value))
  .sort((left, right) => (right.publishedAt ?? right.createdAt ?? '').localeCompare(left.publishedAt ?? left.createdAt ?? ''))[0])
const comparisonBaseVersion = computed(() => {
  if (kind.value === 'Source') return undefined
  if (selectedVersionId.value === 'current') return latestReleasedVersion.value
  const selected = selectedVersion.value
  if (!selected) return latestReleasedVersion.value
  return categoryVersions.value.find(version => version.state === 'Released' && version.versionNumber < selected.versionNumber)
})
const comparisonBaseline = computed(() => {
  if (selectedVersionId.value !== 'current') {
    const version = comparisonBaseVersion.value
    return version ? { label: displayVersionLabel(version.label), items: version.items } : undefined
  }
  if (latestReleasedVersion.value) return { label: displayVersionLabel(latestReleasedVersion.value.label), items: latestReleasedVersion.value.items }
  const releasePackage = latestPublishedComparisonPackage.value
  if (!releasePackage) return undefined
  const items = kind.value === 'Standard' ? releasePackage.standardBomSnapshot
    : kind.value === 'NonStandard' ? releasePackage.nonStandardBomSnapshot
      : kind.value === 'Electrical' ? releasePackage.electricalBomSnapshot : []
  return { label: releasePackage.number, items }
})
const comparisonBaseRows = computed(() => {
  const items = comparisonBaseline.value?.items.filter(item => !item.manuallyExcluded && !item.pendingClassification) ?? []
  return displayMode.value === 'Summary' ? aggregateSourceRows(items) : items
})
const comparison = computed(() => compareBomRows(rows.value.filter(row => !row._quickEntry), comparisonBaseRows.value, displayMode.value))
const selectedBaseline = computed(() => props.baselines.find(baseline => baseline.id === selectedBaselineId.value) ?? props.baselines[0])
const unresolvedCount = computed(() => distinctMaterialCount(
  [...props.standard, ...props.nonStandard, ...props.unclassified, ...props.electrical]
    .filter(item => !item.manuallyExcluded && (item.pendingClassification || item.pendingRemoval || item.manualUnmatched)),
))
const maintainedMechanicalRows = computed(() => [...props.standard, ...props.nonStandard, ...props.unclassified])
const maintainedMechanicalById = computed(() => new Map(maintainedMechanicalRows.value
  .filter(item => !!item.id)
  .map(item => [item.id, item])))
const sourceDataIds = computed(() => new Set(sourceDataRows.value.map(item => item.id).filter(Boolean)))
const reconciliationReminderCount = computed(() => {
  const changedSourceRows = sourceDataRows.value.filter(source => {
    const maintained = source.id ? maintainedMechanicalById.value.get(source.id) : undefined
    if (!maintained) return true
    return source.reconciliationStatus === 'ManualOverrideMismatch'
      && Boolean(reconciliationIssueLabel(source))
  }).length
  const removedSourceRows = maintainedMechanicalRows.value.filter(item => item.source === 'Auto'
    && !!item.sourceDocumentId
    && !item.manuallyExcluded
    && !item.pendingRemoval
    && !item.manualUnmatched
    && !!item.id
    && !sourceDataIds.value.has(item.id)).length
  return changedSourceRows + removedSourceRows
})
const quantityMismatchCount = computed(() => {
  const mismatchedMaterials = new Set<string>()
  maintainedMechanicalRows.value
    .filter(item => !item.manuallyExcluded && !item.pendingRemoval && !item.manualUnmatched)
    .forEach(item => {
      const source = rawSourceRowFor(item)
      if (!source || Number(source.quantity) === Number(item.quantity)) return
      mismatchedMaterials.add(summaryAggregationKey(source) || source.id || item.id || `${item.sequence}`)
    })
  return mismatchedMaterials.size
})
const reconcileActionText = computed(() => quantityMismatchCount.value > 0
  ? `修正数量（${quantityMismatchCount.value}）`
  : '重新对账')
const reconcileActionTitle = computed(() => quantityMismatchCount.value > 0
  ? `按最新设计树修正 ${quantityMismatchCount.value} 类物料数量；名称、型号、品牌等人工维护值保持不变`
  : '图档提交后会自动更新；此操作用于人工重新对账')
const brandOptions = computed(() => distinctFilterOptions(rows.value.map(item => item.brand)))
const materialOptions = computed(() => distinctFilterOptions(rows.value.map(item => item.material)))
function rowMatchesFilters(row: EditableBomRow) {
  const query = searchQuery.value.trim().toLocaleLowerCase()
  const effectiveKind = rowKind(row) ?? 'Unclassified'
  const searchable = [row.name, row.drawingNumber, row.specification, row.sourceInstancePath].join(' ').toLocaleLowerCase()
  if (query && !searchable.includes(query)) return false
  if (kindFilter.value !== 'All' && effectiveKind !== kindFilter.value) return false
  if (brandFilter.value && row.brand?.trim() !== brandFilter.value) return false
  if (materialFilter.value && row.material?.trim() !== materialFilter.value) return false
  if (showPendingOnly.value && !rowNeedsProcessing(row)) return false
  if (comparisonFilter.value === 'Removed') return false
  if (comparisonFilter.value !== 'All' && comparisonRowStatus(row) !== comparisonFilter.value) return false
  return true
}
const filteredRows = computed(() => rows.value.flatMap((row, index) => rowMatchesFilters(row) ? [{ row, index }] : []))

function normalizedStructurePath(row: BomItem) {
  return row.sourceInstancePath?.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '') ?? ''
}

const structureRows = computed<BomRowEntry[]>(() => {
  const nodes = rows.value.map((row, index) => ({
    row,
    index,
    path: normalizedStructurePath(row),
    structureKey: normalizedStructurePath(row) || `row:${rowSelectionKey(row) ?? index}`,
    parentKey: '',
  }))
  const paths = new Set(nodes.flatMap(node => node.path ? [node.path] : []))
  const nodesByKey = new Map(nodes.map(node => [node.structureKey, node]))
  nodes.forEach(node => {
    if (!node.path) return
    let parentPath = node.path.includes('/') ? node.path.slice(0, node.path.lastIndexOf('/')) : ''
    while (parentPath && !paths.has(parentPath)) parentPath = parentPath.includes('/') ? parentPath.slice(0, parentPath.lastIndexOf('/')) : ''
    node.parentKey = parentPath
  })
  const children = new Map<string, typeof nodes>()
  nodes.forEach(node => children.set(node.parentKey, [...(children.get(node.parentKey) ?? []), node]))
  children.forEach(items => items.sort((left, right) => left.path.localeCompare(right.path, 'zh-CN', { numeric: true }) || left.row.sequence - right.row.sequence))
  const matched = new Set(nodes.filter(node => rowMatchesFilters(node.row)).map(node => node.structureKey))
  const included = new Set(matched)
  if (filtersActive.value) {
    matched.forEach(key => {
      let parentKey = nodesByKey.get(key)?.parentKey ?? ''
      while (parentKey) {
        included.add(parentKey)
        parentKey = nodesByKey.get(parentKey)?.parentKey ?? ''
      }
    })
  }
  const result: BomRowEntry[] = []
  const visit = (node: (typeof nodes)[number], depth: number) => {
    if (filtersActive.value && !included.has(node.structureKey)) return
    const childRows = children.get(node.structureKey) ?? []
    const expanded = expandedStructurePaths.value.has(node.structureKey)
    result.push({ row: node.row, index: node.index, depth, hasChildren: childRows.length > 0, expanded, structureKey: node.structureKey })
    if (filtersActive.value || expanded) childRows.forEach(child => visit(child, depth + 1))
  }
  ;(children.get('') ?? []).forEach(node => visit(node, 0))
  return result
})
const displayEntries = computed<BomRowEntry[]>(() => displayMode.value === 'Structure'
  ? structureRows.value
  : filteredRows.value.map(({ row, index }) => ({ row, index, depth: 0, hasChildren: false, expanded: false, structureKey: rowSelectionKey(row) ?? `row:${index}` })))
const bomPageCount = computed(() => Math.max(1, Math.ceil(displayEntries.value.length / bomPageSize.value)))
const pagedRows = computed<BomRowEntry[]>(() => {
  const start = (bomPage.value - 1) * bomPageSize.value
  const entries = displayEntries.value.slice(start, start + bomPageSize.value)
  return displayMode.value === 'Summary' && quickEntryRow.value && canShowQuickEntry.value
    ? [...entries, { row: quickEntryRow.value, index: rows.value.length, depth: 0, hasChildren: false, expanded: false, structureKey: rowSelectionKey(quickEntryRow.value) ?? `row:${rows.value.length}` }]
    : entries
})
const selectableIds = computed(() => pagedRows.value.flatMap(({ row }) => {
  const key = rowSelectionKey(row)
  return key ? [key] : []
}))
const selectedRows = computed(() => rows.value.filter(item => {
  const key = rowSelectionKey(item)
  return !!key && selectedIds.value.includes(key)
}))
const quantityTotals = computed(() => {
  const totals = new Map<string, number>()
  rows.value.forEach(row => {
    const key = quantityAggregationKey(row)
    if (!key) return
    totals.set(key, (totals.get(key) ?? 0) + Number(row.quantity))
  })
  return totals
})
function operationItemIds(row: EditableBomRow) {
  if (row._sourceItemIds?.length) return row._sourceItemIds
  const materialKey = quantityAggregationKey(row)
  if (!materialKey) return row.id ? [row.id] : []
  return rawSourceRows.value
    .filter(item => quantityAggregationKey(item) === materialKey)
    .flatMap(item => item.id ? [item.id] : [])
}
const selectedPersistedIds = computed(() => [...new Set(selectedRows.value.flatMap(operationItemIds))])
const selectedStandardItemsWithoutCode = computed(() => kind.value === 'Standard'
  ? selectedRows.value.filter(item => {
      const status = materialResolution(item)?.status
      return item.id && !item.drawingNumber.trim() && status !== 'ApplicationPending' && status !== 'ApplicationApproved'
    })
  : [])
const hasSelectedDraftRows = computed(() => selectedRows.value.some(item => !item.id))
const recycleBinRows = computed(() => [...props.standard, ...props.nonStandard, ...props.unclassified, ...props.electrical]
  .filter(item => item.id && item.manuallyExcluded)
  .sort((left, right) => (right.deletedAt ?? '').localeCompare(left.deletedAt ?? '') || left.sequence - right.sequence))
const recycleBinAllSelected = computed(() => recycleBinRows.value.length > 0 && recycleBinRows.value.every(item => item.id && recycleBinSelectedIds.value.includes(item.id)))
const allRowsSelected = computed(() => selectableIds.value.length > 0 && selectableIds.value.every(id => selectedIds.value.includes(id)))
const batchFieldCount = computed(() => Object.entries(batchDraft.value).filter(([key, value]) => key.endsWith('Enabled') && value).length)
const retainableSelectedRows = computed(() => selectedRows.value.filter(row => row.id && (row.pendingRemoval || row.manualUnmatched)))
const hasRetainableSelection = computed(() => retainableSelectedRows.value.length > 0)
const canRetainSelected = computed(() => selectedRows.value.length > 0 && retainableSelectedRows.value.length === selectedRows.value.length)
const canConfirmDeleteSelected = computed(() => selectedRows.value.length > 0 && selectedRows.value.every(row => row.pendingRemoval || row.manualUnmatched))
const canRestoreSourceSelected = computed(() => selectedRows.value.length > 0
  && (kind.value === 'Standard' || kind.value === 'NonStandard')
  && selectedRows.value.every(row => row.sourceDocumentId))
const filtersActive = computed(() => !!searchQuery.value.trim() || kindFilter.value !== 'All' || !!brandFilter.value || !!materialFilter.value || showPendingOnly.value || comparisonFilter.value !== 'All')
const allowedReleaseScopes = computed<Array<Exclude<ReleaseScope, 'LegacyCombined'>>>(() => kind.value === 'Standard'
  ? ['StandardLongLead', 'StandardFormal', 'StandardSupplement']
  : kind.value === 'NonStandard' ? ['NonStandardWithDrawing']
    : kind.value === 'Electrical' ? ['ElectricalFormal', 'ElectricalSupplement'] : [])
const hasPublishedStandardFormal = computed(() => props.releasePackages.some(item => item.scope === 'StandardFormal' && item.state === '已发布'))
const createReleaseScopes = computed<Array<Exclude<ReleaseScope, 'LegacyCombined'>>>(() =>
  kind.value === 'Standard' && hasPublishedStandardFormal.value
    ? ['StandardLongLead', 'StandardSupplement']
    : allowedReleaseScopes.value)
const preferredReleaseScope = computed<Exclude<ReleaseScope, 'LegacyCombined'> | undefined>(() => {
  if (kind.value !== 'Standard') return undefined
  if (hasPublishedStandardFormal.value) return 'StandardSupplement'
  return props.releasePackages.some(item => item.scope === 'StandardLongLead' && item.state === '已发布')
    ? 'StandardFormal'
    : 'StandardLongLead'
})
const publishedLongLeadItemIds = computed(() => [...new Set(props.releasePackages
  .filter(item => item.scope === 'StandardLongLead' && item.state === '已发布')
  .flatMap(item => item.selectedBomItemIds))])
const materialReferenceBrandOptions = computed(() => [...new Set(materialReferenceResults.value
  .map(item => item.brand?.trim())
  .filter((brand): brand is string => Boolean(brand)))]
  .sort((left, right) => left.localeCompare(right, 'zh-CN')))
const filteredMaterialReferenceResults = computed(() => {
  const brand = materialReferenceBrandFilter.value.trim().toLocaleLowerCase()
  return materialReferenceResults.value
    .filter(item => !brand || item.brand?.toLocaleLowerCase().includes(brand))
    .sort((left, right) => left.materialCode.localeCompare(right.materialCode, 'zh-CN', { numeric: true }))
})
const materialReferencePageCount = computed(() => Math.max(1, Math.ceil(filteredMaterialReferenceResults.value.length / materialReferencePageSize.value)))
const pagedMaterialReferenceResults = computed(() => {
  const start = (materialReferencePage.value - 1) * materialReferencePageSize.value
  return filteredMaterialReferenceResults.value.slice(start, start + materialReferencePageSize.value)
})
watch([materialReferenceBrandFilter, materialReferencePageSize], () => {
  materialReferencePage.value = 1
})
watch(materialReferencePageCount, pageCount => {
  if (materialReferencePage.value > pageCount) materialReferencePage.value = pageCount
})
const categoryReleasePackages = computed(() => props.releasePackages
  .filter(item => allowedReleaseScopes.value.includes(item.scope as Exclude<ReleaseScope, 'LegacyCombined'>))
  .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? '')))
const selectedReleasePackage = computed(() => props.releasePackages.find(item => item.id === selectedReleasePackageId.value) ?? null)
const activeReleasePackages = computed(() => categoryReleasePackages.value.filter(item => item.state !== '已发布'))
const latestPublishedPackage = computed(() => categoryReleasePackages.value.find(item => item.state === '已发布'))
const selectedReleaseVersionId = computed(() => {
  const release = selectedReleasePackage.value
  if (!release) return undefined
  if (release.scope.startsWith('Electrical')) return release.electricalBomVersionId
  if (release.scope === 'NonStandardWithDrawing') return release.nonStandardBomVersionId
  return release.standardBomVersionId
})
const selectedReleaseVersion = computed(() => props.versions.find(version => version.id === selectedReleaseVersionId.value))
const previousReleaseVersionItems = computed(() => {
  const selected = selectedReleaseVersion.value
  if (!selected) return latestReleasedVersion.value?.items ?? []
  return categoryVersions.value.find(version => version.state === 'Released' && version.versionNumber < selected.versionNumber)?.items ?? []
})

function openReleaseDrawer(releasePackageId = '') {
  selectedReleasePackageId.value = releasePackageId
  releaseDrawerOpen.value = true
}

function materialResolution(row: BomItem) {
  const resolutions = operationItemIds(row as EditableBomRow)
    .map(itemId => materialCodeResolutions.value[itemId])
    .filter((resolution): resolution is MaterialCodeResolution => Boolean(resolution))
  return resolutions.find(resolution => resolution.status === 'ApplicationPending')
    ?? resolutions.find(resolution => resolution.status === 'ApplicationApproved')
    ?? resolutions.find(resolution => resolution.status === 'Ambiguous')
    ?? resolutions.find(resolution => resolution.status === 'Matched')
    ?? resolutions.find(resolution => resolution.status === 'NoMatch')
    ?? resolutions[0]
}

function materialCodeApplicationInProgress(row: BomItem) {
  const resolution = materialResolution(row)
  return resolution?.status === 'ApplicationPending'
    || resolution?.status === 'ApplicationApproved' && resolution.application?.workflowState !== 'Completed'
}

async function resolveMissingStandardMaterialCodes(force = false) {
  if (!props.token || !props.projectId || kind.value !== 'Standard' || materialCodeResolving.value) return
  const targets = rawSourceRows.value.filter(row => row.id && (!row.drawingNumber.trim() || row.sourceDocumentId)).filter(row => {
    const signature = `${row.drawingNumber?.trim().toLocaleLowerCase() ?? ''}|${row.brand?.trim().toLocaleLowerCase() ?? ''}|${row.specification?.trim().toLocaleLowerCase() ?? ''}`
    return force || resolvedMaterialSignatures.get(row.id!) !== signature
  })
  if (targets.length === 0) return
  targets.forEach(row => resolvedMaterialSignatures.set(row.id!, `${row.drawingNumber?.trim().toLocaleLowerCase() ?? ''}|${row.brand?.trim().toLocaleLowerCase() ?? ''}|${row.specification?.trim().toLocaleLowerCase() ?? ''}`))
  materialCodeResolving.value = true
  try {
    const resolutions = await resolveBomMaterialCodes(props.projectId, targets.map(row => row.id!), props.token)
    let linked = false
    for (const resolution of resolutions) {
      materialCodeResolutions.value[resolution.bomItemId] = resolution
      if (resolution.status === 'Matched' && resolution.material) {
        const row = rows.value.find(item => item.id === resolution.bomItemId)
        if (row && !row.drawingNumber.trim()) row.drawingNumber = resolution.material.materialCode
        linked = true
      }
    }
    if (linked) emit('materialCodeChanged')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '自动核对标准件料号失败')
  } finally {
    materialCodeResolving.value = false
  }
}

async function applyForMaterialCodes(itemIds: string[]) {
  if (!props.token || !props.projectId || itemIds.length === 0) return
  const itemById = new Map(rawSourceRows.value.flatMap(item => item.id ? [[item.id, item] as const] : []))
  const invalidItems = itemIds.flatMap(itemId => {
    const item = itemById.get(itemId)
    if (!item) return []
    const missing = [
      !item.name?.trim() ? '物料名称' : '',
      !item.specification?.trim() ? '型号' : '',
      !item.brand?.trim() ? '品牌' : '',
    ].filter(Boolean)
    return missing.length ? [{ item, missing }] : []
  })
  if (invalidItems.length) {
    const details = invalidItems.slice(0, 5)
      .map(({ item, missing }) => `第 ${item.sequence} 行缺少${missing.join('、')}`)
      .join('；')
    const suffix = invalidItems.length > 5 ? `；另有 ${invalidItems.length - 5} 行未完善` : ''
    ElMessage.error(`标准件料号申请前必须补全物料名称、型号、品牌；${details}${suffix}`)
    return
  }
  try {
    const resolutions = await applyForBomMaterialCodes(props.projectId, itemIds, props.token)
    let linked = false
    for (const resolution of resolutions) {
      materialCodeResolutions.value[resolution.bomItemId] = resolution
      if (resolution.status === 'Matched' && resolution.material) linked = true
    }
    const pendingResolutions = resolutions.filter(item => item.status === 'ApplicationPending')
    const pendingCount = pendingResolutions.length
    const pendingApplicationCount = new Set(pendingResolutions.map(item => item.application?.id).filter(Boolean)).size
    const ambiguousCount = resolutions.filter(item => item.status === 'Ambiguous').length
    if (pendingCount) ElMessage.success(pendingApplicationCount > 0 && pendingApplicationCount < pendingCount
      ? `${pendingCount} 项同型号物料已合并为 ${pendingApplicationCount} 个料号申请，等待任意标准化角色处理`
      : `已提交 ${pendingCount} 项料号申请，等待任意标准化角色处理`)
    if (ambiguousCount) ElMessage.warning(`${ambiguousCount} 项存在多个同品牌同型号料品，请先核对后引用`)
    if (linked) emit('materialCodeChanged')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '提交料号申请失败')
  }
}

function openMaterialCandidates(row: BomItem) {
  if (!row.id) return
  const resolution = materialResolution(row)
  selectedIds.value = [row.id]
  materialReferenceQuery.value = row.specification ?? ''
  materialReferenceBrandInput.value = row.brand ?? ''
  materialReferenceBrandFilter.value = row.brand ?? ''
  materialReferenceReason.value = `同品牌“${row.brand || '未填写'}”、同型号“${row.specification || '未填写'}”匹配到多个正式料号，请选择本BOM应引用的料号。`
  materialReferenceResults.value = (resolution?.status === 'Ambiguous' ? resolution.candidates : [])
    .filter(item => !item.isArchived && item.approvalStatus === 'Approved' && item.kind === kind.value)
  materialReferencePage.value = 1
  materialReferencePageSize.value = 20
  materialReferenceOpen.value = true
  if (materialReferenceResults.value.length === 0) void searchMaterialReferences()
}

function selectReleasePackage(releasePackageId: string) {
  selectedReleasePackageId.value = releasePackageId
}

function versionStateLabel(state: BomVersion['state']) {
  return ({ Draft: '工作中', InReview: '待审批', Released: '已发布', Obsolete: '已作废' } as Record<BomVersion['state'], string>)[state]
}

function distinctFilterOptions(values: Array<string | null | undefined>) {
  return [...new Set(values.map(value => value?.trim()).filter((value): value is string => !!value))]
    .sort((left, right) => left.localeCompare(right, 'zh-CN'))
}

function displayVersionLabel(label?: string) {
  return label?.replace(/-B(?=\d+$)/, '-V') ?? '—'
}

function versionLabel(versionId: string) {
  return displayVersionLabel(props.versions.find(version => version.id === versionId)?.label)
}

function baselineChangeLabel(baseline: ManufacturingBomBaseline) {
  const releasePackage = props.releasePackages.find(item => item.id === baseline.releasePackageId)
  return baseline.changeNumber && baseline.changeNumber !== releasePackage?.number ? ` · ${baseline.changeNumber}` : ''
}

const comparisonFields: Array<{ field: keyof BomItem; label: string }> = [
  { field: 'kind', label: '物料分类' }, { field: 'unit', label: '单位' }, { field: 'name', label: '物料名称' },
  { field: 'parentDrawingNumber', label: '上级物料编码' }, { field: 'specification', label: '型号' }, { field: 'remark', label: '备注' },
  { field: 'brand', label: '品牌' }, { field: 'material', label: '材质' }, { field: 'surfaceTreatment', label: '表面处理' },
  { field: 'weight', label: '重量' }, { field: 'quantity', label: '数量' }, { field: 'revision', label: '版本' },
]

function comparisonValue(value: unknown) {
  if (value === null || value === undefined || String(value).trim() === '') return '—'
  return typeof value === 'number' ? String(Number(value.toFixed(4))) : String(value).trim()
}

function comparisonMaterialKey(item: BomItem, mode: BomDisplayMode) {
  const materialKey = quantityAggregationKey(item)
  if (materialKey) return `material:${materialKey}`
  if (item.sourceDocumentId) {
    const sourceParts = ['document', item.sourceDocumentId, item.sourceConfiguration]
    if (mode === 'Structure') sourceParts.push(item.sourceInstancePath)
    return sourceParts.map(value => normalizedAggregationValue(value)).join('|')
  }
  if (item.id) return `id:${item.id.toLocaleLowerCase()}`
  return ['manual', item.name, item.specification, item.unit].map(value => normalizedAggregationValue(value)).join('|')
}

function indexedComparisonRows<T extends BomItem>(items: T[], mode: BomDisplayMode) {
  const occurrences = new Map<string, number>()
  return items.map(item => {
    const baseKey = comparisonMaterialKey(item, mode)
    const occurrence = (occurrences.get(baseKey) ?? 0) + 1
    occurrences.set(baseKey, occurrence)
    return { item, key: `${baseKey}#${occurrence}` }
  })
}

function comparisonRowReference(row: EditableBomRow) {
  return row.id ? `id:${row.id.toLocaleLowerCase()}` : row._clientKey ? `draft:${row._clientKey}` : `sequence:${row.sequence}`
}

function compareBomRows(current: EditableBomRow[], previous: BomItem[], mode: BomDisplayMode) {
  const currentRows = indexedComparisonRows(current, mode)
  const previousRows = indexedComparisonRows(previous, mode)
  const previousByKey = new Map(previousRows.map(entry => [entry.key, entry.item]))
  const currentKeys = new Set(currentRows.map(entry => entry.key))
  const entries = new Map<string, BomComparisonEntry>()
  const added: EditableBomRow[] = []
  const modified: EditableBomRow[] = []
  const released: EditableBomRow[] = []
  currentRows.forEach(({ item, key }) => {
    const old = previousByKey.get(key)
    if (!old) {
      added.push(item)
      entries.set(comparisonRowReference(item), { status: 'Added', current: item, changes: [] })
      return
    }
    const changes = comparisonFields.flatMap(({ field, label }) => {
      const previousValue = comparisonValue(old[field])
      const currentValue = comparisonValue(item[field])
      return previousValue === currentValue ? [] : [{ field: String(field), label, previous: previousValue, current: currentValue }]
    })
    if (changes.length) {
      modified.push(item)
      entries.set(comparisonRowReference(item), { status: 'Modified', current: item, previous: old, changes })
    } else {
      released.push(item)
      entries.set(comparisonRowReference(item), { status: 'Released', current: item, previous: old, changes: [] })
    }
  })
  const removed = previousRows.filter(entry => !currentKeys.has(entry.key)).map(entry => entry.item)
  return { added, removed, modified, released, entries }
}

function comparisonEntry(row: EditableBomRow) {
  if (!comparisonOpen.value || !comparisonBaseline.value || row._quickEntry) return undefined
  return comparison.value.entries.get(comparisonRowReference(row))
}

function comparisonRowStatus(row: EditableBomRow) {
  return comparisonEntry(row)?.status
}

function comparisonStatusLabel(status: BomComparisonStatus | undefined) {
  return status === 'Released' ? '已发布' : status === 'Added' ? '新增' : status === 'Modified' ? '已修改' : ''
}

function comparisonRowTitle(row: EditableBomRow) {
  const entry = comparisonEntry(row)
  if (!entry) return undefined
  if (entry.status === 'Released') return '已发布基线：与最近发布版一致'
  if (entry.status === 'Added') return '新增：最近发布版中不存在该物料'
  return `已修改：${entry.changes.map(change => `${change.label} ${change.previous} → ${change.current}`).join('；')}`
}

function comparisonQuantityDisplay(row: EditableBomRow) {
  const quantityChange = comparisonEntry(row)?.changes.find(change => change.field === 'quantity')
  return quantityChange ? `${quantityChange.previous} → ${quantityChange.current}` : quantityDisplay(row)
}

function comparisonQuantityTitle(row: EditableBomRow) {
  const quantityChange = comparisonEntry(row)?.changes.find(change => change.field === 'quantity')
  return quantityChange
    ? `最近发布数量：${quantityChange.previous}；当前工作区数量：${quantityChange.current}`
    : quantityTitle(row)
}

function releasePackageMatchesKind(releasePackage: ReleasePackageSummary, bomKind: BomView) {
  return bomKind === 'Standard'
    ? ['StandardLongLead', 'StandardFormal', 'StandardSupplement'].includes(releasePackage.scope)
    : bomKind === 'NonStandard' ? releasePackage.scope === 'NonStandardWithDrawing'
      : bomKind === 'Electrical' ? ['ElectricalFormal', 'ElectricalSupplement'].includes(releasePackage.scope) : false
}

function hasManualClassificationMismatch(row: BomItem) {
  return row.source === 'Auto'
    && row.reconciliationStatus === 'ManuallyClassified'
    && row.propertyWritebackStatus !== 'Succeeded'
}

function rawMismatchFields(row: BomItem) {
  const value = row.reconciliationNote?.match(/不一致[：:]\s*([^。]+)/)?.[1]?.trim()
  return value?.split(/[、，,]/).map(item => item.trim()).filter(Boolean) ?? []
}

function mismatchFields(row: BomItem) {
  const fields = rawMismatchFields(row)
  const comparedFields = row.kind === 'Standard'
    ? new Set(['物料分类', '物料编码', '型号', '品牌'])
    : row.kind === 'NonStandard'
      ? new Set(['物料分类', '型号', '材质', '表面处理'])
      : null
  return comparedFields ? fields.filter(field => comparedFields.has(field)) : fields
}

function isClassificationOnlyMismatch(row: BomItem) {
  const fields = mismatchFields(row)
  return row.reconciliationStatus === 'ManualOverrideMismatch'
    && fields.length === 1
    && fields[0] === '物料分类'
}

function reconciliationIssueLabel(row: BomItem) {
  if (row.propertyWritebackStatus === 'Failed') return '写回失败'
  if (row.propertyWritebackStatus === 'Conflict') return '写回冲突'
  if (row.pendingClassification || row.reconciliationStatus === 'PendingClassification') return '待分类'
  if (row.pendingRemoval || row.reconciliationStatus === 'PendingRemoval') return '源数据已删除'
  if (row.manualUnmatched || row.reconciliationStatus === 'ManualUnmatched') return '人工项无来源'
  if (hasManualClassificationMismatch(row)) return '分类不一致'
  if (row.reconciliationStatus !== 'ManualOverrideMismatch' || isClassificationOnlyMismatch(row)) return ''
  const rawFields = rawMismatchFields(row)
  const fields = mismatchFields(row)
  if (rawFields.length && !fields.length) return ''
  if (!fields.length) return '属性不一致'
  if (fields.length <= 2) return `${fields.join('、')}不一致`
  return `${fields[0]}等${fields.length}项不一致`
}

function reconciliationIssueTone(row: BomItem) {
  return row.propertyWritebackStatus === 'Failed' || row.propertyWritebackStatus === 'Conflict' ? 'is-critical' : 'is-warning'
}

function reconciliationDescription(row: BomItem) {
  const fields = mismatchFields(row)
  return isClassificationOnlyMismatch(row)
    ? 'BOM分类已确认；其余图档属性与源数据一致。'
    : row.reconciliationStatus === 'ManualOverrideMismatch' && fields.length
      ? `BOM维护值与最新图档源数据不一致：${fields.join('、')}。`
    : row.reconciliationNote
    ?? (row.pendingClassification ? '图档源数据未填写有效物料分类。'
      : row.pendingRemoval ? '最新图档源数据中已不存在，等待确认处理。'
        : row.manualUnmatched ? 'BOM中存在，但最新图档源数据中无对应项。'
          : row.manuallyExcluded ? '当前图档仍存在，已人工排除出BOM。'
            : row.source === 'Auto' ? '来源：图档源数据。' : '来源：人工新增。')
}

const modelDocumentsById = computed(() => new Map(props.documents
  .filter(document => document.kind === 'Assembly' || document.kind === 'Part')
  .map(document => [document.id, document])))

function drawingNameFor(row: BomItem) {
  if (!row.sourceDocumentId) return ''
  const fileName = modelDocumentsById.value.get(row.sourceDocumentId)?.fileName?.trim() ?? ''
  return fileName.replace(/\.[^./\\]+$/, '')
}

function shouldShowReconciliation(row: BomItem) {
  return Boolean(reconciliationIssueLabel(row))
}

function normalizedComparisonValue(value: string | undefined) {
  return value?.trim().replace(/\s+/g, ' ').toLocaleLowerCase('zh-CN') ?? ''
}

function hasDrawingNameModelMismatch(row: BomItem) {
  const drawingName = drawingNameFor(row)
  return !!drawingName && normalizedComparisonValue(drawingName) !== normalizedComparisonValue(row.specification)
}

function drawingNameMismatchTitle(row: BomItem) {
  return `设计树图纸名称：${drawingNameFor(row) || '未获取'}；BOM型号：${row.specification?.trim() || '未填写'}`
}

function rowKindLabel(row: BomItem) {
  const effectiveKind = rowKind(row)
  return effectiveKind === 'Standard' ? '标准件' : effectiveKind === 'Electrical' ? '电气件' : effectiveKind === 'NonStandard' ? '非标件' : effectiveKind === 'Virtual' ? '虚拟件' : '待分类'
}

function rowIsClassified(row: BomItem) {
  const effectiveKind = rowKind(row)
  return effectiveKind !== undefined && effectiveKind !== 'Unclassified'
}

function rowKind(row: BomItem): BomClassification | undefined {
  const sourceItemIds = (row as EditableBomRow)._sourceItemIds
  if (kind.value === 'Source' && sourceItemIds?.length) {
    const maintainedKinds = sourceItemIds.flatMap(id => {
      const maintainedKind = maintainedKindById.value.get(id)
      return maintainedKind ? [maintainedKind] : []
    })
    if (maintainedKinds.length !== sourceItemIds.length || new Set(maintainedKinds).size !== 1) return undefined
    return maintainedKinds[0]
  }
  if (kind.value === 'Source' && row.id) {
    const maintainedKind = maintainedKindById.value.get(row.id)
    if (maintainedKind) return maintainedKind
  }
  if (row.pendingClassification) return undefined
  return row.kind ?? (kind.value === 'Standard' || kind.value === 'NonStandard' || kind.value === 'Electrical' ? kind.value : undefined)
}

function rowNeedsClassification(row: BomItem) {
  return isSourceView.value ? !rowIsClassified(row) : !!row.pendingClassification
}

function rowNeedsProcessing(row: BomItem) {
  return rowNeedsClassification(row) || !!row.pendingRemoval || !!row.manualUnmatched
}

function legacyValidationFields(bomKind: BomKind): BomValidationField[] {
  const core: BomValidationField[] = ['drawingNumber', 'name', 'unit', 'quantity', 'revision']
  if (bomKind === 'Standard') return [...core, 'specification']
  if (bomKind === 'NonStandard') return [...core, 'material']
  return core
}

function missingRequiredFields(row: BomItem) {
  const effectiveKind = rowKind(row)
  if (!effectiveKind || effectiveKind === 'Unclassified') return ['分类']
  if (effectiveKind === 'Virtual') return []
  const snapshot = selectedVersion.value?.validationRequiredFields
  const requiredFields = selectedVersion.value
    ? snapshot?.length ? snapshot : legacyValidationFields(effectiveKind)
    : effectiveKind === 'Standard'
        ? props.validationRules.standard
        : effectiveKind === 'NonStandard'
          ? props.validationRules.nonStandard
          : props.validationRules.electrical
  return requiredFields.filter(field => !hasValidationValue(row, field)).map(validationFieldLabel)
}

function materialMasterIssues(row: BomItem) {
  if (!props.token || !props.projectId || kind.value !== 'Standard' || rowKind(row) !== 'Standard' || !row.sourceDocumentId) return []
  if (materialCodeApplicationInProgress(row)) return ['料号申请中']
  if (!row.drawingNumber.trim()) return []
  const resolutions = operationItemIds(row as EditableBomRow).map(itemId => materialCodeResolutions.value[itemId])
  if (resolutions.some(resolution => !resolution)) return materialCodeResolving ? ['料品主档校验中'] : ['待校验料品主档']
  return [...new Set(resolutions.flatMap(resolution => resolution?.status === 'Verified' ? [] : resolution?.issues?.length ? resolution.issues : ['料品主档校验失败']))]
}

function dataStatusIssues(row: BomItem) {
  return [...missingRequiredFields(row), ...materialMasterIssues(row)]
}

function hasValidationValue(row: BomItem, field: BomValidationField) {
  if (field === 'quantity') return Number(row.quantity) > 0
  const value = field === 'drawingNumber' ? row.drawingNumber
    : field === 'name' ? row.name
      : field === 'unit' ? row.unit
        : field === 'specification' ? row.specification
          : field === 'brand' ? row.brand
            : field === 'material' ? row.material
              : field === 'surfaceTreatment' ? row.surfaceTreatment
                : field === 'weight' ? row.weight
                  : field === 'revision' ? row.revision
                    : row.remark
  return Boolean(value?.trim())
}

function validationFieldLabel(field: BomValidationField) {
  return ({ drawingNumber: '物料编码', name: '物料名称', unit: '单位', specification: '型号', brand: '品牌', material: '材质', surfaceTreatment: '表面处理', weight: '重量', quantity: '数量', revision: '版本', remark: '备注' } as Record<BomValidationField, string>)[field]
}

function dataStatusLabel(row: BomItem) {
  if (isSourceView.value && rowNeedsClassification(row)) return '待归类'
  const masterIssues = materialMasterIssues(row)
  if (masterIssues.length) return `待人工维护：${masterIssues.join('、')}`
  const missing = missingRequiredFields(row)
  return missing.length === 0 ? '已完善' : `缺少${missing.join('、')}`
}

function displayValue(value: string | number | null | undefined) {
  return value === null || value === undefined || String(value).trim() === '' ? '—' : String(value)
}

function quantityAggregationKey(row: BomItem) {
  const materialCode = row.drawingNumber?.trim().toLocaleLowerCase()
  if (!materialCode) return ''
  return `${materialCode}|${row.unit?.trim().toLocaleLowerCase() ?? ''}`
}

function normalizedAggregationValue(value: string | null | undefined) {
  return value?.trim().toLocaleLowerCase() ?? ''
}

function summaryAggregationKey(row: BomItem) {
  const materialKey = quantityAggregationKey(row)
  if (materialKey) return `material:${materialKey}`
  if (!row.sourceDocumentId) return ''
  return [
    'document', row.sourceDocumentId, row.sourceConfiguration,
    row.kind, row.unit, row.name, row.specification, row.brand,
    row.material, row.surfaceTreatment, row.weight === null || row.weight === undefined ? '' : String(row.weight), row.remark, row.revision,
  ].map(value => normalizedAggregationValue(value)).join('|')
}

function aggregateSourceRows(items: BomItem[]): EditableBomRow[] {
  const grouped = new Map<string, EditableBomRow>()
  items.forEach((item, index) => {
    const aggregationKey = summaryAggregationKey(item)
    const key = aggregationKey || (item.id ? `id:${item.id.toLocaleLowerCase()}` : `row:${index}`)
    const existing = grouped.get(key)
    if (!existing) {
      grouped.set(key, {
        ...item,
        _sourceItemIds: item.id ? [item.id] : [],
      })
      return
    }
    existing.quantity = Number(existing.quantity) + Number(item.quantity)
    if (item.id && !existing._sourceItemIds?.includes(item.id)) existing._sourceItemIds = [...(existing._sourceItemIds ?? []), item.id]
    existing.pendingClassification ||= item.pendingClassification
    existing.pendingRemoval ||= item.pendingRemoval
    existing.manualUnmatched ||= item.manualUnmatched
    existing.complete &&= item.complete
  })
  return [...grouped.values()]
}

function toggleStructureRow(structureKey: string) {
  const next = new Set(expandedStructurePaths.value)
  if (next.has(structureKey)) next.delete(structureKey)
  else next.add(structureKey)
  expandedStructurePaths.value = next
}

function displayModeStorageKey(projectId: string) {
  return `pdm:bom-display:${projectId || 'default'}`
}

function restoreDisplayMode(projectId: string) {
  if (typeof window === 'undefined') return
  const stored = window.localStorage.getItem(displayModeStorageKey(projectId))
  displayMode.value = stored === 'Structure' ? 'Structure' : 'Summary'
}

function formatQuantity(value: number) {
  return Number(value.toFixed(4)).toString()
}

function quantityDisplay(row: BomItem) {
  const source = sourceRowFor(row)
  return `${formatQuantity(Number(row.quantity))} / ${source ? formatQuantity(Number(source.quantity)) : '—'}`
}

function quantityTitle(row: BomItem) {
  const source = sourceRowFor(row)
  const key = quantityAggregationKey(row)
  const total = key ? quantityTotals.value.get(key) ?? Number(row.quantity) : Number(row.quantity)
  return `BOM数量 ${formatQuantity(Number(row.quantity))}；源数量 ${source ? formatQuantity(Number(source.quantity)) : '无'}；同料号合计 ${formatQuantity(total)}`
}

function rawSourceRowFor(row: BomItem) {
  return (row.id ? sourceDataRows.value.find(item => item.id === row.id) : undefined)
    ?? (row.sourceDocumentId ? sourceDataRows.value.find(item => {
      if (item.sourceDocumentId !== row.sourceDocumentId) return false
      if (item.sourceInstancePath && row.sourceInstancePath)
        return item.sourceInstancePath.localeCompare(row.sourceInstancePath, undefined, { sensitivity: 'accent' }) === 0
      return (item.sourceConfiguration ?? '').localeCompare(row.sourceConfiguration ?? '', undefined, { sensitivity: 'accent' }) === 0
    }) : undefined)
}

function sourceRowFor(row: BomItem) {
  if (isSourceView.value && (row as EditableBomRow)._sourceItemIds?.length) return row
  const source = rawSourceRowFor(row)
  if (!source || displayMode.value !== 'Summary') return source
  // Match through source identity first: the maintained BOM may use a different official material code.
  const key = summaryAggregationKey(source)
  return key ? sourceDisplayRows.value.find(item => summaryAggregationKey(item) === key) : source
}

function quantityAuditClass(row: BomItem) {
  const source = sourceRowFor(row)
  if (!source) return 'is-blocking'
  return Number(source.quantity) === Number(row.quantity) ? 'is-matched' : 'is-blocking'
}

function drawingAudit(row: BomItem) {
  if (rowKind(row) !== 'NonStandard') return undefined
  if (!row.sourceDocumentId) return { label: '缺3D图', detail: '必须补齐3D及唯一2D工程图', blocking: true, compact: true }
  const drawingIds = [...new Set(props.documentRelations
    .filter(relation => relation.modelDocumentId === row.sourceDocumentId)
    .map(relation => relation.drawingDocumentId))]
  const drawings = drawingIds.flatMap(id => {
    const document = props.documents.find(candidate => candidate.id === id && candidate.kind === 'Drawing')
    return document ? [document] : []
  })
  if (drawings.length === 0) return { label: '缺2D图', detail: '非标BOM禁止仅有3D', blocking: true, compact: true }
  if (drawings.length > 1) return { label: `关联${drawings.length}张2D`, detail: '必须保持唯一对应', blocking: true }
  return { label: '3D+2D已对应', detail: `${drawings[0]!.drawingNumber} · ${drawings[0]!.revision}`, blocking: false }
}

function saveCurrentBom() {
  if (kind.value !== 'Standard' && kind.value !== 'NonStandard' && kind.value !== 'Electrical') return
  discardDraftsOnNextSourceRefresh = true
  saveRequested = true
  const rawById = new Map(rawSourceRows.value.flatMap(item => item.id ? [[item.id, item] as const] : []))
  const expanded = rows.value.flatMap(row => {
    const { _clientKey, _quickEntry, _sourceItemIds, ...displayItem } = row
    if (!_sourceItemIds?.length) return [{ ...displayItem, complete: dataStatusIssues(row).length === 0 }]
    return _sourceItemIds.flatMap(id => {
      const original = rawById.get(id)
      if (!original) return []
      const item = {
        ...original,
        kind: displayItem.kind,
        drawingNumber: displayItem.drawingNumber,
        name: displayItem.name,
        unit: displayItem.unit,
        material: displayItem.material,
        specification: displayItem.specification,
        remark: displayItem.remark,
        brand: displayItem.brand,
        surfaceTreatment: displayItem.surfaceTreatment,
        weight: displayItem.weight,
        quantity: original.quantity,
        revision: displayItem.revision,
        parentDrawingNumber: displayItem.parentDrawingNumber,
      }
      return [{ ...item, complete: dataStatusIssues(item).length === 0 }]
    })
  }).map((item, index) => ({ ...item, sequence: index + 1 }))
  emit('save', kind.value, expanded)
}

function discardStagedEdits() {
  stagedEditKeys.value = new Set()
  saveRequested = false
  refreshRows(false)
  ElMessage.success('未保存的BOM修改已撤销')
}

async function requestKind(nextKind: BomView) {
  if (nextKind === kind.value) return
  if (hasStagedEdits.value) {
    try {
      await ElMessageBox.confirm(
        `当前BOM有 ${stagedEditCount.value} 项未保存修改，切换分类将放弃这些修改。是否继续？`,
        '放弃未保存修改？',
        { confirmButtonText: '放弃修改并切换', cancelButtonText: '取消', type: 'warning', closeOnClickModal: false },
      )
    } catch (error) {
      if (error === 'cancel' || error === 'close') return
      throw error
    }
    stagedEditKeys.value = new Set()
    saveRequested = false
    refreshRows(false)
  }
  kind.value = nextKind
}

function requestGenerate() {
  const discardUnsavedChanges = hasStagedEdits.value
  if (discardUnsavedChanges) discardDraftsOnNextSourceRefresh = true
  emit('generate', discardUnsavedChanges)
}

function requestDisplayMode(nextMode: BomDisplayMode) {
  if (nextMode === displayMode.value) return
  if (hasStagedEdits.value) {
    ElMessage.warning('当前BOM有未保存修改，请先保存或撤销修改后再切换显示方式。')
    return
  }
  displayMode.value = nextMode
}

function markStagedEdits(row: EditableBomRow, fields: string[]) {
  const persistedIds = operationItemIds(row)
  const targets = persistedIds.length ? persistedIds : [row._clientKey ?? `row:${row.sequence}`]
  const next = new Set(stagedEditKeys.value)
  targets.forEach(itemId => fields.forEach(field => next.add(`${itemId}:${field}`)))
  stagedEditKeys.value = next
}

function applyBatchInputToRows(input: BatchUpdateBomItemsInput) {
  const itemIds = new Set(input.itemIds)
  rows.value.filter(row => operationItemIds(row).some(id => itemIds.has(id))).forEach(row => {
    input.fields.forEach(field => {
      if (field === 'unit' && input.unit !== undefined) row.unit = input.unit
      else if (field === 'drawingNumber' && input.drawingNumber !== undefined) row.drawingNumber = input.drawingNumber
      else if (field === 'name' && input.name !== undefined) row.name = input.name
      else if (field === 'parentDrawingNumber' && input.parentDrawingNumber !== undefined) row.parentDrawingNumber = input.parentDrawingNumber
      else if (field === 'specification' && input.specification !== undefined) row.specification = input.specification
      else if (field === 'remark' && input.remark !== undefined) row.remark = input.remark
      else if (field === 'brand' && input.brand !== undefined) row.brand = input.brand
      else if (field === 'material' && input.material !== undefined) row.material = input.material
      else if (field === 'surfaceTreatment' && input.surfaceTreatment !== undefined) row.surfaceTreatment = input.surfaceTreatment
      else if (field === 'weight' && input.weight !== undefined) row.weight = input.weight
      else if (field === 'quantity' && input.quantity !== undefined) row.quantity = input.quantity
      else if (field === 'revision' && input.revision !== undefined) row.revision = input.revision
    })
    markStagedEdits(row, input.fields)
  })
}

async function searchMaterialReferences() {
  if (!props.token || (kind.value !== 'Standard' && kind.value !== 'NonStandard' && kind.value !== 'Electrical')) return
  materialReferenceBrandFilter.value = materialReferenceBrandInput.value.trim()
  materialReferenceLoading.value = true
  try {
    const loaded = await listMaterials(props.token, materialReferenceQuery.value)
    materialReferenceResults.value = loaded.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && item.kind === kind.value)
    materialReferencePage.value = 1
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品主档查询失败')
  } finally {
    materialReferenceLoading.value = false
  }
}

async function openMaterialReference() {
  if (selectedRows.value.length > 1 || isSourceView.value || !props.projectId || !props.token) return
  materialReferenceQuery.value = selectedRows.value[0]?.drawingNumber ?? ''
  materialReferenceBrandInput.value = ''
  materialReferenceBrandFilter.value = ''
  materialReferenceReason.value = ''
  materialReferencePage.value = 1
  materialReferencePageSize.value = 20
  materialReferenceOpen.value = true
  await searchMaterialReferences()
}

async function applyMaterialReference(material: PdmMaterial) {
  if (!props.projectId || !props.token || (kind.value !== 'Standard' && kind.value !== 'NonStandard' && kind.value !== 'Electrical')) return
  let target = selectedRows.value[0]
  if (!target) {
    addRow()
    target = rows.value[rows.value.length - 1]
    const key = target ? rowSelectionKey(target) : undefined
    if (key) selectedIds.value = [key]
  }
  if (!target) return
  try {
    target.drawingNumber = material.materialCode
    target.name = material.name
    target.unit = material.unitCode
    target.specification = material.specification ?? ''
    target.remark = material.remark ?? ''
    target.brand = material.brand ?? ''
    target.material = material.material ?? ''
    target.surfaceTreatment = material.surfaceTreatment ?? ''
    target.weight = material.weight === null || material.weight === undefined ? '' : String(material.weight)
    materialReferenceOpen.value = false
    if (!target.id) {
      pendingMaterialLink.value = { kind: kind.value, materialId: material.id, materialCode: material.materialCode, sequence: target.sequence, clientKey: target._clientKey ?? '' }
      saveCurrentBom()
      return
    }
    await Promise.all(operationItemIds(target).map(itemId => linkBomMaterial(props.projectId!, itemId, material.id, props.token)))
    saveCurrentBom()
    ElMessage.success(`已引用料品 ${material.materialCode}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品引用失败')
  }
}

function clearFilters() {
  searchQuery.value = ''
  kindFilter.value = 'All'
  brandFilter.value = ''
  materialFilter.value = ''
  showPendingOnly.value = false
  comparisonFilter.value = 'All'
}

async function restoreSelectedFromSource() {
  if (!canRestoreSourceSelected.value) return
  const itemIds = selectedPersistedIds.value
  const selectedItemIds = new Set(itemIds)
  const hasOtherStagedEdits = [...stagedEditKeys.value].some(key => {
    const separator = key.lastIndexOf(':')
    return separator > 0 && !selectedItemIds.has(key.slice(0, separator))
  })
  if (hasOtherStagedEdits) {
    ElMessage.warning('还有未选中物料的修改未保存，请先保存或撤销后再恢复源数据。')
    return
  }
  const labels = selectedRows.value.map(row => row.drawingNumber || row.name).slice(0, 5).join('、')
  const suffix = selectedRows.value.length > 5 ? `等 ${selectedRows.value.length} 条` : `${selectedRows.value.length} 条`
  try {
    await ElMessageBox.confirm(
      `将${labels}（${suffix}）恢复为最新图档源属性吗？物料分类与当前排序不会改变。`,
      '恢复源数据',
      { confirmButtonText: '确认恢复', cancelButtonText: '取消', type: 'warning' },
    )
    restoreSourceRefreshIds = selectedItemIds
    emit('restoreSource', itemIds)
    selectedIds.value = []
  } catch {
    // 用户取消时不修改BOM。
  }
}

function resequence() {
  rows.value.forEach((row, index) => { row.sequence = index + 1 })
}

function withClientKey(item: BomItem): EditableBomRow {
  return { ...item, _clientKey: item.id ? undefined : `draft-${++nextClientKey}` }
}

function createQuickEntryRow(): EditableBomRow {
  return { ...withClientKey({ sequence: rows.value.length + 1, drawingNumber: '', name: '', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Manual' }), _quickEntry: true }
}

function hasQuickEntryInformation(row: EditableBomRow) {
  return [row.drawingNumber, row.name, row.parentDrawingNumber, row.specification, row.remark, row.brand, row.material, row.surfaceTreatment, row.weight]
    .some(value => String(value ?? '').trim().length > 0)
}

function resetQuickEntry() {
  quickEntryRow.value = canShowQuickEntry.value ? createQuickEntryRow() : null
}

function rowSelectionKey(item: EditableBomRow) {
  return item.id ?? item._clientKey
}

async function completePendingMaterialLink() {
  const pending = pendingMaterialLink.value
  if (!pending || kind.value !== pending.kind || !props.projectId || !props.token) return
  const target = rows.value.find(row => row.id
    && row.sequence === pending.sequence
    && row.drawingNumber.trim().toLocaleLowerCase() === pending.materialCode.trim().toLocaleLowerCase())
  if (!target?.id) return
  pendingMaterialLink.value = null
  try {
    await linkBomMaterial(props.projectId, target.id, pending.materialId, props.token)
    ElMessage.success(`已引用料品 ${pending.materialCode}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '新增BOM已保存，但料品引用失败，请重新选择后重试')
  }
}

function refreshRows(preserveDrafts: boolean) {
  const drafts = preserveDrafts
    ? rows.value.flatMap((row, index) => row.id ? [] : [{ row, index }])
    : []
  const refreshed = sourceRows.value.map(withClientKey)
  drafts.forEach(({ row, index }) => refreshed.splice(Math.min(index, refreshed.length), 0, row))
  rows.value = refreshed
  resetQuickEntry()
  const available = new Set(rows.value.flatMap(item => {
    const key = rowSelectionKey(item)
    return key ? [key] : []
  }))
  selectedIds.value = selectedIds.value.filter(id => available.has(id))
}

watch(quickEntryRow, row => {
  if (!row || !row._quickEntry || !hasQuickEntryInformation(row) || promotingQuickEntry) return
  promotingQuickEntry = true
  row._quickEntry = false
  rows.value.push(row)
  resequence()
  quickEntryRow.value = createQuickEntryRow()
  promotingQuickEntry = false
}, { deep: true })

watch(kind, () => {
  if (pendingMaterialLink.value && pendingMaterialLink.value.kind !== kind.value) pendingMaterialLink.value = null
  clearFilters()
  versionSelectionTouched.value = false
  selectedVersionId.value = 'current'
  comparisonOpen.value = kind.value !== 'Source' && kind.value !== 'Overview' && !!comparisonBaseline.value
  acknowledgedPublishedRows = new Set()
  expandedStructurePaths.value = new Set()
  discardDraftsOnNextSourceRefresh = false
  refreshRows(false)
  if (!isOverviewView.value) void resolveMissingStandardMaterialCodes()
}, { immediate: true })
watch(() => props.projectId, projectId => {
  restoreDisplayMode(projectId)
  expandedStructurePaths.value = new Set()
  refreshRows(false)
}, { immediate: true })
watch(displayMode, mode => {
  if (typeof window !== 'undefined') window.localStorage.setItem(displayModeStorageKey(props.projectId), mode)
  selectedIds.value = []
  bomPage.value = 1
  expandedStructurePaths.value = new Set()
  refreshRows(false)
})
watch(categoryVersions, versions => {
  if (kind.value === 'Source' || kind.value === 'Overview' || versionSelectionTouched.value) return
  selectedVersionId.value = 'current'
  comparisonOpen.value = versions.some(version => version.state === 'Released') || !!latestPublishedComparisonPackage.value
}, { deep: true })
watch(selectedVersionId, () => {
  clearFilters()
  comparisonOpen.value = selectedVersionId.value === 'current' && !!comparisonBaseline.value
  acknowledgedPublishedRows = new Set()
  discardDraftsOnNextSourceRefresh = false
  refreshRows(false)
})
watch(() => props.baselines, baselines => {
  if (!selectedBaselineId.value && baselines.length) selectedBaselineId.value = baselines[0].id
}, { immediate: true })
watch([() => props.sourceData, () => props.standard, () => props.nonStandard, () => props.unclassified, () => props.electrical], () => {
  const restoringSource = restoreSourceRefreshIds.size > 0
  const discardingDrafts = discardDraftsOnNextSourceRefresh
  if (hasStagedEdits.value && !saveRequested && !restoringSource && !discardingDrafts) return
  if (restoringSource) {
    stagedEditKeys.value = new Set([...stagedEditKeys.value].filter(key => {
      const separator = key.lastIndexOf(':')
      return separator <= 0 || !restoreSourceRefreshIds.has(key.slice(0, separator))
    }))
  }
  refreshRows(!discardingDrafts)
  if (saveRequested || discardingDrafts) {
    stagedEditKeys.value = new Set()
    saveRequested = false
  }
  discardDraftsOnNextSourceRefresh = false
  if (restoringSource) restoreSourceRefreshIds = new Set()
  void completePendingMaterialLink()
  void resolveMissingStandardMaterialCodes()
}, { deep: true })
watch(() => props.pending, (pending, previous) => {
  if (previous && !pending) discardDraftsOnNextSourceRefresh = false
  if (previous && !pending && saveRequested && props.operationError) saveRequested = false
  if (previous && !pending && props.operationError) restoreSourceRefreshIds = new Set()
})
watch(hasStagedEdits, dirty => emit('dirtyChange', dirty), { immediate: true })
watch([searchQuery, kindFilter, brandFilter, materialFilter, showPendingOnly, comparisonFilter, bomPageSize], () => {
  selectedIds.value = []
  bomPage.value = 1
})
watch(() => displayEntries.value.length, () => {
  bomPage.value = Math.min(bomPage.value, bomPageCount.value)
})
watch(() => props.requestedReleasePackageId, releasePackageId => {
  if (!releasePackageId) return
  const release = props.releasePackages.find(item => item.id === releasePackageId)
  if (!release) return
  kind.value = release.scope.startsWith('Electrical') ? 'Electrical'
    : release.scope === 'NonStandardWithDrawing' ? 'NonStandard' : 'Standard'
  openReleaseDrawer(release.id)
  emit('releaseRequestHandled')
}, { immediate: true })
watch([() => props.projectId, () => props.projects.length], ([projectId, projectCount], previous) => {
  if (projectId && projectCount > 0 && (!previous || previous[0] !== projectId)) kind.value = 'Overview'
}, { immediate: true })
watch(() => props.releasePackages, (packages, previousPackages) => {
  if (!releaseDrawerOpen.value) return
  if (selectedReleasePackageId.value && packages.some(item => item.id === selectedReleasePackageId.value)) return
  if (!selectedReleasePackageId.value && packages.length <= (previousPackages?.length ?? 0)) return
  selectedReleasePackageId.value = categoryReleasePackages.value[0]?.id ?? ''
}, { deep: true })
watch(selectedReleasePackageId, () => {
  if (selectedReleaseVersionId.value) {
    selectedVersionId.value = selectedReleaseVersionId.value
    comparisonOpen.value = true
  }
})

function warnBeforeUnload(event: BeforeUnloadEvent) {
  if (!hasStagedEdits.value) return
  event.preventDefault()
  event.returnValue = ''
}

onMounted(() => window.addEventListener('beforeunload', warnBeforeUnload))
onBeforeUnmount(() => window.removeEventListener('beforeunload', warnBeforeUnload))

function addRow(afterIndex = rows.value.length - 1) {
  rows.value.splice(afterIndex + 1, 0, withClientKey({ sequence: afterIndex + 2, drawingNumber: '', name: '', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Manual' }))
  resequence()
}

let activeDragPointerId: number | null = null
let dragGhost: HTMLDivElement | null = null

function resetRowDrag() {
  document.removeEventListener('pointermove', moveRowPointerDrag)
  document.removeEventListener('pointerup', finishRowPointerDrag)
  document.removeEventListener('pointercancel', cancelRowPointerDrag)
  dragGhost?.remove()
  dragGhost = null
  activeDragPointerId = null
  draggedRowIndex.value = null
  dragOverRowIndex.value = null
  dragOverPosition.value = null
}

function startRowPointerDrag(index: number, event: PointerEvent) {
  if (props.pending || event.button !== 0) return
  event.preventDefault()
  activeDragPointerId = event.pointerId
  draggedRowIndex.value = index
  const row = (event.currentTarget as HTMLElement).closest('tr')
  dragGhost = document.createElement('div')
  dragGhost.className = 'pdm-bom-drag-ghost'
  dragGhost.textContent = `移动第 ${index + 1} 行 · ${rows.value[index]?.drawingNumber || '未编号'} · ${rows.value[index]?.name || '未命名'}`
  if (row) dragGhost.style.width = `${Math.min(row.getBoundingClientRect().width, 720)}px`
  document.body.appendChild(dragGhost)
  positionDragGhost(event)
  document.addEventListener('pointermove', moveRowPointerDrag, { passive: false })
  document.addEventListener('pointerup', finishRowPointerDrag)
  document.addEventListener('pointercancel', cancelRowPointerDrag)
}

function positionDragGhost(event: PointerEvent) {
  if (!dragGhost) return
  dragGhost.style.left = `${event.clientX + 14}px`
  dragGhost.style.top = `${event.clientY + 14}px`
}

function moveRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId !== event.pointerId || draggedRowIndex.value === null) return
  event.preventDefault()
  positionDragGhost(event)
  const targetRow = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLTableRowElement>('tr[data-row-index]')
  if (!targetRow) return
  const index = Number(targetRow.dataset.rowIndex)
  if (!Number.isInteger(index)) return
  const bounds = targetRow.getBoundingClientRect()
  dragOverRowIndex.value = index
  dragOverPosition.value = event.clientY < bounds.top + bounds.height / 2 ? 'before' : 'after'
}

async function applyRowDrop() {
  const source = draggedRowIndex.value
  const index = dragOverRowIndex.value
  const position = dragOverPosition.value
  resetRowDrag()
  if (source === null || index === null || position === null || source < 0 || source >= rows.value.length) return
  let target = index + (position === 'after' ? 1 : 0)
  if (source < target) target -= 1
  if (source === target) return
  const sourceRow = rows.value[source]
  if (sourceRow && !await ensurePublishedRowsAcknowledged([sourceRow], '调整顺序')) return
  const [row] = rows.value.splice(source, 1)
  rows.value.splice(target, 0, row)
  resequence()
}

function finishRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId !== event.pointerId) return
  void applyRowDrop()
}

function cancelRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId !== event.pointerId) return
  resetRowDrag()
}

onBeforeUnmount(resetRowDrag)

function removeDraftRow(index: number) {
  const row = rows.value[index]
  if (!row || row.id) return
  if (pendingMaterialLink.value?.clientKey === row._clientKey) pendingMaterialLink.value = null
  if (row._clientKey) selectedIds.value = selectedIds.value.filter(id => id !== row._clientKey)
  rows.value.splice(index, 1)
  resequence()
}

async function deleteItems(itemIds: string[]) {
  if (itemIds.length === 0 || (isSourceView.value && !canConfirmDeleteSelected.value)) return
  try {
    const deletingRows = rows.value.filter(row => row.id && itemIds.includes(row.id))
    const sourceCount = deletingRows.filter(row => row.sourceDocumentId).length
    const manualCount = deletingRows.length - sourceCount
    const publishedCount = deletingRows.filter(row => ['Released', 'Modified'].includes(comparisonRowStatus(row) ?? '')).length
    const response = await ElMessageBox.prompt(
      `共 ${itemIds.length} 条：有源 ${sourceCount} 条、人工 ${manualCount} 条${publishedCount ? `，其中 ${publishedCount} 条来自最近发布版` : ''}。删除后统一移入回收站，可恢复且不会改动已发布版本。`,
      '移入BOM回收站',
      {
        confirmButtonText: '确认移入', cancelButtonText: '取消', type: 'warning',
        customClass: 'pdm-bom-recycle-prompt',
        inputPlaceholder: '请输入删除原因（选填）',
      },
    )
    emit('batchDelete', itemIds, (response.value ?? '').trim())
    selectedIds.value = []
  } catch {
    // 用户取消时不修改BOM。
  }
}

function toggleRecycleBinRow(itemId: string | undefined, event: Event) {
  if (!itemId) return
  const checked = (event.target as HTMLInputElement).checked
  recycleBinSelectedIds.value = checked
    ? [...new Set([...recycleBinSelectedIds.value, itemId])]
    : recycleBinSelectedIds.value.filter(id => id !== itemId)
}

function toggleRecycleBinAll(event: Event) {
  recycleBinSelectedIds.value = (event.target as HTMLInputElement).checked
    ? recycleBinRows.value.flatMap(item => item.id ? [item.id] : [])
    : []
}

function formatDeletedAt(value?: string) {
  if (!value) return '历史数据'
  return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value))
}

function restoreRecycleBin() {
  if (recycleBinSelectedIds.value.length === 0) return
  emit('batchRestore', [...recycleBinSelectedIds.value], 'Original')
  recycleBinSelectedIds.value = []
}

async function retainSelected() {
  if (!canRetainSelected.value || props.pending) return
  const itemIds = retainableSelectedRows.value.map(row => row.id!)
  const count = itemIds.length
  try {
    await ElMessageBox.confirm(
      `确认保留所选 ${count} 条待处理BOM项吗？确认后这些物料不再显示为待处理。`,
      count > 1 ? '批量确认保留' : '确认保留',
      { confirmButtonText: count > 1 ? '批量确认保留' : '确认保留', cancelButtonText: '取消', type: 'warning' },
    )
    if (count === 1) emit('resolve', itemIds[0], 'retain')
    else emit('batchRetain', itemIds)
    selectedIds.value = []
  } catch {
    // 用户取消时保留当前选择，不修改BOM。
  }
}

async function confirmManualRetain(row: BomItem) {
  if (!row.id || !row.manualUnmatched || !canEditCurrentView.value || props.pending) return
  try {
    await ElMessageBox.confirm(
      `确认将“${row.drawingNumber || row.name}”作为人工BOM项保留吗？确认后该项不再显示为待处理。`,
      '确认保留人工BOM项',
      { confirmButtonText: '确认保留', cancelButtonText: '取消', type: 'warning' },
    )
    emit('resolve', row.id, 'retain')
  } catch {
    // 用户取消时不修改BOM。
  }
}

async function confirmManualDelete(row: BomItem) {
  if (!row.id || !row.manualUnmatched || !canEditCurrentView.value || props.pending) return
  await deleteItems([row.id])
}

async function classifySelected(targetKind: Exclude<BomClassification, 'Unclassified' | 'Electrical'>) {
  if (!canClassifySourceView.value || selectedIds.value.length === 0) return
  if (targetKind !== 'Virtual') {
    if (!props.projectId || !props.token) {
      emit('batchUpdate', { itemIds: selectedPersistedIds.value, fields: ['kind'], targetKind })
      selectedIds.value = []
      return
    }
    reclassifyPending.value = true
    try {
      reclassifyItemIds.value = [...selectedPersistedIds.value]
      reclassifyPreview.value = await previewBomSourceReclassification(props.projectId, reclassifyItemIds.value, targetKind, props.token)
      reclassifyPreviewOpen.value = true
    } catch (error) {
      ElMessage.error(error instanceof Error ? error.message : '重新归类预览失败。')
    } finally {
      reclassifyPending.value = false
    }
    return
  }
  if (selectedPersistedIds.value.length > selectedRows.value.length) {
    try {
      await ElMessageBox.confirm(
        `所选汇总物料对应 ${selectedPersistedIds.value.length} 个结构实例，将全部${targetKind === 'Virtual' ? '设为虚拟件并仅保留在源数据' : `归入${targetKind === 'Standard' ? '标准件' : '非标件'}BOM`}。是否继续？`,
        '确认同步归类',
        { confirmButtonText: '确认归类', cancelButtonText: '取消', type: 'warning' },
      )
    } catch {
      return
    }
  }
  emit('batchUpdate', { itemIds: selectedPersistedIds.value, fields: ['kind'], targetKind })
  selectedIds.value = []
}

function reclassifyKindLabel(value: BomClassification) {
  return value === 'Standard' ? '标准件' : value === 'NonStandard' ? '非标件' : value === 'Virtual' ? '虚拟件' : value === 'Electrical' ? '电气件' : '待分类'
}

async function confirmSourceReclassification() {
  if (!props.projectId || !props.token || !reclassifyPreview.value || reclassifyPending.value) return
  reclassifyPending.value = true
  try {
    await reclassifyBomItemsFromSource(props.projectId, reclassifyItemIds.value, reclassifyPreview.value.targetKind, props.token)
    ElMessage.success(`已重新归类并同步源数据 ${reclassifyItemIds.value.length} 条。`)
    reclassifyPreviewOpen.value = false
    reclassifyPreview.value = null
    reclassifyItemIds.value = []
    selectedIds.value = []
    emit('materialCodeChanged')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '重新归类失败，本次操作未写入。')
  } finally {
    reclassifyPending.value = false
  }
}

function selectImport() {
  fileInput.value?.click()
}

function importSelected(event: Event) {
  const target = event.target as HTMLInputElement
  const file = target.files?.[0]
  if (file && (kind.value === 'Standard' || kind.value === 'NonStandard' || kind.value === 'Electrical')) emit('import', kind.value, file)
  target.value = ''
}

function toggleRow(itemId: string | undefined, event: Event) {
  if (!itemId) return
  const checked = (event.target as HTMLInputElement).checked
  selectedIds.value = checked ? [...new Set([...selectedIds.value, itemId])] : selectedIds.value.filter(id => id !== itemId)
}

function toggleAllRows(event: Event) {
  const pageIds = new Set(selectableIds.value)
  selectedIds.value = (event.target as HTMLInputElement).checked
    ? [...new Set([...selectedIds.value, ...pageIds])]
    : selectedIds.value.filter(id => !pageIds.has(id))
}

async function ensurePublishedRowsAcknowledged(targetRows: EditableBomRow[], action: string) {
  const publishedRows = targetRows.filter(row => ['Released', 'Modified'].includes(comparisonRowStatus(row) ?? ''))
  const pendingRows = publishedRows.filter(row => !acknowledgedPublishedRows.has(comparisonRowReference(row)))
  if (!pendingRows.length) return true
  try {
    await ElMessageBox.confirm(
      `${action}将影响 ${pendingRows.length} 条最近发布版中的物料。修改只会进入当前工作版，不会改动已发布版本。是否继续？`,
      '已发布基线物料',
      { confirmButtonText: '继续操作', cancelButtonText: '取消', type: 'warning' },
    )
    pendingRows.forEach(row => acknowledgedPublishedRows.add(comparisonRowReference(row)))
    return true
  } catch {
    return false
  }
}

async function openBatchEditor() {
  if (selectedIds.value.length === 0 || hasSelectedDraftRows.value) return
  if (!await ensurePublishedRowsAcknowledged(selectedRows.value, '批量修改')) return
  batchDraft.value = createBatchDraft()
  batchValidation.value = ''
  batchOpen.value = true
}

async function beginInlineEdit(row: EditableBomRow, field: EditableBomField) {
  if (!canEditCurrentView.value || props.pending || !row.id) return
  if (field === 'quantity' && displayMode.value === 'Summary' && operationItemIds(row as EditableBomRow).length > 1) {
    ElMessage.warning('汇总数量来自多个结构实例，请切换到“按结构”后修改数量。')
    return
  }
  if (!await ensurePublishedRowsAcknowledged([row], `修改${editableFieldLabel(field)}`)) return
  const value = field === 'kind'
    ? row.pendingClassification ? '' : rowKind(row) ?? ''
    : field === 'quantity' ? row.quantity : row[field] ?? ''
  editingCell.value = { itemId: row.id, field, initialValue: value }
  inlineValue.value = value
}

function editableFieldLabel(field: EditableBomField) {
  return ({
    kind: '物料分类', drawingNumber: '物料编码', name: '物料名称', parentDrawingNumber: '上级物料编码', specification: '型号',
    remark: '备注信息', brand: '品牌', material: '材质', surfaceTreatment: '表面处理', quantity: '数量',
  } as Record<EditableBomField, string>)[field]
}

function isInlineEditing(row: BomItem, field: EditableBomField) {
  return !!row.id && editingCell.value?.itemId === row.id && editingCell.value?.field === field
}

function cancelInlineEdit() {
  editingCell.value = null
}

function materialLookupValue(value: string | null | undefined) {
  return value?.trim().toLocaleLowerCase() ?? ''
}

function setAutofillField(input: BatchUpdateBomItemsInput, field: string, value: string | number | undefined) {
  if (!input.fields.includes(field)) input.fields.push(field)
  if (field === 'unit') input.unit = String(value ?? '')
  if (field === 'drawingNumber') input.drawingNumber = String(value ?? '')
  if (field === 'name') input.name = String(value ?? '')
  if (field === 'specification') input.specification = String(value ?? '')
  if (field === 'remark') input.remark = String(value ?? '')
  if (field === 'brand') input.brand = String(value ?? '')
  if (field === 'material') input.material = String(value ?? '')
  if (field === 'surfaceTreatment') input.surfaceTreatment = String(value ?? '')
  if (field === 'weight') input.weight = value === null || value === undefined ? '' : String(value)
}

type MaterialLookupField = 'drawingNumber' | 'specification' | 'brand'

function applyMaterialAutofill(input: BatchUpdateBomItemsInput, materialItem: PdmMaterial, editedField: MaterialLookupField, editedValue: string) {
  if (materialItem.kind === 'Standard' || materialItem.kind === 'NonStandard') {
    input.fields.push('kind')
    input.targetKind = materialItem.kind
  }
  setAutofillField(input, 'unit', materialItem.unitCode)
  setAutofillField(input, 'drawingNumber', editedField === 'drawingNumber' ? editedValue : materialItem.materialCode)
  setAutofillField(input, 'name', materialItem.name)
  setAutofillField(input, 'specification', editedField === 'specification' ? editedValue : materialItem.specification ?? '')
  setAutofillField(input, 'remark', materialItem.remark ?? '')
  setAutofillField(input, 'brand', editedField === 'brand' ? editedValue : materialItem.brand ?? '')
  setAutofillField(input, 'material', materialItem.material ?? '')
  setAutofillField(input, 'surfaceTreatment', materialItem.surfaceTreatment ?? '')
  setAutofillField(input, 'weight', materialItem.weight ?? '')
}

async function findApprovedMaterialMatches(row: BomItem, field: MaterialLookupField, value: string) {
  const query = field === 'brand' ? row.specification?.trim() ?? '' : value.trim()
  if (!props.token || !query) return []
  const loaded = await listMaterials(props.token, query, false, 500)
  if (field === 'drawingNumber') {
    const code = materialLookupValue(value)
    return loaded.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && materialLookupValue(item.materialCode) === code)
  }
  const specification = materialLookupValue(field === 'specification' ? value : row.specification)
  const modelMatches = loaded.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && materialLookupValue(item.specification) === specification)
  if (modelMatches.length <= 1) return modelMatches
  const brand = materialLookupValue(field === 'brand' ? value : row.brand)
  return brand ? modelMatches.filter(item => materialLookupValue(item.brand) === brand) : modelMatches
}

function applyMaterialToDraft(row: BomItem, materialItem: PdmMaterial) {
  if (materialItem.kind === 'Standard' || materialItem.kind === 'NonStandard') row.kind = materialItem.kind
  row.unit = materialItem.unitCode
  row.drawingNumber = materialItem.materialCode
  row.name = materialItem.name
  row.specification = materialItem.specification ?? ''
  row.remark = materialItem.remark ?? ''
  row.brand = materialItem.brand ?? ''
  row.material = materialItem.material ?? ''
  row.surfaceTreatment = materialItem.surfaceTreatment ?? ''
  row.weight = materialItem.weight === null || materialItem.weight === undefined ? undefined : String(materialItem.weight)
}

function reusableBomRow(row: BomItem, field: MaterialLookupField, value: string) {
  if (kind.value !== 'Standard' || (field !== 'specification' && field !== 'brand')) return undefined
  const specification = materialLookupValue(field === 'specification' ? value : row.specification)
  if (!specification) return undefined
  const brand = materialLookupValue(field === 'brand' ? value : row.brand)
  let matches = rows.value.filter(candidate => candidate !== row
    && !candidate.manuallyExcluded
    && materialLookupValue(candidate.specification) === specification)
  if (brand) matches = matches.filter(candidate => materialLookupValue(candidate.brand) === brand)
  else if (new Set(matches.map(candidate => materialLookupValue(candidate.brand))).size > 1) return undefined
  const codes = [...new Set(matches.map(candidate => materialLookupValue(candidate.drawingNumber)).filter(Boolean))]
  if (codes.length > 1) return undefined
  return matches.find(candidate => !!candidate.drawingNumber.trim()) ?? matches[0]
}

function applyBomRowAutofill(input: BatchUpdateBomItemsInput, match: BomItem, field: MaterialLookupField, value: string) {
  setAutofillField(input, 'unit', match.unit)
  if (match.drawingNumber.trim()) setAutofillField(input, 'drawingNumber', match.drawingNumber)
  setAutofillField(input, 'name', match.name)
  setAutofillField(input, 'specification', field === 'specification' ? value : match.specification ?? '')
  setAutofillField(input, 'remark', match.remark ?? '')
  setAutofillField(input, 'brand', field === 'brand' ? value : match.brand ?? '')
  setAutofillField(input, 'material', match.material ?? '')
  setAutofillField(input, 'surfaceTreatment', match.surfaceTreatment ?? '')
  setAutofillField(input, 'weight', match.weight ?? '')
}

function applyBomRowToDraft(row: BomItem, match: BomItem) {
  row.unit = match.unit
  row.drawingNumber = match.drawingNumber
  row.name = match.name
  row.specification = match.specification ?? ''
  row.remark = match.remark ?? ''
  row.brand = match.brand ?? ''
  row.material = match.material ?? ''
  row.surfaceTreatment = match.surfaceTreatment ?? ''
  row.weight = match.weight
}

function materialMatchWarning(field: MaterialLookupField, value: string, count: number) {
  if (field === 'specification' && count > 1) return `型号“${value.trim()}”匹配到 ${count} 个料品，请输入品牌后自动核对`
  if (field === 'brand' && count > 1) return `型号和品牌仍匹配到 ${count} 个料品，请核对料品主档`
  return `${field === 'drawingNumber' ? '物料编码' : '型号'}“${value.trim()}”匹配到 ${count} 个料品，未自动回填其他信息`
}

async function autofillFromMaterialMaster(row: BomItem, input: BatchUpdateBomItemsInput, field: EditableBomField, value: string) {
  if ((field !== 'drawingNumber' && field !== 'specification' && field !== 'brand') || !value.trim()) return
  const reusable = reusableBomRow(row, field, value)
  if (reusable) {
    applyBomRowAutofill(input, reusable, field, value.trim())
    ElMessage.success(reusable.drawingNumber.trim()
      ? `已自动关联同型号料品 ${reusable.drawingNumber}`
      : '已自动关联同型号BOM物料，料号申请将共用一次')
    return
  }
  try {
    const matches = await findApprovedMaterialMatches(row, field, value)
    if (matches.length === 1) {
      applyMaterialAutofill(input, matches[0], field, value.trim())
      ElMessage.success(`已按${field === 'drawingNumber' ? '物料编码' : field === 'brand' ? '型号和品牌' : '型号'}匹配料品 ${matches[0].materialCode}，并自动补齐其他信息`)
    } else if (matches.length > 1) {
      ElMessage.warning(materialMatchWarning(field, value, matches.length))
    } else {
      ElMessage.warning(field === 'brand'
        ? '料品主档中未找到匹配的型号和品牌，已保留手工输入'
        : `料品主档中未找到${field === 'drawingNumber' ? '物料编码' : '型号'}“${value.trim()}”，已保留手工输入`)
    }
  } catch (error) {
    ElMessage.warning(error instanceof Error ? `${error.message}；已仅保存手工输入` : '料品主档查询失败；已仅保存手工输入')
  }
}

async function autofillDraftFromMaterialMaster(row: BomItem, field: MaterialLookupField) {
  const value = String(row[field] ?? '').trim()
  if (!value) return
  const rowKey = rowSelectionKey(row)
  if ((field === 'specification' || field === 'brand') && rowKey && autoLinkedDraftRows.has(rowKey)) {
    row.drawingNumber = ''
    autoLinkedDraftRows.delete(rowKey)
  }
  if (field === 'drawingNumber' && rowKey) autoLinkedDraftRows.delete(rowKey)
  const reusable = reusableBomRow(row, field, value)
  if (reusable) {
    applyBomRowToDraft(row, reusable)
    ElMessage.success(reusable.drawingNumber.trim()
      ? `已自动关联同型号料品 ${reusable.drawingNumber}`
      : '已自动关联同型号BOM物料，料号申请将共用一次')
    return
  }
  try {
    const matches = await findApprovedMaterialMatches(row, field, value)
    if (matches.length === 1) {
      applyMaterialToDraft(row, matches[0])
      if (rowKey) autoLinkedDraftRows.add(rowKey)
      ElMessage.success(`已自动关联料品 ${matches[0].materialCode}`)
    } else if (matches.length > 1) {
      ElMessage.warning(materialMatchWarning(field, value, matches.length))
    }
  } catch (error) {
    ElMessage.warning(error instanceof Error ? `${error.message}；已保留手工输入` : '料品主档查询失败；已保留手工输入')
  }
}

async function commitInlineEdit(row: BomItem) {
  const edit = editingCell.value
  if (!edit || !row.id || edit.itemId !== row.id) return
  const value = inlineValue.value
  editingCell.value = null
  const unchanged = edit.field === 'quantity'
    ? Number(edit.initialValue) === Number(value)
    : String(edit.initialValue ?? '') === String(value ?? '')
  if (unchanged) return
  const input: BatchUpdateBomItemsInput = { itemIds: operationItemIds(row as EditableBomRow), fields: [edit.field] }
  if (edit.field === 'kind') input.targetKind = value as BomClassification
  if (edit.field === 'drawingNumber') input.drawingNumber = String(value)
  if (edit.field === 'name') input.name = String(value)
  if (edit.field === 'parentDrawingNumber') input.parentDrawingNumber = String(value)
  if (edit.field === 'specification') input.specification = String(value)
  if (edit.field === 'remark') input.remark = String(value)
  if (edit.field === 'brand') input.brand = String(value)
  if (edit.field === 'material') input.material = String(value)
  if (edit.field === 'surfaceTreatment') input.surfaceTreatment = String(value)
  if (edit.field === 'quantity') input.quantity = Number(value)
  if ((edit.field === 'specification' && materialLookupValue(row.specification) !== materialLookupValue(String(value)))
    || (edit.field === 'brand' && materialLookupValue(row.brand) !== materialLookupValue(String(value)))) {
    setAutofillField(input, 'drawingNumber', '')
  }
  await autofillFromMaterialMaster(row, input, edit.field, String(value))
  if (edit.field === 'kind') emit('batchUpdate', input)
  else applyBatchInputToRows(input)
}

async function submitBatchUpdate() {
  const draft = batchDraft.value
  const input: BatchUpdateBomItemsInput = { itemIds: selectedPersistedIds.value, fields: [] }
  if (draft.kindEnabled) { input.fields.push('kind'); input.targetKind = draft.targetKind }
  if (draft.unitEnabled) { input.fields.push('unit'); input.unit = draft.unit }
  if (draft.drawingNumberEnabled) { input.fields.push('drawingNumber'); input.drawingNumber = draft.drawingNumber }
  if (draft.nameEnabled) { input.fields.push('name'); input.name = draft.name }
  if (draft.specificationEnabled) { input.fields.push('specification'); input.specification = draft.specification }
  if (draft.remarkEnabled) { input.fields.push('remark'); input.remark = draft.remark }
  if (draft.brandEnabled) { input.fields.push('brand'); input.brand = draft.brand }
  if (draft.materialEnabled) { input.fields.push('material'); input.material = draft.material }
  if (draft.surfaceTreatmentEnabled) { input.fields.push('surfaceTreatment'); input.surfaceTreatment = draft.surfaceTreatment }
  if (draft.weightEnabled) { input.fields.push('weight'); input.weight = draft.weight }
  if (draft.quantityEnabled) { input.fields.push('quantity'); input.quantity = draft.quantity }
  if (draft.revisionEnabled) { input.fields.push('revision'); input.revision = draft.revision }
  if (input.fields.length === 0) {
    batchValidation.value = '请勾选至少一个要批量修改的属性。'
    return
  }
  if (draft.kindEnabled) emit('batchUpdate', input)
  else applyBatchInputToRows(input)
  batchOpen.value = false
  selectedIds.value = []
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel pdm-bom-manager-panel" aria-label="BOM维护">
    <input ref="fileInput" class="pdm-visually-hidden" type="file" accept=".xlsx" @change="importSelected">
    <div class="pdm-bom-detail-toolbar">
      <div class="pdm-segmented" role="tablist">
        <button v-if="project && projects.length" type="button" role="tab" class="pdm-bom-overview-tab" :aria-selected="kind === 'Overview'" @click="requestKind('Overview')">多级总览</button>
        <button type="button" role="tab" class="pdm-source-data-tab" :aria-selected="kind === 'Source'" @click="requestKind('Source')">源数据（{{ sourceDataTotalCount }}）</button>
        <button type="button" role="tab" :aria-selected="kind === 'Standard'" @click="requestKind('Standard')">标准件BOM（{{ standardSummaryRows.length }}）</button>
        <button type="button" role="tab" :aria-selected="kind === 'NonStandard'" @click="requestKind('NonStandard')">非标件BOM（{{ nonStandardSummaryRows.length }}）</button>
        <button type="button" role="tab" :aria-selected="kind === 'Electrical'" @click="requestKind('Electrical')">电气BOM（{{ electricalSummaryRows.length }}）</button>
        <span v-if="unresolvedCount" class="pdm-bom-unresolved-count">待处理 {{ unresolvedCount }}</span>
      </div>
      <div v-if="!isOverviewView" class="pdm-bom-display-control" role="group" aria-label="BOM显示方式">
        <small>实例 {{ rawSourceRows.length }} · 汇总 {{ summaryRowCount }}</small>
        <button type="button" :class="{ 'is-active': displayMode === 'Summary' }" :aria-pressed="displayMode === 'Summary'" @click="requestDisplayMode('Summary')">按汇总</button>
        <button type="button" :class="{ 'is-active': displayMode === 'Structure' }" :aria-pressed="displayMode === 'Structure'" @click="requestDisplayMode('Structure')">按结构</button>
      </div>
      <div v-if="isSourceView" class="pdm-bom-detail-actions">
        <div class="pdm-manager-actions">
          <span v-if="editable && reconciliationReminderCount" class="pdm-bom-reconcile-hint" role="status" aria-live="polite">源数据更新 {{ reconciliationReminderCount }} 项</span>
          <button v-if="editable" type="button" class="pdm-secondary-action" :class="{ 'is-reconcile-needed': reconciliationReminderCount > 0 || quantityMismatchCount > 0 }" :aria-label="quantityMismatchCount ? `${reconcileActionText}，按最新设计树重新计算并保留人工维护字段` : reconciliationReminderCount ? `源数据更新 ${reconciliationReminderCount} 项，重新对账` : '重新对账'" :title="reconcileActionTitle" :disabled="pending" @click="requestGenerate">{{ reconcileActionText }}</button>
        </div>
      </div>
    </div>
    <BomHierarchyOverview v-if="project" v-show="isOverviewView" :project="project" :projects="projects" :token="token" :editable="editable" />
    <template v-if="!isOverviewView">
    <section v-if="!isSourceView" class="pdm-bom-release-strip" aria-label="当前BOM审批发布">
      <div>
        <small>当前工作版</small>
        <strong>{{ activeDraftVersion ? displayVersionLabel(activeDraftVersion.label) : '修改后创建下一工作版' }}</strong>
      </div>
      <div>
        <small>最近正式发布</small>
        <strong>{{ latestReleasedVersion ? displayVersionLabel(latestReleasedVersion.label) : '尚未发布' }}</strong>
      </div>
      <div>
        <small>在途发布包</small>
        <strong :class="{ 'is-active': activeReleasePackages.length }">{{ activeReleasePackages.length }} 个</strong>
      </div>
      <div>
        <small>发布历史</small>
        <strong>{{ categoryReleasePackages.length }} 个</strong>
      </div>
      <div class="pdm-bom-release-strip-actions">
        <span v-if="editable && reconciliationReminderCount" class="pdm-bom-reconcile-hint" role="status" aria-live="polite">源数据更新 {{ reconciliationReminderCount }} 项</span>
        <button v-if="canEditCurrentView" type="button" class="pdm-secondary-action" @click="selectImport">导入XLSX</button>
        <button type="button" class="pdm-secondary-action" @click="emit('export', kind as BomKind)">导出XLSX</button>
        <button v-if="editable" type="button" class="pdm-secondary-action" :class="{ 'is-reconcile-needed': reconciliationReminderCount > 0 || quantityMismatchCount > 0 }" :aria-label="quantityMismatchCount ? `${reconcileActionText}，按最新设计树重新计算并保留人工维护字段` : reconciliationReminderCount ? `源数据更新 ${reconciliationReminderCount} 项，重新对账` : '重新对账'" :title="reconcileActionTitle" :disabled="pending" @click="requestGenerate">{{ reconcileActionText }}</button>
        <button type="button" class="pdm-secondary-action" @click="openReleaseDrawer(activeReleasePackages[0]?.id || latestPublishedPackage?.id)">{{ activeReleasePackages.length ? `处理审批 ${activeReleasePackages.length}` : '发布记录' }}</button>
        <span v-if="hasStagedEdits" class="pdm-bom-unsaved-count" role="status" aria-live="polite">未保存 {{ stagedEditCount }} 项</span>
        <button v-if="canEditCurrentView && hasStagedEdits" type="button" class="pdm-secondary-action" :disabled="pending" @click="discardStagedEdits">撤销修改</button>
        <button v-if="canEditCurrentView" type="button" class="pdm-primary-action pdm-bom-save-action" :disabled="pending" @click="saveCurrentBom">{{ pending ? '保存中…' : hasStagedEdits ? `保存BOM（${stagedEditCount}）` : '保存BOM' }}</button>
        <button v-if="canManageRelease" type="button" class="pdm-primary-action" @click="openReleaseDrawer()">发起发布</button>
      </div>
    </section>
    <div v-if="!isSourceView && baselines.length" class="pdm-bom-version-toolbar">
      <div v-if="baselines.length" class="pdm-bom-baseline-picker">
        <label>制造基线
          <select v-model="selectedBaselineId" aria-label="选择制造BOM基线">
            <option v-for="baseline in baselines" :key="baseline.id" :value="baseline.id">{{ baseline.label }}{{ baselineChangeLabel(baseline) }}</option>
          </select>
        </label>
        <span v-if="selectedBaseline">S {{ versionLabel(selectedBaseline.standardBomVersionId) }} · N {{ versionLabel(selectedBaseline.nonStandardBomVersionId) }} · E {{ versionLabel(selectedBaseline.electricalBomVersionId) }}</span>
      </div>
    </div>
    <div v-if="comparisonOpen && comparisonBaseline" class="pdm-bom-comparison-summary">
      <strong>{{ selectedVersionId === 'current' ? '当前工作区' : selectedVersion ? displayVersionLabel(selectedVersion.label) : '当前工作区' }} 对比最近发布版 {{ comparisonBaseline.label }}</strong>
      <div class="pdm-bom-comparison-filters" role="group" aria-label="筛选发布版差异">
        <button type="button" :class="{ 'is-active': comparisonFilter === 'All' }" @click="comparisonFilter = 'All'">全部 {{ comparison.released.length + comparison.added.length + comparison.modified.length }}</button>
        <button type="button" class="is-released" :class="{ 'is-active': comparisonFilter === 'Released' }" @click="comparisonFilter = 'Released'">已发布 {{ comparison.released.length }}</button>
        <button type="button" class="is-added" :class="{ 'is-active': comparisonFilter === 'Added' }" @click="comparisonFilter = 'Added'">新增 {{ comparison.added.length }}</button>
        <button type="button" class="is-modified" :class="{ 'is-active': comparisonFilter === 'Modified' }" @click="comparisonFilter = 'Modified'">修改 {{ comparison.modified.length }}</button>
        <button type="button" class="is-removed" :class="{ 'is-active': comparisonFilter === 'Removed' }" @click="comparisonFilter = 'Removed'">已移除 {{ comparison.removed.length }}</button>
      </div>
      <small v-if="comparison.removed.length">已移除：{{ comparison.removed.map(item => item.drawingNumber || item.name).join('、') }}</small>
    </div>
    <div v-if="canSelectCurrentView || !isSourceView" class="pdm-bom-selection-toolbar">
      <div class="pdm-bom-selection-actions">
        <template v-if="canClassifySourceView">
          <button type="button" class="pdm-secondary-action" :disabled="pending || reclassifyPending || selectedIds.length === 0" title="已分类物料也可重新归类，并同步最新图档源属性" @click="classifySelected('Standard')">归入标准件BOM</button>
          <button type="button" class="pdm-secondary-action" :disabled="pending || reclassifyPending || selectedIds.length === 0" title="已分类物料也可重新归类，并同步最新图档源属性" @click="classifySelected('NonStandard')">归入非标件BOM</button>
          <button type="button" class="pdm-secondary-action" :disabled="pending || selectedIds.length === 0" @click="classifySelected('Virtual')">设为虚拟件</button>
        </template>
        <template v-else-if="canEditCurrentView">
          <button type="button" class="pdm-secondary-action" :disabled="pending || selectedIds.length === 0 || hasSelectedDraftRows" :title="hasSelectedDraftRows ? '新增行请直接编辑表格字段' : ''" @click="openBatchEditor">{{ selectedIds.length > 1 ? '批量编辑' : '编辑' }}</button>
          <button v-if="!isSourceView" type="button" class="pdm-primary-action" :disabled="pending || selectedIds.length > 1 || !token || !projectId" @click="openMaterialReference">按编码引用料品</button>
          <button v-if="kind === 'Standard'" type="button" class="pdm-secondary-action" :disabled="pending || selectedStandardItemsWithoutCode.length === 0" @click="applyForMaterialCodes([...new Set(selectedStandardItemsWithoutCode.flatMap(operationItemIds))])">{{ selectedStandardItemsWithoutCode.length > 1 ? '批量申请料号' : '申请料号' }}</button>
          <button v-if="hasRetainableSelection" type="button" class="pdm-secondary-action" :disabled="pending || !canRetainSelected" :title="!canRetainSelected ? '仅支持同时保留人工待确认或待确认删除的物料' : ''" @click="retainSelected">{{ selectedIds.length > 1 ? '批量确认保留' : '确认保留' }}</button>
          <button v-if="!isSourceView || canConfirmDeleteSelected" type="button" class="pdm-secondary-action is-danger" :disabled="pending || selectedIds.length === 0 || hasSelectedDraftRows" @click="deleteItems(selectedPersistedIds)">{{ canConfirmDeleteSelected ? (selectedIds.length > 1 ? '批量确认删除' : '确认删除') : (selectedIds.length > 1 ? '批量删除' : '删除') }}</button>
          <button v-if="kind === 'Standard' || kind === 'NonStandard'" type="button" class="pdm-secondary-action" :disabled="pending || !canRestoreSourceSelected" :title="selectedIds.length > 0 && !canRestoreSourceSelected ? '仅支持恢复有图档来源的标准件或非标件' : '恢复最新图档源属性，保留分类与排序'" @click="restoreSelectedFromSource">恢复源数据</button>
        </template>
        <button v-if="editable" type="button" class="pdm-secondary-action" :disabled="pending" @click="recycleBinOpen = true">回收站（{{ recycleBinRows.length }}）</button>
        <label v-if="isSourceView" class="pdm-bom-pending-filter"><input v-model="showPendingOnly" type="checkbox" aria-label="仅显示待处理"><span>仅显示待处理</span></label>
        <span v-if="selectedIds.length" class="pdm-bom-selection-summary" role="status" aria-live="polite">已选择 {{ selectedIds.length }} 项</span>
      </div>
      <div class="pdm-bom-filters" role="search" aria-label="筛选BOM物料">
        <input v-model.trim="searchQuery" type="search" aria-label="搜索BOM物料" placeholder="搜索名称、编码或型号">
        <select v-model="kindFilter" aria-label="筛选物料分类"><option value="All">全部分类</option><option value="Standard">标准件</option><option value="NonStandard">非标件</option><option value="Electrical">电气件</option><option value="Virtual">虚拟件</option><option value="Unclassified">待分类</option></select>
        <select v-model="brandFilter" aria-label="筛选品牌"><option value="">全部品牌</option><option v-for="brand in brandOptions" :key="brand" :value="brand">{{ brand }}</option></select>
        <select v-model="materialFilter" aria-label="筛选材质"><option value="">全部材质</option><option v-for="material in materialOptions" :key="material" :value="material">{{ material }}</option></select>
        <button type="button" class="pdm-secondary-action" :disabled="!filtersActive" @click="clearFilters">清空筛选</button>
      </div>
      <div v-if="!isSourceView" class="pdm-bom-version-picker">
        <label>查看版本
          <select v-model="selectedVersionId" aria-label="选择BOM版本" @change="versionSelectionTouched = true">
            <option value="current">当前工作区{{ activeDraftVersion ? ` · ${displayVersionLabel(activeDraftVersion.label)} 工作中` : ' · 修改后创建下一工作版' }}</option>
            <option v-for="version in categoryVersions" :key="version.id" :value="version.id">{{ displayVersionLabel(version.label) }} · {{ versionStateLabel(version.state) }}{{ version.changeNumber ? ` · ${version.changeNumber}` : '' }}</option>
          </select>
        </label>
        <span v-if="selectedVersion" class="pdm-bom-version-state" :class="`is-${selectedVersion.state.toLocaleLowerCase()}`">{{ versionStateLabel(selectedVersion.state) }} · 只读</span>
        <button type="button" class="pdm-secondary-action" :disabled="!comparisonBaseline" @click="comparisonOpen = !comparisonOpen">{{ comparisonOpen ? '收起差异' : '对比最近发布版' }}</button>
      </div>
    </div>
    <div class="pdm-table-scroll">
      <table class="pdm-edit-table pdm-bom-table">
        <colgroup>
          <col class="is-select"><col class="is-row-actions"><col class="is-sequence"><col class="is-kind"><col class="is-unit"><col class="is-code">
          <col class="is-name"><col class="is-parent-code"><col class="is-model"><col class="is-remark"><col class="is-brand">
          <col class="is-material"><col class="is-surface"><col class="is-weight"><col class="is-quantity"><col class="is-drawing-audit">
          <col class="is-revision"><col class="is-source"><col class="is-data-status">
        </colgroup>
          <thead><tr><th><input type="checkbox" :aria-label="isSourceView ? '选择全部源数据物料' : '选择当前分类全部物料'" :checked="allRowsSelected" :disabled="!canSelectCurrentView || selectableIds.length === 0" @change="toggleAllRows"></th><th aria-label="行排序与插入操作"></th><th>序号</th><th>物料分类</th><th>单位</th><th>物料编码</th><th>物料名称</th><th>上级物料编码</th><th>型号</th><th>备注信息</th><th>品牌</th><th>材质</th><th>表面处理</th><th>重量</th><th>数量(BOM/源)</th><th class="pdm-bom-drawing-audit-header">图纸核对</th><th>版本</th><th class="pdm-bom-reconciliation-header">问题</th><th>资料状态</th></tr></thead>
        <tbody>
          <tr v-for="{ row, index, depth, hasChildren, expanded, structureKey } in pagedRows" :key="row.id || row._clientKey" :data-row-index="index" :title="comparisonRowTitle(row)" :class="{ 'is-quick-entry': row._quickEntry, 'is-pending-removal': row.pendingRemoval, 'is-bom-unresolved': !row._quickEntry && (rowNeedsClassification(row) || row.manualUnmatched), 'is-data-exception': !row._quickEntry && dataStatusIssues(row).length > 0, 'is-reconciliation-issue': !row._quickEntry && shouldShowReconciliation(row), 'is-release-unchanged': comparisonRowStatus(row) === 'Released', 'is-release-added': comparisonRowStatus(row) === 'Added', 'is-release-modified': comparisonRowStatus(row) === 'Modified', 'is-row-dragging': draggedRowIndex === index, 'is-drag-over-before': dragOverRowIndex === index && dragOverPosition === 'before', 'is-drag-over-after': dragOverRowIndex === index && dragOverPosition === 'after' }">
            <td><input v-if="!row._quickEntry" type="checkbox" aria-label="选择物料" :checked="selectedIds.includes(rowSelectionKey(row) ?? '')" :disabled="!canSelectCurrentView || !rowSelectionKey(row)" @change="toggleRow(rowSelectionKey(row), $event)"></td>
            <td>
              <span v-if="isSourceView" class="pdm-bom-classification-indicator" :class="rowIsClassified(row) ? 'is-classified' : 'is-unclassified'" :aria-label="rowIsClassified(row) ? '已归类' : '未归类'" :title="rowIsClassified(row) ? '已归类' : '未归类'">{{ rowIsClassified(row) ? '✓' : '!' }}</span>
              <div v-else-if="canEditCurrentView && !row._quickEntry && displayMode === 'Summary'" class="pdm-bom-row-actions">
                <span class="pdm-bom-row-drag-handle" :class="{ 'is-disabled': pending }" role="button" tabindex="0" :aria-label="`拖动第 ${index + 1} 行排序`" :title="`按住拖动第 ${index + 1} 行`" @pointerdown="startRowPointerDrag(index, $event)">⠿</span>
                <button type="button" class="pdm-bom-row-action pdm-bom-insert-button" :aria-label="`在第 ${index + 1} 行下方插入物料`" :title="`在第 ${index + 1} 行下方插入物料`" :disabled="pending" @click="addRow(index)">+</button>
                <button v-if="!row.id" type="button" class="pdm-bom-row-action pdm-bom-delete-draft-button" :aria-label="`删除未保存的第 ${index + 1} 行`" :title="`删除未保存的第 ${index + 1} 行`" :disabled="pending" @click="removeDraftRow(index)">×</button>
              </div>
              <span v-else-if="displayMode === 'Structure'" class="pdm-bom-structure-instance" :title="operationItemIds(row).length > 1 ? `此物料在结构中共 ${operationItemIds(row).length} 处；编辑将同步全部实例` : '结构实例'">↳</span>
            </td>
            <td><span v-if="!row._quickEntry" class="pdm-bom-sequence-value">{{ index + 1 }}</span></td>
            <td>
              <select v-if="isInlineEditing(row, 'kind')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑物料分类" autofocus @change="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit"><option v-if="row.pendingClassification" value="" disabled>请选择分类</option><option value="Standard">标准件</option><option value="NonStandard">非标件</option><option value="Electrical">电气件</option><option value="Virtual">虚拟件</option></select>
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit pdm-bom-kind" :data-kind="rowKind(row)" :class="{ 'is-warning': row.pendingClassification }" title="点击编辑分类" aria-label="编辑物料分类" @click="beginInlineEdit(row, 'kind')">{{ rowKindLabel(row) }}</button>
              <span v-else class="pdm-bom-kind" :data-kind="rowKind(row)" :class="{ 'is-warning': rowNeedsClassification(row) }">{{ rowKindLabel(row) }}</span>
            </td>
            <td><span class="pdm-bom-cell-value">{{ u9UnitName(row.unit || '001') }}</span></td>
            <td :class="{ 'pdm-bom-structure-code-cell': displayMode === 'Structure' }">
              <span v-if="displayMode === 'Structure'" class="pdm-bom-structure-indent" :style="{ '--pdm-bom-depth': depth }">
                <button v-if="hasChildren" type="button" class="pdm-bom-structure-toggle" :aria-label="`${expanded ? '折叠' : '展开'} ${row.drawingNumber || row.name}`" :aria-expanded="expanded" @click="toggleStructureRow(structureKey)">{{ expanded ? '−' : '+' }}</button>
                <span v-else class="pdm-bom-structure-spacer" aria-hidden="true"></span>
              </span>
              <input v-if="isInlineEditing(row, 'drawingNumber')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑物料编码" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <span v-else-if="kind === 'Standard' && row.id && materialCodeApplicationInProgress(row)" class="pdm-material-code-state is-pending">申请中</span>
              <button v-else-if="kind === 'Standard' && row.id && !row.drawingNumber.trim() && materialResolution(row)?.status === 'Ambiguous'" type="button" class="pdm-material-code-action is-review" @click="openMaterialCandidates(row)">匹配到 {{ materialResolution(row)?.candidates.length }} 个，请选择</button>
              <button v-else-if="kind === 'Standard' && row.id && !row.drawingNumber.trim() && materialResolution(row)?.status === 'NoMatch'" type="button" class="pdm-material-code-action" @click="applyForMaterialCodes([row.id])">申请料号</button>
              <span v-else-if="kind === 'Standard' && row.id && !row.drawingNumber.trim() && materialCodeResolving" class="pdm-material-code-state">核对中…</span>
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.drawingNumber || '点击编辑物料编码'" aria-label="编辑物料编码" @click="beginInlineEdit(row, 'drawingNumber')">{{ displayValue(row.drawingNumber) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.drawingNumber" required aria-label="物料编码" @blur="autofillDraftFromMaterialMaster(row, 'drawingNumber')">
              <span v-else class="pdm-bom-cell-value" :title="row.drawingNumber">{{ displayValue(row.drawingNumber) }}</span>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'name')" v-model="inlineValue" class="pdm-bom-inline-editor pdm-bom-name-input" aria-label="内联编辑物料名称" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit pdm-bom-name-value" :title="row.name || '点击编辑物料名称'" aria-label="编辑物料名称" @click="beginInlineEdit(row, 'name')">{{ displayValue(row.name) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.name" class="pdm-bom-name-input" required aria-label="物料名称" :title="row.name">
              <span v-else class="pdm-bom-cell-value pdm-bom-name-value" :title="row.name">{{ displayValue(row.name) }}</span>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'parentDrawingNumber')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑上级物料编码" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.parentDrawingNumber || '点击编辑上级物料编码'" aria-label="编辑上级物料编码" @click="beginInlineEdit(row, 'parentDrawingNumber')">{{ displayValue(row.parentDrawingNumber) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.parentDrawingNumber" aria-label="上级物料编码" :placeholder="row._quickEntry ? '' : '可选'">
              <span v-else class="pdm-bom-cell-value" :title="row.parentDrawingNumber">{{ displayValue(row.parentDrawingNumber) }}</span>
            </td>
            <td class="pdm-bom-model-cell">
              <div class="pdm-bom-model-value">
                <input v-if="isInlineEditing(row, 'specification')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑型号" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
                <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.specification || '点击编辑型号'" aria-label="编辑型号" @click="beginInlineEdit(row, 'specification')">{{ displayValue(row.specification) }}</button>
                <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.specification" aria-label="型号" @blur="autofillDraftFromMaterialMaster(row, 'specification')">
                <span v-else class="pdm-bom-cell-value" :title="row.specification">{{ displayValue(row.specification) }}</span>
                <span v-if="hasDrawingNameModelMismatch(row)" class="pdm-bom-drawing-name-warning" aria-label="图纸名称与BOM型号不一致" :title="drawingNameMismatchTitle(row)">⚠</span>
              </div>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'remark')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑备注信息" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.remark || '点击编辑备注信息'" aria-label="编辑备注信息" @click="beginInlineEdit(row, 'remark')">{{ displayValue(row.remark) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.remark" aria-label="备注信息">
              <span v-else class="pdm-bom-cell-value" :title="row.remark">{{ displayValue(row.remark) }}</span>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'brand')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑品牌" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.brand || '点击编辑品牌'" aria-label="编辑品牌" @click="beginInlineEdit(row, 'brand')">{{ displayValue(row.brand) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.brand" aria-label="品牌" @blur="autofillDraftFromMaterialMaster(row, 'brand')">
              <span v-else class="pdm-bom-cell-value" :title="row.brand">{{ displayValue(row.brand) }}</span>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'material')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑材质" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.material || '点击编辑材质'" aria-label="编辑材质" @click="beginInlineEdit(row, 'material')">{{ displayValue(row.material) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.material" aria-label="材质">
              <span v-else class="pdm-bom-cell-value" :title="row.material">{{ displayValue(row.material) }}</span>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'surfaceTreatment')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑表面处理" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.surfaceTreatment || '点击编辑表面处理'" aria-label="编辑表面处理" @click="beginInlineEdit(row, 'surfaceTreatment')">{{ displayValue(row.surfaceTreatment) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.surfaceTreatment" aria-label="表面处理">
              <span v-else class="pdm-bom-cell-value" :title="row.surfaceTreatment">{{ displayValue(row.surfaceTreatment) }}</span>
            </td>
            <td>
              <input v-if="!row.id && canEditCurrentView && !isSourceView" v-model.trim="row.weight" aria-label="重量">
              <span v-else class="pdm-bom-cell-value">{{ displayValue(row.weight) }}</span>
            </td>
            <td class="pdm-bom-quantity-audit" :class="!row._quickEntry && quantityAuditClass(row)">
              <template v-if="row._quickEntry"></template>
              <input v-if="isInlineEditing(row, 'quantity')" v-model.number="inlineValue" type="number" min="0.0001" step="0.0001" class="pdm-bom-inline-editor pdm-bom-quantity-editor" aria-label="内联编辑数量" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="!row._quickEntry && row.id && canEditCurrentView && !(displayMode === 'Summary' && operationItemIds(row).length > 1)" type="button" class="pdm-bom-cell-edit" :title="comparisonQuantityTitle(row)" aria-label="编辑数量" @click="beginInlineEdit(row, 'quantity')">{{ comparisonQuantityDisplay(row) }}</button>
              <input v-else-if="!row._quickEntry && canEditCurrentView && !isSourceView && !(displayMode === 'Summary' && operationItemIds(row).length > 1)" v-model.number="row.quantity" type="number" min="0.0001" step="0.0001" class="pdm-bom-quantity-editor" aria-label="数量">
              <span v-else-if="!row._quickEntry" class="pdm-bom-cell-value" :title="displayMode === 'Summary' && operationItemIds(row).length > 1 ? `${comparisonQuantityTitle(row)}；汇总数量来自多个结构实例，请切换到结构模式修改` : comparisonQuantityTitle(row)">{{ comparisonQuantityDisplay(row) }}</span>
            </td>
            <td class="pdm-bom-drawing-audit-cell">
              <div v-if="!row._quickEntry && drawingAudit(row)" class="pdm-bom-audit" :class="drawingAudit(row)!.blocking ? 'is-blocking' : 'is-matched'" :title="drawingAudit(row)!.detail"><strong>{{ drawingAudit(row)!.label }}</strong><small v-if="!drawingAudit(row)!.compact">{{ drawingAudit(row)!.detail }}</small></div>
              <span v-else-if="!row._quickEntry" class="pdm-bom-cell-value">—</span>
            </td>
            <td>
              <span v-if="row._quickEntry" class="pdm-bom-cell-value">{{ displayValue(row.revision) }}</span>
              <input v-else-if="!row.id && canEditCurrentView && !isSourceView" v-model.trim="row.revision" required aria-label="版本">
              <span v-else class="pdm-bom-cell-value">{{ displayValue(row.revision) }}</span>
            </td>
            <td class="pdm-bom-reconciliation-cell">
              <div v-if="!row._quickEntry && shouldShowReconciliation(row)" class="pdm-bom-reconciliation" :title="reconciliationDescription(row)">
                <span class="pdm-bom-source" :class="reconciliationIssueTone(row)">{{ reconciliationIssueLabel(row) }}</span>
                <div v-if="row.manualUnmatched && canEditCurrentView" class="pdm-bom-reconciliation-actions">
                  <button type="button" class="is-retain" :disabled="pending" :aria-label="`确认保留人工BOM项 ${row.drawingNumber || row.name}`" @click="confirmManualRetain(row)">保留</button>
                  <button type="button" class="is-delete" :disabled="pending" :aria-label="`确认删除人工BOM项 ${row.drawingNumber || row.name}`" @click="confirmManualDelete(row)">删除</button>
                </div>
              </div>
              <span v-else-if="!row._quickEntry" class="pdm-bom-cell-value">—</span>
            </td>
            <td v-if="row._quickEntry" class="pdm-bom-data-status-cell"><span class="pdm-bom-data-status">待录入</span></td>
            <td v-else class="pdm-bom-data-status-cell" :class="dataStatusIssues(row).length ? 'is-incomplete' : 'is-complete'"><span class="pdm-bom-data-status" :class="dataStatusIssues(row).length ? 'is-incomplete' : 'is-complete'" :title="dataStatusIssues(row).length ? `待完善：${dataStatusIssues(row).join('、')}` : '必填资料及料品主档校验均已通过'">{{ dataStatusLabel(row) }}</span></td>
          </tr>
          <tr v-if="filteredRows.length === 0 && !canShowQuickEntry"><td colspan="19" class="pdm-empty-info">{{ rows.length ? '没有符合当前筛选条件的物料。' : isSourceView ? '当前没有图档源数据。' : '当前BOM为空，系统自动按无此类物料处理；可新增物料或导入标准XLSX。' }}</td></tr>
        </tbody>
      </table>
    </div>
    <div v-if="displayEntries.length" class="pdm-bom-pagination" aria-label="BOM明细分页"><span>{{ displayMode === 'Structure' ? `结构实例 ${rawSourceRows.length} 条 · 当前层级 ${displayEntries.length} 条` : `共 ${displayEntries.length} 条` }}</span><select v-model.number="bomPageSize" aria-label="BOM明细每页条数"><option :value="30">30条/页</option><option :value="50">50条/页</option><option :value="100">100条/页</option><option :value="200">200条/页</option></select><button type="button" class="pdm-secondary-action" aria-label="BOM明细上一页" :disabled="bomPage <= 1" @click="bomPage -= 1">‹</button><span>{{ bomPage }} / {{ bomPageCount }}</span><button type="button" class="pdm-secondary-action" aria-label="BOM明细下一页" :disabled="bomPage >= bomPageCount" @click="bomPage += 1">›</button></div>

    <div v-if="materialReferenceOpen" class="pdm-dialog-backdrop" @click.self="materialReferenceOpen = false">
      <section class="pdm-bom-batch-dialog pdm-material-reference-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-material-reference-title">
        <header><div><h3 id="pdm-material-reference-title">从料品主档引用</h3><p>{{ materialReferenceReason || '可按物料编码、名称或规格搜索，并按品牌筛选；只显示与当前BOM分类一致的已批准料品。' }}</p></div><button type="button" class="pdm-icon-button" aria-label="关闭料品引用" @click="materialReferenceOpen = false">×</button></header>
        <div class="pdm-material-reference-search"><input v-model.trim="materialReferenceBrandInput" list="pdm-material-reference-brands" aria-label="筛选引用料品品牌" placeholder="输入或选择品牌" @keydown.enter.prevent="searchMaterialReferences"><datalist id="pdm-material-reference-brands"><option v-for="brand in materialReferenceBrandOptions" :key="brand" :value="brand" /></datalist><input v-model.trim="materialReferenceQuery" type="search" aria-label="搜索料品主档" placeholder="搜索物料编码、名称或规格" @keydown.enter.prevent="searchMaterialReferences"><button type="button" class="pdm-primary-action" :disabled="materialReferenceLoading" @click="searchMaterialReferences">{{ materialReferenceLoading ? '查询中…' : '查询' }}</button></div>
        <div class="pdm-material-reference-table pdm-table-scroll"><table class="pdm-edit-table"><thead><tr><th>物料编码</th><th>名称</th><th>分类</th><th>引用次数</th><th>规格</th><th>品牌</th><th>备注</th><th>同步</th><th></th></tr></thead><tbody><tr v-for="materialItem in pagedMaterialReferenceResults" :key="materialItem.id"><td>{{ materialItem.materialCode }}</td><td>{{ materialItem.name }}</td><td>{{ materialItem.categoryCode }}</td><td>{{ materialItem.referenceCount ?? 0 }}</td><td>{{ materialItem.specification || '—' }}</td><td>{{ materialItem.brand || '—' }}</td><td>{{ materialItem.remark || '—' }}</td><td>{{ materialItem.syncStatus === 'Succeeded' ? '已同步' : '待同步' }}</td><td><button type="button" class="pdm-secondary-action" @click="applyMaterialReference(materialItem)">引用</button></td></tr><tr v-if="!materialReferenceLoading && filteredMaterialReferenceResults.length === 0"><td colspan="9" class="pdm-empty-info">没有符合条件的已批准料品。</td></tr></tbody></table></div>
        <div v-if="filteredMaterialReferenceResults.length" class="pdm-material-reference-pagination"><span>共 {{ filteredMaterialReferenceResults.length }} 条</span><select v-model.number="materialReferencePageSize" aria-label="料品引用每页条数"><option :value="20">20条/页</option><option :value="50">50条/页</option><option :value="100">100条/页</option></select><button type="button" class="pdm-secondary-action" aria-label="料品引用上一页" :disabled="materialReferencePage <= 1" @click="materialReferencePage -= 1">‹</button><span>{{ materialReferencePage }} / {{ materialReferencePageCount }}</span><button type="button" class="pdm-secondary-action" aria-label="料品引用下一页" :disabled="materialReferencePage >= materialReferencePageCount" @click="materialReferencePage += 1">›</button></div>
      </section>
    </div>

    <div v-if="reclassifyPreviewOpen && reclassifyPreview" class="pdm-dialog-backdrop" @click.self="!reclassifyPending && (reclassifyPreviewOpen = false)">
      <section class="pdm-bom-batch-dialog pdm-bom-reclassify-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-bom-reclassify-title">
        <header>
          <div><h3 id="pdm-bom-reclassify-title">重新归类并同步源数据</h3><p>目标：{{ reclassifyKindLabel(reclassifyPreview.targetKind) }}BOM；共 {{ reclassifyPreview.itemCount }} 个结构实例，{{ reclassifyPreview.changedItemCount }} 项属性存在变化。</p></div>
          <button type="button" class="pdm-icon-button" aria-label="关闭重新归类预览" :disabled="reclassifyPending" @click="reclassifyPreviewOpen = false">×</button>
        </header>
        <div class="pdm-bom-reclassify-notice">执行后会同时更新分类及最新设计树中的单位、物料编码、名称、型号、备注、品牌、材质、表面处理、重量、数量和版本。正式 PLM/U9 料号保持不变。</div>
        <div class="pdm-table-scroll pdm-bom-reclassify-table">
          <table class="pdm-edit-table"><thead><tr><th>当前分类</th><th>目标分类</th><th>当前编码</th><th>源编码</th><th>结果编码</th><th>当前名称</th><th>源名称</th><th>变化属性</th></tr></thead><tbody>
            <tr v-for="item in reclassifyPreview.items" :key="item.itemId"><td>{{ reclassifyKindLabel(item.currentKind) }}</td><td>{{ reclassifyKindLabel(item.targetKind) }}</td><td>{{ item.currentDrawingNumber || '—' }}</td><td>{{ item.sourceDrawingNumber || '—' }}</td><td><strong>{{ item.resultDrawingNumber || '—' }}</strong><small v-if="item.officialMaterialCodeProtected" class="pdm-bom-code-protected">正式料号已保护</small></td><td>{{ item.currentName || '—' }}</td><td>{{ item.sourceName || '—' }}</td><td>{{ item.changedFields.length ? item.changedFields.join('、') : '仅分类确认' }}</td></tr>
          </tbody></table>
        </div>
        <footer><span>服务端会再次校验全部实例；任意一项失败时整批不写入。</span><button type="button" class="pdm-secondary-action" :disabled="reclassifyPending" @click="reclassifyPreviewOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="reclassifyPending" @click="confirmSourceReclassification">{{ reclassifyPending ? '处理中…' : '确认重新归类并同步' }}</button></footer>
      </section>
    </div>

    <div v-if="batchOpen" class="pdm-dialog-backdrop" @click.self="batchOpen = false">
      <section class="pdm-bom-batch-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-bom-batch-title">
        <header>
          <div><h3 id="pdm-bom-batch-title">批量编辑BOM属性</h3><p>已选 {{ selectedIds.length }} 项；只有左侧复选框勾选的属性会被修改。</p></div>
          <button type="button" class="pdm-icon-button" aria-label="关闭批量编辑" @click="batchOpen = false">×</button>
        </header>
        <div class="pdm-bom-batch-body">
          <div class="pdm-bom-batch-fields">
            <div v-if="kind !== 'Electrical'" class="pdm-bom-batch-field"><input v-model="batchDraft.kindEnabled" type="checkbox" aria-label="修改物料分类"><span>物料分类</span><select v-model="batchDraft.targetKind" :disabled="!batchDraft.kindEnabled"><option value="Standard">标准件</option><option value="NonStandard">非标件</option><option value="Virtual">虚拟件</option></select></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.unitEnabled" type="checkbox" aria-label="修改单位"><span>单位</span><select v-model="batchDraft.unit" :disabled="!batchDraft.unitEnabled"><option v-for="unit in u9UnitOptions" :key="unit.code" :value="unit.code">{{ unit.code }} {{ unit.name }}</option></select></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.drawingNumberEnabled" type="checkbox" aria-label="修改物料编码"><span>物料编码</span><input v-model.trim="batchDraft.drawingNumber" :disabled="!batchDraft.drawingNumberEnabled" placeholder="允许多行使用同一料号"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.nameEnabled" type="checkbox" aria-label="修改物料名称"><span>物料名称</span><input v-model.trim="batchDraft.name" :disabled="!batchDraft.nameEnabled" placeholder="必填"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.specificationEnabled" type="checkbox" aria-label="修改型号"><span>型号</span><input v-model.trim="batchDraft.specification" :disabled="!batchDraft.specificationEnabled" placeholder="勾选后留空即清空"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.remarkEnabled" type="checkbox" aria-label="修改备注信息"><span>备注信息</span><input v-model.trim="batchDraft.remark" :disabled="!batchDraft.remarkEnabled" placeholder="勾选后留空即清空"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.brandEnabled" type="checkbox" aria-label="修改品牌"><span>品牌</span><input v-model.trim="batchDraft.brand" :disabled="!batchDraft.brandEnabled" placeholder="勾选后留空即清空"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.materialEnabled" type="checkbox" aria-label="修改材质"><span>材质</span><input v-model.trim="batchDraft.material" :disabled="!batchDraft.materialEnabled" placeholder="勾选后留空即清空"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.surfaceTreatmentEnabled" type="checkbox" aria-label="修改表面处理"><span>表面处理</span><input v-model.trim="batchDraft.surfaceTreatment" :disabled="!batchDraft.surfaceTreatmentEnabled" placeholder="勾选后留空即清空"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.weightEnabled" type="checkbox" aria-label="修改重量"><span>重量</span><input v-model.trim="batchDraft.weight" :disabled="!batchDraft.weightEnabled" placeholder="勾选后留空即清空"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.quantityEnabled" type="checkbox" aria-label="修改数量"><span>数量</span><input v-model.number="batchDraft.quantity" type="number" min="0.0001" step="0.0001" :disabled="!batchDraft.quantityEnabled"></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.revisionEnabled" type="checkbox" aria-label="修改版本"><span>版本</span><input v-model.trim="batchDraft.revision" :disabled="!batchDraft.revisionEnabled" placeholder="必填"></div>
          </div>
          <section class="pdm-bom-batch-preview">
            <header><strong>修改范围</strong><span>{{ selectedRows.length }} 条物料 · {{ batchFieldCount }} 个属性</span></header>
            <table><thead><tr><th>物料编码</th><th>物料名称</th><th>当前分类</th></tr></thead><tbody><tr v-for="row in selectedRows" :key="row.id"><td>{{ row.drawingNumber }}</td><td>{{ row.name }}</td><td>{{ rowKindLabel(row) }}</td></tr></tbody></table>
          </section>
        </div>
        <p v-if="batchValidation" class="pdm-dialog-error">{{ batchValidation }}</p>
        <footer><span>服务端会先校验全部数据；任意一条失败时，本次修改全部取消。</span><button type="button" class="pdm-secondary-action" @click="batchOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submitBatchUpdate">确认批量修改</button></footer>
      </section>
    </div>

    <div v-if="recycleBinOpen" class="pdm-dialog-backdrop" @click.self="recycleBinOpen = false">
      <section class="pdm-bom-batch-dialog pdm-bom-recycle-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-bom-recycle-title">
        <header>
          <div><h3 id="pdm-bom-recycle-title">BOM回收站</h3><p>工作区删除项长期保留；恢复不会修改已发布BOM版本和制造基线。</p></div>
          <button type="button" class="pdm-icon-button" aria-label="关闭BOM回收站" @click="recycleBinOpen = false">×</button>
        </header>
        <div class="pdm-bom-recycle-summary">
          <span>共 {{ recycleBinRows.length }} 条</span>
          <span>有源 {{ recycleBinRows.filter(item => item.sourceDocumentId).length }} 条</span>
          <span>人工 {{ recycleBinRows.filter(item => !item.sourceDocumentId).length }} 条</span>
        </div>
        <div class="pdm-bom-recycle-table-wrap">
          <table class="pdm-bom-recycle-table">
            <thead><tr><th><input type="checkbox" aria-label="选择回收站全部物料" :checked="recycleBinAllSelected" @change="toggleRecycleBinAll"></th><th>来源</th><th>原分类</th><th>物料编码</th><th>名称</th><th>删除人</th><th>删除时间</th><th>删除原因</th></tr></thead>
            <tbody>
              <tr v-for="item in recycleBinRows" :key="item.id"><td><input type="checkbox" aria-label="选择回收站物料" :checked="!!item.id && recycleBinSelectedIds.includes(item.id)" @change="toggleRecycleBinRow(item.id, $event)"></td><td><span class="pdm-bom-recycle-source" :class="item.sourceDocumentId ? 'is-source' : 'is-manual'">{{ item.sourceDocumentId ? '有源' : '人工' }}</span></td><td>{{ item.kind === 'Standard' ? '标准件' : item.kind === 'NonStandard' ? '非标件' : item.kind === 'Electrical' ? '电气件' : '待分类' }}</td><td :title="item.drawingNumber">{{ item.drawingNumber }}</td><td :title="item.name">{{ item.name }}</td><td>{{ item.deletedBy || item.reconciliationUpdatedBy || '—' }}</td><td>{{ formatDeletedAt(item.deletedAt || item.reconciliationUpdatedAt) }}</td><td :title="item.deleteReason || item.reconciliationNote">{{ item.deleteReason || item.reconciliationNote || '历史排除数据' }}</td></tr>
              <tr v-if="recycleBinRows.length === 0"><td colspan="8" class="pdm-empty-info">回收站为空。</td></tr>
            </tbody>
          </table>
        </div>
        <footer class="pdm-bom-recycle-footer">
          <div class="pdm-bom-recycle-help">
            <span><strong>恢复后保持删除前的数据来源：</strong>有源数据仍关联原图档；人工添加数据仍保持人工。</span>
          </div>
          <button type="button" class="pdm-primary-action" :disabled="pending || recycleBinSelectedIds.length === 0" @click="restoreRecycleBin">恢复选中</button>
        </footer>
      </section>
    </div>

    <el-drawer v-model="releaseDrawerOpen" class="pdm-bom-release-drawer" :title="kind === 'Standard' ? '标准件BOM审批发布' : kind === 'NonStandard' ? '非标件BOM与图纸发布' : '电气BOM审批发布'" size="min(1080px, 92vw)" destroy-on-close>
      <div class="pdm-bom-release-workspace">
        <aside class="pdm-bom-release-history" aria-label="发布包记录">
          <header><strong>发布记录</strong></header>
          <button v-for="release in categoryReleasePackages" :key="release.id" type="button" :class="{ 'is-active': release.id === selectedReleasePackageId }" @click="selectReleasePackage(release.id)">
            <span><strong>{{ release.number }}</strong><small>{{ release.changeNumber && release.changeNumber !== release.number ? release.changeNumber : '—' }}</small></span>
            <em>{{ release.state }}</em>
          </button>
          <p v-if="!categoryReleasePackages.length">当前BOM尚无发布记录。</p>
        </aside>
        <ReleaseCenter
          :release-package="selectedReleasePackage"
          :standard-items="standardRows"
          :release-items="kind === 'Standard' ? standardRows : kind === 'NonStandard' ? nonStandardRows : electricalRows"
          :username="username"
          :token="token"
          :pending="pending"
          :progress="uploadProgress"
          :error="operationError"
          :can-manage="canManageRelease"
          :can-decide="canDecideApproval"
          :can-emergency-decide="canEmergencyDecide"
          :allowed-scopes="createReleaseScopes"
          :preferred-scope="preferredReleaseScope"
          :change-reason-types="releaseChangeReasonTypes"
          :long-lead-published-item-ids="publishedLongLeadItemIds"
          :previous-version-items="previousReleaseVersionItems"
          @create="emit('releaseCreate', $event)"
          @upload="(releasePackageId, file) => emit('releaseUpload', releasePackageId, file)"
          @submit="releasePackageId => emit('releaseSubmit', releasePackageId)"
          @withdraw="releasePackageId => emit('releaseWithdraw', releasePackageId)"
          @decide="(taskId, decision, comment) => emit('releaseDecide', taskId, decision, comment)"
          @transfer="(taskId, targetUsername, comment) => emit('releaseTransfer', taskId, targetUsername, comment)"
          @emergency-decide="(taskId, decision, reason) => emit('releaseEmergencyDecide', taskId, decision, reason)"
        />
      </div>
    </el-drawer>
    </template>
  </section>
</template>

<style scoped>
.pdm-bom-manager-panel .pdm-bom-kind[data-kind="Standard"]{color:#2563eb}
.pdm-bom-manager-panel .pdm-bom-kind[data-kind="NonStandard"]{color:#7c3aed}
.pdm-bom-manager-panel .pdm-bom-kind[data-kind="Electrical"]{color:#92400e}
.pdm-bom-manager-panel .pdm-bom-kind[data-kind="Virtual"]{color:#6b7280}
.pdm-bom-manager-panel{font-size:12px}.pdm-bom-manager-panel :deep(button),.pdm-bom-manager-panel :deep(input),.pdm-bom-manager-panel :deep(select),.pdm-bom-manager-panel :deep(textarea),.pdm-bom-manager-panel :deep(table),.pdm-bom-manager-panel :deep(label),.pdm-bom-manager-panel :deep(small),.pdm-bom-manager-panel :deep(strong){font-size:12px}
.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation){font-size:12px}
.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation-header),.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation-cell){text-align:center}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation-cell){overflow:visible;vertical-align:middle;white-space:normal}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation){min-width:0;overflow:visible;text-align:center;white-space:normal;overflow-wrap:anywhere;line-height:1.35}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation .pdm-bom-source){display:block;overflow:visible;text-overflow:clip;white-space:normal;overflow-wrap:anywhere}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation .pdm-bom-source.is-critical){color:var(--pdm-danger);font-weight:700}
.pdm-bom-manager-panel :deep(.pdm-bom-drawing-audit-header),.pdm-bom-manager-panel :deep(.pdm-bom-drawing-audit-cell){text-align:center;vertical-align:middle}.pdm-bom-manager-panel :deep(.pdm-bom-drawing-audit-cell .pdm-bom-audit){width:100%;align-items:center;justify-content:center;text-align:center}
.pdm-bom-manager-panel :deep(.pdm-bom-table){width:100%;max-width:100%;table-layout:fixed}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-select){width:28px}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-row-actions){width:72px}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-sequence),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-unit),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-weight),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-revision){width:2.43902439%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-kind),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-brand),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-material),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-surface),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-quantity),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-drawing-audit),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-data-status){width:4.87804878%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-code),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-name),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-parent-code){width:7.31707317%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-model){width:14.63414634%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-remark),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-source){width:9.75609756%}
.pdm-bom-display-control{display:flex;align-items:center;gap:3px;margin-left:auto;padding:2px 3px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff;white-space:nowrap}.pdm-bom-display-control>span{padding:0 4px;color:var(--pdm-muted);font-size:11px}.pdm-bom-display-control button{min-width:42px;height:24px;padding:0 8px;border:0;border-radius:4px;background:transparent;color:var(--pdm-muted);cursor:pointer}.pdm-bom-display-control button.is-active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);font-weight:700}.pdm-bom-display-control small{padding:0 5px;color:var(--pdm-muted);font-size:11px}.pdm-bom-structure-code-cell{white-space:nowrap}.pdm-bom-structure-indent{display:inline-flex;align-items:center;margin-left:calc(var(--pdm-bom-depth) * 15px);margin-right:3px;vertical-align:middle}.pdm-bom-structure-toggle,.pdm-bom-structure-spacer{display:inline-grid;width:18px;height:18px;place-items:center}.pdm-bom-structure-toggle{padding:0;border:1px solid var(--pdm-theme-accent-border);border-radius:3px;background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);font-size:13px;line-height:16px;cursor:pointer}.pdm-bom-structure-spacer::before{content:'·';color:#94a3b8}.pdm-bom-structure-instance{color:var(--pdm-theme-accent);font-weight:700}.pdm-bom-structure-summary{padding:8px 10px;border-top:1px solid var(--pdm-border);color:var(--pdm-muted);font-size:11px;text-align:right}
.pdm-bom-model-value{display:flex;min-width:0;align-items:center;gap:3px}.pdm-bom-model-value>:not(.pdm-bom-drawing-name-warning){min-width:0;flex:1}.pdm-bom-drawing-name-warning{display:inline-flex;flex:0 0 auto;align-items:center;justify-content:center;color:#d97706;font-size:13px;line-height:1;cursor:help}.pdm-bom-pagination{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding:8px 10px;color:var(--pdm-muted);font-size:11px}.pdm-bom-pagination select{height:28px;padding:0 24px 0 8px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;color:var(--pdm-text)}.pdm-bom-pagination .pdm-secondary-action{width:28px;min-width:28px;height:28px;min-height:28px;padding:0}
.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr:is(.is-data-exception,.is-reconciliation-issue)>td){background:#fffbeb}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr:is(.is-data-exception,.is-reconciliation-issue):hover>td){background:#fef3c7}
.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-unchanged:not(.is-data-exception):not(.is-reconciliation-issue)>td){background:#f0fdf4}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-unchanged:not(.is-data-exception):not(.is-reconciliation-issue):hover>td){background:#dcfce7}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-added:not(.is-data-exception):not(.is-reconciliation-issue)>td){background:#eff6ff}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-added:not(.is-data-exception):not(.is-reconciliation-issue):hover>td){background:#dbeafe}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-modified:not(.is-data-exception):not(.is-reconciliation-issue)>td){background:#fff7ed}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-modified:not(.is-data-exception):not(.is-reconciliation-issue):hover>td){background:#ffedd5}
.pdm-bom-comparison-filters{display:flex;align-items:center;gap:5px;flex-wrap:wrap}.pdm-bom-comparison-filters button{min-height:24px;padding:2px 8px;border:1px solid var(--pdm-border);border-radius:999px;background:#fff;color:var(--pdm-muted);font-size:10px;cursor:pointer}.pdm-bom-comparison-filters button.is-active{border-color:var(--pdm-blue);box-shadow:0 0 0 1px var(--pdm-blue);color:var(--pdm-text);font-weight:700}.pdm-bom-comparison-filters button.is-released{background:#f0fdf4;color:#15803d}.pdm-bom-comparison-filters button.is-added{background:#eff6ff;color:#2563eb}.pdm-bom-comparison-filters button.is-modified{background:#fff7ed;color:#c2410c}.pdm-bom-comparison-filters button.is-removed{background:#fef2f2;color:#b91c1c}
.pdm-bom-release-strip{display:flex;align-items:center;gap:12px;padding:8px 11px;border:1px solid #bfdbfe;border-radius:7px;background:#eff6ff;font-size:12px;white-space:nowrap}.pdm-bom-release-strip>div{display:flex;align-items:center;gap:5px;white-space:nowrap}.pdm-bom-release-strip small,.pdm-bom-release-strip strong{font-size:12px;line-height:1.2;white-space:nowrap}.pdm-bom-release-strip small{color:var(--pdm-muted)}.pdm-bom-release-strip strong.is-active{color:#b45309}.pdm-bom-release-strip-actions{display:flex!important;align-items:center;gap:6px;margin-left:auto}.pdm-bom-release-strip-actions button{box-sizing:border-box;width:70px;min-width:70px;height:28px;min-height:28px;padding:4px 10px;font-size:12px;line-height:18px;white-space:nowrap}.pdm-bom-release-workspace{display:grid;grid-template-columns:210px minmax(0,1fr);gap:12px;min-height:100%}.pdm-bom-release-history{border:1px solid var(--pdm-border);border-radius:7px;overflow:auto;background:#fff}.pdm-bom-release-history header{display:flex;align-items:center;justify-content:space-between;padding:10px;border-bottom:1px solid var(--pdm-border)}.pdm-bom-release-history>button{display:flex;width:100%;justify-content:space-between;gap:8px;padding:10px;border:0;border-bottom:1px solid var(--pdm-border);background:#fff;text-align:left;color:var(--pdm-text)}.pdm-bom-release-history>button:hover,.pdm-bom-release-history>button.is-active{background:var(--pdm-blue-soft)}.pdm-bom-release-history>button span{display:grid;gap:3px;min-width:0}.pdm-bom-release-history>button small{overflow:hidden;text-overflow:ellipsis;color:var(--pdm-muted)}.pdm-bom-release-history>button em{font-style:normal;color:var(--pdm-blue);white-space:nowrap}.pdm-bom-release-history>p{padding:12px;color:var(--pdm-muted)}.pdm-bom-release-workspace .release-center{min-width:0;margin:0}@media(max-width:900px){.pdm-bom-release-strip{align-items:flex-start;flex-wrap:wrap}.pdm-bom-release-strip-actions{margin-left:0}.pdm-bom-release-workspace{grid-template-columns:1fr}.pdm-bom-release-history{max-height:180px}}
.pdm-bom-release-strip{box-sizing:border-box;height:38px;min-height:38px;padding-block:4px;background:#fff}
.pdm-bom-unsaved-count{padding:3px 7px;border:1px solid #f59e0b;border-radius:999px;background:#fffbeb;color:#b45309;font-weight:700;white-space:nowrap}
@media(max-width:900px){.pdm-bom-release-strip{height:auto}}
</style>
<style scoped>
.pdm-material-code-action{height:22px;padding:0 7px;border:1px solid var(--shell-accent-border);border-radius:5px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:11px;line-height:20px;white-space:nowrap;cursor:pointer}.pdm-material-code-action.is-review{border-color:#f59e0b;background:#fffbeb;color:#b45309}.pdm-material-code-state{color:#64748b;font-size:11px;white-space:nowrap}.pdm-material-code-state.is-pending{color:#b45309}
</style>
