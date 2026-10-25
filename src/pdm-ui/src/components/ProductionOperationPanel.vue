<script setup lang="ts">
import { vQuantity } from '../quantityInput'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { PdmApiError, cancelUnassignedProductionDemand, readProductionActions, readProductionTrackingRelease, recordProductionMovement } from '../api'
import { lotStage, unassignedQuantity, productionMovementLabels, productionPosts, productionRequestId, type ProductionMovementKind, type ProductionReleaseRecord, type ProductionScanContext } from '../productionTracking'
import { userErrorMessage } from '../userErrorMessage'
import { openProductionLabel } from '../productionLabel'
const props = defineProps<{ token: string; username: string; record: ProductionReleaseRecord; lineId: string; preferredLotId?: string; compact?: boolean }>()
const emit = defineEmits<{ updated: [record: ProductionReleaseRecord] }>()
const context = ref<ProductionScanContext>({ actions: [], hint: '' }), lotId = ref(''), kind = ref<ProductionMovementKind>(), workstationId = ref('')
const quantity = ref(1), recipient = ref(''), note = ref(''), loading = ref(false), pending = ref(false), error = ref(''), notice = ref('')
const controlReason = ref(''), printing = ref(false)
const controlSelection = ref(''), controlQuantity = ref(1)
const demandQuantity = ref(1), demandReason = ref('')
const demandAttempt = ref<Parameters<typeof cancelUnassignedProductionDemand>[1]>()
type Input = Parameters<typeof recordProductionMovement>[1]
const attempt = ref<Input>()
const storageKey = computed(() => `pdm-production-pending-${props.username}-${props.record.state.releasePackageId}`)
const unassigned = computed(() => {
  const line = props.record.state.lines.find(item => item.id === props.lineId)
  return line ? unassignedQuantity(props.record.state, line) : 0
})
const demandHistory = computed(() => (props.record.state.demandCancellations || []).filter(item => item.lineId === props.lineId))
const lots = computed(() => props.record.state.lots.filter(lot => lot.lineId === props.lineId))
const lot = computed(() => lots.value.find(item => item.id === lotId.value))
const options = computed(() => context.value.actions.filter(item => item.lotId === lotId.value && item.post !== 'Control'))
const controls = computed(() => context.value.actions.filter(item => item.lotId === lotId.value && item.post === 'Control'))
const controlKey = (item: typeof controls.value[number]) => `${item.kind}:${item.sourceKind || ''}`
const controlAction = computed(() => controls.value.find(item => controlKey(item) === controlSelection.value))
const kinds = computed(() => [...new Map(options.value.map(item => [item.kind, item.label])).entries()])
const stations = computed(() => options.value.filter(item => item.kind === kind.value))
const action = computed(() => stations.value.find(item => item.workstationId === workstationId.value))
const history = computed(() => props.record.state.movements.filter(item => item.lotId === lotId.value))
const pendingDescription = computed(() => {
  const pendingLot = props.record.state.lots.find(item => item.id === attempt.value?.lotId)
  const line = props.record.state.lines.find(item => item.id === pendingLot?.lineId)
  return `${line?.drawingNumber || ''} · ${line?.name || ''} · 批次${attempt.value?.lotId.slice(0, 8)} · ${context.value.actions.find(item => item.kind === attempt.value?.kind)?.label || productionMovementLabels[attempt.value?.kind || ''] || '流程办理'} ${attempt.value?.quantity ?? ''}`
})
let generation = 0
onBeforeUnmount(() => { generation++ })
function storeAttempt(value?: Input) { attempt.value = value; try { if (value) sessionStorage.setItem(storageKey.value, JSON.stringify(value)); else sessionStorage.removeItem(storageKey.value) } catch { /* 保留当前页面内的重试状态。 */ } }
function storeDemandAttempt(value?: typeof demandAttempt.value) {
  demandAttempt.value = value
  try { if (value) sessionStorage.setItem(storageKey.value + '-demand', JSON.stringify(value)); else sessionStorage.removeItem(storageKey.value + '-demand') } catch { /* 保留当前页面内的重试状态。 */ }
}
async function load() {
  const current = ++generation; loading.value = true; error.value = ''
  try {
    const value = await readProductionActions(props.record.state.releasePackageId, props.token)
    if (current !== generation) return
    context.value = value
    if (!lots.value.some(item => item.id === lotId.value)) {
      const eligible = lots.value.filter(item => value.actions.some(option => option.lotId === item.id))
      lotId.value = lots.value.some(item => item.id === props.preferredLotId) ? props.preferredLotId! : eligible.length === 1 ? eligible[0]!.id : lots.value.length === 1 ? lots.value[0]!.id : ''
    }
  } catch (cause) { if (current === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { if (current === generation) loading.value = false }
}
watch([() => props.token, () => props.lineId, () => props.preferredLotId, () => props.record.state.releasePackageId], () => {
  lotId.value = ''; notice.value = ''; attempt.value = undefined
  demandAttempt.value = undefined
  try { const saved = sessionStorage.getItem(storageKey.value); if (saved) attempt.value = JSON.parse(saved) as Input } catch { /* 不使用不可读的本地重试数据。 */ }
  try { const saved = sessionStorage.getItem(storageKey.value + '-demand'); if (saved) demandAttempt.value = JSON.parse(saved) } catch { /* 不使用不可读的需求重试数据。 */ }
  if (attempt.value) lotId.value = attempt.value.lotId
  void load()
}, { immediate: true })
watch(() => props.record.rowVersion, () => { void load() })
watch(kinds, values => { if (!values.some(([value]) => value === kind.value)) kind.value = values[0]?.[0] })
watch(stations, values => { if (!values.some(item => item.workstationId === workstationId.value)) workstationId.value = values.length === 1 ? values[0]!.workstationId : '' })
watch(action, value => { if (value && !attempt.value) quantity.value = value.remainingQuantity })
watch(controls, values => { if (!values.some(item => controlKey(item) === controlSelection.value)) controlSelection.value = '' })
watch(controlAction, value => { if (value && !attempt.value) controlQuantity.value = value.remainingQuantity })
async function refresh() {
  if (pending.value) return
  try {
    const value = await readProductionTrackingRelease(props.record.state.releasePackageId, props.token)
    if (attempt.value && value.state.movements.some(item => item.requestId === attempt.value!.requestId && item.actor === props.username)) { storeAttempt(); notice.value = '已核实上次操作保存成功。' }
    if (demandAttempt.value && value.state.demandCancellations?.some(item => item.requestId === demandAttempt.value!.requestId && item.actor === props.username)) { storeDemandAttempt(); notice.value = '已核实上次需求取消保存成功。' }
    emit('updated', value); await load()
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
}
async function save() {
  if (pending.value) return
  if (demandAttempt.value) { error.value = '请先核实或重试未分配需求取消操作。'; return }
  if (!attempt.value) {
    if (!action.value || !Number.isFinite(quantity.value) || quantity.value <= 0 || quantity.value > action.value.remainingQuantity) { error.value = '请选择可办理步骤和工位，并填写不超过可办理数量的正数。'; return }
    if ((kind.value === 'Rework' || kind.value === 'Scrap') && !note.value.trim()) { error.value = '返工或报废必须填写原因。'; return }
    if (kind.value === 'Issue' && !recipient.value.trim()) { error.value = '请填写实际领用人。'; return }
    storeAttempt({ lotId: lotId.value, kind: kind.value!, workstationId: workstationId.value, quantity: quantity.value,
      requestId: productionRequestId(), note: note.value.trim(), recipient: kind.value === 'Issue' ? recipient.value.trim() : undefined })
  }
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const value = await recordProductionMovement(props.record.state.releasePackageId, attempt.value!, props.token)
    const label = value.state.movements.find(item => item.requestId === attempt.value!.requestId)?.operationName || productionMovementLabels[attempt.value!.kind] || '流程办理'
    storeAttempt(); note.value = ''; emit('updated', value); notice.value = `${label}已确认并保存操作记录。`; await load()
  } catch (cause) {
    const message = userErrorMessage(cause instanceof Error ? cause.message : '')
    if (cause instanceof PdmApiError && cause.status >= 400 && cause.status < 500) { storeAttempt(); await load() }
    error.value = message
  }
  finally { pending.value = false }
}
async function manage() {
  if (pending.value || attempt.value || demandAttempt.value) return
  const control = controlAction.value
  if (!control) { error.value = '请选择处理操作及发生环节。'; return }
  if (!controlReason.value.trim()) { error.value = '请填写处理原因及实物去向；暂停或恢复请说明原因。'; return }
  if (!Number.isFinite(controlQuantity.value) || controlQuantity.value <= 0 || controlQuantity.value > control.remainingQuantity) { error.value = '处理数量必须大于零，且不能超过该环节可处理数量。'; return }
  const kind = control.kind
  const effect = kind === 'Cancelled' ? '本次会减少业务需求，请确认该数量确实不再需要。'
    : kind === 'TaskCancelled' ? '原业务需求保留，本次数量需重新分配自制或外协任务。'
      : kind === 'StageScrap' ? '原业务需求保留，本次报废数量需补制或补货。' : ''
  if (effect && !window.confirm(`${control.label}：${control.sourceName || '当前环节'}，数量 ${controlQuantity.value}。${effect}历史记录保留。`)) return
  storeAttempt({ lotId: lotId.value, kind, sourceKind: control.sourceKind || undefined, quantity: controlQuantity.value, requestId: productionRequestId(), note: controlReason.value.trim() })
  await save(); if (!attempt.value) controlReason.value = ''
}
async function cancelDemand() {
  if (pending.value || attempt.value) return
  if (!demandAttempt.value) {
    if (!Number.isFinite(demandQuantity.value) || demandQuantity.value <= 0 || demandQuantity.value > unassigned.value || !demandReason.value.trim()) {
      error.value = '请填写不超过未分配需求的正数及需求取消原因。'; return
    }
    if (!window.confirm(`确认取消 ${demandQuantity.value} 的未分配需求？业务需求将减少，已分配和已领用数量保持原记录。`)) return
    storeDemandAttempt({ lineId: props.lineId, quantity: demandQuantity.value, reason: demandReason.value.trim(), requestId: productionRequestId() })
  }
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const value = await cancelUnassignedProductionDemand(props.record.state.releasePackageId, demandAttempt.value!, props.token)
    storeDemandAttempt(); demandReason.value = ''; emit('updated', value); notice.value = '未分配需求已取消并保留操作记录。'; await load()
  } catch (cause) {
    if (cause instanceof PdmApiError && cause.status >= 400 && cause.status < 500) storeDemandAttempt()
    error.value = userErrorMessage(cause instanceof Error ? cause.message : '')
  } finally { pending.value = false }
}
async function printLabel() {
  if (!lot.value || printing.value) return
  printing.value = true; error.value = ''
  try { await openProductionLabel(props.record, lot.value.id, props.token, context.value.labelWidthMm, context.value.labelHeightMm); notice.value = '标签预览已打开，请选择已连接的标签打印机并打印。' }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { printing.value = false }
}
</script>
<template>
  <section class="production-operation" aria-label="当前可办理步骤">
    <header><strong>本岗扫码办理</strong><button type="button" :disabled="loading || pending" @click="refresh">刷新步骤</button></header>
    <p v-if="error" role="alert" class="production-operation__error">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="loading" role="status">正在核对岗位、工位和当前可办理数量…</p>
    <label v-if="lots.length > 1">本次生产批次<select v-model="lotId" :disabled="pending || Boolean(attempt)"><option value="">请选择批次，不默认第一批</option><option v-for="item in lots" :key="item.id" :value="item.id">{{ item.route === 'Self' ? '自制' : '外协' }} · {{ item.quantity }} · {{ lotStage(record.state, item) }} · {{ item.id.slice(0, 8) }}</option></select></label>
    <p v-if="lot">当前环节：{{ lotStage(record.state, lot) }}</p>
    <p v-if="kind === 'Trace'">资源组扫码仅保留追溯记录，不推进阶段，也不作为后续交接的前置条件。</p>
    <p v-if="!loading && !options.length">{{ !context.actions.length ? context.hint : lotId ? '该批次暂无本岗位可办理的步骤，请核对前序交接及本岗权限。' : '请选择本次实际办理的生产批次。' }}</p>
    <form v-if="options.length || attempt" @submit.prevent="save">
      <fieldset v-if="!attempt || !['Pause', 'Resume', 'Cancelled', 'TaskCancelled', 'StageScrap'].includes(attempt.kind)" :disabled="pending || loading || Boolean(attempt)">
        <label>本次步骤<select v-model="kind"><option v-for="[value, label] in kinds" :key="value" :value="value">{{ label }}</option></select></label>
        <label>办理工位<select v-model="workstationId"><option value="">请选择工位</option><option v-for="item in stations" :key="item.workstationId" :value="item.workstationId">{{ item.workstationName }}{{ item.resourceGroup ? ` · ${item.resourceGroup}` : '' }}</option></select></label>
        <p v-if="action">本岗位本工位可办理 {{ action.remainingQuantity }}；岗位：{{ productionPosts[action.post] }}</p>
        <label>确认本次数量<input v-quantity v-model.number="quantity" type="number" min="0.001" step="any" :max="action?.remainingQuantity" required></label>
        <label v-if="kind === 'Issue'">实际领用人<input v-model="recipient" required></label>
        <label v-if="kind === 'Rework' || kind === 'Scrap'">处理原因<input v-model="note" required></label>
        <label v-if="kind === 'Trace'">追溯说明（选填）<input v-model="note"></label>
      </fieldset>
      <p v-if="attempt">待核实上次操作：{{ pendingDescription }}。重试将沿用同一操作编号和数量，避免重复登记。</p>
      <button type="submit" class="pdm-primary-action" :disabled="pending || loading || !attempt && !action">{{ pending ? '正在确认…' : attempt ? '重试确认结果' : '确认数量并推进' }}</button>
    </form>
    <button v-if="lot && context.canPrintLabels" type="button" :disabled="pending || printing" @click="printLabel">{{ printing ? '正在准备标签…' : '打印二维码信息标签' }}</button>
    <component :is="compact ? 'details' : 'div'" v-if="controls.length" class="production-operation__controls">
      <summary v-if="compact">流程管理与异常处理</summary><strong v-else>流程管理与异常处理</strong>
      <p>自制、外协均可按环节处理部分数量。取消加工任务和报废保留需求；取消业务需求会减少需求。已暂停数量须先恢复再取消或报废。</p>
      <label>处理操作及发生环节<select v-model="controlSelection" aria-label="处理操作及发生环节" :disabled="pending || Boolean(attempt)"><option value="">请选择操作及环节</option><option v-for="control in controls" :key="controlKey(control)" :value="controlKey(control)">{{ control.label }} · {{ control.sourceName || '历史整批暂停' }} · 可处理 {{ control.remainingQuantity }}</option></select></label>
      <label>本次处理数量<input v-quantity v-model.number="controlQuantity" aria-label="本次处理数量" type="number" min="0.001" step="any" :max="controlAction?.remainingQuantity" :disabled="pending || Boolean(attempt)"></label>
      <label>处理原因及实物去向<input v-model="controlReason" aria-label="处理原因及实物去向" :disabled="pending || Boolean(attempt)" placeholder="例如：加工损坏2件，已隔离，待补制"></label>
      <button type="button" :disabled="pending || Boolean(attempt)" @click="manage">确认处理</button>
    </component>
    <details v-if="context.canCancelUnassignedDemand && unassigned > 0 || demandAttempt" class="production-operation__controls">
      <summary>取消未分配需求（{{ unassigned }}）</summary><p>用于尚未分配或报废后不再补制的数量；不会取消其他批次正在执行的任务。</p>
      <label>取消未分配数量<input v-quantity v-model.number="demandQuantity" type="number" min="0.001" step="any" :max="unassigned" :disabled="pending || Boolean(demandAttempt)"></label>
      <label>需求取消原因<input v-model="demandReason" :disabled="pending || Boolean(demandAttempt)"></label>
      <p v-if="demandAttempt">待核实上次需求取消：{{ demandAttempt.quantity }}。重试沿用同一操作编号，不重复扣减。</p>
      <button type="button" :disabled="pending || Boolean(attempt)" @click="cancelDemand">{{ demandAttempt ? '重试取消未分配需求' : '确认取消未分配需求' }}</button>
    </details>
    <details v-if="demandHistory.length"><summary>未分配需求取消记录（{{ demandHistory.length }}）</summary><p v-for="item in demandHistory" :key="item.requestId">取消 {{ item.quantity }} · {{ item.actor }} · {{ item.reason }} · {{ new Date(item.occurredAt).toLocaleString('zh-CN', { hour12: false }) }}</p></details>
    <details v-if="lot" class="production-operation__history"><summary>完整操作记录（{{ history.length }}）</summary><ol><li v-for="item in history" :key="item.id"><strong>{{ item.operationName || productionMovementLabels[item.kind] || '流程办理' }} · {{ item.quantity }}</strong><p>{{ item.actorName || item.actor }}（{{ item.actor }}） · {{ new Date(item.occurredAt).toLocaleString('zh-CN', { hour12: false }) }}</p><p v-if="item.workstationName">{{ productionPosts[item.post || ''] }} · {{ item.workstationName }}{{ item.resourceGroup ? ` · ${item.resourceGroup}` : '' }}</p><p v-if="item.fromStage || item.toStage">{{ item.fromStage }} → {{ item.toStage }}</p><p v-if="item.recipient">实际领用人：{{ item.recipient }}</p><p v-if="item.note">{{ item.note }}</p><small>记录编号：{{ item.id }}</small></li></ol><p v-if="!history.length">暂无操作记录</p></details>
  </section>
</template>
<style scoped>
.production-operation{min-width:0;font-size:12px}.production-operation header{display:flex;align-items:center;justify-content:space-between;gap:5px}.production-operation label{display:grid;gap:5px;margin:9px 0}.production-operation fieldset{border:0;padding:0;margin:0;min-width:0}.production-operation input,.production-operation select{width:100%;min-width:0;height:32px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);padding:0 7px}.production-operation button{padding:6px 10px;border:1px solid var(--pdm-border);border-radius:5px;cursor:pointer}.production-operation p{line-height:1.5;overflow-wrap:anywhere}.production-operation__error{color:var(--pdm-danger)}.production-operation__controls{margin:12px 0;border-top:1px solid var(--pdm-border);padding-top:10px}.production-operation__controls>div{display:flex;gap:5px;flex-wrap:wrap}.production-operation__history{margin-top:12px;border-top:1px solid var(--pdm-border);padding-top:8px}.production-operation__history ol{padding-left:18px;max-height:450px;overflow:auto}.production-operation__history li{margin:10px 0}.production-operation__history small{overflow-wrap:anywhere;color:var(--pdm-muted)}.production-operation button:disabled{opacity:.6;cursor:not-allowed}
@media(max-width:700px){.production-operation{font-size:14px}.production-operation input,.production-operation select,.production-operation button{min-height:44px}}
</style>
