<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { PdmApiError, readProductionActions, readProductionTrackingRelease, recordProductionMovement } from '../api'
import { lotStage, productionMovementLabels, productionPosts, productionRequestId, type ProductionMovementKind, type ProductionReleaseRecord, type ProductionScanContext } from '../productionTracking'
import { userErrorMessage } from '../userErrorMessage'
const props = defineProps<{ token: string; username: string; record: ProductionReleaseRecord; lineId: string; preferredLotId?: string }>()
const emit = defineEmits<{ updated: [record: ProductionReleaseRecord] }>()
const context = ref<ProductionScanContext>({ actions: [], hint: '' }), lotId = ref(''), kind = ref<ProductionMovementKind>(), workstationId = ref('')
const quantity = ref(1), recipient = ref(''), note = ref(''), loading = ref(false), pending = ref(false), error = ref(''), notice = ref('')
type Input = Parameters<typeof recordProductionMovement>[1]
const attempt = ref<Input>()
const storageKey = computed(() => `pdm-production-pending-${props.username}-${props.record.state.releasePackageId}`)
const lots = computed(() => props.record.state.lots.filter(lot => lot.lineId === props.lineId))
const lot = computed(() => lots.value.find(item => item.id === lotId.value))
const options = computed(() => context.value.actions.filter(item => item.lotId === lotId.value))
const kinds = computed(() => [...new Map(options.value.map(item => [item.kind, item.label])).entries()])
const stations = computed(() => options.value.filter(item => item.kind === kind.value))
const action = computed(() => stations.value.find(item => item.workstationId === workstationId.value))
const history = computed(() => props.record.state.movements.filter(item => item.lotId === lotId.value))
const pendingDescription = computed(() => {
  const pendingLot = props.record.state.lots.find(item => item.id === attempt.value?.lotId)
  const line = props.record.state.lines.find(item => item.id === pendingLot?.lineId)
  return `${line?.drawingNumber || ''} · ${line?.name || ''} · 批次${attempt.value?.lotId.slice(0, 8)} · ${productionMovementLabels[attempt.value?.kind || ''] || ''} ${attempt.value?.quantity ?? ''}`
})
let generation = 0
onBeforeUnmount(() => { generation++ })
function storeAttempt(value?: Input) { attempt.value = value; try { if (value) sessionStorage.setItem(storageKey.value, JSON.stringify(value)); else sessionStorage.removeItem(storageKey.value) } catch { /* 保留当前页面内的重试状态。 */ } }
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
  try { const saved = sessionStorage.getItem(storageKey.value); if (saved) attempt.value = JSON.parse(saved) as Input } catch { /* 不使用不可读的本地重试数据。 */ }
  if (attempt.value) lotId.value = attempt.value.lotId
  void load()
}, { immediate: true })
watch(() => props.record.rowVersion, () => { void load() })
watch(kinds, values => { if (!values.some(([value]) => value === kind.value)) kind.value = values[0]?.[0] })
watch(stations, values => { if (!values.some(item => item.workstationId === workstationId.value)) workstationId.value = values.length === 1 ? values[0]!.workstationId : '' })
watch(action, value => { if (value && !attempt.value) quantity.value = value.remainingQuantity })
async function refresh() {
  if (pending.value) return
  try {
    const value = await readProductionTrackingRelease(props.record.state.releasePackageId, props.token)
    if (attempt.value && value.state.movements.some(item => item.requestId === attempt.value!.requestId && item.actor === props.username)) { storeAttempt(); notice.value = '已核实上次操作保存成功。' }
    emit('updated', value); await load()
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
}
async function save() {
  if (pending.value) return
  if (!attempt.value) {
    if (!action.value || quantity.value <= 0 || quantity.value > action.value.remainingQuantity) { error.value = '请选择可办理步骤和工位，并填写不超过可办理数量的正数。'; return }
    if ((kind.value === 'Rework' || kind.value === 'Scrap') && !note.value.trim()) { error.value = '返工或报废必须填写原因。'; return }
    if (kind.value === 'Issue' && !recipient.value.trim()) { error.value = '请填写实际领用人。'; return }
    storeAttempt({ lotId: lotId.value, kind: kind.value!, workstationId: workstationId.value, quantity: quantity.value,
      requestId: productionRequestId(), note: note.value.trim(), recipient: kind.value === 'Issue' ? recipient.value.trim() : undefined })
  }
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const value = await recordProductionMovement(props.record.state.releasePackageId, attempt.value!, props.token)
    const label = productionMovementLabels[attempt.value!.kind]
    storeAttempt(); note.value = ''; emit('updated', value); notice.value = `${label}已确认并保存操作记录。`; await load()
  } catch (cause) {
    const message = userErrorMessage(cause instanceof Error ? cause.message : '')
    if (cause instanceof PdmApiError && cause.status >= 400 && cause.status < 500) { storeAttempt(); await load() }
    error.value = message
  }
  finally { pending.value = false }
}
</script>
<template>
  <section class="production-operation" aria-label="当前可办理步骤">
    <header><strong>本岗扫码办理</strong><button type="button" :disabled="loading || pending" @click="refresh">刷新步骤</button></header>
    <p v-if="error" role="alert" class="production-operation__error">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="loading" role="status">正在核对岗位、工位和当前可办理数量…</p>
    <label v-if="lots.length > 1">本次生产批次<select v-model="lotId" :disabled="pending || Boolean(attempt)"><option value="">请选择批次，不默认第一批</option><option v-for="item in lots" :key="item.id" :value="item.id">{{ item.route === 'Self' ? '自制' : '外协' }} · {{ item.quantity }} · {{ lotStage(record.state, item) }} · {{ item.id.slice(0, 8) }}</option></select></label>
    <p v-if="lot">当前环节：{{ lotStage(record.state, lot) }}</p>
    <p v-if="!loading && !options.length">{{ !context.actions.length ? context.hint : lotId ? '该批次暂无本岗位可办理的步骤，请核对前序交接及本岗权限。' : '请选择本次实际办理的生产批次。' }}</p>
    <form v-if="options.length || attempt" @submit.prevent="save">
      <fieldset :disabled="pending || loading || Boolean(attempt)">
        <label>本次步骤<select v-model="kind"><option v-for="[value, label] in kinds" :key="value" :value="value">{{ label }}</option></select></label>
        <label>办理工位<select v-model="workstationId"><option value="">请选择工位</option><option v-for="item in stations" :key="item.workstationId" :value="item.workstationId">{{ item.workstationName }}{{ item.resourceGroup ? ` · ${item.resourceGroup}` : '' }}</option></select></label>
        <p v-if="action">本岗位本工位可办理 {{ action.remainingQuantity }}；岗位：{{ productionPosts[action.post] }}</p>
        <label>确认本次数量<input v-model.number="quantity" type="number" min="0.001" step="any" :max="action?.remainingQuantity" required></label>
        <label v-if="kind === 'Issue'">实际领用人<input v-model="recipient" required></label>
        <label v-if="kind === 'Rework' || kind === 'Scrap'">处理原因<input v-model="note" required></label>
      </fieldset>
      <p v-if="attempt">待核实上次操作：{{ pendingDescription }}。重试将沿用同一操作编号和数量，避免重复登记。</p>
      <button type="submit" class="pdm-primary-action" :disabled="pending || loading || !attempt && !action">{{ pending ? '正在确认…' : attempt ? '重试确认结果' : '确认数量并推进' }}</button>
    </form>
    <details v-if="lot" class="production-operation__history"><summary>完整操作记录（{{ history.length }}）</summary><ol><li v-for="item in history" :key="item.id"><strong>{{ productionMovementLabels[item.kind] || item.kind }} · {{ item.quantity }}</strong><p>{{ item.actorName || item.actor }}（{{ item.actor }}） · {{ new Date(item.occurredAt).toLocaleString('zh-CN', { hour12: false }) }}</p><p v-if="item.workstationName">{{ productionPosts[item.post || ''] }} · {{ item.workstationName }}{{ item.resourceGroup ? ` · ${item.resourceGroup}` : '' }}</p><p v-if="item.fromStage || item.toStage">{{ item.fromStage }} → {{ item.toStage }}</p><p v-if="item.recipient">实际领用人：{{ item.recipient }}</p><p v-if="item.note">{{ item.note }}</p><small>记录编号：{{ item.id }}</small></li></ol><p v-if="!history.length">暂无操作记录</p></details>
  </section>
</template>
<style scoped>
.production-operation{min-width:0;font-size:12px}.production-operation header{display:flex;align-items:center;justify-content:space-between;gap:5px}.production-operation label{display:grid;gap:5px;margin:9px 0}.production-operation fieldset{border:0;padding:0;margin:0;min-width:0}.production-operation input,.production-operation select{width:100%;min-width:0;height:32px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);padding:0 7px}.production-operation button{padding:6px 10px;border:1px solid var(--pdm-border);border-radius:5px;cursor:pointer}.production-operation p{line-height:1.5;overflow-wrap:anywhere}.production-operation__error{color:var(--pdm-danger)}.production-operation__history{margin-top:12px;border-top:1px solid var(--pdm-border);padding-top:8px}.production-operation__history ol{padding-left:18px;max-height:450px;overflow:auto}.production-operation__history li{margin:10px 0}.production-operation__history small{overflow-wrap:anywhere;color:var(--pdm-muted)}.production-operation button:disabled{opacity:.6;cursor:not-allowed}
@media(max-width:700px){.production-operation{font-size:14px}.production-operation input,.production-operation select,.production-operation button{min-height:44px}}
</style>
