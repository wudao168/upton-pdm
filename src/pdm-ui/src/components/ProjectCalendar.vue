<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ChevronLeft, ChevronRight, RotateCw as RefreshCw, Maximize2, Minimize2 } from '@lucide/vue'
import { readProjectCalendar, readCalendarTodos, createUserTodo } from '../api'
import { ElDialog } from 'element-plus'
import ProjectTodoFields from './ProjectTodoFields.vue'
import type { PdmUser, UserNotification } from '../types'
import type { WorkbenchData, WorkbenchTab } from '../projectWorkbench'
import { addDays, isMine } from '../projectWorkbench'
import { calendarEvents, chinaToday, monthDays, shiftMonth } from '../projectCalendar'
import type { CalendarEvent } from '../projectCalendar'
import { festivalForDate, loadGlobalStatusContent } from '../globalStatusContent'

const props = defineProps<{ token: string; currentUsername: string; companyId: string; users: PdmUser[] }>()
const emit = defineEmits<{ open: [projectId: string, tab?: WorkbenchTab, taskId?: string]; todosChanged: []; openNotification: [notification: UserNotification] }>()
const todos = ref<UserNotification[]>([])
const todoOpen = ref(false), todoSaving = ref(false), todoError = ref('')
const todoContent = ref(''), todoDueDate = ref<string>(), todoRecipients = ref<string[]>([])
let todoGeneration = 0
const todoUsers = computed(() => {
  const users = props.users.filter(user => user.isActive && (!user.companyId || user.companyId === props.companyId || user.accessibleCompanyIds?.includes(props.companyId)))
  return users.some(user => user.username === props.currentUsername) ? users : [{ username: props.currentUsername, displayName: props.currentUsername, role: '', isActive: true }, ...users]
})
const maximized = ref(false)
const today = ref(chinaToday())
const month = ref(today.value.slice(0, 7) + '-01')
const selected = ref(today.value)
async function loadTodos() {
  const generation = ++todoGeneration
  try {
    const response = await readCalendarTodos(monthDays(month.value)[0]!, monthDays(shiftMonth(month.value, 1)).at(-1)!, props.token)
    if (generation === todoGeneration) { todos.value = response; todoError.value = '' }
  } catch { if (generation === todoGeneration) todoError.value = '待办读取失败，请刷新日历后重试。' }
}
watch([month, () => props.token, () => props.companyId, () => props.currentUsername], () => { todos.value = []; todoOpen.value = false; void loadTodos() }, { immediate: true })
function newTodo() { todoContent.value = ''; todoDueDate.value = selected.value; todoRecipients.value = [props.currentUsername]; todoError.value = ''; todoOpen.value = true }
async function saveTodo() {
  if (todoSaving.value) return
  if (!todoContent.value.trim()) { todoError.value = '请输入待办内容。'; return }
  if (!todoRecipients.value.length) { todoError.value = '请选择至少一名待办接收人。'; return }
  todoSaving.value = true
  try {
    await createUserTodo({ content: todoContent.value.trim(), dueDate: todoDueDate.value || undefined, recipientUsernames: todoRecipients.value }, props.token)
    todoOpen.value = false
    await loadTodos()
    emit('todosChanged')
  } catch { todoError.value = '待办保存失败，请检查内容、接收人和网络后重试。' }
  finally { todoSaving.value = false }
}
function refresh() { void load(); void loadTodos() }
const data = ref<WorkbenchData>()
const loading = ref(false)
const error = ref('')
const query = ref(''), unit = ref(''), manager = ref(''), mine = ref(false), kind = ref('')
const display = ref<'double' | 'month' | 'week' | 'list'>('double')
let generation = 0
let request: AbortController | undefined
async function load() {
  const current = ++generation
  request?.abort()
  const controller = new AbortController()
  request = controller
  loading.value = true; error.value = ''
  try {
    const response = await readProjectCalendar(props.token, controller.signal)
    if (current === generation) { data.value = response; today.value = chinaToday() }
  } catch (reason) {
    if (current !== generation || controller.signal.aborted) return
    console.error('项目日历读取失败', reason)
    data.value = undefined
    error.value = '项目日历读取失败，请检查网络后重试；如仍失败，请联系管理员。'
  } finally { if (current === generation) loading.value = false }
}
watch([() => props.token, () => props.companyId, () => props.currentUsername], () => {
  data.value = undefined; query.value = ''; unit.value = ''; manager.value = ''; mine.value = false
  void load()
}, { immediate: true })
watch(today, (value, previous) => { if (value !== previous) void load() })
const timer = setInterval(() => { today.value = chinaToday() }, 60000)
const exitBoard = (event: KeyboardEvent) => { if (event.key === 'Escape') maximized.value = false }
window.addEventListener('keydown', exitBoard)
onBeforeUnmount(() => { generation++; todoGeneration++; request?.abort(); clearInterval(timer); window.removeEventListener('keydown', exitBoard) })
const allItems = computed(() => data.value?.projects ?? [])
const units = computed(() => [...new Set(allItems.value.map(item => item.project.executionUnitName).filter(Boolean))] as string[])
const managers = computed(() => [...new Set(allItems.value.map(item => item.project.primaryProjectManager).filter(Boolean))] as string[])
const items = computed(() => allItems.value.filter(({ project: p }) =>
  (!unit.value || p.executionUnitName === unit.value) && (!manager.value || p.primaryProjectManager === manager.value)
  && (!mine.value || isMine(p.primaryProjectManager, props.currentUsername) || p.collaborativeProjectManagers?.some(person => isMine(person, props.currentUsername)))
  && [p.code, p.name, p.customerName].join(' ').toLowerCase().includes(query.value.trim().toLowerCase())))
const allEvents = computed<CalendarEvent[]>(() => [...calendarEvents(items.value, data.value?.risks ?? [], today.value), ...todos.value
  .filter(todo => [todo.title, todo.content].join(' ').toLowerCase().includes(query.value.trim().toLowerCase()))
  .map(todo => ({ id: `todo-${todo.id}`, todoId: todo.id, projectId: todo.projectId || '', code: '待办', projectName: todo.title,
    title: todo.content, date: todo.dueDate!, owner: todo.recipient, kind: 'todo' as const, tab: 'overview' as const,
    tone: todo.dueDate! < today.value ? 'danger' as const : 'normal' as const,
    detail: '仅创建者和指定接收人可见；与“我的待办”共用。' }))].sort((a, b) => a.date.localeCompare(b.date)))
const events = computed(() => allEvents.value.filter(event => !kind.value || event.kind === kind.value))
const months = computed(() => display.value === 'double' ? [month.value, shiftMonth(month.value, 1)] : [month.value])
const end = computed(() => shiftMonth(month.value, display.value === 'month' ? 1 : 2))
const inRange = computed(() => events.value.filter(event => event.date >= month.value && event.date < end.value))
const byDay = computed(() => {
  const map = new Map<string, CalendarEvent[]>()
  for (const event of events.value) map.set(event.date, [...(map.get(event.date) ?? []), event])
  return map
})
const dayEvents = (day: string) => byDay.value.get(day) ?? []
const details = computed(() => dayEvents(selected.value))
const weekDays = computed(() => {
  const offset = (new Date(selected.value + 'T00:00:00Z').getUTCDay() + 6) % 7
  return Array.from({ length: 7 }, (_, index) => addDays(selected.value, index - offset))
})
const label = (value: string) => `${value.slice(0, 4)}年${Number(value.slice(5, 7))}月`
const name = (username?: string) => props.users.find(user => user.username === username)?.displayName || username || '待安排'
const toneLabel = { normal: '待完成', warning: '需关注', danger: '逾期／风险', done: '已完成' }
const kindLabel = { milestone: '里程碑', stage: '阶段节点', shipping: '计划发货', risk: '风险期限', todo: '待办事项' }
const holidayDays = ref(new Map<string, { name: string; isOffDay: boolean }>())
void loadGlobalStatusContent().then(library => {
  holidayDays.value = new Map(library.holidayCalendars.flatMap(calendar => calendar.days.map(day => [day.date, day] as const)))
}).catch(reason => { console.error('项目日历节假日数据读取失败', reason) })
const holidayLabels = computed(() => new Map([...new Set([...months.value.flatMap(monthDays), ...weekDays.value])].map(day => {
  const schedule = holidayDays.value.get(day)
  const name = schedule?.name || festivalForDate(new Date(day + 'T12:00:00'))
  return [day, name ? {
    label: schedule?.isOffDay === false ? '补班' : name,
    title: schedule ? `${name}（${schedule.isOffDay ? '放假' : '调休上班'}）` : name,
    tone: schedule?.isOffDay === false ? 'workday' : 'holiday',
  } : undefined] as const
})))
function move(offset: number) {
  if (display.value === 'week') { selected.value = addDays(selected.value, offset * 7); month.value = selected.value.slice(0, 7) + '-01' }
  else { month.value = shiftMonth(month.value, offset); selected.value = month.value }
}
function reset() { today.value = chinaToday(); month.value = today.value.slice(0, 7) + '-01'; selected.value = today.value }
function select(day: string) { selected.value = day }
function open(event: CalendarEvent) { if (event.kind === 'todo') { const todo = todos.value.find(item => item.id === event.todoId); if (todo) emit('openNotification', todo); return }; emit('open', event.projectId, event.tab, event.taskId) }
</script>

<template>
  <section class="project-calendar" :class="{ 'is-maximized': maximized }" aria-label="项目日历" :aria-busy="loading">
    <p v-if="todoError && !todoOpen" class="pc-error" role="alert">{{ todoError }}</p>
    <p v-if="error" class="pc-error" role="alert">{{ error }} <button @click="load">重试</button></p>
    <div class="pc-toolbar"><div class="pc-controls"><button aria-label="上一期" @click="move(-1)"><ChevronLeft :size="17" /></button><strong>{{ display === 'week' ? `${weekDays[0]} — ${weekDays[6]}` : `${label(month)}${display === 'month' ? '' : ' — ' + label(shiftMonth(month, 1))}` }}</strong><button aria-label="下一期" @click="move(1)"><ChevronRight :size="17" /></button><button @click="reset">回到本月</button><button :disabled="loading" aria-label="刷新日历" @click="refresh"><RefreshCw :size="14" /></button><button :aria-pressed="maximized" @click="maximized = !maximized"><component :is="maximized ? Minimize2 : Maximize2" :size="14" />{{ maximized ? '退出看板' : '最大化看板' }}</button><div class="pdm-tabs" role="group" aria-label="日历视图"><button v-for="(text, key) in { double: '双月', month: '月', week: '周', list: '清单' }" :key="key" :aria-pressed="display === key" @click="display = key">{{ text }}</button></div></div><div class="pc-filters">
      <select v-model="unit" aria-label="事业部"><option value="">全部事业部</option><option v-for="entry in units" :key="entry">{{ entry }}</option></select>
      <select v-model="manager" aria-label="项目经理"><option value="">全部项目经理</option><option v-for="entry in managers" :key="entry" :value="entry">{{ name(entry) }}</option></select>
      <input v-model="query" aria-label="搜索项目" placeholder="搜索项目号、名称、客户">
      <select v-model="kind" aria-label="事项类型"><option value="">全部事项</option><option v-for="(text, key) in kindLabel" :key="key" :value="key">{{ text }}</option></select>
      <div class="pdm-tabs" role="group" aria-label="日历范围"><button :aria-pressed="!mine" @click="mine = false">权限范围内全部</button><button :aria-pressed="mine" @click="mine = true">我负责的</button></div>
    </div></div>
    <div class="pc-bottom">
      <section class="pdm-panel pc-details" aria-label="事项详情"><header class="pc-details-header"><h2>{{ selected + ' · 当日事项' }}</h2><button type="button" class="pdm-primary-action" @click="newTodo">新增待办</button></header>
        <div class="pc-table"><table><colgroup><col v-for="(weight, index) in [1, 2, 2, 1, 1, 1, 1, 1, 2, 1]" :key="index" :style="{ width: `${weight / 13 * 100}%` }"></colgroup><thead><tr><th>项目号</th><th>项目名称</th><th>事项</th><th>计划日期</th><th>基线日期</th><th>实际日期</th><th>负责人</th><th>状态</th><th>说明</th><th>操作</th></tr></thead><tbody><tr v-for="event in details" :key="event.id"><td>{{ event.code }}</td><td :title="event.projectName">{{ event.projectName }}</td><td :title="event.title">{{ event.title }}</td><td>{{ event.date }}</td><td>{{ event.baseline || '—' }}</td><td>{{ event.actual || '—' }}</td><td>{{ name(event.owner) }}</td><td :class="event.tone">{{ toneLabel[event.tone] }}</td><td :title="event.detail">{{ event.detail }}</td><td><button v-if="event.kind !== 'todo'" @click="open(event)">查看{{ event.kind === 'risk' ? '项目' : '计划' }}</button><button v-else @click="open(event)">查看待办</button></td></tr></tbody></table><p v-if="!details.length" class="pc-empty">{{ loading ? '正在读取事项…' : '暂无符合条件的重要事项' }}</p></div>
      </section>

    </div>
    <div v-if="display === 'double' || display === 'month'" class="pc-months" :class="{ single: display === 'month' }">
      <article v-for="entry in months" :key="entry" class="pc-month pdm-panel">
        <h2>{{ label(entry) }}<small v-if="entry.startsWith(today.slice(0, 7))">本月</small><small v-else-if="entry === shiftMonth(today, 1)">下月</small></h2>
        <div class="pc-weekdays"><span v-for="day in ['一','二','三','四','五','六','日']" :key="day">{{ day }}</span></div>
        <div class="pc-grid"><button v-for="day in monthDays(entry)" :key="day" class="pc-day" :class="{ outside: !day.startsWith(entry.slice(0, 7)), selected: day === selected }" :aria-label="`${day}，${holidayLabels.get(day) ? holidayLabels.get(day)!.title + '，' : ''}${day.startsWith(entry.slice(0, 7)) ? dayEvents(day).length : 0}项事项`" :aria-pressed="day === selected" @click="select(day)">
          <span class="pc-day-heading"><span class="pc-date" :class="{ today: day === today }">{{ Number(day.slice(8)) }}</span><span v-if="holidayLabels.get(day)" class="pc-holiday" :class="`is-${holidayLabels.get(day)!.tone}`" :title="holidayLabels.get(day)!.title">{{ holidayLabels.get(day)!.label }}</span></span>
          <template v-if="day.startsWith(entry.slice(0, 7))"><span v-for="event in dayEvents(day).slice(0, 2)" :key="event.id" class="pc-event" :class="event.tone" :title="`${event.title} · ${event.code} · ${event.detail}`"><b>{{ event.code }} · {{ event.title }}</b></span><small v-if="dayEvents(day).length > 2" class="pc-more">另有{{ dayEvents(day).length - 2 }}项</small></template>
        </button></div>
      </article>
    </div>
    <div v-else-if="display === 'week'" class="pc-week-view"><article v-for="day in weekDays" :key="day" class="pdm-panel"><button @click="select(day)">{{ day }}</button><span v-if="holidayLabels.get(day)" class="pc-holiday" :class="`is-${holidayLabels.get(day)!.tone}`" :title="holidayLabels.get(day)!.title">{{ holidayLabels.get(day)!.label }}</span><button v-for="event in dayEvents(day)" :key="event.id" class="pc-event" :class="event.tone" :title="`${event.code} · ${event.title} · ${event.detail}`" @click="open(event)"><b>{{ event.code }} · {{ event.title }}</b></button><p v-if="!dayEvents(day).length">暂无事项</p></article></div>
    <div v-else class="pc-list pdm-panel"><button v-for="event in inRange" :key="event.id" @click="open(event)"><span>{{ event.date }}</span><strong>{{ event.code }} · {{ event.title }}</strong><span>{{ name(event.owner) }}</span><span :class="event.tone">{{ toneLabel[event.tone] }}</span></button><p v-if="!inRange.length" class="pc-empty">所选月份暂无符合条件的重要事项</p></div>
    <div class="pc-legend"><span v-for="(text, tone) in toneLabel" :key="tone" :class="tone">● {{ text }}</span></div>

  </section>
  <el-dialog v-model="todoOpen" title="新增待办" width="520px" append-to-body :close-on-click-modal="!todoSaving">
    <label class="pdm-dialog-field">待办内容<textarea v-model="todoContent" class="pdm-manager-note-dialog__input" rows="5" maxlength="1000" placeholder="记录需要跟进的事项" aria-label="待办内容" :disabled="todoSaving" /></label>
    <ProjectTodoFields v-model:due-date="todoDueDate" v-model:recipients="todoRecipients" :users="todoUsers" :pending="todoSaving" description="仅创建者和指定接收人可见；不设截止日期时仅在“我的待办”显示。" />
    <p v-if="todoError" role="alert" class="pc-todo-error">{{ todoError }}</p>
    <template #footer><button type="button" class="pdm-secondary-action" :disabled="todoSaving" @click="todoOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="todoSaving" @click="saveTodo">{{ todoSaving ? '保存中…' : '保存待办' }}</button></template>
  </el-dialog>
</template>

<style scoped>
.project-calendar{height:100%;overflow:auto;min-height:0;display:flex;flex-direction:column;gap:12px;color:var(--pdm-text);padding:4px 4px 16px;container-type:inline-size}.project-calendar>*{flex-shrink:0}.project-calendar button,.project-calendar input,.project-calendar select{font:inherit;color:inherit}.project-calendar button{cursor:pointer}.pc-heading,.pc-heading h1,.pc-toolbar,.pc-toolbar>div,.pc-filters{display:flex;align-items:center;gap:8px}.pc-heading{justify-content:space-between}.pc-heading h1{font-size:22px;margin:0}.pc-heading p,.pc-note{font-size:12px;color:var(--pdm-muted);margin:4px 0}.pc-heading>button,.pc-toolbar button,.pc-filters button,.pc-filters select,.pc-filters input,.pc-table button{border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface,#fff);padding:7px 10px}.pc-heading>button{display:flex;align-items:center;gap:5px}.pc-filters{flex-wrap:wrap}.pc-filters input{flex:1;min-width:160px}.pc-filters button[aria-pressed=true],.pc-toolbar button[aria-pressed=true]{background:var(--pdm-blue,var(--pdm-theme-accent));color:white}.pc-toolbar{justify-content:space-between;flex-wrap:wrap}.pc-toolbar strong{font-size:15px}.pc-toolbar>div{flex-wrap:wrap}.pc-months{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}.pc-months.single{grid-template-columns:1fr}.pc-month{overflow:hidden;min-width:0}.pc-month h2,.pc-details h2,.pc-attention h2{font-size:16px;margin:0;padding:12px 14px;border-bottom:1px solid var(--pdm-border)}.pc-month h2 small{font-size:11px;padding:3px 9px;border-radius:12px;background:var(--pdm-theme-accent-soft,var(--pdm-theme-accent-soft));color:var(--pdm-blue);margin-left:10px}.pc-weekdays,.pc-grid{display:grid;grid-template-columns:repeat(7,minmax(0,1fr))}.pc-weekdays span{padding:8px;text-align:center;font-size:12px;border-bottom:1px solid var(--pdm-border)}.pc-grid .pc-day{min-width:0;min-height:104px;padding:6px 4px;border:0;border-right:1px solid var(--pdm-border-soft,var(--pdm-border-soft));border-bottom:1px solid var(--pdm-border-soft,var(--pdm-border-soft));background:var(--pdm-surface,#fff);text-align:left;display:flex;flex-direction:column;gap:3px;overflow:hidden}.pc-grid .pc-day.outside{background:var(--pdm-surface-muted,var(--pdm-table-header));color:var(--pdm-muted)}.pc-grid .pc-day.selected{background:var(--pdm-theme-accent-soft,var(--pdm-theme-accent-soft));box-shadow:inset 0 0 0 1px var(--pdm-blue)}.pc-date{font-size:12px;display:grid;place-items:center;width:23px;height:23px}.pc-date.today{background:var(--pdm-blue,var(--pdm-theme-accent));color:white;border-radius:50%}.pc-event{display:block;border:0;border-left:3px solid currentColor;border-radius:3px;padding:3px 4px;text-align:left;max-width:100%;background:transparent;font-size:11px;line-height:1.4}.pc-event b,.pc-event small{display:block;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;font-weight:400}.normal{color:var(--pdm-theme-accent)}.warning{color:var(--pdm-orange)}.danger{color:var(--pdm-danger)}.done{color:var(--pdm-green)}.pc-more{font-size:10px;color:var(--pdm-blue)}.pc-legend{display:flex;gap:18px;font-size:12px}.pc-bottom{display:grid;grid-template-columns:minmax(0,1fr) 250px;gap:14px}.pc-table{overflow:auto;max-height:400px}.pc-table table{border-collapse:collapse;width:100%;font-size:12px;text-align:left}.pc-table td,.pc-table th{padding:10px;border-bottom:1px solid var(--pdm-border-soft);vertical-align:top}.pc-table small{display:block;font-size:11px;line-height:1.6;max-width:320px}.pc-table th{font-weight:500;color:var(--pdm-muted)}.pc-attention button{border:0;border-bottom:1px solid var(--pdm-border-soft);background:transparent;width:100%;padding:12px;text-align:left;display:flex;justify-content:space-between;font-size:12px}.pc-attention>small{display:block;font-size:11px;color:var(--pdm-muted);padding:12px}.pc-empty{text-align:center;padding:20px;color:var(--pdm-muted);font-size:12px}.pc-missing{display:block;border:0;background:transparent;padding:12px;text-align:left}.pc-list>button{display:flex;gap:20px;align-items:center;border:0;border-bottom:1px solid var(--pdm-border);background:transparent;width:100%;padding:12px;text-align:left}.pc-list strong{flex:1}.pc-week-view{display:grid;grid-template-columns:repeat(7,minmax(0,1fr));gap:6px;overflow:auto}.pc-week-view article{padding:8px;min-width:110px;min-height:200px}.pc-week-view button{display:block;margin-bottom:8px;border:0}.pc-week-view p{font-size:12px;color:var(--pdm-muted)}.pc-error{padding:12px;background:var(--pdm-danger-soft);color:var(--pdm-danger)}.project-calendar button:focus-visible{outline:2px solid var(--pdm-blue);outline-offset:-2px}@container (max-width:1000px){.pc-months{grid-template-columns:1fr}.pc-bottom{grid-template-columns:1fr}.pc-attention{order:-1}.pc-week-view{grid-template-columns:repeat(7,140px)}}@container (max-width:550px){.pc-filters>*{max-width:100%}.pc-toolbar strong{font-size:12px}.pc-legend{gap:8px;font-size:10px}.pc-grid .pc-day{padding:3px 1px}.pc-event{padding:2px;font-size:10px}.pc-list>button{flex-wrap:wrap;font-size:12px}}

/* A bounded grid keeps both calendars and the detail strip in one viewport. */
.project-calendar{box-sizing:border-box;overflow:hidden;display:grid;grid-template-rows:auto minmax(90px,18%) minmax(180px,1fr) auto;gap:6px;padding:4px 4px 6px;position:relative}
.project-calendar.is-maximized{position:fixed;inset:0;z-index:2000;height:100dvh;width:100vw;padding:12px;background:var(--pdm-background,var(--pdm-surface-muted))}
.pc-heading-actions{display:flex;gap:8px}.pc-heading-actions button{display:flex;align-items:center;gap:5px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface,#fff);padding:6px 9px;font-size:12px}
.pc-heading h1{font-size:19px}.pc-heading p{display:none}.pc-note{margin:0;font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.pc-filters{gap:5px}.pc-filters input,.pc-filters select,.pc-filters button{padding:5px 7px;font-size:12px}.pc-toolbar button{padding:4px 7px;display:inline-flex;align-items:center;gap:4px}.pc-toolbar strong{font-size:13px}
.pc-months,.pc-months.single{min-height:0;overflow:hidden;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.pc-months.single{grid-template-columns:1fr}.pc-month{display:grid;grid-template-rows:auto auto minmax(0,1fr)}.pc-month h2{padding:7px 10px;font-size:14px}.pc-weekdays span{padding:4px}.pc-grid{min-height:0;grid-template-rows:repeat(6,minmax(0,1fr))}.pc-grid .pc-day{min-height:0;padding:2px 3px;gap:1px;position:relative}.pc-date{width:20px;height:20px;min-height:18px;font-size:11px}.pc-event{padding:1px 3px;font-size:10px;min-height:14px;flex-shrink:0}.pc-event b{font-size:10px}.pc-event small{display:none}.pc-more{position:absolute;right:2px;top:3px;font-size:10px;background:var(--pdm-surface,#fff)}.pc-legend{font-size:11px;line-height:16px}
.pc-bottom{min-height:0;grid-template-columns:minmax(0,1fr) 220px;gap:8px}.pc-details{min-height:0;display:flex;flex-direction:column;overflow:auto}.pc-details h2,.pc-attention h2{padding:6px 10px;font-size:13px;flex-shrink:0}.pc-table{min-height:0;overflow:auto;max-height:none}.pc-table th,.pc-table td{padding:5px 8px}.pc-table th{position:sticky;top:0;background:var(--pdm-surface,#fff)}.pc-table button{padding:3px 6px;white-space:nowrap}.pc-empty{padding:8px;margin:0}.pc-attention{min-height:0;overflow:auto;order:initial}.pc-attention button{padding:5px 10px;font-size:11px}.pc-attention>small{padding:5px 10px;font-size:10px}.pc-list,.pc-week-view{min-height:0;overflow:auto}.pc-week-view{grid-template-columns:repeat(7,minmax(110px,1fr))}.pc-week-view article{min-height:0}.pc-error{position:absolute;top:40px;left:8px;right:8px;z-index:3;margin:0}
@container (max-width:1000px){.pc-months{grid-template-columns:repeat(2,minmax(0,1fr))}.pc-bottom{grid-template-columns:minmax(0,1fr) 175px}.pc-attention{order:initial}.pc-filters input{min-width:110px;width:110px}.pc-filters select{max-width:110px}.pc-grid .pc-day{padding:1px}.pc-event{padding:0 2px;border-left-width:2px}.pc-month h2{font-size:12px}.pc-heading h1{font-size:17px}}
@container (max-width:550px){.pc-filters{flex-wrap:nowrap;overflow:auto}.pc-filters>*{flex-shrink:0}.pc-bottom{grid-template-columns:minmax(0,1fr) 135px}.pc-heading-actions button{font-size:10px;padding:4px}.pc-month h2 small{display:none}.pc-table table{min-width:500px}.pc-toolbar{gap:3px}.pc-toolbar>div{gap:3px}.pc-months{gap:4px}.pc-event b{font-size:9px}.pc-legend{font-size:9px}}

.project-calendar,.pc-filters,.pc-toolbar,.pc-toolbar>div,.pc-months,.pc-months.single,.pc-bottom,.pc-week-view{gap:5px}

.pc-event,.pc-event.warning,.pc-event.danger,.pc-event.done{background:transparent;border-left:0}.pc-table table{min-width:1100px;table-layout:fixed}.pc-table th,.pc-table td{white-space:nowrap;vertical-align:middle;text-align:center;overflow:hidden;text-overflow:ellipsis}

.pc-bottom{grid-template-columns:minmax(0,1fr)}

.pc-toolbar{justify-content:space-between;align-items:center;flex-wrap:wrap}.pc-controls{flex-shrink:0;display:flex;align-items:center;gap:5px;flex-wrap:nowrap}.pc-toolbar .pc-filters{display:flex;flex:1;min-width:0;justify-content:flex-end;flex-wrap:nowrap;margin-left:10px}.pc-toolbar .pc-filters input{min-width:90px;width:140px;flex:0 1 180px}.pc-toolbar .pc-filters select{max-width:115px}.pc-toolbar .pc-filters button{white-space:nowrap}.pc-grid{position:relative;isolation:isolate}.pc-grid .pc-day{background:transparent;z-index:1}.pc-grid .pc-day.outside{background:var(--pdm-surface-muted,var(--pdm-table-header))}.pc-grid .pc-day.selected{background:var(--pdm-theme-accent-soft,var(--pdm-theme-accent-soft))}
@container (max-width:1250px){.pc-toolbar .pc-filters{flex-basis:100%;margin-left:0}.pc-controls{flex-wrap:wrap}.pc-toolbar .pc-filters input{flex:1}.pc-toolbar .pc-filters{overflow:auto}}
.pc-day-heading{display:flex;align-items:center;gap:3px;min-width:0;width:100%;flex-shrink:0}.pc-day-heading .pc-date{flex:0 0 20px}.pc-holiday{display:block;min-width:0;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;color:var(--pdm-orange);font-size:10px;font-weight:600;line-height:18px}.pc-holiday.is-workday{color:var(--pdm-blue)}.pc-grid .pc-more{position:static;align-self:flex-end;margin-top:auto}
.pc-controls > button { justify-content: center; text-align: center; }
@container (max-width:850px){.pc-day-heading{flex-wrap:wrap;gap:0}.pc-day-heading .pc-date{height:16px;min-height:16px}.pc-day-heading .pc-holiday{flex-basis:100%;font-size:9px;line-height:12px}}
</style>

<style scoped>
.pc-details-header{display:flex;align-items:center;justify-content:space-between;gap:8px;border-bottom:1px solid var(--pdm-border);padding-right:8px;flex-shrink:0}.pc-details-header h2{border:0}.pc-details-header>.pdm-primary-action{box-sizing:border-box;flex:0 0 60px;width:60px;min-width:60px;max-width:60px;height:28px;min-height:28px;max-height:28px;padding:0;font-size:12px;line-height:1;white-space:nowrap;color:#fff}.pc-todo-error{color:var(--pdm-danger)}
</style>
