<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { applyForBomMaterialCodes, applyMaterialRelations, getMaterialRelationCompleteness, linkBomMaterial, listMaterials, previewBomSourceReclassification, reclassifyBomItemsFromSource, resolveBomMaterialCodes } from '../api'
import type { BatchUpdateBomItemsInput, BomClassification, BomEmptyDeclaration, BomExportMode, BomGenerationResult, BomItem, BomKind, BomSourceReclassificationPreview, BomValidationField, BomValidationRules, BomVersion, CreateReleasePackageInput, DocumentModelDrawingRelation, DocumentNode, ManagedDocument, ManufacturingBomBaseline, MaterialCodeResolution, MaterialRelationCompleteness, MaterialRelationGroupCheck, PdmMaterial, ProjectSummary, ReleasePackageSummary, ReleaseScope, UpdateReleasePackageDraftInput } from '../types'
import { u9UnitName, u9UnitOptions } from '../u9Units'
import BomHierarchyOverview from './BomHierarchyOverview.vue'
import ReleaseCenter from './ReleaseCenter.vue'

type BomView = 'Overview' | 'Source' | BomKind
type BomDisplayMode = 'Summary' | 'Structure'
type BomKindFilter = 'All' | BomClassification
type BomComparisonFilter = 'All' | 'Released' | 'Added' | 'Modified' | 'Removed'
type BomComparisonStatus = Exclude<BomComparisonFilter, 'All' | 'Removed'>
type EditableBomField = 'kind' | 'drawingNumber' | 'name' | 'parentDrawingNumber' | 'specification' | 'remark' | 'brand' | 'material' | 'surfaceTreatment' | 'quantity'
type EditableBomRow = BomItem & { _clientKey?: string; _quickEntry?: boolean; _sourceItemIds?: string[]; _sourceKinds?: BomClassification[] }
type BomRowEntry = { row: EditableBomRow; index: number; depth: number; hasChildren: boolean; expanded: boolean; structureKey: string }
type PendingMaterialLink = { kind: BomKind; materialId: string; materialCode: string; sequence: number; clientKey: string }
type MaterialMatchResult = { candidates: PdmMaterial[]; duplicateDetected: boolean }
type BomComparisonChange = { field: string; label: string; previous: string; current: string }
type BomComparisonEntry = { status: BomComparisonStatus; current: EditableBomRow; previous?: BomItem; changes: BomComparisonChange[] }
type BomIssueComparison = { field: string; sourceLabel: string; source: string; currentLabel: string; current: string }
type ReconciliationChangeType = 'Added' | 'Quantity' | 'Attribute' | 'Classification' | 'Manual' | 'Removed'
type ReconciliationDetail = { key: string; groupKey: string; type: ReconciliationChangeType; item: string; field: string; current: string; source: string; proposed: string; action: string; instances: number }
type SummaryQuantityAllocation = Record<string, number>
type SummaryQuantityCandidate = { item: BomItem; quantity: number | string }
type SummaryParentAssembly = Pick<DocumentNode, 'id' | 'drawingNumber' | 'name'>

const props = withDefaults(defineProps<{
  sourceData?: BomItem[]
  standard: BomItem[]
  nonStandard: BomItem[]
  unclassified?: BomItem[]
  electrical: BomItem[]
  referenceRoot?: DocumentNode
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
  previewReconciliation?: () => Promise<BomGenerationResult>
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
  export: [kind: BomKind, mode: BomExportMode]
  generate: [discardUnsavedChanges: boolean]
  resolve: [itemId: string, action: 'classify' | 'retain' | 'remove', targetKind?: BomKind]
  batchRetain: [itemIds: string[]]
  batchUpdate: [input: BatchUpdateBomItemsInput]
  batchDelete: [itemIds: string[], reason: string]
  batchRestore: [itemIds: string[], mode: 'Original' | 'AsManual']
  releaseExclusion: [itemIds: string[], excluded: boolean, reason: string]
  restoreSource: [itemIds: string[]]
  releaseCreate: [input: CreateReleasePackageInput]
  releaseUpdateDraft: [releasePackageId: string, input: UpdateReleasePackageDraftInput]
  releaseDeleteDraft: [releasePackageId: string]
  releaseUpload: [releasePackageId: string, file: File]
  releaseSubmit: [releasePackageId: string]
  releaseWithdraw: [releasePackageId: string]
  releaseRetryU9: [releasePackageId: string]
  releaseDecide: [taskId: string, decision: 'Approved' | 'Rejected', comment: string]
  releaseTransfer: [taskId: string, targetUsername: string, comment: string]
  releaseEmergencyDecide: [taskId: string, decision: 'Approved' | 'Rejected', reason: string]
  releaseRequestHandled: []
  materialCodeChanged: []
  materialRelationsApplied: []
  dirtyChange: [dirty: boolean]
}>()
const kind = ref<BomView>('Source')
const displayMode = ref<BomDisplayMode>('Summary')
const exportDialogOpen = ref(false)
const exportMode = ref<BomExportMode>('Summary')
const exportKind = ref<BomKind>('Standard')
const expandedStructurePaths = ref(new Set<string>())
const selectedVersionId = ref('current')
const versionSelectionTouched = ref(false)
const selectedBaselineId = ref('')
const comparisonOpen = ref(false)
const comparisonFilter = ref<BomComparisonFilter>('All')
const issueDetailRow = ref<EditableBomRow | null>(null)
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
const duplicateMaterialChoiceOpen = ref(false)
const duplicateMaterialChoiceCandidates = ref<PdmMaterial[]>([])
const duplicateMaterialChoiceReason = ref('')
const summaryQuantityChoiceOpen = ref(false)
const summaryQuantityChoiceMaterial = ref<EditableBomRow | null>(null)
const summaryQuantityChoiceCandidates = ref<SummaryQuantityCandidate[]>([])
const summaryQuantityChoiceCurrentTotal = ref(0)
const summaryQuantityChoiceTargetQuantity = ref(0)
const pendingMaterialLink = ref<PendingMaterialLink | null>(null)
const materialCodeResolutions = ref<Record<string, MaterialCodeResolution>>({})
const materialCodeResolving = ref(false)
const relationCompleteness = ref<MaterialRelationCompleteness | null>(null)
const relationDialogOpen = ref(false)
const relationLoading = ref(false)
const relationApplying = ref(false)
const relationChoices = ref<Record<string, string[]>>({})
const resolvedMaterialSignatures = new Map<string, string>()
const autoLinkedDraftRows = new Set<string>()
const unconfirmedDuplicateDraftRows = new Set<string>()
const releaseDrawerOpen = ref(false)
const selectedReleasePackageId = ref('')
const reconciliationDrawerOpen = ref(false)
const reconciliationPreviewLoading = ref(false)
const reconciliationPreviewError = ref('')
const reconciliationPreview = ref<BomGenerationResult | null>(null)
const reconciliationFilter = ref<ReconciliationChangeType | 'All'>('All')
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
let duplicateMaterialChoiceResolver: ((material: PdmMaterial | null) => void) | null = null
let summaryQuantityChoiceResolver: ((allocation: SummaryQuantityAllocation | null) => void) | null = null
const summaryQuantityTargets = new Map<string, SummaryQuantityAllocation>()

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

function openExportDialog(targetKind: BomKind) {
  exportKind.value = targetKind
  exportMode.value = 'Summary'
  exportDialogOpen.value = true
}

function confirmExport() {
  exportDialogOpen.value = false
  emit('export', exportKind.value, exportMode.value)
}

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
const publishedStandardLongLeadPackages = computed(() => {
  const releasedAt = latestReleasedVersion.value?.releasedAt
  return props.releasePackages
    .filter(item => item.scope === 'StandardLongLead'
      && item.state === '已发布'
      && !!item.publishedAt
      && (!releasedAt || item.publishedAt > releasedAt))
    .sort((left, right) => (left.publishedAt ?? left.createdAt ?? '').localeCompare(right.publishedAt ?? right.createdAt ?? ''))
})
const publishedStandardBaselineItems = computed(() => {
  const merged = new Map<string, BomItem>()
  const add = (item: BomItem, index: number) => {
    const key = quantityAggregationKey(item) || (item.id ? `id:${item.id.toLocaleLowerCase()}` : `row:${index}`)
    const existing = merged.get(key)
    merged.set(key, existing
      ? { ...item, quantity: Number(existing.quantity) + Number(item.quantity) }
      : { ...item })
  }
  latestReleasedVersion.value?.items.forEach(add)
  publishedStandardLongLeadPackages.value.flatMap(item => item.standardBomSnapshot).forEach(add)
  return [...merged.values()].sort((left, right) => left.sequence - right.sequence)
})
const publishedStandardBaselineLabel = computed(() => {
  const released = latestReleasedVersion.value
  const longLeadCount = publishedStandardLongLeadPackages.value.length
  if (released)
    return longLeadCount ? `${displayVersionLabel(released.label)} + 长交期发布（${longLeadCount}次）` : displayVersionLabel(released.label)
  if (longLeadCount === 1) return publishedStandardLongLeadPackages.value[0]?.number
  return longLeadCount > 1 ? `累计长交期发布（${longLeadCount}次）` : undefined
})
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
  if (kind.value === 'Standard' && publishedStandardBaselineItems.value.length)
    return { label: publishedStandardBaselineLabel.value ?? '标准件已发布基线', items: publishedStandardBaselineItems.value }
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
const reconciliationReminderKeys = computed(() => {
  const keys = new Set<string>()
  sourceDataRows.value.forEach((source, index) => {
    const maintained = source.id ? maintainedMechanicalById.value.get(source.id) : undefined
    if (!maintained || (source.reconciliationStatus === 'ManualOverrideMismatch' && Boolean(reconciliationIssueLabel(source))))
      keys.add(reconciliationGroupKey(source, index))
  })
  maintainedMechanicalRows.value.filter(item => item.source === 'Auto'
    && !!item.sourceDocumentId
    && !item.manuallyExcluded
    && !item.pendingRemoval
    && !item.manualUnmatched
    && !!item.id
    && !sourceDataIds.value.has(item.id)).forEach((item, index) => keys.add(reconciliationGroupKey(item, index)))
  return keys
})
const quantityMismatchKeys = computed(() => {
  const keys = new Set<string>()
  maintainedMechanicalRows.value
    .filter(item => !item.manuallyExcluded && !item.pendingRemoval && !item.manualUnmatched)
    .forEach((item, index) => {
      const source = rawSourceRowFor(item)
      if (!source || Number(source.quantity) === Number(item.quantity)) return
      keys.add(reconciliationGroupKey(source, index))
    })
  return keys
})
const reconciliationPendingKeys = computed(() => new Set([...reconciliationReminderKeys.value, ...quantityMismatchKeys.value]))
const reconciliationReminderCount = computed(() => reconciliationPendingKeys.value.size)
const reconciliationReminderInstanceCount = computed(() => {
  if (!reconciliationReminderCount.value) return 0
  const sourceInstances = sourceDataRows.value.filter((item, index) => reconciliationPendingKeys.value.has(reconciliationGroupKey(item, index))).length
  const missingInstances = maintainedMechanicalRows.value.filter((item, index) => !item.manuallyExcluded
    && !sourceDataIds.value.has(item.id)
    && reconciliationPendingKeys.value.has(reconciliationGroupKey(item, index))).length
  return sourceInstances + missingInstances
})
const reconcileActionTitle = '只读查看最新图档源数据与当前机械BOM工作区的差异；不会自动修改机械BOM'
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
  children.forEach(items => items.sort((left, right) => left.row.sequence - right.row.sequence || left.path.localeCompare(right.path, 'zh-CN', { numeric: true })))
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
function quantityTotalKey(row: BomItem) {
  return summaryAggregationKey(row) || comparisonMaterialKey(row, 'Summary')
}

function buildQuantityTotals(items: BomItem[]) {
  const totals = new Map<string, number>()
  items.forEach(row => {
    const key = quantityTotalKey(row)
    if (!key) return
    totals.set(key, (totals.get(key) ?? 0) + Number(row.quantity))
  })
  return totals
}

const bomQuantityTotals = computed(() => buildQuantityTotals(rows.value.filter(row => !row._quickEntry)))
const sourceQuantityTotals = computed(() => buildQuantityTotals(sourceDataRows.value))
const publishedQuantityItems = computed(() => {
  if (selectedVersionId.value !== 'current' || kind.value !== 'Standard')
    return comparisonBaseline.value?.items ?? []
  return publishedStandardBaselineItems.value
})
const publishedQuantityTotals = computed(() => buildQuantityTotals(
  publishedQuantityItems.value.filter(item => !item.manuallyExcluded && !item.pendingClassification),
))
function operationItemIds(row: EditableBomRow) {
  if (displayMode.value === 'Structure') return row.id ? [row.id] : []
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
const canSetNoPublish = computed(() => selectedRows.value.length > 0
  && !hasSelectedDraftRows.value
  && selectedRows.value.every(item => !item.releaseExcluded))
const canRestorePublish = computed(() => selectedRows.value.length > 0
  && !hasSelectedDraftRows.value
  && selectedRows.value.every(item => item.releaseExcluded))
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
const publishedLongLeadItems = computed(() => props.releasePackages
  .filter(item => item.scope === 'StandardLongLead' && item.state === '已发布')
  .flatMap(item => item.standardBomSnapshot))
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

function issueCurrentRow(row: EditableBomRow) {
  if (!isSourceView.value || !row.id) return row
  return maintainedMechanicalById.value.get(row.id) ?? row
}

function issueComparisons(row: EditableBomRow): BomIssueComparison[] {
  const current = issueCurrentRow(row)
  const source = rawSourceRowFor(current) ?? row
  const fields = mismatchFields(current)
  const fieldValues: Record<string, { sourceLabel: string; source: string | undefined; currentLabel: string; current: string | undefined }> = {
    物料分类: { sourceLabel: '图档源分类', source: rowKindLabel(source), currentLabel: 'BOM分类', current: rowKindLabel(current) },
    物料编码: { sourceLabel: '图档源物料编码', source: source.drawingNumber, currentLabel: 'BOM物料编码', current: current.drawingNumber },
    型号: hasDrawingNameModelMismatch(current)
      ? { sourceLabel: '设计树图纸名称', source: drawingNameFor(current), currentLabel: 'BOM型号', current: current.specification }
      : { sourceLabel: '图档源型号', source: source.specification, currentLabel: 'BOM型号', current: current.specification },
    品牌: { sourceLabel: '图档源品牌', source: source.brand, currentLabel: 'BOM品牌', current: current.brand },
    材质: { sourceLabel: '图档源材质', source: source.material, currentLabel: 'BOM材质', current: current.material },
    表面处理: { sourceLabel: '图档源表面处理', source: source.surfaceTreatment, currentLabel: 'BOM表面处理', current: current.surfaceTreatment },
  }
  return fields.flatMap(field => {
    const values = fieldValues[field]
    return values ? [{ field, sourceLabel: values.sourceLabel, source: values.source?.trim() || '未填写', currentLabel: values.currentLabel, current: values.current?.trim() || '未填写' }] : []
  })
}

function openIssueDetails(row: EditableBomRow) {
  if (shouldShowReconciliation(row)) issueDetailRow.value = row
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
  const { _sourceItemIds: sourceItemIds, _sourceKinds: sourceKinds } = row as EditableBomRow
  if (kind.value === 'Source' && sourceItemIds?.length) {
    const maintainedKinds = sourceItemIds.flatMap(id => {
      const maintainedKind = maintainedKindById.value.get(id)
      return maintainedKind ? [maintainedKind] : []
    })
    if (maintainedKinds.length === sourceItemIds.length && new Set(maintainedKinds).size === 1) return maintainedKinds[0]
    if (maintainedKinds.length > 0 || row.pendingClassification || !sourceKinds?.length || new Set(sourceKinds).size !== 1) return undefined
    return sourceKinds[0] === 'Unclassified' ? undefined : sourceKinds[0]
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
  if (masterIssues.length) return `${row.releaseExcluded ? '不发布 · ' : ''}待人工维护：${masterIssues.join('、')}`
  const missing = missingRequiredFields(row)
  const status = missing.length === 0 ? '已完善' : `缺少${missing.join('、')}`
  return row.releaseExcluded ? `不发布 · ${status}` : status
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
        _sourceKinds: item.kind ? [item.kind] : [],
      })
      return
    }
    existing.quantity = Number(existing.quantity) + Number(item.quantity)
    if (item.id && !existing._sourceItemIds?.includes(item.id)) existing._sourceItemIds = [...(existing._sourceItemIds ?? []), item.id]
    if (item.kind && !existing._sourceKinds?.includes(item.kind)) existing._sourceKinds = [...(existing._sourceKinds ?? []), item.kind]
    existing.pendingClassification ||= item.pendingClassification
    existing.pendingRemoval ||= item.pendingRemoval
    existing.manualUnmatched ||= item.manualUnmatched
    existing.releaseExcluded ||= item.releaseExcluded
    if (!existing.releaseExclusionReason && item.releaseExclusionReason) existing.releaseExclusionReason = item.releaseExclusionReason
    existing.complete &&= item.complete
  })
  return [...grouped.values()]
}

function reconciliationGroupKey(row: BomItem, fallbackIndex = 0) {
  if (row.sourceDocumentId) return ['document', row.sourceDocumentId, row.sourceConfiguration].map(value => normalizedAggregationValue(value)).join('|')
  const materialKey = quantityAggregationKey(row)
  if (materialKey) return `material:${materialKey}`
  return row.id ? `id:${row.id.toLocaleLowerCase()}` : `row:${fallbackIndex}`
}

function groupReconciliationItems(items: BomItem[]) {
  const groups = new Map<string, BomItem[]>()
  items.filter(item => !item.manuallyExcluded).forEach((item, index) => {
    const key = reconciliationGroupKey(item, index)
    groups.set(key, [...(groups.get(key) ?? []), item])
  })
  return groups
}

function reconciliationKindLabel(value: BomClassification | undefined) {
  return value === 'Standard' ? '标准件BOM' : value === 'NonStandard' ? '非标件BOM' : value === 'Electrical' ? '电气BOM' : value === 'Virtual' ? '虚拟件（仅源数据）' : '待分类'
}

function reconciliationGroupKind(items: BomItem[]) {
  return items.find(item => item.kind)?.kind ?? (items.some(item => item.pendingClassification) ? 'Unclassified' : undefined)
}

function reconciliationGroupValue(items: BomItem[], field: keyof BomItem) {
  if (field === 'quantity') return items.length ? formatQuantity(items.reduce((sum, item) => sum + Number(item.quantity || 0), 0)) : ''
  const values = [...new Set(items.map(item => displayValue(item[field] as string | number | undefined)).filter(value => value !== '—'))]
  return values.length > 1 ? '多个值' : values[0] ?? ''
}

function reconciliationItemLabel(items: BomItem[]) {
  const row = items[0]
  if (!row) return '未识别物料'
  const code = row.drawingNumber?.trim()
  const name = row.name?.trim() || row.specification?.trim()
  return [code, name].filter(Boolean).join(' · ') || row.sourceDocumentId || '未编码物料'
}

const reconciliationFieldDefinitions: Array<{ field: keyof BomItem; label: string }> = [
  { field: 'drawingNumber', label: '物料编码' },
  { field: 'name', label: '物料名称' },
  { field: 'unit', label: '单位' },
  { field: 'specification', label: '型号' },
  { field: 'remark', label: '备注信息' },
  { field: 'brand', label: '品牌' },
  { field: 'material', label: '材质' },
  { field: 'surfaceTreatment', label: '表面处理' },
  { field: 'heatTreatment', label: '热处理' },
  { field: 'revision', label: '版本' },
  { field: 'parentDrawingNumber', label: '上级物料编码' },
]

function buildReconciliationDetails(preview: BomGenerationResult) {
  const currentItems = [
    ...maintainedMechanicalRows.value,
    ...sourceDataRows.value.filter(item => item.kind === 'Virtual'),
  ]
  const proposedItems = [...preview.standardItems, ...preview.nonStandardItems, ...preview.unclassifiedItems, ...preview.virtualItems]
  const currentGroups = groupReconciliationItems(currentItems)
  const sourceGroups = groupReconciliationItems(sourceDataRows.value)
  const proposedGroups = groupReconciliationItems(proposedItems)
  const keys = new Set([...currentGroups.keys(), ...sourceGroups.keys(), ...proposedGroups.keys()])
  const details: ReconciliationDetail[] = []
  const push = (groupKey: string, type: ReconciliationChangeType, items: BomItem[], field: string, current: string, source: string, proposed: string, action: string) => {
    details.push({ key: `${groupKey}:${type}:${field}`, groupKey, type, item: reconciliationItemLabel(items), field, current: current || '—', source: source || '—', proposed: proposed || '—', action, instances: Math.max(1, items.length) })
  }

  keys.forEach(groupKey => {
    const current = currentGroups.get(groupKey) ?? []
    const source = sourceGroups.get(groupKey) ?? []
    const proposed = proposedGroups.get(groupKey) ?? []
    const displayItems = proposed.length ? proposed : current.length ? current : source
    const proposedKind = reconciliationGroupKind(proposed)
    const currentKind = reconciliationGroupKind(current)
    const sourceKind = reconciliationGroupKind(source)
    const instances = source.length ? source : displayItems

    if (!current.length && proposed.length) {
      push(groupKey, 'Added', instances, '整行', '', reconciliationKindLabel(sourceKind), reconciliationKindLabel(proposedKind), `提醒人工确认是否加入${reconciliationKindLabel(proposedKind)}`)
      return
    }
    if (!proposed.length || proposed.some(item => item.pendingRemoval || item.manualUnmatched)) {
      push(groupKey, 'Removed', instances, '来源状态', reconciliationKindLabel(currentKind), source.length ? '仍存在' : '最新源数据中不存在', '待处理', '提醒人工处理，不自动删除')
      return
    }
    if (currentKind !== proposedKind)
      push(groupKey, 'Classification', instances, '物料分类', reconciliationKindLabel(currentKind), reconciliationKindLabel(sourceKind), reconciliationKindLabel(proposedKind), '提醒人工核对分类')

    const currentQuantity = reconciliationGroupValue(current, 'quantity')
    const sourceQuantity = reconciliationGroupValue(source, 'quantity')
    const proposedQuantity = reconciliationGroupValue(proposed, 'quantity')
    if (currentQuantity !== proposedQuantity)
      push(groupKey, 'Quantity', instances, '数量', currentQuantity, sourceQuantity, proposedQuantity, '提醒人工核对数量')

    reconciliationFieldDefinitions.forEach(({ field, label }) => {
      const currentValue = reconciliationGroupValue(current, field)
      const sourceValue = reconciliationGroupValue(source, field)
      const proposedValue = reconciliationGroupValue(proposed, field)
      if (currentValue !== proposedValue) {
        push(groupKey, 'Attribute', instances, label, currentValue, sourceValue, proposedValue, '提醒人工核对属性')
        return
      }
      const manualFields = proposedKind === 'Standard'
        ? ['drawingNumber', 'specification', 'brand']
        : proposedKind === 'NonStandard' ? ['specification', 'material', 'surfaceTreatment'] : []
      if (manualFields.includes(field) && sourceValue && currentValue !== sourceValue)
        push(groupKey, 'Manual', instances, label, currentValue, sourceValue, proposedValue, '仅提示差异，不自动覆盖')
    })
  })
  return details
}

const reconciliationDetails = computed(() => reconciliationPreview.value ? buildReconciliationDetails(reconciliationPreview.value) : [])
const filteredReconciliationDetails = computed(() => reconciliationFilter.value === 'All'
  ? reconciliationDetails.value
  : reconciliationDetails.value.filter(item => item.type === reconciliationFilter.value))
const reconciliationSummary = computed(() => {
  const count = (type: ReconciliationChangeType) => new Set(reconciliationDetails.value.filter(item => item.type === type).map(item => item.groupKey)).size
  return {
    total: new Set(reconciliationDetails.value.map(item => item.groupKey)).size,
    instances: new Set(reconciliationDetails.value.flatMap(item => Array.from({ length: item.instances }, (_, index) => `${item.groupKey}:${index}`))).size,
    added: count('Added'), quantity: count('Quantity'), attribute: count('Attribute'), classification: count('Classification'), manual: count('Manual'), removed: count('Removed'),
  }
})

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
  return formatQuantity(Number(row.quantity))
}

function quantityTitle(row: BomItem) {
  return displayMode.value === 'Summary' && operationItemIds(row as EditableBomRow).length > 1
    ? `当前汇总数量：${quantityDisplay(row)}；输入新的汇总总数量`
    : `当前数量：${quantityDisplay(row)}`
}

const summaryQuantityChoiceResultTotal = computed(() => {
  let total = 0
  for (const candidate of summaryQuantityChoiceCandidates.value) {
    if (!candidate.item.id) return Number.NaN
    const value = Number(candidate.quantity)
    if (!Number.isFinite(value) || value < 0) return Number.NaN
    total += value
  }
  return total
})
const summaryQuantityChoiceRemaining = computed(() => summaryQuantityChoiceTargetQuantity.value - summaryQuantityChoiceResultTotal.value)
const summaryQuantityChoiceChangedCount = computed(() => summaryQuantityChoiceCandidates.value.filter(candidate => candidate.item.id
  && Number(candidate.quantity) !== Number(candidate.item.quantity)).length)
const summaryQuantityChoiceZeroCount = computed(() => summaryQuantityChoiceCandidates.value.filter(candidate => candidate.item.id
  && Number(candidate.quantity) === 0).length)
const canConfirmSummaryQuantityChoice = computed(() => Number.isFinite(summaryQuantityChoiceResultTotal.value)
  && Math.abs(summaryQuantityChoiceRemaining.value) < 0.00005
  && summaryQuantityChoiceChangedCount.value > 0)

function updateSummaryQuantityCandidate(candidate: SummaryQuantityCandidate, event: Event) {
  const value = (event.target as HTMLInputElement).value
  candidate.quantity = value === '' ? '' : Number(value)
}

function closeSummaryQuantityChoice(allocation: SummaryQuantityAllocation | null = null) {
  summaryQuantityChoiceOpen.value = false
  const resolver = summaryQuantityChoiceResolver
  summaryQuantityChoiceResolver = null
  resolver?.(allocation)
}

function chooseSummaryQuantityTarget(row: EditableBomRow, candidates: BomItem[], currentTotal: number, targetQuantity: number) {
  closeSummaryQuantityChoice()
  summaryQuantityChoiceMaterial.value = row
  summaryQuantityChoiceCurrentTotal.value = currentTotal
  summaryQuantityChoiceTargetQuantity.value = targetQuantity
  const rowKey = rowSelectionKey(row)
  const currentAllocation = rowKey ? summaryQuantityTargets.get(rowKey) : undefined
  summaryQuantityChoiceCandidates.value = candidates.map(item => ({
    item,
    quantity: item.id ? currentAllocation?.[item.id] ?? Number(item.quantity) : Number(item.quantity),
  }))
  summaryQuantityChoiceOpen.value = true
  return new Promise<SummaryQuantityAllocation | null>(resolve => { summaryQuantityChoiceResolver = resolve })
}

async function confirmSummaryQuantityChoice() {
  if (!canConfirmSummaryQuantityChoice.value) return
  if (summaryQuantityChoiceZeroCount.value > 0) {
    try {
      await ElMessageBox.confirm(
        `有 ${summaryQuantityChoiceZeroCount.value} 个结构位置的修改后数量为 0，保存BOM时这些位置将自动移入回收站（可恢复）。是否继续？`,
        '二次确认：删除结构位置',
        { confirmButtonText: '确认删除并继续', cancelButtonText: '返回修改', type: 'warning', closeOnClickModal: false },
      )
    } catch (error) {
      if (error === 'cancel' || error === 'close') return
      throw error
    }
  }
  const allocation: SummaryQuantityAllocation = {}
  summaryQuantityChoiceCandidates.value.forEach(candidate => {
    if (candidate.item.id) allocation[candidate.item.id] = Number(candidate.quantity)
  })
  closeSummaryQuantityChoice(allocation)
}

function summaryQuantityLocation(item: BomItem, index: number) {
  return item.sourceInstancePath?.trim() || `${item.parentDrawingNumber?.trim() || '顶层'} / 第 ${item.sequence || index + 1} 项`
}

function normalizedReferenceValue(value?: string) {
  return value?.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '').toLocaleLowerCase('zh-CN') ?? ''
}

function summaryQuantityParentAssembly(item: BomItem): SummaryParentAssembly | undefined {
  const root = props.referenceRoot
  const sourceItem = rawSourceRowFor(item) ?? item
  const sourcePath = normalizedReferenceValue(sourceItem.sourceInstancePath)
  const sourceDocumentId = sourceItem.sourceDocumentId?.trim()
  const sourceConfiguration = normalizedReferenceValue(sourceItem.sourceConfiguration)
  const pathMatches = new Map<string, DocumentNode>()
  const documentMatches = new Map<string, DocumentNode>()

  const visit = (node: DocumentNode, parentAssembly?: DocumentNode) => {
    const nodePath = normalizedReferenceValue(node.instancePath)
    const nextParentAssembly = node.kind === 'Assembly' ? node : parentAssembly
    if (node !== root && parentAssembly) {
      if (sourcePath && nodePath === sourcePath) pathMatches.set(parentAssembly.id, parentAssembly)
      if (sourceDocumentId && node.documentId === sourceDocumentId
        && (!sourceConfiguration || normalizedReferenceValue(node.configuration) === sourceConfiguration)) {
        documentMatches.set(parentAssembly.id, parentAssembly)
      }
    }
    node.children.forEach(child => visit(child, nextParentAssembly))
  }
  if (root) visit(root)

  const pathParent = pathMatches.size === 1 ? [...pathMatches.values()][0] : undefined
  if (pathParent) return pathParent

  const parentDrawingNumber = sourceItem.parentDrawingNumber?.trim() || item.parentDrawingNumber?.trim()
  if (parentDrawingNumber) {
    const key = normalizedReferenceValue(parentDrawingNumber)
    const treeMatches: DocumentNode[] = []
    const findByDrawingNumber = (node: DocumentNode) => {
      if (node.kind === 'Assembly' && normalizedReferenceValue(node.drawingNumber) === key) treeMatches.push(node)
      node.children.forEach(findByDrawingNumber)
    }
    if (root) findByDrawingNumber(root)
    if (treeMatches.length > 0) return treeMatches[0]
    const document = props.documents.find(candidate => candidate.kind === 'Assembly' && normalizedReferenceValue(candidate.drawingNumber) === key)
    if (document) return document
  }

  const documentParent = documentMatches.size === 1 ? [...documentMatches.values()][0] : undefined
  return documentParent ?? (root?.kind === 'Assembly' ? root : undefined)
}

function summaryQuantityParentAssemblyName(item: BomItem) {
  const parent = summaryQuantityParentAssembly(item)
  return parent?.name?.trim() || parent?.drawingNumber?.trim() || item.parentDrawingNumber?.trim() || '顶层'
}

function summaryQuantityParentAssemblyTitle(item: BomItem) {
  const parent = summaryQuantityParentAssembly(item)
  const name = summaryQuantityParentAssemblyName(item)
  const drawingNumber = parent?.drawingNumber?.trim() || item.parentDrawingNumber?.trim()
  return drawingNumber && normalizedReferenceValue(drawingNumber) !== normalizedReferenceValue(name) ? `${name}（${drawingNumber}）` : name
}

function summaryQuantityForSave(row: EditableBomRow, originals: BomItem[], original: BomItem) {
  if (originals.length <= 1) return Number(row.quantity)
  const rowKey = rowSelectionKey(row)
  const allocation = rowKey ? summaryQuantityTargets.get(rowKey) : undefined
  return original.id && allocation?.[original.id] !== undefined ? allocation[original.id] : Number(original.quantity)
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

function bomQuantityTotal(row: BomItem) {
  if (isSourceView.value) return undefined
  return bomQuantityTotals.value.get(quantityTotalKey(row)) ?? Number(row.quantity)
}

function publishedQuantityTotal(row: BomItem) {
  return publishedQuantityTotals.value.get(quantityTotalKey(row))
}

function sourceQuantityTotal(row: BomItem) {
  const source = rawSourceRowFor(row)
  if (!source) return undefined
  return sourceQuantityTotals.value.get(quantityTotalKey(source)) ?? Number(source.quantity)
}

function quantityReferenceDisplay(value: number | undefined) {
  return value === undefined ? '—' : formatQuantity(value)
}

function sourceQuantityDiffers(row: BomItem) {
  const total = bomQuantityTotal(row)
  const source = sourceQuantityTotal(row)
  return total !== undefined && source !== undefined && total !== source
}

function quantityReferenceTitle(row: BomItem) {
  return `已发布总数量：${quantityReferenceDisplay(publishedQuantityTotal(row) ?? 0)}；当前BOM总数量：${quantityReferenceDisplay(bomQuantityTotal(row))}；源总数量：${quantityReferenceDisplay(sourceQuantityTotal(row))}`
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
  const unconfirmed = rows.value.find(row => {
    const key = rowSelectionKey(row)
    return key && unconfirmedDuplicateDraftRows.has(key)
  })
  if (unconfirmed) {
    ElMessage.error(`“${unconfirmed.specification || unconfirmed.name || unconfirmed.drawingNumber || '当前物料'}”存在重复料品，请完成多选一确认后再保存`)
    return
  }
  discardDraftsOnNextSourceRefresh = true
  saveRequested = true
  const rawById = new Map(rawSourceRows.value.flatMap(item => item.id ? [[item.id, item] as const] : []))
  const expanded = rows.value.flatMap(row => {
    const { _clientKey, _quickEntry, _sourceItemIds, _sourceKinds, ...displayItem } = row
    if (!_sourceItemIds?.length) return [{ ...displayItem, complete: dataStatusIssues(row).length === 0 }]
    const originals = _sourceItemIds.flatMap(id => {
      const original = rawById.get(id)
      return original ? [original] : []
    })
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
        quantity: summaryQuantityForSave(row, originals, original),
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
  summaryQuantityTargets.clear()
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

async function requestGenerate() {
  if (!props.previewReconciliation) {
    emit('generate', false)
    return
  }
  reconciliationDrawerOpen.value = true
  reconciliationPreviewLoading.value = true
  reconciliationPreviewError.value = ''
  reconciliationPreview.value = null
  reconciliationFilter.value = 'All'
  try {
    reconciliationPreview.value = await props.previewReconciliation()
  } catch (error) {
    reconciliationPreviewError.value = error instanceof Error ? error.message : '机械BOM对账预览失败'
  } finally {
    reconciliationPreviewLoading.value = false
  }
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
    const targetKey = rowSelectionKey(target)
    if (targetKey) unconfirmedDuplicateDraftRows.delete(targetKey)
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

function relationChoiceKey(mainBomItemId: string, groupId: string) {
  return `${mainBomItemId}|${groupId}`
}

function relationChoice(mainBomItemId: string, groupId: string) {
  return relationChoices.value[relationChoiceKey(mainBomItemId, groupId)] ?? []
}

function isSingleRelationGroup(group: MaterialRelationGroupCheck) {
  return group.selectionMode === 'Single' || (group.selectionMode as unknown) === 0
}

function isPerMainRelationQuantity(value: unknown) {
  return value === 'PerMainQuantity' || value === 0
}

function updateRelationChoice(mainBomItemId: string, groupId: string, value: string | string[]) {
  relationChoices.value = { ...relationChoices.value, [relationChoiceKey(mainBomItemId, groupId)]: Array.isArray(value) ? value : value ? [value] : [] }
}

function updateRelationMultiple(mainBomItemId: string, groupId: string, value: unknown) {
  updateRelationChoice(mainBomItemId, groupId, Array.isArray(value) ? value.map(String) : [])
}

function updateRelationSingle(mainBomItemId: string, groupId: string, value: unknown) {
  updateRelationChoice(mainBomItemId, groupId, value == null ? '' : String(value))
}

function initializeRelationChoices(result: MaterialRelationCompleteness) {
  const next: Record<string, string[]> = {}
  for (const main of result.mainMaterials) for (const group of main.groups) {
    let ids = [...group.selectedOptionIds]
    if (!ids.length && group.options.length === 1 && ((group.isRequired && group.status === '可自动带出') || group.options[0].isDefault)) ids = [group.options[0].id]
    next[relationChoiceKey(main.mainBomItemId, group.groupId)] = ids
  }
  relationChoices.value = next
}

async function loadRelationCompleteness(showError = true) {
  if (!props.projectId || !props.token) return
  relationLoading.value = true
  try {
    const result = await getMaterialRelationCompleteness(props.projectId, props.token)
    relationCompleteness.value = result
    initializeRelationChoices(result)
  } catch (error) {
    relationCompleteness.value = null
    if (showError) ElMessage.error(error instanceof Error ? error.message : '配套完整性加载失败')
  } finally { relationLoading.value = false }
}

async function openMaterialRelations() {
  if (hasStagedEdits.value) { ElMessage.warning('请先保存当前BOM修改，再进行配套选型。'); return }
  relationDialogOpen.value = true
  await loadRelationCompleteness()
}

function relationQuantityText(group: MaterialRelationGroupCheck, optionId: string, mainQuantity: number) {
  const option = group.options.find(item => item.id === optionId)
  if (!option) return ''
  const perMainQuantity = isPerMainRelationQuantity(option.quantityMode)
  const quantity = perMainQuantity ? option.quantityPerSet * mainQuantity : option.quantityPerSet
  return `${perMainQuantity ? `${option.quantityPerSet} × ${mainQuantity}` : '固定'} = ${quantity}`
}

async function applySelectedRelations() {
  if (!props.projectId || !props.token || !relationCompleteness.value?.mainMaterials.length) return
  relationApplying.value = true
  try {
    const payload = relationCompleteness.value.mainMaterials.map(main => ({
      mainBomItemId: main.mainBomItemId,
      choices: main.groups.map(group => ({ groupId: group.groupId, optionIds: relationChoice(main.mainBomItemId, group.groupId) })),
    }))
    relationCompleteness.value = await applyMaterialRelations(props.projectId, payload, props.token)
    initializeRelationChoices(relationCompleteness.value)
    emit('materialRelationsApplied')
    emit('materialCodeChanged')
    ElMessage.success(relationCompleteness.value.isComplete ? '配套物料已加入BOM，完整性校验通过' : '配套物料已更新，请继续处理未完整项')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '配套物料应用失败') }
  finally { relationApplying.value = false }
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
  if (!preserveDrafts) summaryQuantityTargets.clear()
  resetQuickEntry()
  const available = new Set(rows.value.flatMap(item => {
    const key = rowSelectionKey(item)
    return key ? [key] : []
  }))
  selectedIds.value = selectedIds.value.filter(id => available.has(id))
  for (const key of unconfirmedDuplicateDraftRows) if (!available.has(key)) unconfirmedDuplicateDraftRows.delete(key)
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
  if (projectId && projectCount > 0 && (!previous || previous[0] !== projectId)) {
    kind.value = 'Overview'
    relationCompleteness.value = null
    relationChoices.value = {}
    relationDialogOpen.value = false
  }
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
onBeforeUnmount(() => duplicateMaterialChoiceResolver?.(null))
onBeforeUnmount(() => summaryQuantityChoiceResolver?.(null))

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
  if (displayMode.value === 'Structure' && structureParentKeyAtIndex(index) !== structureParentKeyAtIndex(draggedRowIndex.value)) {
    dragOverRowIndex.value = null
    dragOverPosition.value = null
    return
  }
  const bounds = targetRow.getBoundingClientRect()
  dragOverRowIndex.value = index
  dragOverPosition.value = event.clientY < bounds.top + bounds.height / 2 ? 'before' : 'after'
}

function structureParentKeyAtIndex(index: number) {
  const path = normalizedStructurePath(rows.value[index]!)
  if (!path) return ''
  const paths = new Set(rows.value.map(normalizedStructurePath).filter(Boolean))
  let parentPath = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : ''
  while (parentPath && !paths.has(parentPath)) parentPath = parentPath.includes('/') ? parentPath.slice(0, parentPath.lastIndexOf('/')) : ''
  return parentPath
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
  if (row._clientKey) unconfirmedDuplicateDraftRows.delete(row._clientKey)
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

async function setSelectedReleaseExclusion(excluded: boolean) {
  const itemIds = selectedPersistedIds.value
  if (!itemIds.length || props.pending) return
  try {
    if (excluded) {
      const response = await ElMessageBox.prompt(
        `所选 ${itemIds.length} 条物料将保留在BOM中，但不会进入后续发布文件和U9C发布汇总。`,
        '不发布',
        {
          confirmButtonText: '确认不发布', cancelButtonText: '取消', type: 'warning',
          inputPlaceholder: '请输入不发布原因（必填）',
          inputValidator: value => value?.trim() ? true : '请填写不发布原因',
        },
      )
      emit('releaseExclusion', itemIds, true, response.value.trim())
    } else {
      await ElMessageBox.confirm(
        `所选 ${itemIds.length} 条物料将恢复参与后续发布。`,
        '恢复发布',
        { confirmButtonText: '确认恢复发布', cancelButtonText: '取消', type: 'warning' },
      )
      emit('releaseExclusion', itemIds, false, '')
    }
    selectedIds.value = []
  } catch {
    // 用户取消时不修改发布状态。
  }
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

function closeDuplicateMaterialChoice(material: PdmMaterial | null = null) {
  duplicateMaterialChoiceOpen.value = false
  duplicateMaterialChoiceCandidates.value = []
  duplicateMaterialChoiceReason.value = ''
  const resolve = duplicateMaterialChoiceResolver
  duplicateMaterialChoiceResolver = null
  resolve?.(material)
}

function chooseDuplicateMaterial(candidates: PdmMaterial[], row: BomItem, field: MaterialLookupField, value: string) {
  closeDuplicateMaterialChoice()
  const fieldLabel = field === 'drawingNumber' ? '物料编码' : field === 'brand' ? '型号和品牌' : '型号'
  const brand = field === 'brand' ? value.trim() : row.brand?.trim()
  duplicateMaterialChoiceCandidates.value = [...candidates].sort((left, right) => {
    const leftPreferred = brand && materialLookupValue(left.brand) === materialLookupValue(brand) ? 0 : 1
    const rightPreferred = brand && materialLookupValue(right.brand) === materialLookupValue(brand) ? 0 : 1
    return leftPreferred - rightPreferred || left.materialCode.localeCompare(right.materialCode, 'zh-CN', { numeric: true })
  })
  duplicateMaterialChoiceReason.value = `${fieldLabel}“${value.trim()}”检出 ${candidates.length} 个重复候选。系统不会自动默认，请确认本次要使用的料品。`
  duplicateMaterialChoiceOpen.value = true
  return new Promise<PdmMaterial | null>(resolve => { duplicateMaterialChoiceResolver = resolve })
}

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

async function findApprovedMaterialMatches(row: BomItem, field: MaterialLookupField, value: string): Promise<MaterialMatchResult> {
  const query = field === 'brand' ? row.specification?.trim() ?? '' : value.trim()
  if (!props.token || !query) return { candidates: [], duplicateDetected: false }
  const loaded = await listMaterials(props.token, query, false, 500)
  if (field === 'drawingNumber') {
    const code = materialLookupValue(value)
    const candidates = loaded.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && materialLookupValue(item.materialCode) === code)
    return { candidates, duplicateDetected: candidates.length > 1 }
  }
  const specification = materialLookupValue(field === 'specification' ? value : row.specification)
  const modelMatches = loaded.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && materialLookupValue(item.specification) === specification)
  return { candidates: modelMatches, duplicateDetected: modelMatches.length > 1 }
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
  if (matches.length !== 1) return undefined
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

async function autofillFromMaterialMaster(row: BomItem, input: BatchUpdateBomItemsInput, field: EditableBomField, value: string) {
  if ((field !== 'drawingNumber' && field !== 'specification' && field !== 'brand') || !value.trim()) return true
  const reusable = reusableBomRow(row, field, value)
  if (reusable) {
    applyBomRowAutofill(input, reusable, field, value.trim())
    ElMessage.success(reusable.drawingNumber.trim()
      ? `已自动关联同型号料品 ${reusable.drawingNumber}`
      : '已自动关联同型号BOM物料，料号申请将共用一次')
    return true
  }
  try {
    const result = await findApprovedMaterialMatches(row, field, value)
    if (result.duplicateDetected) {
      const selected = await chooseDuplicateMaterial(result.candidates, row, field, value)
      if (!selected) {
        ElMessage.warning('未确认重复料品，本次修改已取消')
        return false
      }
      applyMaterialAutofill(input, selected, field, value.trim())
      ElMessage.success(`已确认料品 ${selected.materialCode}，并补齐其他信息`)
    } else if (result.candidates.length === 1) {
      applyMaterialAutofill(input, result.candidates[0], field, value.trim())
      ElMessage.success(`已按${field === 'drawingNumber' ? '物料编码' : field === 'brand' ? '型号和品牌' : '型号'}匹配料品 ${result.candidates[0].materialCode}，并自动补齐其他信息`)
    } else {
      ElMessage.warning(field === 'brand'
        ? '料品主档中未找到匹配的型号和品牌，已保留手工输入'
        : `料品主档中未找到${field === 'drawingNumber' ? '物料编码' : '型号'}“${value.trim()}”，已保留手工输入`)
    }
    return true
  } catch (error) {
    ElMessage.warning(error instanceof Error ? `${error.message}；已仅保存手工输入` : '料品主档查询失败；已仅保存手工输入')
    return true
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
    const result = await findApprovedMaterialMatches(row, field, value)
    if (result.duplicateDetected) {
      if (rowKey) unconfirmedDuplicateDraftRows.add(rowKey)
      const selected = await chooseDuplicateMaterial(result.candidates, row, field, value)
      if (!selected) {
        ElMessage.warning('未确认重复料品，已保留本次手工输入')
        return
      }
      applyMaterialToDraft(row, selected)
      if (rowKey) {
        autoLinkedDraftRows.add(rowKey)
        unconfirmedDuplicateDraftRows.delete(rowKey)
      }
      ElMessage.success(`已确认并关联料品 ${selected.materialCode}`)
    } else if (result.candidates.length === 1) {
      applyMaterialToDraft(row, result.candidates[0])
      if (rowKey) {
        autoLinkedDraftRows.add(rowKey)
        unconfirmedDuplicateDraftRows.delete(rowKey)
      }
      ElMessage.success(`已自动关联料品 ${result.candidates[0].materialCode}`)
    } else if (rowKey) {
      unconfirmedDuplicateDraftRows.delete(rowKey)
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
  if (edit.field === 'quantity') {
    const targetQuantity = Number(value)
    if (!Number.isFinite(targetQuantity) || targetQuantity < 0) {
      ElMessage.error('数量不能小于 0')
      return
    }
    const itemIds = operationItemIds(row as EditableBomRow)
    if (displayMode.value === 'Summary' && itemIds.length > 1) {
      const rawById = new Map(rawSourceRows.value.flatMap(item => item.id ? [[item.id, item] as const] : []))
      const originals = itemIds.flatMap(id => {
        const original = rawById.get(id)
        return original ? [original] : []
      })
      const rowKey = rowSelectionKey(row as EditableBomRow)
      const existingAllocation = rowKey ? summaryQuantityTargets.get(rowKey) : undefined
      const currentTotal = existingAllocation
        ? Object.values(existingAllocation).reduce((sum, quantity) => sum + quantity, 0)
        : originals.reduce((sum, item) => sum + Number(item.quantity || 0), 0)
      const allocation = await chooseSummaryQuantityTarget(row as EditableBomRow, originals, currentTotal, targetQuantity)
      if (!allocation) return
      if (rowKey) summaryQuantityTargets.set(rowKey, allocation)
      row.quantity = Object.values(allocation).reduce((sum, quantity) => sum + quantity, 0)
      const next = new Set([...stagedEditKeys.value].filter(key => !itemIds.some(id => key === `${id}:quantity`)))
      originals.forEach(item => {
        if (item.id && allocation[item.id] !== Number(item.quantity)) next.add(`${item.id}:quantity`)
      })
      stagedEditKeys.value = next
      return
    }
    if (targetQuantity === 0) {
      try {
        await ElMessageBox.confirm(
          `物料“${row.drawingNumber || row.name || '当前物料'}”的数量将改为 0，保存BOM时会自动删除该结构位置并移入回收站（可恢复）。是否继续？`,
          '二次确认：删除结构位置',
          { confirmButtonText: '确认删除并继续', cancelButtonText: '取消', type: 'warning', closeOnClickModal: false },
        )
      } catch (error) {
        if (error === 'cancel' || error === 'close') return
        throw error
      }
    }
  }
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
  if (!await autofillFromMaterialMaster(row, input, edit.field, String(value))) return
  if (edit.field === 'kind') emit('batchUpdate', input)
  else applyBatchInputToRows(input)
}

async function submitBatchUpdate() {
  const draft = batchDraft.value
  if (draft.quantityEnabled && displayMode.value === 'Summary' && selectedRows.value.some(row => operationItemIds(row).length > 1)) {
    batchValidation.value = '汇总数量涉及多个结构位置，请在表格中逐行修改并选择本次变化的位置。'
    return
  }
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
  const lookupField: MaterialLookupField | undefined = draft.drawingNumberEnabled && draft.drawingNumber.trim()
    ? 'drawingNumber'
    : draft.brandEnabled && (draft.specificationEnabled ? draft.specification : selectedRows.value[0]?.specification ?? '').trim()
      ? 'brand'
      : draft.specificationEnabled && draft.specification.trim() ? 'specification' : undefined
  if (lookupField) {
    const lookupRow: BomItem = {
      ...selectedRows.value[0]!,
      specification: draft.specificationEnabled ? draft.specification : selectedRows.value[0]?.specification,
      brand: draft.brandEnabled ? draft.brand : selectedRows.value[0]?.brand,
    }
    const lookupValue = lookupField === 'drawingNumber' ? draft.drawingNumber : lookupField === 'brand' ? draft.brand : draft.specification
    if (!await autofillFromMaterialMaster(lookupRow, input, lookupField, lookupValue)) return
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
          <span v-if="editable && reconciliationReminderCount" class="pdm-bom-reconcile-hint" role="status" aria-live="polite">机械BOM待对账 {{ reconciliationReminderCount }} 类（{{ reconciliationReminderInstanceCount }} 个实例）</span>
          <button v-if="editable" type="button" class="pdm-secondary-action" :class="{ 'is-reconcile-needed': reconciliationReminderCount > 0 }" aria-label="查看机械BOM对账明细" :title="reconcileActionTitle" :disabled="pending || reconciliationPreviewLoading" @click="requestGenerate">{{ reconciliationPreviewLoading ? '正在生成明细…' : '查看对账明细' }}</button>
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
        <span v-if="editable && reconciliationReminderCount" class="pdm-bom-reconcile-hint" role="status" aria-live="polite">机械BOM待对账 {{ reconciliationReminderCount }} 类（{{ reconciliationReminderInstanceCount }} 个实例）</span>
        <button v-if="canEditCurrentView" type="button" class="pdm-secondary-action" @click="selectImport">导入XLSX</button>
        <button type="button" class="pdm-secondary-action" @click="openExportDialog(kind as BomKind)">导出XLSX</button>
        <button v-if="editable" type="button" class="pdm-secondary-action" :class="{ 'is-reconcile-needed': reconciliationReminderCount > 0 }" aria-label="查看机械BOM对账明细" :title="reconcileActionTitle" :disabled="pending || reconciliationPreviewLoading" @click="requestGenerate">{{ reconciliationPreviewLoading ? '正在生成明细…' : '查看对账明细' }}</button>
        <button type="button" class="pdm-secondary-action" @click="openReleaseDrawer(activeReleasePackages[0]?.id || latestPublishedPackage?.id)">{{ activeReleasePackages.length ? `处理审批 ${activeReleasePackages.length}` : '发布记录' }}</button>
        <span v-if="hasStagedEdits" class="pdm-bom-unsaved-count" role="status" aria-live="polite">未保存 {{ stagedEditCount }} 项</span>
        <button v-if="canEditCurrentView && hasStagedEdits" type="button" class="pdm-secondary-action pdm-bom-discard-action" :disabled="pending" @click="discardStagedEdits">撤销修改</button>
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
      <strong>{{ selectedVersionId === 'current' ? '当前工作区' : selectedVersion ? displayVersionLabel(selectedVersion.label) : '当前工作区' }} 对比已发布基线 {{ comparisonBaseline.label }}</strong>
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
          <button v-if="!isSourceView" type="button" class="pdm-primary-action pdm-bom-material-code-toolbar-action" :disabled="pending || selectedIds.length > 1 || !token || !projectId" @click="openMaterialReference">引用物料</button>
          <button v-if="!isSourceView" type="button" class="pdm-secondary-action pdm-bom-relation-action" :class="{ 'is-warning': relationCompleteness && !relationCompleteness.isComplete, 'is-complete': relationCompleteness?.isComplete }" :disabled="pending || !token || !projectId" @click="openMaterialRelations">配套选型<span v-if="relationCompleteness">（{{ relationCompleteness.isComplete ? '完整' : `${relationCompleteness.incompleteGroupCount}项待处理` }}）</span></button>
          <button v-if="kind === 'Standard'" type="button" class="pdm-secondary-action pdm-bom-material-code-toolbar-action" :disabled="pending || materialCodeResolving || !token || !projectId" @click="resolveMissingStandardMaterialCodes(true)">核对料号</button>
          <button v-if="kind === 'Standard'" type="button" class="pdm-secondary-action pdm-bom-material-code-toolbar-action" :disabled="pending || selectedStandardItemsWithoutCode.length === 0" @click="applyForMaterialCodes([...new Set(selectedStandardItemsWithoutCode.flatMap(operationItemIds))])">申请料号</button>
          <button v-if="hasRetainableSelection" type="button" class="pdm-secondary-action" :disabled="pending || !canRetainSelected" :title="!canRetainSelected ? '仅支持同时保留人工待确认或待确认删除的物料' : ''" @click="retainSelected">{{ selectedIds.length > 1 ? '批量确认保留' : '确认保留' }}</button>
          <button type="button" class="pdm-secondary-action pdm-bom-no-publish-action" :disabled="pending || !canSetNoPublish" title="物料保留在BOM中，但不进入发布文件和U9C发布汇总" @click="setSelectedReleaseExclusion(true)">不发布</button>
          <button type="button" class="pdm-secondary-action" :disabled="pending || !canRestorePublish" title="恢复参与后续发布" @click="setSelectedReleaseExclusion(false)">恢复发布</button>
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
        <button type="button" class="pdm-secondary-action" :disabled="!comparisonBaseline" @click="comparisonOpen = !comparisonOpen">{{ comparisonOpen ? '收起差异' : '对比已发布基线' }}</button>
      </div>
    </div>
    <div class="pdm-table-scroll">
      <table class="pdm-edit-table pdm-bom-table">
        <colgroup>
          <col class="is-select"><col class="is-row-actions"><col class="is-sequence"><col class="is-kind"><col class="is-unit"><col class="is-code">
          <col class="is-name"><col class="is-parent-code"><col class="is-model"><col class="is-remark"><col class="is-brand">
          <col class="is-material"><col class="is-surface"><col class="is-weight"><col class="is-quantity"><col class="is-quantity-reference"><col class="is-drawing-audit">
          <col class="is-revision"><col class="is-source"><col class="is-data-status">
        </colgroup>
          <thead><tr><th><input type="checkbox" :aria-label="isSourceView ? '选择全部源数据物料' : '选择当前分类全部物料'" :checked="allRowsSelected" :disabled="!canSelectCurrentView || selectableIds.length === 0" @change="toggleAllRows"></th><th aria-label="行排序与插入操作"></th><th>序号</th><th>物料分类</th><th>单位</th><th>物料编码</th><th>物料名称</th><th>上级物料编码</th><th>型号</th><th>备注信息</th><th>品牌</th><th>材质</th><th>表面处理</th><th>重量</th><th>数量</th><th title="已发布总数量 / 当前BOM总数量 / 源总数量">发布&nbsp;&nbsp;总/源</th><th class="pdm-bom-drawing-audit-header">图纸核对</th><th>版本</th><th class="pdm-bom-reconciliation-header">问题</th><th>资料状态</th></tr></thead>
        <tbody>
          <tr v-for="{ row, index, depth, hasChildren, expanded, structureKey } in pagedRows" :key="row.id || row._clientKey" :data-row-index="index" :title="comparisonRowTitle(row)" :class="{ 'is-quick-entry': row._quickEntry, 'is-pending-removal': row.pendingRemoval, 'is-release-excluded': row.releaseExcluded, 'is-bom-unresolved': !row._quickEntry && (rowNeedsClassification(row) || row.manualUnmatched), 'is-data-exception': !row._quickEntry && dataStatusIssues(row).length > 0, 'is-reconciliation-issue': !row._quickEntry && shouldShowReconciliation(row), 'is-release-unchanged': comparisonRowStatus(row) === 'Released', 'is-release-added': comparisonRowStatus(row) === 'Added', 'is-release-modified': comparisonRowStatus(row) === 'Modified', 'is-row-dragging': draggedRowIndex === index, 'is-drag-over-before': dragOverRowIndex === index && dragOverPosition === 'before', 'is-drag-over-after': dragOverRowIndex === index && dragOverPosition === 'after' }">
            <td><input v-if="!row._quickEntry" type="checkbox" aria-label="选择物料" :checked="selectedIds.includes(rowSelectionKey(row) ?? '')" :disabled="!canSelectCurrentView || !rowSelectionKey(row)" @change="toggleRow(rowSelectionKey(row), $event)"></td>
            <td>
              <span v-if="isSourceView" class="pdm-bom-classification-indicator" :class="rowIsClassified(row) ? 'is-classified' : 'is-unclassified'" :aria-label="rowIsClassified(row) ? '已归类' : '未归类'" :title="rowIsClassified(row) ? '已归类' : '未归类'">{{ rowIsClassified(row) ? '✓' : '!' }}</span>
              <div v-else-if="canEditCurrentView && !row._quickEntry" class="pdm-bom-row-actions">
                <span class="pdm-bom-row-drag-handle" :class="{ 'is-disabled': pending }" role="button" tabindex="0" :aria-label="`拖动第 ${index + 1} 行排序`" :title="displayMode === 'Structure' ? `按住拖动第 ${index + 1} 行，在当前层级内排序` : `按住拖动第 ${index + 1} 行`" @pointerdown="startRowPointerDrag(index, $event)">⠿</span>
                <button type="button" class="pdm-bom-row-action pdm-bom-insert-button" :aria-label="`在第 ${index + 1} 行下方插入物料`" :title="`在第 ${index + 1} 行下方插入物料`" :disabled="pending" @click="addRow(index)">+</button>
                <button v-if="!row.id" type="button" class="pdm-bom-row-action pdm-bom-delete-draft-button" :aria-label="`删除未保存的第 ${index + 1} 行`" :title="`删除未保存的第 ${index + 1} 行`" :disabled="pending" @click="removeDraftRow(index)">×</button>
              </div>
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
            <td class="pdm-bom-quantity-audit">
              <template v-if="row._quickEntry"></template>
              <input v-if="isInlineEditing(row, 'quantity')" v-model.number="inlineValue" type="number" min="0.0001" step="0.0001" class="pdm-bom-inline-editor pdm-bom-quantity-editor" aria-label="内联编辑数量" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="!row._quickEntry && row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="quantityTitle(row)" aria-label="编辑数量" @click="beginInlineEdit(row, 'quantity')">{{ quantityDisplay(row) }}</button>
              <input v-else-if="!row._quickEntry && canEditCurrentView && !isSourceView" v-model.number="row.quantity" type="number" min="0.0001" step="0.0001" class="pdm-bom-quantity-editor" aria-label="数量">
              <span v-else-if="!row._quickEntry" class="pdm-bom-cell-value" :title="quantityTitle(row)">{{ quantityDisplay(row) }}</span>
            </td>
            <td class="pdm-bom-quantity-reference" :aria-label="row._quickEntry ? undefined : quantityReferenceTitle(row)" :title="row._quickEntry ? undefined : quantityReferenceTitle(row)">
              <div v-if="!row._quickEntry" class="pdm-bom-quantity-reference-line">
                <span :class="{ 'is-published': publishedQuantityTotal(row) !== undefined }">{{ quantityReferenceDisplay(publishedQuantityTotal(row) ?? 0) }}</span>
                <span :class="{ 'is-total-source-mismatch': sourceQuantityDiffers(row) }"><span>{{ quantityReferenceDisplay(bomQuantityTotal(row)) }}</span><span>/</span><span>{{ quantityReferenceDisplay(sourceQuantityTotal(row)) }}</span></span>
              </div>
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
                <button type="button" class="pdm-bom-issue-button pdm-bom-source" :class="reconciliationIssueTone(row)" :aria-label="`查看问题详情：${reconciliationIssueLabel(row)}`" @click="openIssueDetails(row)">{{ reconciliationIssueLabel(row) }}</button>
                <div v-if="row.manualUnmatched && canEditCurrentView" class="pdm-bom-reconciliation-actions">
                  <button type="button" class="is-retain" :disabled="pending" :aria-label="`确认保留人工BOM项 ${row.drawingNumber || row.name}`" @click="confirmManualRetain(row)">保留</button>
                  <button type="button" class="is-delete" :disabled="pending" :aria-label="`确认删除人工BOM项 ${row.drawingNumber || row.name}`" @click="confirmManualDelete(row)">删除</button>
                </div>
              </div>
              <span v-else-if="!row._quickEntry" class="pdm-bom-cell-value">—</span>
            </td>
            <td v-if="row._quickEntry" class="pdm-bom-data-status-cell"><span class="pdm-bom-data-status">待录入</span></td>
            <td v-else class="pdm-bom-data-status-cell" :class="dataStatusIssues(row).length ? 'is-incomplete' : row.releaseExcluded ? 'is-release-excluded' : 'is-complete'"><span class="pdm-bom-data-status" :class="dataStatusIssues(row).length ? 'is-incomplete' : row.releaseExcluded ? 'is-release-excluded' : 'is-complete'" :title="row.releaseExcluded ? `不发布：${row.releaseExclusionReason || '未记录原因'}` : dataStatusIssues(row).length ? `待完善：${dataStatusIssues(row).join('、')}` : '必填资料及料品主档校验均已通过'">{{ dataStatusLabel(row) }}</span></td>
          </tr>
          <tr v-if="filteredRows.length === 0 && !canShowQuickEntry"><td colspan="19" class="pdm-empty-info">{{ rows.length ? '没有符合当前筛选条件的物料。' : isSourceView ? '当前没有图档源数据。' : '当前BOM为空，系统自动按无此类物料处理；可新增物料或导入标准XLSX。' }}</td></tr>
        </tbody>
      </table>
    </div>
    <div v-if="displayEntries.length" class="pdm-bom-pagination" aria-label="BOM明细分页"><span>{{ displayMode === 'Structure' ? `结构实例 ${rawSourceRows.length} 条 · 当前层级 ${displayEntries.length} 条` : `共 ${displayEntries.length} 条` }}</span><select v-model.number="bomPageSize" aria-label="BOM明细每页条数"><option :value="30">30条/页</option><option :value="50">50条/页</option><option :value="100">100条/页</option><option :value="200">200条/页</option></select><button type="button" class="pdm-secondary-action" aria-label="BOM明细上一页" :disabled="bomPage <= 1" @click="bomPage -= 1">‹</button><span>{{ bomPage }} / {{ bomPageCount }}</span><button type="button" class="pdm-secondary-action" aria-label="BOM明细下一页" :disabled="bomPage >= bomPageCount" @click="bomPage += 1">›</button></div>

    <div v-if="issueDetailRow" class="pdm-dialog-backdrop pdm-bom-issue-backdrop" @click.self="issueDetailRow = null">
      <section class="pdm-bom-issue-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-bom-issue-title">
        <header>
          <div><h3 id="pdm-bom-issue-title">问题详情</h3><p>{{ issueDetailRow.drawingNumber || issueDetailRow.name || '当前物料' }}</p></div>
          <button type="button" class="pdm-icon-button" aria-label="关闭问题详情" @click="issueDetailRow = null">×</button>
        </header>
        <div class="pdm-bom-issue-body">
          <div class="pdm-bom-issue-summary"><span :class="reconciliationIssueTone(issueDetailRow)">{{ reconciliationIssueLabel(issueDetailRow) }}</span><p>{{ reconciliationDescription(issueDetailRow) }}</p></div>
          <div v-if="issueComparisons(issueDetailRow).length" class="pdm-bom-issue-comparisons">
            <div v-for="comparison in issueComparisons(issueDetailRow)" :key="comparison.field" class="pdm-bom-issue-comparison">
              <strong>{{ comparison.field }}</strong>
              <dl><div><dt>{{ comparison.sourceLabel }}</dt><dd>{{ comparison.source }}</dd></div><div><dt>{{ comparison.currentLabel }}</dt><dd>{{ comparison.current }}</dd></div></dl>
            </div>
          </div>
          <div class="pdm-bom-issue-notice">仅用于提醒，不限制保存、审批及后续操作。</div>
        </div>
        <footer><button type="button" class="pdm-primary-action" @click="issueDetailRow = null">知道了</button></footer>
      </section>
    </div>

    <Teleport to="body">
      <div v-if="relationDialogOpen" class="pdm-dialog-backdrop pdm-material-relation-backdrop" @click.self="relationDialogOpen = false">
        <section class="pdm-material-relation-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-material-relation-title">
          <header>
            <div><h3 id="pdm-material-relation-title">BOM 配套选型与完整性</h3><p>每一条主物料分别核对并生成自己的配件行；即使多个主物料选择同一配件，也不会合并或重复计数。</p></div>
            <button type="button" class="pdm-icon-button" aria-label="关闭配套选型" @click="relationDialogOpen = false">×</button>
          </header>
          <div class="pdm-material-relation-body">
            <div v-if="relationLoading" class="pdm-empty-info">正在核对关联配置与 BOM…</div>
            <div v-else-if="!relationCompleteness?.mainMaterials.length" class="pdm-material-relation-empty"><strong>当前 BOM 没有需要选配的主物料</strong><span>标准化部门在料品明细中发布关联配置后，对应主物料会自动出现在这里。</span></div>
            <template v-else>
            <article v-for="main in relationCompleteness?.mainMaterials ?? []" :key="main.mainBomItemId" class="pdm-material-relation-main" :class="{ 'is-complete': main.isComplete }">
              <header><div><strong>{{ main.mainMaterialCode }} · {{ main.mainMaterialName }}</strong><span>主物料数量 {{ main.mainQuantity }} · 模板 V{{ main.revisionVersion }}</span></div><em>{{ main.isComplete ? '配套完整' : '待处理' }}</em></header>
              <section v-for="group in main.groups" :key="group.groupId" class="pdm-material-relation-group" :class="{ 'is-incomplete': !group.isComplete }">
                <div class="pdm-material-relation-group-title"><div><strong>{{ group.groupName }}</strong><span>{{ group.isRequired ? '必选' : '可选' }} · {{ isSingleRelationGroup(group) ? '只能选 1 项' : `可同时选多项（最多 ${group.maxSelection ?? group.options.length} 项）` }}</span></div><em>{{ group.status }}</em></div>
                <el-radio-group v-if="isSingleRelationGroup(group)" :model-value="relationChoice(main.mainBomItemId, group.groupId)[0] ?? ''" @update:model-value="updateRelationSingle(main.mainBomItemId, group.groupId, $event)">
                  <el-radio v-for="option in group.options" :key="option.id" :value="option.id">
                    <span class="pdm-material-relation-option"><strong>{{ option.materialCode }}</strong><span>{{ option.materialName }}</span><small>{{ relationQuantityText(group, option.id, main.mainQuantity) }}</small></span>
                  </el-radio>
                </el-radio-group>
                <el-checkbox-group v-else :model-value="relationChoice(main.mainBomItemId, group.groupId)" @update:model-value="updateRelationMultiple(main.mainBomItemId, group.groupId, $event)">
                  <el-checkbox v-for="option in group.options" :key="option.id" :value="option.id">
                    <span class="pdm-material-relation-option"><strong>{{ option.materialCode }}</strong><span>{{ option.materialName }}</span><small>{{ relationQuantityText(group, option.id, main.mainQuantity) }}</small></span>
                  </el-checkbox>
                </el-checkbox-group>
                <button v-if="!group.isRequired && relationChoice(main.mainBomItemId, group.groupId).length" type="button" class="pdm-link-action" @click="updateRelationChoice(main.mainBomItemId, group.groupId, [])">不选择此组</button>
              </section>
            </article>
            </template>
          </div>
          <footer><span v-if="relationCompleteness">共 {{ relationCompleteness.mainMaterialCount }} 条主物料，{{ relationCompleteness.incompleteGroupCount }} 个配件组待处理</span><button type="button" class="pdm-secondary-action" @click="relationDialogOpen = false">关闭</button><button type="button" class="pdm-primary-action" :disabled="relationApplying || relationLoading || !relationCompleteness?.mainMaterials.length" @click="applySelectedRelations">{{ relationApplying ? '正在加入…' : '加入并核对全部 BOM' }}</button></footer>
        </section>
      </div>
    </Teleport>

    <div v-if="materialReferenceOpen" class="pdm-dialog-backdrop" @click.self="materialReferenceOpen = false">
      <section class="pdm-bom-batch-dialog pdm-material-reference-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-material-reference-title">
        <header><div><h3 id="pdm-material-reference-title">从料品主档引用</h3><p>{{ materialReferenceReason || '可按物料编码、名称或规格搜索，并按品牌筛选；只显示与当前BOM分类一致的已批准料品。' }}</p></div><button type="button" class="pdm-icon-button" aria-label="关闭料品引用" @click="materialReferenceOpen = false">×</button></header>
        <div class="pdm-material-reference-search"><input v-model.trim="materialReferenceBrandInput" list="pdm-material-reference-brands" aria-label="筛选引用料品品牌" placeholder="输入或选择品牌" @keydown.enter.prevent="searchMaterialReferences"><datalist id="pdm-material-reference-brands"><option v-for="brand in materialReferenceBrandOptions" :key="brand" :value="brand" /></datalist><input v-model.trim="materialReferenceQuery" type="search" aria-label="搜索料品主档" placeholder="搜索物料编码、名称或规格" @keydown.enter.prevent="searchMaterialReferences"><button type="button" class="pdm-primary-action" :disabled="materialReferenceLoading" @click="searchMaterialReferences">{{ materialReferenceLoading ? '查询中…' : '查询' }}</button></div>
        <div class="pdm-material-reference-table pdm-table-scroll"><table class="pdm-edit-table"><thead><tr><th>物料编码</th><th>名称</th><th>分类</th><th>引用次数</th><th>规格</th><th>品牌</th><th>备注</th><th>同步</th><th></th></tr></thead><tbody><tr v-for="materialItem in pagedMaterialReferenceResults" :key="materialItem.id"><td>{{ materialItem.materialCode }}</td><td>{{ materialItem.name }}</td><td>{{ materialItem.categoryCode }}</td><td>{{ materialItem.referenceCount ?? 0 }}</td><td>{{ materialItem.specification || '—' }}</td><td>{{ materialItem.brand || '—' }}</td><td>{{ materialItem.remark || '—' }}</td><td>{{ materialItem.syncStatus === 'Succeeded' ? '已同步' : '待同步' }}</td><td><button type="button" class="pdm-secondary-action" @click="applyMaterialReference(materialItem)">引用</button></td></tr><tr v-if="!materialReferenceLoading && filteredMaterialReferenceResults.length === 0"><td colspan="9" class="pdm-empty-info">没有符合条件的已批准料品。</td></tr></tbody></table></div>
        <div v-if="filteredMaterialReferenceResults.length" class="pdm-material-reference-pagination"><span>共 {{ filteredMaterialReferenceResults.length }} 条</span><select v-model.number="materialReferencePageSize" aria-label="料品引用每页条数"><option :value="20">20条/页</option><option :value="50">50条/页</option><option :value="100">100条/页</option></select><button type="button" class="pdm-secondary-action" aria-label="料品引用上一页" :disabled="materialReferencePage <= 1" @click="materialReferencePage -= 1">‹</button><span>{{ materialReferencePage }} / {{ materialReferencePageCount }}</span><button type="button" class="pdm-secondary-action" aria-label="料品引用下一页" :disabled="materialReferencePage >= materialReferencePageCount" @click="materialReferencePage += 1">›</button></div>
      </section>
    </div>

    <Teleport to="body">
      <div v-if="summaryQuantityChoiceOpen" class="pdm-dialog-backdrop pdm-summary-quantity-backdrop" @click.self="closeSummaryQuantityChoice()">
        <section class="pdm-bom-batch-dialog pdm-summary-quantity-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-summary-quantity-title">
          <header><div><h3 id="pdm-summary-quantity-title">分配汇总数量到结构位置</h3><p>汇总数量 {{ formatQuantity(summaryQuantityChoiceCurrentTotal) }} → {{ formatQuantity(summaryQuantityChoiceTargetQuantity) }}；请修改一个或多个结构位置，修改后合计必须等于目标总量。</p></div><button type="button" class="pdm-icon-button" aria-label="关闭结构位置选择" @click="closeSummaryQuantityChoice()">×</button></header>
          <dl v-if="summaryQuantityChoiceMaterial" class="pdm-summary-quantity-material"><div><dt>物料编码</dt><dd>{{ displayValue(summaryQuantityChoiceMaterial.drawingNumber) }}</dd></div><div><dt>物料名称</dt><dd>{{ displayValue(summaryQuantityChoiceMaterial.name) }}</dd></div><div><dt>型号</dt><dd>{{ displayValue(summaryQuantityChoiceMaterial.specification) }}</dd></div><div><dt>品牌</dt><dd>{{ displayValue(summaryQuantityChoiceMaterial.brand) }}</dd></div></dl>
          <div class="pdm-table-scroll"><table class="pdm-edit-table"><thead><tr><th>结构位置</th><th>上层装配体名称</th><th>当前数量</th><th>修改后数量</th></tr></thead><tbody><tr v-for="(candidate, index) in summaryQuantityChoiceCandidates" :key="candidate.item.id || index" :class="{ 'is-summary-quantity-changed': candidate.item.id && Number(candidate.quantity) !== Number(candidate.item.quantity), 'is-summary-quantity-removed': candidate.item.id && Number(candidate.quantity) === 0 }"><td :title="summaryQuantityLocation(candidate.item, index)">{{ summaryQuantityLocation(candidate.item, index) }}</td><td :title="summaryQuantityParentAssemblyTitle(candidate.item)">{{ summaryQuantityParentAssemblyName(candidate.item) }}</td><td>{{ formatQuantity(Number(candidate.item.quantity)) }}</td><td><div v-if="candidate.item.id" class="pdm-summary-quantity-input"><input :value="candidate.quantity" type="number" min="0" step="0.0001" :aria-label="`修改后数量 ${summaryQuantityLocation(candidate.item, index)}`" @input="updateSummaryQuantityCandidate(candidate, $event)"><small v-if="Number(candidate.quantity) === 0">将删除此位置</small></div></td></tr></tbody></table></div>
          <footer><div class="pdm-summary-quantity-status"><span>当前总量 <strong>{{ formatQuantity(summaryQuantityChoiceCurrentTotal) }}</strong></span><span>目标总量 <strong>{{ formatQuantity(summaryQuantityChoiceTargetQuantity) }}</strong></span><span>已分配差额 <strong>{{ Number.isFinite(summaryQuantityChoiceResultTotal) ? formatQuantity(summaryQuantityChoiceResultTotal - summaryQuantityChoiceCurrentTotal) : '—' }}</strong></span><span :class="{ 'is-invalid': !Number.isFinite(summaryQuantityChoiceRemaining) || Math.abs(summaryQuantityChoiceRemaining) >= 0.00005 }">待分配 <strong>{{ Number.isFinite(summaryQuantityChoiceRemaining) ? formatQuantity(summaryQuantityChoiceRemaining) : '—' }}</strong></span><em v-if="summaryQuantityChoiceZeroCount">其中 {{ summaryQuantityChoiceZeroCount }} 个位置数量为 0，确认时将再次提醒删除。</em></div><button type="button" class="pdm-secondary-action" @click="closeSummaryQuantityChoice()">取消</button><button type="button" class="pdm-primary-action" :disabled="!canConfirmSummaryQuantityChoice" @click="confirmSummaryQuantityChoice">确认分配</button></footer>
        </section>
      </div>
    </Teleport>

    <Teleport to="body">
      <div v-if="duplicateMaterialChoiceOpen" class="pdm-dialog-backdrop" @click.self="closeDuplicateMaterialChoice()">
        <section class="pdm-bom-batch-dialog pdm-material-reference-dialog pdm-duplicate-material-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-duplicate-material-title">
          <header><div><h3 id="pdm-duplicate-material-title">重复料品，请确认选择</h3><p>{{ duplicateMaterialChoiceReason }}</p></div><button type="button" class="pdm-icon-button" aria-label="关闭重复料品确认" @click="closeDuplicateMaterialChoice()">×</button></header>
          <div class="pdm-material-reference-table pdm-table-scroll"><table class="pdm-edit-table"><thead><tr><th>物料编码</th><th>名称</th><th>分类</th><th>规格</th><th>品牌</th><th>备注</th><th></th></tr></thead><tbody><tr v-for="materialItem in duplicateMaterialChoiceCandidates" :key="materialItem.id"><td>{{ materialItem.materialCode }}</td><td>{{ materialItem.name }}</td><td>{{ materialItem.categoryCode }}</td><td>{{ materialItem.specification || '—' }}</td><td>{{ materialItem.brand || '—' }}</td><td>{{ materialItem.remark || '—' }}</td><td><button type="button" class="pdm-primary-action" @click="closeDuplicateMaterialChoice(materialItem)">确认选择</button></td></tr></tbody></table></div>
          <footer><span>本次选择仅用于当前操作，下次遇到重复料品仍会再次确认。</span><button type="button" class="pdm-secondary-action" @click="closeDuplicateMaterialChoice()">取消</button></footer>
        </section>
      </div>
    </Teleport>

    <div v-if="exportDialogOpen" class="pdm-dialog-backdrop pdm-export-dialog-backdrop" @click.self="exportDialogOpen = false">
      <section class="pdm-bom-batch-dialog pdm-export-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-export-dialog-title">
        <header><div><h3 id="pdm-export-dialog-title">导出BOM</h3><p>请选择 Excel 中的明细组织方式。</p></div><button type="button" class="pdm-icon-button" aria-label="关闭导出BOM" @click="exportDialogOpen = false">×</button></header>
        <div class="pdm-export-options" role="radiogroup" aria-label="BOM导出方式">
          <label :class="{ 'is-selected': exportMode === 'Summary' }"><input v-model="exportMode" type="radio" value="Summary"><span><strong>汇总导出</strong><small>相同物料编码及单位合并，并累计数量。</small></span></label>
          <label :class="{ 'is-selected': exportMode === 'Structure' }"><input v-model="exportMode" type="radio" value="Structure"><span><strong>结构导出</strong><small>保留当前 BOM 的结构明细和原序号。</small></span></label>
        </div>
        <footer><span>默认使用汇总导出。</span><button type="button" class="pdm-secondary-action" @click="exportDialogOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="confirmExport">确认导出</button></footer>
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

    <el-drawer v-model="reconciliationDrawerOpen" class="pdm-bom-reconciliation-drawer" title="机械BOM对账明细" size="min(1180px, 94vw)" :close-on-click-modal="false">
      <section class="pdm-reconciliation-workspace" aria-label="机械BOM对账预览">
        <div class="pdm-reconciliation-scope">
          <div><small>只读输入</small><strong>最新归档设计树／图档源数据</strong></div>
          <span aria-hidden="true">→</span>
          <div><small>只读差异提醒</small><strong>当前机械BOM工作区</strong><em>仅核对，不自动更新</em></div>
          <div class="is-unaffected"><small>不受影响</small><strong>机械BOM · 电气BOM · 已发布BOM版本</strong></div>
        </div>

        <div v-if="reconciliationPreviewLoading" class="pdm-reconciliation-loading" role="status" aria-live="polite">
          <strong>正在读取最新图档源数据并计算机械BOM差异…</strong>
          <span>数据量较大时可能需要几十秒，请勿重复点击。</span>
          <i aria-hidden="true"><b></b></i>
        </div>
        <div v-else-if="reconciliationPreviewError" class="pdm-reconciliation-error" role="alert">
          <div><strong>对账明细生成失败</strong><span>{{ reconciliationPreviewError }}</span></div>
          <button type="button" class="pdm-secondary-action" :disabled="pending" @click="requestGenerate">重试</button>
        </div>
        <template v-else-if="reconciliationPreview">
          <div class="pdm-reconciliation-summary" aria-label="机械BOM变更汇总">
            <button type="button" :class="{ 'is-active': reconciliationFilter === 'All' }" @click="reconciliationFilter = 'All'"><small>变更物料</small><strong>{{ reconciliationSummary.total }}</strong><em>{{ reconciliationSummary.instances }} 个实例</em></button>
            <button type="button" :class="{ 'is-active': reconciliationFilter === 'Added' }" @click="reconciliationFilter = 'Added'"><small>新增</small><strong>{{ reconciliationSummary.added }}</strong></button>
            <button type="button" :class="{ 'is-active': reconciliationFilter === 'Quantity' }" @click="reconciliationFilter = 'Quantity'"><small>数量变化</small><strong>{{ reconciliationSummary.quantity }}</strong></button>
            <button type="button" :class="{ 'is-active': reconciliationFilter === 'Attribute' }" @click="reconciliationFilter = 'Attribute'"><small>属性变化</small><strong>{{ reconciliationSummary.attribute }}</strong></button>
            <button type="button" :class="{ 'is-active': reconciliationFilter === 'Classification' }" @click="reconciliationFilter = 'Classification'"><small>分类变化</small><strong>{{ reconciliationSummary.classification }}</strong></button>
            <button type="button" :class="{ 'is-active': reconciliationFilter === 'Manual' }" @click="reconciliationFilter = 'Manual'"><small>人工值保留</small><strong>{{ reconciliationSummary.manual }}</strong></button>
            <button type="button" :class="{ 'is-active': reconciliationFilter === 'Removed' }" @click="reconciliationFilter = 'Removed'"><small>待移除</small><strong>{{ reconciliationSummary.removed }}</strong></button>
          </div>
          <div class="pdm-reconciliation-note">本页只计算并显示差异，不写入机械BOM。新增、删除、分类、数量和属性差异均需人工在对应BOM中处理并保存。</div>
          <div class="pdm-reconciliation-table-wrap">
            <table class="pdm-reconciliation-table">
              <thead><tr><th>物料标识</th><th>字段</th><th>当前机械BOM</th><th>最新源数据</th><th>实例</th></tr></thead>
              <tbody>
                <tr v-for="detail in filteredReconciliationDetails" :key="detail.key">
                  <td :title="detail.item">{{ detail.item }}</td><td>{{ detail.field }}</td><td :title="detail.current">{{ detail.current }}</td><td :title="detail.source">{{ detail.source }}</td><td>{{ detail.instances }}</td>
                </tr>
                <tr v-if="filteredReconciliationDetails.length === 0"><td colspan="8" class="pdm-empty-info">{{ reconciliationDetails.length ? '当前筛选项没有变更。' : '当前机械BOM已与最新图档源数据一致。' }}</td></tr>
              </tbody>
            </table>
          </div>
        </template>
        <footer class="pdm-reconciliation-footer">
          <span>该页面为只读提醒，关闭后不会写入任何数据。</span>
          <button type="button" class="pdm-primary-action" @click="reconciliationDrawerOpen = false">关闭</button>
        </footer>
      </section>
    </el-drawer>

    <el-drawer v-model="releaseDrawerOpen" class="pdm-bom-release-drawer" :title="kind === 'Standard' ? '标准件BOM审批发布' : kind === 'NonStandard' ? '非标件BOM与图纸发布' : '电气BOM审批发布'" size="min(1360px, 96vw)" destroy-on-close>
      <div class="pdm-bom-release-workspace">
        <aside class="pdm-bom-release-history" aria-label="发布包记录">
          <header><strong>发布记录</strong><button v-if="canManageRelease" type="button" class="pdm-secondary-action pdm-release-new-button" @click="selectReleasePackage('')">新建发布包</button></header>
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
          :long-lead-published-items="publishedLongLeadItems"
          :previous-version-items="previousReleaseVersionItems"
          @create="emit('releaseCreate', $event)"
          @update-draft="(releasePackageId, input) => emit('releaseUpdateDraft', releasePackageId, input)"
          @delete-draft="releasePackageId => emit('releaseDeleteDraft', releasePackageId)"
          @upload="(releasePackageId, file) => emit('releaseUpload', releasePackageId, file)"
          @submit="releasePackageId => emit('releaseSubmit', releasePackageId)"
          @withdraw="releasePackageId => emit('releaseWithdraw', releasePackageId)"
          @retry-u9="releasePackageId => emit('releaseRetryU9', releasePackageId)"
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
.pdm-export-dialog-backdrop{align-items:center;justify-content:center;padding:12px}.pdm-export-dialog{width:min(440px,calc(100vw - 24px));height:auto;max-height:calc(100vh - 24px);border-radius:8px;animation:none}.pdm-export-options{display:grid;gap:10px;padding:18px}.pdm-export-options>label{display:flex;align-items:flex-start;gap:10px;padding:12px;border:1px solid var(--pdm-border);border-radius:7px;cursor:pointer}.pdm-export-options>label.is-selected{border-color:var(--pdm-theme-accent);background:var(--pdm-theme-accent-soft);box-shadow:0 0 0 1px var(--pdm-theme-accent)}.pdm-export-options input{margin-top:2px}.pdm-export-options span{display:grid;gap:3px}.pdm-export-options small{color:var(--pdm-muted);font-weight:400}
.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation){font-size:12px}
.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation-header),.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation-cell){text-align:center}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation-cell){overflow:visible;vertical-align:middle;white-space:normal}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation){min-width:0;overflow:visible;text-align:center;white-space:normal;overflow-wrap:anywhere;line-height:1.35}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation .pdm-bom-source){display:block;overflow:visible;text-overflow:clip;white-space:normal;overflow-wrap:anywhere}.pdm-bom-manager-panel :deep(.pdm-bom-reconciliation .pdm-bom-source.is-critical){color:var(--pdm-danger);font-weight:700}
.pdm-bom-issue-button{width:100%;min-height:26px;padding:3px 5px;border:0;border-radius:4px;background:transparent;font:inherit;line-height:1.35;cursor:pointer}.pdm-bom-issue-button:hover,.pdm-bom-issue-button:focus-visible{background:#ffedd5;outline:1px solid #fdba74}.pdm-bom-issue-button.is-warning{color:#b45309;font-weight:700}.pdm-bom-issue-button.is-critical{color:var(--pdm-danger);font-weight:700}.pdm-bom-issue-backdrop{align-items:center;justify-content:center;padding:16px}.pdm-bom-issue-dialog{display:flex;width:min(620px,calc(100vw - 32px));max-height:calc(100vh - 32px);flex-direction:column;overflow:hidden;border-radius:9px;background:#fff;box-shadow:0 20px 45px rgba(15,23,42,.22)}.pdm-bom-issue-dialog>header{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;padding:16px 18px;border-bottom:1px solid var(--pdm-border)}.pdm-bom-issue-dialog h3,.pdm-bom-issue-dialog p{margin:0}.pdm-bom-issue-dialog header p{margin-top:4px;color:var(--pdm-muted)}.pdm-bom-issue-body{display:grid;gap:14px;overflow:auto;padding:18px}.pdm-bom-issue-summary{display:grid;gap:7px}.pdm-bom-issue-summary>span{width:max-content;padding:3px 8px;border-radius:999px;background:#fff7ed;color:#b45309;font-weight:700}.pdm-bom-issue-summary>span.is-critical{background:#fef2f2;color:var(--pdm-danger)}.pdm-bom-issue-summary p{color:var(--pdm-text);line-height:1.55}.pdm-bom-issue-comparisons{display:grid;gap:10px}.pdm-bom-issue-comparison{display:grid;gap:7px;padding:11px;border:1px solid #fed7aa;border-radius:7px;background:#fffbeb}.pdm-bom-issue-comparison dl{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin:0}.pdm-bom-issue-comparison dl>div{min-width:0;padding:9px;border-radius:5px;background:#fff}.pdm-bom-issue-comparison dt{color:var(--pdm-muted);font-size:11px}.pdm-bom-issue-comparison dd{margin:5px 0 0;overflow-wrap:anywhere;color:var(--pdm-text);font-weight:700}.pdm-bom-issue-notice{padding:9px 11px;border-radius:6px;background:#f0fdf4;color:#166534}.pdm-bom-issue-dialog>footer{display:flex;justify-content:flex-end;padding:12px 18px;border-top:1px solid var(--pdm-border)}
.pdm-bom-manager-panel :deep(.pdm-bom-drawing-audit-header),.pdm-bom-manager-panel :deep(.pdm-bom-drawing-audit-cell){text-align:center;vertical-align:middle}.pdm-bom-manager-panel :deep(.pdm-bom-drawing-audit-cell .pdm-bom-audit){width:100%;align-items:center;justify-content:center;text-align:center}
.pdm-bom-manager-panel :deep(.pdm-bom-table){width:100%;max-width:100%;table-layout:fixed}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-select){width:28px}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-row-actions){width:72px}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-sequence),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-unit),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-weight),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-revision){width:2.43902439%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-kind),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-brand),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-material),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-surface),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-quantity),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-drawing-audit),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-data-status){width:4.87804878%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-code),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-name),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-parent-code){width:7.31707317%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-model){width:14.63414634%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-remark),.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-source){width:9.75609756%}
.pdm-bom-manager-panel :deep(.pdm-bom-table col:is(.is-sequence,.is-unit,.is-weight,.is-revision)){width:2.27272727%}.pdm-bom-manager-panel :deep(.pdm-bom-table col:is(.is-kind,.is-brand,.is-material,.is-surface,.is-quantity,.is-drawing-audit,.is-data-status)){width:4.54545455%}.pdm-bom-manager-panel :deep(.pdm-bom-table col:is(.is-name,.is-parent-code,.is-quantity-reference)){width:6.81818182%}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-code){width:120px}.pdm-bom-manager-panel :deep(.pdm-bom-table col.is-model){width:13.63636364%}.pdm-bom-manager-panel :deep(.pdm-bom-table col:is(.is-remark,.is-source)){width:9.09090909%}
.pdm-bom-manager-panel :deep(.pdm-bom-quantity-audit),.pdm-bom-manager-panel :deep(.pdm-bom-quantity-reference){text-align:center;vertical-align:middle;white-space:nowrap}.pdm-bom-manager-panel :deep(.pdm-bom-quantity-reference){overflow:hidden;text-overflow:ellipsis}.pdm-bom-manager-panel :deep(.pdm-bom-quantity-reference-line){display:inline-flex;max-width:100%;align-items:center;justify-content:center;gap:8px}.pdm-bom-manager-panel :deep(.pdm-bom-quantity-reference span){display:inline}.pdm-bom-manager-panel :deep(.pdm-bom-quantity-reference .is-published){color:var(--pdm-green);font-weight:700}.pdm-bom-manager-panel :deep(.pdm-bom-quantity-reference .is-total-source-mismatch){color:var(--pdm-orange);font-weight:700}.pdm-bom-discard-action{border-color:var(--pdm-orange);background:var(--pdm-orange-soft);color:var(--pdm-orange)}
.pdm-bom-display-control{display:flex;align-items:center;gap:3px;margin-left:auto;padding:2px 3px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff;white-space:nowrap}.pdm-bom-display-control>span{padding:0 4px;color:var(--pdm-muted);font-size:11px}.pdm-bom-display-control button{min-width:42px;height:24px;padding:0 8px;border:0;border-radius:4px;background:transparent;color:var(--pdm-muted);cursor:pointer}.pdm-bom-display-control button.is-active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);font-weight:700}.pdm-bom-display-control small{padding:0 5px;color:var(--pdm-muted);font-size:11px}.pdm-bom-structure-code-cell{white-space:nowrap}.pdm-bom-structure-indent{display:inline-flex;align-items:center;margin-left:calc(var(--pdm-bom-depth) * 15px);margin-right:3px;vertical-align:middle}.pdm-bom-structure-toggle,.pdm-bom-structure-spacer{display:inline-grid;width:18px;height:18px;place-items:center}.pdm-bom-structure-toggle{padding:0;border:1px solid var(--pdm-theme-accent-border);border-radius:3px;background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);font-size:13px;line-height:16px;cursor:pointer}.pdm-bom-structure-spacer::before{content:'·';color:#94a3b8}.pdm-bom-structure-instance{color:var(--pdm-theme-accent);font-weight:700}.pdm-bom-structure-summary{padding:8px 10px;border-top:1px solid var(--pdm-border);color:var(--pdm-muted);font-size:11px;text-align:right}
.pdm-bom-model-value{display:flex;min-width:0;align-items:center;gap:3px}.pdm-bom-model-value>:not(.pdm-bom-drawing-name-warning){min-width:0;flex:1}.pdm-bom-drawing-name-warning{display:inline-flex;flex:0 0 auto;align-items:center;justify-content:center;color:#d97706;font-size:13px;line-height:1;cursor:default}.pdm-bom-pagination{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding:8px 10px;color:var(--pdm-muted);font-size:11px}.pdm-bom-pagination select{height:28px;padding:0 24px 0 8px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;color:var(--pdm-text)}.pdm-bom-pagination .pdm-secondary-action{width:28px;min-width:28px;height:28px;min-height:28px;padding:0}
.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr:is(.is-data-exception,.is-reconciliation-issue)>td){background:#fffbeb}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr:is(.is-data-exception,.is-reconciliation-issue):hover>td){background:#fef3c7}
.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-excluded:not(.is-data-exception):not(.is-reconciliation-issue)>td){background:#f1f5f9;color:#64748b}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-excluded:not(.is-data-exception):not(.is-reconciliation-issue):hover>td){background:#e2e8f0}.pdm-bom-manager-panel :deep(.pdm-bom-data-status.is-release-excluded){background:#e2e8f0;color:#475569}.pdm-bom-no-publish-action{border-color:#94a3b8;color:#475569}
.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-unchanged:not(.is-data-exception):not(.is-reconciliation-issue)>td){background:#f0fdf4}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-unchanged:not(.is-data-exception):not(.is-reconciliation-issue):hover>td){background:#dcfce7}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-added:not(.is-data-exception):not(.is-reconciliation-issue)>td){background:#eff6ff}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-added:not(.is-data-exception):not(.is-reconciliation-issue):hover>td){background:#dbeafe}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-modified:not(.is-data-exception):not(.is-reconciliation-issue)>td){background:#fff7ed}.pdm-bom-manager-panel :deep(.pdm-bom-table tbody tr.is-release-modified:not(.is-data-exception):not(.is-reconciliation-issue):hover>td){background:#ffedd5}
.pdm-bom-comparison-filters{display:flex;align-items:center;gap:5px;flex-wrap:wrap}.pdm-bom-comparison-filters button{min-height:24px;padding:2px 8px;border:1px solid var(--pdm-border);border-radius:999px;background:#fff;color:var(--pdm-muted);font-size:10px;cursor:pointer}.pdm-bom-comparison-filters button.is-active{border-color:var(--pdm-blue);box-shadow:0 0 0 1px var(--pdm-blue);color:var(--pdm-text);font-weight:700}.pdm-bom-comparison-filters button.is-released{background:#f0fdf4;color:#15803d}.pdm-bom-comparison-filters button.is-added{background:#eff6ff;color:#2563eb}.pdm-bom-comparison-filters button.is-modified{background:#fff7ed;color:#c2410c}.pdm-bom-comparison-filters button.is-removed{background:#fef2f2;color:#b91c1c}
.pdm-bom-release-strip{display:flex;align-items:center;gap:12px;padding:8px 11px;border:1px solid #bfdbfe;border-radius:7px;background:#eff6ff;font-size:12px;white-space:nowrap}.pdm-bom-release-strip>div{display:flex;align-items:center;gap:5px;white-space:nowrap}.pdm-bom-release-strip small,.pdm-bom-release-strip strong{font-size:12px;line-height:1.2;white-space:nowrap}.pdm-bom-release-strip small{color:var(--pdm-muted)}.pdm-bom-release-strip strong.is-active{color:#b45309}.pdm-bom-release-strip-actions{display:flex!important;align-items:center;gap:6px;margin-left:auto}.pdm-bom-release-strip-actions button{box-sizing:border-box;width:70px;min-width:70px;height:28px;min-height:28px;padding:4px 10px;font-size:12px;line-height:18px;white-space:nowrap}.pdm-bom-release-workspace{display:grid;grid-template-columns:210px minmax(0,1fr);gap:12px;min-height:100%}.pdm-bom-release-history{border:1px solid var(--pdm-border);border-radius:7px;overflow:auto;background:#fff}.pdm-bom-release-history header{position:sticky;top:0;z-index:1;display:flex;align-items:center;justify-content:space-between;gap:8px;padding:10px;border-bottom:1px solid var(--pdm-border);background:#fff}.pdm-bom-release-history header .pdm-release-new-button{flex:0 0 auto;min-height:28px;padding:4px 8px}.pdm-bom-release-history>button{display:flex;width:100%;justify-content:space-between;gap:8px;padding:10px;border:0;border-bottom:1px solid var(--pdm-border);background:#fff;text-align:left;color:var(--pdm-text)}.pdm-bom-release-history>button:hover,.pdm-bom-release-history>button.is-active{background:var(--pdm-blue-soft)}.pdm-bom-release-history>button span{display:grid;gap:3px;min-width:0}.pdm-bom-release-history>button small{overflow:hidden;text-overflow:ellipsis;color:var(--pdm-muted)}.pdm-bom-release-history>button em{font-style:normal;color:var(--pdm-blue);white-space:nowrap}.pdm-bom-release-history>p{padding:12px;color:var(--pdm-muted)}.pdm-bom-release-workspace .release-center{min-width:0;margin:0}@media(max-width:900px){.pdm-bom-release-strip{align-items:flex-start;flex-wrap:wrap}.pdm-bom-release-strip-actions{margin-left:0}.pdm-bom-release-workspace{grid-template-columns:1fr}.pdm-bom-release-history{max-height:180px}}
.pdm-bom-release-strip{box-sizing:border-box;height:38px;min-height:38px;padding-block:4px;background:#fff}
.pdm-bom-unsaved-count{padding:3px 7px;border:1px solid #f59e0b;border-radius:999px;background:#fffbeb;color:#b45309;font-weight:700;white-space:nowrap}
.pdm-reconciliation-workspace{display:flex;height:100%;min-height:0;flex-direction:column;gap:12px}.pdm-reconciliation-scope{display:grid;grid-template-columns:minmax(210px,1fr) auto minmax(260px,1.2fr) minmax(240px,1fr);align-items:stretch;gap:10px;padding:12px;border:1px solid var(--pdm-theme-accent-border);border-radius:8px;background:var(--pdm-theme-accent-soft)}.pdm-reconciliation-scope>div{display:grid;align-content:center;gap:3px;padding:9px 11px;border:1px solid color-mix(in srgb,var(--pdm-theme-accent) 24%,white);border-radius:6px;background:#fff}.pdm-reconciliation-scope>span{align-self:center;color:var(--pdm-theme-accent);font-size:22px}.pdm-reconciliation-scope small{color:var(--pdm-muted)}.pdm-reconciliation-scope strong{color:var(--pdm-text)}.pdm-reconciliation-scope em{color:var(--pdm-theme-accent);font-style:normal}.pdm-reconciliation-scope .is-unaffected{border-color:var(--pdm-border);background:#f8fafc}.pdm-reconciliation-loading,.pdm-reconciliation-error{display:flex;min-height:96px;align-items:center;justify-content:center;gap:14px;padding:18px;border:1px solid var(--pdm-border);border-radius:8px;background:#f8fafc;text-align:center}.pdm-reconciliation-loading{flex-direction:column}.pdm-reconciliation-loading span,.pdm-reconciliation-error span{color:var(--pdm-muted)}.pdm-reconciliation-loading i{display:block;width:min(520px,75%);height:6px;overflow:hidden;border-radius:999px;background:#dbeafe}.pdm-reconciliation-loading b{display:block;width:35%;height:100%;border-radius:inherit;background:var(--pdm-theme-accent);animation:pdm-reconciliation-progress 1.2s ease-in-out infinite}.pdm-reconciliation-error{justify-content:space-between;border-color:#fecaca;background:#fef2f2;text-align:left}.pdm-reconciliation-error>div{display:grid;gap:3px}.pdm-reconciliation-error strong{color:var(--pdm-danger)}@keyframes pdm-reconciliation-progress{0%{transform:translateX(-100%)}100%{transform:translateX(390%)}}
.pdm-reconciliation-summary{display:grid;grid-template-columns:repeat(7,minmax(88px,1fr));gap:7px}.pdm-reconciliation-summary button{display:grid;min-height:58px;place-items:center;padding:6px;border:1px solid var(--pdm-border);border-radius:7px;background:#fff;color:var(--pdm-text);cursor:pointer}.pdm-reconciliation-summary button.is-active{border-color:var(--pdm-theme-accent);box-shadow:0 0 0 1px var(--pdm-theme-accent)}.pdm-reconciliation-summary small{color:var(--pdm-muted)}.pdm-reconciliation-summary strong{font-size:18px!important}.pdm-reconciliation-summary em{color:var(--pdm-muted);font-size:10px;font-style:normal}.pdm-reconciliation-note{padding:8px 10px;border-left:3px solid var(--pdm-theme-accent);background:var(--pdm-theme-accent-soft);color:var(--pdm-muted);line-height:1.5}.pdm-reconciliation-table-wrap{min-height:0;flex:1;overflow:auto;border:1px solid var(--pdm-border);border-radius:8px}.pdm-reconciliation-table{width:100%;border-collapse:collapse;table-layout:fixed}.pdm-reconciliation-table th,.pdm-reconciliation-table td{padding:8px;border-bottom:1px solid var(--pdm-border);vertical-align:middle;text-align:center;white-space:normal;overflow-wrap:anywhere}.pdm-reconciliation-table th{position:sticky;top:0;z-index:1;background:#f8fafc}.pdm-reconciliation-table th:nth-child(1){width:220px}.pdm-reconciliation-table th:nth-child(2){width:100px}.pdm-reconciliation-table th:nth-child(5){width:64px}.pdm-reconciliation-footer{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding-top:10px;border-top:1px solid var(--pdm-border)}.pdm-reconciliation-footer>span{margin-right:auto;color:var(--pdm-muted)}
@media(max-width:900px){.pdm-bom-release-strip{height:auto}}
@media(max-width:900px){.pdm-reconciliation-scope{grid-template-columns:1fr}.pdm-reconciliation-scope>span{display:none}.pdm-reconciliation-summary{grid-template-columns:repeat(2,minmax(100px,1fr))}}
</style>
<style scoped>
.pdm-material-code-action{height:22px;padding:0 7px;border:1px solid var(--shell-accent-border);border-radius:5px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:11px;line-height:20px;white-space:nowrap;cursor:pointer}.pdm-material-code-action.is-review{border-color:#f59e0b;background:#fffbeb;color:#b45309}.pdm-material-code-state{color:#64748b;font-size:11px;white-space:nowrap}.pdm-material-code-state.is-pending{color:#b45309}
.pdm-duplicate-material-dialog .pdm-material-reference-table table{width:100%;min-width:0;table-layout:fixed}.pdm-duplicate-material-dialog .pdm-material-reference-table :is(th,td){min-width:0;overflow:hidden;text-overflow:ellipsis}.pdm-duplicate-material-dialog .pdm-material-reference-table :is(th,td):nth-child(1){width:140px}.pdm-duplicate-material-dialog .pdm-material-reference-table :is(th,td):nth-child(2){width:160px}.pdm-duplicate-material-dialog .pdm-material-reference-table :is(th,td):nth-child(3){width:90px}.pdm-duplicate-material-dialog .pdm-material-reference-table :is(th,td):nth-child(4){width:150px}.pdm-duplicate-material-dialog .pdm-material-reference-table :is(th,td):nth-child(5){width:120px}.pdm-duplicate-material-dialog .pdm-material-reference-table :is(th,td):last-child{width:110px;min-width:110px}
.pdm-summary-quantity-backdrop{align-items:center;justify-content:center!important;padding:16px}.pdm-summary-quantity-dialog{width:min(900px,calc(100vw - 32px));height:auto;max-height:calc(100vh - 32px);border-radius:9px;animation:none}.pdm-summary-quantity-material{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px;margin:0;padding:12px 18px;border-bottom:1px solid var(--pdm-border);background:#f8fafc}.pdm-summary-quantity-material>div{min-width:0}.pdm-summary-quantity-material dt{color:var(--pdm-muted);font-size:10px}.pdm-summary-quantity-material dd{margin:4px 0 0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--pdm-text);font-weight:700}.pdm-summary-quantity-dialog .pdm-table-scroll{max-height:min(460px,calc(100vh - 300px))}.pdm-summary-quantity-dialog table{width:100%;table-layout:fixed}.pdm-summary-quantity-dialog th:first-child{width:38%}.pdm-summary-quantity-dialog th:nth-child(2){width:24%}.pdm-summary-quantity-dialog th:nth-child(3){width:14%}.pdm-summary-quantity-dialog th:nth-child(4){width:24%}.pdm-summary-quantity-dialog td{overflow:hidden;text-overflow:ellipsis}.pdm-summary-quantity-input{display:flex;min-width:0;align-items:center;gap:7px}.pdm-summary-quantity-input input{box-sizing:border-box;width:100px;height:30px;padding:4px 8px;border:1px solid var(--pdm-border);border-radius:5px;color:var(--pdm-text);text-align:right}.pdm-summary-quantity-input input:focus{border-color:var(--pdm-theme-accent);outline:2px solid var(--pdm-theme-accent-soft)}.pdm-summary-quantity-input small{color:var(--pdm-danger);font-weight:700;white-space:nowrap}.pdm-summary-quantity-dialog tr.is-summary-quantity-changed td{background:#eff6ff}.pdm-summary-quantity-dialog tr.is-summary-quantity-removed td{background:#fef2f2}.pdm-summary-quantity-dialog>footer{align-items:flex-end;flex-wrap:wrap}.pdm-summary-quantity-status{display:flex;min-width:360px;flex:1;align-items:center;gap:8px;flex-wrap:wrap}.pdm-summary-quantity-dialog>footer .pdm-summary-quantity-status span{min-width:auto;flex:0 0 auto;padding:5px 8px;border-radius:5px;background:#f8fafc;color:var(--pdm-muted);font-size:10px}.pdm-summary-quantity-status span.is-invalid{background:#fff7ed;color:#b45309}.pdm-summary-quantity-status strong{color:var(--pdm-text)}.pdm-summary-quantity-status em{width:100%;color:var(--pdm-danger);font-size:10px;font-style:normal;font-weight:700}@media(max-width:760px){.pdm-summary-quantity-material{grid-template-columns:repeat(2,minmax(0,1fr))}.pdm-summary-quantity-status{min-width:100%}}
.pdm-bom-relation-action.is-warning{border-color:#f59e0b;background:#fffbeb;color:#b45309}.pdm-bom-relation-action.is-complete{border-color:#86efac;background:#f0fdf4;color:#15803d}.pdm-material-relation-backdrop{align-items:center;justify-content:center!important;padding:16px}.pdm-material-relation-dialog{display:flex;width:min(960px,calc(100vw - 32px));max-height:calc(100vh - 32px);flex-direction:column;overflow:hidden;border-radius:9px;background:#fff;box-shadow:0 20px 45px rgba(15,23,42,.22)}.pdm-material-relation-dialog>header{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;padding:16px 18px;border-bottom:1px solid var(--pdm-border)}.pdm-material-relation-dialog h3,.pdm-material-relation-dialog p{margin:0}.pdm-material-relation-dialog header p{margin-top:4px;color:var(--pdm-muted);line-height:1.5}.pdm-material-relation-body{display:grid;gap:12px;min-height:180px;overflow:auto;padding:16px 18px;background:#f8fafc}.pdm-material-relation-empty{display:grid;place-content:center;gap:7px;min-height:180px;text-align:center}.pdm-material-relation-empty span{color:var(--pdm-muted)}.pdm-material-relation-main{overflow:hidden;border:1px solid #f59e0b;border-radius:8px;background:#fff}.pdm-material-relation-main.is-complete{border-color:#86efac}.pdm-material-relation-main>header{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:11px 13px;background:#fffbeb}.pdm-material-relation-main.is-complete>header{background:#f0fdf4}.pdm-material-relation-main>header div{display:grid;gap:3px}.pdm-material-relation-main>header span{color:var(--pdm-muted);font-size:11px}.pdm-material-relation-main>header em{color:#b45309;font-style:normal;font-weight:700}.pdm-material-relation-main.is-complete>header em{color:#15803d}.pdm-material-relation-group{display:grid;gap:9px;padding:12px 13px;border-top:1px solid var(--pdm-border)}.pdm-material-relation-group.is-incomplete{box-shadow:inset 3px 0 #f59e0b}.pdm-material-relation-group-title{display:flex;align-items:center;justify-content:space-between;gap:12px}.pdm-material-relation-group-title>div{display:flex;align-items:center;gap:8px}.pdm-material-relation-group-title span{color:var(--pdm-muted);font-size:11px}.pdm-material-relation-group-title em{padding:2px 7px;border-radius:999px;background:#f1f5f9;color:#475569;font-size:11px;font-style:normal}.pdm-material-relation-group :deep(.el-radio-group),.pdm-material-relation-group :deep(.el-checkbox-group){display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.pdm-material-relation-group :deep(.el-radio),.pdm-material-relation-group :deep(.el-checkbox){box-sizing:border-box;width:100%;height:auto;min-height:52px;margin:0;padding:8px 10px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff;white-space:normal}.pdm-material-relation-group :deep(.el-radio.is-checked),.pdm-material-relation-group :deep(.el-checkbox.is-checked){border-color:var(--pdm-theme-accent);background:var(--pdm-theme-accent-soft)}.pdm-material-relation-option{display:grid;min-width:0;gap:2px;line-height:1.35}.pdm-material-relation-option span,.pdm-material-relation-option small{overflow:hidden;text-overflow:ellipsis;color:var(--pdm-muted)}.pdm-material-relation-dialog>footer{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding:12px 18px;border-top:1px solid var(--pdm-border)}.pdm-material-relation-dialog>footer>span{margin-right:auto;color:var(--pdm-muted)}@media(max-width:760px){.pdm-material-relation-group :deep(.el-radio-group),.pdm-material-relation-group :deep(.el-checkbox-group){grid-template-columns:1fr}}
</style>
