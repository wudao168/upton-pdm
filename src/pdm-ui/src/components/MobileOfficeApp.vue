<script setup lang="ts">
import SearchField from './SearchField.vue'
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, provide, ref, watch } from 'vue'
import { Home, ClipboardCheck, FolderOpen, Clock, UserRound } from '@lucide/vue'
import { heartbeatSession, listMyApprovalTasks, listProjects, listUserNotifications, loginDingTalkOffice, logoutSession, markUserNotificationRead, readDingTalkOfficeBootstrap, readProjectPlan, readWorkHours } from '../api'
import type { AuthSession } from '../api'
import type { MyApprovalTask, ProjectPlan, ProjectSummary, UserNotification } from '../types'
import { dingTalkOfficeAuthCode } from '../dingtalkOffice'
import { useWorkHoursReminder } from '../composables/useWorkHoursReminder'
import { canViewProjectTab, projectCenterAccessKey } from '../projectCenterAccess'
import { userDisplayNameKey } from '../userDisplay'
import { userErrorMessage } from '../userErrorMessage'
import { isTaskCenterNotification } from '../taskNotifications'
import { chinaToday } from '../projectCalendar'
import '../styles/mobile-office.css'

const WorkHoursManager = defineAsyncComponent(() => import('./WorkHoursManager.vue'))
const ProjectOplManager = defineAsyncComponent(() => import('./ProjectOplManager.vue'))
const MobileApproval = defineAsyncComponent(() => import('./MobileApproval.vue'))
type Page = 'home' | 'tasks' | 'projects' | 'work-hours' | 'mine'
type Guard = { canLeave: () => boolean; prefill?: (projectId: string) => void }
const query = new URLSearchParams(window.location.search)
const session = ref<AuthSession>(), pending = ref(false), refreshing = ref(false), error = ref(''), notice = ref('')
const page = ref<Page>('home'), projects = ref<ProjectSummary[]>([]), tasks = ref<MyApprovalTask[]>([]), notifications = ref<UserNotification[]>([])
const projectId = ref(''), projectSection = ref<'overview' | 'opl'>('overview'), plan = ref<ProjectPlan | null>(null), projectLoading = ref(false), projectError = ref('')
const search = ref(''), taskFilter = ref<'pending' | 'read'>('pending'), selectedTask = ref<MyApprovalTask>()
const workHours = ref<Guard>(), opl = ref<Guard>(), approval = ref<Guard>(), workHoursVisited = ref(false), workHoursProject = ref('')
const todayHours = ref<number | null>(null), version = ref(''), todayHoursError = ref('')
const token = computed(() => session.value?.accessToken ?? '')
const permissions = computed(() => session.value?.permissions ?? [])
const selectedProject = computed(() => projects.value.find(project => project.id === projectId.value))
const visibleProjects = computed(() => projects.value.filter(project => `${project.code} ${project.name} ${project.customerName || ''}`.toLowerCase().includes(search.value.trim().toLowerCase())))
const visibleNotifications = computed(() => notifications.value.filter(isTaskCenterNotification).filter(item => taskFilter.value === 'read' ? !!item.readAt : !item.readAt))
const unread = computed(() => notifications.value.filter(item => isTaskCenterNotification(item) && !item.readAt).length)
const reminder = useWorkHoursReminder(() => ({ token: token.value, companyId: session.value?.activeCompanyId ?? '', username: session.value?.username ?? '' }))
const navigation = [ { key: 'home', label: '首页', icon: Home }, { key: 'tasks', label: '待办', icon: ClipboardCheck }, { key: 'projects', label: '项目', icon: FolderOpen }, { key: 'work-hours', label: '工时', icon: Clock }, { key: 'mine', label: '我的', icon: UserRound } ] as const
provide(projectCenterAccessKey, permissions)
provide(userDisplayNameKey, (username?: string | null, fallback = '—') => session.value && username === session.value.username ? session.value.displayName : username || fallback)
const canView = (tab: string) => canViewProjectTab(permissions.value, tab)
const canViewProjects = computed(() => permissions.value.includes('project.view') && (canView('overview') || canView('opl')))
const visibleNavigation = computed(() => navigation.filter(item => item.key !== 'projects' || canViewProjects.value))
watch(canViewProjects, allowed => { if (!allowed && page.value === 'projects') { page.value = 'home'; projectId.value = ''; plan.value = null } })
const failure = (cause: unknown, fallback: string) => userErrorMessage(cause instanceof Error ? cause.message : fallback)
let disposed = false, heartbeat: ReturnType<typeof setInterval> | undefined, generation = 0, projectGeneration = 0

function canLeave() {
  const current = selectedTask.value ? approval.value : page.value === 'work-hours' ? workHours.value : page.value === 'projects' && projectSection.value === 'opl' ? opl.value : undefined
  if (current && !current.canLeave()) { error.value = '当前有未保存内容或操作正在进行，请先保存或取消修改。'; return false }
  return true
}
function navigate(target: Page) {
  if (target === 'projects' && !canViewProjects.value) return
  if (!canLeave()) return
  selectedTask.value = undefined; page.value = target; error.value = ''; notice.value = ''
  if (target === 'work-hours') workHoursVisited.value = true
  window.scrollTo(0, 0)
}
async function refreshHours() {
  if (!session.value) return
  const current = token.value
  try {
    const view = await readWorkHours(chinaToday(), chinaToday(), false, current)
    if (current !== token.value || disposed) return
    todayHours.value = view.sheets.flatMap(sheet => sheet.entries).filter(entry => entry.date === view.today).reduce((sum, entry) => sum + entry.hours, 0); todayHoursError.value = ''
  } catch (cause) { if (current === token.value && !disposed) todayHoursError.value = failure(cause, '今日工时暂时无法读取，请进入工时页核对。') }
}
async function refresh() {
  if (!session.value || refreshing.value) return
  refreshing.value = true; error.value = ''
  const current = ++generation, currentToken = token.value
  const results = await Promise.allSettled([canViewProjects.value ? listProjects(currentToken) : Promise.resolve([]), listMyApprovalTasks(currentToken), listUserNotifications(currentToken)])
  if (current !== generation || disposed || currentToken !== token.value) return
  const [projectResult, taskResult, notificationResult] = results
  const failures: string[] = []
  if (projectResult.status === 'fulfilled') projects.value = projectResult.value.filter(project => project.canReadContent !== false)
  else failures.push(failure(projectResult.reason, '项目读取失败。'))
  if (taskResult.status === 'fulfilled') tasks.value = taskResult.value
  else failures.push(failure(taskResult.reason, '审批待办读取失败。'))
  if (notificationResult.status === 'fulfilled') notifications.value = notificationResult.value
  else failures.push(failure(notificationResult.reason, '业务待办读取失败。'))
  error.value = [...new Set(failures)].join('；'); refreshing.value = false
  void refreshHours()
}
async function signIn() {
  if (pending.value) return
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const bootstrap = await readDingTalkOfficeBootstrap(query.get('companyId') || undefined)
    if (!bootstrap.enabled) throw new Error('钉钉移动办公尚未启用，请联系管理员完成企业应用配置。')
    const code = await dingTalkOfficeAuthCode(bootstrap.corpId, bootstrap.appKey)
    const result = await loginDingTalkOffice(bootstrap.companyId, code)
    if (disposed) return
    window.localStorage.setItem('pdm_active_organization', result.activeCompanyId)
    generation++; projectGeneration++; refreshing.value = false
    projects.value = []; tasks.value = []; notifications.value = []; selectedTask.value = undefined; projectId.value = ''; plan.value = null; workHoursVisited.value = false; todayHours.value = null
    session.value = result
    await refresh()
    const target = query.get('view')
    if (visibleNavigation.value.some(item => item.key === target)) navigate(target as Page)
    if (query.get('projectId')) await openProject(query.get('projectId')!, query.get('tab') === 'opl' ? 'opl' : 'overview')
    if (query.get('taskId')) {
      const task = tasks.value.find(item => item.id === query.get('taskId'))
      if (task) openTask(task)
      else error.value = '该待办已处理或当前账号无权访问，请刷新待办核对。'
    }
  } catch (cause) { if (!disposed) error.value = failure(cause, '钉钉登录失败，请重新进入。') }
  finally { pending.value = false }
}
async function openProject(id: string, section: 'overview' | 'opl' = 'overview') {
  if (!canViewProjects.value) return
  if (!canLeave()) return
  const project = projects.value.find(item => item.id === id)
  if (!project) { error.value = '项目不存在或当前账号无权访问，请刷新项目列表。'; return }
  if (section === 'overview' && !canView('overview') && canView('opl')) section = 'opl'
  if (!canView(section === 'opl' ? 'opl' : 'overview')) { error.value = '当前账号没有此项目页签的查看权限。'; return }
  const current = ++projectGeneration
  selectedTask.value = undefined; projectId.value = id; projectSection.value = section; page.value = 'projects'; plan.value = null; projectError.value = ''; error.value = ''
  if (section === 'overview' && canView('project-plan')) {
    projectLoading.value = true
    try { const result = await readProjectPlan(id, token.value); if (current === projectGeneration && !disposed) plan.value = result }
    catch (cause) { if (current === projectGeneration && !disposed) projectError.value = failure(cause, '项目计划读取失败，请重试。') }
    finally { if (current === projectGeneration) projectLoading.value = false }
  }
}
function openTask(task: MyApprovalTask) { if (canLeave()) { selectedTask.value = task; page.value = 'tasks'; error.value = '' } }
async function approvalDone() { selectedTask.value = undefined; notice.value = '审批操作已保存，待办已刷新。'; await refresh() }
async function openNotification(notification: UserNotification) {
  if (!canLeave()) return
  try {
    await markUserNotificationRead(notification.id, token.value)
    notification.readAt = new Date().toISOString()
    if (notification.category === 'work-hours-correction') navigate('work-hours')
    else if (notification.releasePackageId) {
      const task = tasks.value.find(item => item.releasePackageId === notification.releasePackageId)
      if (task) openTask(task)
      else if (notification.projectId) await openProject(notification.projectId)
    } else if (notification.projectId) await openProject(notification.projectId)
  } catch (cause) { error.value = failure(cause, '待办打开失败，请重试。') }
}
function projectHours() {
  if (!canLeave()) return
  if (workHours.value?.prefill) workHours.value.prefill(projectId.value)
  else workHoursProject.value = projectId.value
  navigate('work-hours')
}
function reenter() { if (canLeave()) void signIn() }
function workHoursChanged() {
  if (error.value === '当前有未保存内容或操作正在进行，请先保存或取消修改。') error.value = ''
  void reminder.refresh(); void refreshHours()
}
async function signOut() {
  if (!canLeave() || !session.value || pending.value) return
  pending.value = true
  try {
    await logoutSession(token.value)
    generation++; projectGeneration++; refreshing.value = false; session.value = undefined; projects.value = []; tasks.value = []; notifications.value = []; workHoursVisited.value = false; todayHours.value = null; projectId.value = ''; selectedTask.value = undefined; page.value = 'home'; notice.value = '已退出，可点击重新进入识别当前钉钉账号。'
  } catch (cause) { error.value = failure(cause, '退出未完成，请检查网络后重试。') }
  finally { pending.value = false }
}
function expired(event: Event) {
  const detail = (event as CustomEvent<{ token?: string }>).detail
  if (session.value && detail?.token === token.value) error.value = '登录已失效，当前填写内容仍保留。请保存可保留的内容后重新进入应用。'
}
async function keepAlive() {
  if (!session.value || document.hidden) return
  try { await heartbeatSession(token.value) }
  catch (cause) { error.value = failure(cause, '登录状态核对失败，请检查网络。') }
}
function companyChanged(event: StorageEvent) {
  if (session.value && event.key === 'pdm_active_organization' && event.newValue !== session.value.activeCompanyId) {
    window.localStorage.setItem('pdm_active_organization', session.value.activeCompanyId)
    error.value = '其他页面切换了公司，当前移动办公仍使用已绑定的企业，请核对后再操作。'
  }
}
onMounted(() => {
  document.documentElement.classList.add('pdm-mobile-office')
  document.body.classList.add('pdm-mobile-office')
  window.addEventListener('pdm-session-expired', expired); window.addEventListener('storage', companyChanged)
  heartbeat = setInterval(() => void keepAlive(), 60_000)
  void fetch('./client-bootstrap.json', { cache: 'no-store' }).then(response => response.ok ? response.json() : null).then(value => { version.value = value?.ConfigurationVersion ?? '' }).catch(() => {})
  void signIn()
})
onBeforeUnmount(() => { disposed = true; generation++; projectGeneration++; document.documentElement.classList.remove('pdm-mobile-office'); document.body.classList.remove('pdm-mobile-office'); window.removeEventListener('pdm-session-expired', expired); window.removeEventListener('storage', companyChanged); if (heartbeat) clearInterval(heartbeat) })
</script>
<template>
  <div class="mobile-office">
    <header class="mobile-office-header"><div><strong>UPLM</strong><span>移动办公</span></div><small v-if="session">{{ session.activeCompanyName }} · {{ session.displayName }}</small><button v-if="session" :disabled="refreshing" @click="refresh">{{ refreshing ? '刷新中…' : '刷新' }}</button></header>
    <main class="mobile-office-main">
      <p v-if="error" class="mobile-error" role="alert">{{ error }}</p><p v-if="notice" class="mobile-notice" role="status">{{ notice }}</p>
      <section v-if="!session" class="mobile-signin"><h1>UPLM 员工移动办公</h1><p>待办 · 审批 · 项目 · OPL · 工时</p><p v-if="pending" role="status">正在识别钉钉员工身份…</p><button v-else class="pdm-primary-action" @click="signIn">重新进入</button><p>请从钉钉工作台打开，首次使用需由管理员绑定现有UPLM账号。</p></section>
      <template v-else>
        <section v-if="page === 'home'" aria-label="移动办公首页" class="mobile-home">
          <h1>{{ session.displayName }}，{{ chinaToday() }}</h1><p>项目与业务数据和电脑端同步。</p>
          <div class="mobile-metrics"><button @click="navigate('tasks')"><strong>{{ tasks.length + unread }}</strong><span>待处理事项</span></button><button @click="navigate('work-hours')"><strong>{{ todayHours === null ? '—' : todayHours }}</strong><span>今日已填工时（小时）</span></button></div>
          <p v-if="todayHoursError" class="mobile-error" role="status">{{ todayHoursError }}</p>
          <div class="mobile-shortcuts"><button @click="navigate('tasks')">处理审批</button><button @click="navigate('work-hours')">填写工时<span v-if="reminder.count.value"> · 未填{{ reminder.count.value }}天</span></button><button v-if="canViewProjects" @click="navigate('projects')">查看项目 / 提交OPL</button></div>
          <h2>我的审批</h2><p v-if="refreshing" role="status">正在读取待办…</p><p v-else-if="!tasks.length">暂无待处理审批。</p>
          <button v-for="task in tasks.slice(0, 5)" :key="task.id" class="mobile-record mobile-record-button" @click="openTask(task)"><strong>{{ task.projectCode }} · {{ task.projectName }}</strong><span>{{ task.releasePackageNumber || '验证 / 验收计划' }} · {{ task.stepName || task.stage }}</span></button>
          <template v-if="canViewProjects"><h2>我的项目</h2><button v-for="project in projects.slice(0, 5)" :key="project.id" class="mobile-record mobile-record-button" @click="openProject(project.id)"><strong>{{ project.code }} · {{ project.name }}</strong><span>{{ project.stage }} · {{ project.customerName || '未设置客户' }}</span></button><p v-if="!refreshing && !projects.length">当前账号暂无可查看的项目。</p></template>
        </section>
        <section v-if="page === 'tasks'" aria-label="我的待办">
          <MobileApproval v-if="selectedTask" ref="approval" :key="selectedTask.id" :task="selectedTask" :token="token" :username="session.username" :permissions="permissions" @done="approvalDone" @back="navigate('tasks')" />
          <template v-else><h1>我的待办</h1><div class="mobile-tabs"><button :aria-pressed="taskFilter === 'pending'" @click="taskFilter = 'pending'">待处理</button><button :aria-pressed="taskFilter === 'read'" @click="taskFilter = 'read'">已读事项</button></div>
            <template v-if="taskFilter === 'pending'"><h2>待审批（{{ tasks.length }}）</h2><button v-for="task in tasks" :key="task.id" class="mobile-record mobile-record-button" @click="openTask(task)"><strong>{{ task.projectCode }} · {{ task.projectName }}</strong><span>{{ task.releasePackageNumber || '验证 / 验收计划' }} · {{ task.stepName || task.stage }}</span><small>{{ task.createdAt.slice(0, 10) }}</small></button></template>
            <h2>{{ taskFilter === 'read' ? '已读' : '业务' }}事项（{{ visibleNotifications.length }}）</h2>
            <article v-for="notification in visibleNotifications" :key="notification.id" class="mobile-record"><strong>{{ notification.title }}</strong><p>{{ notification.content }}</p><small>{{ notification.dueDate ? `截止 ${notification.dueDate} · ` : '' }}{{ notification.createdAt.slice(0, 10) }}</small><button @click="openNotification(notification)">{{ notification.projectId ? '查看事项' : '标记已读' }}</button></article>
            <p v-if="!refreshing && !visibleNotifications.length && (taskFilter === 'read' || !tasks.length)">暂无符合条件的事项。</p>
          </template>
        </section>
        <section v-if="page === 'projects' && canViewProjects" aria-label="移动项目">
          <template v-if="!selectedProject"><h1>我的项目</h1><label class="mobile-field">搜索项目<SearchField><input v-model="search" aria-label="搜索项目" placeholder="项目号、名称或客户" /></SearchField></label><button v-for="project in visibleProjects" :key="project.id" class="mobile-record mobile-record-button" @click="openProject(project.id)"><strong>{{ project.code }} · {{ project.name }}</strong><span>{{ project.stage }} · {{ project.parentProjectId ? '子项目' : '主项目' }}</span><small>{{ project.customerName }}{{ project.deliveryDate ? ` · 交期 ${project.deliveryDate}` : '' }}</small></button><p v-if="!visibleProjects.length">暂无符合条件的项目。</p></template>
          <template v-else><button @click="() => { if (canLeave()) { projectGeneration++; projectId = ''; projectSection = 'overview' } }">返回项目列表</button><h1>{{ selectedProject.code }} · {{ selectedProject.name }}</h1><div class="mobile-tabs"><button v-if="canView('overview')" :aria-pressed="projectSection === 'overview'" @click="openProject(selectedProject.id)">项目概况</button><button v-if="canView('opl')" :aria-pressed="projectSection === 'opl'" @click="openProject(selectedProject.id, 'opl')">OPL开口问题</button><button @click="projectHours">填写项目工时</button></div>
            <ProjectOplManager v-if="projectSection === 'opl'" ref="opl" :key="selectedProject.id" :project="selectedProject" :token="token" :mobile="true" @open-release="notice = '请在待办中打开对应审批；历史发布资料可在电脑端查看。'" />
            <template v-else><dl class="mobile-project-facts"><dt>项目状态</dt><dd>{{ selectedProject.stage }}</dd><dt>客户</dt><dd>{{ selectedProject.customerName || '—' }}</dd><dt>项目经理</dt><dd>{{ selectedProject.primaryProjectManager || selectedProject.owner || '—' }}</dd><dt>协同经理</dt><dd>{{ selectedProject.collaborativeProjectManagers?.join('、') || '—' }}</dd><dt>设计人员</dt><dd>{{ selectedProject.designers?.join('、') || '—' }}</dd><dt>交付日期</dt><dd>{{ selectedProject.deliveryDate || '—' }}</dd></dl>
              <h2 v-if="canView('project-plan')">项目计划</h2><p v-if="projectLoading" role="status">正在读取项目计划…</p><p v-if="projectError" class="mobile-error" role="alert">{{ projectError }} <button @click="openProject(selectedProject.id)">重试</button></p><p v-if="canView('project-plan') && !projectLoading && !projectError && !plan">项目尚未建立计划。</p><template v-if="plan"><p>{{ plan.templateName }} · 计划完成 {{ plan.plannedFinish }}</p><article v-for="item in plan.tasks" :key="item.id" class="mobile-record"><strong>{{ item.name }}</strong><p>{{ item.assignee || '未分派' }} · {{ item.plannedStart }} 至 {{ item.plannedFinish }}</p><progress :value="item.completionPercent" max="100" :aria-label="`${item.name}完成比例`" /><span>{{ item.completionPercent }}%</span></article></template>
            </template>
          </template>
        </section>
        <WorkHoursManager v-if="workHoursVisited" v-show="page === 'work-hours'" ref="workHours" :token="token" :company-id="session.activeCompanyId" :active="page === 'work-hours'" :mobile="true" :initial-project-id="workHoursProject" @changed="workHoursChanged" />
        <section v-if="page === 'mine'" aria-label="我的账号"><h1>我的账号</h1><dl class="mobile-project-facts"><dt>姓名</dt><dd>{{ session.displayName }}</dd><dt>UPLM账号</dt><dd>{{ session.username }}</dd><dt>公司</dt><dd>{{ session.activeCompanyName }}</dd><dt>界面版本</dt><dd>{{ version || '开发预览' }}</dd></dl><p>权限和审批规则沿用UPLM设置。工时保存后立即计入汇总；工时缺填在独立入口提醒。</p><button :disabled="pending" @click="reenter">重新验证钉钉身份</button><button :disabled="pending" @click="signOut">退出当前登录</button></section>
      </template>
    </main>
    <nav v-if="session" class="mobile-bottom-nav" aria-label="移动办公导航"><button v-for="item in visibleNavigation" :key="item.key" :aria-current="page === item.key ? 'page' : undefined" @click="navigate(item.key)"><component :is="item.icon" :size="21" /><span>{{ item.label }}</span><small v-if="item.key === 'tasks' && tasks.length + unread">{{ tasks.length + unread }}</small><small v-if="item.key === 'work-hours' && reminder.count.value">{{ reminder.count.value }}天</small></button></nav>
  </div>
</template>
