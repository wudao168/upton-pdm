<script setup lang="ts">
import { computed, ref } from 'vue'
import { batchAllocateProduction, updateProductionLineDelivery } from '../api'
import { allocationChangeBlockReason, effectiveDemand, productionPriority, productionRequestId,
  type BatchProductionAllocation, type ProductionLine, type ProductionReleaseRecord, type ProductionRoute } from '../productionTracking'
import { ElMessage } from '../statusMessage'
import ProductionSupplierPicker from './ProductionSupplierPicker.vue'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ token: string; mode: 'delivery' | 'assign'; record: ProductionReleaseRecord; lines: ProductionLine[] }>()
const emit = defineEmits<{ close: []; updated: [record: ProductionReleaseRecord, lineId: string]; completed: [count: number] }>()
type BatchRow = { line: ProductionLine; assigned: number; quantity: number; blocked: string; status: 'ready' | 'running' | 'done' | 'failed'; error: string; execute?: () => Promise<ProductionReleaseRecord> }
const rows = ref<BatchRow[]>(props.lines.map(line => {
  const lots = props.record.state.lots.filter(lot => lot.lineId === line.id)
  const assigned = lots.reduce((total, lot) => total + lot.quantity, 0)
  const demand = effectiveDemand(props.record.state, line)
  const blocked = lots.map(lot => allocationChangeBlockReason(props.record.state, lot)).find(Boolean)
    || (assigned > demand ? '现有分配超过有效需求，请先核对。' : '')
  return { line, assigned, quantity: demand, blocked, status: 'ready', error: '' }
}))
const eligibleRows = computed(() => rows.value.filter(row => row.quantity > 0))
const blockedRows = computed(() => eligibleRows.value.some(row => row.blocked))
let allocationInput: BatchProductionAllocation | undefined
const priority = ref(''), requiredOn = ref(''), reason = ref('')
const route = ref<ProductionRoute>('Self'), supplier = ref(''), supplierId = ref(''), assignee = ref(''), promisedOn = ref('')
const pending = ref(false), started = ref(false)
const title = computed(() => props.mode === 'delivery' ? '批量调整紧急程度／需求日期' : '批量分配自制／外协')
const completedCount = computed(() => rows.value.filter(row => row.status === 'done').length)
const taskCount = computed(() => props.mode === 'assign' ? eligibleRows.value.length : rows.value.filter(row => row.execute).length)
const actionLabel = computed(() => pending.value ? `正在处理 ${completedCount.value}／${taskCount.value} 项`
  : started.value ? '重试未完成项' : props.mode === 'delivery' ? '确认批量调整' : '确认批量分配')

function close() { if (!pending.value) emit('close') }
async function submit() {
  if (pending.value) return
  if (!started.value) {
    if (props.mode === 'delivery') {
      if (!priority.value && !requiredOn.value) { ElMessage.warning('请至少选择一项要调整的紧急程度或需求日期。'); return }
      if (!reason.value.trim()) { ElMessage.warning('请填写批量调整原因。'); return }
    } else {
      if (route.value === 'External' && (!supplierId.value || !promisedOn.value)) {
        ElMessage.warning('批量外协分配必须填写供应商和承诺日期。'); return
      }
      if (blockedRows.value) { ElMessage.warning('所选物料包含不能修改的批次，请取消勾选对应物料后重试。'); return }
      if (!eligibleRows.value.length) { ElMessage.warning('所选物料没有需要分配的有效需求。'); return }
      allocationInput = { lineIds: eligibleRows.value.map(row => row.line.id), route: route.value,
        supplier: supplier.value.trim(), supplierId: route.value === 'External' ? supplierId.value : null,
        assignee: assignee.value.trim(), promisedOn: promisedOn.value || null, reason: reason.value.trim(),
        requestId: productionRequestId(), expectedRowVersion: props.record.rowVersion }
    }
    const releaseId = props.record.state.releasePackageId, token = props.token
    for (const row of rows.value) {
      if (props.mode === 'delivery') {
        const input = { lineId: row.line.id, priority: priority.value || row.line.priority,
          requiredOn: requiredOn.value || row.line.requiredOn || null, reason: reason.value.trim(), requestId: productionRequestId() }
        row.execute = () => updateProductionLineDelivery(releaseId, input, token)
      }
    }
    started.value = true
  }
  pending.value = true
  try {
    if (props.mode === 'assign' && allocationInput) {
      for (const row of eligibleRows.value) { row.status = 'running'; row.error = '' }
      try {
        const updated = await batchAllocateProduction(props.record.state.releasePackageId, allocationInput, props.token)
        for (const row of eligibleRows.value) { row.status = 'done'; emit('updated', updated, row.line.id) }
        emit('completed', eligibleRows.value.length)
      } catch (cause) {
        console.error('生产批量操作失败', cause)
        const message = userErrorMessage(cause instanceof Error ? cause.message : '')
        for (const row of eligibleRows.value) { row.status = 'failed'; row.error = message }
        ElMessage.error(message)
      }
      return
    }
    for (const row of rows.value) {
      if (!row.execute || row.status === 'done') continue
      row.status = 'running'; row.error = ''
      try {
        const updated = await row.execute()
        row.status = 'done'
        emit('updated', updated, row.line.id)
      } catch (cause) {
        console.error('生产批量操作失败', cause)
        row.status = 'failed'; row.error = userErrorMessage(cause instanceof Error ? cause.message : '')
      }
    }
    const failed = rows.value.filter(row => row.status === 'failed').length
    if (failed) ElMessage.error(`已完成 ${completedCount.value} 项，${failed} 项未完成，请查看列表原因后重试。`)
    else emit('completed', completedCount.value)
  } finally { pending.value = false }
}
</script>

<template>
  <div class="production-batch__backdrop" @click.self="close">
    <section class="production-batch pdm-panel" role="dialog" aria-modal="true" :aria-label="title">
      <header><h2>{{ title }}</h2><button type="button" aria-label="关闭批量操作" :disabled="pending" @click="close">×</button></header>
      <p>{{ record.state.packageNumber }} · 已选 {{ rows.length }} 项</p>
      <form novalidate @submit.prevent="submit">
        <fieldset :disabled="pending || started" class="production-batch__fields">
          <template v-if="mode === 'delivery'">
            <label>紧急程度<select v-model="priority"><option value="">保留各自紧急程度</option><option value="Normal">普通</option><option value="Priority">加急</option><option value="Urgent">紧急</option></select></label>
            <label>需求日期<input v-model="requiredOn" type="date"><small>留空保留各自需求日期</small></label>
            <label class="production-batch__reason">调整原因<input v-model="reason" placeholder="填写本次批量调整的原因"></label>
          </template>
          <template v-else>
            <label>分配方式<select v-model="route" aria-label="分配方式"><option value="Self">自制</option><option value="External">外协</option></select></label>
            <ProductionSupplierPicker v-if="route === 'External'" v-model="supplierId" :token="token" :disabled="pending || started" @selected="supplier = $event?.name ?? ''" />
            <label>负责人（选填）<input v-model="assignee" placeholder="留空保留原负责人"></label>
            <label>承诺日期<input v-model="promisedOn" type="date"></label>
            <label class="production-batch__reason">调整原因（选填）<input v-model="reason" placeholder="填写本次分配调整原因"></label>
          </template>
        </fieldset>
        <p v-if="mode === 'assign'">数量固定随发布包BOM，不可编辑。已有未开工批次统一修改方式并保留编号及拆分数量，未分配数量自动补齐。负责人和自制承诺日期留空时保留原值。</p>
        <p v-if="mode === 'assign' && blockedRows" class="production-batch__error">存在不可修改项，请关闭并取消勾选对应物料后重试；整批不会保存。</p>
        <div class="production-batch__table"><table class="pdm-fit-columns">
          <thead><tr><th>图号／名称</th><template v-if="mode === 'delivery'"><th>当前紧急程度</th><th>当前需求日期</th></template><template v-else><th>有效需求</th><th>当前已分配</th></template><th>处理结果</th></tr></thead>
          <tbody><tr v-for="row in rows" :key="row.line.id"><td :title="`${row.line.drawingNumber} · ${row.line.name}`"><strong v-stocked-material="row.line.materialCode">{{ row.line.drawingNumber }}</strong><small v-stocked-material="row.line.materialCode">{{ row.line.name }}</small></td><template v-if="mode === 'delivery'"><td>{{ productionPriority(row.line.priority) }}</td><td>{{ row.line.requiredOn || '待确认' }}</td></template><template v-else><td>{{ row.quantity }}</td><td>{{ row.assigned }}</td></template><td :class="{ 'production-batch__error': row.status === 'failed' || mode === 'assign' && row.blocked }">{{ row.status === 'done' ? '已完成' : row.status === 'running' ? '正在处理' : row.error || (mode === 'assign' ? row.quantity === 0 ? '跳过：有效需求为零' : row.blocked || '待处理' : '待处理') }}</td></tr></tbody>
        </table></div>
        <footer><span v-if="started">已完成 {{ completedCount }}／{{ taskCount }} 项</span><button type="button" class="pdm-secondary-action" :disabled="pending" @click="close">关闭</button><button type="submit" class="pdm-primary-action" :disabled="pending || mode === 'assign' && blockedRows">{{ actionLabel }}</button></footer>
      </form>
    </section>
  </div>
</template>

<style scoped>
.production-batch__backdrop{position:fixed;inset:0;z-index:2500;background:var(--pdm-overlay);display:grid;place-items:center;padding:16px}
.production-batch{width:min(900px,100%);max-height:calc(100vh - 32px);min-height:0;display:flex;flex-direction:column;padding:18px;box-sizing:border-box;color:var(--pdm-text)}
.production-batch header,.production-batch footer{display:flex;align-items:center;gap:10px}.production-batch header{justify-content:space-between}.production-batch h2{font-size:16px;margin:0}.production-batch header button{border:0;background:none;font-size:24px;color:inherit;cursor:pointer}
.production-batch p{margin:10px 0;color:var(--pdm-muted);font-size:12px}.production-batch form{display:flex;flex-direction:column;min-height:0}.production-batch__fields{display:flex;flex-wrap:wrap;align-items:start;gap:12px;border:0;padding:0;margin:0 0 12px}.production-batch label{display:grid;gap:5px;font-size:12px}.production-batch__reason{flex:1;min-width:200px}.production-batch small{display:block;color:var(--pdm-muted);font-size:11px}
.production-batch input,.production-batch select{height:30px;min-width:0;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);padding:0 8px;background:var(--pdm-surface);color:inherit;font:inherit}.production-batch input[type=number]{width:100%;text-align:center}.production-batch__table{overflow:auto;min-height:0}.production-batch table{width:100%;border-collapse:collapse;font-size:12px}.production-batch th{position:sticky;top:0;background:var(--pdm-table-header)}.production-batch td,.production-batch th{padding:8px;border-bottom:1px solid var(--pdm-border);text-align:center;vertical-align:middle}.production-batch td:first-child{white-space:nowrap}.production-batch td:first-child small{overflow:hidden;text-overflow:ellipsis}.production-batch__error{color:var(--pdm-danger);overflow-wrap:anywhere}.production-batch footer{justify-content:flex-end;padding-top:14px;flex:none}.production-batch footer span{margin-right:auto;color:var(--pdm-muted);font-size:12px}
@media(max-width:600px){.production-batch{padding:12px}.production-batch__fields>label{flex:1 1 140px}.production-batch__fields input,.production-batch__fields select{width:100%}}
</style>
