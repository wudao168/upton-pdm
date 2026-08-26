<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { BomItem, CreateReleasePackageInput, ReleasePackageSummary, ReleaseScope } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()

const props = withDefaults(defineProps<{
  releasePackage: ReleasePackageSummary | null
  standardItems?: BomItem[]
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
}>(), { standardItems: () => [], canEmergencyDecide: false, allowedScopes: () => [], changeReasonTypes: () => ['设计变更', '客户需求', '物料替代', '质量整改', '生产反馈', '其他'], longLeadPublishedItemIds: () => [], previousVersionItems: () => [] })
const emit = defineEmits<{
  create: [input: CreateReleasePackageInput]
  upload: [releasePackageId: string, file: File]
  submit: [releasePackageId: string]
  withdraw: [releasePackageId: string]
  decide: [taskId: string, decision: 'Approved' | 'Rejected', comment: string]
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
const selectedBomItemIds = ref<string[]>([])
const comment = ref('同意')
const emergencyReason = ref('')
const currentTask = computed(() => props.releasePackage?.steps.find(step => step.id !== 'production-release' && step.status === 'current'))
const canPrepare = computed(() => !props.releasePackage || ['草稿', '已驳回', '发布失败'].includes(props.releasePackage.state))
const isSupplement = computed(() => scope.value === 'StandardSupplement' || scope.value === 'ElectricalSupplement')
const availableStandardItems = computed(() => props.standardItems.filter(row => !row.manuallyExcluded))
const longLeadPublishedIds = computed(() => new Set(props.longLeadPublishedItemIds))
const itemKey = (item: BomItem) => (item.drawingNumber || `${item.name}|${item.specification}`).trim().toLocaleLowerCase()
const itemSignature = (item: BomItem) => [item.kind, item.unit, item.drawingNumber, item.name, item.specification, item.remark, item.brand, item.material, item.surfaceTreatment, item.weight, item.quantity, item.revision].join('|')
const supplementRows = computed(() => {
  const previous = new Map(props.previousVersionItems.map(item => [itemKey(item), item]))
  const current = new Map(availableStandardItems.value.map(item => [itemKey(item), item]))
  return [
    ...availableStandardItems.value.filter(item => !previous.has(itemKey(item))).map(item => ({ item, change: '新增' })),
    ...availableStandardItems.value.filter(item => previous.has(itemKey(item)) && itemSignature(previous.get(itemKey(item))!) !== itemSignature(item)).map(item => ({ item, change: '修改' })),
    ...props.previousVersionItems.filter(item => !current.has(itemKey(item))).map(item => ({ item, change: '删除' })),
  ]
})
const createDisabled = computed(() => props.pending
  || scope.value === 'StandardLongLead' && selectedBomItemIds.value.length === 0
  || isSupplement.value && selectedChangeReasons.value.length === 0)
const requiresDrawingFiles = computed(() => props.releasePackage?.locksDocuments ?? scope.value === 'NonStandardWithDrawing')
const canHandleCurrentTask = computed(() => props.canDecide && currentTask.value
  && (currentTask.value.assignee.toLowerCase() === props.username.toLowerCase() || props.username.toLowerCase() === 'admin'))
const frozenItems = computed(() => {
  if (!props.releasePackage) return []
  if (props.releasePackage.scope.startsWith('Electrical')) return props.releasePackage.electricalBomSnapshot
  if (props.releasePackage.scope === 'NonStandardWithDrawing') return props.releasePackage.nonStandardBomSnapshot
  return props.releasePackage.standardBomSnapshot
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
watch(() => props.releasePackage?.id, () => { showCreate.value = false })

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
        <legend>选择长交期标准件（已选 {{ selectedBomItemIds.length }} 项）</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table">
            <thead><tr><th>选择</th><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>数量</th><th>备注</th></tr></thead>
            <tbody>
              <tr v-for="(item, index) in availableStandardItems" :key="item.id">
                <td><input v-model="selectedBomItemIds" type="checkbox" :value="item.id" :aria-label="`选择长交期物料 ${item.drawingNumber}`"></td>
                <td>{{ index + 1 }}</td><td>{{ item.drawingNumber || '—' }}</td><td>{{ item.name || '—' }}</td><td>{{ item.specification || '—' }}</td><td>{{ item.brand || '—' }}</td><td>{{ item.quantity }}</td><td>{{ item.remark || '—' }}</td>
              </tr>
              <tr v-if="!availableStandardItems.length"><td colspan="8" class="pdm-empty-info">当前没有可选的标准件。</td></tr>
            </tbody>
          </table>
        </div>
      </fieldset>
      <fieldset v-if="scope === 'StandardFormal'" class="release-item-picker">
        <legend>正式发布内容（共 {{ availableStandardItems.length }} 项）</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table">
            <thead><tr><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>数量</th><th>备注</th><th>长交期</th></tr></thead>
            <tbody>
              <tr v-for="(item, index) in availableStandardItems" :key="item.id">
                <td>{{ index + 1 }}</td><td>{{ item.drawingNumber || '—' }}</td><td>{{ item.name || '—' }}</td><td>{{ item.specification || '—' }}</td><td>{{ item.brand || '—' }}</td><td>{{ item.quantity }}</td><td>{{ item.remark || '—' }}</td>
                <td><span v-if="item.id && longLeadPublishedIds.has(item.id)" class="long-lead-tag">已提前发布</span><span v-else>—</span></td>
              </tr>
              <tr v-if="!availableStandardItems.length"><td colspan="8" class="pdm-empty-info">当前没有可发布的标准件。</td></tr>
            </tbody>
          </table>
        </div>
      </fieldset>
      <fieldset v-if="scope === 'StandardSupplement'" class="release-item-picker">
        <legend>增补/变更内容（共 {{ supplementRows.length }} 项）</legend>
        <div class="pdm-table-scroll">
          <table class="pdm-edit-table">
            <thead><tr><th>变更</th><th>序号</th><th>物料编码</th><th>名称</th><th>型号</th><th>品牌</th><th>数量</th><th>备注</th></tr></thead>
            <tbody>
              <tr v-for="(row, index) in supplementRows" :key="`${row.change}-${row.item.id}-${index}`">
                <td><span :class="`release-change-tag is-${row.change}`">{{ row.change }}</span></td><td>{{ index + 1 }}</td><td>{{ row.item.drawingNumber || '—' }}</td><td>{{ row.item.name || '—' }}</td><td>{{ row.item.specification || '—' }}</td><td>{{ row.item.brand || '—' }}</td><td>{{ row.item.quantity }}</td><td>{{ row.item.remark || '—' }}</td>
              </tr>
              <tr v-if="!supplementRows.length"><td colspan="8" class="pdm-empty-info">与上一正式发布版相比，当前没有增补或变更内容。</td></tr>
            </tbody>
          </table>
        </div>
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
        <div class="pdm-table-scroll"><table class="pdm-edit-table"><thead><tr><th>序号</th><th>物料编码</th><th>物料名称</th><th>型号</th><th>数量</th><th>版本</th></tr></thead><tbody><tr v-for="(item, index) in frozenItems" :key="item.id || `${item.drawingNumber}-${index}`"><td>{{ index + 1 }}</td><td>{{ item.drawingNumber }}</td><td>{{ item.name }}</td><td>{{ item.specification || '—' }}</td><td>{{ item.quantity }} {{ item.unit }}</td><td>{{ item.revision }}</td></tr><tr v-if="!frozenItems.length"><td colspan="6" class="pdm-empty-info">该发布包没有BOM快照数据。</td></tr></tbody></table></div>
        <p v-if="releasePackage.scope === 'StandardLongLead'" class="pdm-release-integration-note">发布后正常输出长交期BOM，并写入U9C待同步集成事件；不更新制造基线。</p>
      </section>

      <div v-if="canManage && ['审批中', '工艺审核', '待批准'].includes(releasePackage.state)" class="pdm-manager-actions">
        <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('withdraw', releasePackage.id)">撤回审批</button>
        <small>{{ releasePackage.locksDocuments ? '撤回后图档与非标件BOM恢复为工作中。' : '撤回后当前BOM版本恢复为草稿。' }}</small>
      </div>

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
          <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('decide', currentTask.id, 'Rejected', comment || '驳回')">驳回</button>
          <button type="button" class="pdm-primary-action" :disabled="pending" @click="emit('decide', currentTask.id, 'Approved', comment || '同意')">同意并流转</button>
        </div>
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
</style>
