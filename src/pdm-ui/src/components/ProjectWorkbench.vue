<script setup lang="ts">
import { CalendarClock, ChevronRight, FolderKanban, ListChecks, RefreshCw, Search, TriangleAlert, UsersRound } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { readProjectPlanPortfolio } from '../api'
import type { PdmUser, ProjectPlan, ProjectPlanPortfolio, ProjectPlanTask, ProjectSummary } from '../types'

type Focus = 'all' | 'overdue' | 'due' | 'pending' | 'conflict'
type WorkbenchRow = {
  project: ProjectSummary
  role: string
  portfolio?: ProjectPlanPortfolio
}
type TaskContext = { projectId: string; projectCode: string; task: ProjectPlanTask }

const props = defineProps<{
  projects: ProjectSummary[]
  users: PdmUser[]
  token: string
  currentUsername: string
  canViewAll?: boolean
}>()
const emit = defineEmits<{ open: [projectId: string, tab?: 'overview' | 'project-plan'] }>()

const scope = ref<'mine' | 'all'>(props.canViewAll ? 'all' : 'mine')
const focus = ref<Focus>('all')
const query = ref('')
const loading = ref(false)
const loadFailures = ref<string[]>([])
const portfolios = ref<Record<string, ProjectPlanPortfolio>>({})
let loadGeneration = 0

const username = computed(() => props.currentUsername.trim().toLocaleLowerCase())
const rootProjects = computed(() => props.projects.filter(project => !project.parentProjectId))

function sameUser(candidate?: string | null) {
  return candidate?.trim().toLocaleLowerCase() === username.value
}

function childrenOf(root: ProjectSummary) {
  return props.projects.filter(project => project.parentProjectId === root.id || project.rootProjectId === root.id)
}

function roleOf(root: ProjectSummary) {
  if (sameUser(root.primaryProjectManager)) return '主项目经理'
  if ((root.collaborativeProjectManagers ?? []).some(sameUser)) return '协同项目经理'
  if (childrenOf(root).some(child => sameUser(child.primaryProjectManager))) return '子项目负责人'
  return '授权查看'
}

const scopedRoots = computed(() => rootProjects.value.filter(root => scope.value === 'all' && props.canViewAll || roleOf(root) !== '授权查看'))
const scopedRootIds = computed(() => scopedRoots.value.map(project => project.id).sort().join(','))

async function load() {
  const generation = ++loadGeneration
  const roots = [...scopedRoots.value]
  if (!roots.length) {
    portfolios.value = {}
    loadFailures.value = []
    return
  }
  loading.value = true
  const results = await Promise.all(roots.map(async project => {
    try {
      return { projectId: project.id, portfolio: await readProjectPlanPortfolio(project.id, props.token) }
    } catch {
      return { projectId: project.id, portfolio: undefined }
    }
  }))
  if (generation !== loadGeneration) return
  portfolios.value = Object.fromEntries(results.flatMap(result => result.portfolio ? [[result.projectId, result.portfolio]] : []))
  loadFailures.value = results.filter(result => !result.portfolio).map(result => result.projectId)
  loading.value = false
}

watch([scopedRootIds, () => props.token], load, { immediate: true })

function effectivePlan(plan: ProjectPlan) {
  return plan.changeDraftSource ?? plan
}

function approvedPlans(row: WorkbenchRow) {
  return (row.portfolio?.projects ?? []).flatMap(item => item.plan?.approvalStatus === 'Approved' ? [effectivePlan(item.plan)] : [])
}

function allPlans(row: WorkbenchRow) {
  return (row.portfolio?.projects ?? []).flatMap(item => item.plan ? [item.plan] : [])
}

function activeTasks(row: WorkbenchRow) {
  const seen = new Set<string>()
  return approvedPlans(row).flatMap(plan => plan.tasks).filter(task => {
    const key = task.sourceTaskId || task.id
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })
}

const rows = computed<WorkbenchRow[]>(() => scopedRoots.value.map(project => ({
  project,
  role: roleOf(project),
  portfolio: portfolios.value[project.id],
})))

function dateNumber(value: string) {
  const [year, month, day] = value.split('-').map(Number)
  return Date.UTC(year!, month! - 1, day!)
}

function dayDiff(left: string, right: string) {
  return Math.round((dateNumber(right) - dateNumber(left)) / 86_400_000)
}

function dateOffset(value: string, days: number) {
  const date = new Date(dateNumber(value) + days * 86_400_000)
  return date.toISOString().slice(0, 10)
}

function todayKey() {
  const now = new Date()
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
}

const today = computed(todayKey)
const dueLimit = computed(() => dateOffset(today.value, 7))

function projectBounds(row: WorkbenchRow) {
  const tasks = activeTasks(row)
  if (!tasks.length) return { start: undefined, finish: undefined }
  return {
    start: tasks.reduce((value, task) => task.plannedStart < value ? task.plannedStart : value, tasks[0]!.plannedStart),
    finish: tasks.reduce((value, task) => task.plannedFinish > value ? task.plannedFinish : value, tasks[0]!.plannedFinish),
  }
}

function isOverdue(task: ProjectPlanTask) {
  return task.isRequired && task.status !== 'Completed' && task.plannedFinish < today.value
}

function isDueSoon(task: ProjectPlanTask) {
  return task.status !== 'Completed' && task.plannedFinish >= today.value && task.plannedFinish <= dueLimit.value
}

function hasPending(row: WorkbenchRow) {
  return allPlans(row).some(plan => plan.approvalStatus === 'Pending' || plan.changeRequest?.status === 'Pending')
}

function hasApprovedPlan(row: WorkbenchRow) {
  return approvedPlans(row).length > 0
}

function stageName(row: WorkbenchRow, code?: string) {
  if (!code) return '未生效'
  for (const plan of approvedPlans(row)) {
    const definition = plan.stages?.find(stage => stage.code === code)
    if (definition?.name) return definition.name
  }
  const fallback: Record<string, string> = { Design: '设计', Preparation: '备料', Assembly: '装配', Commissioning: '调试', Acceptance: '验收发货', Paused: '暂停', Cancelled: '取消', Terminated: '终止' }
  return fallback[code] ?? code
}

function nearestNode(row: WorkbenchRow) {
  return activeTasks(row)
    .filter(task => task.status !== 'Completed' && (task.isRequired || task.isMilestone))
    .sort((left, right) => left.plannedFinish.localeCompare(right.plannedFinish))[0]
}

const taskContexts = computed<TaskContext[]>(() => rows.value.flatMap(row => activeTasks(row)
  .filter(task => task.status !== 'Completed' && task.assignee)
  .map(task => ({ projectId: row.project.id, projectCode: row.project.code, task }))))

const resourceConflicts = computed(() => {
  const byPerson = new Map<string, { username: string; projects: Set<string>; projectIds: Set<string>; tasks: Set<string>; starts: string[]; finishes: string[] }>()
  const tasks = taskContexts.value
  for (let leftIndex = 0; leftIndex < tasks.length; leftIndex++) {
    const left = tasks[leftIndex]!
    for (let rightIndex = leftIndex + 1; rightIndex < tasks.length; rightIndex++) {
      const right = tasks[rightIndex]!
      if (left.projectId === right.projectId || !sameAssignee(left.task.assignee, right.task.assignee)) continue
      const start = left.task.plannedStart > right.task.plannedStart ? left.task.plannedStart : right.task.plannedStart
      const finish = left.task.plannedFinish < right.task.plannedFinish ? left.task.plannedFinish : right.task.plannedFinish
      if (start > finish) continue
      const key = left.task.assignee!.trim().toLocaleLowerCase()
      const item = byPerson.get(key) ?? { username: left.task.assignee!, projects: new Set<string>(), projectIds: new Set<string>(), tasks: new Set<string>(), starts: [], finishes: [] }
      item.projects.add(left.projectCode); item.projects.add(right.projectCode)
      item.projectIds.add(left.projectId); item.projectIds.add(right.projectId)
      item.tasks.add(left.task.id); item.tasks.add(right.task.id)
      item.starts.push(start); item.finishes.push(finish)
      byPerson.set(key, item)
    }
  }
  return [...byPerson.values()].map(item => ({
    username: item.username,
    name: props.users.find(user => sameAssignee(user.username, item.username))?.displayName || item.username,
    projects: [...item.projects].sort((left, right) => left.localeCompare(right, 'zh-CN', { numeric: true })),
    projectIds: item.projectIds,
    taskCount: item.tasks.size,
    start: item.starts.sort()[0]!,
    finish: item.finishes.sort().at(-1)!,
  })).sort((left, right) => right.projects.length - left.projects.length || left.name.localeCompare(right.name, 'zh-CN'))
})

function sameAssignee(left?: string | null, right?: string | null) {
  return Boolean(left && right && left.trim().localeCompare(right.trim(), undefined, { sensitivity: 'accent' }) === 0)
}

const conflictProjectIds = computed(() => new Set(resourceConflicts.value.flatMap(item => [...item.projectIds])))
const overdueRows = computed(() => rows.value.filter(row => activeTasks(row).some(isOverdue)))
const dueSoonTasks = computed(() => rows.value.flatMap(row => activeTasks(row).filter(task => task.isMilestone && isDueSoon(task))))
const pendingRows = computed(() => rows.value.filter(hasPending))
const activeRows = computed(() => rows.value.filter(row => !/(已完成|完成|关闭|终止|取消)/.test(row.project.stage)))

const metricCards = computed(() => [
  { key: 'all' as Focus, label: '进行中项目', value: activeRows.value.length, detail: '当前管理范围', icon: FolderKanban, tone: 'blue' },
  { key: 'overdue' as Focus, label: '逾期项目', value: overdueRows.value.length, detail: '必需任务已超期', icon: TriangleAlert, tone: 'red' },
  { key: 'due' as Focus, label: '7天内节点', value: dueSoonTasks.value.length, detail: '未完成里程碑', icon: CalendarClock, tone: 'orange' },
  { key: 'pending' as Focus, label: '待审批／变更', value: pendingRows.value.length, detail: '计划尚未生效', icon: ListChecks, tone: 'purple' },
  { key: 'conflict' as Focus, label: '资源冲突人员', value: resourceConflicts.value.length, detail: '跨项目日期重叠', icon: UsersRound, tone: 'teal' },
])

const filteredRows = computed(() => {
  const normalizedQuery = query.value.trim().toLocaleLowerCase()
  return rows.value.filter(row => {
    if (normalizedQuery && ![row.project.code, row.project.name, row.project.customerName, row.role].some(value => value?.toLocaleLowerCase().includes(normalizedQuery))) return false
    if (focus.value === 'overdue') return activeTasks(row).some(isOverdue)
    if (focus.value === 'due') return activeTasks(row).some(task => task.isMilestone && isDueSoon(task))
    if (focus.value === 'pending') return hasPending(row)
    if (focus.value === 'conflict') return conflictProjectIds.value.has(row.project.id)
    return true
  })
})

function health(row: WorkbenchRow) {
  if (activeTasks(row).some(isOverdue)) return { label: '逾期', tone: 'danger' }
  if (hasPending(row)) return { label: '待审批', tone: 'warning' }
  if (!hasApprovedPlan(row)) return { label: '未生效', tone: 'neutral' }
  if (activeTasks(row).some(isDueSoon)) return { label: '关注', tone: 'warning' }
  return { label: '正常', tone: 'success' }
}

const priorityItems = computed(() => {
  const items: Array<{ key: string; projectId: string; projectCode: string; projectName: string; title: string; detail: string; tone: string; order: number }> = []
  for (const row of rows.value) {
    for (const task of activeTasks(row).filter(isOverdue)) items.push({
      key: `overdue-${row.project.id}-${task.id}`, projectId: row.project.id, projectCode: row.project.code, projectName: row.project.name,
      title: task.name, detail: `已逾期 ${Math.abs(dayDiff(task.plannedFinish, today.value))} 天 · ${task.plannedFinish}`, tone: 'danger', order: 0,
    })
    for (const task of activeTasks(row).filter(task => task.isMilestone && isDueSoon(task))) items.push({
      key: `due-${row.project.id}-${task.id}`, projectId: row.project.id, projectCode: row.project.code, projectName: row.project.name,
      title: task.name, detail: task.plannedFinish === today.value ? '今天到期' : `${dayDiff(today.value, task.plannedFinish)} 天后到期 · ${task.plannedFinish}`, tone: 'warning', order: 1,
    })
    if (hasPending(row)) items.push({ key: `pending-${row.project.id}`, projectId: row.project.id, projectCode: row.project.code, projectName: row.project.name, title: '项目计划待审批或变更确认', detail: '进入项目计划查看当前审批状态', tone: 'info', order: 2 })
    const missing = activeTasks(row).filter(task => task.isRequired && task.status !== 'Completed' && !task.assignee)
    if (missing.length) items.push({ key: `missing-${row.project.id}`, projectId: row.project.id, projectCode: row.project.code, projectName: row.project.name, title: `${missing.length} 项必需任务未分配责任人`, detail: '进入项目计划补充分工', tone: 'neutral', order: 3 })
  }
  return items.sort((left, right) => left.order - right.order || left.detail.localeCompare(right.detail) || left.projectCode.localeCompare(right.projectCode, 'zh-CN', { numeric: true })).slice(0, 10)
})

function stageSegments(row: WorkbenchRow) {
  const segments = new Map<string, { code: string; name: string; start: string; finish: string; sort: number }>()
  for (const plan of approvedPlans(row)) {
    for (const task of plan.tasks) {
      const current = segments.get(task.stage)
      const sort = plan.stages?.findIndex(stage => stage.code === task.stage) ?? 999
      if (!current) segments.set(task.stage, { code: task.stage, name: stageName(row, task.stage), start: task.plannedStart, finish: task.plannedFinish, sort: sort < 0 ? 999 : sort })
      else {
        if (task.plannedStart < current.start) current.start = task.plannedStart
        if (task.plannedFinish > current.finish) current.finish = task.plannedFinish
        current.sort = Math.min(current.sort, sort < 0 ? 999 : sort)
      }
    }
  }
  return [...segments.values()].sort((left, right) => left.start.localeCompare(right.start) || left.sort - right.sort)
}

const timelineBounds = computed(() => {
  const bounds = filteredRows.value.map(projectBounds).filter((item): item is { start: string; finish: string } => Boolean(item.start && item.finish))
  if (!bounds.length) return { start: dateOffset(today.value, -7), finish: dateOffset(today.value, 56) }
  const start = bounds.map(item => item.start).sort()[0]!
  const finish = bounds.map(item => item.finish).sort().at(-1)!
  return { start: dateOffset(start, -3), finish: dateOffset(finish, 3) }
})
const timelineDays = computed(() => Math.max(1, dayDiff(timelineBounds.value.start, timelineBounds.value.finish) + 1))
const timelineWidth = computed(() => Math.max(760, timelineDays.value * 11))
const timelineTicks = computed(() => Array.from({ length: Math.ceil(timelineDays.value / 7) }, (_, index) => {
  const date = dateOffset(timelineBounds.value.start, index * 7)
  return { date, left: `${index * 7 / timelineDays.value * 100}%`, label: `${Number(date.slice(5, 7))}/${Number(date.slice(8, 10))}` }
}))

function barStyle(start: string, finish: string) {
  const left = Math.max(0, dayDiff(timelineBounds.value.start, start))
  const width = Math.max(1, dayDiff(start, finish) + 1)
  return { left: `${left / timelineDays.value * 100}%`, width: `${width / timelineDays.value * 100}%` }
}

function stageTone(index: number) {
  return `tone-${index % 5}`
}
</script>

<template>
  <section class="pdm-project-workbench" aria-label="项目工作台">
    <header class="pdm-project-workbench__header">
      <div><h1>项目工作台</h1><p>集中查看并行项目、近期节点与跨项目资源冲突；统计仅使用当前生效计划。</p></div>
      <div class="pdm-project-workbench__filters">
        <label v-if="canViewAll">范围<select v-model="scope" aria-label="项目范围"><option value="all">全部有权限项目</option><option value="mine">我负责／协同</option></select></label>
        <label>状态<select v-model="focus" aria-label="关注状态"><option value="all">全部状态</option><option value="overdue">逾期项目</option><option value="due">7天内节点</option><option value="pending">待审批／变更</option><option value="conflict">资源冲突</option></select></label>
        <label class="pdm-project-workbench__search"><Search :size="14" /><input v-model="query" type="search" aria-label="搜索工作台项目" placeholder="项目号、名称、客户"></label>
        <button type="button" class="pdm-secondary-action" :disabled="loading" @click="load"><RefreshCw :size="14" />刷新</button>
      </div>
    </header>

    <div class="pdm-project-workbench__metrics" aria-label="项目关键指标">
      <button v-for="card in metricCards" :key="card.key" type="button" class="pdm-project-workbench__metric" :class="[`is-${card.tone}`, { 'is-active': focus === card.key }]" :aria-pressed="focus === card.key" @click="focus = card.key">
        <span><component :is="card.icon" :size="17" /></span><div><small>{{ card.label }}</small><strong>{{ card.value }}</strong><em>{{ card.detail }}</em></div>
      </button>
    </div>

    <p v-if="loadFailures.length" class="pdm-project-workbench__warning" role="status">{{ loadFailures.length }} 个项目的计划暂时无法读取，其基本信息仍保留显示。</p>

    <div class="pdm-project-workbench__upper">
      <article class="pdm-panel pdm-project-workbench__attention" aria-label="重点关注">
        <header><div><h2>重点关注</h2><small>按逾期、即将到期、待审批、分工缺失排序</small></div><b>{{ priorityItems.length }}</b></header>
        <div v-if="loading && !Object.keys(portfolios).length" class="pdm-project-workbench__empty">正在读取项目计划…</div>
        <div v-else-if="priorityItems.length" class="pdm-project-workbench__attention-list">
          <button v-for="item in priorityItems" :key="item.key" type="button" @click="emit('open', item.projectId, 'project-plan')">
            <i :class="`is-${item.tone}`" /><span><strong>{{ item.title }}</strong><small>{{ item.projectCode }} · {{ item.projectName }}</small></span><em>{{ item.detail }}</em><ChevronRight :size="14" />
          </button>
        </div>
        <div v-else class="pdm-project-workbench__empty">当前没有需要优先处理的计划事项。</div>
      </article>

      <article class="pdm-panel pdm-project-workbench__resources" aria-label="跨项目资源冲突">
        <header><div><h2>资源冲突</h2><small>按人员统计跨项目日期重叠，不累计任务两两组合数量</small></div><b>{{ resourceConflicts.length }} 人</b></header>
        <div v-if="resourceConflicts.length" class="pdm-project-workbench__resource-list">
          <div v-for="item in resourceConflicts.slice(0, 8)" :key="item.username">
            <span><strong>{{ item.name }}</strong><small>{{ item.projects.join('、') }}</small></span>
            <em>{{ item.projects.length }} 个项目 · {{ item.taskCount }} 项任务<br>{{ item.start }} ～ {{ item.finish }}</em>
          </div>
        </div>
        <div v-else class="pdm-project-workbench__empty">未发现同一人员在不同项目间的日期重叠。</div>
      </article>
    </div>

    <article class="pdm-panel pdm-project-workbench__portfolio" aria-label="项目总览">
      <header><div><h2>项目总览</h2><small>{{ filteredRows.length }} 个主项目</small></div><button v-if="focus !== 'all' || query" type="button" class="pdm-text-action" @click="focus='all'; query=''">清除筛选</button></header>
      <div class="pdm-project-workbench__table-wrap">
        <table>
          <thead><tr><th>项目</th><th>我的角色</th><th>客户</th><th>当前阶段</th><th>交付进度</th><th>计划日期</th><th>最近节点</th><th>状态</th></tr></thead>
          <tbody>
            <tr v-for="row in filteredRows" :key="row.project.id" @click="emit('open', row.project.id, 'overview')">
              <td><button type="button" :aria-label="`进入项目 ${row.project.code}`"><strong>{{ row.project.code }}</strong><small>{{ row.project.name }}</small></button></td>
              <td>{{ row.role }}</td><td>{{ row.project.customerName || '—' }}</td><td>{{ stageName(row, row.portfolio?.currentStage) }}</td>
              <td><span class="pdm-project-workbench__progress"><i :style="{ width: `${row.portfolio?.completionPercent ?? 0}%` }" /><b>{{ row.portfolio?.completionPercent ?? 0 }}%</b></span></td>
              <td><span v-if="projectBounds(row).start">{{ projectBounds(row).start }}<br>{{ projectBounds(row).finish }}</span><span v-else>—</span></td>
              <td><span v-if="nearestNode(row)"><strong>{{ nearestNode(row)?.name }}</strong><small>{{ nearestNode(row)?.plannedFinish }}</small></span><span v-else>—</span></td>
              <td><span class="pdm-project-workbench__health" :class="`is-${health(row).tone}`">{{ health(row).label }}</span></td>
            </tr>
          </tbody>
        </table>
        <div v-if="!filteredRows.length" class="pdm-project-workbench__empty">没有符合当前范围和筛选条件的项目。</div>
      </div>
    </article>

    <article class="pdm-panel pdm-project-workbench__gantt" aria-label="跨项目阶段甘特图">
      <header><div><h2>跨项目阶段甘特图</h2><small>一行一个主项目；阶段日期由生效子任务首尾节点汇总</small></div><span>周视图</span></header>
      <div class="pdm-project-workbench__gantt-scroll">
        <div class="pdm-project-workbench__gantt-grid" :style="{ '--timeline-width': `${timelineWidth}px` }">
          <div class="pdm-project-workbench__gantt-name is-head">项目</div>
          <div class="pdm-project-workbench__axis" :style="{ width: `${timelineWidth}px` }"><span v-for="tick in timelineTicks" :key="tick.date" :style="{ left: tick.left }">{{ tick.label }}</span></div>
          <template v-for="row in filteredRows" :key="row.project.id">
            <button type="button" class="pdm-project-workbench__gantt-name" @click="emit('open', row.project.id, 'project-plan')"><strong>{{ row.project.code }}</strong><small>{{ row.project.name }}</small></button>
            <div class="pdm-project-workbench__gantt-line" :style="{ width: `${timelineWidth}px` }">
              <i v-if="today >= timelineBounds.start && today <= timelineBounds.finish" class="is-today" :style="{ left: `${dayDiff(timelineBounds.start, today) / timelineDays * 100}%` }" />
              <span v-for="(segment, index) in stageSegments(row)" :key="segment.code" class="pdm-project-workbench__stage" :class="stageTone(index)" :style="barStyle(segment.start, segment.finish)" :title="`${segment.name}：${segment.start} ～ ${segment.finish}`"><b>{{ segment.name }}</b></span>
              <em v-if="!stageSegments(row).length">尚无生效计划</em>
            </div>
          </template>
        </div>
      </div>
      <footer><span><i class="tone-0" />设计</span><span><i class="tone-1" />备料</span><span><i class="tone-2" />装配</span><span><i class="tone-3" />调试</span><span><i class="tone-4" />验收发货</span><em>红线为今天</em></footer>
    </article>
  </section>
</template>

<style scoped>
.pdm-project-workbench{min-height:0;height:100%;display:flex;flex-direction:column;gap:5px;overflow:auto;color:var(--pdm-text)}
.pdm-project-workbench__header{display:flex;align-items:center;justify-content:space-between;gap:16px;padding:11px 14px;border:1px solid var(--pdm-border);border-radius:8px;background:#fff}
.pdm-project-workbench__header h1,.pdm-project-workbench header h2{margin:0;color:#17212b;font-size:16px;font-weight:600}.pdm-project-workbench__header p{margin:3px 0 0;color:var(--pdm-muted);font-size:11px}
.pdm-project-workbench__filters{display:flex;align-items:flex-end;justify-content:flex-end;gap:6px}.pdm-project-workbench__filters label{display:grid;gap:3px;color:var(--pdm-muted);font-size:9px}.pdm-project-workbench__filters select,.pdm-project-workbench__search{box-sizing:border-box;height:31px;border:1px solid var(--pdm-border);border-radius:6px;background:#fff;color:var(--pdm-text);font-size:11px}.pdm-project-workbench__filters select{min-width:112px;padding:0 7px}.pdm-project-workbench__search{width:190px;display:flex;align-items:center;gap:5px;padding:0 8px}.pdm-project-workbench__search input{min-width:0;width:100%;border:0;outline:0;background:transparent;font:inherit}
.pdm-project-workbench__metrics{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:5px}.pdm-project-workbench__metric{min-height:72px;display:flex;align-items:center;gap:10px;padding:10px 12px;border:1px solid var(--pdm-border);border-radius:8px;background:#fff;color:var(--pdm-text);text-align:left;box-shadow:0 3px 12px var(--pdm-shadow)}.pdm-project-workbench__metric>span{width:31px;height:31px;display:grid;flex:0 0 31px;place-items:center;border-radius:8px;background:#eff6ff;color:#2563eb}.pdm-project-workbench__metric>div{min-width:0;display:grid;grid-template-columns:1fr auto;align-items:end;gap:1px 8px}.pdm-project-workbench__metric small{color:var(--pdm-muted);font-size:10px}.pdm-project-workbench__metric strong{grid-row:span 2;color:#17212b;font-size:24px;line-height:1}.pdm-project-workbench__metric em{color:#94a3b8;font-size:9px;font-style:normal;white-space:nowrap}.pdm-project-workbench__metric:hover,.pdm-project-workbench__metric.is-active{border-color:var(--shell-accent);box-shadow:0 0 0 2px var(--shell-accent-soft)}.pdm-project-workbench__metric.is-red>span{background:#fff0ef;color:#dc2626}.pdm-project-workbench__metric.is-orange>span{background:#fff7ed;color:#ea580c}.pdm-project-workbench__metric.is-purple>span{background:#f5f3ff;color:#7c3aed}.pdm-project-workbench__metric.is-teal>span{background:#ecfdf5;color:#0f9f91}
.pdm-project-workbench__warning{margin:0;padding:6px 10px;border:1px solid #fde68a;border-radius:6px;background:#fffbeb;color:#b45309;font-size:10px}
.pdm-project-workbench__upper{display:grid;grid-template-columns:minmax(0,1.35fr) minmax(310px,.65fr);gap:5px}.pdm-project-workbench article>header{min-height:42px;display:flex;align-items:center;justify-content:space-between;gap:10px;padding:7px 11px;border-bottom:1px solid var(--pdm-border-soft)}.pdm-project-workbench article>header h2{font-size:12px}.pdm-project-workbench article>header small{display:block;margin-top:2px;color:var(--pdm-muted);font-size:9px}.pdm-project-workbench article>header>b{color:var(--shell-accent);font-size:12px}
.pdm-project-workbench__attention,.pdm-project-workbench__resources{min-height:180px;overflow:hidden}.pdm-project-workbench__attention-list{max-height:194px;overflow:auto}.pdm-project-workbench__attention-list button{width:100%;min-height:39px;display:grid;grid-template-columns:7px minmax(170px,1fr) auto 14px;align-items:center;gap:8px;padding:5px 10px;border:0;border-bottom:1px solid var(--pdm-border-soft);background:#fff;color:var(--pdm-text);text-align:left}.pdm-project-workbench__attention-list button:hover{background:var(--pdm-theme-accent-soft)}.pdm-project-workbench__attention-list i{width:6px;height:6px;border-radius:50%;background:#94a3b8}.pdm-project-workbench__attention-list i.is-danger{background:#dc2626}.pdm-project-workbench__attention-list i.is-warning{background:#f59e0b}.pdm-project-workbench__attention-list i.is-info{background:#3b82f6}.pdm-project-workbench__attention-list span{min-width:0;display:flex;flex-direction:column}.pdm-project-workbench__attention-list strong,.pdm-project-workbench__resource-list strong{overflow:hidden;font-size:10px;font-weight:600;text-overflow:ellipsis;white-space:nowrap}.pdm-project-workbench__attention-list small,.pdm-project-workbench__resource-list small{overflow:hidden;color:var(--pdm-muted);font-size:9px;text-overflow:ellipsis;white-space:nowrap}.pdm-project-workbench__attention-list em{color:var(--pdm-muted);font-size:9px;font-style:normal;white-space:nowrap}
.pdm-project-workbench__resource-list{max-height:194px;overflow:auto}.pdm-project-workbench__resource-list>div{min-height:42px;display:flex;align-items:center;justify-content:space-between;gap:10px;padding:6px 10px;border-bottom:1px solid var(--pdm-border-soft)}.pdm-project-workbench__resource-list span{min-width:0;display:flex;flex-direction:column}.pdm-project-workbench__resource-list em{flex:0 0 auto;color:#b45309;font-size:9px;font-style:normal;text-align:right;line-height:1.45}
.pdm-project-workbench__empty{min-height:100px;display:grid;place-items:center;padding:14px;color:var(--pdm-muted);font-size:10px;text-align:center}
.pdm-project-workbench__portfolio{flex:0 0 auto;overflow:hidden}.pdm-project-workbench__table-wrap{max-height:260px;overflow:auto}.pdm-project-workbench table{width:100%;border-collapse:collapse;font-size:10px}.pdm-project-workbench th{position:sticky;top:0;z-index:1;padding:7px 9px;background:#f8fafc;color:var(--pdm-muted);font-size:9px;font-weight:500;text-align:left;white-space:nowrap}.pdm-project-workbench td{padding:7px 9px;border-top:1px solid var(--pdm-border-soft);vertical-align:middle}.pdm-project-workbench tbody tr{cursor:pointer}.pdm-project-workbench tbody tr:hover{background:var(--pdm-theme-accent-soft)}.pdm-project-workbench td:first-child button{min-width:135px;display:flex;flex-direction:column;padding:0;border:0;background:transparent;color:var(--pdm-text);text-align:left}.pdm-project-workbench td:first-child strong,.pdm-project-workbench td:nth-child(7) strong{font-size:10px}.pdm-project-workbench td:first-child small,.pdm-project-workbench td:nth-child(7) small{max-width:180px;overflow:hidden;color:var(--pdm-muted);font-size:9px;text-overflow:ellipsis;white-space:nowrap}.pdm-project-workbench td:nth-child(6)>span,.pdm-project-workbench td:nth-child(7)>span{display:flex;flex-direction:column;line-height:1.35}
.pdm-project-workbench__progress{position:relative;width:86px;height:16px;display:grid;place-items:center;overflow:hidden;border:1px solid #bed3df;border-radius:8px;background:#edf3f6}.pdm-project-workbench__progress i{position:absolute;inset:0 auto 0 0;background:#35aaa2}.pdm-project-workbench__progress b{position:relative;font-size:9px}.pdm-project-workbench__health{display:inline-flex;padding:2px 7px;border-radius:9px;background:#f1f5f9;color:#64748b;font-size:9px}.pdm-project-workbench__health.is-danger{background:#fff0ef;color:#dc2626}.pdm-project-workbench__health.is-warning{background:#fff7ed;color:#b45309}.pdm-project-workbench__health.is-success{background:#ecfdf5;color:#078071}
.pdm-project-workbench__gantt{flex:0 0 auto;min-height:190px;overflow:hidden}.pdm-project-workbench__gantt>header>span{color:var(--pdm-muted);font-size:9px}.pdm-project-workbench__gantt-scroll{max-height:270px;overflow:auto}.pdm-project-workbench__gantt-grid{min-width:calc(170px + var(--timeline-width));display:grid;grid-template-columns:170px var(--timeline-width);align-items:stretch}.pdm-project-workbench__gantt-name{min-height:38px;display:flex;flex-direction:column;justify-content:center;padding:5px 10px;border:0;border-right:1px solid var(--pdm-border);border-bottom:1px solid var(--pdm-border-soft);background:#fff;color:var(--pdm-text);text-align:left}.pdm-project-workbench__gantt-name strong{font-size:9px}.pdm-project-workbench__gantt-name small{max-width:145px;overflow:hidden;color:var(--pdm-muted);font-size:8px;text-overflow:ellipsis;white-space:nowrap}.pdm-project-workbench__gantt-name:hover{background:var(--pdm-theme-accent-soft)}.pdm-project-workbench__gantt-name.is-head{min-height:27px;background:#f8fafc;color:var(--pdm-muted);font-size:9px}.pdm-project-workbench__axis{position:relative;height:27px;border-bottom:1px solid var(--pdm-border);background:repeating-linear-gradient(90deg,#f8fafc 0,#f8fafc 76px,#dbe5ee 76px,#dbe5ee 77px)}.pdm-project-workbench__axis span{position:absolute;top:7px;color:var(--pdm-muted);font-size:8px;transform:translateX(3px)}.pdm-project-workbench__gantt-line{position:relative;min-height:38px;border-bottom:1px solid var(--pdm-border-soft);background:repeating-linear-gradient(90deg,#fff 0,#fff 76px,#e8eef4 76px,#e8eef4 77px)}.pdm-project-workbench__gantt-line>.is-today{position:absolute;z-index:2;top:0;bottom:0;width:1px;background:#ef4444}.pdm-project-workbench__stage{position:absolute;top:10px;height:18px;min-width:4px;display:flex;align-items:center;justify-content:center;overflow:hidden;border:1px solid #3b82f6;border-radius:4px;background:#dbeafe;color:#1e40af}.pdm-project-workbench__stage b{overflow:hidden;padding:0 4px;font-size:8px;text-overflow:ellipsis;white-space:nowrap}.pdm-project-workbench__stage.tone-1{border-color:#d97706;background:#fef3c7;color:#92400e}.pdm-project-workbench__stage.tone-2{border-color:#0f9f91;background:#ccfbf1;color:#115e59}.pdm-project-workbench__stage.tone-3{border-color:#7c3aed;background:#ede9fe;color:#5b21b6}.pdm-project-workbench__stage.tone-4{border-color:#db2777;background:#fce7f3;color:#9d174d}.pdm-project-workbench__gantt-line>em{position:absolute;top:13px;left:10px;color:#94a3b8;font-size:8px;font-style:normal}.pdm-project-workbench__gantt footer{display:flex;align-items:center;gap:12px;padding:6px 10px;color:var(--pdm-muted);font-size:8px}.pdm-project-workbench__gantt footer span{display:flex;align-items:center;gap:4px}.pdm-project-workbench__gantt footer i{width:10px;height:4px;border-radius:2px;background:#dbeafe}.pdm-project-workbench__gantt footer i.tone-1{background:#fef3c7}.pdm-project-workbench__gantt footer i.tone-2{background:#ccfbf1}.pdm-project-workbench__gantt footer i.tone-3{background:#ede9fe}.pdm-project-workbench__gantt footer i.tone-4{background:#fce7f3}.pdm-project-workbench__gantt footer em{margin-left:auto;font-style:normal}
@media(max-width:1100px){.pdm-project-workbench__header{align-items:flex-start;flex-direction:column}.pdm-project-workbench__filters{width:100%;justify-content:flex-start;flex-wrap:wrap}.pdm-project-workbench__metrics{grid-template-columns:repeat(3,minmax(0,1fr))}.pdm-project-workbench__upper{grid-template-columns:1fr}.pdm-project-workbench__attention,.pdm-project-workbench__resources{min-height:160px}}
@media(max-width:720px){.pdm-project-workbench__metrics{grid-template-columns:repeat(2,minmax(0,1fr))}.pdm-project-workbench__search{width:min(100%,220px)}.pdm-project-workbench__filters label{flex:1 1 120px}.pdm-project-workbench__filters select{width:100%}.pdm-project-workbench__attention-list button{grid-template-columns:7px minmax(120px,1fr) 14px}.pdm-project-workbench__attention-list em{display:none}}
</style>
