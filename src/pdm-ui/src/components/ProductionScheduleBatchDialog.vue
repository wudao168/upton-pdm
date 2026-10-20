<script setup lang="ts">
import { computed, ref } from 'vue'
import { scheduleProductionLot } from '../api'
import { productionRequestId, type ProductionLine, type ProductionLot, type ProductionReleaseRecord, type ProductionScheduleSlot } from '../productionTracking'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ token: string; record: ProductionReleaseRecord; lots: ProductionLot[]; resources: string[]; date: string }>()
const emit = defineEmits<{ close: []; updated: [record: ProductionReleaseRecord, lotId: string]; completed: [count: number] }>()
type Row = { lot: ProductionLot; line?: ProductionLine; status: 'ready' | 'running' | 'done' | 'failed'; error: string; execute?: () => Promise<ProductionReleaseRecord> }
const rows = ref<Row[]>(props.lots.map(lot => ({ lot, line: props.record.state.lines.find(line => line.id === lot.lineId), status: 'ready', error: '' })))
const slots = ref<ProductionScheduleSlot[]>([{ resourceGroupId: props.resources[0] || 'CNC组', date: props.date, hours: 1 }])
const promisedOn = ref(props.date), reason = ref(''), pending = ref(false), started = ref(false)
const needsReason = rows.value.some(row => row.lot.schedule.length > 0)
const completed = computed(() => rows.value.filter(row => row.status === 'done').length)
async function submit() {
  if (pending.value) return
  if (!started.value) {
    if (!slots.value.length || slots.value.some(slot => !slot.resourceGroupId.trim() || !slot.date || !Number.isFinite(slot.hours) || slot.hours <= 0) || !promisedOn.value) {
      ElMessage.warning('请填写资源组、排产日期、正数工时和预计完成日期。'); return
    }
    if (needsReason && !reason.value.trim()) { ElMessage.warning('所选批次已有排程，请填写本次调整原因。'); return }
    if (rows.value.some(row => row.lot.route !== 'Self' || row.lot.frozen)) { ElMessage.warning('只能安排未冻结的自制批次，请重新选择。'); return }
    const releaseId = props.record.state.releasePackageId, token = props.token
    for (const row of rows.value) {
      const input = { lotId: row.lot.id, slots: slots.value.map(slot => ({ ...slot })), promisedOn: promisedOn.value,
        changeReason: reason.value, requestId: productionRequestId() }
      row.execute = () => scheduleProductionLot(releaseId, input, token)
    }
    started.value = true
  }
  pending.value = true
  try {
    for (const row of rows.value) {
      if (row.status === 'done' || !row.execute) continue
      row.status = 'running'; row.error = ''
      try {
        const record = await row.execute()
        row.status = 'done'; emit('updated', record, row.lot.id)
      } catch (cause) {
        console.error('批量排程失败', cause)
        row.status = 'failed'; row.error = userErrorMessage(cause instanceof Error ? cause.message : '排程保存失败，请重试。')
      }
    }
    const failed = rows.value.filter(row => row.status === 'failed').length
    if (failed) ElMessage.error(`已完成 ${completed.value} 个批次，${failed} 个未完成，请查看原因后重试。`)
    else emit('completed', completed.value)
  } finally { pending.value = false }
}
</script>

<template>
  <el-dialog :model-value="true" title="批量安排自制排程" width="min(940px, calc(100vw - 32px))" :close-on-click-modal="!pending" :close-on-press-escape="!pending" :show-close="!pending" @update:model-value="!$event && !pending && emit('close')">
    <form class="production-schedule-batch" novalidate @submit.prevent="submit">
      <p>{{ record.state.packageNumber }} · 已选 {{ rows.length }} 个批次</p>
      <p>以下安排应用到每个批次，工时按批次分别计入产能；已有排程将被替换。</p>
      <fieldset :disabled="pending || started">
        <div v-for="(slot, index) in slots" :key="index" class="production-schedule-batch__slot">
          <label>资源组<select v-model="slot.resourceGroupId" aria-label="批量资源组"><option v-for="name in resources" :key="name">{{ name }}</option></select></label>
          <label>排产日期<input v-model="slot.date" type="date" required aria-label="批量排产日期"></label>
          <label>每批次工时（小时）<input v-model.number="slot.hours" type="number" min="0.1" step="0.1" required aria-label="每批次工时"></label>
          <el-button :disabled="slots.length === 1" @click="slots.splice(index, 1)">删除</el-button>
        </div>
        <el-button @click="slots.push({ resourceGroupId: resources[0] || 'CNC组', date, hours: 1 })">增加日期／资源</el-button>
        <div class="production-schedule-batch__fields"><label>预计完成日期<input v-model="promisedOn" type="date" required></label><label>调整原因<input v-model="reason" placeholder="填写批量排程调整原因" :required="needsReason"></label></div>
      </fieldset>
      <div class="production-schedule-batch__table"><table class="pdm-fit-columns"><thead><tr><th>图号／名称</th><th>版次</th><th>批次数量</th><th>原排程</th><th>处理结果</th></tr></thead><tbody><tr v-for="row in rows" :key="row.lot.id"><td><strong>{{ row.line?.drawingNumber }}</strong><small>{{ row.line?.name }}</small></td><td>{{ row.line?.revision || '—' }}</td><td>{{ row.lot.quantity }}</td><td>{{ row.lot.schedule.length ? '已排产' : '待排产' }}</td><td :class="{ 'is-error': row.status === 'failed' }">{{ row.status === 'done' ? '已完成' : row.status === 'running' ? '正在处理' : row.error || '待处理' }}</td></tr></tbody></table></div>
      <footer><span v-if="started">已完成 {{ completed }}／{{ rows.length }} 个批次</span><el-button :disabled="pending" @click="emit('close')">关闭</el-button><el-button native-type="submit" type="primary" :loading="pending">{{ started ? '重试未完成项' : '确认批量排程' }}</el-button></footer>
    </form>
  </el-dialog>
</template>

<style scoped>
.production-schedule-batch{display:flex;flex-direction:column;gap:12px;min-height:0;font-size:12px}.production-schedule-batch p{margin:0;color:var(--pdm-muted)}.production-schedule-batch fieldset{min-width:0;margin:0;padding:0;border:0}.production-schedule-batch label{display:grid;min-width:0;gap:5px}.production-schedule-batch input,.production-schedule-batch select{width:100%;min-width:0;height:30px;padding:0 8px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);background:var(--pdm-surface);color:inherit;font:inherit}.production-schedule-batch__slot{display:grid;grid-template-columns:1fr 1fr 1fr auto;align-items:end;gap:10px;margin-bottom:10px}.production-schedule-batch__fields{display:grid;grid-template-columns:1fr 2fr;gap:12px;margin-top:12px}.production-schedule-batch__table{max-height:40vh;overflow:auto}.production-schedule-batch table{width:100%;border-collapse:collapse}.production-schedule-batch th{position:sticky;top:0;background:var(--pdm-table-header)}.production-schedule-batch td,.production-schedule-batch th{text-align:center;vertical-align:middle;padding:8px;border-bottom:1px solid var(--pdm-border)}.production-schedule-batch small{display:block;color:var(--pdm-muted)}.production-schedule-batch .is-error{color:var(--pdm-danger);overflow-wrap:anywhere}.production-schedule-batch footer{display:flex;align-items:center;justify-content:flex-end;gap:10px}.production-schedule-batch footer span{margin-right:auto;color:var(--pdm-muted)}@media(max-width:700px){.production-schedule-batch__slot,.production-schedule-batch__fields{grid-template-columns:1fr 1fr}.production-schedule-batch footer{flex-wrap:wrap}}
</style>
