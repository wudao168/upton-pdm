<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { addReleaseItemComment, listApprovalTransferCandidates, listReleaseItemComments } from '../api'
import type { ApprovalTransferCandidate, BomItem, CreateReleasePackageInput, ReleaseItemComment, ReleasePackageSummary, ReleaseScope, UpdateReleasePackageDraftInput } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()

const props = withDefaults(defineProps<{
  releasePackage: ReleasePackageSummary | null
  token?: string
  standardItems?: BomItem[]
  releaseItems?: BomItem[]
  username: string
  pending: boolean
  progress: number
  error: string
  canManage: boolean
  canDecide: boolean
  canEmergencyDecide?: boolean
  allowedScopes?: Array<Exclude<ReleaseScope, 'LegacyCombined'>>
  preferredScope?: Exclude<ReleaseScope, 'LegacyCombined'>
  changeReasonTypes?: string[]
  longLeadPublishedItems?: BomItem[]
  previousVersionItems?: BomItem[]
}>(), { standardItems: () => [], releaseItems: () => [], canEmergencyDecide: false, allowedScopes: () => [], changeReasonTypes: () => ['设计变更', '客户需求', '物料替代', '质量整改', '生产反馈', '其他'], longLeadPublishedItems: () => [], previousVersionItems: () => [] })
const emit = defineEmits<{
  create: [input: CreateReleasePackageInput]
  updateDraft: [releasePackageId: string, input: UpdateReleasePackageDraftInput]
  deleteDraft: [releasePackageId: string]
  upload: [releasePackageId: string, file: File]
  submit: [releasePackageId: string]
  withdraw: [releasePackageId: string]
  decide: [taskId: string, decision: 'Approved' | 'Rejected', comment: string]
  transfer: [taskId: string, targetUsername: string, comment: string]
  emergencyDecide: [taskId: string, decision: 'Approved' | 'Rejected', reason: string]
  retryU9: [releasePackageId: string]
}>()

const releaseTypes: { value: Exclude<ReleaseScope, 'LegacyCombined'>; label: string }[] = [
  { value: 'StandardLongLead', label: '标准件 · 长交期提前发布' },
  { value: 'StandardFormal', label: '标准件 · 正式发布' },
  { value: 'StandardSupplement', label: '标准件 · 增补/变更' },
  { value: 'ElectricalFormal', label: '电气BOM · 正式发布' },
  { value: 'ElectricalSupplement', label: '电气BOM · 增补/变更' },
  { value: 'NonStandardWithDrawing', label: '非标件BOM + 图纸' },
]
const scopeLabels = Object.fromEntries(releaseTypes.map(item => [item.value, item.label])) as Record<string, string>
const workflowNames: Record<string, string> = {
  'mechanical-release': '机械发布审批',
  'electrical-release': '电气发布审批',
}
const visibleReleaseTypes = computed(() => props.allowedScopes.length ? releaseTypes.filter(item => props.allowedScopes.includes(item.value)) : releaseTypes)
const releaseNote = ref('')
const selectedChangeReasons = ref<string[]>([])
const scope = ref<Exclude<ReleaseScope, 'LegacyCombined'>>('StandardLongLead')
const wholeSetMultiplier = ref(1)
const selectedLongLeadKeys = ref<string[]>([])
const longLeadRequestedQuantities = ref<Record<string, number>>({})
const editingDraft = ref(false)
const comment = ref('')
const approvalCommentError = ref('')
const emergencyReason = ref('')
const transferOpen = ref(false)
const transferQuery = ref('')
const transferTarget = ref('')
const transferComment = ref('')
const transferCandidates = ref<ApprovalTransferCandidate[]>([])
const transferCandidatesLoading = ref(false)
const transferError = ref('')
const currentTask = computed(() => props.releasePackage?.steps.find(step => step.id !== 'production-release' && step.status === 'current'))
const releaseWorkflowLabel = computed(() => {
  const releasePackage = props.releasePackage
  if (!releasePackage?.workflowCode) return '旧版固定流程'
  const fallbackName = releasePackage.scope.startsWith('Electrical') ? '电气发布审批' : '机械发布审批'
  return `${workflowNames[releasePackage.workflowCode] ?? fallbackName} · 第${releasePackage.workflowVersion || 1}版`
})
const releaseBomRevisionLabel = computed(() => {
  const releasePackage = props.releasePackage
  if (!releasePackage) return ''
  if (releasePackage.scope === 'StandardLongLead') {
    return `标准件：长交期批次 ${releasePackage.standardBomRevision || '未生成'} · 非标件：不适用 · 电气件：不适用`
  }
  return `标准件：${releasePackage.standardBomRevision || '未发布'} · 非标件：${releasePackage.nonStandardBomRevision || '未发布'} · 电气件：${releasePackage.electricalBomRevision || '未发布'}`
})
const canPrepare = computed(() => !props.releasePackage || ['草稿', '已驳回', '发布失败'].includes(props.releasePackage.state))
const isSupplement = computed(() => scope.value === 'StandardSupplement' || scope.value === 'ElectricalSupplement')
const availableReleaseItems = computed(() => (props.releaseItems.length ? props.releaseItems : props.standardItems)
  .filter(row => !row.manuallyExcluded && !row.releaseExcluded))
const releasePageSize = 50
const longLeadPage = ref(1)
const formalPage = ref(1)
const supplementPage = ref(1)
const frozenPage = ref(1)
const frozenViewMode = ref<'Summary' | 'Structure'>('Summary')
const releaseItemComments = ref<ReleaseItemComment[]>([])
const itemCommentsLoading = ref(false)
const itemCommentsError = ref('')
const itemCommentOpen = ref(false)
const itemCommentTarget = ref<FrozenDisplayRow | null>(null)
const itemCommentText = ref('')
const itemCommentSaving = ref(false)
type FrozenDisplayRow = { key: string; materialKey: string; item: BomItem; sourceItems: BomItem[] }
const releaseItemGroupKey = (item: BomItem, index: number) => {
  const materialCode = item.drawingNumber?.trim().toLocaleLowerCase()
  return materialCode
    ? `material:${materialCode}|${item.unit?.trim().toLocaleLowerCase() ?? ''}`
    : [
        'item', item.sourceDocumentId, item.sourceConfiguration, item.kind, item.unit, item.name,
        item.specification, item.brand, item.material, item.surfaceTreatment, item.heatTreatment,
        item.weight, item.remark, item.revision, item.id || index,
      ].map(value => String(value ?? '').trim().toLocaleLowerCase()).join('|')
}
const longLeadPublishedQuantities = computed(() => {
  const quantities = new Map<string, number>()
  props.longLeadPublishedItems.forEach((item, index) => {
    const key = releaseItemGroupKey(item, index)
    quantities.set(key, (quantities.get(key) ?? 0) + Number(item.quantity))
  })
  return quantities
})
const longLeadReleaseRows = computed(() => {
  const grouped = new Map<string, { key: string; item: BomItem; sourceItems: BomItem[] }>()
  availableReleaseItems.value.forEach((item, index) => {
    const key = releaseItemGroupKey(item, index)
    const existing = grouped.get(key)
    if (!existing) {
      grouped.set(key, { key, item: { ...item }, sourceItems: item.id ? [item] : [] })
      return
    }
    existing.item.quantity = Number(existing.item.quantity) + Number(item.quantity)
    if (item.id) existing.sourceItems.push(item)
  })
  return [...grouped.values()].map(row => ({
    ...row,
    item: {
      ...row.item,
      quantity: Math.max(0, Number(row.item.quantity) - (longLeadPublishedQuantities.value.get(row.key) ?? 0)),
    },
  })).filter(row => row.sourceItems.length > 0 && row.item.quantity > 0)
})
const selectedBomItemQuantities = computed(() => {
  const quantities: Record<string, number> = {}
  longLeadReleaseRows.value.filter(row => selectedLongLeadKeys.value.includes(row.key)).forEach(row => {
    let remaining = Number(longLeadRequestedQuantities.value[row.key])
    row.sourceItems.forEach(item => {
      if (!item.id || remaining <= 0) return
      const quantity = Math.min(Number(item.quantity), remaining)
      if (quantity > 0) quantities[item.id] = quantity
      remaining -= quantity
    })
  })
  return quantities
})
const selectedBomItemIds = computed(() => Object.keys(selectedBomItemQuantities.value))
const longLeadPageCount = computed(() => Math.max(1, Math.ceil(longLeadReleaseRows.value.length / releasePageSize)))
const pagedLongLeadItems = computed(() => {
  const start = (longLeadPage.value - 1) * releasePageSize
  return longLeadReleaseRows.value.slice(start, start + releasePageSize)
})
const formalReleaseRows = computed(() => {
  const grouped = new Map<string, { key: string; item: BomItem; longLeadPublishedQuantity: number }>()
  availableReleaseItems.value.forEach((item, index) => {
    const key = releaseItemGroupKey(item, index)
    const existing = grouped.get(key)
    if (!existing) {
      grouped.set(key, {
        key,
        item: { ...item },
        longLeadPublishedQuantity: longLeadPublishedQuantities.value.get(key) ?? 0,
      })
      return
    }
    existing.item.quantity = Number(existing.item.quantity) + Number(item.quantity)
  })
  return [...grouped.values()]
})
const formalPageCount = computed(() => Math.max(1, Math.ceil(formalReleaseRows.value.length / releasePageSize)))
const pagedFormalReleaseRows = computed(() => {
  const start = (formalPage.value - 1) * releasePageSize
  return formalReleaseRows.value.slice(start, start + releasePageSize)
})
const itemKey = (item: BomItem) => (item.drawingNumber || `${item.name}|${item.specification}`).trim().toLocaleLowerCase()
const itemSignature = (item: BomItem) => [item.kind, item.unit, item.drawingNumber, item.name, item.specification, item.remark, item.brand, item.material, item.surfaceTreatment, item.heatTreatment, item.weight, item.quantity, item.revision].join('|')
const supplementRows = computed(() => {
  const previous = new Map(props.previousVersionItems.map(item => [itemKey(item), item]))
  const current = new Map(availableReleaseItems.value.map(item => [itemKey(item), item]))
  return [
    ...availableReleaseItems.value.filter(item => !previous.has(itemKey(item))).map(item => ({ item, change: '新增' })),
    ...availableReleaseItems.value.filter(item => previous.has(itemKey(item)) && itemSignature(previous.get(itemKey(item))!) !== itemSignature(item)).map(item => ({ item, change: '修改' })),
    ...props.previousVersionItems.filter(item => !current.has(itemKey(item))).map(item => ({ item, change: '删除' })),
  ]
})
const supplementPageCount = computed(() => Math.max(1, Math.ceil(supplementRows.value.length / releasePageSize)))
const pagedSupplementRows = computed(() => {
  const start = (supplementPage.value - 1) * releasePageSize
  return supplementRows.value.slice(start, start + releasePageSize)
})
const isLongLeadRelease = computed(() => scope.value === 'StandardLongLead')
const isFormalRelease = computed(() => scope.value === 'StandardFormal' || scope.value === 'ElectricalFormal' || scope.value === 'NonStandardWithDrawing')
const releaseDetailRowCount = computed(() => {
  if (isLongLeadRelease.value) return longLeadReleaseRows.value.length
  if (isFormalRelease.value) return formalReleaseRows.value.length
  return supplementRows.value.length
})
const releaseDetailPage = computed(() => {
  if (isLongLeadRelease.value) return longLeadPage.value
  if (isFormalRelease.value) return formalPage.value
  return supplementPage.value
})
const releaseDetailPageCount = computed(() => {
  if (isLongLeadRelease.value) return longLeadPageCount.value
  if (isFormalRelease.value) return formalPageCount.value
  return supplementPageCount.value
})
const releaseDetailLegend = computed(() => {
  if (isLongLeadRelease.value) return `选择长交期标准件（已选 ${selectedLongLeadKeys.value.length} 项）`
  if (isFormalRelease.value) return `正式发布内容（共 ${formalReleaseRows.value.length} 项 · 整套倍率 ×${wholeSetMultiplier.value}）`
  return `增补/变更内容（共 ${supplementRows.value.length} 项 · 整套倍率 ×${wholeSetMultiplier.value}）`
})
const releaseDetailEmptyText = computed(() => {
  if (isLongLeadRelease.value) return '当前没有剩余可提前发布数量的标准件。'
  if (isFormalRelease.value) return '当前没有可发布的物料。'
  return '与上一正式发布版相比，当前没有增补或变更内容。'
})
const hasInvalidLongLeadQuantity = computed(() => selectedLongLeadKeys.value.some(key => {
  const row = longLeadReleaseRows.value.find(item => item.key === key)
  const requested = Number(longLeadRequestedQuantities.value[key])
  return !row || !Number.isFinite(requested) || requested <= 0 || requested > Number(row.item.quantity)
}))
const hasInvalidWholeSetMultiplier = computed(() => scope.value !== 'StandardLongLead'
  && (!Number.isInteger(Number(wholeSetMultiplier.value)) || Number(wholeSetMultiplier.value) < 1 || Number(wholeSetMultiplier.value) > 1000))
const createDisabled = computed(() => props.pending
  || scope.value === 'StandardLongLead' && selectedBomItemIds.value.length === 0
  || scope.value === 'StandardLongLead' && hasInvalidLongLeadQuantity.value
  || hasInvalidWholeSetMultiplier.value
  || isSupplement.value && selectedChangeReasons.value.length === 0)
const requiresDrawingFiles = computed(() => props.releasePackage?.locksDocuments ?? scope.value === 'NonStandardWithDrawing')
const isCurrentTaskAssignee = computed(() => Boolean(currentTask.value
  && currentTask.value.assignee.toLowerCase() === props.username.toLowerCase()))
const canHandleCurrentTask = computed(() => isCurrentTaskAssignee.value
  || Boolean(props.canDecide && currentTask.value && props.username.toLowerCase() === 'admin'))
const filteredTransferCandidates = computed(() => {
  const query = transferQuery.value.trim().toLocaleLowerCase()
  if (!query) return transferCandidates.value
  return transferCandidates.value.filter(item => `${item.displayName} ${item.username}`.toLocaleLowerCase().includes(query))
})
const frozenItems = computed(() => {
  if (!props.releasePackage) return []
  if (props.releasePackage.scope.startsWith('Electrical')) return props.releasePackage.electricalBomSnapshot
  if (props.releasePackage.scope === 'NonStandardWithDrawing') return props.releasePackage.nonStandardBomSnapshot
  return props.releasePackage.standardBomSnapshot
})
const frozenMaterialKey = (item: BomItem) => {
  const materialCode = item.drawingNumber?.trim().toLocaleLowerCase()
  return materialCode ? `material:${materialCode}|${item.unit?.trim().toLocaleLowerCase() ?? ''}` : `item:${item.id?.toLocaleLowerCase() ?? ''}`
}
const frozenSummaryRows = computed<FrozenDisplayRow[]>(() => {
  const grouped = new Map<string, FrozenDisplayRow>()
  frozenItems.value.forEach((item, index) => {
    const materialKey = frozenMaterialKey(item)
    const existing = grouped.get(materialKey)
    if (!existing) {
      grouped.set(materialKey, { key: materialKey || `summary-${index}`, materialKey, item: { ...item }, sourceItems: [item] })
      return
    }
    existing.item.quantity = Number(existing.item.quantity) + Number(item.quantity)
    existing.sourceItems.push(item)
  })
  return [...grouped.values()].map(row => ({
    ...row,
    item: { ...row.item, quantity: Number(row.item.quantity) * (props.releasePackage?.wholeSetMultiplier ?? 1) },
  }))
})
const frozenStructureRows = computed<FrozenDisplayRow[]>(() => frozenItems.value.map((item, index) => ({
  key: `structure:${item.id || index}`,
  materialKey: frozenMaterialKey(item),
  item: { ...item, quantity: Number(item.quantity) * (props.releasePackage?.wholeSetMultiplier ?? 1) },
  sourceItems: [item],
})))
const frozenDisplayRows = computed(() => frozenViewMode.value === 'Summary' ? frozenSummaryRows.value : frozenStructureRows.value)
const frozenPageCount = computed(() => Math.max(1, Math.ceil(frozenDisplayRows.value.length / releasePageSize)))
const pagedFrozenRows = computed(() => {
  const start = (frozenPage.value - 1) * releasePageSize
  return frozenDisplayRows.value.slice(start, start + releasePageSize)
})
const canAddItemComment = computed(() => Boolean(currentTask.value && (canHandleCurrentTask.value || props.canEmergencyDecide)))
const selectedItemComments = computed(() => itemCommentTarget.value
  ? releaseItemComments.value.filter(item => item.materialKey.toLocaleLowerCase() === itemCommentTarget.value!.materialKey)
  : [])
const frozenDiff = computed(() => {
  const previous = new Map(props.previousVersionItems.map(item => [itemKey(item), item]))
  const current = new Map(frozenItems.value.map(item => [itemKey(item), item]))
  return {
    added: frozenItems.value.filter(item => !previous.has(itemKey(item))).length,
    modified: frozenItems.value.filter(item => previous.has(itemKey(item)) && itemSignature(previous.get(itemKey(item))!) !== itemSignature(item)).length,
    removed: props.previousVersionItems.filter(item => !current.has(itemKey(item))).length,
  }
})

watch([visibleReleaseTypes, () => props.preferredScope], ([items, preferred]) => {
  const preferredItem = items.find(item => item.value === preferred)
  if (preferredItem) scope.value = preferredItem.value
  else if (items.length && !items.some(item => item.value === scope.value)) scope.value = items[0].value
}, { immediate: true })
watch(() => props.releasePackage?.id, () => {
  frozenPage.value = 1
  frozenViewMode.value = 'Summary'
  editingDraft.value = false
  itemCommentOpen.value = false
  comment.value = ''
  approvalCommentError.value = ''
})
watch(frozenViewMode, () => { frozenPage.value = 1 })
watch([() => props.releasePackage?.id, () => props.token], () => { void loadReleaseItemComments() }, { immediate: true })
watch(scope, () => {
  if (scope.value === 'StandardLongLead') wholeSetMultiplier.value = 1
  longLeadPage.value = 1
  formalPage.value = 1
  supplementPage.value = 1
})
watch([() => availableReleaseItems.value.length, () => supplementRows.value.length], () => {
  if (scope.value !== 'StandardLongLead') longLeadPage.value = 1
  else longLeadPage.value = Math.min(longLeadPage.value, longLeadPageCount.value)
  formalPage.value = Math.min(formalPage.value, formalPageCount.value)
  supplementPage.value = Math.min(supplementPage.value, supplementPageCount.value)
})
watch(() => longLeadReleaseRows.value.map(row => row.key).join('\n'), () => {
  const availableKeys = new Set(longLeadReleaseRows.value.map(row => row.key))
  selectedLongLeadKeys.value = selectedLongLeadKeys.value.filter(key => availableKeys.has(key))
  longLeadRequestedQuantities.value = Object.fromEntries(Object.entries(longLeadRequestedQuantities.value).filter(([key]) => availableKeys.has(key)))
})

function create() {
  if (createDisabled.value) return
  const input: CreateReleasePackageInput = {
    changeReason: isSupplement.value ? selectedChangeReasons.value.join('；') : releaseNote.value,
    scope: scope.value,
    selectedBomItemIds: scope.value === 'StandardLongLead' ? selectedBomItemIds.value : [],
    selectedBomItemQuantities: scope.value === 'StandardLongLead' ? selectedBomItemQuantities.value : undefined,
    wholeSetMultiplier: scope.value === 'StandardLongLead' ? 1 : Number(wholeSetMultiplier.value),
  }
  if (editingDraft.value && props.releasePackage) {
    emit('updateDraft', props.releasePackage.id, {
      changeReason: input.changeReason,
      selectedBomItemIds: input.selectedBomItemIds,
      selectedBomItemQuantities: input.selectedBomItemQuantities,
      wholeSetMultiplier: input.wholeSetMultiplier,
    })
    editingDraft.value = false
    return
  }
  emit('create', input)
}

function toggleLongLeadSelection(key: string, checked: boolean, maximum: number) {
  if (checked) {
    if (!selectedLongLeadKeys.value.includes(key)) selectedLongLeadKeys.value = [...selectedLongLeadKeys.value, key]
    if (!(Number(longLeadRequestedQuantities.value[key]) > 0)) longLeadRequestedQuantities.value[key] = maximum
    return
  }
  selectedLongLeadKeys.value = selectedLongLeadKeys.value.filter(item => item !== key)
  delete longLeadRequestedQuantities.value[key]
}

function startDraftEdit() {
  const releasePackage = props.releasePackage
  if (!releasePackage || releasePackage.state !== '草稿' || releasePackage.scope === 'LegacyCombined') return
  scope.value = releasePackage.scope
  releaseNote.value = releasePackage.scope === 'StandardSupplement' || releasePackage.scope === 'ElectricalSupplement' ? '' : releasePackage.changeReason || ''
  selectedChangeReasons.value = releasePackage.scope === 'StandardSupplement' || releasePackage.scope === 'ElectricalSupplement'
    ? (releasePackage.changeReason || '').split('；').filter(Boolean)
    : []
  wholeSetMultiplier.value = releasePackage.scope === 'StandardLongLead' ? 1 : releasePackage.wholeSetMultiplier ?? 1
  selectedLongLeadKeys.value = []
  longLeadRequestedQuantities.value = {}
  if (releasePackage.scope === 'StandardLongLead') {
    const selectedByKey = new Map<string, number>()
    releasePackage.standardBomSnapshot.forEach((item, index) => {
      const key = releaseItemGroupKey(item, index)
      selectedByKey.set(key, (selectedByKey.get(key) ?? 0) + Number(item.quantity))
    })
    longLeadReleaseRows.value.forEach(row => {
      const quantity = selectedByKey.get(row.key)
      if (!(quantity && quantity > 0)) return
      selectedLongLeadKeys.value.push(row.key)
      longLeadRequestedQuantities.value[row.key] = quantity
    })
  }
  editingDraft.value = true
}

function deleteDraft() {
  const releasePackage = props.releasePackage
  if (!releasePackage || releasePackage.state !== '草稿') return
  if (!window.confirm(`确定删除草稿发布包 ${releasePackage.number}？该操作会清理草稿审批任务和暂存文件，并保留审计记录。`)) return
  emit('deleteDraft', releasePackage.id)
}

function moveReleaseDetailPage(offset: number) {
  const nextPage = Math.min(releaseDetailPageCount.value, Math.max(1, releaseDetailPage.value + offset))
  if (isLongLeadRelease.value) longLeadPage.value = nextPage
  else if (isFormalRelease.value) formalPage.value = nextPage
  else supplementPage.value = nextPage
}

function uploadSelected(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file && props.releasePackage) emit('upload', props.releasePackage.id, file)
  input.value = ''
}

function submitDecision(decision: 'Approved' | 'Rejected') {
  if (!currentTask.value) return
  const value = comment.value.trim()
  if (decision === 'Rejected' && !value) {
    approvalCommentError.value = '请填写退回原因。'
    return
  }
  approvalCommentError.value = ''
  emit('decide', currentTask.value.id, decision, value || '同意')
}

async function openTransfer() {
  if (!currentTask.value || transferCandidatesLoading.value) return
  transferOpen.value = true
  transferQuery.value = ''
  transferTarget.value = ''
  transferComment.value = ''
  transferError.value = ''
  transferCandidatesLoading.value = true
  try {
    transferCandidates.value = await listApprovalTransferCandidates(currentTask.value.id, props.token || '')
  } catch (error) {
    transferCandidates.value = []
    transferError.value = error instanceof Error ? error.message : '加载可转交人员失败。'
  } finally {
    transferCandidatesLoading.value = false
  }
}

function confirmTransfer() {
  if (!currentTask.value || !transferTarget.value) return
  emit('transfer', currentTask.value.id, transferTarget.value, transferComment.value.trim())
  transferOpen.value = false
}

function structureDepth(item: BomItem) {
  if (!item.sourceInstancePath) return item.parentDrawingNumber ? 1 : 0
  return Math.max(0, item.sourceInstancePath.split(/[\\/>]+/).filter(Boolean).length - 1)
}

function structureIndent(item: BomItem) {
  return `${7 + structureDepth(item) * 12}px`
}

function structureLocation(item: BomItem) {
  return item.sourceInstancePath || (item.parentDrawingNumber ? `父件：${item.parentDrawingNumber}` : '')
}

function commentsForRow(row: FrozenDisplayRow) {
  return releaseItemComments.value.filter(item => item.materialKey.toLocaleLowerCase() === row.materialKey)
}

function formatCommentTime(value: string) {
  const time = new Date(value)
  return Number.isNaN(time.getTime()) ? value : time.toLocaleString('zh-CN', { hour12: false })
}

async function loadReleaseItemComments() {
  const packageId = props.releasePackage?.id
  if (!packageId || !props.token) {
    releaseItemComments.value = []
    itemCommentsError.value = ''
    itemCommentsLoading.value = false
    return
  }
  itemCommentsLoading.value = true
  itemCommentsError.value = ''
  try {
    const comments = await listReleaseItemComments(packageId, props.token)
    if (props.releasePackage?.id === packageId) releaseItemComments.value = comments
  } catch (error) {
    if (props.releasePackage?.id === packageId) {
      releaseItemComments.value = []
      itemCommentsError.value = error instanceof Error ? error.message : '加载物料批注失败。'
    }
  } finally {
    if (props.releasePackage?.id === packageId) itemCommentsLoading.value = false
  }
}

function openItemComment(row: FrozenDisplayRow) {
  itemCommentTarget.value = row
  itemCommentText.value = ''
  itemCommentsError.value = ''
  itemCommentOpen.value = true
}

async function saveItemComment() {
  const packageId = props.releasePackage?.id
  const bomItemId = itemCommentTarget.value?.item.id
  const text = itemCommentText.value.trim()
  if (!packageId || !bomItemId || !props.token || !text || itemCommentSaving.value) return
  itemCommentSaving.value = true
  itemCommentsError.value = ''
  try {
    const saved = await addReleaseItemComment(packageId, bomItemId, text, props.token)
    releaseItemComments.value = [...releaseItemComments.value, saved]
    itemCommentText.value = ''
  } catch (error) {
    itemCommentsError.value = error instanceof Error ? error.message : '保存物料批注失败。'
  } finally {
    itemCommentSaving.value = false
  }
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel release-center" aria-label="审批与生产发包">
    <form v-if="canManage && (!releasePackage || editingDraft)" class="pdm-form-grid pdm-release-create-form" @submit.prevent="create">
      <section class="pdm-release-create-header" aria-label="发布参数">
        <div class="pdm-release-type-row">
          <label>发布类型
            <select v-model="scope" :disabled="editingDraft" aria-label="发布类型">
              <option v-for="item in visibleReleaseTypes" :key="item.value" :value="item.value">{{ item.label }}</option>
            </select>
          </label>
          <label v-if="!isLongLeadRelease" title="仅影响本发布包的输出数量，与项目及子项目数量无关">整套倍率
            <input v-model.number="wholeSetMultiplier" type="number" min="1" max="1000" step="1" aria-label="整套倍率">
          </label>
          <div class="pdm-release-draft-actions">
            <button v-if="editingDraft" type="button" class="pdm-secondary-action" :disabled="pending" @click="editingDraft = false">取消编辑</button>
            <button type="submit" class="pdm-primary-action" :disabled="createDisabled">{{ editingDraft ? '保存草稿' : '创建草稿' }}</button>
          </div>
          <label v-if="isSupplement">变更单号<input value="创建草稿后自动生成" readonly aria-label="变更单号由系统自动生成"></label>
        </div>
        <div class="pdm-release-parameter-slot">
          <label v-if="!isSupplement" class="pdm-release-reason">备注<textarea v-model.trim="releaseNote" rows="3" maxlength="500" placeholder="可填写本次发布备注（选填）"></textarea></label>
          <fieldset v-else class="release-change-reason-picker">
            <legend>变更原因（可多选，已选 {{ selectedChangeReasons.length }} 项）</legend>
            <label v-for="reason in changeReasonTypes" :key="reason">
              <input v-model="selectedChangeReasons" type="checkbox" :value="reason" :aria-label="`变更原因 ${reason}`">
              <span>{{ reason }}</span>
            </label>
          </fieldset>
        </div>
      </section>
      <fieldset class="release-detail-picker">
        <legend>{{ releaseDetailLegend }}</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table">
            <colgroup><col><col><col><col><col><col><col><col><col><col></colgroup>
            <thead><tr><th>标记</th><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>可发布数量</th><th>本次发布</th><th>备注</th><th>发布状态</th></tr></thead>
            <tbody>
              <template v-if="isLongLeadRelease">
                <tr v-for="(row, index) in pagedLongLeadItems" :key="row.key">
                  <td class="is-release-centered"><input :checked="selectedLongLeadKeys.includes(row.key)" type="checkbox" :aria-label="`选择长交期物料 ${row.item.drawingNumber}`" @change="toggleLongLeadSelection(row.key, ($event.target as HTMLInputElement).checked, Number(row.item.quantity))"></td>
                  <td class="is-release-centered">{{ (longLeadPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td class="is-release-centered">{{ row.item.brand || '—' }}</td><td class="is-release-centered">{{ row.item.quantity }}</td><td class="is-release-centered"><input v-if="selectedLongLeadKeys.includes(row.key)" v-model.number="longLeadRequestedQuantities[row.key]" class="long-lead-quantity-input" type="number" min="0.000001" :max="row.item.quantity" step="any" :aria-label="`本次发布数量 ${row.item.drawingNumber}`"><span v-else>—</span></td><td>{{ row.item.remark || '—' }}</td><td class="is-release-centered"><span class="release-status-tag">剩余可发布</span></td>
                </tr>
              </template>
              <template v-else-if="isFormalRelease">
                <tr v-for="(row, index) in pagedFormalReleaseRows" :key="`${row.item.drawingNumber || row.item.id || index}-${row.item.unit}`">
                  <td class="is-release-centered"><span class="release-inclusion-tag">全量</span></td><td class="is-release-centered">{{ (formalPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td class="is-release-centered">{{ row.item.brand || '—' }}</td><td class="is-release-centered">{{ row.item.quantity }}</td><td class="is-release-centered">{{ row.item.quantity }} × {{ wholeSetMultiplier }} = {{ Number(row.item.quantity) * Number(wholeSetMultiplier) }}</td><td>{{ row.item.remark || '—' }}</td>
                  <td class="is-release-centered"><span v-if="row.longLeadPublishedQuantity > 0" class="long-lead-tag">已提前发布 {{ row.longLeadPublishedQuantity }}/{{ row.item.quantity }}</span><span v-else>—</span></td>
                </tr>
              </template>
              <template v-else>
                <tr v-for="(row, index) in pagedSupplementRows" :key="`${row.change}-${row.item.id}-${index}`">
                  <td class="is-release-centered"><span :class="`release-change-tag is-${row.change}`">{{ row.change }}</span></td><td class="is-release-centered">{{ (supplementPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td class="is-release-centered">{{ row.item.brand || '—' }}</td><td class="is-release-centered">{{ row.item.quantity }}</td><td class="is-release-centered">{{ row.change === '删除' ? '—' : `${row.item.quantity} × ${wholeSetMultiplier} = ${Number(row.item.quantity) * Number(wholeSetMultiplier)}` }}</td><td>{{ row.item.remark || '—' }}</td><td class="is-release-centered"><span class="release-status-tag">待纳入变更</span></td>
                </tr>
              </template>
              <tr v-if="!releaseDetailRowCount"><td colspan="10" class="pdm-empty-info">{{ releaseDetailEmptyText }}</td></tr>
            </tbody>
          </table>
        </div>
        <nav class="release-detail-pagination" aria-label="发布明细分页">
          <span>共 {{ releaseDetailRowCount }} 条 · 50 条/页</span>
          <button type="button" :disabled="releaseDetailPage <= 1" aria-label="发布明细上一页" @click="moveReleaseDetailPage(-1)">‹</button>
          <strong>{{ releaseDetailPage }} / {{ releaseDetailPageCount }}</strong>
          <button type="button" :disabled="releaseDetailPage >= releaseDetailPageCount" aria-label="发布明细下一页" @click="moveReleaseDetailPage(1)">›</button>
        </nav>
      </fieldset>
    </form>

    <template v-else-if="releasePackage">
      <section class="pdm-release-top-workflow" aria-label="发布审批流程与操作">
        <div class="pdm-approval-chain">
          <article v-for="step in releasePackage.steps" :key="step.id" :class="`is-${step.status}`">
            <span>{{ step.status === 'approved' || step.status === 'done' ? '✓' : step.status === 'rejected' ? '×' : step.status === 'current' ? '●' : step.status === 'skipped' ? '—' : '○' }}</span>
            <div>
              <strong>{{ step.stage }}</strong>
              <small>{{ displayUserName(step.decisionBy || step.assignee) }} · {{ step.detail }}</small>
              <em v-if="step.emergencySubstitute">紧急代批：{{ step.emergencyReason }}</em>
              <em v-else-if="step.comment">{{ step.comment }}</em>
            </div>
          </article>
        </div>

        <div v-if="canManage && releasePackage.state === '草稿'" class="pdm-manager-actions pdm-release-draft-management">
          <button type="button" class="pdm-secondary-action" :disabled="pending" @click="startDraftEdit">编辑草稿</button>
          <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="deleteDraft">删除草稿</button>
        </div>

        <div v-if="canManage && canPrepare" class="pdm-release-preparation">
          <h3>发布资料</h3>
          <p v-if="requiresDrawingFiles">非标件BOM已固化为XLSX；请上传至少一份PDF，审批通过后与图纸原子发布。</p>
          <p v-else>系统已按本次发布范围生成受控BOM XLSX，无需上传机械图纸。</p>
          <div class="pdm-manager-actions">
            <template v-if="requiresDrawingFiles">
              <label class="pdm-secondary-action pdm-file-button">上传PDF<input type="file" accept=".pdf" @change="uploadSelected"></label>
            </template>
            <button type="button" class="pdm-primary-action" :disabled="pending" @click="emit('submit', releasePackage.id)">{{ releasePackage.state === '已驳回' ? '重新提交审批' : '提交审批' }}</button>
          </div>
          <progress v-if="pending && progress > 0" :value="progress" max="100">{{ progress }}%</progress>
        </div>

        <div v-if="canHandleCurrentTask && currentTask" class="pdm-decision-box">
          <label>审批意见<textarea v-model="comment" rows="3" maxlength="1000" placeholder="通过可不填；退回必须填写原因" @input="approvalCommentError = ''" /><small v-if="approvalCommentError" class="pdm-inline-error">{{ approvalCommentError }}</small></label>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="submitDecision('Rejected')">退回</button>
            <button type="button" class="pdm-secondary-action" :disabled="pending" @click="openTransfer">转交</button>
            <button type="button" class="pdm-primary-action" :disabled="pending" @click="submitDecision('Approved')">通过</button>
          </div>
        </div>
        <div v-else-if="canManage && ['审批中', '工艺审核', '待批准'].includes(releasePackage.state)" class="pdm-decision-box pdm-withdraw-decision">
          <small>{{ releasePackage.locksDocuments ? '撤回后图档与非标件BOM恢复为工作中。' : '撤回后当前BOM版本恢复为草稿。' }}</small>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('withdraw', releasePackage.id)">撤回审批</button>
          </div>
        </div>
        <div v-if="canEmergencyDecide && currentTask && !canHandleCurrentTask" class="pdm-decision-box emergency-decision">
          <label>紧急代批原因<textarea v-model.trim="emergencyReason" rows="3" maxlength="1000" required placeholder="说明必须立即处理的业务原因；该内容会进入审计记录" /></label>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-secondary-action is-danger" :disabled="pending || !emergencyReason" @click="emit('emergencyDecide', currentTask.id, 'Rejected', emergencyReason)">紧急代驳回</button>
            <button type="button" class="pdm-primary-action" :disabled="pending || !emergencyReason" @click="emit('emergencyDecide', currentTask.id, 'Approved', emergencyReason)">紧急代批当前节点</button>
          </div>
        </div>
        <div v-if="canManage && releasePackage.scope === 'StandardLongLead' && releasePackage.state === '已发布'" class="pdm-decision-box pdm-long-lead-u9-retry">
          <small>自动补建当前及上级BOM料号申请；正式料号齐全后按BOM层级续传U9C。</small>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-secondary-action" :disabled="pending" @click="emit('retryU9', releasePackage.id)">{{ pending ? '处理中…' : '重试U9C' }}</button>
          </div>
        </div>
      </section>

      <div class="pdm-release-summary">
        <div><small>发布包</small><strong>{{ releasePackage.number }}</strong></div>
        <div><small>发布范围</small><strong>{{ scopeLabels[releasePackage.scope] || '旧版组合发布' }}</strong></div>
        <div><small>当前状态</small><strong>{{ releasePackage.state }}</strong></div>
        <div><small>审批模板</small><strong>{{ releaseWorkflowLabel }}</strong></div>
        <div><small>整套倍率</small><strong>× {{ releasePackage.wholeSetMultiplier ?? 1 }}</strong></div>
        <div v-if="releasePackage.changeNumber && releasePackage.changeNumber !== releasePackage.number"><small>变更单号</small><strong>{{ releasePackage.changeNumber }}</strong></div>
        <div><small>BOM版本</small><strong>{{ releaseBomRevisionLabel }}</strong></div>
        <div><small>制造基线</small><strong>{{ releasePackage.createsManufacturingBaseline ? '发布后生成新基线' : releasePackage.scope === 'StandardLongLead' ? '不更新（长交期输出）' : '三条正式流齐备后生成' }}</strong></div>
        <div><small>发布目录</small><strong>{{ releasePackage.publishedPath || '审批通过后自动投放' }}</strong></div>
      </div>
      <p v-if="releasePackage.changeReason" class="pdm-release-change-reason"><strong>{{ releasePackage.scope === 'StandardSupplement' || releasePackage.scope === 'ElectricalSupplement' ? '变更原因' : '备注' }}：</strong>{{ releasePackage.changeReason }}</p>

      <section class="pdm-release-frozen-snapshot" aria-label="审批固化快照">
        <header>
          <div><strong>审批固化快照</strong><small>显示发布后的最终数量（工作区数量 × 整套倍率 {{ releasePackage.wholeSetMultiplier ?? 1 }}）；批注独立保存，不修改BOM。</small></div>
          <div class="pdm-frozen-view-actions">
            <span>{{ frozenDisplayRows.length }} 项<span v-if="frozenViewMode === 'Summary' && frozenItems.length !== frozenDisplayRows.length"> · {{ frozenItems.length }} 个实例</span></span>
            <div class="pdm-view-switch" role="group" aria-label="审批快照显示方式">
              <button type="button" :class="{ 'is-active': frozenViewMode === 'Summary' }" :aria-pressed="frozenViewMode === 'Summary'" @click="frozenViewMode = 'Summary'">按汇总</button>
              <button type="button" :class="{ 'is-active': frozenViewMode === 'Structure' }" :aria-pressed="frozenViewMode === 'Structure'" @click="frozenViewMode = 'Structure'">按结构</button>
            </div>
          </div>
        </header>
        <div class="pdm-release-diff-summary"><span class="is-added">新增 {{ frozenDiff.added }}</span><span class="is-modified">修改 {{ frozenDiff.modified }}</span><span class="is-removed">减少 {{ frozenDiff.removed }}</span><small>对比上一正式发布版</small></div>
        <p v-if="itemCommentsError && !itemCommentOpen" class="pdm-inline-error pdm-item-comments-load-error">批注加载失败：{{ itemCommentsError }}</p>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table pdm-release-frozen-table">
            <colgroup><col><col><col><col><col><col><col><col><col></colgroup>
            <thead><tr><th>序号</th><th>物料编码</th><th>{{ frozenViewMode === 'Structure' ? '物料名称 / 结构位置' : '物料名称' }}</th><th>型号</th><th>品牌</th><th>备注</th><th>数量</th><th>版本</th><th>批注</th></tr></thead>
            <tbody>
              <tr v-for="(row, index) in pagedFrozenRows" :key="row.key">
                <td class="is-release-centered">{{ (frozenPage - 1) * releasePageSize + index + 1 }}</td>
                <td>{{ row.item.drawingNumber || '—' }}</td>
                <td class="pdm-frozen-item-name" :style="frozenViewMode === 'Structure' ? { paddingLeft: structureIndent(row.item) } : undefined">
                  <span v-if="frozenViewMode === 'Structure'" class="pdm-structure-marker">↳</span>{{ row.item.name || '—' }}
                  <small v-if="frozenViewMode === 'Structure' && structureLocation(row.item)">{{ structureLocation(row.item) }}</small>
                </td>
                <td>{{ row.item.specification || '—' }}</td>
                <td class="is-release-centered">{{ row.item.brand || '—' }}</td>
                <td>{{ row.item.remark || '—' }}</td>
                <td class="is-release-centered">{{ row.item.quantity }}</td>
                <td>{{ row.item.revision || '—' }}</td>
                <td class="is-release-centered"><button type="button" class="pdm-item-comment-action" :aria-label="`查看或添加物料批注 ${row.item.drawingNumber || row.item.name}`" @click="openItemComment(row)">批注<span v-if="commentsForRow(row).length">（{{ commentsForRow(row).length }}）</span></button></td>
              </tr>
              <tr v-if="!frozenDisplayRows.length"><td colspan="9" class="pdm-empty-info">该发布包没有BOM快照数据。</td></tr>
            </tbody>
          </table>
        </div>
        <nav class="release-list-pagination" aria-label="审批固化快照分页">
          <span>共 {{ frozenDisplayRows.length }} 条 · 50 条/页</span>
          <button type="button" :disabled="frozenPage <= 1" aria-label="固化快照上一页" @click="frozenPage--">‹</button>
          <strong>{{ frozenPage }} / {{ frozenPageCount }}</strong>
          <button type="button" :disabled="frozenPage >= frozenPageCount" aria-label="固化快照下一页" @click="frozenPage++">›</button>
        </nav>
        <p v-if="releasePackage.scope === 'StandardLongLead'" class="pdm-release-integration-note">发布后正常输出长交期BOM，并写入U9C待同步集成事件；不更新制造基线。</p>
      </section>

      <div v-if="itemCommentOpen && itemCommentTarget" class="pdm-dialog-backdrop pdm-item-comment-backdrop" role="presentation" @click.self="itemCommentOpen = false">
        <section class="pdm-item-comment-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-item-comment-title">
          <header><div><strong id="pdm-item-comment-title">物料审批批注</strong><small>{{ itemCommentTarget.item.drawingNumber || '无料号' }} · {{ itemCommentTarget.item.name }}</small></div><button type="button" class="pdm-icon-button" aria-label="关闭物料批注" @click="itemCommentOpen = false">×</button></header>
          <div class="pdm-item-comment-history" aria-label="物料批注历史">
            <article v-for="entry in selectedItemComments" :key="entry.id"><p>{{ entry.comment }}</p><small>{{ displayUserName(entry.createdBy) }} · {{ formatCommentTime(entry.createdAt) }}</small></article>
            <p v-if="itemCommentsLoading">正在加载批注…</p>
            <p v-else-if="!selectedItemComments.length">暂无批注。</p>
          </div>
          <form v-if="canAddItemComment" @submit.prevent="saveItemComment">
            <label>新增批注<textarea v-model.trim="itemCommentText" rows="3" maxlength="1000" aria-label="物料审批批注" placeholder="批注只追加保存，不修改BOM内容" /></label>
            <p v-if="itemCommentsError" class="pdm-inline-error">{{ itemCommentsError }}</p>
            <footer><button type="button" class="pdm-secondary-action" @click="itemCommentOpen = false">关闭</button><button type="submit" class="pdm-primary-action" :disabled="itemCommentSaving || !itemCommentText.trim()">{{ itemCommentSaving ? '保存中…' : '保存批注' }}</button></footer>
          </form>
          <footer v-else><small>审批已结束或当前账号不是本节点处理人，批注历史仅供查看。</small><button type="button" class="pdm-secondary-action" @click="itemCommentOpen = false">关闭</button></footer>
        </section>
      </div>

      <div v-if="transferOpen" class="pdm-dialog-backdrop pdm-approval-transfer-backdrop" role="presentation" @click.self="transferOpen = false">
        <section class="pdm-approval-transfer-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-approval-transfer-title">
          <header><strong id="pdm-approval-transfer-title">转交审批</strong><button type="button" class="pdm-icon-button" aria-label="关闭转交审批" @click="transferOpen = false">×</button></header>
          <label>选择人员<input v-model.trim="transferQuery" type="search" placeholder="输入姓名或账号筛选" aria-label="筛选审批转交人员"></label>
          <div class="pdm-approval-transfer-list" role="listbox" aria-label="审批转交人员">
            <button v-for="candidate in filteredTransferCandidates" :key="candidate.username" type="button" :class="{ 'is-selected': transferTarget === candidate.username }" role="option" :aria-selected="transferTarget === candidate.username" @click="transferTarget = candidate.username"><strong>{{ candidate.displayName }}</strong><small>{{ candidate.username }}</small></button>
            <p v-if="transferCandidatesLoading">正在加载可转交人员…</p>
            <p v-else-if="transferError" class="pdm-inline-error">{{ transferError }}</p>
            <p v-else-if="!filteredTransferCandidates.length">没有符合条件的可转交人员。</p>
          </div>
          <label>转交说明<textarea v-model.trim="transferComment" rows="2" maxlength="500" placeholder="选填"></textarea></label>
          <footer><button type="button" class="pdm-secondary-action" @click="transferOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending || !transferTarget" @click="confirmTransfer">确认转交</button></footer>
        </section>
      </div>
      <p v-if="releasePackage.publishError" class="pdm-inline-error">发布失败：{{ releasePackage.publishError }}</p>
    </template>
    <p v-if="error" class="pdm-inline-error">{{ error }}</p>
  </section>
</template>

<style scoped>
.pdm-release-top-workflow{display:grid;gap:6px;margin-bottom:8px}.pdm-release-top-workflow .pdm-approval-chain,.pdm-release-summary{grid-template-columns:repeat(4,minmax(0,1fr));gap:6px}.pdm-release-top-workflow .pdm-approval-chain{margin:0}.pdm-release-top-workflow .pdm-approval-chain article,.pdm-release-summary>div{min-width:0;min-height:46px;box-sizing:border-box;align-content:center;gap:2px;padding:6px 8px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface)}.pdm-release-top-workflow .pdm-approval-chain article{align-items:flex-start;gap:6px}.pdm-release-top-workflow .pdm-approval-chain article div{overflow:hidden}.pdm-release-top-workflow .pdm-approval-chain strong,.pdm-release-top-workflow .pdm-approval-chain small,.pdm-release-top-workflow .pdm-approval-chain em{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-release-top-workflow .pdm-release-draft-management{margin:0}.pdm-release-top-workflow .pdm-release-preparation{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:8px;margin:0;padding:6px 8px}.pdm-release-top-workflow .pdm-release-preparation h3,.pdm-release-top-workflow .pdm-release-preparation p{margin:0}.pdm-release-top-workflow .pdm-release-preparation .pdm-manager-actions{flex-wrap:nowrap}.pdm-release-top-workflow .pdm-release-preparation progress{grid-column:1/-1;margin-top:0}.pdm-release-top-workflow .pdm-decision-box{grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:8px;margin:0;padding:6px 8px}.pdm-release-top-workflow .pdm-decision-box textarea{min-height:30px;height:30px;box-sizing:border-box;resize:vertical}.pdm-release-top-workflow .pdm-withdraw-decision{align-items:center}.pdm-release-summary{grid-auto-rows:minmax(46px,auto);margin-bottom:8px}.pdm-release-summary small{font-size:10px}.pdm-release-summary strong{font-size:11px}@media(max-width:900px){.pdm-release-top-workflow .pdm-approval-chain,.pdm-release-summary{grid-template-columns:repeat(2,minmax(0,1fr))}.pdm-release-top-workflow .pdm-release-preparation,.pdm-release-top-workflow .pdm-decision-box{grid-template-columns:1fr}.pdm-release-top-workflow .pdm-release-preparation .pdm-manager-actions,.pdm-release-top-workflow .pdm-decision-box .pdm-manager-actions{justify-content:flex-end}}@media(max-width:560px){.pdm-release-top-workflow .pdm-approval-chain,.pdm-release-summary{grid-template-columns:1fr}}
.release-center select{height:34px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff;padding:0 9px;color:var(--pdm-text)}.pdm-release-create-header{grid-column:1/-1;display:grid;grid-template-rows:52px 82px;gap:8px;min-height:162px;padding:10px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface)}.pdm-release-type-row{display:grid;grid-template-columns:minmax(220px,1fr) minmax(120px,180px) auto minmax(180px,1fr);gap:5px;align-items:end}.pdm-release-type-row label{min-width:0}.pdm-release-type-row select,.pdm-release-type-row input{width:100%;height:34px;box-sizing:border-box}.pdm-release-draft-actions{display:flex;gap:5px}.pdm-release-draft-actions button{height:34px;padding:0 10px;white-space:nowrap}.pdm-release-parameter-slot{min-height:82px;overflow:auto}.pdm-release-parameter-slot>.pdm-release-reason{height:100%;box-sizing:border-box}.pdm-release-parameter-slot>.pdm-release-reason textarea{height:60px;box-sizing:border-box;resize:none}.release-detail-picker,.release-change-reason-picker{grid-column:1/-1;margin:0;padding:10px;border:1px solid var(--pdm-border);border-radius:7px}.release-detail-picker legend,.release-change-reason-picker legend{padding:0 5px;font-weight:600}.release-detail-picker .pdm-table-scroll{border:1px solid var(--pdm-border);border-radius:5px}.release-detail-picker table{width:100%;min-width:0;table-layout:fixed}.release-detail-picker col:nth-child(1){width:6%}.release-detail-picker col:nth-child(2){width:5%}.release-detail-picker col:nth-child(3){width:12%}.release-detail-picker col:nth-child(4){width:13%}.release-detail-picker col:nth-child(5){width:15%}.release-detail-picker col:nth-child(6){width:8%}.release-detail-picker col:nth-child(7){width:9%}.release-detail-picker col:nth-child(8){width:10%}.release-detail-picker col:nth-child(9){width:12%}.release-detail-picker col:nth-child(10){width:10%}.release-detail-picker th,.release-detail-picker td{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;vertical-align:middle}.release-detail-picker th,.release-detail-picker td.is-release-centered{text-align:center}.release-detail-picker td:nth-child(9),.release-detail-picker td:nth-child(10){white-space:normal;overflow-wrap:anywhere}.long-lead-quantity-input{width:100%;min-width:0;height:26px;box-sizing:border-box;text-align:center}.pdm-release-draft-management{justify-content:flex-end;margin-bottom:8px}.release-change-reason-picker{display:flex;height:100%;box-sizing:border-box;flex-wrap:wrap;align-content:flex-start;gap:8px 18px}.release-change-reason-picker label{display:flex;align-items:center;gap:5px}.long-lead-tag,.release-change-tag,.release-inclusion-tag,.release-status-tag{display:inline-flex;align-items:center;min-height:20px;padding:0 6px;border-radius:10px}.long-lead-tag,.release-change-tag{background:#fff7ed;color:#c2410c}.release-inclusion-tag{background:#eff6ff;color:#1d4ed8}.release-status-tag{background:var(--pdm-surface-soft);color:var(--pdm-muted)}.release-change-tag.is-新增{background:#ecfdf5;color:#15803d}.release-change-tag.is-修改{background:#fff7ed;color:#b45309}.release-change-tag.is-删除{background:#fef2f2;color:#b91c1c}.pdm-release-create-form .pdm-release-reason{grid-column:1/-1}.emergency-decision{border-color:#f59e0b;background:#fffbeb}.pdm-release-frozen-snapshot{margin:12px 0;border:1px solid var(--pdm-border);border-radius:7px;overflow:hidden}.pdm-release-frozen-snapshot>header{display:flex;justify-content:space-between;gap:12px;align-items:center;padding:9px 11px;background:var(--pdm-surface-soft)}.pdm-release-frozen-snapshot>header>div:first-child{display:grid;gap:2px;min-width:0}.pdm-release-frozen-snapshot>header small{color:var(--pdm-muted)}.pdm-frozen-view-actions{display:flex;align-items:center;justify-content:flex-end;gap:10px;white-space:nowrap}.pdm-view-switch{display:inline-flex;padding:2px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff}.pdm-view-switch button{height:24px;padding:0 9px;border:0;border-radius:4px;background:transparent;color:var(--pdm-muted)}.pdm-view-switch button.is-active{background:#0f9d90;color:#fff}.pdm-release-diff-summary{display:flex;align-items:center;gap:10px;padding:7px 11px;border-top:1px solid var(--pdm-border);border-bottom:1px solid var(--pdm-border)}.pdm-release-diff-summary small{margin-left:auto;color:var(--pdm-muted)}.pdm-release-diff-summary .is-added{color:#15803d}.pdm-release-diff-summary .is-modified{color:#b45309}.pdm-release-diff-summary .is-removed{color:#b91c1c}.pdm-release-frozen-snapshot .pdm-table-scroll{max-height:230px}.pdm-item-comments-load-error{margin:0;padding:7px 11px}.pdm-item-comment-action{border:0;background:transparent;color:#0f9d90;white-space:nowrap}.pdm-frozen-item-name{padding-left:7px}.pdm-frozen-item-name small{display:block;margin-top:2px;color:var(--pdm-muted);font-size:10px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-structure-marker{margin-right:4px;color:#0f9d90}.pdm-release-integration-note{margin:0;padding:8px 11px;color:#0f766e;background:#f0fdfa;border-top:1px solid #99f6e4}@media(max-width:900px){.pdm-release-type-row{grid-template-columns:repeat(2,minmax(0,1fr))}.pdm-release-frozen-snapshot>header{align-items:flex-start;flex-direction:column}.pdm-frozen-view-actions{width:100%;justify-content:space-between}}
.release-detail-picker{display:flex;width:100%;min-width:0;min-height:0;box-sizing:border-box;flex-direction:column}.release-detail-picker .pdm-table-scroll{width:100%;height:clamp(220px,calc(100dvh - 440px),630px);max-width:100%;max-height:none;box-sizing:border-box;overflow-x:hidden;overflow-y:auto}.release-detail-pagination,.release-list-pagination{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding-top:8px;color:var(--pdm-muted)}.release-detail-pagination span,.release-list-pagination span{margin-right:auto}.release-detail-pagination button,.release-list-pagination button{width:28px;height:28px;border:1px solid var(--pdm-border);border-radius:6px;background:var(--pdm-surface);color:var(--pdm-text);cursor:pointer}.release-detail-pagination button:disabled,.release-list-pagination button:disabled{cursor:not-allowed;opacity:.45}.release-detail-pagination strong,.release-list-pagination strong{min-width:54px;text-align:center;color:var(--pdm-text)}
.pdm-release-frozen-snapshot{display:flex;min-width:0;min-height:0;flex-direction:column}.pdm-release-frozen-snapshot .pdm-table-scroll{width:100%;height:clamp(220px,calc(100dvh - 470px),620px);max-width:100%;max-height:none;box-sizing:border-box;overflow-x:hidden;overflow-y:auto}.pdm-release-frozen-snapshot .release-list-pagination{padding:8px 11px}
.pdm-release-frozen-table{width:100%;min-width:0;table-layout:fixed}.pdm-release-frozen-table col:nth-child(1){width:5%}.pdm-release-frozen-table col:nth-child(2){width:12%}.pdm-release-frozen-table col:nth-child(3){width:21%}.pdm-release-frozen-table col:nth-child(4){width:15%}.pdm-release-frozen-table col:nth-child(5){width:10%}.pdm-release-frozen-table col:nth-child(6){width:13%}.pdm-release-frozen-table col:nth-child(7),.pdm-release-frozen-table col:nth-child(8){width:7%}.pdm-release-frozen-table col:nth-child(9){width:10%}.pdm-release-frozen-table th,.pdm-release-frozen-table td{white-space:normal;overflow-wrap:anywhere;vertical-align:middle}.pdm-release-frozen-table th,.pdm-release-frozen-table td.is-release-centered{text-align:center}
.pdm-item-comment-backdrop{z-index:3200}.pdm-item-comment-dialog{width:min(560px,calc(100vw - 32px));max-height:min(680px,calc(100vh - 32px));display:grid;grid-template-rows:auto minmax(120px,1fr) auto;gap:12px;padding:16px;border-radius:10px;background:#fff;box-shadow:0 18px 60px rgb(15 23 42 / 24%)}.pdm-item-comment-dialog>header,.pdm-item-comment-dialog>footer,.pdm-item-comment-dialog form>footer{display:flex;align-items:center;justify-content:space-between;gap:8px}.pdm-item-comment-dialog>header>div{display:grid;gap:3px}.pdm-item-comment-dialog>header small{color:var(--pdm-muted)}.pdm-item-comment-history{min-height:120px;overflow:auto;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.pdm-item-comment-history article{padding:10px 12px;border-bottom:1px solid var(--pdm-border);background:#fff}.pdm-item-comment-history article:last-child{border-bottom:0}.pdm-item-comment-history p{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}.pdm-item-comment-history small{display:block;margin-top:5px;color:var(--pdm-muted)}.pdm-item-comment-history>p{margin:0;padding:14px;color:var(--pdm-muted)}.pdm-item-comment-dialog form{display:grid;gap:8px}.pdm-item-comment-dialog form label{display:grid;gap:5px}.pdm-item-comment-dialog textarea{width:100%;box-sizing:border-box;resize:vertical}.pdm-item-comment-dialog form>footer{justify-content:flex-end}.pdm-item-comment-dialog>footer{justify-content:flex-end}.pdm-item-comment-dialog>footer small{margin-right:auto;color:var(--pdm-muted)}
.pdm-approval-transfer-backdrop{z-index:3100}.pdm-approval-transfer-dialog{width:min(520px,calc(100vw - 32px));max-height:min(620px,calc(100vh - 32px));display:grid;grid-template-rows:auto auto minmax(100px,1fr) auto auto;gap:12px;padding:16px;border-radius:10px;background:#fff;box-shadow:0 18px 60px rgb(15 23 42 / 24%)}.pdm-approval-transfer-dialog>header,.pdm-approval-transfer-dialog>footer{display:flex;align-items:center;justify-content:space-between;gap:8px}.pdm-approval-transfer-dialog>footer{justify-content:flex-end}.pdm-approval-transfer-dialog>label{display:grid;gap:5px}.pdm-approval-transfer-dialog input,.pdm-approval-transfer-dialog textarea{width:100%;box-sizing:border-box}.pdm-approval-transfer-list{min-height:100px;overflow:auto;border:1px solid var(--pdm-border);border-radius:7px}.pdm-approval-transfer-list>button{width:100%;display:flex;align-items:center;justify-content:space-between;gap:12px;padding:9px 11px;border:0;border-bottom:1px solid var(--pdm-border);background:#fff;color:var(--pdm-text);text-align:left}.pdm-approval-transfer-list>button.is-selected{background:#ecfdf5;color:#0f766e}.pdm-approval-transfer-list>button small{color:var(--pdm-muted)}.pdm-approval-transfer-list>p{margin:0;padding:12px;color:var(--pdm-muted)}
.pdm-release-top-workflow .pdm-approval-chain article.is-current{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}
.pdm-release-top-workflow .pdm-approval-chain article.is-approved,.pdm-release-top-workflow .pdm-approval-chain article.is-done{border-color:var(--pdm-green);background:var(--pdm-green-soft);color:var(--pdm-green)}
.pdm-release-top-workflow .pdm-approval-chain article.is-rejected{border-color:var(--pdm-danger);background:#fff0ef;color:var(--pdm-danger)}
.pdm-release-top-workflow .pdm-approval-chain article.is-skipped{background:var(--pdm-surface-muted);color:var(--pdm-muted)}
.pdm-release-top-workflow .pdm-approval-chain small,.pdm-release-top-workflow .pdm-approval-chain em{overflow:visible;text-overflow:clip;white-space:normal;overflow-wrap:anywhere}
.pdm-release-top-workflow .pdm-decision-box label{display:grid;gap:4px}.pdm-release-top-workflow .pdm-decision-box .pdm-inline-error{margin:0;padding:5px 7px}
</style>
