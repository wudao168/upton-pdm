<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ClipboardCheck, KeyRound, RefreshCw } from '@lucide/vue'
import type { EditLockSummary, MaterialCodeApplication, MyApprovalTask, PasswordResetTask, ProgramTemplateTask, UserNotification } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()

const props = withDefaults(defineProps<{
  tasks: MyApprovalTask[]
  notifications?: UserNotification[]
  materialCodeTasks: MaterialCodeApplication[]
  programTemplateTasks?: ProgramTemplateTask[]
  locks: EditLockSummary[]
  passwordResetTasks: PasswordResetTask[]
  pending: boolean
  onRequestRelease: (documentId: string, reason: string) => Promise<void>
  onForceRelease: (documentId: string, reason: string) => Promise<void>
  onResetPassword: (taskId: string) => Promise<void>
  onMarkAllNotificationsRead?: () => Promise<void>
}>(), {
  notifications: () => [],
  programTemplateTasks: () => [],
  onMarkAllNotificationsRead: async () => {},
})
defineEmits<{ open: [projectId: string, releasePackageId: string]; openNotification: [notification: UserNotification]; openMaterialApprovals: []; openProgramTemplate: [templateId: string]; refresh: [] }>()

type TaskFilter = 'all' | 'notification' | 'approval' | 'material' | 'program' | 'lock' | 'password'
type TaskCenterRow = {
  key: string
  kind: Exclude<TaskFilter, 'all'>
  status: string
  statusClass: string
  title: string
  content: string
  createdAt: string
  notification?: UserNotification
  approval?: MyApprovalTask
  material?: MaterialCodeApplication
  program?: ProgramTemplateTask
  locks?: EditLockSummary[]
  password?: PasswordResetTask
}

const bomHeaderLabels = {
  Master: '项目主BOM', Standard: '标准件BOM', NonStandard: '非标件BOM', Electrical: '电气BOM',
} as const
const bomHeaderOrder = ['Master', 'Standard', 'NonStandard', 'Electrical'] as const

const lockGroups = computed(() => {
  const groups = new Map<string, EditLockSummary[]>()
  for (const lock of props.locks) {
    const locks = groups.get(lock.projectId) ?? []
    locks.push(lock)
    groups.set(lock.projectId, locks)
  }
  return [...groups.values()]
})

const materialTaskRows = computed<TaskCenterRow[]>(() => {
  const bomHeaderGroups = new Map<string, MaterialCodeApplication[]>()
  const individualTasks: MaterialCodeApplication[] = []
  for (const task of props.materialCodeTasks) {
    if (task.applicationType !== 'BomHeader') {
      individualTasks.push(task)
      continue
    }
    const tasks = bomHeaderGroups.get(task.projectId) ?? []
    tasks.push(task)
    bomHeaderGroups.set(task.projectId, tasks)
  }

  const groupedRows = [...bomHeaderGroups.values()].map(tasks => {
    const first = tasks[0]
    const latest = tasks.reduce((result, task) => new Date(task.requestedAt).getTime() > new Date(result.requestedAt).getTime() ? task : result)
    const kinds = [...new Set(tasks.map(task => task.bomHeaderKind).filter((kind): kind is NonNullable<MaterialCodeApplication['bomHeaderKind']> => !!kind))]
      .sort((left, right) => bomHeaderOrder.indexOf(left) - bomHeaderOrder.indexOf(right))
      .map(kind => bomHeaderLabels[kind])
    const applicants = [...new Set(tasks.map(task => task.requestedBy))]
    return {
      key: `material-bom-project-${first.projectId}`,
      kind: 'material' as const,
      status: '待审批',
      statusClass: 'is-remind',
      title: `${first.projectCode || '项目'} BOM料号审批${tasks.length > 1 ? `（${tasks.length}项）` : ''}`,
      content: `${first.projectName || first.projectId} · ${kinds.join('、') || 'BOM物料'}${tasks.length > 1 ? ` · 共${tasks.length}项` : ''} · 申请人 ${applicants.map(item => displayUserName(item)).join('、')}`,
      createdAt: latest.requestedAt,
      material: first,
    }
  })
  const individualRows = individualTasks.map(task => ({
    key: `material-${task.id}`,
    kind: 'material' as const,
    status: '待审批',
    statusClass: 'is-remind',
    title: `${task.projectCode || '项目'} 标准件料号审批`,
    content: `${task.projectName || task.projectId} · ${task.applicationName || task.bomItemName || 'BOM物料'} · 申请人 ${displayUserName(task.requestedBy)}`,
    createdAt: task.requestedAt,
    material: task,
  }))
  return [...groupedRows, ...individualRows]
})

const taskFilter = ref<TaskFilter>('all')
const currentPage = ref(1)
const pageSize = ref(20)
const pageSizeOptions = [20, 50, 100]
const commonForceReleaseReasons = ['编辑人长期离线未处理', '项目交接需继续设计', '误获取或未正确释放', '人员离职或岗位调整', '客户端异常导致占用']
const forceReleaseDialogOpen = ref(false)
const forceReleaseDialogTargets = ref<EditLockSummary[]>([])
const forceReleaseReason = ref('')
const forceReleaseSubmitting = ref(false)
const forceReleaseDialogSummary = computed(() => {
  const targets = forceReleaseDialogTargets.value
  return targets.length ? `将统一释放 ${targets[0].projectCode} 项目下 ${targets.length} 个超时编辑权限，相关旧会话将立即失效，只能另存文件，不能提交。` : ''
})

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
  ...props.notifications.map(notification => ({
    key: `notification-${notification.id}`,
    kind: 'notification' as const,
    status: notification.readAt ? '已读' : '未读',
    statusClass: notification.readAt ? 'is-ok' : 'is-alert',
    title: notification.title,
    content: notification.content,
    createdAt: notification.createdAt,
    notification,
  })),
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
  ...materialTaskRows.value,
  ...props.programTemplateTasks.map(task => ({
    key: `program-${task.id}`,
    kind: 'program' as const,
    status: task.stage === 'Review' ? '待审核' : '待批准',
    statusClass: 'is-remind',
    title: `${task.templateCode} 程序模板${task.stage === 'Review' ? '审核' : '批准'}`,
    content: `${task.templateName} · ${task.version} · ${task.stage === 'Review' ? '电气组织审核' : '集团标准化批准'}`,
    createdAt: task.createdAt,
    program: task,
  })),
  ...lockGroups.value.map(locks => {
    const first = locks[0]
    const oldest = locks.reduce((result, lock) => new Date(lock.checkedOutAt).getTime() < new Date(result.checkedOutAt).getTime() ? lock : result)
    const mostUrgent = locks.reduce((result, lock) => attentionIndex(lock.attentionLevel) > attentionIndex(result.attentionLevel) ? lock : result)
    const editorCount = new Set(locks.map(lock => lock.checkedOutBy)).size
    return {
      key: `lock-project-${first.projectId}`,
      kind: 'lock' as const,
      status: attentionLabel(mostUrgent.attentionLevel),
      statusClass: lockStatusClass(mostUrgent),
      title: `${first.projectCode} · ${first.projectName}`,
      content: locks.length === 1
        ? `${first.drawingNumber} · ${first.documentName} · ${displayUserName(first.checkedOutBy)}（${first.checkoutMachine || '未知电脑'}）· ${connectionLabel(first.connectionState)} · 已占用${elapsed(first.checkedOutAt)}`
        : `${locks.length} 个图档 · ${editorCount} 位编辑人 · 最长占用${elapsed(oldest.checkedOutAt)}`,
      createdAt: oldest.checkedOutAt,
      locks,
    }
  }),
  ...props.passwordResetTasks.map(task => ({
    key: `password-${task.id}`,
    kind: 'password' as const,
    status: '待处理',
    statusClass: 'is-remind',
    title: `${displayUserName(task.username)} 密码重置申请`,
    content: `${task.displayName}申请将账号密码重置为初始密码`,
    createdAt: task.requestedAt,
    password: task,
  })),
].sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()))

const filterCounts = computed(() => ({
  all: rows.value.length,
  notification: props.notifications.length,
  approval: props.tasks.length,
  material: materialTaskRows.value.length,
  program: props.programTemplateTasks.length,
  lock: lockGroups.value.length,
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

function forceReleaseTargets(locks: EditLockSummary[] = []) {
  return locks.filter(lock => lock.canForceRelease && !lock.ownedByCurrentUser)
}

function requestReleaseTargets(locks: EditLockSummary[] = []) {
  return locks.filter(lock => lock.canRequestRelease && !lock.ownedByCurrentUser && !lock.releaseRequestedBy)
}

function forceReleaseLabel(locks: EditLockSummary[] = []) {
  const count = forceReleaseTargets(locks).length
  return count > 1 ? `统一强制释放（${count}）` : '强制释放'
}

function requestReleaseLabel(locks: EditLockSummary[] = []) {
  const count = requestReleaseTargets(locks).length
  return count > 1 ? `统一催办／申请（${count}）` : '催办／申请释放'
}

function openForceRelease(locks: EditLockSummary[]) {
  const targets = forceReleaseTargets(locks)
  if (!targets.length) return
  forceReleaseDialogTargets.value = targets
  forceReleaseReason.value = ''
  forceReleaseDialogOpen.value = true
}

async function requestRelease(locks: EditLockSummary[]) {
  const targets = requestReleaseTargets(locks)
  if (!targets.length) return
  let completed = 0
  try {
    const { value } = await ElMessageBox.prompt(`将向 ${targets[0].projectCode} 项目下 ${targets.length} 个图档的当前编辑人统一发送释放申请。`, '批量申请释放编辑权限', { inputValue: '需要继续该项目设计，请及时提交存档或结束编辑。', inputValidator: value => value.trim().length > 0 || '请填写申请原因', confirmButtonText: '提交申请', cancelButtonText: '取消' })
    for (const lock of targets) {
      await props.onRequestRelease(lock.documentId, value)
      completed++
    }
    ElMessage.success(`已统一提交 ${completed} 个释放申请`)
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    const message = error instanceof Error ? error.message : '申请释放失败'
    ElMessage.error(completed ? `已提交 ${completed} 个，其余未完成：${message}` : message)
  }
}

async function confirmForceRelease() {
  const targets = forceReleaseDialogTargets.value
  const reason = forceReleaseReason.value.trim()
  if (!targets.length) return
  if (!reason) {
    ElMessage.warning('请选择常用原因或填写强制释放原因')
    return
  }
  let completed = 0
  forceReleaseSubmitting.value = true
  try {
    for (const lock of targets) {
      await props.onForceRelease(lock.documentId, reason)
      completed++
    }
    forceReleaseDialogOpen.value = false
    ElMessage.success(`已统一释放 ${completed} 个超时编辑权限`)
  } catch (error) {
    const message = error instanceof Error ? error.message : '强制释放失败'
    ElMessage.error(completed ? `已释放 ${completed} 个，其余未完成：${message}` : message)
  } finally {
    forceReleaseSubmitting.value = false
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
        <button v-if="notifications.some(item => !item.readAt)" type="button" class="pdm-secondary-action" :disabled="pending" @click="onMarkAllNotificationsRead">全部已读</button>
        <div class="pdm-task-filters" role="tablist" aria-label="待办类型">
          <button type="button" role="tab" :aria-selected="taskFilter === 'all'" @click="setTaskFilter('all')">全部待办（{{ filterCounts.all }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'approval'" @click="setTaskFilter('approval')">审批任务（{{ filterCounts.approval }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'material'" @click="setTaskFilter('material')">料号审批（{{ filterCounts.material }}）</button>
          <button v-if="programTemplateTasks.length" type="button" role="tab" :aria-selected="taskFilter === 'program'" @click="setTaskFilter('program')">程序模板（{{ filterCounts.program }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'lock'" @click="setTaskFilter('lock')">编辑权限（{{ filterCounts.lock }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'password'" @click="setTaskFilter('password')">密码重置（{{ filterCounts.password }}）</button>
          <button type="button" role="tab" :aria-selected="taskFilter === 'notification'" @click="setTaskFilter('notification')">系统消息（{{ filterCounts.notification }}）</button>
        </div>
      </div>

      <div v-if="pagedRows.length" class="pdm-table-scroll pdm-task-table-scroll">
        <table class="pdm-project-table pdm-task-table">
          <thead><tr><th>状态</th><th>标题</th><th>内容</th><th>创建时间</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="row in pagedRows" :key="row.key" :class="{ 'is-task-actionable': row.kind === 'notification' || row.kind === 'approval' || row.kind === 'material' || row.kind === 'program' }" @click="row.notification ? $emit('openNotification', row.notification) : row.approval ? $emit('open', row.approval.projectId, row.approval.releasePackageId) : row.material ? $emit('openMaterialApprovals') : row.program ? $emit('openProgramTemplate', row.program.templateId) : undefined">
              <td><span class="pdm-status" :class="row.statusClass">{{ row.status }}</span></td>
              <td><strong>{{ row.title }}</strong></td>
              <td :title="row.content">{{ row.content }}</td>
              <td>{{ formatDateTime(row.createdAt) }}</td>
              <td>
                <button v-if="row.notification" type="button" class="pdm-text-action" @click.stop="$emit('openNotification', row.notification)">查看发布包</button>
                <button v-else-if="row.approval" type="button" class="pdm-text-action" @click.stop="$emit('open', row.approval.projectId, row.approval.releasePackageId)">查看</button>
                <button v-else-if="row.material" type="button" class="pdm-text-action" @click.stop="$emit('openMaterialApprovals')">查看申请</button>
                <button v-else-if="row.program" type="button" class="pdm-text-action" @click.stop="$emit('openProgramTemplate', row.program.templateId)">查看模板</button>
                <button v-else-if="row.password" type="button" class="pdm-text-action" :disabled="pending" @click.stop="resetPassword(row.password)"><KeyRound :size="14" />重置密码</button>
                <button v-else-if="forceReleaseTargets(row.locks).length" type="button" class="pdm-text-action is-danger" :disabled="pending" @click.stop="openForceRelease(row.locks!)">{{ forceReleaseLabel(row.locks) }}</button>
                <button v-else-if="requestReleaseTargets(row.locks).length" type="button" class="pdm-text-action" :disabled="pending" @click.stop="requestRelease(row.locks!)">{{ requestReleaseLabel(row.locks) }}</button>
                <span v-else-if="row.locks?.some(lock => lock.releaseRequestedBy)">已申请</span>
                <span v-else-if="row.locks?.some(lock => lock.ownedByCurrentUser)" class="pdm-lock-own">请在SolidWorks处理</span>
                <span v-else>—</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else class="pdm-project-empty pdm-task-empty"><ClipboardCheck :size="42" /><h2>当前没有待办或消息</h2><p>新的审批、退回消息、编辑权限或密码重置任务会显示在这里。</p></div>

      <footer class="pdm-task-pagination">
        <span>共 {{ filteredRows.length }} 条</span>
        <label>每页<select v-model.number="pageSize"><option v-for="size in pageSizeOptions" :key="size" :value="size">{{ size }}</option></select>条</label>
        <button type="button" :disabled="currentPage <= 1" aria-label="上一页" @click="currentPage--">‹</button>
        <strong>{{ currentPage }} / {{ totalPages }}</strong>
        <button type="button" :disabled="currentPage >= totalPages" aria-label="下一页" @click="currentPage++">›</button>
      </footer>
    </section>

    <el-dialog v-if="forceReleaseDialogOpen" v-model="forceReleaseDialogOpen" class="pdm-force-release-dialog" modal-class="pdm-force-release-overlay" title="批量强制释放超时权限" width="520px" :close-on-click-modal="false" append-to-body>
      <div class="pdm-force-release-form">
        <p>{{ forceReleaseDialogSummary }}</p>
        <fieldset>
          <legend>常用释放原因</legend>
          <div class="pdm-force-release-reasons" role="group" aria-label="常用释放原因">
            <button v-for="reason in commonForceReleaseReasons" :key="reason" type="button" :aria-pressed="forceReleaseReason === reason" @click="forceReleaseReason = reason">{{ reason }}</button>
          </div>
        </fieldset>
        <label>释放原因<textarea v-model="forceReleaseReason" rows="3" maxlength="500" placeholder="请选择常用原因或填写具体原因" aria-label="强制释放原因" /></label>
      </div>
      <template #footer>
        <div class="pdm-force-release-dialog-actions">
          <button type="button" class="pdm-secondary-action" :disabled="forceReleaseSubmitting" @click="forceReleaseDialogOpen = false">取消</button>
          <button type="button" class="pdm-primary-action" :disabled="pending || forceReleaseSubmitting" @click="confirmForceRelease">{{ forceReleaseSubmitting ? '释放中…' : '确认释放' }}</button>
        </div>
      </template>
    </el-dialog>
  </section>
</template>
