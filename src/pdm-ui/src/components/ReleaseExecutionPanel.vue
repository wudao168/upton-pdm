<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { acknowledgeReleaseDepartment, readReleaseExecution, retryReleaseNotification } from '../api'
import { notificationChannels, notificationEvents, type ReleaseExecutionView, type ReleaseChannelDelivery } from '../releaseDelivery'
import { userErrorMessage } from '../userErrorMessage'
const props = defineProps<{ packageId: string; token: string; canManage: boolean }>()
const view = ref<ReleaseExecutionView | null>(null), error = ref(''), pending = ref(false)
const notes = ref<Record<string, string>>({}), retryConfirmation = ref<Record<string, boolean>>({})
const labels: Record<string, string> = { Pending: '待处理', Sending: '发送中', Running: '处理中', Succeeded: '已完成', Failed: '未完成', Unknown: '结果待核对', Published: '已发布', Draft: '草稿', ProcessReview: '审核中', Approval: '审批中', Publishing: '发布处理中', PublishFailed: '发布失败', Rejected: '已退回', None: '待转换' }
const progress = computed(() => view.value?.progress)
const workflowNotices = computed(() => view.value?.workflowNotifications?.filter(item => item.enabled) ?? [])
const notificationStatus = computed(() => {
  if (workflowNotices.value.some(item => item.state === 'Attention')) return '部门助理通知待处理'
  if (workflowNotices.value.some(item => !['Sent', 'Cancelled'].includes(item.state))) return '部门助理通知待发送'
  if (workflowNotices.value.some(item => item.state === 'Sent') && !progress.value?.notificationRules.length) return '已发送部门助理站内消息'
  if (!progress.value) return '发布后生成'
  if (!progress.value.notificationRules.length) return '未配置通知'
  if (progress.value.departments.some(item => !item.recipients.length)) return '部分部门无可接收人员'
  if (progress.value.deliveries.some(item => item.state === 'Unknown')) return '发送结果待核对'
  if (progress.value.deliveries.some(item => item.state === 'Failed')) return '部分发送失败'
  if (progress.value.deliveries.some(item => item.state !== 'Succeeded')) return '待发送'
  return progress.value.deliveries.length ? '已发送，请查看部门回执' : '等待通知时机'
})
const u9Status = computed(() => !progress.value ? '发布后处理' : Object.values(progress.value.u9).length > 0 && Object.values(progress.value.u9).every(item => item.state === 'Succeeded') ? '已回查确认' : Object.values(progress.value.u9).some(item => item.state === 'Failed') ? '同步未完成' : '等待同步回查')
let revision = 0
async function load() {
  const current = ++revision
  try { const result = await readReleaseExecution(props.packageId, props.token); if (current === revision) { view.value = result; error.value = '' } }
  catch (cause) { if (current === revision) error.value = userErrorMessage(cause instanceof Error ? cause.message : '发布执行状态读取失败') }
}
watch(() => [props.packageId, props.token], () => { view.value = null; notes.value = {}; retryConfirmation.value = {}; void load() }, { immediate: true })
const timer = setInterval(() => { if (!pending.value && view.value?.state === 'Published') void load() }, 15000)
onBeforeUnmount(() => { revision++; clearInterval(timer) })
async function acknowledge(id: string, executed: boolean) {
  pending.value = true; error.value = ''
  try { await acknowledgeReleaseDepartment(props.packageId, id, executed, notes.value[id] ?? '', props.token); await load() }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '回执保存失败') }
  finally { pending.value = false }
}
async function retry(item: ReleaseChannelDelivery) {
  pending.value = true; error.value = ''
  try { await retryReleaseNotification(props.packageId, item.id, retryConfirmation.value[item.id] === true, props.token); await load() }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '通知重试失败') }
  finally { pending.value = false }
}
const eventLabel = (event: string) => notificationEvents[event as keyof typeof notificationEvents] ?? event
</script>

<template>
  <details class="release-execution" open aria-label="发布执行与部门回执">
    <summary>发布执行与部门回执</summary>
    <p v-if="error" role="alert" class="execution-error">{{ error }} <button type="button" @click="load">重新读取</button></p>
    <template v-if="view">
      <div class="execution-statuses">
        <div><span>发布</span><strong>{{ labels[view.state] ?? view.state }}</strong></div><div><span>U9同步</span><strong>{{ u9Status }}</strong></div>
        <div><span>生产图纸</span><strong>{{ !progress ? '发布后处理' : !progress.previewRequired ? '本次不需转图' : labels[progress.previewState] ?? progress.previewState }}</strong></div>
        <div><span>部门通知</span><strong>{{ notificationStatus }}</strong></div>
      </div>
      <details v-if="view.versionBindings?.length"><summary>本次固定的图纸版本（{{ view.versionBindings.filter(item => item.publish).length }} 项发布，其余仅用于完整打开）</summary><div class="execution-table"><table><thead><tr><th>图档</th><th>上一正式版</th><th>本次源版本 / 发布版</th><th>本次处理</th></tr></thead><tbody><tr v-for="item in view.versionBindings" :key="item.documentId"><td>{{ item.fileName || item.documentId }}</td><td>{{ item.previousReleasedRevision || '—' }}</td><td>{{ item.revision }}{{ item.targetRevision ? ` / ${item.targetRevision}` : '' }}</td><td>{{ item.publish ? item.requiresPromotion === false ? '复用已审核正式图纸' : '发布该版本' : '保留引用，不随本次升版' }}</td></tr></tbody></table></div></details>
      <p v-if="view.confirmedRemovedBomItemIds.length" class="execution-hint">本次已明确确认删除 {{ view.confirmedRemovedBomItemIds.length }} 项；未确认的删除项保留上一正式值。</p>
      <div v-if="progress?.departments.length" class="execution-table"><table><thead><tr><th>通知部门 / 事件</th><th>接收情况</th><th>执行情况</th><th>部门确认</th></tr></thead><tbody>
        <tr v-for="item in progress.departments" :key="item.id"><td>{{ item.departmentName }}<small>{{ eventLabel(item.event) }}</small></td><td>{{ !item.recipients.length ? '无具有项目权限的接收人员' : item.receivedAt ? `已接收 · ${item.receivedBy}` : '待部门确认接收' }}</td><td>{{ item.executedAt ? `已执行 · ${item.executedBy}` : item.requireExecution ? '待执行确认' : '不要求执行回执' }}<small v-if="item.executionNote">{{ item.executionNote }}</small></td><td>
          <template v-if="view.acknowledgementIds.includes(item.id)"><button v-if="!item.receivedAt" type="button" :disabled="pending" @click="acknowledge(item.id, false)">确认接收</button><template v-if="item.requireExecution && !item.executedAt"><textarea v-model="notes[item.id]" :aria-label="`${item.departmentName}执行说明`" placeholder="说明变更执行情况及旧物料、旧图纸的处理结果" maxlength="1000" /><button type="button" :disabled="pending || !notes[item.id]?.trim()" @click="acknowledge(item.id, true)">确认执行</button></template></template><span v-else>由该部门成员确认</span>
        </td></tr>
      </tbody></table></div>
      <details v-if="progress?.deliveries.length"><summary>通知发送明细</summary><div class="execution-table"><table><thead><tr><th>事件 / 渠道</th><th>状态</th><th>处理说明</th><th>操作</th></tr></thead><tbody><tr v-for="item in progress.deliveries" :key="item.id"><td>{{ eventLabel(item.event) }}<small>{{ notificationChannels[item.channel as keyof typeof notificationChannels] ?? item.channel }}</small></td><td>{{ labels[item.state] ?? item.state }}</td><td>{{ item.message || '等待发送' }}</td><td><template v-if="canManage && ['Failed', 'Unknown'].includes(item.state)"><label v-if="item.state === 'Unknown'"><input v-model="retryConfirmation[item.id]" type="checkbox">已核对接收结果，确认需要重发</label><button type="button" :disabled="pending || item.state === 'Unknown' && !retryConfirmation[item.id]" @click="retry(item)">重试此通知</button></template></td></tr></tbody></table></div></details>
    </template>
    <p v-else-if="!error">正在读取发布执行状态…</p>
  </details>
</template>

<style scoped>
.release-execution{margin:10px 0 16px;padding:12px;border:1px solid var(--pdm-border);border-radius:6px;background:var(--pdm-panel-bg,#fff);font-size:13px}.release-execution summary{cursor:pointer;font-weight:600;padding:5px 0}.execution-statuses{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin:12px 0}.execution-statuses>div{display:flex;flex-direction:column;gap:8px;background:var(--pdm-bg);padding:12px;border-radius:4px;overflow-wrap:anywhere}.execution-statuses span,.execution-hint{color:var(--pdm-text-soft)}.execution-hint{line-height:1.7}.execution-error{color:var(--pdm-danger)}.execution-table{overflow-x:auto;max-width:100%;margin:10px 0}.execution-table table{width:100%;border-collapse:collapse;min-width:560px}.execution-table th,.execution-table td{padding:9px;text-align:left;border-bottom:1px solid var(--pdm-surface-muted);vertical-align:top}.execution-table small{display:block;color:var(--pdm-text-soft);max-width:340px;white-space:normal;overflow-wrap:anywhere;margin-top:5px}.execution-table textarea{display:block;width:100%;min-width:180px;max-width:340px;min-height:58px;margin:8px 0;padding:6px;box-sizing:border-box}.execution-table label{display:block;margin-bottom:7px}@media(max-width:760px){.execution-statuses{grid-template-columns:repeat(2,minmax(0,1fr))}}
</style>
