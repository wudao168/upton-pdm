<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { ClipboardCheck, ClockAlert, RefreshCw } from '@lucide/vue'
import type { EditLockSummary, MyApprovalTask } from '../types'

const props = defineProps<{
  tasks: MyApprovalTask[]
  locks: EditLockSummary[]
  pending: boolean
  onRequestRelease: (documentId: string, reason: string) => Promise<void>
  onForceRelease: (documentId: string, reason: string) => Promise<void>
}>()
defineEmits<{ open: [projectId: string]; refresh: [] }>()

function stageLabel(stage: string | number) {
  return stage === 'ProcessReview' || stage === 0 ? '工艺审核' : '批准'
}

function attentionIndex(value: EditLockSummary['attentionLevel']) {
  return typeof value === 'number' ? value : ['Normal', 'Reminder', 'StrongReminder', 'Overdue', 'Reclaimable'].indexOf(value)
}

function attentionLabel(value: EditLockSummary['attentionLevel']) {
  return ['正常', '请及时存档', '强提醒', '已超时', '可强制释放'][attentionIndex(value)] ?? '正常'
}

function connectionLabel(value: EditLockSummary['connectionState']) {
  const index = typeof value === 'number' ? value : ['Active', 'OfflineGrace', 'Offline'].indexOf(value)
  return ['在线', '离线宽限', '离线'][index] ?? '未知'
}

function elapsed(from: string) {
  const hours = Math.max(0, Math.floor((Date.now() - new Date(from).getTime()) / 3_600_000))
  return hours < 24 ? `${hours}小时` : `${Math.floor(hours / 24)}天${hours % 24}小时`
}

async function requestRelease(lock: EditLockSummary) {
  try {
    const { value } = await ElMessageBox.prompt('请说明需要该图档的原因，系统会记录申请并提醒当前编辑人。', '申请释放编辑权限', { inputValue: '需要继续该图档设计，请及时提交存档或结束编辑。', inputValidator: value => value.trim().length > 0 || '请填写申请原因', confirmButtonText: '提交申请', cancelButtonText: '取消' })
    await props.onRequestRelease(lock.documentId, value)
    ElMessage.success('释放申请已记录')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '申请释放失败')
  }
}

async function forceRelease(lock: EditLockSummary) {
  try {
    const { value } = await ElMessageBox.prompt(`将使${lock.checkedOutBy}的旧会话立即失效，旧会话只能另存文件，不能提交。`, '强制释放超时权限', { inputPlaceholder: '请填写强制释放原因', inputValidator: value => value.trim().length > 0 || '请填写强制释放原因', confirmButtonText: '确认强制释放', cancelButtonText: '取消', type: 'warning' })
    await props.onForceRelease(lock.documentId, value)
    ElMessage.success('超时编辑权限已释放')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '强制释放失败')
  }
}
</script>

<template>
  <section class="pdm-project-manager pdm-task-center" aria-label="我的待办">
    <header class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">PDM <span>/</span> 我的待办</div><h1>我的待办</h1><p>集中处理审批任务和长时间未提交的编辑权限。</p></div>
      <button type="button" class="pdm-secondary-action" :disabled="pending" @click="$emit('refresh')"><RefreshCw :size="15" />刷新</button>
    </header>
    <section class="pdm-panel pdm-project-list">
      <header class="pdm-panel-heading"><div><h2>编辑权限</h2><small>在线会话继续保留权限；离线和超时权限可催办，达到强制释放时限后由授权人员处理。</small></div></header>
      <div v-if="locks.length" class="pdm-table-scroll"><table class="pdm-project-table pdm-lock-table"><thead><tr><th>项目／图档</th><th>编辑人</th><th>已占用</th><th>连接</th><th>状态</th><th>申请情况</th><th>操作</th></tr></thead><tbody><tr v-for="lock in locks" :key="lock.documentId" :class="`is-lock-level-${attentionIndex(lock.attentionLevel)}`"><td><strong>{{ lock.projectCode }} · {{ lock.drawingNumber }}</strong><small>{{ lock.documentName }}</small></td><td>{{ lock.checkedOutBy }}<small>{{ lock.checkoutMachine || '未知电脑' }}</small></td><td>{{ elapsed(lock.checkedOutAt) }}<small>{{ new Date(lock.checkedOutAt).toLocaleString() }}</small></td><td><span class="pdm-status" :class="connectionLabel(lock.connectionState) === '在线' ? 'is-ok' : 'is-alert'">{{ connectionLabel(lock.connectionState) }}</span><small>心跳 {{ new Date(lock.lastHeartbeatAt).toLocaleString() }}</small></td><td><span class="pdm-status" :class="attentionIndex(lock.attentionLevel) >= 3 ? 'is-alert' : attentionIndex(lock.attentionLevel) > 0 ? 'is-remind' : 'is-ok'">{{ attentionLabel(lock.attentionLevel) }}</span></td><td><span v-if="lock.releaseRequestedBy">{{ lock.releaseRequestedBy }} 已申请</span><small v-if="lock.releaseRequestReason">{{ lock.releaseRequestReason }}</small><span v-else>—</span></td><td><span v-if="lock.ownedByCurrentUser" class="pdm-lock-own">请在SolidWorks提交或放弃</span><button v-else-if="lock.canForceRelease" type="button" class="pdm-text-action is-danger" :disabled="pending" @click="forceRelease(lock)">强制释放</button><button v-else-if="lock.canRequestRelease" type="button" class="pdm-text-action" :disabled="pending || !!lock.releaseRequestedBy" @click="requestRelease(lock)">{{ lock.releaseRequestedBy ? '已申请' : '催办／申请释放' }}</button><span v-else>—</span></td></tr></tbody></table></div>
      <div v-else class="pdm-project-empty pdm-lock-empty"><ClockAlert :size="34" /><h2>当前没有编辑权限待办</h2><p>本人签出或权限范围内的占用记录会显示在这里。</p></div>
    </section>
    <section class="pdm-panel pdm-project-list">
      <header class="pdm-panel-heading"><div><h2>审批任务</h2><small>处理分配给当前账号的工艺审核和批准任务。</small></div></header>
      <div v-if="tasks.length" class="pdm-table-scroll"><table class="pdm-project-table"><thead><tr><th>项目号</th><th>项目名称</th><th>发布包</th><th>待办环节</th><th>进入时间</th><th>操作</th></tr></thead><tbody><tr v-for="task in tasks" :key="task.id"><td><strong>{{ task.projectCode }}</strong></td><td>{{ task.projectName }}</td><td>{{ task.releasePackageNumber }}</td><td><span class="pdm-status is-warn">{{ stageLabel(task.stage) }}</span></td><td>{{ new Date(task.createdAt).toLocaleString() }}</td><td><button type="button" class="pdm-text-action" @click="$emit('open', task.projectId)">进入审批与发布</button></td></tr></tbody></table></div>
      <div v-else class="pdm-project-empty"><ClipboardCheck :size="42" /><h2>当前没有待处理任务</h2><p>新的工艺审核或批准任务分配给你后，会显示在这里。</p></div>
    </section>
  </section>
</template>
