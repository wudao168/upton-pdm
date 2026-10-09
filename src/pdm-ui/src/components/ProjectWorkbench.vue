<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { ChevronDown, ChevronRight, RefreshCw, Search } from '@lucide/vue'
import { closeWorkbenchRisk, readProjectWorkbench, registerWorkbenchRisk } from '../api'
import type { PdmUser } from '../types'
import { addDays, deliveryDate, effectiveTasks, isEffective, manualRiskAction, nextTask, progress, projectActions, relevantProject, resourceOverlaps, splitPeople, stageName, validDate } from '../projectWorkbench'
import type { WorkbenchAction, WorkbenchData, WorkbenchProject, WorkbenchRisk, WorkbenchTab, WorkbenchView } from '../projectWorkbench'

const props = defineProps<{ users: PdmUser[]; token: string; currentUsername: string; companyId: string }>()
const emit = defineEmits<{ open: [projectId: string, tab?: WorkbenchTab, taskId?: string] }>()
const data = ref<WorkbenchData>()
const view = ref<WorkbenchView | ''>('')
const days = ref(14)
const loading = ref(false)
const error = ref('')
const query = ref('')
const focus = ref('all')
const display = ref('projects')
const group = ref('全部')
const expanded = ref(new Set<string>())
const riskDialog = ref(false)
const saving = ref(false)
const showClosed = ref(false)
const draft = ref({ projectId: '', title: '', impact: '', owner: props.currentUsername, dueDate: '', severity: 'warning' })
const memoryKey = computed(() => 'pdm.workbench.' + props.companyId + '.' + props.currentUsername)
let generation = 0
async function load(requested = view.value) {
  const current = ++generation
  loading.value = true
  error.value = ''
  try {
    const response = await readProjectWorkbench(requested, props.token)
    if (current !== generation) return
    data.value = response
    view.value = response.view
    localStorage.setItem(memoryKey.value, response.view)
  } catch (reason) {
    if (current !== generation) return
    // A remembered role may have been removed since the last visit.
    if (requested && (reason as { status?: number }).status === 403) { await load(''); return }
    data.value = undefined
    error.value = reason instanceof Error ? reason.message : '工作台读取失败，请重试'
  } finally { if (current === generation) loading.value = false }
}
watch([() => props.token, () => props.companyId, () => props.currentUsername], () => {
  data.value = undefined
  view.value = (localStorage.getItem(memoryKey.value) || '') as WorkbenchView | ''
  void load()
}, { immediate: true })
async function switchView() {
  focus.value = 'all'; group.value = '全部'; display.value = 'projects'; expanded.value.clear(); data.value = undefined
  await load()
}
const today = computed(() => new Date(Date.parse(data.value?.updatedAt || new Date().toISOString()) + 8 * 3600000).toISOString().slice(0, 10))
const limit = computed(() => addDays(today.value, days.value))
const role = computed(() => view.value || 'engineer')
const descriptions: Record<WorkbenchView, string> = {
  division: '本事业部项目的进度、任务、预算、人员安排与风险', manager: '我负责或协同项目的交付与待协调事项',
  engineer: '我参与项目的进度、分配给我的任务与风险', procurement: '已发布待下单、近期备料与交期风险',
  production: '当前及即将进入生产的项目、齐套与人员准备', sales: '我负责客户的项目进度、交付节点与风险', quality: '近期验收、整改与发货前资料准备',
}
const budgetVisible = computed(() => ['division', 'manager'].includes(role.value))
const resourcesVisible = computed(() => ['division', 'manager', 'production'].includes(role.value))
const allItems = computed(() => data.value?.projects ?? [])
const rootId = (item: WorkbenchProject) => allItems.value.some(parent => parent.project.id === (item.project.rootProjectId || item.project.parentProjectId)) ? item.project.rootProjectId || item.project.parentProjectId! : item.project.id
const relevantRoots = computed(() => new Set(allItems.value.filter(item => relevantProject(item, role.value, limit.value)).map(rootId)))
const items = computed(() => allItems.value.filter(item => relevantRoots.value.has(rootId(item))))
const roots = computed(() => items.value.filter(item => rootId(item) === item.project.id))
const risks = computed(() => (data.value?.risks ?? []).filter(risk => items.value.some(item => item.project.id === risk.projectId)))
const actions = computed(() => [
  ...items.value.flatMap(item => projectActions(item, role.value, today.value, days.value, props.currentUsername)),
  ...risks.value.filter(risk => !risk.closed).map(risk => manualRiskAction(risk, props.currentUsername, today.value)),
].sort((a, b) => a.priority - b.priority || (a.due || '9999').localeCompare(b.due || '9999')))
const resources = computed(() => resourceOverlaps(items.value, today.value, limit.value))
const related = (root: WorkbenchProject) => items.value.filter(item => rootId(item) === root.project.id)
const actionsFor = (item: WorkbenchProject) => actions.value.filter(action => action.projectId === item.project.id)
const isOverdue = (action: WorkbenchAction) => Boolean(action.due && action.due < today.value)
function matchesFocus(item: WorkbenchProject) {
  const list = actionsFor(item)
  if (focus.value === 'mine') return list.some(action => action.group === '待我处理')
  if (focus.value === 'overdue') return list.some(isOverdue)
  if (focus.value === 'risk') return list.some(action => action.kind === 'risk' || action.priority <= 1)
  if (focus.value === 'missing') return list.some(action => action.kind === 'missing')
  if (focus.value === 'upcoming') return list.some(action => action.upcoming)
  if (focus.value === 'overlap') return resources.value.some(person => person.overlapCount && person.tasks.some(task => task.item.project.id === item.project.id))
  return true
}
const filteredRoots = computed(() => roots.value.filter(root => related(root).some(item => matchesFocus(item)) && related(root).some(item =>
  [item.project.code, item.project.name, item.project.customerName].join(' ').toLowerCase().includes(query.value.trim().toLowerCase()))))
const filteredIds = computed(() => new Set(filteredRoots.value.flatMap(root => related(root).map(item => item.project.id))))
const visibleRows = computed(() => filteredRoots.value.flatMap(root => expanded.value.has(root.project.id) ? related(root) : [root]))
const cards = computed(() => [
  { key: 'all', label: '关注项目', value: roots.value.length, unit: '个主项目' },
  { key: 'mine', label: '待我处理', value: actions.value.filter(action => action.group === '待我处理').length, unit: '项' },
  { key: 'overdue', label: '逾期事项', value: actions.value.filter(isOverdue).length, unit: '项' },
  { key: 'risk', label: '风险项目', value: roots.value.filter(root => related(root).some(item => actionsFor(item).some(action => action.kind === 'risk' || action.priority <= 1))).length, unit: '个' },
  { key: 'upcoming', label: days.value + ' 天内准备', value: actions.value.filter(action => action.upcoming).length, unit: '项' },
  { key: 'missing', label: '待补充信息', value: roots.value.filter(root => related(root).some(item => actionsFor(item).some(action => action.kind === 'missing'))).length, unit: '个项目' },
])
const focusActions = computed(() => actions.value.filter(action => filteredIds.value.has(action.projectId) && (group.value === '全部' || action.group === group.value)))
const queues = computed(() => [
  { title: '当前待关注', items: focusActions.value.filter(action => !action.upcoming) },
  { title: '提前准备 · 未来 ' + days.value + ' 天', items: focusActions.value.filter(action => action.upcoming) },
])
const nameOf = (value?: string) => splitPeople(value).map(username => props.users.find(user => user.username.toLowerCase() === username)?.displayName || username).join('、') || '待安排'
const projectCode = (id: string) => allItems.value.find(item => item.project.id === id)?.project.code || ''
const money = (value?: number | null) => value == null ? '待补充' : value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
function status(item: WorkbenchProject) {
  const list = (rootId(item) === item.project.id ? related(item).flatMap(actionsFor) : actionsFor(item))
  if (list.some(action => action.priority === 0)) return { tone: 'danger', text: '逾期／风险' }
  if (list.some(action => action.kind === 'risk' || action.priority === 1)) return { tone: 'warning', text: '需协调' }
  if (list.some(action => action.kind === 'missing')) return { tone: 'muted', text: '待补充信息' }
  if (list.some(action => action.kind === 'approval')) return { tone: 'warning', text: '待审批' }
  return { tone: 'normal', text: '暂无已识别异常' }
}
function toggle(id: string) { if (expanded.value.has(id)) expanded.value.delete(id); else expanded.value.add(id) }
function go(action: WorkbenchAction) { emit('open', action.projectId, action.tab, action.taskId) }
function segments(item: WorkbenchProject) {
  const tasks = effectiveTasks(item).filter(task => validDate(task.plannedStart) && validDate(task.plannedFinish) && task.plannedStart <= limit.value && task.plannedFinish >= today.value)
  return [...new Set(tasks.map(task => task.stage))].map(code => {
    const dates = tasks.filter(task => task.stage === code)
    const start = dates.map(task => task.plannedStart).sort()[0]!
    const finish = dates.map(task => task.plannedFinish).sort().at(-1)!
    const offset = (date: string) => (Date.parse(date) - Date.parse(today.value)) / 86400000
    return { code, name: stageName(item, code), start, finish, left: Math.max(0, offset(start)) / (days.value + 1) * 100,
      width: (Math.min(days.value, offset(finish)) - Math.max(0, offset(start)) + 1) / (days.value + 1) * 100 }
  })
}
const registerable = computed(() => items.value.filter(item => item.canRegisterRisk))
const owners = computed(() => props.users.filter(user => user.isActive && (!user.companyId || user.companyId === props.companyId)))
function openRisk() {
  draft.value = { projectId: registerable.value[0]?.project.id || '', title: '', impact: '', owner: props.currentUsername, dueDate: limit.value, severity: 'warning' }
  riskDialog.value = true
}
async function saveRisk() {
  if (saving.value) return
  saving.value = true
  try { await registerWorkbenchRisk(draft.value.projectId, draft.value, props.token); riskDialog.value = false; await load(); ElMessage.success('风险已登记') }
  catch (reason) { ElMessage.error(reason instanceof Error ? reason.message : '登记失败') }
  finally { saving.value = false }
}
async function closeRisk(risk: WorkbenchRisk) {
  saving.value = true
  try { await closeWorkbenchRisk(risk.id, props.token); await load(); ElMessage.success('风险已关闭') }
  catch (reason) { ElMessage.error(reason instanceof Error ? reason.message : '关闭失败') }
  finally { saving.value = false }
}
</script>

<template>
  <section class="pdm-project-workbench" aria-label="项目工作台" :aria-busy="loading">
    <header class="wb-header pdm-panel">
      <div><h1>项目工作台</h1><p>{{ descriptions[role] }}</p></div>
      <div class="wb-filters">
        <label>岗位视角<select v-model="view" aria-label="岗位视角" :disabled="loading" @change="switchView"><option v-for="option in data?.views" :key="option.key" :value="option.key">{{ option.label }}</option></select></label>
        <label>关注周期<select v-model.number="days" aria-label="关注周期"><option :value="7">未来 7 天</option><option :value="14">未来 14 天</option><option :value="30">未来 30 天</option></select></label>
        <label class="wb-search"><Search :size="14" /><input v-model="query" type="search" aria-label="搜索工作台项目" placeholder="项目号、名称、客户"></label>
        <button class="pdm-secondary-action" :disabled="loading" @click="load()"><RefreshCw :size="14" />刷新</button>
      </div>
    </header>
    <p v-if="error" role="alert" class="wb-error">{{ error }} <button class="pdm-text-action" @click="load('')">重新读取</button></p>
    <p v-if="loading" role="status" class="wb-note">正在汇总授权项目与业务数据…</p>
    <template v-if="data">
      <p class="wb-note">{{ data.updatedAt ? new Date(data.updatedAt).toLocaleString('zh-CN') : '' }} 更新 · {{ today }} ～ {{ limit }} · 仅当前公司授权项目 · 进度按生效计划统计</p>
      <div class="wb-metrics" aria-label="项目关键指标">
        <button v-for="card in cards" :key="card.key" :aria-pressed="focus === card.key" :class="{ selected: focus === card.key }" @click="focus = card.key">
          <span>{{ card.label }}</span><strong>{{ card.value }}</strong><small>{{ card.unit }}</small>
        </button>
      </div>
      <div class="wb-toolbar"><div class="wb-tabs" aria-label="事项分组"><button v-for="label in ['全部', '待我处理', '待安排', '等待配合']" :key="label" :aria-pressed="group === label" @click="group = label">{{ label }}</button></div><button v-if="focus !== 'all' || query" class="pdm-text-action" @click="focus = 'all'; query = ''">清除筛选</button></div>
      <div class="wb-queues">
        <article v-for="queue in queues" :key="queue.title" class="pdm-panel wb-queue" :aria-label="queue.title">
          <header><h2>{{ queue.title }}</h2><span>{{ queue.items.length }} 项</span></header>
          <div class="wb-actions">
            <button v-for="action in queue.items" :key="action.id" class="wb-action" @click="go(action)">
              <i :class="'priority-' + action.priority" /><span><strong>{{ action.title }}</strong><small>{{ projectCode(action.projectId) }} · {{ nameOf(action.owner) }} · {{ action.group }}</small><small>{{ action.impact }}</small></span><em>{{ action.due || '待定日期' }}<ChevronRight :size="14" /></em>
            </button>
            <p v-if="!queue.items.length" class="wb-empty">当前范围内暂无事项</p>
          </div>
        </article>
      </div>
      <article class="pdm-panel wb-details">
        <header><div class="wb-tabs" aria-label="工作台详情视图"><button v-for="tab in [{ key: 'projects', label: '项目总览' }, ...(resourcesVisible ? [{ key: 'resources', label: '人员安排' }] : []), { key: 'gantt', label: '阶段甘特' }, { key: 'risks', label: '人工风险' }]" :key="tab.key" :aria-pressed="display === tab.key" @click="display = tab.key">{{ tab.label }}</button></div><button v-if="registerable.length" class="pdm-secondary-action" @click="openRisk">登记风险</button></header>
        <div v-if="display === 'projects'" class="wb-scroll" aria-label="项目总览">
          <table><thead><tr><th>项目</th><th v-if="view === 'sales'">客户</th><th>当前阶段</th><th>交付进度</th><th>下一节点</th><th>计划交付</th><th>风险／状态</th><th v-if="budgetVisible">预算 / 实际 / 已下单 / 预计 / 差额（元）</th><th v-if="view === 'procurement' || view === 'production'">采购与准备</th><th v-if="view === 'quality'">验收情况</th></tr></thead>
            <tbody><tr v-for="item in visibleRows" :key="item.project.id" :class="{ 'wb-child': rootId(item) !== item.project.id }">
              <td><div class="wb-project"><button v-if="rootId(item) === item.project.id && related(item).length > 1" :aria-label="'展开子项目 ' + item.project.code" :aria-expanded="expanded.has(item.project.id)" @click="toggle(item.project.id)"><component :is="expanded.has(item.project.id) ? ChevronDown : ChevronRight" :size="14" /></button><button class="pdm-text-action" :aria-label="'进入项目 ' + item.project.code" @click="emit('open', item.project.id, 'overview')"><b>{{ item.project.code }}</b><small>{{ item.project.name }}</small></button></div></td>
              <td v-if="view === 'sales'">{{ item.project.customerName || '待补充' }}</td><td>{{ stageName(item) }}<small v-if="!isEffective(item)">预估／未审批</small></td>
              <td><template v-if="progress(item) != null"><progress :value="progress(item)!" max="100" /> {{ progress(item) }}%</template><span v-else>待补充信息</span></td>
              <td><button v-if="nextTask(item)" class="pdm-text-action" @click="emit('open', item.project.id, 'project-plan', nextTask(item)?.id)">{{ nextTask(item)?.name }}<small>{{ nextTask(item)?.plannedFinish }}</small></button><span v-else>{{ isEffective(item) && effectiveTasks(item).length ? '任务已完成' : '待确认' }}</span></td>
              <td><button class="pdm-text-action" @click="emit('open', item.project.id, 'project-plan')">{{ deliveryDate(item) || '待补充' }}<small v-if="item.plan && !isEffective(item)">预估日期</small></button></td>
              <td><span class="wb-badge" :class="status(item).tone">{{ status(item).text }}</span><small v-if="rootId(item) === item.project.id && related(item).length > 1">含 {{ related(item).length - 1 }} 个子项目</small></td>
              <td v-if="budgetVisible"><button class="pdm-text-action wb-budget" @click="emit('open', item.project.id, 'budget')"><span>预算 {{ money(item.budget?.budget) }} · 实际 {{ money(item.budget?.actual) }}</span><span>下单 {{ money(item.budget?.ordered) }} · 预计 {{ money(item.budget?.estimated) }}</span><span>差额 {{ money(item.budget?.variance) }}</span></button></td>
              <td v-if="view === 'procurement' || view === 'production'"><button class="pdm-text-action" @click="emit('open', item.project.id, 'procurement')"><template v-if="item.procurement">待下单 {{ item.procurement.pendingOrder }} · 未齐 {{ item.procurement.missing }}<small>延期 {{ item.procurement.delayed }} · {{ item.procurement.refreshedAt ? '同步 ' + new Date(item.procurement.refreshedAt).toLocaleDateString('zh-CN') : '待核实' }}</small></template><span v-else>待补充信息</span></button></td>
              <td v-if="view === 'quality'"><button class="pdm-text-action" @click="emit('open', item.project.id, 'validation-plan')">{{ !item.quality || !item.quality.total ? '待补充验收计划' : '待验 ' + item.quality.pending + ' · 未通过 ' + item.quality.failed }}<small v-if="item.quality">验收记录 {{ item.quality.records }} 份</small></button></td>
            </tr></tbody>
          </table><p v-if="!visibleRows.length" class="wb-empty">没有符合当前岗位范围和筛选条件的项目</p>
        </div>
        <div v-else-if="display === 'resources' && resourcesVisible" class="wb-scroll" aria-label="人员安排">
          <p class="wb-note">仅统计未来 {{ days }} 天生效计划中的未完成任务。日期重叠需要人工确认，不代表超负荷。</p>
          <table><thead><tr><th>人员</th><th>任务数</th><th>排期情况</th><th>涉及项目／任务</th></tr></thead><tbody><tr v-for="person in resources.filter(person => person.tasks.some(task => filteredIds.has(task.item.project.id)))" :key="person.person"><td>{{ nameOf(person.person) }}</td><td>{{ person.tasks.length }}</td><td>{{ person.overlapCount ? '排期重叠待确认 · ' + person.overlapCount + ' 项' : '暂未发现重叠' }}</td><td><button v-for="entry in person.tasks" :key="entry.item.project.id + entry.task.id" class="pdm-text-action wb-resource-task" @click="emit('open', entry.item.project.id, 'project-plan', entry.task.id)">{{ entry.item.project.code }} · {{ entry.task.name }}（{{ entry.task.plannedStart }} ～ {{ entry.task.plannedFinish }}）</button></td></tr></tbody></table><p v-if="!resources.length" class="wb-empty">当前范围内没有已分配、日期完整的生效任务</p>
        </div>
        <div v-else-if="display === 'gantt'" class="wb-scroll" aria-label="阶段甘特">
          <p class="wb-note">{{ today }} ～ {{ limit }} · 各阶段分别显示，仅使用生效计划</p>
          <div v-for="item in items.filter(item => filteredIds.has(item.project.id))" :key="item.project.id" class="wb-gantt-row"><button class="pdm-text-action" @click="emit('open', item.project.id, 'project-plan')">{{ item.project.code }}<small>{{ item.project.name }}</small></button><div><div v-for="(bar, index) in segments(item)" :key="bar.code" class="wb-gantt-track"><button :class="'wb-bar tone-' + index % 4" :style="{ marginLeft: bar.left + '%', width: bar.width + '%' }" :title="bar.name + '：' + bar.start + ' ～ ' + bar.finish" @click="emit('open', item.project.id, 'project-plan')">{{ bar.name }}</button></div><span v-if="!segments(item).length" class="wb-note">{{ isEffective(item) ? '周期内无阶段安排' : '待补充生效计划' }}</span></div></div>
        </div>
        <div v-else-if="display === 'risks'" class="wb-scroll" aria-label="人工风险">
          <label class="wb-note wb-check"><input v-model="showClosed" type="checkbox">显示已关闭</label>
          <table><thead><tr><th>项目</th><th>风险与影响</th><th>负责人</th><th>截止日期</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="risk in risks.filter(risk => filteredIds.has(risk.projectId) && (showClosed || !risk.closed))" :key="risk.id"><td>{{ projectCode(risk.projectId) }}</td><td>{{ risk.title }}<small>{{ risk.impact }}</small></td><td>{{ nameOf(risk.owner) }}</td><td>{{ risk.dueDate }}</td><td>{{ risk.closed ? '已关闭' : risk.severity === 'danger' ? '高风险' : '跟进中' }}<small v-if="risk.closed">{{ nameOf(risk.closedBy) }} · {{ risk.closedAt?.slice(0, 10) }}</small></td><td><button v-if="risk.canClose && !risk.closed" class="pdm-text-action" :disabled="saving" @click="closeRisk(risk)">关闭风险</button></td></tr></tbody></table><p v-if="!risks.some(risk => filteredIds.has(risk.projectId) && (showClosed || !risk.closed))" class="wb-empty">当前范围内没有登记的风险</p>
        </div>
      </article>
      <p v-if="!data.views.length" class="wb-empty">当前账号暂无对应岗位视角，请确认角色及项目人员分配。</p>
    </template>
    <el-dialog v-model="riskDialog" title="登记项目风险" width="560px" append-to-body>
      <form class="wb-risk-form" @submit.prevent="saveRisk">
        <label>项目<select v-model="draft.projectId" required><option v-for="item in registerable" :key="item.project.id" :value="item.project.id">{{ item.project.code }} · {{ item.project.name }}</option></select></label>
        <label>风险<input v-model="draft.title" required maxlength="150"></label>
        <label>影响<textarea v-model="draft.impact" required maxlength="1000" rows="3" /></label>
        <label>负责人<select v-model="draft.owner" required><option value="" disabled>请选择</option><option v-for="person in owners" :key="person.username" :value="person.username">{{ person.displayName }}</option></select></label>
        <label>截止日期<input v-model="draft.dueDate" type="date" required></label>
        <label>严重程度<select v-model="draft.severity"><option value="warning">一般风险</option><option value="danger">高风险／影响交付</option></select></label>
        <footer><button type="button" class="pdm-secondary-action" @click="riskDialog = false">取消</button><button type="submit" class="pdm-primary-action" :disabled="saving">{{ saving ? '保存中…' : '保存' }}</button></footer>
      </form>
    </el-dialog>
  </section>
</template>

<style scoped>
.pdm-project-workbench{height:100%;min-height:0;overflow:auto;display:flex;flex-direction:column;gap:var(--pdm-container-gap);color:var(--pdm-text);padding-bottom:8px}.pdm-project-workbench>*{flex-shrink:0}.wb-header{padding:12px 14px;display:flex;gap:16px;align-items:center;justify-content:space-between}.wb-header h1{margin:0;font-size:var(--pdm-font-size-heading);font-weight:600}.wb-header p{margin:5px 0 0;font-size:12px;color:var(--pdm-muted)}.wb-filters{display:flex;align-items:center;gap:8px;flex-wrap:wrap}.wb-filters label{display:flex;align-items:center;gap:6px;font-size:12px;white-space:nowrap}.wb-filters select,.wb-filters input,.wb-risk-form input,.wb-risk-form select,.wb-risk-form textarea{border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--pdm-text);padding:6px;font:inherit}.wb-filters input{width:160px}.wb-search{position:relative}.wb-search svg{position:absolute;left:6px;color:var(--pdm-muted)}.wb-search input{padding-left:25px}.wb-note{font-size:11px;color:var(--pdm-muted);margin:2px 10px;line-height:1.6}.wb-error{padding:12px;background:#fff1f0;color:var(--pdm-danger)}.wb-metrics{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:8px}.wb-metrics button{background:var(--pdm-surface);border:1px solid var(--pdm-border);border-radius:7px;text-align:left;padding:11px 14px;color:var(--pdm-text);cursor:pointer}.wb-metrics button.selected{border-color:var(--pdm-blue);box-shadow:inset 0 2px var(--pdm-blue)}.wb-metrics span{display:block;font-size:12px;color:var(--pdm-muted)}.wb-metrics strong{display:inline-block;font-size:24px;margin:5px 7px 0 0;font-weight:600}.wb-metrics small{font-size:11px;color:var(--pdm-muted)}.wb-toolbar,.wb-details>header{display:flex;align-items:center;justify-content:space-between}.wb-tabs{display:flex;gap:4px;flex-wrap:wrap}.wb-tabs button{padding:6px 12px;border:0;border-radius:4px;background:transparent;color:var(--pdm-muted);font-size:12px;cursor:pointer}.wb-tabs button[aria-pressed=true]{background:var(--pdm-theme-accent-soft,#edf4ff);color:var(--pdm-blue);font-weight:600}.wb-queues{display:grid;grid-template-columns:1fr 1fr;gap:8px}.wb-queue{min-width:0;overflow:hidden}.wb-queue header,.wb-details>header{padding:8px 12px;border-bottom:1px solid var(--pdm-border);display:flex;justify-content:space-between;align-items:center}.wb-queue h2{margin:0;font-size:var(--pdm-font-size-heading);font-weight:600}.wb-queue header>span{font-size:11px;color:var(--pdm-muted)}.wb-actions{max-height:248px;overflow:auto}.wb-action{width:100%;display:flex;align-items:center;gap:8px;padding:9px 12px;border:0;border-bottom:1px solid var(--pdm-border-soft);text-align:left;background:var(--pdm-surface);color:var(--pdm-text);cursor:pointer}.wb-action:hover{background:var(--pdm-theme-accent-soft)}.wb-action>i{width:5px;height:28px;flex-shrink:0;border-radius:3px;background:#94a3b8}.wb-action>i.priority-0{background:#dc4444}.wb-action>i.priority-1{background:#df901e}.wb-action>i.priority-3{background:#408ccb}.wb-action>span{flex:1;min-width:0}.wb-action strong{font-size:12px;font-weight:600}.wb-action small,.wb-details td small{display:block;font-size:11px;color:var(--pdm-muted);line-height:1.6}.wb-action em{display:flex;align-items:center;gap:4px;white-space:nowrap;font-size:11px;font-style:normal;color:var(--pdm-muted)}.wb-empty{padding:24px 12px;text-align:center;color:var(--pdm-muted);font-size:12px}.wb-details{overflow:hidden}.wb-scroll{overflow:auto;max-height:560px}.wb-scroll table{width:100%;border-collapse:collapse;font-size:12px;min-width:760px}.wb-scroll th{background:var(--pdm-table-header);position:sticky;top:0;z-index:1;font-weight:500;text-align:left;white-space:nowrap}.wb-scroll th,.wb-scroll td{padding:9px;border-bottom:1px solid var(--pdm-border-soft);vertical-align:middle}.wb-scroll tr:hover td{background:var(--pdm-theme-accent-soft)}.wb-scroll .pdm-text-action{text-align:left;white-space:normal;line-height:1.5}.wb-project{display:flex;align-items:center;gap:4px;min-width:140px}.wb-project>button:first-child:not(.pdm-text-action){padding:2px;border:0;background:transparent;color:var(--pdm-muted)}.wb-project small{max-width:210px;overflow-wrap:anywhere}.wb-child .wb-project{padding-left:22px}.wb-child{background:#fafcfe}.wb-scroll progress{width:65px;height:8px;accent-color:#1ba39b}.wb-badge{display:inline-block;padding:3px 6px;border-radius:4px;white-space:nowrap;font-size:11px;background:var(--pdm-surface-muted);color:var(--pdm-muted)}.wb-badge.danger{background:var(--pdm-danger-soft);color:var(--pdm-danger)}.wb-badge.warning{background:var(--pdm-orange-soft);color:var(--pdm-orange)}.wb-badge.normal{background:#ebfaf2;color:var(--pdm-green)}.wb-budget{min-width:240px;font-size:11px;display:grid;gap:3px}.wb-resource-task{display:block;margin:4px 0}.wb-gantt-row{display:grid;grid-template-columns:170px 1fr;min-width:740px;padding:10px;border-bottom:1px solid var(--pdm-border-soft);gap:10px}.wb-gantt-row>button{text-align:left}.wb-gantt-row small{display:block;color:var(--pdm-muted)}.wb-gantt-track{height:23px;background:repeating-linear-gradient(90deg,var(--pdm-table-header),var(--pdm-table-header) calc(25% - 1px),#e5ebf1 25%);margin:3px 0}.wb-bar{height:21px;border:0;border-radius:3px;background:var(--pdm-theme-accent-soft-strong);color:#24548f;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;cursor:pointer;font-size:11px}.wb-bar.tone-1{background:#ede9fe;color:#6b439d}.wb-bar.tone-2{background:var(--pdm-theme-accent-soft-strong);color:#12645d}.wb-bar.tone-3{background:#fef3c7;color:#946517}.wb-check{display:flex;align-items:center;gap:6px;padding:7px}.wb-risk-form{display:grid;gap:12px}.wb-risk-form label{display:grid;grid-template-columns:75px 1fr;align-items:center;font-size:13px;gap:8px}.wb-risk-form footer{display:flex;justify-content:flex-end;gap:8px;margin-top:12px}
@media(max-width:1200px){.wb-header{align-items:flex-start;flex-direction:column}.wb-filters{width:100%}.wb-action em{font-size:10px}.wb-metrics{grid-template-columns:repeat(3,minmax(0,1fr))}}@media(max-width:800px){.wb-queues{grid-template-columns:1fr}.wb-action{padding:8px}.wb-filters label{flex-wrap:wrap}.wb-metrics{gap:5px}.wb-metrics button{padding:8px}.wb-action em{display:block}.wb-action em svg{display:none}}
</style>
