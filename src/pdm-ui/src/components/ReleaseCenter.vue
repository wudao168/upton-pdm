<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { addReleaseItemComment, listApprovalTransferCandidates, listReleaseItemComments } from '../api'
import { releasePreviewStateLabel } from '../releasePreviewState'
import type { ApprovalTransferCandidate, BomItem, CreateReleasePackageInput, DrawingDeliveryOverride, DrawingPriority, DrawingReviewCandidate, DrawingReviewPackage, FormalSupplementPolicies, ReleaseItemComment, ReleasePackageSummary, ReleaseScope, UpdateReleasePackageDraftInput } from '../types'
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
  formalSupplementPolicies?: FormalSupplementPolicies
  releasePackages?: ReleasePackageSummary[]
  longLeadPublishedItems?: BomItem[]
  previousVersionItems?: BomItem[]
  drawingReviewCandidates?: DrawingReviewCandidate[]
  drawingReviews?: DrawingReviewPackage[]
}>(), { standardItems: () => [], releaseItems: () => [], canEmergencyDecide: false, allowedScopes: () => [], changeReasonTypes: () => [], formalSupplementPolicies: () => ({ standard: { maximumCount: 2, validDays: null }, electrical: { maximumCount: 2, validDays: null } }), releasePackages: () => [], longLeadPublishedItems: () => [], previousVersionItems: () => [], drawingReviewCandidates: () => [] })
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
  retryPreview: [releasePackageId: string]
}>()

const releaseTypes: { value: Exclude<ReleaseScope, 'LegacyCombined'>; label: string }[] = [
  { value: 'StandardLongLead', label: '标准件 · 长交期BOM发布' },
  { value: 'StandardFormal', label: '标准件 · 正式发布' },
  { value: 'StandardSupplement', label: '标准件 · 增补/变更' },
  { value: 'ElectricalFormal', label: '电气BOM · 正式发布' },
  { value: 'ElectricalSupplement', label: '电气BOM · 增补/变更' },
  { value: 'ElectricalLongLead', label: '电气件 · 长交期BOM发布' },
  { value: 'NonStandardLongLead', label: '非标件 · 长交期BOM发布' },
  { value: 'NonStandardWithDrawing', label: '非标件BOM + 图纸 · 正式发布' },
  { value: 'NonStandardSupplement', label: '非标件 · 增补/变更' },
]
const isLongLeadScope = (value: ReleaseScope) => value === 'StandardLongLead' || value === 'NonStandardLongLead' || value === 'ElectricalLongLead'
const isSupplementScope = (value: ReleaseScope) => value === 'StandardSupplement' || value === 'ElectricalSupplement' || value === 'NonStandardSupplement'
const isNonStandardScope = (value: ReleaseScope) => value === 'NonStandardWithDrawing' || value === 'NonStandardLongLead' || value === 'NonStandardSupplement'
const formalScopeForSupplement = (value: ReleaseScope): ReleaseScope =>
  value === 'StandardSupplement' ? 'StandardFormal'
  : value === 'ElectricalSupplement' ? 'ElectricalFormal'
  : 'NonStandardWithDrawing'
const hasPublishedFormalFor = (value: ReleaseScope) => props.releasePackages
  .some(previous => previous.scope === formalScopeForSupplement(value) && previous.state === '已发布')
const scopeLabels = Object.fromEntries(releaseTypes.map(item => [item.value, item.label])) as Record<string, string>
const workflowNames: Record<string, string> = {
  'mechanical-release': '机械发布审批',
  'electrical-release': '电气发布审批',
}
const visibleReleaseTypes = computed(() => releaseTypes.filter(item =>
  (!props.allowedScopes.length || props.allowedScopes.includes(item.value))
  // 增补/变更仅在对应BOM完成首次正式发布后可选（与后端规则一致）。
  && !(isSupplementScope(item.value) && !hasPublishedFormalFor(item.value))
  && !((item.value === 'StandardLongLead' || item.value === 'StandardFormal')
    && props.releasePackages.some(previous => previous.scope === 'StandardFormal' && previous.state === '已发布'))
  && !((item.value === 'NonStandardLongLead' || item.value === 'NonStandardWithDrawing')
    && props.releasePackages.some(previous => previous.scope === 'NonStandardWithDrawing' && previous.state === '已发布'))
  && !((item.value === 'ElectricalLongLead' || item.value === 'ElectricalFormal')
    && props.releasePackages.some(previous => previous.scope === 'ElectricalFormal' && previous.state === '已发布'))))
const releaseNote = ref('')
const selectedChangeReasons = ref<string[]>([])
const otherChangeReason = ref('')
const changeReasonGroups = [
  { category: '正式补充', reasons: [{ label: '正式补充', value: '正式补充' }] },
  { category: '物料问题', reasons: ['交期不满足', '物料下单晚', '买错物料', '物料漏买'].map(reason => ({ label: reason, value: `物料问题 / ${reason}` })) },
  { category: '图纸问题', reasons: ['图纸漏下', '图纸错误'].map(reason => ({ label: reason, value: `图纸问题 / ${reason}` })) },
  { category: '设计问题', reasons: ['设计变更', '设计错误'].map(reason => ({ label: reason, value: `设计问题 / ${reason}` })) },
  { category: '客户原因', reasons: ['客户需求变更', '客户信息输入错误', '客户未及时确认', '客户未及时提供产品'].map(reason => ({ label: reason, value: `客户原因 / ${reason}` })) },
  { category: '其他', reasons: [{ label: '其他', value: '其他' }] },
]
const scope = ref<Exclude<ReleaseScope, 'LegacyCombined'>>('StandardLongLead')
const drawingPriority = ref<DrawingPriority>('Normal')
const drawingRequiredOn = ref('')
const drawingOverrides = ref<Record<string, DrawingDeliveryOverride>>({})
const drawingScope = computed(() => scope.value === 'NonStandardWithDrawing' || scope.value === 'NonStandardSupplement')
const selectedDrawingCandidates = computed(() => {
  const selected = new Set(selectedScopedBomItemIds.value ?? [])
  return props.drawingReviewCandidates.filter(candidate => candidate.drawingDocumentId
    && candidate.bomItemId && selected.has(candidate.bomItemId))
    .filter((candidate, index, all) => all.findIndex(item => item.drawingDocumentId === candidate.drawingDocumentId) === index)
})
function setDrawingOverride(id: string, priority: DrawingPriority, requiredOn: string) {
  drawingOverrides.value = { ...drawingOverrides.value, [id]: { priority, requiredOn } }
}
const wholeSetMultiplier = ref(1)
const selectedLongLeadKeys = ref<string[]>([])
const selectedFormalKeys = ref<string[]>([])
const selectedSupplementKeys = ref<string[]>([])
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
  if (releasePackage.scope === 'NonStandardLongLead') {
    return `标准件：不适用 · 非标件：长交期批次 ${releasePackage.nonStandardBomRevision || '未生成'} · 电气件：不适用`
  }
  if (releasePackage.scope === 'ElectricalLongLead') {
    return `标准件：不适用 · 非标件：不适用 · 电气件：长交期批次 ${releasePackage.electricalBomRevision || '未生成'}`
  }
  return `标准件：${releasePackage.standardBomRevision || '未发布'} · 非标件：${releasePackage.nonStandardBomRevision || '未发布'} · 电气件：${releasePackage.electricalBomRevision || '未发布'}`
})
const canPrepare = computed(() => !props.releasePackage || ['草稿', '已驳回', '发布失败'].includes(props.releasePackage.state))
const isSupplement = computed(() => isSupplementScope(scope.value))
const isFormalSupplementReason = (releasePackage: ReleasePackageSummary) =>
  Boolean(releasePackage.changeReasonSelections?.some(item => item.categoryCode === 'FormalSupplement'))
  || (releasePackage.changeReason || '').split('；').some(reason => reason.trim() === '正式补充')
const formalSupplementAvailability = computed(() => {
  const standardPolicy = scope.value !== 'ElectricalSupplement'
  const formalScope: ReleaseScope = scope.value === 'StandardSupplement' ? 'StandardFormal'
    : scope.value === 'NonStandardSupplement' ? 'NonStandardWithDrawing' : 'ElectricalFormal'
  const supplementScope: ReleaseScope = scope.value
  const formal = props.releasePackages
    .filter(item => item.scope === formalScope && item.state === '已发布' && item.publishedAt)
    .sort((left, right) => new Date(left.publishedAt!).getTime() - new Date(right.publishedAt!).getTime())[0]
  if (!formal) return { allowed: false, status: '尚未完成首次正式发布', used: 0, active: 0, maximum: 0 as number | null, validUntil: '' }
  const configured = standardPolicy ? props.formalSupplementPolicies.standard : props.formalSupplementPolicies.electrical
  const maximum = formal.formalSupplementPolicySnapshotted ? formal.formalSupplementMaximumCount ?? null : configured.maximumCount ?? null
  const validDays = formal.formalSupplementPolicySnapshotted ? formal.formalSupplementValidDays ?? null : configured.validDays ?? null
  const relevant = props.releasePackages.filter(item => item.id !== props.releasePackage?.id && item.scope === supplementScope && isFormalSupplementReason(item))
  const used = relevant.filter(item => item.state === '已发布').length
  const active = relevant.filter(item => ['审批中', '工艺审核', '待批准', '发布中'].includes(item.state)).length
  const validUntilDate = validDays == null ? null : new Date(new Date(formal.publishedAt!).getTime() + validDays * 86400000)
  const expired = Boolean(validUntilDate && Date.now() > validUntilDate.getTime())
  const exhausted = maximum != null && used + active >= maximum
  const countText = maximum == null ? `已发布 ${used} 次，不限次数` : `已发布 ${used}/${maximum} 次${active ? `，在途 ${active} 次` : ''}`
  const timeText = validUntilDate ? `有效期至 ${validUntilDate.toLocaleString('zh-CN', { hour12: false })}` : '不限时间'
  const status = expired ? `${countText}；已超过${timeText.replace('有效期至 ', '')}` : exhausted ? `${countText}；次数已用完` : `${countText}；${timeText}`
  return { allowed: !expired && !exhausted, status, used, active, maximum, validUntil: validUntilDate?.toISOString() ?? '' }
})
const changeReasonText = computed(() => selectedChangeReasons.value.map(reason =>
  reason === '其他' ? `其他 / ${otherChangeReason.value.trim()}` : reason).join('；'))
const changeReasonSummary = computed(() => selectedChangeReasons.value.map(reason =>
  reason === '其他' ? `其他 / ${otherChangeReason.value.trim() || '请填写具体原因'}` : reason).join('；'))
const availableReleaseItems = computed(() => (props.releaseItems.length ? props.releaseItems : props.standardItems)
  .filter(row => !row.manuallyExcluded && !row.releaseExcluded))
const releasePageSize = 50
const longLeadPage = ref(1)
const formalPage = ref(1)
const supplementPage = ref(1)
const frozenPage = ref(1)
const frozenViewMode = ref<'Summary' | 'Structure'>('Summary')
const frozenShowFullBom = ref(false)
const releaseItemComments = ref<ReleaseItemComment[]>([])
const itemCommentsLoading = ref(false)
const itemCommentsError = ref('')
const itemCommentOpen = ref(false)
const itemCommentTarget = ref<FrozenDisplayRow | null>(null)
const itemCommentText = ref('')
const itemCommentSaving = ref(false)
type FrozenDisplayRow = { key: string; materialKey: string; item: BomItem; sourceItems: BomItem[]; change?: string; details?: string[] }
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
const drawingReviewCandidateFor = (item: BomItem) => props.drawingReviewCandidates.find(candidate => candidate.bomItemId === item.id)
  ?? props.drawingReviewCandidates.find(candidate => Boolean(item.sourceDocumentId) && candidate.modelDocumentId === item.sourceDocumentId)
// 主管批准后审核单进入“已批准/回写属性”，逐张图纸记录可能仍是“已通过（待批准）”，这里同样按已批准处理。
const drawingReviewApprovedByPackage = (item: BomItem) => {
  const candidate = drawingReviewCandidateFor(item)
  if (!candidate) return false
  return (props.drawingReviews ?? []).some(packageValue =>
    (packageValue.state === 'Approved' || packageValue.state === 'WritingProperties')
    && packageValue.items.some(value =>
      (Boolean(candidate.drawingDocumentId) && value.drawingDocumentId === candidate.drawingDocumentId)
      || (Boolean(candidate.modelDocumentId) && value.modelDocumentId === candidate.modelDocumentId)))
}
const itemDrawingReviewReady = (item: BomItem) => item.kind !== 'NonStandard'
  || drawingReviewCandidateFor(item)?.state === 'ApprovedCurrent'
  || drawingReviewApprovedByPackage(item)
const releaseRowDrawingReviewReady = (row: { sourceItems: BomItem[] }) => row.sourceItems.length > 0 && row.sourceItems.every(itemDrawingReviewReady)
const drawingReviewStatusForItem = (item: BomItem) => {
  const candidate = drawingReviewCandidateFor(item)
  if (candidate?.state === 'ApprovedCurrent') return '图纸已批准'
  if (drawingReviewApprovedByPackage(item)) return '图纸已批准'
  if (candidate?.state === 'InReview') return '图纸待审核'
  if (candidate?.state === 'Unavailable') return candidate.reason || '图纸不可审核'
  return candidate?.reason || '图纸待提交'
}
const releaseRowDrawingReviewStatus = (row: { sourceItems: BomItem[] }) => {
  if (releaseRowDrawingReviewReady(row)) return '图纸已批准'
  return [...new Set(row.sourceItems.map(drawingReviewStatusForItem))].join('；') || '图纸待提交'
}
const currentReleaseKeysByTrackingId = computed(() => new Map(
  availableReleaseItems.value
    .filter(item => item.releaseTrackingId)
    .map((item, index) => [item.releaseTrackingId!, releaseItemGroupKey(item, index)]),
))
const longLeadPublishedQuantities = computed(() => {
  const quantities = new Map<string, number>()
  props.longLeadPublishedItems.forEach((item, index) => {
    const key = item.releaseTrackingId
      ? currentReleaseKeysByTrackingId.value.get(item.releaseTrackingId) ?? releaseItemGroupKey(item, index)
      : releaseItemGroupKey(item, index)
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
  const grouped = new Map<string, { key: string; item: BomItem; sourceItems: BomItem[]; longLeadPublishedQuantity: number }>()
  availableReleaseItems.value.forEach((item, index) => {
    const key = releaseItemGroupKey(item, index)
    const existing = grouped.get(key)
    if (!existing) {
      grouped.set(key, {
        key,
        item: { ...item },
        sourceItems: item.id ? [item] : [],
        longLeadPublishedQuantity: longLeadPublishedQuantities.value.get(key) ?? 0,
      })
      return
    }
    existing.item.quantity = Number(existing.item.quantity) + Number(item.quantity)
    if (item.id) existing.sourceItems.push(item)
  })
  return [...grouped.values()].map(row => ({
    ...row,
    remainingQuantity: Math.max(0, Number(row.item.quantity) - row.longLeadPublishedQuantity),
  }))
})
const isFormalRowFullyPublished = (row: { remainingQuantity: number }) => row.remainingQuantity <= 0
const fullyPublishedFormalRowCount = computed(() => formalReleaseRows.value.filter(isFormalRowFullyPublished).length)
const formalRowPublishQuantity = (row: { item: BomItem; longLeadPublishedQuantity: number }) =>
  Math.max(0, Number(row.item.quantity) * Number(wholeSetMultiplier.value) - row.longLeadPublishedQuantity)
const formalRowPublishTitle = (row: { item: BomItem; longLeadPublishedQuantity: number; remainingQuantity: number }) =>
  isFormalRowFullyPublished(row)
    ? `该物料已发布${row.longLeadPublishedQuantity}/${row.item.quantity}，已发布数量不允许重复发布，本次不再下发数量。`
    : `该物料已发布${row.longLeadPublishedQuantity}/${row.item.quantity}，本次正式发布只下发剩余数量${row.remainingQuantity}。`
const selectedFormalBomItemIds = computed(() => formalReleaseRows.value
  .filter(row => selectedFormalKeys.value.includes(row.key))
  .flatMap(row => row.sourceItems.map(item => item.id).filter((id): id is string => Boolean(id))))
const formalPageCount = computed(() => Math.max(1, Math.ceil(formalReleaseRows.value.length / releasePageSize)))
const pagedFormalReleaseRows = computed(() => {
  const start = (formalPage.value - 1) * releasePageSize
  return formalReleaseRows.value.slice(start, start + releasePageSize)
})
const itemKey = (item: BomItem) => `${(item.drawingNumber || `${item.name}|${item.specification}`).trim().toLocaleLowerCase()}|${item.unit?.trim().toLocaleLowerCase() ?? ''}`
const comparedFields: Array<{ key: keyof BomItem; label: string }> = [
  { key: 'kind', label: '分类' }, { key: 'unit', label: '单位' }, { key: 'drawingNumber', label: '物料编码' },
  { key: 'name', label: '名称' }, { key: 'specification', label: '型号' }, { key: 'remark', label: '备注' },
  { key: 'brand', label: '品牌' }, { key: 'material', label: '材质' }, { key: 'surfaceTreatment', label: '表面处理' },
  { key: 'heatTreatment', label: '热处理' }, { key: 'weight', label: '重量' }, { key: 'quantity', label: '数量' }, { key: 'revision', label: '版本' },
]
const fieldValue = (item: BomItem, key: keyof BomItem) => String(item[key] ?? '').trim()
const itemSignature = (item: BomItem) => JSON.stringify(comparedFields.map(field => fieldValue(item, field.key)))
function changedRows(current: BomItem[], previous: BomItem[]) {
  const remaining = new Set(previous)
  const matches = new Map<BomItem, BomItem>()
  // Reserve stable identities first, so repeated material codes cannot consume another instance's baseline.
  for (const item of current) {
    const match = [...remaining].find(old => Boolean(item.id) && old.id === item.id)
    if (match) { matches.set(item, match); remaining.delete(match) }
  }
  for (const item of current.filter(row => !matches.has(row))) {
    const match = [...remaining].find(old => Boolean(item.sourceDocumentId && item.sourceInstancePath)
      && old.sourceDocumentId === item.sourceDocumentId && old.sourceInstancePath === item.sourceInstancePath
      && old.sourceConfiguration === item.sourceConfiguration)
      ?? [...remaining].find(old => itemSignature(old) === itemSignature(item))
    if (match) { matches.set(item, match); remaining.delete(match) }
  }
  for (const item of current.filter(row => !matches.has(row))) {
    const candidates = [...remaining].filter(old => itemKey(old) === itemKey(item))
    if (candidates.length === 1 && current.filter(row => !matches.has(row) && itemKey(row) === itemKey(item)).length === 1) {
      matches.set(item, candidates[0]!); remaining.delete(candidates[0]!)
    }
  }
  const rows: Array<{ item: BomItem; change: string; details: string[] }> = []
  for (const item of current) {
    const old = matches.get(item)
    if (!old) { rows.push({ item, change: '新增', details: ['上一发布版不存在此项'] }); continue }
    const details = comparedFields.filter(field => fieldValue(old, field.key) !== fieldValue(item, field.key))
      .map(field => `${field.label}：${fieldValue(old, field.key) || '—'} → ${fieldValue(item, field.key) || '—'}`)
    if (details.length) rows.push({ item, change: '修改', details })
  }
  rows.push(...[...remaining].map(item => ({ item, change: '删除', details: ['当前发布范围已移除此项'] })))
  return rows
}
const supplementRows = computed(() => changedRows(availableReleaseItems.value, props.previousVersionItems))
const supplementPageCount = computed(() => Math.max(1, Math.ceil(supplementRows.value.length / releasePageSize)))
const pagedSupplementRows = computed(() => {
  const start = (supplementPage.value - 1) * releasePageSize
  return supplementRows.value.slice(start, start + releasePageSize)
})
const supplementRowKey = (row: { change: string; item: BomItem }) => `${row.change}|${row.item.id ?? itemKey(row.item)}`
// 只有新增项可以单独顺延；修改/删除的是已发布物料，必须随本次变更单一起发布。
const supplementRowSelectable = (row: { change: string }) => row.change === '新增'
// 本次不纳入的新增项（顺延到下一次变更）不参与发布范围校验。
const isDeferredSupplementRow = (row: { change: string; item: BomItem }) =>
  supplementRowSelectable(row) && !selectedSupplementKeys.value.includes(supplementRowKey(row))
const deferredSupplementItemIds = computed(() => supplementRows.value
  .filter(row => supplementRowSelectable(row) && !selectedSupplementKeys.value.includes(supplementRowKey(row)))
  .map(row => row.item.id)
  .filter((id): id is string => Boolean(id)))
const selectedSupplementBomItemIds = computed<string[] | undefined>(() => {
  const deferredIds = new Set(deferredSupplementItemIds.value)
  if (deferredIds.size === 0) return undefined
  return availableReleaseItems.value.flatMap(item => item.id && !deferredIds.has(item.id) ? [item.id] : [])
})
const isLongLeadRelease = computed(() => isLongLeadScope(scope.value))
const isFormalRelease = computed(() => scope.value === 'StandardFormal' || scope.value === 'ElectricalFormal' || scope.value === 'NonStandardWithDrawing')
// 三类BOM的每个发布阶段都支持选择本次发布的部分物料，规则与标准件正式发布一致。
// 增补/变更阶段未勾选的新增项顺延到下一次变更；已发布物料的修改与删除随本次变更单一起生效，不能单独顺延。
const selectedScopedBomItemIds = computed<string[] | undefined>(() => isLongLeadRelease.value
  ? selectedBomItemIds.value
  : isFormalRelease.value ? selectedFormalBomItemIds.value : selectedSupplementBomItemIds.value)
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
  if (isLongLeadRelease.value) return `选择长交期${scope.value === 'NonStandardLongLead' ? '非标件' : scope.value === 'ElectricalLongLead' ? '电气件' : '标准件'}（已选 ${selectedLongLeadKeys.value.length} 项）`
  if (isFormalRelease.value) return `正式发布内容（已选 ${selectedFormalKeys.value.length} / 共 ${formalReleaseRows.value.length} 项 · 默认全选 · 整套倍率 ×${wholeSetMultiplier.value}${fullyPublishedFormalRowCount.value ? ` · ${fullyPublishedFormalRowCount.value} 项已发布不再重复下发` : ''}）`
  const deferrableCount = supplementRows.value.filter(supplementRowSelectable).length
  return deferrableCount
    ? `增补/变更内容（共 ${supplementRows.value.length} 项变更 · 新增项已选 ${selectedSupplementKeys.value.length} / ${deferrableCount} 项 · 未勾选的新增项顺延到下一次变更 · 整套倍率 ×${wholeSetMultiplier.value}）`
    : `增补/变更内容（共 ${supplementRows.value.length} 项变更 · 整套倍率 ×${wholeSetMultiplier.value}）`
})
const releaseDetailEmptyText = computed(() => {
  if (isLongLeadRelease.value) return `当前没有剩余可发布数量的${scope.value === 'NonStandardLongLead' ? '非标件' : scope.value === 'ElectricalLongLead' ? '电气件' : '标准件'}。`
  if (isFormalRelease.value) return '当前没有可发布的物料。'
  return '与上一正式发布版相比，当前没有增补或变更内容。'
})
const hasInvalidLongLeadQuantity = computed(() => selectedLongLeadKeys.value.some(key => {
  const row = longLeadReleaseRows.value.find(item => item.key === key)
  const requested = Number(longLeadRequestedQuantities.value[key])
  return !row || !Number.isFinite(requested) || requested <= 0 || requested > Number(row.item.quantity)
}))
const hasInvalidWholeSetMultiplier = computed(() => !isLongLeadRelease.value
  && (!Number.isInteger(Number(wholeSetMultiplier.value)) || Number(wholeSetMultiplier.value) < 1 || Number(wholeSetMultiplier.value) > 1000))
const hasBlockedNonStandardDrawingReview = computed(() => {
  if (!isNonStandardScope(scope.value)) return false
  if (scope.value === 'NonStandardLongLead') {
    return selectedLongLeadKeys.value.some(key => {
      const row = longLeadReleaseRows.value.find(candidate => candidate.key === key)
      return !row || !releaseRowDrawingReviewReady(row)
    })
  }
  if (scope.value === 'NonStandardSupplement') {
    return supplementRows.value.some(row => row.change !== '删除' && !isDeferredSupplementRow(row) && !itemDrawingReviewReady(row.item))
  }
  return formalReleaseRows.value.some(row => selectedFormalKeys.value.includes(row.key) && !releaseRowDrawingReviewReady(row))
})
const drawingReviewBlockMessage = computed(() => {
  if (!isNonStandardScope(scope.value)) return ''
  if (scope.value === 'NonStandardLongLead') return longLeadReleaseRows.value.some(row => !releaseRowDrawingReviewReady(row))
    ? '未完成当前版本图纸审核的非标件已禁用，不能勾选发布。' : ''
  return hasBlockedNonStandardDrawingReview.value ? '当前发布范围包含未完成当前版本图纸审核的非标件，完成审核后才可创建发布草稿。' : ''
})
const createDisabled = computed(() => props.pending
  || drawingScope.value && !drawingRequiredOn.value
  || isLongLeadRelease.value && selectedBomItemIds.value.length === 0
  || isLongLeadRelease.value && hasInvalidLongLeadQuantity.value
  || isFormalRelease.value && selectedFormalBomItemIds.value.length === 0
  || isSupplement.value && selectedScopedBomItemIds.value !== undefined && selectedScopedBomItemIds.value.length === 0
  || hasInvalidWholeSetMultiplier.value
  || isSupplement.value && selectedChangeReasons.value.length === 0
  || isSupplement.value && selectedChangeReasons.value.includes('其他') && !otherChangeReason.value.trim()
  || isSupplement.value && selectedChangeReasons.value.includes('正式补充') && !formalSupplementAvailability.value.allowed
  || hasBlockedNonStandardDrawingReview.value)
const requiresDrawingFiles = computed(() => props.releasePackage?.locksDocuments ?? (isNonStandardScope(scope.value) && !isLongLeadScope(scope.value)))
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
  if (isNonStandardScope(props.releasePackage.scope)) return props.releasePackage.nonStandardBomSnapshot
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
const isFrozenSupplement = computed(() => Boolean(props.releasePackage && isSupplementScope(props.releasePackage.scope)))
const showFrozenChanges = computed(() => isFrozenSupplement.value && !frozenShowFullBom.value)
const frozenChanges = computed(() => changedRows(frozenItems.value, props.previousVersionItems))
const frozenChangeRows = computed<FrozenDisplayRow[]>(() => frozenChanges.value.map((row, index) => ({
  ...row,
  key: `change:${row.item.id || index}:${row.change}`,
  materialKey: frozenMaterialKey(row.item),
  item: { ...row.item, quantity: row.change === '删除' ? 0 : Number(row.item.quantity) * (props.releasePackage?.wholeSetMultiplier ?? 1) },
  sourceItems: [row.item],
})))
const frozenDisplayRows = computed(() => showFrozenChanges.value ? frozenChangeRows.value
  : frozenViewMode.value === 'Summary' ? frozenSummaryRows.value : frozenStructureRows.value)
const frozenPageCount = computed(() => Math.max(1, Math.ceil(frozenDisplayRows.value.length / releasePageSize)))
const showFormalIssueQuantities = computed(() => Boolean(props.releasePackage
  && ['StandardFormal', 'NonStandardWithDrawing', 'ElectricalFormal'].includes(props.releasePackage.scope)
  && frozenViewMode.value === 'Summary'))
const issueUnitAliases: Record<string, string> = { EA: '001', 件: '001', 个: '001', 台: '002', 盒: '004', 卷: '005', 捆: '006', 双: '007', 片: '008', 桶: '009', 支: '010', 组: '011', 套: '011', 箱: '012', 包: '013' }
const issueMaterialKey = (item: BomItem) => {
  const unit = item.unit?.trim().toUpperCase() ?? ''
  return `${item.drawingNumber?.trim().toUpperCase()}|${issueUnitAliases[unit] ?? unit}`
}
const formalPriorQuantities = computed(() => {
  const quantities = new Map<string, number>()
  const longLeadScope: ReleaseScope = props.releasePackage?.scope === 'NonStandardWithDrawing'
    ? 'NonStandardLongLead'
    : props.releasePackage?.scope === 'ElectricalFormal' ? 'ElectricalLongLead' : 'StandardLongLead'
  const currentKeysByTrackingId = new Map(frozenItems.value.filter(item => item.releaseTrackingId)
    .map(item => [item.releaseTrackingId!, issueMaterialKey(item)]))
  props.releasePackages.filter(previous => previous.scope === longLeadScope && previous.state === '已发布'
    && previous.publishedAt && (!props.releasePackage?.publishedAt || previous.publishedAt <= props.releasePackage.publishedAt))
    .forEach(previous => (longLeadScope === 'NonStandardLongLead' ? previous.nonStandardBomSnapshot
      : longLeadScope === 'ElectricalLongLead' ? previous.electricalBomSnapshot : previous.standardBomSnapshot)
      .filter(item => !item.manuallyExcluded && !item.releaseExcluded && !item.pendingRemoval)
      .forEach(item => {
        const key = item.releaseTrackingId ? currentKeysByTrackingId.get(item.releaseTrackingId) ?? issueMaterialKey(item) : issueMaterialKey(item)
        quantities.set(key, (quantities.get(key) ?? 0) + Number(item.quantity) * (previous.wholeSetMultiplier ?? 1))
      }))
  return quantities
})
const priorQuantityForRow = (row: FrozenDisplayRow) => formalPriorQuantities.value.get(issueMaterialKey(row.item)) ?? 0
const newIssueQuantityForRow = (row: FrozenDisplayRow) => Math.max(0, Number(row.item.quantity) - priorQuantityForRow(row))
const pagedFrozenRows = computed(() => {
  const start = (frozenPage.value - 1) * releasePageSize
  return frozenDisplayRows.value.slice(start, start + releasePageSize)
})
const canAddItemComment = computed(() => Boolean(currentTask.value && (canHandleCurrentTask.value || props.canEmergencyDecide)))
const selectedItemComments = computed(() => itemCommentTarget.value
  ? releaseItemComments.value.filter(item => item.materialKey.toLocaleLowerCase() === itemCommentTarget.value!.materialKey)
  : [])
const frozenDiff = computed(() => {
  const rows = frozenChanges.value
  return {
    added: rows.filter(row => row.change === '新增').length,
    modified: rows.filter(row => row.change === '修改').length,
    removed: rows.filter(row => row.change === '删除').length,
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
  frozenShowFullBom.value = false
  editingDraft.value = false
  itemCommentOpen.value = false
  comment.value = ''
  approvalCommentError.value = ''
})
watch(frozenViewMode, () => { frozenPage.value = 1 })
watch(showFrozenChanges, () => { frozenPage.value = 1; frozenViewMode.value = 'Summary' })
watch([() => props.releasePackage?.id, () => props.token], () => { void loadReleaseItemComments() }, { immediate: true })
watch(scope, () => {
  if (isLongLeadRelease.value) wholeSetMultiplier.value = 1
  longLeadPage.value = 1
  formalPage.value = 1
  supplementPage.value = 1
})
watch([() => availableReleaseItems.value.length, () => supplementRows.value.length], () => {
  if (!isLongLeadRelease.value) longLeadPage.value = 1
  else longLeadPage.value = Math.min(longLeadPage.value, longLeadPageCount.value)
  formalPage.value = Math.min(formalPage.value, formalPageCount.value)
  supplementPage.value = Math.min(supplementPage.value, supplementPageCount.value)
})
watch([() => longLeadReleaseRows.value.map(row => row.key).join('\n'), () => props.drawingReviewCandidates.map(candidate => `${candidate.candidateId}:${candidate.state}`).join('\n')], () => {
  const availableKeys = new Set(longLeadReleaseRows.value.map(row => row.key))
  selectedLongLeadKeys.value = selectedLongLeadKeys.value.filter(key => {
    const row = longLeadReleaseRows.value.find(candidate => candidate.key === key)
    return availableKeys.has(key) && Boolean(row && releaseRowDrawingReviewReady(row))
  })
  longLeadRequestedQuantities.value = Object.fromEntries(Object.entries(longLeadRequestedQuantities.value).filter(([key]) => availableKeys.has(key)))
})
watch([scope, () => formalReleaseRows.value.map(row => row.key).join('\n')], () => {
  selectedFormalKeys.value = isFormalRelease.value ? formalReleaseRows.value.map(row => row.key) : []
}, { immediate: true, flush: 'sync' })
watch([scope, () => supplementRows.value.map(row => supplementRowKey(row)).join('\n')], () => {
  selectedSupplementKeys.value = isSupplement.value
    ? supplementRows.value.filter(supplementRowSelectable).map(row => supplementRowKey(row))
    : []
}, { immediate: true, flush: 'sync' })

function create() {
  if (createDisabled.value) return
  const input: CreateReleasePackageInput = {
    changeReason: isSupplement.value ? changeReasonText.value : releaseNote.value,
    scope: scope.value,
    selectedBomItemIds: selectedScopedBomItemIds.value ?? [],
    selectedBomItemQuantities: isLongLeadRelease.value ? selectedBomItemQuantities.value : undefined,
    wholeSetMultiplier: isLongLeadRelease.value ? 1 : Number(wholeSetMultiplier.value),
    ...(drawingScope.value ? {
      drawingPriority: drawingPriority.value,
      drawingRequiredOn: drawingRequiredOn.value,
      drawingDeliveryOverrides: Object.fromEntries(Object.entries(drawingOverrides.value)
        .filter(([id]) => selectedDrawingCandidates.value.some(candidate => candidate.drawingDocumentId === id))),
    } : {}),
  }
  if (editingDraft.value && props.releasePackage) {
    emit('updateDraft', props.releasePackage.id, {
      changeReason: input.changeReason,
      selectedBomItemIds: input.selectedBomItemIds,
      selectedBomItemQuantities: input.selectedBomItemQuantities,
      wholeSetMultiplier: input.wholeSetMultiplier,
      ...(drawingScope.value ? {
        drawingPriority: input.drawingPriority,
        drawingRequiredOn: input.drawingRequiredOn,
        drawingDeliveryOverrides: input.drawingDeliveryOverrides,
      } : {}),
    })
    editingDraft.value = false
    return
  }
  emit('create', input)
}

function toggleLongLeadSelection(key: string, checked: boolean, maximum: number) {
  const row = longLeadReleaseRows.value.find(candidate => candidate.key === key)
  if (checked && (!row || !releaseRowDrawingReviewReady(row))) return
  if (checked) {
    if (!selectedLongLeadKeys.value.includes(key)) selectedLongLeadKeys.value = [...selectedLongLeadKeys.value, key]
    if (!(Number(longLeadRequestedQuantities.value[key]) > 0)) longLeadRequestedQuantities.value[key] = maximum
    return
  }
  selectedLongLeadKeys.value = selectedLongLeadKeys.value.filter(item => item !== key)
  delete longLeadRequestedQuantities.value[key]
}

function toggleFormalSelection(key: string, checked: boolean) {
  const row = formalReleaseRows.value.find(candidate => candidate.key === key)
  if (row && isFormalRowFullyPublished(row)) return
  selectedFormalKeys.value = checked
    ? [...new Set([...selectedFormalKeys.value, key])]
    : selectedFormalKeys.value.filter(item => item !== key)
}

function toggleSupplementSelection(row: { change: string; item: BomItem }, checked: boolean) {
  // 服务端要求同一料号+单位整组选择，这里同步切换同组的新增项。
  const groupKey = `${row.item.drawingNumber?.trim() ?? ''}|${row.item.unit?.trim() ?? ''}`
  const keys = supplementRows.value
    .filter(candidate => supplementRowSelectable(candidate)
      && `${candidate.item.drawingNumber?.trim() ?? ''}|${candidate.item.unit?.trim() ?? ''}` === groupKey)
    .map(candidate => supplementRowKey(candidate))
  selectedSupplementKeys.value = checked
    ? [...new Set([...selectedSupplementKeys.value, ...keys])]
    : selectedSupplementKeys.value.filter(item => !keys.includes(item))
}

function startDraftEdit() {
  const releasePackage = props.releasePackage
  if (!releasePackage || releasePackage.state !== '草稿' || releasePackage.scope === 'LegacyCombined') return
  scope.value = releasePackage.scope
  drawingPriority.value = releasePackage.drawingPriority ?? 'Normal'
  drawingRequiredOn.value = releasePackage.drawingRequiredOn ?? ''
  drawingOverrides.value = { ...releasePackage.drawingDeliveryOverrides }
  releaseNote.value = isSupplementScope(releasePackage.scope) ? '' : releasePackage.changeReason || ''
  selectedChangeReasons.value = []
  otherChangeReason.value = ''
  if (isSupplementScope(releasePackage.scope)) {
    if (releasePackage.changeReasonSelections?.length) {
      selectedChangeReasons.value = releasePackage.changeReasonSelections.map(item => {
        if (item.categoryCode === 'FormalSupplement') return '正式补充'
        if (item.categoryCode === 'Other') {
          otherChangeReason.value = item.detail || ''
          return '其他'
        }
        return `${item.category} / ${item.reason}`
      })
    } else {
      selectedChangeReasons.value = (releasePackage.changeReason || '').split('；').filter(Boolean).map(reason => {
        if (reason.startsWith('其他 / ')) {
          otherChangeReason.value = reason.slice(5).trim()
          return '其他'
        }
        return reason
      })
    }
  }
  wholeSetMultiplier.value = isLongLeadScope(releasePackage.scope) ? 1 : releasePackage.wholeSetMultiplier ?? 1
  selectedLongLeadKeys.value = []
  longLeadRequestedQuantities.value = {}
  if (isLongLeadScope(releasePackage.scope)) {
    const selectedByKey = new Map<string, number>()
    const snapshot = releasePackage.scope === 'NonStandardLongLead' ? releasePackage.nonStandardBomSnapshot
      : releasePackage.scope === 'ElectricalLongLead' ? releasePackage.electricalBomSnapshot : releasePackage.standardBomSnapshot
    snapshot.forEach((item, index) => {
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
  if (!isLongLeadScope(releasePackage.scope) && releasePackage.selectedBomItemIds.length > 0) {
    const selectedIds = new Set(releasePackage.selectedBomItemIds)
    if (isSupplementScope(releasePackage.scope)) {
      // 已发布物料的修改/删除不参与勾选，此处只回填可顺延的新增项。
      selectedSupplementKeys.value = supplementRows.value
        .filter(row => supplementRowSelectable(row) && Boolean(row.item.id) && selectedIds.has(row.item.id!))
        .map(row => supplementRowKey(row))
    } else {
      selectedFormalKeys.value = formalReleaseRows.value
        .filter(row => isFormalRowFullyPublished(row)
          || row.sourceItems.length > 0 && row.sourceItems.every(item => selectedIds.has(item.id!)))
        .map(row => row.key)
    }
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

function submitDecision(decision: 'Approved' | 'Rejected') {
  if (!currentTask.value) return
  const value = comment.value.trim()
  if (decision === 'Rejected' && !value) {
    approvalCommentError.value = '请填写驳回原因。'
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
    <p v-if="error" class="pdm-inline-error" role="alert">{{ error }}</p>
    <form v-if="canManage && (!releasePackage || editingDraft)" class="pdm-form-grid pdm-release-create-form" @submit.prevent="create">
      <section class="pdm-release-create-header" :class="{ 'has-drawing-delivery': drawingScope }" aria-label="发布参数">
        <div class="pdm-release-type-row">
          <label>发布类型
            <select v-model="scope" :disabled="editingDraft" aria-label="发布类型">
              <option v-for="item in visibleReleaseTypes" :key="item.value" :value="item.value">{{ item.label }}</option>
            </select>
          </label>
          <label v-if="!isLongLeadRelease" title="仅影响本发布包的输出数量，与项目及子项目数量无关">整套倍率
            <input v-model.number="wholeSetMultiplier" type="number" min="1" max="1000" step="1" aria-label="整套倍率">
          </label>
          <label v-if="drawingScope">整包紧急程度
            <select v-model="drawingPriority" aria-label="整包紧急程度"><option value="Normal">普通</option><option value="Priority">优先</option><option value="Urgent">紧急</option></select>
          </label>
          <label v-if="drawingScope">整包需求日期
            <input v-model="drawingRequiredOn" type="date" required aria-label="整包需求日期">
          </label>
          <div class="pdm-release-draft-actions">
            <button v-if="editingDraft" type="button" class="pdm-secondary-action" :disabled="pending" @click="editingDraft = false">取消编辑</button>
            <button type="submit" class="pdm-primary-action" :disabled="createDisabled">{{ editingDraft ? '保存草稿' : '创建草稿' }}</button>
          </div>
          <label v-if="isSupplement">变更单号<input value="创建草稿后自动生成" readonly aria-label="变更单号由系统自动生成"></label>
        </div>
        <div class="pdm-release-parameter-slot">
          <p v-if="isLongLeadRelease" class="pdm-inline-info">长交期BOM发布允许在尚无引用树时使用，仅冻结本次BOM清单，不发布图纸或制造结构；正式发布将自动扣除已经发布的数量。</p>
          <p v-if="drawingReviewBlockMessage" class="pdm-inline-warning" role="status">{{ drawingReviewBlockMessage }}</p>
          <label v-if="!isSupplement" class="pdm-release-reason">备注<textarea v-model.trim="releaseNote" rows="3" maxlength="500" placeholder="可填写本次发布备注（选填）"></textarea></label>
          <fieldset v-else class="release-change-reason-picker">
            <legend>变更原因（可跨分类多选，必须选择具体原因，已选 {{ selectedChangeReasons.length }} 项）</legend>
            <div class="release-change-reason-groups">
              <section v-for="group in changeReasonGroups" :key="group.category" class="release-change-reason-group">
                <strong>{{ group.category }}</strong>
                <div>
                  <label v-for="reason in group.reasons" :key="reason.value" :class="{ 'is-disabled': reason.value === '正式补充' && !formalSupplementAvailability.allowed }">
                    <input v-model="selectedChangeReasons" type="checkbox" :value="reason.value" :disabled="reason.value === '正式补充' && !formalSupplementAvailability.allowed" :aria-label="`变更原因 ${reason.value}`">
                    <span>{{ reason.label }}</span>
                  </label>
                  <input v-if="group.category === '其他' && selectedChangeReasons.includes('其他')" v-model.trim="otherChangeReason" class="release-other-reason-input" maxlength="200" placeholder="请填写具体原因（必填）" aria-label="其他具体原因">
                </div>
                <small v-if="group.category === '正式补充'" :class="{ 'is-unavailable': !formalSupplementAvailability.allowed }">{{ formalSupplementAvailability.status }}</small>
              </section>
            </div>
            <p v-if="selectedChangeReasons.length" class="release-change-reason-summary"><strong>已选：</strong>{{ changeReasonSummary }}</p>
          </fieldset>
        </div>
      </section>
      <section v-if="drawingScope && selectedDrawingCandidates.length" class="pdm-drawing-delivery-overrides" aria-label="逐张图发图信息">
        <strong>逐张图调整（不填则沿用整包设置）</strong>
        <div v-for="candidate in selectedDrawingCandidates" :key="candidate.drawingDocumentId!" class="pdm-drawing-delivery-row">
          <span>{{ candidate.drawingNumber }} · {{ candidate.name }}</span>
          <select :value="drawingOverrides[candidate.drawingDocumentId!]?.priority ?? drawingPriority" :aria-label="`${candidate.drawingNumber}紧急程度`" @change="setDrawingOverride(candidate.drawingDocumentId!, ($event.target as HTMLSelectElement).value as DrawingPriority, drawingOverrides[candidate.drawingDocumentId!]?.requiredOn ?? drawingRequiredOn)">
            <option value="Normal">普通</option><option value="Priority">优先</option><option value="Urgent">紧急</option>
          </select>
          <input type="date" :value="drawingOverrides[candidate.drawingDocumentId!]?.requiredOn ?? drawingRequiredOn" :aria-label="`${candidate.drawingNumber}需求日期`" @change="setDrawingOverride(candidate.drawingDocumentId!, drawingOverrides[candidate.drawingDocumentId!]?.priority ?? drawingPriority, ($event.target as HTMLInputElement).value)">
        </div>
      </section>
      <fieldset class="release-detail-picker">
        <legend>{{ releaseDetailLegend }}</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table" :class="{ 'is-supplement': isSupplement }">
            <colgroup><col><col><col><col><col><col><col><col><col><col><col v-if="isSupplement"></colgroup>
            <thead><tr><th>标记</th><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>可发布数量</th><th>发布总数量</th><th>备注</th><th>发布状态</th><th v-if="isSupplement">变更明细（原值 → 新值）</th></tr></thead>
            <tbody>
              <template v-if="isLongLeadRelease">
                <tr v-for="(row, index) in pagedLongLeadItems" :key="row.key">
                  <td class="is-release-centered"><input :checked="selectedLongLeadKeys.includes(row.key)" type="checkbox" :aria-label="`选择长交期物料 ${row.item.drawingNumber}`" :disabled="scope === 'NonStandardLongLead' && !releaseRowDrawingReviewReady(row)" :title="scope === 'NonStandardLongLead' && !releaseRowDrawingReviewReady(row) ? releaseRowDrawingReviewStatus(row) : undefined" @change="toggleLongLeadSelection(row.key, ($event.target as HTMLInputElement).checked, Number(row.item.quantity))"></td>
                  <td class="is-release-centered">{{ (longLeadPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td class="is-release-centered">{{ row.item.brand || '—' }}</td><td class="is-release-centered">{{ row.item.quantity }}</td><td class="is-release-centered"><input v-if="selectedLongLeadKeys.includes(row.key)" v-model.number="longLeadRequestedQuantities[row.key]" class="long-lead-quantity-input" type="number" min="0.000001" :max="row.item.quantity" step="any" :aria-label="`本次发布数量 ${row.item.drawingNumber}`"><span v-else>—</span></td><td :title="row.item.remark || undefined">{{ row.item.remark || '—' }}</td><td class="is-release-centered"><span class="release-status-tag" :class="{ 'is-blocked': scope === 'NonStandardLongLead' && !releaseRowDrawingReviewReady(row) }">{{ scope === 'NonStandardLongLead' ? releaseRowDrawingReviewStatus(row) : '剩余可发布' }}</span></td>
                </tr>
              </template>
              <template v-else-if="isFormalRelease">
                <tr v-for="(row, index) in pagedFormalReleaseRows" :key="row.key" :class="{ 'is-release-unselected': !selectedFormalKeys.includes(row.key) }">
                  <td class="is-release-centered"><input :checked="selectedFormalKeys.includes(row.key)" type="checkbox" :disabled="isFormalRowFullyPublished(row)" :title="isFormalRowFullyPublished(row) ? formalRowPublishTitle(row) : undefined" :aria-label="`本次发布物料 ${row.item.drawingNumber}`" @change="toggleFormalSelection(row.key, ($event.target as HTMLInputElement).checked)"></td><td class="is-release-centered">{{ (formalPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td class="is-release-centered">{{ row.item.brand || '—' }}</td><td class="is-release-centered">{{ row.remainingQuantity }}</td><td class="is-release-centered" :class="{ 'is-release-multiplied': Number(wholeSetMultiplier) !== 1 && selectedFormalKeys.includes(row.key) }">{{ selectedFormalKeys.includes(row.key) ? formalRowPublishQuantity(row) : '—' }}</td><td :title="row.item.remark || undefined">{{ row.item.remark || '—' }}</td>
                  <td class="is-release-centered"><span v-if="!selectedFormalKeys.includes(row.key)" class="release-status-tag">本次不发布</span><span v-else-if="scope === 'NonStandardWithDrawing'" class="release-status-tag" :class="{ 'is-blocked': !releaseRowDrawingReviewReady(row) }">{{ releaseRowDrawingReviewStatus(row) }}</span><span v-else-if="row.longLeadPublishedQuantity > 0" class="long-lead-tag" :title="formalRowPublishTitle(row)">已发布 {{ row.longLeadPublishedQuantity }}/{{ row.item.quantity }}</span><span v-else class="release-status-tag">本次发布</span></td>
                </tr>
              </template>
              <template v-else>
                <tr v-for="(row, index) in pagedSupplementRows" :key="`${row.change}-${row.item.id}-${index}`" :class="{ 'is-release-unselected': supplementRowSelectable(row) && !selectedSupplementKeys.includes(supplementRowKey(row)) }">
                  <td class="is-release-centered"><input v-if="supplementRowSelectable(row)" :checked="selectedSupplementKeys.includes(supplementRowKey(row))" type="checkbox" :aria-label="`本次纳入 ${row.item.drawingNumber || row.item.name}`" :title="`取消勾选后该项顺延到下一次增补/变更`" @change="toggleSupplementSelection(row, ($event.target as HTMLInputElement).checked)"><input v-else type="checkbox" checked disabled :title="row.change === '修改' ? '已发布物料的修改随本次变更单一起发布，不能单独顺延' : '删除项随本次变更单一起生效'"><span :class="`release-change-tag is-${row.change}`">{{ row.change }}</span></td><td class="is-release-centered">{{ (supplementPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td class="is-release-centered">{{ row.item.brand || '—' }}</td><td class="is-release-centered">{{ row.item.quantity }}</td><td class="is-release-centered" :class="{ 'is-release-multiplied': row.change !== '删除' && Number(wholeSetMultiplier) !== 1 }">{{ row.change === '删除' ? '—' : Number(row.item.quantity) * Number(wholeSetMultiplier) }}</td><td :title="row.item.remark || undefined">{{ row.item.remark || '—' }}</td><td class="is-release-centered"><span v-if="supplementRowSelectable(row) && !selectedSupplementKeys.includes(supplementRowKey(row))" class="release-status-tag">本次不发布</span><span v-else class="release-status-tag" :class="{ 'is-blocked': scope === 'NonStandardSupplement' && row.change !== '删除' && !itemDrawingReviewReady(row.item) }">{{ scope === 'NonStandardSupplement' && row.change !== '删除' ? drawingReviewStatusForItem(row.item) : '待纳入变更' }}</span></td>
                  <td class="release-change-details" :title="row.details.join('；')"><div v-for="detail in row.details" :key="detail">{{ detail }}</div></td>
                </tr>
              </template>
              <tr v-if="!releaseDetailRowCount"><td :colspan="isSupplement ? 11 : 10" class="pdm-empty-info">{{ releaseDetailEmptyText }}</td></tr>
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
          <p v-if="requiresDrawingFiles">非标件BOM已固化为XLSX；图纸发布预览由系统生成，审批通过后与BOM原子发布。</p>
          <p v-else>系统已按本次发布范围生成受控BOM XLSX，无需上传机械图纸。</p>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-primary-action" :disabled="pending" @click="emit('submit', releasePackage.id)">{{ releasePackage.state === '已驳回' ? '重新提交审批' : '提交审批' }}</button>
          </div>
          <progress v-if="pending && progress > 0" :value="progress" max="100">{{ progress }}%</progress>
        </div>

        <div v-if="canHandleCurrentTask && currentTask" class="pdm-decision-box">
          <label>审批意见<textarea v-model="comment" rows="3" maxlength="1000" placeholder="通过可不填；驳回必须填写原因" @input="approvalCommentError = ''" /><small v-if="approvalCommentError" class="pdm-inline-error">{{ approvalCommentError }}</small></label>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="submitDecision('Rejected')">驳回</button>
            <button type="button" class="pdm-secondary-action" :disabled="pending" @click="openTransfer">转交</button>
            <button type="button" class="pdm-primary-action" :disabled="pending" @click="submitDecision('Approved')">通过</button>
          </div>
        </div>
        <div v-else-if="canManage && ['审批中', '工艺审核', '待批准', '已驳回'].includes(releasePackage.state)" class="pdm-decision-box pdm-withdraw-decision">
          <small>{{ releasePackage.state === '已驳回' ? '驳回后撤回：发布包恢复为草稿，可编辑草稿重新绑定当前BOM，或删除后重新创建。' : releasePackage.locksDocuments ? '撤回后图档与非标件BOM恢复为工作中。' : '撤回后当前BOM版本恢复为草稿。' }}</small>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('withdraw', releasePackage.id)">{{ releasePackage.state === '已驳回' ? '撤回为草稿' : '撤回审批' }}</button>
          </div>
        </div>
        <div v-if="canEmergencyDecide && currentTask && !canHandleCurrentTask" class="pdm-decision-box emergency-decision">
          <label>紧急代批原因<textarea v-model.trim="emergencyReason" rows="3" maxlength="1000" required placeholder="说明必须立即处理的业务原因；该内容会进入审计记录" /></label>
          <div class="pdm-manager-actions">
            <button type="button" class="pdm-secondary-action is-danger" :disabled="pending || !emergencyReason" @click="emit('emergencyDecide', currentTask.id, 'Rejected', emergencyReason)">紧急代驳回</button>
            <button type="button" class="pdm-primary-action" :disabled="pending || !emergencyReason" @click="emit('emergencyDecide', currentTask.id, 'Approved', emergencyReason)">紧急代批当前节点</button>
          </div>
        </div>
        <div v-if="canManage && isLongLeadScope(releasePackage.scope) && releasePackage.state === '已发布'" class="pdm-decision-box pdm-long-lead-u9-retry">
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
        <div><small>制造基线</small><strong>{{ releasePackage.createsManufacturingBaseline ? '发布后生成新基线' : isLongLeadScope(releasePackage.scope) ? '不更新（长交期BOM输出）' : '三条正式流齐备后生成' }}</strong></div>
        <div><small>发布目录</small><strong>{{ releasePackage.publishedPath || '审批通过后自动投放' }}</strong></div>
      </div>
      <p v-if="releasePackage.changeReason" class="pdm-release-change-reason"><strong>{{ isSupplementScope(releasePackage.scope) ? '变更原因' : '备注' }}：</strong>{{ releasePackage.changeReason }}</p>

      <section class="pdm-release-frozen-snapshot" aria-label="审批固化快照">
        <header>
          <div><strong>{{ showFrozenChanges ? '本次变更明细' : '审批固化快照' }}</strong><small>显示发布后的最终数量（工作区数量 × 整套倍率 {{ releasePackage.wholeSetMultiplier ?? 1 }}）；批注独立保存，不修改BOM。</small><small v-if="showFrozenChanges">仅列出新增、修改、删除的实例；变更字段按工作区原值对比，删除项发布后数量为0。完整BOM仍保留。</small></div>
          <div class="pdm-frozen-view-actions">
            <span>{{ frozenDisplayRows.length }} 项<span v-if="!showFrozenChanges && frozenViewMode === 'Summary' && frozenItems.length !== frozenDisplayRows.length"> · {{ frozenItems.length }} 个实例</span></span>
            <button v-if="isFrozenSupplement" type="button" class="pdm-secondary-action" @click="frozenShowFullBom = !frozenShowFullBom">{{ showFrozenChanges ? '查看完整BOM' : '仅看本次变更' }}</button>
            <div v-if="!showFrozenChanges" class="pdm-view-switch" role="group" aria-label="审批快照显示方式">
              <button type="button" :class="{ 'is-active': frozenViewMode === 'Summary' }" :aria-pressed="frozenViewMode === 'Summary'" @click="frozenViewMode = 'Summary'">按汇总</button>
              <button type="button" :class="{ 'is-active': frozenViewMode === 'Structure' }" :aria-pressed="frozenViewMode === 'Structure'" @click="frozenViewMode = 'Structure'">按结构</button>
            </div>
          </div>
        </header>
        <div class="pdm-release-diff-summary"><span class="is-added">新增 {{ frozenDiff.added }}</span><span class="is-modified">修改 {{ frozenDiff.modified }}</span><span class="is-removed">减少 {{ frozenDiff.removed }}</span><small>对比上一正式发布版</small></div>
        <p v-if="itemCommentsError && !itemCommentOpen" class="pdm-inline-error pdm-item-comments-load-error">批注加载失败：{{ itemCommentsError }}</p>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table pdm-release-frozen-table" :class="{ 'is-formal-issue-view': showFormalIssueQuantities, 'is-change-view': showFrozenChanges }">
            <colgroup><col><col><col><col><col><col><col><template v-if="showFormalIssueQuantities"><col><col></template><col><col><template v-if="showFrozenChanges"><col><col></template></colgroup>
            <thead><tr><th>序号</th><th>物料编码</th><th>{{ frozenViewMode === 'Structure' ? '物料名称 / 结构位置' : '物料名称' }}</th><th>型号</th><th>版本</th><th>品牌</th><th>备注</th><th>{{ showFormalIssueQuantities ? 'BOM总量' : '数量' }}</th><template v-if="showFormalIssueQuantities"><th>前期已发布</th><th>本次新增下发</th></template><th>批注</th><template v-if="showFrozenChanges"><th>变更</th><th>变更字段（原值 → 新值）</th></template></tr></thead>
            <tbody>
              <tr v-for="(row, index) in pagedFrozenRows" :key="row.key">
                <td class="is-release-centered">{{ (frozenPage - 1) * releasePageSize + index + 1 }}</td>
                <td class="is-release-centered">{{ row.item.drawingNumber || '—' }}</td>
                <td class="pdm-frozen-item-name" :style="frozenViewMode === 'Structure' ? { paddingLeft: structureIndent(row.item) } : undefined">
                  <span v-if="frozenViewMode === 'Structure'" class="pdm-structure-marker">↳</span>{{ row.item.name || '—' }}
                  <small v-if="frozenViewMode === 'Structure' && structureLocation(row.item)">{{ structureLocation(row.item) }}</small>
                </td>
                <td>{{ row.item.specification || '—' }}</td>
                <td class="is-release-centered">{{ row.item.revision || '—' }}</td>
                <td class="is-release-centered">{{ row.item.brand || '—' }}</td>
                <td>{{ row.item.remark || '—' }}</td>
                <td class="is-release-centered">{{ row.item.quantity }}</td>
                <template v-if="showFormalIssueQuantities"><td class="is-release-centered">{{ priorQuantityForRow(row) }}</td><td class="is-release-centered is-new-issue-quantity">{{ newIssueQuantityForRow(row) }}</td></template>
                <td class="is-release-centered"><span v-if="row.change === '删除'">—</span><button v-else type="button" class="pdm-item-comment-action" :class="{ 'has-comments': commentsForRow(row).length > 0 }" :aria-label="`查看或添加物料批注 ${row.item.drawingNumber || row.item.name}`" @click="openItemComment(row)">批注<span v-if="commentsForRow(row).length">（{{ commentsForRow(row).length }}）</span></button></td>
                <template v-if="showFrozenChanges"><td class="is-release-centered"><span class="release-change-tag" :class="`is-${row.change}`">{{ row.change }}</span></td><td class="pdm-frozen-change-details"><div v-for="detail in row.details" :key="detail">{{ detail }}</div></td></template>
              </tr>
              <tr v-if="!frozenDisplayRows.length"><td :colspan="showFormalIssueQuantities || showFrozenChanges ? 11 : 9" class="pdm-empty-info">{{ showFrozenChanges ? '与上一发布版相比，本次没有增补或变更内容。' : '该发布包没有BOM快照数据。' }}</td></tr>
            </tbody>
          </table>
        </div>
        <nav class="release-list-pagination" aria-label="审批固化快照分页">
          <span>共 {{ frozenDisplayRows.length }} 条 · 50 条/页</span>
          <button type="button" :disabled="frozenPage <= 1" aria-label="固化快照上一页" @click="frozenPage--">‹</button>
          <strong>{{ frozenPage }} / {{ frozenPageCount }}</strong>
          <button type="button" :disabled="frozenPage >= frozenPageCount" aria-label="固化快照下一页" @click="frozenPage++">›</button>
        </nav>
        <p v-if="isLongLeadScope(releasePackage.scope)" class="pdm-release-integration-note">发布后输出长交期BOM，并写入U9C待同步集成事件；不更新制造基线，也不发布图纸。</p>
        <p v-if="['StandardFormal', 'NonStandardWithDrawing', 'ElectricalFormal'].includes(releasePackage.scope)" class="pdm-release-integration-note">完整BOM保留总量；按稳定物料身份和单位扣除前期已发布量，仅下发剩余需求。按结构查看完整BOM，按汇总查看新增下发数量。</p>
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
            <button v-for="candidate in filteredTransferCandidates" :key="candidate.username" type="button" :class="{ 'is-selected': transferTarget === candidate.username }" role="option" :aria-selected="transferTarget === candidate.username" @click="transferTarget = candidate.username"><strong>{{ candidate.displayName }}</strong></button>
            <p v-if="transferCandidatesLoading">正在加载可转交人员…</p>
            <p v-else-if="transferError" class="pdm-inline-error">{{ transferError }}</p>
            <p v-else-if="!filteredTransferCandidates.length">没有符合条件的可转交人员。</p>
          </div>
          <label>转交说明<textarea v-model.trim="transferComment" rows="2" maxlength="500" placeholder="选填"></textarea></label>
          <footer><button type="button" class="pdm-secondary-action" @click="transferOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending || !transferTarget" @click="confirmTransfer">确认转交</button></footer>
        </section>
      </div>
      <p v-if="releasePackage.publishError" class="pdm-inline-error">发布失败：{{ releasePackage.publishError }}</p>
      <!-- 转图与发布解耦：发布已完成时转图单独显示状态，失败由后台自动重试，也可手动重试。 -->
      <p v-if="releasePackage.previewState && releasePackage.previewState !== 'None'" class="pdm-release-preview-state">
        <span>图纸转换（STEP/PDF）：{{ releasePreviewStateLabel(releasePackage) }}</span>
        <span v-if="releasePackage.previewError && releasePackage.previewState !== 'Running'" class="is-error">{{ releasePackage.previewError }}</span>
        <button v-if="releasePackage.previewState !== 'Succeeded'" type="button" class="pdm-secondary-action" :disabled="pending" @click="emit('retryPreview', releasePackage.id)">重试转图</button>
      </p>
    </template>
  </section>
</template>

<style scoped>
.pdm-inline-info{margin:0 0 8px;padding:7px 10px;border:1px solid #99f6e4;border-radius:6px;background:#f0fdfa;color:var(--pdm-green);white-space:normal}
.pdm-release-preview-state{margin:0 0 8px;display:flex;flex-wrap:wrap;align-items:center;gap:8px;padding:6px 10px;border:1px solid var(--pdm-border);border-radius:6px;background:var(--pdm-surface-muted);color:var(--pdm-text-soft);font-size:12px;white-space:normal}
.pdm-release-preview-state .is-error{color:var(--pdm-danger)}
.release-detail-picker .pdm-edit-table td.release-change-details{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.release-detail-picker .pdm-edit-table td.release-change-details>div{display:inline}
.release-detail-picker .pdm-edit-table td.release-change-details>div+div::before{content:'；'}
.release-detail-picker table.is-supplement col:nth-child(1),.release-detail-picker table.is-supplement col:nth-child(2){width:4%}
.release-detail-picker table.is-supplement col:nth-child(3){width:11%}
.release-detail-picker table.is-supplement col:nth-child(4){width:9%}
.release-detail-picker table.is-supplement col:nth-child(5){width:10%}
.release-detail-picker table.is-supplement col:nth-child(6),.release-detail-picker table.is-supplement col:nth-child(7){width:6%}
.release-detail-picker table.is-supplement col:nth-child(8),.release-detail-picker table.is-supplement col:nth-child(10){width:7%}
.release-detail-picker table.is-supplement col:nth-child(9){width:8%}
.release-detail-picker table.is-supplement col:nth-child(11){width:18%}
.release-detail-picker table.is-supplement th{white-space:normal;overflow-wrap:anywhere}
.pdm-release-top-workflow{display:grid;gap:6px;margin-bottom:8px}.pdm-release-top-workflow .pdm-approval-chain,.pdm-release-summary{grid-template-columns:repeat(4,minmax(0,1fr));gap:6px}.pdm-release-top-workflow .pdm-approval-chain{margin:0}.pdm-release-top-workflow .pdm-approval-chain article,.pdm-release-summary>div{min-width:0;min-height:46px;box-sizing:border-box;align-content:center;gap:2px;padding:6px 8px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface)}.pdm-release-top-workflow .pdm-approval-chain article{align-items:flex-start;gap:6px}.pdm-release-top-workflow .pdm-approval-chain article div{overflow:hidden}.pdm-release-top-workflow .pdm-approval-chain strong,.pdm-release-top-workflow .pdm-approval-chain small,.pdm-release-top-workflow .pdm-approval-chain em{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-release-top-workflow .pdm-release-draft-management{margin:0}.pdm-release-top-workflow .pdm-release-preparation{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:8px;margin:0;padding:6px 8px}.pdm-release-top-workflow .pdm-release-preparation h3,.pdm-release-top-workflow .pdm-release-preparation p{margin:0}.pdm-release-top-workflow .pdm-release-preparation .pdm-manager-actions{flex-wrap:nowrap}.pdm-release-top-workflow .pdm-release-preparation progress{grid-column:1/-1;margin-top:0}.pdm-release-top-workflow .pdm-decision-box{grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:8px;margin:0;padding:6px 8px}.pdm-release-top-workflow .pdm-decision-box textarea{min-height:30px;height:30px;box-sizing:border-box;resize:vertical}.pdm-release-top-workflow .pdm-withdraw-decision{align-items:center}.pdm-release-summary{grid-auto-rows:minmax(46px,auto);margin-bottom:8px}.pdm-release-summary small{font-size:10px}.pdm-release-summary strong{font-size:11px}@media(max-width:900px){.pdm-release-top-workflow .pdm-approval-chain,.pdm-release-summary{grid-template-columns:repeat(2,minmax(0,1fr))}.pdm-release-top-workflow .pdm-release-preparation,.pdm-release-top-workflow .pdm-decision-box{grid-template-columns:1fr}.pdm-release-top-workflow .pdm-release-preparation .pdm-manager-actions,.pdm-release-top-workflow .pdm-decision-box .pdm-manager-actions{justify-content:flex-end}}@media(max-width:560px){.pdm-release-top-workflow .pdm-approval-chain,.pdm-release-summary{grid-template-columns:1fr}}
.release-center select{height:34px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff;padding:0 9px;color:var(--pdm-text)}.pdm-release-create-header{grid-column:1/-1;display:grid;grid-template-rows:52px 82px;gap:8px;min-height:162px;padding:10px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface)}.pdm-release-type-row{display:grid;grid-template-columns:minmax(220px,1fr) minmax(120px,180px) auto minmax(180px,1fr);gap:5px;align-items:end}.pdm-release-type-row label{min-width:0}.pdm-release-type-row select,.pdm-release-type-row input{width:100%;height:34px;box-sizing:border-box}.pdm-release-draft-actions{display:flex;gap:5px}.pdm-release-draft-actions button{height:34px;padding:0 10px;white-space:nowrap}.pdm-release-parameter-slot{min-height:82px;overflow:auto}.pdm-release-parameter-slot>.pdm-release-reason{height:100%;box-sizing:border-box}.pdm-release-parameter-slot>.pdm-release-reason textarea{height:60px;box-sizing:border-box;resize:none}.release-detail-picker,.release-change-reason-picker{grid-column:1/-1;margin:0;padding:10px;border:1px solid var(--pdm-border);border-radius:7px}.release-detail-picker legend,.release-change-reason-picker legend{padding:0 5px;font-weight:600}.release-detail-picker .pdm-table-scroll{border:1px solid var(--pdm-border);border-radius:5px}.release-detail-picker table{width:100%;min-width:0;table-layout:fixed}.release-detail-picker col:nth-child(1){width:6%}.release-detail-picker col:nth-child(2){width:5%}.release-detail-picker col:nth-child(3){width:12%}.release-detail-picker col:nth-child(4){width:13%}.release-detail-picker col:nth-child(5){width:15%}.release-detail-picker col:nth-child(6){width:8%}.release-detail-picker col:nth-child(7){width:9%}.release-detail-picker col:nth-child(8){width:10%}.release-detail-picker col:nth-child(9){width:12%}.release-detail-picker col:nth-child(10){width:10%}.release-detail-picker th,.release-detail-picker td{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;vertical-align:middle}.release-detail-picker td{height:32px}.release-detail-picker th,.release-detail-picker td.is-release-centered{text-align:center}.release-detail-picker th:nth-child(8),.release-detail-picker td:nth-child(8){background:#fffbeb}.release-detail-picker td.is-release-multiplied{font-weight:600}.long-lead-quantity-input{width:100%;min-width:0;height:26px;box-sizing:border-box;text-align:center}.pdm-release-draft-management{justify-content:flex-end;margin-bottom:8px}.release-change-reason-picker{display:flex;height:100%;box-sizing:border-box;flex-wrap:wrap;align-content:flex-start;gap:8px 18px}.release-change-reason-picker label{display:flex;align-items:center;gap:5px}.long-lead-tag,.release-change-tag,.release-inclusion-tag,.release-status-tag{display:inline-flex;align-items:center;min-height:20px;padding:0 6px;border-radius:10px}.long-lead-tag,.release-change-tag{background:#fff7ed;color:var(--pdm-orange)}.release-inclusion-tag{background:#eff6ff;color:var(--pdm-blue)}.release-status-tag{background:var(--pdm-surface-soft);color:var(--pdm-muted)}.release-change-tag.is-新增{background:#ecfdf5;color:var(--pdm-green)}.release-change-tag.is-修改{background:#fff7ed;color:var(--pdm-orange)}.release-change-tag.is-删除{background:#fef2f2;color:var(--pdm-danger)}.pdm-release-create-form .pdm-release-reason{grid-column:1/-1}.emergency-decision{border-color:#f59e0b;background:#fffbeb}.pdm-release-frozen-snapshot{margin:12px 0;border:1px solid var(--pdm-border);border-radius:7px;overflow:hidden}.pdm-release-frozen-snapshot>header{display:flex;justify-content:space-between;gap:12px;align-items:center;padding:9px 11px;background:var(--pdm-surface-soft)}.pdm-release-frozen-snapshot>header>div:first-child{display:grid;gap:2px;min-width:0}.pdm-release-frozen-snapshot>header small{color:var(--pdm-muted)}.pdm-frozen-view-actions{display:flex;align-items:center;justify-content:flex-end;gap:10px;white-space:nowrap}.pdm-view-switch{display:inline-flex;padding:2px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff}.pdm-view-switch button{height:24px;padding:0 9px;border:0;border-radius:4px;background:transparent;color:var(--pdm-muted)}.pdm-view-switch button.is-active{background:#0f9d90;color:#fff}.pdm-release-diff-summary{display:flex;align-items:center;gap:10px;padding:7px 11px;border-top:1px solid var(--pdm-border);border-bottom:1px solid var(--pdm-border)}.pdm-release-diff-summary small{margin-left:auto;color:var(--pdm-muted)}.pdm-release-diff-summary .is-added{color:var(--pdm-green)}.pdm-release-diff-summary .is-modified{color:var(--pdm-orange)}.pdm-release-diff-summary .is-removed{color:var(--pdm-danger)}.pdm-release-frozen-snapshot .pdm-table-scroll{max-height:230px}.pdm-item-comments-load-error{margin:0;padding:7px 11px}.pdm-item-comment-action{border:0;background:transparent;color:var(--pdm-green);white-space:nowrap}.pdm-frozen-item-name{padding-left:7px}.pdm-frozen-item-name small{display:block;margin-top:2px;color:var(--pdm-muted);font-size:10px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-structure-marker{margin-right:4px;color:var(--pdm-green)}.pdm-release-integration-note{margin:0;padding:8px 11px;color:var(--pdm-green);background:#f0fdfa;border-top:1px solid #99f6e4}@media(max-width:900px){.pdm-release-type-row{grid-template-columns:repeat(2,minmax(0,1fr))}.pdm-release-frozen-snapshot>header{align-items:flex-start;flex-direction:column}.pdm-release-frozen-view-actions{width:100%;justify-content:space-between}}
.release-detail-picker col:nth-child(8){width:11%}.release-detail-picker col:nth-child(9){width:11%}.release-detail-picker th:nth-child(8){white-space:normal;text-overflow:clip}
.release-detail-picker{display:flex;width:100%;min-width:0;min-height:0;box-sizing:border-box;flex-direction:column}.release-detail-picker .pdm-table-scroll{width:100%;height:clamp(220px,calc(100dvh - 440px),630px);max-width:100%;max-height:none;box-sizing:border-box;overflow-x:hidden;overflow-y:auto}.release-detail-pagination,.release-list-pagination{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding-top:8px;color:var(--pdm-muted)}.release-detail-pagination span,.release-list-pagination span{margin-right:auto}.release-detail-pagination button,.release-list-pagination button{width:28px;height:28px;border:1px solid var(--pdm-border);border-radius:6px;background:var(--pdm-surface);color:var(--pdm-text);cursor:pointer}.release-detail-pagination button:disabled,.release-list-pagination button:disabled{cursor:not-allowed;opacity:.45}.release-detail-pagination strong,.release-list-pagination strong{min-width:54px;text-align:center;color:var(--pdm-text)}
.pdm-release-frozen-snapshot{display:flex;min-width:0;min-height:0;flex-direction:column}.pdm-release-frozen-snapshot .pdm-table-scroll{width:100%;height:clamp(220px,calc(100dvh - 470px),620px);max-width:100%;max-height:none;box-sizing:border-box;overflow-x:hidden;overflow-y:auto}.pdm-release-frozen-snapshot .release-list-pagination{padding:8px 11px}
.pdm-release-frozen-table{width:100%;min-width:0;table-layout:fixed}.pdm-release-frozen-table col:nth-child(1){width:5%}.pdm-release-frozen-table col:nth-child(2){width:12%}.pdm-release-frozen-table col:nth-child(3){width:21%}.pdm-release-frozen-table col:nth-child(4){width:15%}.pdm-release-frozen-table col:nth-child(5){width:10%}.pdm-release-frozen-table col:nth-child(6){width:13%}.pdm-release-frozen-table col:nth-child(7),.pdm-release-frozen-table col:nth-child(8){width:7%}.pdm-release-frozen-table col:nth-child(9){width:10%}.pdm-release-frozen-table th,.pdm-release-frozen-table td{white-space:normal;overflow-wrap:anywhere;vertical-align:middle}.pdm-release-frozen-table th,.pdm-release-frozen-table td.is-release-centered{text-align:center}
.pdm-item-comment-backdrop{z-index:3200}.pdm-item-comment-dialog{width:min(560px,calc(100vw - 32px));max-height:min(680px,calc(100vh - 32px));display:grid;grid-template-rows:auto minmax(120px,1fr) auto;gap:12px;padding:16px;border-radius:10px;background:#fff;box-shadow:0 18px 60px rgb(15 23 42 / 24%)}.pdm-item-comment-dialog>header,.pdm-item-comment-dialog>footer,.pdm-item-comment-dialog form>footer{display:flex;align-items:center;justify-content:space-between;gap:8px}.pdm-item-comment-dialog>header>div{display:grid;gap:3px}.pdm-item-comment-dialog>header small{color:var(--pdm-muted)}.pdm-item-comment-history{min-height:120px;overflow:auto;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.pdm-item-comment-history article{padding:10px 12px;border-bottom:1px solid var(--pdm-border);background:#fff}.pdm-item-comment-history article:last-child{border-bottom:0}.pdm-item-comment-history p{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}.pdm-item-comment-history small{display:block;margin-top:5px;color:var(--pdm-muted)}.pdm-item-comment-history>p{margin:0;padding:14px;color:var(--pdm-muted)}.pdm-item-comment-dialog form{display:grid;gap:8px}.pdm-item-comment-dialog form label{display:grid;gap:5px}.pdm-item-comment-dialog textarea{width:100%;box-sizing:border-box;resize:vertical}.pdm-item-comment-dialog form>footer{justify-content:flex-end}.pdm-item-comment-dialog>footer{justify-content:flex-end}.pdm-item-comment-dialog>footer small{margin-right:auto;color:var(--pdm-muted)}
.pdm-approval-transfer-backdrop{z-index:3100}.pdm-approval-transfer-dialog{width:min(520px,calc(100vw - 32px));max-height:min(620px,calc(100vh - 32px));display:grid;grid-template-rows:auto auto minmax(100px,1fr) auto auto;gap:12px;padding:16px;border-radius:10px;background:#fff;box-shadow:0 18px 60px rgb(15 23 42 / 24%)}.pdm-approval-transfer-dialog>header,.pdm-approval-transfer-dialog>footer{display:flex;align-items:center;justify-content:space-between;gap:8px}.pdm-approval-transfer-dialog>footer{justify-content:flex-end}.pdm-approval-transfer-dialog>label{display:grid;gap:5px}.pdm-approval-transfer-dialog input,.pdm-approval-transfer-dialog textarea{width:100%;box-sizing:border-box}.pdm-approval-transfer-list{min-height:100px;overflow:auto;border:1px solid var(--pdm-border);border-radius:7px}.pdm-approval-transfer-list>button{width:100%;display:flex;align-items:center;justify-content:space-between;gap:12px;padding:9px 11px;border:0;border-bottom:1px solid var(--pdm-border);background:#fff;color:var(--pdm-text);text-align:left}.pdm-approval-transfer-list>button.is-selected{background:#ecfdf5;color:var(--pdm-green)}.pdm-approval-transfer-list>button small{color:var(--pdm-muted)}.pdm-approval-transfer-list>p{margin:0;padding:12px;color:var(--pdm-muted)}
.pdm-release-top-workflow .pdm-approval-chain article.is-current{border-color:var(--pdm-blue);background:var(--pdm-blue-soft)}
.pdm-release-top-workflow .pdm-approval-chain article.is-approved,.pdm-release-top-workflow .pdm-approval-chain article.is-done{border-color:var(--pdm-green);background:var(--pdm-green-soft);color:var(--pdm-green)}
.pdm-release-top-workflow .pdm-approval-chain article.is-rejected{border-color:var(--pdm-danger);background:#fff0ef;color:var(--pdm-danger)}
.pdm-release-top-workflow .pdm-approval-chain article.is-skipped{background:var(--pdm-surface-muted);color:var(--pdm-muted)}
.pdm-release-top-workflow .pdm-approval-chain small,.pdm-release-top-workflow .pdm-approval-chain em{overflow:visible;text-overflow:clip;white-space:normal;overflow-wrap:anywhere}
.pdm-release-top-workflow .pdm-decision-box label{display:grid;gap:4px}.pdm-release-top-workflow .pdm-decision-box .pdm-inline-error{margin:0;padding:5px 7px}
.pdm-release-create-header{grid-template-rows:52px minmax(168px,auto);min-height:240px}.pdm-release-parameter-slot{overflow:visible}.release-change-reason-picker{display:block;height:auto}.release-change-reason-groups{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:7px}.release-change-reason-group{display:grid;grid-template-columns:72px minmax(0,1fr);align-items:start;gap:5px 8px;padding:7px;border:1px solid var(--pdm-border);border-radius:6px;background:var(--pdm-surface-soft)}.release-change-reason-group>strong{padding-top:2px;color:var(--pdm-text)}.release-change-reason-group>div{display:flex;align-items:center;flex-wrap:wrap;gap:5px 12px}.release-change-reason-group label{display:flex;align-items:center;gap:4px;white-space:nowrap}.release-change-reason-group label.is-disabled{color:var(--pdm-muted)}.release-change-reason-group>small{grid-column:2;color:var(--pdm-green)}.release-change-reason-group>small.is-unavailable{color:var(--pdm-danger)}.release-other-reason-input{flex:1 1 190px;min-width:150px;height:28px;border:1px solid var(--pdm-border);border-radius:5px;padding:0 7px}.release-change-reason-summary{margin:7px 0 0;color:var(--pdm-muted);white-space:normal;overflow-wrap:anywhere}.release-change-reason-summary strong{color:var(--pdm-text)}@media(max-width:1000px){.release-change-reason-groups{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.release-change-reason-groups{grid-template-columns:1fr}}
.is-release-unselected{color:var(--pdm-muted);background:var(--pdm-surface-soft)}
.release-center .pdm-release-frozen-table th,.release-center .pdm-release-frozen-table td.is-release-centered{text-align:center}
.pdm-item-comment-action.has-comments{color:var(--pdm-danger)}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(1){width:4%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(2){width:12%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(3){width:17%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(4){width:14%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(5){width:7%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(6){width:10%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(7),.pdm-release-frozen-table.is-formal-issue-view col:nth-child(8),.pdm-release-frozen-table.is-formal-issue-view col:nth-child(9){width:8%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(10){width:5%}
.pdm-release-frozen-table.is-formal-issue-view col:nth-child(11){width:7%}
.pdm-release-frozen-table .is-new-issue-quantity{background:#fffbeb}
.pdm-release-frozen-table.is-change-view col:nth-child(1){width:4%}
.pdm-release-frozen-table.is-change-view col:nth-child(2){width:12%}
.pdm-release-frozen-table.is-change-view col:nth-child(3){width:11%}
.pdm-release-frozen-table.is-change-view col:nth-child(4){width:11%}
.pdm-release-frozen-table.is-change-view col:nth-child(5){width:7%}
.pdm-release-frozen-table.is-change-view col:nth-child(6){width:9%}
.pdm-release-frozen-table.is-change-view col:nth-child(7),.pdm-release-frozen-table.is-change-view col:nth-child(8){width:5%}
.pdm-release-frozen-table.is-change-view col:nth-child(9),.pdm-release-frozen-table.is-change-view col:nth-child(10){width:6%}
.pdm-release-frozen-table.is-change-view col:nth-child(11){width:24%}
.pdm-frozen-view-actions{flex-wrap:wrap}
.pdm-frozen-change-details>div+div{margin-top:3px}
.pdm-release-frozen-table.is-change-view .release-change-tag{padding:0 3px;white-space:nowrap}
.release-status-tag.is-blocked{background:#fff7ed;color:var(--pdm-orange);font-weight:600}
.pdm-release-parameter-slot>.pdm-inline-warning{margin:0 0 6px;padding:6px 8px;border-radius:5px;background:#fff7ed;color:var(--pdm-orange)}
.pdm-drawing-delivery-overrides{grid-column:1/-1;display:grid;gap:6px;padding:10px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}
.pdm-release-create-header.has-drawing-delivery{grid-template-rows:auto minmax(82px,auto);min-height:210px}.pdm-release-create-header.has-drawing-delivery .pdm-release-type-row{row-gap:8px}
.pdm-drawing-delivery-row{display:grid;grid-template-columns:minmax(160px,1fr) 110px 145px;gap:8px;align-items:center}.pdm-drawing-delivery-row span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-drawing-delivery-row input,.pdm-drawing-delivery-row select{width:100%;box-sizing:border-box}
@media(max-width:700px){.pdm-drawing-delivery-row{grid-template-columns:1fr 1fr}.pdm-drawing-delivery-row span{grid-column:1/-1}}
</style>
