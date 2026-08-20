<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { linkBomMaterial, listMaterials } from '../api'
import type { BatchUpdateBomItemsInput, BomEmptyDeclaration, BomItem, BomKind, BomValidationField, BomValidationRules, BomVersion, ManufacturingBomBaseline, PdmMaterial } from '../types'
import { u9UnitName, u9UnitOptions } from '../u9Units'

type BomView = 'Source' | BomKind
type BomKindFilter = 'All' | BomKind
type EditableBomField = 'kind' | 'drawingNumber' | 'name' | 'specification' | 'remark' | 'brand' | 'material' | 'surfaceTreatment' | 'quantity'
type EditableBomRow = BomItem & { _clientKey?: string }

const props = withDefaults(defineProps<{
  sourceData?: BomItem[]
  standard: BomItem[]
  nonStandard: BomItem[]
  unclassified?: BomItem[]
  electrical: BomItem[]
  validationRules?: BomValidationRules
  declarations: BomEmptyDeclaration[]
  versions?: BomVersion[]
  baselines?: ManufacturingBomBaseline[]
  pending: boolean
  editable?: boolean
  token?: string
  projectId?: string
}>(), {
  editable: false,
  sourceData: () => [],
  unclassified: () => [],
  versions: () => [],
  baselines: () => [],
  token: '',
  projectId: '',
  validationRules: () => ({
    standard: ['drawingNumber', 'name', 'unit', 'specification', 'quantity', 'revision'],
    nonStandard: ['drawingNumber', 'name', 'unit', 'material', 'quantity', 'revision'],
    electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'],
  }),
})
const emit = defineEmits<{
  save: [kind: BomKind, items: BomItem[]]
  import: [kind: BomKind, file: File]
  export: [kind: BomKind]
  generate: []
  resolve: [itemId: string, action: 'classify' | 'retain' | 'remove', targetKind?: BomKind]
  batchUpdate: [input: BatchUpdateBomItemsInput]
  batchDelete: [itemIds: string[], reason: string]
  batchRestore: [itemIds: string[], mode: 'Original' | 'AsManual']
  restoreSource: [itemIds: string[]]
}>()
const kind = ref<BomView>('Source')
const selectedVersionId = ref('current')
const selectedBaselineId = ref('')
const comparisonOpen = ref(false)
const rows = ref<EditableBomRow[]>([])
const fileInput = ref<HTMLInputElement>()
const selectedIds = ref<string[]>([])
const batchOpen = ref(false)
const recycleBinOpen = ref(false)
const recycleBinSelectedIds = ref<string[]>([])
const batchValidation = ref('')
const editingCell = ref<{ itemId: string; field: EditableBomField } | null>(null)
const inlineValue = ref<string | number | BomKind>('')
const searchQuery = ref('')
const kindFilter = ref<BomKindFilter>('All')
const brandFilter = ref('')
const materialFilter = ref('')
const materialReferenceOpen = ref(false)
const materialReferenceQuery = ref('')
const materialReferenceLoading = ref(false)
const materialReferenceResults = ref<PdmMaterial[]>([])
const draggedRowIndex = ref<number | null>(null)
const dragOverRowIndex = ref<number | null>(null)
const dragOverPosition = ref<'before' | 'after' | null>(null)
let nextClientKey = 0
let discardDraftsOnNextSourceRefresh = false

function createBatchDraft() {
  return {
    kindEnabled: false, targetKind: 'Standard' as BomKind,
    unitEnabled: false, unit: '001', drawingNumberEnabled: false, drawingNumber: '', nameEnabled: false, name: '',
    specificationEnabled: false, specification: '', remarkEnabled: false, remark: '', brandEnabled: false, brand: '',
    materialEnabled: false, material: '', surfaceTreatmentEnabled: false, surfaceTreatment: '', weightEnabled: false, weight: '',
    quantityEnabled: false, quantity: 1, revisionEnabled: false, revision: '',
  }
}
const batchDraft = ref(createBatchDraft())

const sourceDataRows = computed(() => props.sourceData)
const standardRows = computed(() => props.standard.filter(item => !item.manuallyExcluded && !item.pendingClassification))
const nonStandardRows = computed(() => props.nonStandard.filter(item => !item.manuallyExcluded && !item.pendingClassification))
const electricalRows = computed(() => props.electrical.filter(item => !item.manuallyExcluded))
const maintainedKindById = computed(() => new Map(
  [
    ...standardRows.value.map(item => [item.id, 'Standard'] as const),
    ...nonStandardRows.value.map(item => [item.id, 'NonStandard'] as const),
    ...electricalRows.value.map(item => [item.id, 'Electrical'] as const),
  ].filter((entry): entry is readonly [string, Exclude<BomKind, 'Unclassified'>] => !!entry[0]),
))
const categoryVersions = computed(() => kind.value === 'Source' ? [] : props.versions
  .filter(version => version.kind === kind.value)
  .sort((left, right) => right.versionNumber - left.versionNumber))
const activeDraftVersion = computed(() => categoryVersions.value.find(version => version.state === 'Draft'))
const selectedVersion = computed(() => selectedVersionId.value === 'current' ? undefined : categoryVersions.value.find(version => version.id === selectedVersionId.value))
const currentCategoryRows = computed(() => kind.value === 'Standard' ? standardRows.value : kind.value === 'NonStandard' ? nonStandardRows.value : electricalRows.value)
const sourceRows = computed(() => kind.value === 'Source'
  ? sourceDataRows.value
  : (selectedVersion.value?.items.filter(item => !item.manuallyExcluded && !item.pendingClassification) ?? currentCategoryRows.value))
const isSourceView = computed(() => kind.value === 'Source')
const canClassifySourceView = computed(() => props.editable && isSourceView.value)
const canEditCurrentView = computed(() => props.editable && !isSourceView.value && selectedVersionId.value === 'current')
const canSelectCurrentView = computed(() => canClassifySourceView.value || canEditCurrentView.value)
const latestReleasedVersion = computed(() => categoryVersions.value.find(version => version.state === 'Released'))
const comparisonBaseVersion = computed(() => {
  if (kind.value === 'Source') return undefined
  const selected = selectedVersion.value ?? activeDraftVersion.value
  if (!selected) return undefined
  return categoryVersions.value.find(version => version.state === 'Released' && version.versionNumber < selected.versionNumber)
})
const comparison = computed(() => compareBomRows(sourceRows.value, comparisonBaseVersion.value?.items ?? []))
const selectedBaseline = computed(() => props.baselines.find(baseline => baseline.id === selectedBaselineId.value) ?? props.baselines[0])
const unresolvedCount = computed(() => [...props.standard, ...props.nonStandard, ...props.unclassified, ...props.electrical]
  .filter(item => !item.manuallyExcluded && (item.pendingClassification || item.pendingRemoval || item.manualUnmatched)).length)
const brandOptions = computed(() => distinctFilterOptions(rows.value.map(item => item.brand)))
const materialOptions = computed(() => distinctFilterOptions(rows.value.map(item => item.material)))
const filteredRows = computed(() => {
  const query = searchQuery.value.trim().toLocaleLowerCase()
  return rows.value.flatMap((row, index) => {
    const effectiveKind = rowKind(row) ?? 'Unclassified'
    const searchable = [row.name, row.drawingNumber, row.specification].join(' ').toLocaleLowerCase()
    if (query && !searchable.includes(query)) return []
    if (kindFilter.value !== 'All' && effectiveKind !== kindFilter.value) return []
    if (brandFilter.value && row.brand?.trim() !== brandFilter.value) return []
    if (materialFilter.value && row.material?.trim() !== materialFilter.value) return []
    return [{ row, index }]
  })
})
const selectableIds = computed(() => filteredRows.value.flatMap(({ row }) => row.id ? [row.id] : []))
const selectedRows = computed(() => rows.value.filter(item => item.id && selectedIds.value.includes(item.id)))
const recycleBinRows = computed(() => [...props.standard, ...props.nonStandard, ...props.unclassified, ...props.electrical]
  .filter(item => item.id && item.manuallyExcluded)
  .sort((left, right) => (right.deletedAt ?? '').localeCompare(left.deletedAt ?? '') || left.sequence - right.sequence))
const recycleBinAllSelected = computed(() => recycleBinRows.value.length > 0 && recycleBinRows.value.every(item => item.id && recycleBinSelectedIds.value.includes(item.id)))
const allRowsSelected = computed(() => selectableIds.value.length > 0 && selectableIds.value.every(id => selectedIds.value.includes(id)))
const batchFieldCount = computed(() => Object.entries(batchDraft.value).filter(([key, value]) => key.endsWith('Enabled') && value).length)
const canRetainSelected = computed(() => selectedRows.value.length === 1 && (selectedRows.value[0].pendingRemoval || selectedRows.value[0].manualUnmatched))
const canConfirmDeleteSelected = computed(() => selectedRows.value.length > 0 && selectedRows.value.every(row => row.pendingRemoval || row.manualUnmatched))
const canRestoreSourceSelected = computed(() => selectedRows.value.length > 0
  && (kind.value === 'Standard' || kind.value === 'NonStandard')
  && selectedRows.value.every(row => row.sourceDocumentId))
const filtersActive = computed(() => !!searchQuery.value.trim() || kindFilter.value !== 'All' || !!brandFilter.value || !!materialFilter.value)

function versionStateLabel(state: BomVersion['state']) {
  return ({ Draft: '工作中', InReview: '待审批', Released: '已发布', Obsolete: '已作废' } as Record<BomVersion['state'], string>)[state]
}

function distinctFilterOptions(values: Array<string | null | undefined>) {
  return [...new Set(values.map(value => value?.trim()).filter((value): value is string => !!value))]
    .sort((left, right) => left.localeCompare(right, 'zh-CN'))
}

function versionLabel(versionId: string) {
  return props.versions.find(version => version.id === versionId)?.label ?? '—'
}

function bomItemKey(item: BomItem) {
  return (item.drawingNumber || `${item.name}|${item.specification}`).trim().toLocaleLowerCase()
}

function bomItemSignature(item: BomItem) {
  return [item.kind, item.unit, item.drawingNumber, item.name, item.specification, item.remark, item.brand, item.material, item.surfaceTreatment, item.weight, item.quantity, item.revision].join('|')
}

function compareBomRows(current: BomItem[], previous: BomItem[]) {
  const currentByKey = new Map(current.map(item => [bomItemKey(item), item]))
  const previousByKey = new Map(previous.map(item => [bomItemKey(item), item]))
  const added = current.filter(item => !previousByKey.has(bomItemKey(item)))
  const removed = previous.filter(item => !currentByKey.has(bomItemKey(item)))
  const modified = current.filter(item => {
    const old = previousByKey.get(bomItemKey(item))
    return old && bomItemSignature(item) !== bomItemSignature(old)
  })
  return { added, removed, modified }
}

function writebackLabel(status?: BomItem['propertyWritebackStatus']) {
  return ({ PendingSave: '待保存BOM', Pending: '待写回SolidWorks', InProgress: '正在写回', Succeeded: '已写回', Conflict: '写回冲突', Failed: '写回失败', Superseded: '已被新任务替代' } as Record<string, string>)[status ?? ''] ?? ''
}

function hasManualClassificationMismatch(row: BomItem) {
  return row.source === 'Auto'
    && row.reconciliationStatus === 'ManuallyClassified'
    && row.propertyWritebackStatus !== 'Succeeded'
}

function reconciliationLabel(row: BomItem) {
  if (hasManualClassificationMismatch(row)) return '分类不一致'
  const labels: Record<string, string> = {
    AutoAdded: '自动新增', ClassificationChanged: '分类已变更', PendingClassification: '待分类',
    PendingRemoval: '待确认删除', ManualUnmatched: '人工项待确认', ManuallyClassified: '已人工分类',
    ManuallyRetained: '已人工保留', ManuallyExcluded: '已人工排除', ManualAdded: '人工新增', Restored: '已恢复',
    ManualOverrideMismatch: '与源数据不一致', SourceMatched: '已与源数据一致',
  }
  return labels[row.reconciliationStatus ?? '']
    ?? (row.manuallyExcluded ? '已人工排除' : row.pendingClassification ? '待分类' : row.pendingRemoval ? '待确认删除' : row.manualUnmatched ? '人工项待确认' : row.source === 'Auto' ? '图纸生成' : '人工新增')
}

function reconciliationDescription(row: BomItem) {
  return row.reconciliationNote
    ?? (row.pendingClassification ? '图档源数据未填写有效物料分类。'
      : row.pendingRemoval ? '最新图档源数据中已不存在，等待确认处理。'
        : row.manualUnmatched ? 'BOM中存在，但最新图档源数据中无对应项。'
          : row.manuallyExcluded ? '当前图档仍存在，已人工排除出BOM。'
            : row.source === 'Auto' ? '来源：图档源数据。' : '来源：人工新增。')
}

function rowKindLabel(row: BomItem) {
  const effectiveKind = rowKind(row)
  return effectiveKind === 'Standard' ? '标准件' : effectiveKind === 'Electrical' ? '电气件' : effectiveKind === 'NonStandard' ? '非标件' : '待分类'
}

function rowIsClassified(row: BomItem) {
  const effectiveKind = rowKind(row)
  return effectiveKind !== undefined && effectiveKind !== 'Unclassified'
}

function rowKind(row: BomItem): BomKind | undefined {
  if (kind.value === 'Source' && row.id) {
    const maintainedKind = maintainedKindById.value.get(row.id)
    if (maintainedKind) return maintainedKind
  }
  if (row.pendingClassification) return undefined
  return row.kind ?? (kind.value === 'Source' ? undefined : kind.value)
}

function rowNeedsClassification(row: BomItem) {
  return isSourceView.value ? !rowIsClassified(row) : !!row.pendingClassification
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
  const missing = missingRequiredFields(row)
  return missing.length === 0 ? '已完善' : `缺少${missing.join('、')}`
}

function displayValue(value: string | number | null | undefined) {
  return value === null || value === undefined || String(value).trim() === '' ? '—' : String(value)
}

function saveCurrentBom() {
  if (kind.value === 'Source') return
  discardDraftsOnNextSourceRefresh = true
  emit('save', kind.value, rows.value.map(row => {
    const { _clientKey, ...item } = row
    return { ...item, complete: missingRequiredFields(row).length === 0 }
  }))
}

async function searchMaterialReferences() {
  if (!props.token) return
  materialReferenceLoading.value = true
  try {
    const loaded = await listMaterials(props.token, materialReferenceQuery.value)
    materialReferenceResults.value = loaded.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && item.kind === kind.value)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品主档查询失败')
  } finally {
    materialReferenceLoading.value = false
  }
}

async function openMaterialReference() {
  if (selectedRows.value.length !== 1 || isSourceView.value || !props.projectId || !props.token) return
  materialReferenceQuery.value = selectedRows.value[0].drawingNumber ?? ''
  materialReferenceOpen.value = true
  await searchMaterialReferences()
}

async function applyMaterialReference(material: PdmMaterial) {
  const target = selectedRows.value[0]
  if (!target?.id || !props.projectId || !props.token) return
  try {
    await linkBomMaterial(props.projectId, target.id, material.id, props.token)
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
}

async function restoreSelectedFromSource() {
  if (!canRestoreSourceSelected.value) return
  const labels = selectedRows.value.map(row => row.drawingNumber || row.name).slice(0, 5).join('、')
  const suffix = selectedRows.value.length > 5 ? `等 ${selectedRows.value.length} 条` : `${selectedRows.value.length} 条`
  try {
    await ElMessageBox.confirm(
      `将${labels}（${suffix}）恢复为最新图档源属性吗？物料分类与当前排序不会改变。`,
      '恢复源数据',
      { confirmButtonText: '确认恢复', cancelButtonText: '取消', type: 'warning' },
    )
    emit('restoreSource', [...selectedIds.value])
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

function refreshRows(preserveDrafts: boolean) {
  const drafts = preserveDrafts
    ? rows.value.flatMap((row, index) => row.id ? [] : [{ row, index }])
    : []
  const refreshed = sourceRows.value.map(withClientKey)
  drafts.forEach(({ row, index }) => refreshed.splice(Math.min(index, refreshed.length), 0, row))
  rows.value = refreshed
  const available = new Set(rows.value.flatMap(item => item.id ? [item.id] : []))
  selectedIds.value = selectedIds.value.filter(id => available.has(id))
}

watch(kind, () => {
  clearFilters()
  selectedVersionId.value = 'current'
  comparisonOpen.value = false
  discardDraftsOnNextSourceRefresh = false
  refreshRows(false)
}, { immediate: true })
watch(selectedVersionId, () => {
  clearFilters()
  comparisonOpen.value = false
  discardDraftsOnNextSourceRefresh = false
  refreshRows(false)
})
watch(() => props.baselines, baselines => {
  if (!selectedBaselineId.value && baselines.length) selectedBaselineId.value = baselines[0].id
}, { immediate: true })
watch([() => props.sourceData, () => props.standard, () => props.nonStandard, () => props.unclassified, () => props.electrical], () => {
  refreshRows(!discardDraftsOnNextSourceRefresh)
  discardDraftsOnNextSourceRefresh = false
}, { deep: true })
watch(() => props.pending, (pending, previous) => {
  if (previous && !pending) discardDraftsOnNextSourceRefresh = false
})
watch([searchQuery, kindFilter, brandFilter, materialFilter], () => {
  selectedIds.value = []
})

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

function applyRowDrop() {
  const source = draggedRowIndex.value
  const index = dragOverRowIndex.value
  const position = dragOverPosition.value
  resetRowDrag()
  if (source === null || index === null || position === null || source < 0 || source >= rows.value.length) return
  let target = index + (position === 'after' ? 1 : 0)
  if (source < target) target -= 1
  if (source === target) return
  const [row] = rows.value.splice(source, 1)
  rows.value.splice(target, 0, row)
  resequence()
}

function finishRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId !== event.pointerId) return
  applyRowDrop()
}

function cancelRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId !== event.pointerId) return
  resetRowDrag()
}

onBeforeUnmount(resetRowDrag)

function removeDraftRow(index: number) {
  const row = rows.value[index]
  if (!row || row.id) return
  rows.value.splice(index, 1)
  resequence()
}

async function deleteItems(itemIds: string[]) {
  if (itemIds.length === 0 || (isSourceView.value && !canConfirmDeleteSelected.value)) return
  try {
    const deletingRows = rows.value.filter(row => row.id && itemIds.includes(row.id))
    const sourceCount = deletingRows.filter(row => row.sourceDocumentId).length
    const manualCount = deletingRows.length - sourceCount
    const response = await ElMessageBox.prompt(
      `共 ${itemIds.length} 条：有源 ${sourceCount} 条、人工 ${manualCount} 条。删除后统一移入回收站，可恢复且不会改动已发布版本。`,
      '移入BOM回收站',
      {
        confirmButtonText: '确认移入', cancelButtonText: '取消', type: 'warning',
        inputPlaceholder: '请输入删除原因（必填）',
        inputValidator: value => value.trim().length > 0 ? true : '删除原因不能为空',
      },
    )
    emit('batchDelete', itemIds, response.value.trim())
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

function retainSelected() {
  const row = selectedRows.value[0]
  if (!row?.id || (!row.pendingRemoval && !row.manualUnmatched)) return
  emit('resolve', row.id, 'retain')
  selectedIds.value = []
}

function classifySelected(targetKind: Exclude<BomKind, 'Unclassified'>) {
  if (!canClassifySourceView.value || selectedIds.value.length === 0) return
  emit('batchUpdate', { itemIds: [...selectedIds.value], fields: ['kind'], targetKind })
  selectedIds.value = []
}

function selectImport() {
  fileInput.value?.click()
}

function importSelected(event: Event) {
  const target = event.target as HTMLInputElement
  const file = target.files?.[0]
  if (file && kind.value !== 'Source') emit('import', kind.value, file)
  target.value = ''
}

function toggleRow(itemId: string | undefined, event: Event) {
  if (!itemId) return
  const checked = (event.target as HTMLInputElement).checked
  selectedIds.value = checked ? [...new Set([...selectedIds.value, itemId])] : selectedIds.value.filter(id => id !== itemId)
}

function toggleAllRows(event: Event) {
  selectedIds.value = (event.target as HTMLInputElement).checked ? [...selectableIds.value] : []
}

function openBatchEditor() {
  if (selectedIds.value.length === 0) return
  batchDraft.value = createBatchDraft()
  batchValidation.value = ''
  batchOpen.value = true
}

function beginInlineEdit(row: BomItem, field: EditableBomField) {
  if (!canEditCurrentView.value || props.pending || !row.id) return
  editingCell.value = { itemId: row.id, field }
  if (field === 'kind') inlineValue.value = row.pendingClassification ? '' : rowKind(row) ?? ''
  else if (field === 'quantity') inlineValue.value = row.quantity
  else inlineValue.value = row[field] ?? ''
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

function applyMaterialAutofill(input: BatchUpdateBomItemsInput, materialItem: PdmMaterial, editedField: 'drawingNumber' | 'specification', editedValue: string) {
  if (materialItem.kind === 'Standard' || materialItem.kind === 'NonStandard') {
    input.fields.push('kind')
    input.targetKind = materialItem.kind
  }
  setAutofillField(input, 'unit', materialItem.unitCode)
  setAutofillField(input, 'drawingNumber', editedField === 'drawingNumber' ? editedValue : materialItem.materialCode)
  setAutofillField(input, 'name', materialItem.name)
  setAutofillField(input, 'specification', editedField === 'specification' ? editedValue : materialItem.specification ?? '')
  setAutofillField(input, 'remark', materialItem.remark ?? '')
  setAutofillField(input, 'brand', materialItem.brand ?? '')
  setAutofillField(input, 'material', materialItem.material ?? '')
  setAutofillField(input, 'surfaceTreatment', materialItem.surfaceTreatment ?? '')
  setAutofillField(input, 'weight', materialItem.weight ?? '')
}

async function autofillFromMaterialMaster(input: BatchUpdateBomItemsInput, field: EditableBomField, value: string) {
  if (!props.token || (field !== 'drawingNumber' && field !== 'specification') || !value.trim()) return
  const lookupValue = materialLookupValue(value)
  try {
    const loaded = await listMaterials(props.token, value, false, 500)
    const matches = loaded.filter(materialItem => {
      if (materialItem.isArchived || materialItem.approvalStatus !== 'Approved') return false
      const candidate = field === 'drawingNumber' ? materialItem.materialCode : materialItem.specification
      return materialLookupValue(candidate) === lookupValue
    })
    if (matches.length === 1) {
      applyMaterialAutofill(input, matches[0], field, value.trim())
      ElMessage.success(`已按${field === 'drawingNumber' ? '物料编码' : '型号'}匹配料品 ${matches[0].materialCode}，并自动补齐其他信息`)
    } else if (matches.length > 1) {
      ElMessage.warning(`${field === 'drawingNumber' ? '物料编码' : '型号'}“${value.trim()}”匹配到 ${matches.length} 个料品，未自动回填其他信息`)
    } else {
      ElMessage.warning(`料品主档中未找到${field === 'drawingNumber' ? '物料编码' : '型号'}“${value.trim()}”，已保留手工输入`)
    }
  } catch (error) {
    ElMessage.warning(error instanceof Error ? `${error.message}；已仅保存手工输入` : '料品主档查询失败；已仅保存手工输入')
  }
}

async function commitInlineEdit(row: BomItem) {
  const edit = editingCell.value
  if (!edit || !row.id || edit.itemId !== row.id) return
  const value = inlineValue.value
  editingCell.value = null
  const input: BatchUpdateBomItemsInput = { itemIds: [row.id], fields: [edit.field] }
  if (edit.field === 'kind') input.targetKind = value as BomKind
  if (edit.field === 'drawingNumber') input.drawingNumber = String(value)
  if (edit.field === 'name') input.name = String(value)
  if (edit.field === 'specification') input.specification = String(value)
  if (edit.field === 'remark') input.remark = String(value)
  if (edit.field === 'brand') input.brand = String(value)
  if (edit.field === 'material') input.material = String(value)
  if (edit.field === 'surfaceTreatment') input.surfaceTreatment = String(value)
  if (edit.field === 'quantity') input.quantity = Number(value)
  await autofillFromMaterialMaster(input, edit.field, String(value))
  emit('batchUpdate', input)
}

function submitBatchUpdate() {
  const draft = batchDraft.value
  const input: BatchUpdateBomItemsInput = { itemIds: [...selectedIds.value], fields: [] }
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
  emit('batchUpdate', input)
  batchOpen.value = false
  selectedIds.value = []
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel pdm-bom-manager-panel" aria-label="BOM维护">
    <input ref="fileInput" class="pdm-visually-hidden" type="file" accept=".xlsx" @change="importSelected">
    <div class="pdm-bom-detail-toolbar">
      <div class="pdm-segmented" role="tablist">
        <button type="button" role="tab" class="pdm-source-data-tab" :aria-selected="kind === 'Source'" @click="kind = 'Source'">源数据（{{ sourceDataRows.length }}）</button>
        <button type="button" role="tab" :aria-selected="kind === 'Standard'" @click="kind = 'Standard'">标准件BOM（{{ standardRows.length }}）</button>
        <button type="button" role="tab" :aria-selected="kind === 'NonStandard'" @click="kind = 'NonStandard'">非标件BOM（{{ nonStandardRows.length }}）</button>
        <button type="button" role="tab" :aria-selected="kind === 'Electrical'" @click="kind = 'Electrical'">电气BOM（{{ electricalRows.length }}）</button>
        <span v-if="unresolvedCount" class="pdm-bom-unresolved-count">待处理 {{ unresolvedCount }}</span>
      </div>
      <div class="pdm-bom-detail-actions">
        <div class="pdm-manager-actions">
          <button v-if="canEditCurrentView" type="button" class="pdm-secondary-action" title="图档提交后会自动更新；此操作用于人工重新对账" :disabled="pending" @click="emit('generate')">重新对账</button>
          <button v-if="canEditCurrentView && !isSourceView" type="button" class="pdm-secondary-action" @click="selectImport">导入XLSX</button>
          <button v-if="!isSourceView" type="button" class="pdm-secondary-action" @click="emit('export', kind as BomKind)">导出XLSX</button>
          <button v-if="canEditCurrentView && !isSourceView" type="button" class="pdm-secondary-action" @click="addRow()">新增物料</button>
          <button v-if="canEditCurrentView && !isSourceView" type="button" class="pdm-primary-action" :disabled="pending" @click="saveCurrentBom">{{ pending ? '保存中…' : rows.length === 0 ? '保存空BOM' : '保存BOM' }}</button>
        </div>
      </div>
    </div>
    <div v-if="!isSourceView && baselines.length" class="pdm-bom-version-toolbar">
      <div v-if="baselines.length" class="pdm-bom-baseline-picker">
        <label>制造基线
          <select v-model="selectedBaselineId" aria-label="选择制造BOM基线">
            <option v-for="baseline in baselines" :key="baseline.id" :value="baseline.id">{{ baseline.label }} · {{ baseline.changeNumber }}</option>
          </select>
        </label>
        <span v-if="selectedBaseline">S {{ versionLabel(selectedBaseline.standardBomVersionId) }} · N {{ versionLabel(selectedBaseline.nonStandardBomVersionId) }} · E {{ versionLabel(selectedBaseline.electricalBomVersionId) }} · 序列号 {{ selectedBaseline.effectiveSerialFrom }}{{ selectedBaseline.effectiveSerialTo ? `～${selectedBaseline.effectiveSerialTo}` : '起' }}</span>
      </div>
    </div>
    <div v-if="comparisonOpen && comparisonBaseVersion" class="pdm-bom-comparison-summary">
      <strong>{{ selectedVersion?.label || activeDraftVersion?.label || '当前工作区' }} 对比 {{ comparisonBaseVersion.label }}</strong>
      <span class="is-added">新增 {{ comparison.added.length }}</span>
      <span class="is-modified">修改 {{ comparison.modified.length }}</span>
      <span class="is-removed">减少 {{ comparison.removed.length }}</span>
      <small v-if="comparison.removed.length">减少项：{{ comparison.removed.map(item => item.drawingNumber || item.name).join('、') }}</small>
    </div>
    <div v-if="canSelectCurrentView || !isSourceView" class="pdm-bom-selection-toolbar">
      <div class="pdm-bom-selection-actions">
        <template v-if="canClassifySourceView">
          <button type="button" class="pdm-secondary-action" :disabled="pending || selectedIds.length === 0" @click="classifySelected('Standard')">归入标准件BOM</button>
          <button type="button" class="pdm-secondary-action" :disabled="pending || selectedIds.length === 0" @click="classifySelected('NonStandard')">归入非标件BOM</button>
        </template>
        <template v-else-if="canEditCurrentView">
          <button type="button" class="pdm-secondary-action" :disabled="pending || selectedIds.length === 0" @click="openBatchEditor">{{ selectedIds.length > 1 ? '批量编辑' : '编辑' }}</button>
          <button v-if="!isSourceView" type="button" class="pdm-secondary-action" :disabled="pending || selectedIds.length !== 1 || !token || !projectId" @click="openMaterialReference">按编码引用料品</button>
          <button v-if="canRetainSelected" type="button" class="pdm-secondary-action" :disabled="pending" @click="retainSelected">保留此项</button>
          <button v-if="!isSourceView || canConfirmDeleteSelected" type="button" class="pdm-secondary-action is-danger" :disabled="pending || selectedIds.length === 0" @click="deleteItems(selectedIds)">{{ canConfirmDeleteSelected ? (selectedIds.length > 1 ? '批量确认删除' : '确认删除') : (selectedIds.length > 1 ? '批量删除' : '删除') }}</button>
          <button v-if="kind === 'Standard' || kind === 'NonStandard'" type="button" class="pdm-secondary-action" :disabled="pending || !canRestoreSourceSelected" :title="selectedIds.length > 0 && !canRestoreSourceSelected ? '仅支持恢复有图档来源的标准件或非标件' : '恢复最新图档源属性，保留分类与排序'" @click="restoreSelectedFromSource">恢复源数据</button>
        </template>
        <button v-if="editable" type="button" class="pdm-secondary-action" :disabled="pending" @click="recycleBinOpen = true">回收站（{{ recycleBinRows.length }}）</button>
        <span v-if="selectedIds.length" class="pdm-bom-selection-summary" role="status" aria-live="polite">已选择 {{ selectedIds.length }} 项</span>
      </div>
      <div class="pdm-bom-filters" role="search" aria-label="筛选BOM物料">
        <input v-model.trim="searchQuery" type="search" aria-label="搜索BOM物料" placeholder="搜索名称、编码或型号">
        <select v-model="kindFilter" aria-label="筛选物料分类"><option value="All">全部分类</option><option value="Standard">标准件</option><option value="NonStandard">非标件</option><option value="Electrical">电气件</option><option value="Unclassified">待分类</option></select>
        <select v-model="brandFilter" aria-label="筛选品牌"><option value="">全部品牌</option><option v-for="brand in brandOptions" :key="brand" :value="brand">{{ brand }}</option></select>
        <select v-model="materialFilter" aria-label="筛选材质"><option value="">全部材质</option><option v-for="material in materialOptions" :key="material" :value="material">{{ material }}</option></select>
        <button type="button" class="pdm-secondary-action" :disabled="!filtersActive" @click="clearFilters">清空筛选</button>
      </div>
      <div v-if="!isSourceView" class="pdm-bom-version-picker">
        <label>查看版本
          <select v-model="selectedVersionId" aria-label="选择BOM版本">
            <option value="current">当前工作区{{ activeDraftVersion ? ` · ${activeDraftVersion.label} 工作中` : ' · 修改后创建下一工作版' }}</option>
            <option v-for="version in categoryVersions" :key="version.id" :value="version.id">{{ version.label }} · {{ versionStateLabel(version.state) }}{{ version.changeNumber ? ` · ${version.changeNumber}` : '' }}</option>
          </select>
        </label>
        <span v-if="selectedVersion" class="pdm-bom-version-state" :class="`is-${selectedVersion.state.toLocaleLowerCase()}`">{{ versionStateLabel(selectedVersion.state) }} · 只读</span>
        <button type="button" class="pdm-secondary-action" :disabled="!comparisonBaseVersion" @click="comparisonOpen = !comparisonOpen">{{ comparisonOpen ? '收起差异' : '与上一发布版对比' }}</button>
      </div>
    </div>
    <div class="pdm-table-scroll">
      <table class="pdm-edit-table pdm-bom-table">
        <colgroup>
          <col class="is-select"><col class="is-row-actions"><col class="is-sequence"><col class="is-kind"><col class="is-unit"><col class="is-code">
          <col class="is-name"><col class="is-model"><col class="is-remark"><col class="is-brand">
          <col class="is-material"><col class="is-surface"><col class="is-weight"><col class="is-quantity">
          <col class="is-revision"><col class="is-source"><col class="is-data-status">
        </colgroup>
        <thead><tr><th><input type="checkbox" :aria-label="isSourceView ? '选择全部源数据物料' : '选择当前分类全部物料'" :checked="allRowsSelected" :disabled="!canSelectCurrentView || selectableIds.length === 0" @change="toggleAllRows"></th><th aria-label="行排序与插入操作"></th><th>序号</th><th>物料分类</th><th>单位</th><th>物料编码</th><th>物料名称</th><th>型号</th><th>备注信息</th><th>品牌</th><th>材质</th><th>表面处理</th><th>重量</th><th>数量</th><th>版本</th><th>对账状态/说明</th><th>资料状态</th></tr></thead>
        <tbody>
          <tr v-for="{ row, index } in filteredRows" :key="row.id || row._clientKey" :data-row-index="index" :class="{ 'is-pending-removal': row.pendingRemoval, 'is-bom-unresolved': rowNeedsClassification(row) || row.manualUnmatched, 'is-row-dragging': draggedRowIndex === index, 'is-drag-over-before': dragOverRowIndex === index && dragOverPosition === 'before', 'is-drag-over-after': dragOverRowIndex === index && dragOverPosition === 'after' }">
            <td><input type="checkbox" aria-label="选择物料" :checked="!!row.id && selectedIds.includes(row.id)" :disabled="!canSelectCurrentView || !row.id" @change="toggleRow(row.id, $event)"></td>
            <td>
              <span v-if="isSourceView" class="pdm-bom-classification-indicator" :class="rowIsClassified(row) ? 'is-classified' : 'is-unclassified'" :aria-label="rowIsClassified(row) ? '已归类' : '未归类'" :title="rowIsClassified(row) ? '已归类' : '未归类'">{{ rowIsClassified(row) ? '✓' : '!' }}</span>
              <div v-else-if="canEditCurrentView" class="pdm-bom-row-actions">
                <span class="pdm-bom-row-drag-handle" :class="{ 'is-disabled': pending }" role="button" tabindex="0" :aria-label="`拖动第 ${index + 1} 行排序`" :title="`按住拖动第 ${index + 1} 行`" @pointerdown="startRowPointerDrag(index, $event)">⠿</span>
                <button type="button" class="pdm-bom-row-action pdm-bom-insert-button" :aria-label="`在第 ${index + 1} 行下方插入物料`" :title="`在第 ${index + 1} 行下方插入物料`" :disabled="pending" @click="addRow(index)">+</button>
                <button v-if="!row.id" type="button" class="pdm-bom-row-action pdm-bom-delete-draft-button" :aria-label="`删除未保存的第 ${index + 1} 行`" :title="`删除未保存的第 ${index + 1} 行`" :disabled="pending" @click="removeDraftRow(index)">×</button>
              </div>
            </td>
            <td><span class="pdm-bom-sequence-value">{{ index + 1 }}</span></td>
            <td>
              <select v-if="isInlineEditing(row, 'kind')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑物料分类" autofocus @change="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit"><option v-if="row.pendingClassification" value="" disabled>请选择分类</option><option value="Standard">标准件</option><option value="NonStandard">非标件</option><option value="Electrical">电气件</option></select>
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit pdm-bom-kind" :class="{ 'is-warning': row.pendingClassification }" title="点击编辑分类" aria-label="编辑物料分类" @click="beginInlineEdit(row, 'kind')">{{ rowKindLabel(row) }}</button>
              <span v-else class="pdm-bom-kind" :class="{ 'is-warning': rowNeedsClassification(row) }">{{ rowKindLabel(row) }}</span>
            </td>
            <td><span class="pdm-bom-cell-value">{{ u9UnitName(row.unit || '001') }}</span></td>
            <td>
              <input v-if="isInlineEditing(row, 'drawingNumber')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑物料编码" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.drawingNumber || '点击编辑物料编码'" aria-label="编辑物料编码" @click="beginInlineEdit(row, 'drawingNumber')">{{ displayValue(row.drawingNumber) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.drawingNumber" required aria-label="物料编码">
              <span v-else class="pdm-bom-cell-value" :title="row.drawingNumber">{{ displayValue(row.drawingNumber) }}</span>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'name')" v-model="inlineValue" class="pdm-bom-inline-editor pdm-bom-name-input" aria-label="内联编辑物料名称" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit pdm-bom-name-value" :title="row.name || '点击编辑物料名称'" aria-label="编辑物料名称" @click="beginInlineEdit(row, 'name')">{{ displayValue(row.name) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.name" class="pdm-bom-name-input" required aria-label="物料名称" :title="row.name">
              <span v-else class="pdm-bom-cell-value pdm-bom-name-value" :title="row.name">{{ displayValue(row.name) }}</span>
            </td>
            <td>
              <input v-if="isInlineEditing(row, 'specification')" v-model="inlineValue" class="pdm-bom-inline-editor" aria-label="内联编辑型号" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="row.specification || '点击编辑型号'" aria-label="编辑型号" @click="beginInlineEdit(row, 'specification')">{{ displayValue(row.specification) }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.specification" aria-label="型号">
              <span v-else class="pdm-bom-cell-value" :title="row.specification">{{ displayValue(row.specification) }}</span>
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
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.trim="row.brand" aria-label="品牌">
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
            <td>
              <input v-if="isInlineEditing(row, 'quantity')" v-model.number="inlineValue" type="number" min="0.0001" step="0.0001" class="pdm-bom-inline-editor pdm-bom-quantity-editor" aria-label="内联编辑数量" autofocus @blur="commitInlineEdit(row)" @keydown.enter.prevent="commitInlineEdit(row)" @keydown.esc.prevent="cancelInlineEdit">
              <button v-else-if="row.id && canEditCurrentView" type="button" class="pdm-bom-cell-edit" :title="String(row.quantity)" aria-label="编辑数量" @click="beginInlineEdit(row, 'quantity')">{{ row.quantity }}</button>
              <input v-else-if="canEditCurrentView && !isSourceView" v-model.number="row.quantity" type="number" min="0.0001" step="0.0001" class="pdm-bom-quantity-editor" aria-label="数量">
              <span v-else class="pdm-bom-cell-value">{{ row.quantity }}</span>
            </td>
            <td>
              <input v-if="!row.id && canEditCurrentView && !isSourceView" v-model.trim="row.revision" required aria-label="版本">
              <span v-else class="pdm-bom-cell-value">{{ displayValue(row.revision) }}</span>
            </td>
            <td class="pdm-bom-reconciliation-cell">
              <div class="pdm-bom-reconciliation" :title="reconciliationDescription(row)">
                <span class="pdm-bom-source" :class="{ 'is-warning': row.reconciliationStatus === 'ManualOverrideMismatch' || hasManualClassificationMismatch(row) || row.manuallyExcluded || row.pendingRemoval || row.pendingClassification || row.manualUnmatched || row.propertyWritebackStatus === 'Conflict' || row.propertyWritebackStatus === 'Failed' }">{{ reconciliationLabel(row) }}</span>
                <small>{{ reconciliationDescription(row) }}</small>
              </div>
              <small v-if="row.propertyWritebackStatus" class="pdm-bom-writeback-status">{{ writebackLabel(row.propertyWritebackStatus) }}</small>
            </td>
            <td class="pdm-bom-data-status-cell" :class="missingRequiredFields(row).length ? 'is-incomplete' : 'is-complete'"><span class="pdm-bom-data-status" :class="missingRequiredFields(row).length ? 'is-incomplete' : 'is-complete'" :title="missingRequiredFields(row).length ? `待完善：${missingRequiredFields(row).join('、')}` : '必填资料已齐全'">{{ dataStatusLabel(row) }}</span></td>
          </tr>
          <tr v-if="filteredRows.length === 0"><td colspan="17" class="pdm-empty-info">{{ rows.length ? '没有符合当前筛选条件的物料。' : isSourceView ? '当前没有图档源数据。' : '当前BOM为空，系统自动按无此类物料处理；可新增物料或导入标准XLSX。' }}</td></tr>
        </tbody>
      </table>
    </div>

    <div v-if="materialReferenceOpen" class="pdm-dialog-backdrop" @click.self="materialReferenceOpen = false">
      <section class="pdm-bom-batch-dialog pdm-material-reference-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-material-reference-title">
        <header><div><h3 id="pdm-material-reference-title">从料品主档引用</h3><p>输入物料编码、名称或规格搜索；只显示与当前BOM分类一致的已批准料品。</p></div><button type="button" class="pdm-icon-button" aria-label="关闭料品引用" @click="materialReferenceOpen = false">×</button></header>
        <div class="pdm-material-reference-search"><input v-model.trim="materialReferenceQuery" type="search" aria-label="搜索料品主档" placeholder="输入完整编码可精确定位" @keydown.enter.prevent="searchMaterialReferences"><button type="button" class="pdm-primary-action" :disabled="materialReferenceLoading" @click="searchMaterialReferences">{{ materialReferenceLoading ? '查询中…' : '查询' }}</button></div>
        <div class="pdm-table-scroll"><table class="pdm-edit-table"><thead><tr><th>物料编码</th><th>名称</th><th>规格</th><th>分类</th><th>同步</th><th></th></tr></thead><tbody><tr v-for="materialItem in materialReferenceResults" :key="materialItem.id"><td>{{ materialItem.materialCode }}</td><td>{{ materialItem.name }}</td><td>{{ materialItem.specification || '—' }}</td><td>{{ materialItem.categoryCode }}</td><td>{{ materialItem.syncStatus === 'Succeeded' ? '已同步' : '待同步' }}</td><td><button type="button" class="pdm-secondary-action" @click="applyMaterialReference(materialItem)">引用</button></td></tr><tr v-if="!materialReferenceLoading && materialReferenceResults.length === 0"><td colspan="6" class="pdm-empty-info">没有符合条件的已批准料品。</td></tr></tbody></table></div>
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
            <div v-if="kind !== 'Electrical'" class="pdm-bom-batch-field"><input v-model="batchDraft.kindEnabled" type="checkbox" aria-label="修改物料分类"><span>物料分类</span><select v-model="batchDraft.targetKind" :disabled="!batchDraft.kindEnabled"><option value="Standard">标准件</option><option value="NonStandard">非标件</option></select></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.unitEnabled" type="checkbox" aria-label="修改单位"><span>单位</span><select v-model="batchDraft.unit" :disabled="!batchDraft.unitEnabled"><option v-for="unit in u9UnitOptions" :key="unit.code" :value="unit.code">{{ unit.code }} {{ unit.name }}</option></select></div>
            <div class="pdm-bom-batch-field"><input v-model="batchDraft.drawingNumberEnabled" type="checkbox" aria-label="修改物料编码" :disabled="selectedIds.length > 1"><span>物料编码</span><input v-model.trim="batchDraft.drawingNumber" :disabled="!batchDraft.drawingNumberEnabled || selectedIds.length > 1" placeholder="仅支持单条修改"><small v-if="selectedIds.length > 1">编码唯一，多选时不可统一填写</small></div>
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
  </section>
</template>
