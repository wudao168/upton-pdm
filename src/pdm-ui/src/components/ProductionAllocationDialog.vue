<script setup lang="ts">
import { vQuantity } from '../quantityInput'
import { computed, ref } from 'vue'
import ProductionSupplierPicker from './ProductionSupplierPicker.vue'
import { updateProductionLotAllocation } from '../api'
import { allocationChangeBlockReason, movementTotal, productionRequestId,
  type ProductionLot, type ProductionReleaseRecord, type ProductionRoute, type UpdateProductionAllocation } from '../productionTracking'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ token: string; record: ProductionReleaseRecord; lot: ProductionLot }>()
const emit = defineEmits<{ close: []; updated: [record: ProductionReleaseRecord] }>()
const initial = props.lot, state = props.record.state, version = props.record.rowVersion
const line = state.lines.find(item => item.id === initial.lineId)!
const route = ref<ProductionRoute>(initial.route), quantity = ref(initial.quantity)
const supplierId = ref(initial.supplierId || ''), supplier = ref(initial.supplier || '')
const assignee = ref(initial.assignee || ''), promisedOn = ref(initial.promisedOn || ''), reason = ref('')
const pending = ref(false), error = ref(''), submitted = ref(false)
const maximum = line.requiredQuantity + state.lots.filter(item => item.lineId === line.id)
  .reduce((total, lot) => total + movementTotal(state, lot.id, 'Scrap') - (lot.id === initial.id ? 0 : lot.quantity), 0)
const clearsSchedule = computed(() => route.value !== initial.route || quantity.value !== initial.quantity)
let input: UpdateProductionAllocation | undefined
async function save() {
  if (pending.value) return
  if (!input) {
    const blocked = allocationChangeBlockReason(state, initial)
    if (blocked) { ElMessage.warning(blocked); return }
    if (!Number.isFinite(quantity.value) || quantity.value <= 0 || quantity.value > maximum) { ElMessage.warning(`分配数量须大于零且不超过 ${maximum}。`); return }
    if (route.value === 'External' && (!supplierId.value || !promisedOn.value)) { ElMessage.warning('外协分配必须选择供应商并填写承诺日期。'); return }
    if (!clearsSchedule.value && initial.schedule.length && (!promisedOn.value || initial.schedule.some(slot => slot.date > promisedOn.value))) {
      ElMessage.warning('保留原排程时，预计完成日期不能为空或早于最后一个排产日期。'); return
    }
    input = { lotId: initial.id, route: route.value, quantity: quantity.value, supplier: route.value === 'External' ? supplier.value : '',
      supplierId: route.value === 'External' ? supplierId.value : null, assignee: assignee.value, promisedOn: promisedOn.value || null,
      reason: reason.value.trim(), expectedRowVersion: version, requestId: productionRequestId() }
    submitted.value = true
  }
  pending.value = true; error.value = ''
  try { emit('updated', await updateProductionLotAllocation(state.releasePackageId, input, props.token)) }
  catch (cause) { console.error('生产分配修改失败', cause); error.value = userErrorMessage(cause instanceof Error ? cause.message : '分配修改失败，请重试。') }
  finally { pending.value = false }
}
</script>

<template>
  <el-dialog :model-value="true" title="修改批次分配" width="min(680px, calc(100vw - 32px))" :show-close="!pending" :close-on-click-modal="!pending" :close-on-press-escape="!pending" @update:model-value="!$event && !pending && emit('close')">
    <form class="production-allocation" novalidate @submit.prevent="save">
      <p>{{ state.packageNumber }} · {{ line.drawingNumber }} · {{ line.name }}</p><p>修改当前批次，保留批次编号和历史记录。加工、交付或后续流转开始后不能修改。</p>
      <fieldset :disabled="pending || submitted"><label>分配方式<select v-model="route" aria-label="修改分配方式"><option value="Self">自制</option><option value="External">外协</option></select></label><label>分配数量<input v-quantity v-model.number="quantity" type="number" min="0.001" step="any" :max="maximum" aria-label="修改分配数量"></label>
        <ProductionSupplierPicker v-if="route === 'External'" v-model="supplierId" :token="token" :disabled="pending || submitted" @selected="supplier = $event?.name ?? ''" />
        <label>负责人（选填）<input v-model="assignee" aria-label="修改分配负责人"></label><label>{{ route === 'External' ? '承诺日期' : '预计完成日期' }}<input v-model="promisedOn" type="date" aria-label="修改分配承诺日期"></label><label class="production-allocation__reason">调整原因（选填）<input v-model="reason" placeholder="填写分配调整原因"></label>
      </fieldset>
      <p v-if="initial.schedule.length">{{ clearsSchedule ? '方式或数量发生变化，保存后将清空原排程并释放工时；自制批次需要重新排产。' : '当前方式和数量未变化，保存后保留原排程。' }}</p>
      <p v-if="error" role="alert">{{ error }}</p><p v-if="submitted && error">重试将核对同一次保存结果；如需更改内容，请关闭表单并刷新后重新打开。</p>
      <footer><el-button :disabled="pending" @click="emit('close')">关闭</el-button><el-button native-type="submit" type="primary" :loading="pending">{{ submitted ? '重试保存' : '保存分配修改' }}</el-button></footer>
    </form>
  </el-dialog>
</template>

<style scoped>
.production-allocation{display:grid;gap:12px;font-size:12px}.production-allocation p{margin:0;color:var(--pdm-muted)}.production-allocation fieldset{display:grid;grid-template-columns:1fr 1fr;align-items:start;gap:12px;border:0;padding:0;margin:0;min-width:0}.production-allocation label{display:grid;align-content:start;min-width:0;gap:5px}.production-allocation input,.production-allocation select{height:30px;min-width:0;width:100%;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);padding:0 8px;background:var(--pdm-surface);color:inherit;font:inherit}.production-allocation__reason,.production-allocation :deep(.production-supplier-picker){grid-column:1/-1}.production-allocation [role=alert]{color:var(--pdm-danger)}.production-allocation footer{display:flex;justify-content:flex-end;gap:10px}@media(max-width:600px){.production-allocation fieldset{grid-template-columns:1fr}}
</style>
