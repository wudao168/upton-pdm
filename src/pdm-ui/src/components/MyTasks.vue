<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ClipboardCheck, KeyRound, RefreshCw } from '@lucide/vue'
import type { EditLockSummary, MyApprovalTask, PasswordResetTask } from '../types'

const props = defineProps<{
  tasks: MyApprovalTask[]
  locks: EditLockSummary[]
  passwordResetTasks: PasswordResetTask[]
  pending: boolean
  onRequestRelease: (documentId: string, reason: string) => Promise<void>
  onForceRelease: (documentId: string, reason: string) => Promise<void>
  onResetPassword: (taskId: string) => Promise<void>
}>()
defineEmits<{ open: [projectId: string, releasePackageId: string]; refresh: [] }>()

type TaskFilter = 'all' | 'approval' | 'lock' | 'password'
type TaskCenterRow = {
  key: string
  kind: Exclude<TaskFilter, 'all'>
  status: string
  statusClass: string
  title: string
  content: string
  createdAt: string
  approval?: MyApprovalTask
  lock?: EditLockSummary
  password?: PasswordResetTask
}

const taskFilter = ref<TaskFilter>('all')
const currentPage = ref(1)
const pageSize = ref(20)
const pageSizeOptions = [20, 50, 100]

function stageLabel(stage: string | number) {
  const key = typeof stage === 'number'
    ? ({ 1: 'ProcessReview', 2: 'Approval', 10: 'MechanicalEngineer', 20: 'MainDesigner', 30: 'MechanicalSupervisor', 40: 'HardwareEngineer', 50: 'HardwareSupervisor', 60: 'StandardizationSupervisor' } as Record<number, string>)[stage]
    : stage
  return ({
    ProcessReview: '工艺审核', Approval: '批准', MechanicalEngineer: '机械工程师自检', MainDesigner: '主设审核',
    MechanicalSupervisor: '机械主管批准', HardwareEngineer: '硬件工程师自检', HardwareSupervisor: '硬件主管审核', StandardizationSupervisor: '标准化主管批准',
  } as Record<string, string>)[key] ?? String(stage)
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

function lockStatusClass(lock: EditLockSummary) {
  const index = attentionIndex(lock.attentionLevel)
  return index >= 3 ? 'is-alert' : index > 0 ? 'is-remind' : 'is-ok'
}

function formatDateTime(value: string) {
  return new Date(value).toLocaleString()
}

const rows = computed<TaskCenterRow[]>(() => [
  ...props.tasks.map(task => ({
    key: `approval-${task.id}`,
    kind: 'approval' as const,
    status: '待审批',
    statusClass: 'is-remind',
    title: `${task.projectCode} BOM发布审批`,
    content: `${task.projectName} · ${task.releasePackageNumber} · ${stageLabel(task.stage)}`,
    createdAt: task.createdAt,
    approval: task,
  })),
  ...props.locks.map(lock => ({
    key: `lock-${lock.documentId}`,
    kind: 'lock' as const,
    status: attentionLabel(lock.attentionLevel),
    statusClass: lockStatusClass(lock),
    title: `${lock.projectCode} · ${lock.drawingNumber}`,
    content: `${lock.documentName} · ${lock.checkedOutBy}（${lock.checkoutMachine || '未知电脑'}）· ${connectionLabel(lock.connectionState)} · 已占用${elapsed(lock.checkedOutAt)}`,
    createdAt: lock.checkedOutAt,
    lock,
  })),
  ...props.passwordResetTasks.map(task => ({
    key: `password-${task.id}`,
    kind: 'password' as const,
    status: '待处理',
    statusClass: 'is-remind',
    title: `${task.username} 密码重置申请`,
    content: `${task.displayName}申请将账号密码重置为初始密码`,
    createdAt: task.requestedAt,
    password: task,
  })),
].sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()))

const filterCounts = computed(() => ({
  all: rows.value.length,
  approval: props.tasks.length,
  lock: props.locks.length,
  password: props.passwordResetTasks.length,
}))
const filteredRows = computed(() => taskFilter.value === 'all' ? rows.value : rows.value.filter(row => row.kind === taskFilter.value))
const pagedRows = computed(() => filteredRows.value.slice((currentPage.value - 1) * pageSize.value, currentPage.value * pageSize.value))
const totalPages = computed(() => Math.max(1, Math.ceil(filteredRows.value.length / pageSize.value)))

function setTaskFilter(value: TaskFilter) {
  taskFilter.value = value
}

watch(taskFilter, () => {
  currentPage.value = 1
})
watch([pageSize, () => filteredRows.value.length], () => {
  currentPage.value = Math.min(currentPage.value, totalPages.value)
})

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

async function resetPassword(task: PasswordResetTask) {
  try {
    await ElMessageBox.confirm(`确认将 ${task.displayName} 的密码重置为 11111111？`, '重置密码', { type: 'warning' })
    await props.onResetPassword(task.id)
    ElMessage.success('密码已重置为 11111111')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '密码重置失败')
  }
}
</script>

<template>
  <section class="pdm-project-manager pdm-task-center" aria-label="我的待办">
    <section class="pdm-panel pdm-task-message-panel" aria-label="消息中心">
      <div class="pdm-task-toolbar">
        <button type="button" class="pdm-secondary-action" :disabled="pending" @click="$emit('refresh')"><RefreshCw :size="14" />刷新</button>
        <div class="pdm-task-filters" role="tablist" aria-label="待办类型">
          <button type="button" role="tab" :aria-selected="taskFilter === 'all'" @click="setTaskFilter('all')">全部待办（{{ filterCounts.all }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'approval'" @click="setTaskFilter('approval')">审批任务（{{ filterCounts.approval }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'lock'" @click="setTaskFilter('lock')">编辑权限（{{ filterCounts.lock }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'password'" @click="setTaskFilter('password')">密码重置（{{ filterCounts.password }}）</button>
        </div>
      </div>

      <div v-if="pagedRows.length" class="pdm-table-scroll pdm-task-table-scroll">
        <table class="pdm-project-table pdm-task-table">
          <thead><tr><th>状态</th><th>标题</th><th>内容</th><th>创建时间</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="row in pagedRows" :key="row.key" :class="{ 'is-task-actionable': row.kind === 'approval' }" @click="row.approval && $emit('open', row.approval.projectId, row.approval.releasePackageId)">
              <td><span class="pdm-status" :class="row.statusClass">{{ row.status }}</span></td>
              <td><strong>{{ row.title }}</strong></td>
              <td :title="row.content">{{ row.content }}</td>
              <td>{{ formatDateTime(row.createdAt) }}</td>
              <td>
                <button v-if="row.approval" type="button" class="pdm-text-action" @click.stop="$emit('open', row.approval.projectId, row.approval.releasePackageId)">查看</button>
                <button v-else-if="row.password" type="button" class="pdm-text-action" :disabled="pending" @click.stop="resetPassword(row.password)"><KeyRound :size="14" />重置密码</button>
                <span v-else-if="row.lock?.ownedByCurrentUser" class="pdm-lock-own">请在SolidWorks处理</span>
                <button v-else-if="row.lock?.canForceRelease" type="button" class="pdm-text-action is-danger" :disabled="pending" @click.stop="forceRelease(row.lock)">强制释放</button>
                <button v-else-if="row.lock?.canRequestRelease" type="button" class="pdm-text-action" :disabled="pending || !!row.lock.releaseRequestedBy" @click.stop="requestRelease(row.lock)">{{ row.lock.releaseRequestedBy ? '已申请' : '催办／申请释放' }}</button>
                <span v-else>—</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else class="pdm-project-empty pdm-task-empty"><ClipboardCheck :size="42" /><h2>当前没有待办任务</h2><p>新的审批、编辑权限或密码重置任务会显示在这里。</p></div>

      <footer class="pdm-task-pagination">
        <span>共 {{ filteredRows.length }} 条</span>
        <label>每页<select v-model.number="pageSize"><option v-for="size in pageSizeOptions" :key="size" :value="size">{{ size }}</option></select>条</label>
        <button type="button" :disabled="currentPage <= 1" aria-label="上一页" @click="currentPage--">‹</button>
        <strong>{{ currentPage }} / {{ totalPages }}</strong>
        <button type="button" :disabled="currentPage >= totalPages" aria-label="下一页" @click="currentPage++">›</button>
      </footer>
    </section>
  </section>
</template>
