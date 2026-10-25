<script setup lang="ts">
import { vQuantity } from '../quantityInput'
import { computed, nextTick, ref, watch } from 'vue'
import type { resolveProductionRevision } from '../api'
import { movementTotal, productionRequestId, type ProductionLot, type ProductionReleaseState } from '../productionTracking'

const props = defineProps<{ state: ProductionReleaseState; lot: ProductionLot; unit: string; canResolve: boolean; pending: boolean }>()
type Decision = Parameters<typeof resolveProductionRevision>[1]
const emit = defineEmits<{ resolve: [input: Decision] }>()
const heading = ref<HTMLElement>()
const action = ref<Decision['action'] | ''>(''), reason = ref(''), quantity = ref(0), requestId = ref(productionRequestId())
const remaining = computed(() => props.lot.quantity - ['Issue', 'Cancelled', 'Transferred']
  .reduce((sum, kind) => sum + movementTotal(props.state, props.lot.id, kind), 0))
const valid = computed(() => props.canResolve && props.lot.frozen && !!props.lot.frozenByReleasePackageId
  && remaining.value > 0 && !!action.value && !!reason.value.trim()
  && (action.value !== 'CancelRemaining' || quantity.value > 0 && quantity.value <= remaining.value))
watch(() => props.lot.id, () => { action.value = ''; reason.value = ''; quantity.value = 0; requestId.value = productionRequestId() })
watch(action, value => { if (value === 'CancelRemaining') quantity.value = Math.max(0, remaining.value) })
function submit() {
  if (props.pending || !valid.value || !action.value || !props.lot.frozenByReleasePackageId) return
  emit('resolve', { lotId: props.lot.id, changedReleasePackageId: props.lot.frozenByReleasePackageId,
    action: action.value, reason: reason.value.trim(), requestId: requestId.value,
    quantity: action.value === 'CancelRemaining' ? quantity.value : undefined })
}
defineExpose({ focus: async () => { await nextTick(); heading.value?.focus(); heading.value?.scrollIntoView?.({ block: 'nearest' }) } })
</script>

<template>
  <section class="production-revision" aria-label="图纸变更处置">
    <h4 ref="heading" tabindex="-1">图纸／BOM变更待处理</h4>
    <p>{{ lot.freezeReason || '后续变更发布涉及该物料的升版、减量或移除，旧批次已冻结。' }}</p>
    <p>冻结期间不能排程或登记生产流转。请先核对变更内容，在此选择处置方式并填写原因，确认后解除本次变更冻结。</p>
    <p>待处置数量：<strong>{{ remaining }} {{ unit }}</strong>（扣除已领用、已取消、已转版数量）</p>
    <p v-if="!lot.frozenByReleasePackageId" role="status">缺少关联的变更发布包，暂不能处置。请先刷新；仍未恢复时请联系系统管理员核对变更记录。</p>
    <p v-else-if="!canResolve" role="status">当前账号没有变更处置权限。请联系拥有“分配非标件自制与外协”{{ lot.route === 'Self' ? '或“安排自制生产”' : '' }}权限的人员，在本页选中该批次办理；权限可由管理员在设置页面调整。</p>
    <form v-else @submit.prevent="submit">
      <fieldset :disabled="pending || remaining <= 0">
        <label>处置方式<select v-model="action" aria-label="处置方式" required><option disabled value="">请选择处置方式</option><option value="ContinueOld">确认继续旧版</option><option value="TransferNew">剩余数量转新版返工</option><option value="CancelRemaining">取消旧版剩余数量</option></select></label>
        <p v-if="action === 'ContinueOld'">保留旧版批次和已有排程，继续按旧版办理；旧版图档已作废时不可使用。</p>
        <p v-else-if="action === 'TransferNew'">将全部待处置数量转入新版发布包，结束旧批次剩余生产并释放对应排程。须先由供应链接收新版发布包；转入后到“发布跟踪”打开新版包，重新分配批次。</p>
        <p v-else-if="action === 'CancelRemaining'">仅取消本次填写的数量。全部取消时释放旧批次排程；部分取消时，其余数量保留在旧批次继续办理。</p>
        <label v-if="action === 'CancelRemaining'">本次取消数量<input v-quantity v-model.number="quantity" type="number" min="0.001" :max="remaining" step="any" required></label>
        <label>处置原因<input v-model="reason" placeholder="填写核对后的变更处置依据" required></label>
        <p>确认后记录操作人、时间、处置方式、数量及原因，请核对后提交。</p>
        <button type="submit" class="pdm-primary-action" :disabled="!valid">确认变更处置</button>
      </fieldset>
    </form>
  </section>
</template>

<style scoped>
.production-revision{margin:10px 0;padding:10px;border:1px solid var(--pdm-border);border-left:3px solid var(--pdm-blue);border-radius:5px;background:var(--pdm-surface)}
.production-revision h4{margin:0 0 8px;font-size:14px}.production-revision p{font-size:12px;line-height:1.6;overflow-wrap:anywhere}
.production-revision fieldset{margin:0;padding:0;border:0;min-width:0}.production-revision label{display:grid;gap:4px;margin:9px 0;font-size:12px}.production-revision input,.production-revision select{width:100%;min-width:0;height:28px;box-sizing:border-box;padding:3px 6px;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--pdm-text);font:inherit}
</style>
