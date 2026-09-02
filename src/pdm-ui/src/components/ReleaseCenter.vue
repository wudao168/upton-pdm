<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { listApprovalTransferCandidates } from '../api'
import type { ApprovalTransferCandidate, BomItem, CreateReleasePackageInput, ReleasePackageSummary, ReleaseScope } from '../types'
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
  longLeadPublishedItemIds?: string[]
  previousVersionItems?: BomItem[]
}>(), { standardItems: () => [], releaseItems: () => [], canEmergencyDecide: false, allowedScopes: () => [], changeReasonTypes: () => ['设计变更', '客户需求', '物料替代', '质量整改', '生产反馈', '其他'], longLeadPublishedItemIds: () => [], previousVersionItems: () => [] })
const emit = defineEmits<{
  create: [input: CreateReleasePackageInput]
  upload: [releasePackageId: string, file: File]
  submit: [releasePackageId: string]
  withdraw: [releasePackageId: string]
  decide: [taskId: string, decision: 'Approved' | 'Rejected', comment: string]
  transfer: [taskId: string, targetUsername: string, comment: string]
  emergencyDecide: [taskId: string, decision: 'Approved' | 'Rejected', reason: string]
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
const visibleReleaseTypes = computed(() => props.allowedScopes.length ? releaseTypes.filter(item => props.allowedScopes.includes(item.value)) : releaseTypes)
const showCreate = ref(false)
const releaseNote = ref('')
const selectedChangeReasons = ref<string[]>([])
const scope = ref<Exclude<ReleaseScope, 'LegacyCombined'>>('StandardLongLead')
const selectedLongLeadKeys = ref<string[]>([])
const comment = ref('同意')
const emergencyReason = ref('')
const transferOpen = ref(false)
const transferQuery = ref('')
const transferTarget = ref('')
const transferComment = ref('')
const transferCandidates = ref<ApprovalTransferCandidate[]>([])
const transferCandidatesLoading = ref(false)
const transferError = ref('')
const currentTask = computed(() => props.releasePackage?.steps.find(step => step.id !== 'production-release' && step.status === 'current'))
const canPrepare = computed(() => !props.releasePackage || ['草稿', '已驳回', '发布失败'].includes(props.releasePackage.state))
const isSupplement = computed(() => scope.value === 'StandardSupplement' || scope.value === 'ElectricalSupplement')
const availableReleaseItems = computed(() => (props.releaseItems.length ? props.releaseItems : props.standardItems).filter(row => !row.manuallyExcluded))
const releasePageSize = 50
const longLeadPage = ref(1)
const formalPage = ref(1)
const supplementPage = ref(1)
const frozenPage = ref(1)
const longLeadPublishedIds = computed(() => new Set(props.longLeadPublishedItemIds))
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
const longLeadPublishedKeys = computed(() => new Set(availableReleaseItems.value
  .map((item, index) => item.id && longLeadPublishedIds.value.has(item.id) ? releaseItemGroupKey(item, index) : null)
  .filter((key): key is string => Boolean(key))))
const longLeadReleaseRows = computed(() => {
  const grouped = new Map<string, { key: string; item: BomItem; sourceItemIds: string[] }>()
  availableReleaseItems.value.forEach((item, index) => {
    const key = releaseItemGroupKey(item, index)
    if (longLeadPublishedKeys.value.has(key)) return
    const existing = grouped.get(key)
    if (!existing) {
      grouped.set(key, { key, item: { ...item }, sourceItemIds: item.id ? [item.id] : [] })
      return
    }
    existing.item.quantity = Number(existing.item.quantity) + Number(item.quantity)
    if (item.id) existing.sourceItemIds.push(item.id)
  })
  return [...grouped.values()].filter(row => row.sourceItemIds.length > 0)
})
const selectedBomItemIds = computed(() => longLeadReleaseRows.value
  .filter(row => selectedLongLeadKeys.value.includes(row.key))
  .flatMap(row => row.sourceItemIds))
const longLeadPageCount = computed(() => Math.max(1, Math.ceil(longLeadReleaseRows.value.length / releasePageSize)))
const pagedLongLeadItems = computed(() => {
  const start = (longLeadPage.value - 1) * releasePageSize
  return longLeadReleaseRows.value.slice(start, start + releasePageSize)
})
const formalReleaseRows = computed(() => {
  const grouped = new Map<string, { item: BomItem; longLeadPublished: boolean }>()
  availableReleaseItems.value.forEach((item, index) => {
    const key = releaseItemGroupKey(item, index)
    const existing = grouped.get(key)
    if (!existing) {
      grouped.set(key, {
        item: { ...item },
        longLeadPublished: Boolean(item.id && longLeadPublishedIds.value.has(item.id)),
      })
      return
    }
    existing.item.quantity = Number(existing.item.quantity) + Number(item.quantity)
    existing.longLeadPublished ||= Boolean(item.id && longLeadPublishedIds.value.has(item.id))
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
const createDisabled = computed(() => props.pending
  || scope.value === 'StandardLongLead' && selectedBomItemIds.value.length === 0
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
const frozenPageCount = computed(() => Math.max(1, Math.ceil(frozenItems.value.length / releasePageSize)))
const pagedFrozenItems = computed(() => {
  const start = (frozenPage.value - 1) * releasePageSize
  return frozenItems.value.slice(start, start + releasePageSize)
})
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
watch(() => props.releasePackage?.id, () => { showCreate.value = false; frozenPage.value = 1 })
watch(scope, () => {
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
})

function create() {
  if (createDisabled.value) return
  emit('create', {
    changeReason: isSupplement.value ? selectedChangeReasons.value.join('；') : releaseNote.value,
    scope: scope.value,
    selectedBomItemIds: scope.value === 'StandardLongLead' ? selectedBomItemIds.value : [],
  })
}

function uploadSelected(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file && props.releasePackage) emit('upload', props.releasePackage.id, file)
  input.value = ''
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
</script>

<template>
  <section class="pdm-panel pdm-manager-panel release-center" aria-label="审批与生产发包">
    <div v-if="canManage && releasePackage?.state === '已发布'" class="pdm-manager-actions">
      <button type="button" class="pdm-primary-action" @click="showCreate = !showCreate">新建发布包</button>
    </div>

    <form v-if="canManage && (!releasePackage || showCreate)" class="pdm-form-grid pdm-release-create-form" @submit.prevent="create">
      <div class="pdm-release-type-row">
        <label>发布类型
          <select v-model="scope">
            <option v-for="item in visibleReleaseTypes" :key="item.value" :value="item.value">{{ item.label }}</option>
          </select>
        </label>
        <button type="submit" class="pdm-primary-action" :disabled="createDisabled">创建草稿</button>
        <label v-if="isSupplement">变更单号<input value="创建草稿后自动生成" readonly aria-label="变更单号由系统自动生成"></label>
      </div>
      <label v-if="!isSupplement" class="pdm-release-reason">备注<textarea v-model.trim="releaseNote" rows="3" maxlength="500" placeholder="可填写本次发布备注（选填）"></textarea></label>
      <fieldset v-else class="release-change-reason-picker">
        <legend>变更原因（可多选，已选 {{ selectedChangeReasons.length }} 项）</legend>
        <label v-for="reason in changeReasonTypes" :key="reason">
          <input v-model="selectedChangeReasons" type="checkbox" :value="reason" :aria-label="`变更原因 ${reason}`">
          <span>{{ reason }}</span>
        </label>
      </fieldset>
      <fieldset v-if="scope === 'StandardLongLead'" class="long-lead-picker">
        <legend>选择长交期标准件（已选 {{ selectedLongLeadKeys.length }} 项）</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table">
            <thead><tr><th>选择</th><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>数量</th><th>备注</th></tr></thead>
            <tbody>
              <tr v-for="(row, index) in pagedLongLeadItems" :key="row.key">
                <td><input v-model="selectedLongLeadKeys" type="checkbox" :value="row.key" :aria-label="`选择长交期物料 ${row.item.drawingNumber}`"></td>
                <td>{{ (longLeadPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td>{{ row.item.brand || '—' }}</td><td>{{ row.item.quantity }}</td><td>{{ row.item.remark || '—' }}</td>
              </tr>
              <tr v-if="!longLeadReleaseRows.length"><td colspan="8" class="pdm-empty-info">当前没有尚未提前发布的标准件。</td></tr>
            </tbody>
          </table>
        </div>
        <nav class="long-lead-pagination" aria-label="长交期标准件分页">
          <span>共 {{ longLeadReleaseRows.length }} 条 · 50 条/页</span>
          <button type="button" :disabled="longLeadPage <= 1" aria-label="上一页" @click="longLeadPage--">‹</button>
          <strong>{{ longLeadPage }} / {{ longLeadPageCount }}</strong>
          <button type="button" :disabled="longLeadPage >= longLeadPageCount" aria-label="下一页" @click="longLeadPage++">›</button>
        </nav>
      </fieldset>
      <fieldset v-if="scope === 'StandardFormal' || scope === 'ElectricalFormal' || scope === 'NonStandardWithDrawing'" class="release-item-picker">
        <legend>正式发布内容（共 {{ formalReleaseRows.length }} 项）</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table">
            <thead><tr><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>数量</th><th>备注</th><th>长交期</th></tr></thead>
            <tbody>
              <tr v-for="(row, index) in pagedFormalReleaseRows" :key="`${row.item.drawingNumber || row.item.id || index}-${row.item.unit}`">
                <td>{{ (formalPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td>{{ row.item.brand || '—' }}</td><td>{{ row.item.quantity }}</td><td>{{ row.item.remark || '—' }}</td>
                <td><span v-if="row.longLeadPublished" class="long-lead-tag">已提前发布</span><span v-else>—</span></td>
              </tr>
              <tr v-if="!formalReleaseRows.length"><td colspan="8" class="pdm-empty-info">当前没有可发布的标准件。</td></tr>
            </tbody>
          </table>
        </div>
        <nav class="release-list-pagination" aria-label="正式发布内容分页">
          <span>共 {{ formalReleaseRows.length }} 条 · 50 条/页</span>
          <button type="button" :disabled="formalPage <= 1" aria-label="正式发布上一页" @click="formalPage--">‹</button>
          <strong>{{ formalPage }} / {{ formalPageCount }}</strong>
          <button type="button" :disabled="formalPage >= formalPageCount" aria-label="正式发布下一页" @click="formalPage++">›</button>
        </nav>
      </fieldset>
      <fieldset v-if="isSupplement" class="release-item-picker">
        <legend>增补/变更内容（共 {{ supplementRows.length }} 项）</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table">
            <thead><tr><th>变更</th><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>数量</th><th>备注</th></tr></thead>
            <tbody>
              <tr v-for="(row, index) in pagedSupplementRows" :key="`${row.change}-${row.item.id}-${index}`">
                <td><span :class="`release-change-tag is-${row.change}`">{{ row.change }}</span></td><td>{{ (supplementPage - 1) * releasePageSize + index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td>{{ row.item.brand || '—' }}</td><td>{{ row.item.quantity }}</td><td>{{ row.item.remark || '—' }}</td>
              </tr>
              <tr v-if="!supplementRows.length"><td colspan="8" class="pdm-empty-info">与上一正式发布版相比，当前没有增补或变更内容。</td></tr>
            </tbody>
          </table>
        </div>
        <nav class="release-list-pagination" aria-label="增补变更内容分页">
          <span>共 {{ supplementRows.length }} 条 · 50 条/页</span>
          <button type="button" :disabled="supplementPage <= 1" aria-label="增补变更上一页" @click="supplementPage--">‹</button>
          <strong>{{ supplementPage }} / {{ supplementPageCount }}</strong>
          <button type="button" :disabled="supplementPage >= supplementPageCount" aria-label="增补变更下一页" @click="supplementPage++">›</button>
        </nav>
      </fieldset>
    </form>

    <template v-if="releasePackage && !showCreate">
      <div class="pdm-release-summary">
        <div><small>发布包</small><strong>{{ releasePackage.number }}</strong></div>
        <div><small>发布范围</small><strong>{{ scopeLabels[releasePackage.scope] || '旧版组合发布' }}</strong></div>
        <div><small>当前状态</small><strong>{{ releasePackage.state }}</strong></div>
        <div><small>审批模板</small><strong>{{ releasePackage.workflowCode ? `${releasePackage.workflowCode} v${releasePackage.workflowVersion}` : '旧版固定流程' }}</strong></div>
        <div v-if="releasePackage.changeNumber && releasePackage.changeNumber !== releasePackage.number"><small>变更单号</small><strong>{{ releasePackage.changeNumber }}</strong></div>
        <div><small>BOM版本</small><strong>S {{ releasePackage.standardBomRevision || '—' }} · N {{ releasePackage.nonStandardBomRevision || '—' }} · E {{ releasePackage.electricalBomRevision || '—' }}</strong></div>
        <div><small>制造基线</small><strong>{{ releasePackage.createsManufacturingBaseline ? '发布后生成新基线' : releasePackage.scope === 'StandardLongLead' ? '不更新（长交期输出）' : '三条正式流齐备后生成' }}</strong></div>
        <div><small>发布目录</small><strong>{{ releasePackage.publishedPath || '审批通过后自动投放' }}</strong></div>
      </div>
      <p v-if="releasePackage.changeReason" class="pdm-release-change-reason"><strong>{{ releasePackage.scope === 'StandardSupplement' || releasePackage.scope === 'ElectricalSupplement' ? '变更原因' : '备注' }}：</strong>{{ releasePackage.changeReason }}</p>

      <section class="pdm-release-frozen-snapshot" aria-label="审批固化快照">
        <header><div><strong>审批固化快照</strong><small>审批、历史和发布均使用创建发布包时的不可变数据。</small></div><span>{{ frozenItems.length }} 项</span></header>
        <div class="pdm-release-diff-summary"><span class="is-added">新增 {{ frozenDiff.added }}</span><span class="is-modified">修改 {{ frozenDiff.modified }}</span><span class="is-removed">减少 {{ frozenDiff.removed }}</span><small>对比上一正式发布版</small></div>
        <div class="pdm-table-scroll"><table class="pdm-edit-table pdm-release-frozen-table"><colgroup><col><col><col><col><col><col><col><col></colgroup><thead><tr><th>序号</th><th>物料编码</th><th>物料名称</th><th>型号</th><th>品牌</th><th>备注</th><th>数量</th><th>版本</th></tr></thead><tbody><tr v-for="(item, index) in pagedFrozenItems" :key="item.id || `${item.drawingNumber}-${index}`"><td>{{ (frozenPage - 1) * releasePageSize + index + 1 }}</td><td>{{ item.drawingNumber || '—' }}</td><td>{{ item.name || '—' }}</td><td>{{ item.specification || '—' }}</td><td>{{ item.brand || '—' }}</td><td>{{ item.remark || '—' }}</td><td>{{ item.quantity }}</td><td>{{ item.revision || '—' }}</td></tr><tr v-if="!frozenItems.length"><td colspan="8" class="pdm-empty-info">该发布包没有BOM快照数据。</td></tr></tbody></table></div>
        <nav class="release-list-pagination" aria-label="审批固化快照分页">
          <span>共 {{ frozenItems.length }} 条 · 50 条/页</span>
          <button type="button" :disabled="frozenPage <= 1" aria-label="固化快照上一页" @click="frozenPage--">‹</button>
          <strong>{{ frozenPage }} / {{ frozenPageCount }}</strong>
          <button type="button" :disabled="frozenPage >= frozenPageCount" aria-label="固化快照下一页" @click="frozenPage++">›</button>
        </nav>
        <p v-if="releasePackage.scope === 'StandardLongLead'" class="pdm-release-integration-note">发布后正常输出长交期BOM，并写入U9C待同步集成事件；不更新制造基线。</p>
      </section>

      <div class="pdm-approval-chain">
        <article v-for="step in releasePackage.steps" :key="step.id" :class="`is-${step.status}`">
          <span>{{ step.status === 'done' ? '✓' : step.status === 'current' ? '●' : '○' }}</span>
          <div>
            <strong>{{ step.stage }}</strong>
            <small>{{ displayUserName(step.assignee) }} · {{ step.detail }}</small>
            <em v-if="step.emergencySubstitute">紧急代批：{{ step.emergencyReason }}</em>
            <em v-else-if="step.comment">{{ step.comment }}</em>
          </div>
        </article>
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
        <label>审批意见<textarea v-model.trim="comment" rows="3" maxlength="1000" /></label>
        <div class="pdm-manager-actions">
          <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('decide', currentTask.id, 'Rejected', comment || '退回')">退回</button>
          <button type="button" class="pdm-secondary-action" :disabled="pending" @click="openTransfer">转交</button>
          <button type="button" class="pdm-primary-action" :disabled="pending" @click="emit('decide', currentTask.id, 'Approved', comment || '同意')">通过</button>
        </div>
      </div>
      <div v-else-if="canManage && ['审批中', '工艺审核', '待批准'].includes(releasePackage.state)" class="pdm-decision-box pdm-withdraw-decision">
        <small>{{ releasePackage.locksDocuments ? '撤回后图档与非标件BOM恢复为工作中。' : '撤回后当前BOM版本恢复为草稿。' }}</small>
        <div class="pdm-manager-actions">
          <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('withdraw', releasePackage.id)">撤回审批</button>
        </div>
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
      <div v-if="canEmergencyDecide && currentTask && !canHandleCurrentTask" class="pdm-decision-box emergency-decision">
        <label>紧急代批原因<textarea v-model.trim="emergencyReason" rows="3" maxlength="1000" required placeholder="说明必须立即处理的业务原因；该内容会进入审计记录" /></label>
        <div class="pdm-manager-actions">
          <button type="button" class="pdm-secondary-action is-danger" :disabled="pending || !emergencyReason" @click="emit('emergencyDecide', currentTask.id, 'Rejected', emergencyReason)">紧急代驳回</button>
          <button type="button" class="pdm-primary-action" :disabled="pending || !emergencyReason" @click="emit('emergencyDecide', currentTask.id, 'Approved', emergencyReason)">紧急代批当前节点</button>
        </div>
      </div>
      <p v-if="releasePackage.publishError" class="pdm-inline-error">发布失败：{{ releasePackage.publishError }}</p>
    </template>
    <p v-if="error" class="pdm-inline-error">{{ error }}</p>
  </section>
</template>

<style scoped>
.release-center select{height:34px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff;padding:0 9px;color:var(--pdm-text)}.pdm-release-type-row{grid-column:1/-1;display:grid;grid-template-columns:minmax(0,1fr) 80px minmax(0,1fr);gap:5px;align-items:end}.pdm-release-type-row label{min-width:0}.pdm-release-type-row select,.pdm-release-type-row input{width:100%;height:34px;box-sizing:border-box}.pdm-release-type-row .pdm-primary-action{width:80px;height:34px;padding:0}.long-lead-picker,.release-item-picker,.release-change-reason-picker{grid-column:1/-1;margin:0;padding:10px;border:1px solid var(--pdm-border);border-radius:7px}.long-lead-picker legend,.release-item-picker legend,.release-change-reason-picker legend{padding:0 5px;font-weight:600}.long-lead-picker .pdm-table-scroll,.release-item-picker .pdm-table-scroll{max-height:240px;border:1px solid var(--pdm-border);border-radius:5px}.long-lead-picker th,.long-lead-picker td,.release-item-picker th,.release-item-picker td{white-space:nowrap}.long-lead-picker th:first-child,.long-lead-picker td:first-child{width:42px;text-align:center}.long-lead-picker th:nth-child(2),.long-lead-picker td:nth-child(2),.release-item-picker th:nth-child(2),.release-item-picker td:nth-child(2){width:46px;text-align:center}.release-change-reason-picker{display:flex;flex-wrap:wrap;gap:8px 18px}.release-change-reason-picker label{display:flex;align-items:center;gap:5px}.long-lead-tag,.release-change-tag{display:inline-flex;align-items:center;min-height:20px;padding:0 6px;border-radius:10px;background:#fff7ed;color:#c2410c}.release-change-tag.is-新增{background:#ecfdf5;color:#15803d}.release-change-tag.is-修改{background:#fff7ed;color:#b45309}.release-change-tag.is-删除{background:#fef2f2;color:#b91c1c}.pdm-release-create-form .pdm-release-reason{grid-column:1/-1}.emergency-decision{border-color:#f59e0b;background:#fffbeb}.pdm-release-frozen-snapshot{margin:12px 0;border:1px solid var(--pdm-border);border-radius:7px;overflow:hidden}.pdm-release-frozen-snapshot>header{display:flex;justify-content:space-between;gap:12px;align-items:center;padding:9px 11px;background:var(--pdm-surface-soft)}.pdm-release-frozen-snapshot>header div{display:grid;gap:2px}.pdm-release-frozen-snapshot>header small{color:var(--pdm-muted)}.pdm-release-diff-summary{display:flex;align-items:center;gap:10px;padding:7px 11px;border-top:1px solid var(--pdm-border);border-bottom:1px solid var(--pdm-border)}.pdm-release-diff-summary small{margin-left:auto;color:var(--pdm-muted)}.pdm-release-diff-summary .is-added{color:#15803d}.pdm-release-diff-summary .is-modified{color:#b45309}.pdm-release-diff-summary .is-removed{color:#b91c1c}.pdm-release-frozen-snapshot .pdm-table-scroll{max-height:230px}.pdm-release-integration-note{margin:0;padding:8px 11px;color:#0f766e;background:#f0fdfa;border-top:1px solid #99f6e4}@media(max-width:900px){.pdm-release-type-row{grid-template-columns:minmax(0,1fr) 80px minmax(0,1fr)}}
.long-lead-picker,.release-item-picker{display:flex;min-height:0;flex-direction:column}.long-lead-picker .pdm-table-scroll,.release-item-picker .pdm-table-scroll{height:clamp(220px,calc(100dvh - 390px),680px);max-height:none;overflow:auto}.long-lead-pagination,.release-list-pagination{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding-top:8px;color:var(--pdm-muted)}.long-lead-pagination span,.release-list-pagination span{margin-right:auto}.long-lead-pagination button,.release-list-pagination button{width:28px;height:28px;border:1px solid var(--pdm-border);border-radius:6px;background:var(--pdm-surface);color:var(--pdm-text);cursor:pointer}.long-lead-pagination button:disabled,.release-list-pagination button:disabled{cursor:not-allowed;opacity:.45}.long-lead-pagination strong,.release-list-pagination strong{min-width:54px;text-align:center;color:var(--pdm-text)}
.pdm-release-frozen-snapshot{display:flex;min-height:0;flex-direction:column}.pdm-release-frozen-snapshot .pdm-table-scroll{height:clamp(220px,calc(100dvh - 470px),620px);max-height:none;overflow:auto}.pdm-release-frozen-snapshot .release-list-pagination{padding:8px 11px}
.pdm-release-frozen-table{width:100%;table-layout:fixed}.pdm-release-frozen-table col:nth-child(1),.pdm-release-frozen-table col:nth-child(7),.pdm-release-frozen-table col:nth-child(8){width:7.142857%}.pdm-release-frozen-table col:nth-child(2),.pdm-release-frozen-table col:nth-child(3),.pdm-release-frozen-table col:nth-child(5),.pdm-release-frozen-table col:nth-child(6){width:14.285714%}.pdm-release-frozen-table col:nth-child(4){width:21.428571%}.pdm-release-frozen-table th,.pdm-release-frozen-table td{white-space:normal;overflow-wrap:anywhere;vertical-align:middle}.pdm-release-frozen-table th:nth-child(1),.pdm-release-frozen-table td:nth-child(1),.pdm-release-frozen-table th:nth-child(7),.pdm-release-frozen-table td:nth-child(7),.pdm-release-frozen-table th:nth-child(8),.pdm-release-frozen-table td:nth-child(8){text-align:center}
.pdm-approval-transfer-backdrop{z-index:3100}.pdm-approval-transfer-dialog{width:min(520px,calc(100vw - 32px));max-height:min(620px,calc(100vh - 32px));display:grid;grid-template-rows:auto auto minmax(100px,1fr) auto auto;gap:12px;padding:16px;border-radius:10px;background:#fff;box-shadow:0 18px 60px rgb(15 23 42 / 24%)}.pdm-approval-transfer-dialog>header,.pdm-approval-transfer-dialog>footer{display:flex;align-items:center;justify-content:space-between;gap:8px}.pdm-approval-transfer-dialog>footer{justify-content:flex-end}.pdm-approval-transfer-dialog>label{display:grid;gap:5px}.pdm-approval-transfer-dialog input,.pdm-approval-transfer-dialog textarea{width:100%;box-sizing:border-box}.pdm-approval-transfer-list{min-height:100px;overflow:auto;border:1px solid var(--pdm-border);border-radius:7px}.pdm-approval-transfer-list>button{width:100%;display:flex;align-items:center;justify-content:space-between;gap:12px;padding:9px 11px;border:0;border-bottom:1px solid var(--pdm-border);background:#fff;color:var(--pdm-text);text-align:left}.pdm-approval-transfer-list>button.is-selected{background:#ecfdf5;color:#0f766e}.pdm-approval-transfer-list>button small{color:var(--pdm-muted)}.pdm-approval-transfer-list>p{margin:0;padding:12px;color:var(--pdm-muted)}
</style>
