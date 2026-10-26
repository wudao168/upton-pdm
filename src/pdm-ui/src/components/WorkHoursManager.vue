<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { ChevronLeft, ChevronRight, Clock, Download, Plus, RefreshCw } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'
import { rememberedTab } from '../pagePreference'
import WorkHoursSettings from './WorkHoursSettings.vue'
import WorkHoursCompliance from './WorkHoursCompliance.vue'
import { readWorkHours, readWorkHoursHistory, saveWorkHours } from '../api'
import { chinaToday, monthDays, shiftMonth } from '../projectCalendar'
import { addDays } from '../projectWorkbench'
import { loadGlobalStatusContent } from '../globalStatusContent'
import { downloadProcurementWorkbook } from '../procurementWorkbook'
import { dateRange, departmentPeople, hoursOn, reportRows, validateHours, weekDates, workingDay, workHoursDepartmentTree } from '../workHours'
import type { HolidayMap, WorkHourEntry, WorkHourInput, WorkHourRevision, WorkHoursProject, WorkHoursReportRow, WorkHoursView } from '../workHours'

const props = defineProps<{ token: string; companyId: string; active: boolean }>()
const failure = (cause: unknown, fallback: string) => cause instanceof Error ? userErrorMessage(cause.message) : fallback
const tab = rememberedTab('work-hours.tab', 'entry', ['entry','report','settings']), mode = rememberedTab('work-hours.mode', 'calendar', ['calendar','week'])
const own = ref<WorkHoursView>(), report = ref<WorkHoursView>()
const selected = ref(chinaToday()), month = ref(selected.value.slice(0, 7) + '-01')
const loading = ref(false), reporting = ref(false), saving = ref(false), error = ref('')
const holidays = ref<HolidayMap>(new Map()), calendarReady = ref(false), calendarError = ref('')
const today = computed(() => own.value?.today ?? chinaToday())
const entries = computed(() => own.value?.sheets.flatMap(x => x.entries) ?? [])
const days = computed(() => monthDays(month.value)), week = computed(() => weekDates(selected.value))
const monthEnd = computed(() => addDays(shiftMonth(month.value, 1), -1))
const monthEntries = computed(() => entries.value.filter(x => x.date >= month.value && x.date <= monthEnd.value))
const monthHours = computed(() => monthEntries.value.reduce((n, x) => n + x.hours, 0))
const ownChecks = computed(() => own.value?.compliance?.find(x => x.username.toLowerCase() === own.value?.username.toLowerCase())?.days ?? [])
const missed = computed(() => ownChecks.value.filter(x => x.date >= month.value && x.date <= monthEnd.value && x.status === '未填写').map(x => x.date))
const incomplete = computed(() => ownChecks.value.filter(x => x.date >= month.value && x.date <= monthEnd.value && x.status === '填写不完整').map(x => x.date))
const daily = computed(() => entries.value.filter(x => x.date === selected.value))
const editing = ref(''), form = reactive({ projectId: '', location: '厂内', hours: 4 }), formBaseline = ref('')
const formDirty = computed(() => JSON.stringify(form) !== formBaseline.value)
let rowSequence = 0, ownGeneration = 0, reportGeneration = 0
type WeekRow = { key: number; projectId: string; location: string; values: Record<string, number | string> }
const rows = ref<WeekRow[]>([]), weekBaseline = ref('')
const weekDirty = computed(() => JSON.stringify(rows.value) !== weekBaseline.value)
const settingsDirty = ref(false)
const dirty = computed(() => formDirty.value || weekDirty.value || settingsDirty.value)
const allProjects = computed(() => {
  const result = new Map<string, WorkHoursProject>((own.value?.projects ?? []).map(x => [x.id, x]))
  for (const entry of entries.value) if (!result.has(entry.projectId)) result.set(entry.projectId, { id: entry.projectId, code: entry.projectCode, name: entry.projectName,
    rootProjectId: entry.rootProjectId, rootProjectCode: entry.rootProjectCode, rootProjectName: entry.rootProjectName })
  return [...result.values()]
})
const recent = computed(() => [...new Set([...entries.value].sort((a, b) => b.date.localeCompare(a.date)).map(x => x.projectId))].slice(0, 5).map(id => allProjects.value.find(x => x.id === id)!).filter(Boolean))
const projectName = computed(() => allProjects.value.find(x => x.id === form.projectId)?.name ?? '选择项目后自动带出')
const number = (value: number) => value.toLocaleString('zh-CN', { maximumFractionDigits: 1 })
const total = (items: WorkHourInput[]) => items.reduce((n, x) => n + x.hours, 0)
function visibleDayStatus(day: string) {
  const check = ownChecks.value.find(x => x.date === day)
  if (check?.status === '已豁免') return '已豁免'
  if (check?.status === '未填写') return '未填'
  if (check?.status === '填写不完整') return '待核对'
  return day === today.value && !hoursOn(entries.value, day) ? '待填写' : ''
}
function resetForm(entry?: WorkHourEntry) {
  editing.value = entry?.id ?? ''
  Object.assign(form, { projectId: entry?.projectId ?? '', location: entry?.location ?? entries.value.at(-1)?.location ?? '厂内', hours: entry?.hours ?? 4 })
  formBaseline.value = JSON.stringify(form)
}
function makeRow(projectId = '', location = '厂内'): WeekRow { return { key: ++rowSequence, projectId, location, values: Object.fromEntries(week.value.map(day => [day, ''])) } }
function resetWeek() {
  const result: WeekRow[] = []
  for (const entry of entries.value.filter(x => week.value.includes(x.date))) {
    let row = result.find(x => x.projectId === entry.projectId && x.location === entry.location)
    if (!row) { row = makeRow(entry.projectId, entry.location); result.push(row) }
    row.values[entry.date] = entry.hours
  }
  rows.value = result.length ? result : [makeRow()]
  weekBaseline.value = JSON.stringify(rows.value)
}
function mayLeave() {
  if (dirty.value) { ElMessage.warning(settingsDirty.value ? '有未保存的工时设置，请先保存或取消更改。' : '有未保存的工时，请先保存或取消修改。'); return false }
  return !saving.value
}
async function loadOwn() {
  const generation = ++ownGeneration
  loading.value = true; error.value = ''
  try {
    const result = await readWorkHours(days.value[0]!, days.value.at(-1)!, false, props.token)
    if (generation !== ownGeneration) return
    own.value = result; resetForm(); resetWeek()
    if (tab.value === 'settings' && !(result.canManageSettings ?? result.canManage)) tab.value = 'entry'
    if (tab.value === 'report' && !report.value) await loadReport()
  } catch (cause) { if (generation === ownGeneration) error.value = failure(cause, '工时读取失败，请检查网络后刷新重试。') }
  finally { if (generation === ownGeneration) loading.value = false }
}
async function selectDay(day: string) {
  if (!mayLeave()) return
  selected.value = day
  if (month.value.slice(0, 7) !== day.slice(0, 7)) { month.value = day.slice(0, 7) + '-01'; await loadOwn() }
  else { resetForm(); resetWeek() }
}
async function move(offset: number) {
  if (!mayLeave()) return
  if (mode.value === 'week') await selectDay(addDays(selected.value, offset * 7))
  else { month.value = shiftMonth(month.value, offset); selected.value = month.value; await loadOwn() }
}
function changeMode(value: typeof mode.value) { if (value === mode.value || !mayLeave()) return; mode.value = value; resetWeek() }
async function changeTab(value: typeof tab.value) { if (value === tab.value || !mayLeave()) return; tab.value = value; if (value === 'report') await loadReport() }
async function persist(from: string, to: string, values: WorkHourInput[]) {
  const message = validateHours(values)
  if (message) { ElMessage.warning(message); return false }
  if (saving.value || !own.value) return false
  saving.value = true
  try {
    await saveWorkHours({ from, to, entries: values, versions: own.value.sheets.filter(x => x.month >= from.slice(0, 7) && x.month <= to.slice(0, 7)).map(x => ({ month: x.month, rowVersion: x.rowVersion })) }, props.token)
    resetForm(); weekBaseline.value = JSON.stringify(rows.value)
    await loadOwn()
    ElMessage.success('工时已保存并计入汇总。')
    return true
  } catch (cause) { ElMessage.error(failure(cause, '工时保存失败，填写内容已保留，请稍后重试。')); return false }
  finally { saving.value = false }
}
async function saveDay() {
  const values: WorkHourInput[] = daily.value.filter(x => x.id !== editing.value).map(x => ({ date: x.date, projectId: x.projectId, location: x.location, hours: x.hours }))
  values.push({ date: selected.value, ...form, hours: Number(form.hours) })
  await persist(selected.value, selected.value, values)
}
async function removeDay(entry: WorkHourEntry) {
  if (!mayLeave()) return
  try { await ElMessageBox.confirm(`确认删除 ${entry.projectCode} 的 ${number(entry.hours)} 小时工时？删除记录仍保留在修改历史中。`, '删除工时', { confirmButtonText: '确认删除', cancelButtonText: '取消', type: 'warning' }) }
  catch { return }
  await persist(selected.value, selected.value, daily.value.filter(x => x.id !== entry.id))
}
const weekInputs = computed<WorkHourInput[]>(() => rows.value.flatMap(row => week.value.filter(day => row.values[day] !== '' && Number(row.values[day]) !== 0).map(date => ({ date, projectId: row.projectId, location: row.location, hours: Number(row.values[date]) }))))
async function saveWeek() {
  if (week.value[0]! > today.value) return ElMessage.warning('实际工时不能填写未来日期。')
  await persist(week.value[0]!, week.value.at(-1)! > today.value ? today.value : week.value.at(-1)!, weekInputs.value)
}
function cancelChanges() { resetForm(); resetWeek() }
const copyOpen = ref(false), copyEntries = ref<WorkHourInput[]>([]), copyDate = ref('')
async function copyPreviousDay() {
  if (!mayLeave()) return
  const context = `${props.companyId}|${props.token}|${selected.value}`
  let previous = addDays(selected.value, -1)
  while (!workingDay(previous, holidays.value)) previous = addDays(previous, -1)
  try {
    const source = await readWorkHours(previous, previous, false, props.token)
    if (context !== `${props.companyId}|${props.token}|${selected.value}`) return
    copyEntries.value = source.sheets.flatMap(x => x.entries).filter(x => x.date === previous).map(x => ({ date: selected.value, projectId: x.projectId, location: x.location, hours: x.hours }))
    if (!copyEntries.value.length) return ElMessage.warning('上一工作日没有可复制的工时记录。')
    // 被复制的历史项目也保留可读的名称。
    for (const item of source.projects) if (!own.value?.projects.some(x => x.id === item.id)) own.value?.projects.push(item)
    copyDate.value = previous; copyOpen.value = true
  } catch (cause) { ElMessage.error(failure(cause, '上一工作日工时读取失败，请稍后重试。')) }
}
async function saveCopied() { if (await persist(selected.value, selected.value, [...daily.value, ...copyEntries.value])) copyOpen.value = false }
function copyWeekProjects() {
  if (weekDirty.value) return ElMessage.warning('请先保存或取消本周修改。')
  const context = `${props.companyId}|${props.token}|${selected.value}`
  const sourceWeek = weekDates(addDays(selected.value, -7))
  void readWorkHours(sourceWeek[0]!, sourceWeek.at(-1)!, false, props.token).then(source => {
    if (context !== `${props.companyId}|${props.token}|${selected.value}` || weekDirty.value) return
    const all = source.sheets.flatMap(x => x.entries).filter(x => sourceWeek.includes(x.date))
    const added = new Set(rows.value.filter(x => x.projectId).map(x => `${x.projectId}/${x.location}`))
    for (const entry of all) {
      const key = `${entry.projectId}/${entry.location}`
      if (added.has(key)) continue
      if (!allProjects.value.some(x => x.id === entry.projectId)) continue
      rows.value.push(makeRow(entry.projectId, entry.location)); added.add(key)
    }
    rows.value = rows.value.filter(x => x.projectId)
    if (!rows.value.length) { resetWeek(); ElMessage.warning('上周没有可复用的项目。') }
    else ElMessage.success('已复制项目行，请填写本周工时；未复制上周时数。')
  }).catch(cause => ElMessage.error(failure(cause, '上周项目读取失败，请重试。')))
}

let reportInitialized = false
const filter = reactive({ from: month.value, to: selected.value === month.value ? selected.value : addDays(selected.value,-1), username: '', department: '', projectId: '', location: '' })
const reportDateRange = computed<string[] | null>({
  get: () => filter.from && filter.to ? [filter.from, filter.to] : null,
  set: value => { filter.from = value?.[0] ?? ''; filter.to = value?.[1] ?? '' },
})
watch(() => [filter.from, filter.to], () => { reportGeneration++; report.value = undefined }, { flush: 'sync' })
const reportMode = rememberedTab('work-hours.report', 'people', ['people','projects','details'])
const filtered = computed(() => report.value ? reportRows(report.value, filter.from, filter.to, filter.username, filter.department, filter.projectId, filter.location) : [])
const reportProjects = computed(() => {
  const projects = new Map<string, { id: string; code: string; name: string }>()
  for (const sheet of report.value?.sheets ?? []) for (const entry of sheet.entries) {
    projects.set(entry.rootProjectId, { id: entry.rootProjectId, code: entry.rootProjectCode, name: entry.rootProjectName })
    projects.set(entry.projectId, { id: entry.projectId, code: entry.projectCode, name: entry.projectName })
  }
  for (const p of report.value?.projects ?? []) projects.set(p.id, p)
  return [...projects.values()].sort((a, b) => a.code.localeCompare(b.code))
})
const departmentTree = computed(() => report.value ? workHoursDepartmentTree(report.value) : [])
const personnelOptions = computed(() => report.value ? departmentPeople(report.value, filter.department) : [])
const reportPeople = computed(() => personnelOptions.value.filter(p => !filter.username || p.username === filter.username))
watch([() => filter.department, report], () => {
  if (report.value && filter.username && !personnelOptions.value.some(p => p.username === filter.username)) filter.username = ''
}, {flush:'sync'})
function personMissed(username: string) {
  return report.value?.compliance?.find(x => x.username.toLowerCase() === username.toLowerCase())?.days.filter(x => x.status === '未填写').map(x => x.date) ?? []
}
const peopleSummary = computed(() => reportPeople.value.map(person => {
  const values = filtered.value.filter(x => x.person.username === person.username).map(x => x.entry)
  return { person, hours: total(values), inside: total(values.filter(x => x.location === '厂内')), outside: total(values.filter(x => x.location === '厂外')),
    days: new Set(values.map(x => x.date)).size, missed: personMissed(person.username) }
}))
const projectSummary = computed(() => {
  const map = new Map<string, { id: string; code: string; name: string; hours: number; inside: number; outside: number; children: Set<string>; people: Set<string> }>()
  for (const row of filtered.value) {
    const e = row.entry
    const p = map.get(e.rootProjectId) ?? { id: e.rootProjectId, code: e.rootProjectCode, name: e.rootProjectName, hours: 0, inside: 0, outside: 0, children: new Set<string>(), people: new Set<string>() }
    p.hours += e.hours; p[e.location === '厂内' ? 'inside' : 'outside'] += e.hours; p.children.add(e.projectId); p.people.add(row.person.username); map.set(p.id, p)
  }
  return [...map.values()].sort((a, b) => b.hours - a.hours)
})
const chartDays = computed(() => dateRange(filter.from, filter.to).map(date => {
  const rows = filtered.value.filter(x => x.entry.date === date)
  return { date, inside: total(rows.filter(x => x.entry.location === '厂内').map(x => x.entry)), outside: total(rows.filter(x => x.entry.location === '厂外').map(x => x.entry)) }
}))
const chartMax = computed(() => Math.max(8, ...chartDays.value.map(x => x.inside + x.outside)))
const projectChartMax = computed(() => Math.max(8, ...projectSummary.value.map(x => x.hours)))
async function loadReport() {
  if (!filter.from || !filter.to) return ElMessage.warning('请选择起止日期。')
  const generation = ++reportGeneration
  reporting.value = true; error.value = ''
  try {
    const value = await readWorkHours(filter.from, filter.to, true, props.token)
    if (generation === reportGeneration) {
      report.value = value
      if (!reportInitialized) { filter.department = (value.defaultDepartmentId === undefined ? value.defaultDepartment : value.defaultDepartmentId) || ''; reportInitialized = true }
      if (value.participatingDepartmentIds && filter.department && !value.participatingDepartmentIds.includes(filter.department)) filter.department = ''
    }
  }
  catch (cause) { if (generation === reportGeneration) { report.value = undefined; error.value = failure(cause, '汇总读取失败，请调整日期或稍后重试。') } }
  finally { if (generation === reportGeneration) reporting.value = false }
}
const detailOpen = ref(false), detailTitle = ref(''), detailRows = ref<WorkHoursReportRow[]>([])
async function managementChanged() { await loadOwn(); if (tab.value === 'report') await loadReport(); else report.value = undefined }
function showDetails(label: string, rows: WorkHoursReportRow[]) { detailTitle.value = label; detailRows.value = rows; detailOpen.value = true }
const historyOpen = ref(false), history = ref<WorkHourRevision[]>([]), historyLoading = ref(false)
async function showHistory() {
  const context = `${props.companyId}|${props.token}|${month.value}`
  historyOpen.value = true; historyLoading.value = true; history.value = []
  try { const result = await readWorkHoursHistory(month.value.slice(0, 7), props.token); if (context === `${props.companyId}|${props.token}|${month.value}`) history.value = result.reverse() }
  catch (cause) { ElMessage.error(failure(cause, '修改记录读取失败，请稍后重试。')) }
  finally { historyLoading.value = false }
}
function exportReport() {
  if (!report.value) return
  let headers: string[], values: (string | number)[][]
  if (reportMode.value === 'people') {
    headers = ['人员', '部门／事业部', '已填天数', '工时合计', '厂内工时', '厂外工时', '整日未填工作日', '未填日期']
    values = peopleSummary.value.map(x => [x.person.displayName, x.person.department, x.days, x.hours, x.inside, x.outside, x.missed.length, x.missed.join('、')])
  } else if (reportMode.value === 'projects') {
    headers = ['主项目号', '项目名称', '投入工时', '厂内工时', '厂外工时', '填报人数', '项目及子项目数']
    values = projectSummary.value.map(x => [x.code, x.name, x.hours, x.inside, x.outside, x.people.size, x.children.size])
  } else {
    headers = ['日期', '人员', '部门／事业部', '所在地', '工时数', '项目号', '项目名称', '所属主项目']
    values = filtered.value.map(x => [x.entry.date, x.person.displayName, x.person.department, x.entry.location, x.entry.hours, x.entry.projectCode, x.entry.projectName, x.entry.rootProjectCode])
  }
  try { downloadProcurementWorkbook(`工时汇总_${filter.from}_${filter.to}.xlsx`, headers, values, '工时汇总', { title: '工时汇总报表', company: 'UPTON', scope: `${report.value.scope} · ${filter.from}至${filter.to}`, versionLabel: '报表类型', version: reportMode.value === 'people' ? '人员汇总' : reportMode.value === 'projects' ? '项目汇总' : '填报明细', exportedAt: new Date().toISOString(), exportedBy: report.value.displayName,
    notes: '子项目上卷不重复累计；整日未填按全部项目工时核对，不随项目或所在地筛选变化。', fields: [] }) }
  catch { ElMessage.error('报表导出失败，请检查浏览器下载权限后重试。') }
}
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value) { event.preventDefault(); event.returnValue = '' } }
window.addEventListener('beforeunload', beforeUnload)
onBeforeUnmount(() => { ownGeneration++; reportGeneration++; window.removeEventListener('beforeunload', beforeUnload) })
watch(() => [props.token, props.companyId], () => { ownGeneration++; reportGeneration++; copyOpen.value = false; detailOpen.value = false; historyOpen.value = false; history.value = []; detailRows.value = []; copyEntries.value = []; own.value = undefined; report.value = undefined; reportInitialized = false; rows.value = []; weekBaseline.value = '[]'; resetForm(); selected.value = chinaToday(); month.value = selected.value.slice(0, 7) + '-01'; Object.assign(filter, { from: month.value, to: selected.value === month.value ? selected.value : addDays(selected.value,-1), username: '', department: '', projectId: '', location: '' }); if (props.active) void loadOwn() }, { immediate: true })
watch(() => props.active, active => { if (active && !own.value && !loading.value) void loadOwn() })
void loadGlobalStatusContent().then(data => { holidays.value = new Map(data.holidayCalendars.flatMap(c => c.days.map(day => [day.date, day] as const))); calendarReady.value = true })
  .catch(() => { calendarError.value = '节假日标记暂时读取失败，仍可填写工时及查看服务端完整性检查，请刷新后重试。' })
</script>

<template>
  <section class="work-hours" aria-label="工时管理" :aria-busy="loading || reporting || saving">
    <div class="wh-tabs pdm-tabs" role="group" aria-label="工时页签"><button :aria-pressed="tab === 'entry'" :class="{ active: tab === 'entry' }" @click="changeTab('entry')">填报工时</button><button :aria-pressed="tab === 'report'" :class="{ active: tab === 'report' }" @click="changeTab('report')">汇总工时</button><button v-if="own?.canManageSettings ?? own?.canManage" :aria-pressed="tab === 'settings'" :class="{ active: tab === 'settings' }" @click="changeTab('settings')">工时设置</button></div>
    <p v-if="error" class="wh-error" role="alert">{{ error }} <button @click="tab === 'entry' ? loadOwn() : loadReport()">重试</button></p>
    <p v-if="calendarError" class="wh-warning" role="status">{{ calendarError }}</p>
    <template v-if="tab === 'entry'">
      <header class="wh-toolbar">
        <div class="wh-title"><Clock :size="17" /><strong>我的工时</strong><span>{{ own?.displayName }}</span></div>
        <div class="wh-controls"><button aria-label="上一期" @click="move(-1)"><ChevronLeft :size="15" /></button><strong>{{ mode === 'calendar' ? month.slice(0, 7) : `${week[0]} 至 ${week[6]}` }}</strong><button aria-label="下一期" @click="move(1)"><ChevronRight :size="15" /></button><button @click="selectDay(today)">回到今天</button><button :disabled="loading || saving || dirty" @click="loadOwn"><RefreshCw :size="14" />刷新</button></div>
        <div class="wh-controls wh-modes"><button :class="{ active: mode === 'calendar' }" @click="changeMode('calendar')">月历</button><button :class="{ active: mode === 'week' }" @click="changeMode('week')">周表</button></div>
      </header>
      <div class="wh-summary"><span>本月累计 <b>{{ number(monthHours) }}</b> 小时</span><span>已填 <b>{{ new Set(monthEntries.map(x => x.date)).size }}</b> 天</span><button :disabled="!missed.length" @click="selectDay(missed[0]!)">未填工作日 <b>{{ missed.length }}</b> 天</button><button :disabled="!incomplete.length" @click="selectDay(incomplete[0]!)">填写不足 <b>{{ incomplete.length }}</b> 天</button><span>按工时设置中的应填名单检查</span><button @click="showHistory">修改记录</button></div>
      <div v-if="mode === 'calendar'" class="wh-calendar-layout">
        <div class="pdm-panel wh-calendar">
          <div class="wh-weekdays"><span v-for="(day,index) in ['周一','周二','周三','周四','周五','周六','周日']" :key="day" :class="{ 'wh-rest': index === 6 }">{{ day }}</span></div>
          <div class="wh-day-grid"><button v-for="day in days" :key="day" class="wh-day" :class="{ outside: day.slice(0,7) !== month.slice(0,7), selected: day === selected, today: day === today, missed: visibleDayStatus(day) === '未填', rest: !workingDay(day, holidays) }" :aria-label="`${day} ${number(hoursOn(entries,day))}小时`" :aria-pressed="day === selected" @click="selectDay(day)"><span class="wh-date">{{ Number(day.slice(8)) }} <small v-if="day === today">今天</small><small v-else-if="holidays.get(day)">{{ holidays.get(day)?.isOffDay ? holidays.get(day)?.name : '补班' }}</small></span><b v-if="hoursOn(entries,day)">{{ number(hoursOn(entries,day)) }} 小时</b><span v-if="hoursOn(entries,day)" class="wh-day-records"><span v-for="entry in entries.filter(x => x.date === day)" :key="entry.id" class="wh-day-record" :title="`${entry.projectCode} · ${entry.projectName} · ${entry.location} · ${number(entry.hours)}小时`"><span class="wh-day-project"><span class="wh-day-project-code">{{ entry.projectCode }}</span><span class="wh-day-project-name">{{ entry.projectName }}</span></span><span class="wh-day-record-hours">{{ number(entry.hours) }}小时</span></span></span><span v-if="visibleDayStatus(day)" class="wh-day-status" :class="{ warning: visibleDayStatus(day) === '待核对' }">{{ visibleDayStatus(day) }}</span></button></div>
          <p class="wh-note">仅已生效名单参与完整性检查，默认周日休息，节假日按日历安排。未填不代表缺勤。</p>
        </div>
        <aside class="pdm-panel wh-day-editor"><header><strong>{{ selected }} · 当日工时</strong><span>合计 {{ number(total(daily)) }} 小时</span></header>
          <form @submit.prevent="saveDay"><label>日期<input type="date" :value="selected" :max="today" :disabled="saving || loading" aria-label="填报日期" @change="selectDay(($event.target as HTMLInputElement).value)"></label><fieldset :disabled="saving || loading || !own || selected > today"><label>所在地<select v-model="form.location" aria-label="所在地"><option>厂内</option><option>厂外</option></select></label><label>项目号<el-select v-model="form.projectId" filterable placeholder="搜索主项目号、子项目号或名称" aria-label="填报项目" :disabled="saving || loading || selected > today"><el-option v-for="project in allProjects" :key="project.id" :value="project.id" :label="`${project.code} · ${project.name}`" /></el-select></label><label>项目名称<input :value="projectName" readonly aria-label="自动带出项目名称"></label><label>工时数<input v-model.number="form.hours" type="number" min="0.5" max="24" step="0.5" required aria-label="工时数"></label><div class="wh-controls"><button v-for="value in [1,2,3,4,6,8,10,12]" :key="value" type="button" @click="form.hours = value">{{ value }}小时</button></div><div class="wh-controls"><button type="submit" class="pdm-primary-action">{{ editing ? '保存修改' : '保存并继续' }}</button><button v-if="formDirty || editing" type="button" @click="resetForm()">取消修改</button><button type="button" :disabled="dirty || !calendarReady" @click="copyPreviousDay">复制上一工作日</button></div></fieldset></form>
          <p v-if="selected > today" class="wh-note">未来日期仅供查看，实际工时请在当天或之后填写。</p>
          <div v-if="recent.length" class="wh-recent"><span>最近项目</span><button v-for="p in recent" :key="p.id" :disabled="saving || selected > today" @click="form.projectId = p.id">{{ p.code }} · {{ p.name }}</button></div>
          <h3>当日记录</h3><p v-if="!daily.length" class="wh-empty">暂无记录，填写后保存即可计入汇总。</p><div v-for="entry in daily" :key="entry.id" class="wh-entry"><div><b>{{ entry.projectCode }}</b> · {{ entry.projectName }}<small>{{ entry.location }} · {{ number(entry.hours) }} 小时</small></div><button :disabled="saving || dirty" @click="resetForm(entry)">修改</button><button :disabled="saving || dirty" @click="removeDay(entry)">删除</button></div>
        </aside>
      </div>
      <section v-else class="pdm-panel wh-week-panel"><div class="wh-controls wh-week-actions"><span>{{ weekDirty ? '有未保存修改' : '同一项目的厂内、厂外分行填写' }}</span><button :disabled="saving || loading || !own" @click="rows.push(makeRow())"><Plus :size="14" />添加项目</button><button :disabled="saving || loading || weekDirty" @click="copyWeekProjects">复制上周项目</button><button :disabled="saving || loading || !weekDirty" @click="cancelChanges">取消修改</button><button class="pdm-primary-action" :disabled="saving || loading || !own || !weekDirty" @click="saveWeek">保存本周</button></div>
        <div class="wh-table-wrap"><table class="wh-week-table"><thead><tr><th>项目号／项目名称</th><th>所在地</th><th v-for="(date,index) in week" :key="date" :class="{ 'wh-rest': !workingDay(date,holidays) }">{{ ['周一','周二','周三','周四','周五','周六','周日'][index] }}<small>{{ date.slice(5) }}</small></th><th>合计</th><th>操作</th></tr></thead><tbody><tr v-for="row in rows" :key="row.key"><td><el-select v-model="row.projectId" filterable placeholder="选择项目或子项目" aria-label="周表项目" :disabled="saving || loading"><el-option v-for="p in allProjects" :key="p.id" :label="`${p.code} · ${p.name}`" :value="p.id" /></el-select></td><td><select v-model="row.location" aria-label="周表所在地" :disabled="saving"><option>厂内</option><option>厂外</option></select></td><td v-for="date in week" :key="date" :class="{ 'wh-rest': !workingDay(date,holidays) }"><input v-model="row.values[date]" type="number" min="0" max="24" step="0.5" :aria-label="`${date}工时`" :disabled="saving || loading || date > today"></td><td>{{ number(Object.values(row.values).reduce<number>((n,x) => n + (Number(x) || 0),0)) }}</td><td><button :disabled="saving" aria-label="移除项目行" @click="rows = rows.filter(x => x.key !== row.key)">移除</button></td></tr></tbody><tfoot><tr><th colspan="2">每日合计</th><td v-for="date in week" :key="date">{{ number(hoursOn(weekInputs,date)) }}</td><td>{{ number(total(weekInputs)) }}</td><td></td></tr></tfoot></table></div><p class="wh-note">按Tab键切换日期，0或留空表示该日没有这项工时。保存本周会更新本周所有项目记录，并保留修改历史。</p></section>
    </template>
    <WorkHoursSettings v-else-if="tab === 'settings' && (own?.canManageSettings ?? own?.canManage)" :token="token" :company-id="companyId" @dirty-change="settingsDirty = $event" @changed="managementChanged" />
    <template v-else-if="tab === 'report'">
      <section class="pdm-panel wh-report-overview" aria-label="工时汇总统计">
      <form class="wh-report-filters" @submit.prevent="loadReport"><label class="wh-date-range"><el-date-picker v-model="reportDateRange" type="daterange" value-format="YYYY-MM-DD" format="YYYY-MM-DD" aria-label="汇总日期区间" range-separator="-" start-placeholder="开始日期" end-placeholder="结束日期" unlink-panels :clearable="false" /></label><label title="仅显示参与工时的部门；选择事业部或部门时包含其下级部门"><el-tree-select :model-value="filter.department" @update:model-value="filter.department = $event || ''" :data="departmentTree" node-key="id" :props="{label: 'name', children: 'children'}" check-strictly clearable filterable :render-after-expand="false" placeholder="全部部门／事业部" aria-label="汇总部门" /></label><label><select v-model="filter.username" aria-label="汇总人员"><option value="">全部</option><option v-for="p in personnelOptions" :key="p.username" :value="p.username">{{ p.displayName }}</option></select></label><label><el-select v-model="filter.projectId" filterable clearable placeholder="全部主／子项目" aria-label="汇总项目"><el-option v-for="p in reportProjects" :key="p.id" :value="p.id" :label="`${p.code} · ${p.name}`" /></el-select></label><label><select v-model="filter.location" aria-label="汇总所在地"><option value="">全部</option><option>厂内</option><option>厂外</option></select></label><button type="submit" class="pdm-primary-action" :disabled="reporting">查询</button></form>
      <div v-if="report" class="wh-summary wh-report-summary"><span>工时总计 <b>{{ number(total(filtered.map(x => x.entry))) }}</b> 小时</span><span>厂内 <b>{{ number(total(filtered.filter(x => x.entry.location === '厂内').map(x => x.entry))) }}</b> 小时</span><span>厂外 <b>{{ number(total(filtered.filter(x => x.entry.location === '厂外').map(x => x.entry))) }}</b> 小时</span><span>填报人数 <b>{{ new Set(filtered.map(x => x.person.username)).size }}</b> 人</span><span>整日未填 <b>{{ peopleSummary.reduce((n,x) => n+x.missed.length,0) }}</b> 人天</span></div>
      </section>
      <div v-if="report" class="wh-charts">
        <section class="pdm-panel"><header><strong>每日工时</strong><span class="wh-legend"><i></i>厂内 <i class="outside"></i>厂外 · 小时</span></header><div class="wh-bars" role="img" aria-label="按日期显示厂内、厂外工时"><div v-for="day in chartDays" :key="day.date" class="wh-bar-column" :title="`${day.date} 厂内${day.inside}小时，厂外${day.outside}小时`"><span>{{ number(day.inside+day.outside) }}</span><div class="wh-stack"><div class="wh-bar-outside" :style="{ height: `${day.outside/chartMax*125}px` }"></div><div class="wh-bar-inside" :style="{ height: `${day.inside/chartMax*125}px` }"></div></div><small>{{ day.date.slice(5) }}</small></div></div></section>
        <section class="pdm-panel wh-project-chart"><header><strong>项目投入</strong><span class="wh-legend"><i></i>厂内 <i class="outside"></i>厂外 · 小时</span></header>
          <div v-if="projectSummary.length" class="wh-bars wh-project-bars" role="group" aria-label="按项目显示厂内、厂外工时">
            <button v-for="p in projectSummary" :key="p.id" class="wh-bar-column" :title="`${p.code} · ${p.name} 厂内${number(p.inside)}小时，厂外${number(p.outside)}小时`" :aria-label="`查看${p.code}项目工时明细`" @click="showDetails(`${p.code} · ${p.name}`,filtered.filter(x => x.entry.rootProjectId === p.id))"><span>{{ number(p.hours) }}</span><div class="wh-stack"><div class="wh-bar-outside" :style="{ height: `${p.outside/projectChartMax*125}px` }"></div><div class="wh-bar-inside" :style="{ height: `${p.inside/projectChartMax*125}px` }"></div></div><small>{{ p.code }}</small></button>
          </div><p v-else class="wh-empty wh-project-empty">当前范围暂无工时记录</p>
        </section>
      </div>
      <div v-if="report" class="wh-report-bottom">
        <WorkHoursCompliance :view="report" :token="token" :company-id="companyId" :username="filter.username" :department="filter.department" @changed="managementChanged" />
        <section class="pdm-panel wh-report-list" aria-label="工时统计列表">
          <header><div class="wh-controls wh-report-modes pdm-tabs"><button :class="{ active: reportMode === 'people' }" :aria-pressed="reportMode === 'people'" @click="reportMode = 'people'">按人员</button><button :class="{ active: reportMode === 'projects' }" :aria-pressed="reportMode === 'projects'" @click="reportMode = 'projects'">按项目</button><button :class="{ active: reportMode === 'details' }" :aria-pressed="reportMode === 'details'" @click="reportMode = 'details'">填报明细</button></div><button :disabled="reporting" @click="exportReport"><Download :size="14" />导出Excel</button></header>
      <div class="wh-table-wrap wh-report-table"><table v-if="reportMode === 'people'"><thead><tr><th>人员</th><th>部门／事业部</th><th>已填天数</th><th>工时合计</th><th>厂内</th><th>厂外</th><th>整日未填工作日</th><th>操作</th></tr></thead><tbody><tr v-for="p in peopleSummary" :key="p.person.username"><td>{{ p.person.displayName }}{{ p.person.isActive ? '' : '（已停用）' }}</td><td>{{ p.person.department }}</td><td>{{ p.days }}天</td><td>{{ number(p.hours) }}小时</td><td>{{ number(p.inside) }}</td><td>{{ number(p.outside) }}</td><td :class="{ 'wh-danger': p.missed.length }" :title="p.missed.join('、')">{{ p.missed.length }}天</td><td><button @click="showDetails(`${p.person.displayName} · 工时明细`,filtered.filter(x => x.person.username === p.person.username))">查看明细</button></td></tr></tbody></table>
      <table v-else-if="reportMode === 'projects'"><thead><tr><th>主项目号</th><th>项目名称</th><th>工时合计</th><th>厂内</th><th>厂外</th><th>人数</th><th>操作</th></tr></thead><tbody><tr v-for="p in projectSummary" :key="p.id"><td>{{ p.code }}</td><td>{{ p.name }}</td><td>{{ number(p.hours) }}小时</td><td>{{ number(p.inside) }}</td><td>{{ number(p.outside) }}</td><td>{{ p.people.size }}</td><td><button @click="showDetails(`${p.code} · ${p.name}`,filtered.filter(x => x.entry.rootProjectId === p.id))">展开项目明细</button></td></tr></tbody></table>
      <table v-else><thead><tr><th>日期</th><th>人员</th><th>所在地</th><th>工时数</th><th>项目号</th><th>项目名称</th><th>所属主项目</th></tr></thead><tbody><tr v-for="row in filtered" :key="row.entry.id"><td>{{ row.entry.date }}</td><td>{{ row.person.displayName }}</td><td>{{ row.entry.location }}</td><td>{{ number(row.entry.hours) }}</td><td>{{ row.entry.projectCode }}</td><td>{{ row.entry.projectName }}</td><td>{{ row.entry.rootProjectCode }}</td></tr></tbody></table><p v-if="!filtered.length" class="wh-empty">当前筛选范围没有填报记录</p></div><p class="wh-note">子项目工时上卷至主项目，不重复累计。整日未填按人员的全部项目记录核对，不受项目、所在地筛选影响；仅作提醒，不作为缺勤判断。</p></section></div>
    </template>
    <el-dialog v-model="copyOpen" :title="`复制 ${copyDate} 的记录到 ${selected}`" width="660px" append-to-body :close-on-click-modal="!saving"><p>核对后保存，已有记录不会被覆盖；重复项目及所在地请先合并。</p><div v-for="(entry,index) in copyEntries" :key="index" class="wh-copy-row"><span>{{ allProjects.find(x => x.id === entry.projectId)?.code }} · {{ allProjects.find(x => x.id === entry.projectId)?.name }}</span><select v-model="entry.location" aria-label="复制记录所在地"><option>厂内</option><option>厂外</option></select><input v-model.number="entry.hours" type="number" min="0.5" max="24" step="0.5" aria-label="复制记录工时"><button @click="copyEntries.splice(index,1)">移除</button></div><template #footer><button :disabled="saving" @click="copyOpen=false">取消</button><button class="pdm-primary-action" :disabled="saving || !copyEntries.length" @click="saveCopied">保存复制工时</button></template></el-dialog>
    <el-drawer v-model="detailOpen" :title="detailTitle" size="min(900px, 95vw)" append-to-body><div class="wh-table-wrap"><table><thead><tr><th>日期</th><th>人员</th><th>项目号</th><th>项目名称</th><th>所在地</th><th>工时</th></tr></thead><tbody><tr v-for="row in detailRows" :key="row.entry.id"><td>{{ row.entry.date }}</td><td>{{ row.person.displayName }}</td><td>{{ row.entry.projectCode }}</td><td>{{ row.entry.projectName }}</td><td>{{ row.entry.location }}</td><td>{{ number(row.entry.hours) }}</td></tr></tbody></table><p v-if="!detailRows.length" class="wh-empty">暂无明细</p></div></el-drawer>
    <el-drawer v-model="historyOpen" :title="`${month.slice(0,7)} · 本人工时修改记录`" size="min(760px,95vw)" append-to-body><p v-if="historyLoading">正在读取…</p><p v-else-if="!history.length">本月暂无修改记录。</p><details v-for="item in history" :key="item.id" class="wh-history"><summary>{{ new Date(item.at).toLocaleString('zh-CN',{hour12:false}) }} · {{ item.actor }} · {{ item.dates.join('、') }}</summary><p v-if="item.reason">原因：{{ item.reason }}</p><div v-for="(list,index) in [item.before,item.after]" :key="index"><b>{{ index === 0 ? '修改前' : '修改后' }}</b><p v-if="!list.length">无记录</p><p v-for="entry in list" :key="entry.id">{{ entry.date }} · {{ entry.projectCode }} · {{ entry.projectName }} · {{ entry.location }} · {{ number(entry.hours) }}小时</p></div></details></el-drawer>
  </section>
</template>

<style scoped>
.work-hours{height:100%;min-height:0;overflow:auto;display:flex;flex-direction:column;gap:5px;padding:0 2px 8px;color:var(--pdm-text);font-size:12px}.work-hours button,.work-hours input,.work-hours select{font:inherit}.work-hours button:not(.wh-day),.work-hours input,.work-hours select{border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);background:var(--pdm-surface);color:var(--pdm-text);padding:5px 8px;min-height:28px}.work-hours button{display:inline-flex;align-items:center;justify-content:center;gap:4px}.work-hours button:disabled{opacity:.55;cursor:default}.work-hours .pdm-primary-action,.work-hours button.active{background:var(--pdm-theme-accent);border-color:var(--pdm-theme-accent);color:var(--pdm-theme-on-accent,#fff)}.work-hours>.wh-tabs.pdm-tabs{align-self:stretch;flex-shrink:0;width:100%;margin-block:0!important}.wh-toolbar,.wh-title,.wh-controls,.wh-summary{display:flex;align-items:center;gap:8px;flex-wrap:wrap}.wh-toolbar{justify-content:space-between;flex-shrink:0}.wh-title>span,.wh-note,.wh-muted{color:var(--pdm-muted)}.wh-title strong{font-size:15px}.wh-summary{background:var(--pdm-surface);padding:8px 12px;gap:20px;flex-shrink:0;border-bottom:1px solid var(--pdm-border)}.wh-summary b{color:var(--pdm-theme-accent);font-size:17px;margin:0 3px}.wh-summary>span:last-of-type{margin-left:auto;font-size:11px;color:var(--pdm-muted)}.wh-calendar-layout{display:grid;grid-template-columns:minmax(0,1fr) minmax(300px,34%);gap:8px;flex:1;min-height:520px}.wh-calendar{display:flex;flex-direction:column;overflow:hidden;min-width:0}.wh-weekdays,.wh-day-grid{display:grid;grid-template-columns:repeat(7,minmax(0,1fr))}.wh-weekdays span{text-align:center;padding:9px;background:var(--pdm-table-header)}.wh-day-grid{flex:1;grid-template-rows:repeat(6,minmax(65px,1fr))}.work-hours .wh-day{border:0;border-top:1px solid var(--pdm-border);border-right:1px solid var(--pdm-border);background:var(--pdm-surface);color:var(--pdm-text);padding:6px;display:flex;align-items:flex-start;justify-content:flex-start;flex-direction:column;gap:5px;min-width:0;text-align:left}.wh-day.rest,.wh-weekdays .wh-rest{background:color-mix(in srgb,var(--pdm-text) 9%,var(--pdm-surface))}.wh-day.outside{opacity:.45}.wh-day.selected{box-shadow:inset 0 0 0 2px var(--pdm-theme-accent);background:var(--pdm-theme-accent-soft)}.wh-day.missed{background:var(--pdm-danger-soft)}.wh-date{display:flex;align-items:center;gap:4px;flex-wrap:wrap}.wh-date small{font-size:10px;color:var(--pdm-theme-accent);overflow:hidden;max-width:70px;text-overflow:ellipsis;white-space:nowrap}.wh-day.today .wh-date{color:var(--pdm-theme-accent);font-weight:600}.wh-day>b{color:var(--pdm-theme-accent)}.wh-day-status{font-size:11px}.wh-day.missed .wh-day-status,.wh-danger{color:var(--pdm-danger)}.wh-day-status.warning{color:var(--pdm-orange)}.wh-note{font-size:11px;line-height:1.7;padding:4px 8px;margin:0}.wh-day-editor{overflow:auto;min-width:0;padding:0 10px 10px}.wh-day-editor>header,.wh-charts header{display:flex;justify-content:space-between;align-items:center;padding:10px 2px;border-bottom:1px solid var(--pdm-border);gap:8px}.wh-day-editor>header span{color:var(--pdm-theme-accent)}.wh-day-editor form,.wh-day-editor fieldset{display:flex;flex-direction:column;gap:10px}.wh-day-editor form{padding:12px 0}.wh-day-editor label{display:grid;grid-template-columns:65px minmax(0,1fr);align-items:center;gap:7px}.wh-day-editor fieldset{border:0;padding:0;margin:0;min-width:0}.wh-day-editor input,.wh-day-editor select{width:100%;min-width:0}.wh-day-editor input[readonly]{background:var(--pdm-table-header);color:var(--pdm-muted)}.wh-day-editor h3{font-size:12px;border-top:1px solid var(--pdm-border);padding-top:10px}.wh-entry{display:flex;gap:5px;align-items:center;padding:8px 0;border-bottom:1px solid var(--pdm-border)}.wh-entry>div{flex:1;min-width:0;overflow-wrap:anywhere}.wh-entry small{display:block;color:var(--pdm-muted);margin-top:4px}.wh-entry button{flex-shrink:0}.wh-recent{display:flex;flex-wrap:wrap;gap:4px}.wh-recent>span{flex-basis:100%;color:var(--pdm-muted)}.wh-recent button{max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:11px!important}.wh-empty{text-align:center;color:var(--pdm-muted);padding:20px 6px}.wh-error,.wh-warning{margin:0;padding:8px;background:var(--pdm-danger-soft);color:var(--pdm-danger)}.wh-warning{background:var(--pdm-surface-muted,var(--pdm-table-header));color:var(--pdm-orange)}.wh-week-panel{padding:10px}.wh-week-actions{margin-bottom:10px}.wh-week-actions>span{flex:1;color:var(--pdm-muted)}.wh-table-wrap{overflow:auto;min-width:0}.wh-table-wrap table{width:100%;border-collapse:collapse;font-size:12px;text-align:center}.wh-table-wrap th,.wh-table-wrap td{padding:8px;border-bottom:1px solid var(--pdm-border);white-space:nowrap}.wh-table-wrap th{background:var(--pdm-table-header);position:sticky;top:0}.wh-week-table{min-width:1000px;table-layout:fixed}.wh-week-table th:first-child{width:290px}.wh-week-table th:nth-child(2){width:85px}.wh-week-table th small{display:block;font-weight:400;margin-top:5px}.wh-week-table input{width:100%;min-width:60px;text-align:center}.wh-week-table .wh-rest{background:color-mix(in srgb,var(--pdm-text) 9%,var(--pdm-surface))}.wh-week-table tfoot{font-weight:600}.wh-report-filters{display:flex;gap:7px;flex-wrap:wrap;align-items:flex-end}.wh-report-filters label{display:flex;flex:0 0 120px;flex-direction:column;gap:4px;width:120px;min-width:0}.wh-report-filters .el-select,.wh-report-filters select{width:100%;box-sizing:border-box}.wh-report-filters .wh-date-range{flex-basis:280px;width:280px;max-width:100%}.wh-date-range :deep(.el-date-editor){width:100%;min-width:0;box-sizing:border-box}.wh-date-range :deep(.el-range-input){min-width:0}.wh-report-summary{justify-content:space-around;gap:12px}.wh-report-summary>span:last-of-type{margin:0;color:var(--pdm-text);font-size:12px}.wh-charts{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:5px;flex-shrink:0}.wh-charts section{min-width:0;padding:0 12px 10px}.wh-legend{font-size:11px;color:var(--pdm-muted);display:flex;align-items:center;gap:5px}.wh-legend i{width:9px;height:9px;background:var(--pdm-theme-accent)}.wh-legend i.outside{background:var(--pdm-orange)}.wh-bars{display:flex;gap:8px;height:190px;align-items:flex-end;overflow:auto;padding:8px 2px 0}.wh-bar-column{flex:1;min-width:35px;text-align:center;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;height:100%;gap:4px;font-variant-numeric:tabular-nums}.wh-stack{width:75%;max-width:45px;display:flex;flex-direction:column;justify-content:flex-end;min-height:2px}.wh-bar-inside{background:var(--pdm-theme-accent)}.wh-bar-outside{background:var(--pdm-orange)}.wh-bar-column>small{font-size:10px;white-space:nowrap}.wh-report-table{flex:1;min-height:120px}.wh-report-table button{border:0!important;color:var(--pdm-theme-accent)!important;background:transparent!important}.wh-copy-row{display:flex;align-items:center;gap:6px;padding:8px;border-bottom:1px solid var(--pdm-border)}.wh-copy-row>span{flex:1}.wh-copy-row input{width:80px}.wh-history{padding:10px 0;border-bottom:1px solid var(--pdm-border)}.wh-history summary{cursor:pointer}.wh-history p{font-size:12px;overflow-wrap:anywhere}
.wh-report-overview{flex-shrink:0;min-width:0;padding:10px;display:flex;flex-direction:column;gap:8px}.wh-report-overview .wh-report-summary{margin:0;padding:8px 4px}.wh-project-empty{height:190px;box-sizing:border-box;margin:0;display:grid;place-items:center}.work-hours .wh-project-bars>.wh-bar-column{display:flex;justify-content:flex-end;min-width:90px;min-height:0;border:0;border-radius:0;background:transparent;padding:0}.wh-project-bars>.wh-bar-column:focus-visible{outline:2px solid var(--pdm-theme-accent);outline-offset:-2px}
.wh-report-bottom{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:5px;flex:1;min-height:320px;align-items:stretch}.wh-report-list{min-width:0;min-height:0;display:flex;flex-direction:column;padding:8px 12px;overflow:hidden}.wh-report-list>header{display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap;min-height:56px;margin-bottom:10px;flex-shrink:0;box-sizing:border-box}.wh-report-list .wh-report-table{min-height:0}.wh-report-list table{min-width:650px}.wh-report-list>.wh-note{flex-shrink:0}
@media(max-width:1100px){.wh-report-bottom{flex:none;grid-template-columns:minmax(0,1fr);grid-auto-rows:360px}}
.wh-report-filters :deep(.el-select__wrapper),.wh-report-filters :deep(.el-date-editor),.work-hours .wh-report-filters>label>select,.work-hours .wh-report-filters>button{height:32px;min-height:32px;box-sizing:border-box}
.wh-day-records{display:flex;flex-direction:column;gap:3px;align-self:stretch;min-height:0;overflow-y:auto;flex:1 1 0;font-size:11px}.wh-day-record{display:flex;align-items:baseline;justify-content:space-between;gap:2px 6px;flex-shrink:0;line-height:1.4;min-width:0}.wh-day-project{display:flex;gap:4px;min-width:0;flex:1}.wh-day-project-code{white-space:nowrap;flex-shrink:0}.wh-day-project-name{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1}.wh-day-record-hours{white-space:nowrap;font-variant-numeric:tabular-nums;flex-shrink:0}.wh-day .wh-date,.wh-day>b,.wh-day .wh-day-status{flex-shrink:0}
@media(max-width:1100px){.wh-day-record{flex-wrap:wrap}.wh-day-project{flex-basis:100%}.wh-day-record-hours{margin-left:auto}.wh-calendar-layout{display:flex;flex-direction:column;flex:none;min-height:0}.wh-calendar{min-height:720px}.wh-day-editor{overflow:visible;flex-shrink:0}.wh-summary{gap:10px}.wh-summary>span:last-of-type{margin-left:0}.wh-charts{grid-template-columns:minmax(0,1fr)}}
@media(max-width:760px){.wh-calendar-layout{display:flex;flex-direction:column;min-height:0}.wh-calendar{min-height:720px}.wh-day-editor{overflow:visible}.wh-day-grid{grid-template-rows:repeat(6,minmax(60px,1fr))}.wh-toolbar{gap:8px}.wh-controls{gap:4px}.wh-day{padding:4px!important;gap:4px!important;font-size:11px}.wh-day .wh-muted{display:none}.wh-charts{display:block}.wh-charts section+section{margin-top:5px}.wh-report-table table{min-width:650px}.wh-summary b{font-size:15px}.wh-weekdays span{padding:6px 2px}.wh-copy-row{flex-wrap:wrap}.wh-copy-row>span{flex-basis:100%}}
</style>
