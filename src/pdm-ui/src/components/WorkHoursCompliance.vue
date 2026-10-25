<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'
import { downloadProcurementWorkbook } from '../procurementWorkbook'
import { complianceSummary, departmentPeople, validateHours } from '../workHours'
import type { WorkHoursView, WorkHoursDayCheck, WorkHourInput, WorkHourRevision, WorkHoursProject } from '../workHours'
import { readWorkHoursSettings, setWorkHoursException, readWorkHoursCorrection, correctWorkHours, remindWorkHours } from '../api'
const props = defineProps<{ view: WorkHoursView; token: string; companyId: string; username: string; department: string }>()
const emit = defineEmits<{ changed: [] }>()
const busy = ref(false), error = ref(''), open = ref(false), selected = ref(''), selectedDate = ref('')
const exceptionOpen = ref(false), correctionOpen = ref(false)
const exception = reactive({ from: '', to: '', kind: 'target', hours: 0, reason: '' })
const exceptionDateRange = computed<string[] | null>({
  get: () => exception.from && exception.to ? [exception.from, exception.to] : null,
  set: value => { exception.from = value?.[0] ?? ''; exception.to = value?.[1] ?? '' },
})
watch(() => exception.kind, kind => { if (kind === 'mute' && exception.to < props.view.today) exception.to = props.view.today })
const correctionRows = ref<Array<WorkHourInput & { key: number }>>([]), correctionProjects = ref<WorkHoursProject[]>([]), correctionVersion = ref(0), correctionReason = ref(''), revisions = ref<WorkHourRevision[]>([])
let sequence = 0, generation = 0
const canManage = (username: string) => props.view.canManage && props.view.manageableUsernames?.some(x => x.toLowerCase() === username.toLowerCase())
const departmentMembers = computed(() => departmentPeople(props.view, props.department))
const people = computed(() => (props.view.compliance ?? []).flatMap(check => {
  const person = departmentMembers.value.find(x => x.username.toLowerCase() === check.username.toLowerCase())
  const summary = complianceSummary(check.days)
  return person && (!props.username || props.username === person.username) ? [{ person, days: check.days, ...summary, pending: check.reminderEnabled === false ? 0 : summary.pending, reminderEnabled: check.reminderEnabled !== false }] : []
}))
const current = computed(() => people.value.find(x => x.person.username === selected.value))
const n = (value: number) => value.toLocaleString('zh-CN',{maximumFractionDigits:1})
function show(username: string) { selected.value = username; selectedDate.value = people.value.find(x => x.person.username === username)?.days.find(x => x.actual < x.expected)?.date ?? ''; open.value = true; error.value = '' }
function openException() {
  if (!current.value) return
  Object.assign(exception,{ from: selectedDate.value || current.value.days[0]?.date || props.view.today, to: selectedDate.value || current.value.days[0]?.date || props.view.today, kind:'target',hours:0,reason:'' }); exceptionOpen.value = true
}
async function run(action: () => Promise<void>) {
  if (busy.value) return
  const request = generation; busy.value = true; error.value = ''
  try { await action() } catch (cause) { if (request === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '工时处理失败，请核对后重试。') }
  finally { if (request === generation) busy.value = false }
}
async function saveException() {
  if (!exception.from || !exception.to || exception.from > exception.to) return ElMessage.warning('请选择有效的起止日期。')
  if (!exception.reason.trim()) return ElMessage.warning('请填写特殊处理原因。')
  const username = selected.value, token = props.token, request = generation
  await run(async () => {
    const settings = await readWorkHoursSettings(token)
    if (request !== generation) return
    await setWorkHoursException({ ...exception, username, hours: exception.kind === 'target' ? Number(exception.hours) : null, rowVersion: settings.rowVersion }, token)
    if (request !== generation) return
    exceptionOpen.value = false; emit('changed'); ElMessage.success('特殊处理已保存，原工时记录保持完整。')
  })
}
async function send(names: string[]) {
  if (!names.length) return ElMessage.info('当前没有需要提醒且在授权范围内的人员。')
  const token = props.token, request = generation
  await run(async () => {
    const result = await remindWorkHours(names,token)
    if (request !== generation) return
    const reasons = result.results?.filter(x => !x.sent).map(x => `${x.displayName}：${x.reason}`).join('；')
    if (result.sent > 0) ElMessage.success(`已向${result.sent}人发送站内工时提醒。${result.skipped ? `另有${result.skipped}人未发送：${reasons || '请刷新后核对应填名单、缺填及暂停提醒状态'}。` : ''}`)
    else ElMessage.info(`本次未发送提醒：${reasons || '请刷新后核对应填名单、缺填及暂停提醒状态'}。`)
  })
}
async function openCorrection() {
  if (!selectedDate.value) return ElMessage.warning('请先选择要更正的日期。')
  const date = selectedDate.value, username = selected.value, token = props.token, request = generation
  await run(async () => {
    const value = await readWorkHoursCorrection(username,date.slice(0,7),token)
    if (request !== generation) return
    correctionRows.value = value.sheet.entries.filter(x => x.date === date).map(x => ({date:x.date,projectId:x.projectId,location:x.location,hours:x.hours,key:++sequence}))
    const projects = new Map(value.projects.map(x => [x.id,x]))
    for (const e of value.sheet.entries) if (!projects.has(e.projectId)) projects.set(e.projectId,{ id:e.projectId,code:e.projectCode,name:e.projectName,rootProjectId:e.rootProjectId,rootProjectCode:e.rootProjectCode,rootProjectName:e.rootProjectName })
    correctionProjects.value = [...projects.values()]; correctionVersion.value = value.sheet.rowVersion; revisions.value = [...value.sheet.history].reverse(); correctionReason.value = ''; correctionOpen.value = true
  })
}
async function saveCorrection() {
  const entries = correctionRows.value.map(({key,...entry}) => ({...entry,hours:Number(entry.hours)}))
  const invalid = validateHours(entries)
  if (invalid || !correctionReason.value.trim()) return ElMessage.warning(invalid || '请填写代更正原因。')
  const request = generation, token = props.token, username = selected.value, date = selectedDate.value
  await run(async () => {
    await correctWorkHours({ username, reason:correctionReason.value, sheet:{from:date,to:date,entries,versions:[{month:date.slice(0,7),rowVersion:correctionVersion.value}]} },token)
    if (request !== generation) return
    correctionOpen.value = false; emit('changed'); ElMessage.success('工时已更正，修改前后记录已保留，并已通知本人核对。')
  })
}
function selectDate(day: WorkHoursDayCheck) { selectedDate.value = day.date }
function exportChecks() {
  try {
    downloadProcurementWorkbook('人员工时完整性.xlsx',['人员','部门','应填天数','完整天数','未填天数','不足天数','缺少小时','已豁免天数','待提醒天数','暂停提醒天数'],
      people.value.map(p => [p.person.displayName,p.person.department,p.expected,p.complete,p.missing,p.short,p.deficit,p.exempt,p.pending,p.muted]),'人员完整性',
      {title:'人员工时完整性',company:'UPTON',scope:props.view.scope,versionLabel:'统计口径',version:'每日全部项目工时',exportedAt:new Date().toISOString(),exportedBy:props.view.displayName,notes:'只检查已生效名单的已过工作日；暂停提醒仍计缺填，已豁免不计应填。',fields:[]})
  } catch { ElMessage.error('完整性报表导出失败，请检查浏览器下载权限后重试。') }
}
watch(() => [props.token,props.companyId],() => { generation++; open.value=false; exceptionOpen.value=false; correctionOpen.value=false; busy.value=false; error.value=''; selected.value='' })
</script>

<template>
  <section class="pdm-panel wh-compliance" aria-label="人员填报完整性">
    <header><strong>人员填报完整性</strong><span>按所选日期内已过工作日检查 · 每日全部项目工时</span><div class="wh-header-actions"><button :disabled="!people.length" @click="exportChecks">导出完整性报表</button><button v-if="view.canManage" :disabled="busy" @click="send(people.filter(x => x.pending && x.person.isActive && canManage(x.person.username)).map(x => x.person.username))">提醒当前缺填人员</button></div></header>
    <p v-if="error" class="wh-check-error" role="alert">{{ error }}</p>
    <div class="wh-check-table"><table class="wh-compliance-table"><colgroup><col v-for="(width,index) in [10,12,8,6,6,6,10,8,16,18]" :key="index" :style="{ '--wh-column-width': `${width}%` }"></colgroup><thead><tr><th>人员</th><th>部门</th><th>应填天数</th><th>完整</th><th>未填</th><th>不足</th><th>缺少小时</th><th>已豁免</th><th>处理状态</th><th>操作</th></tr></thead><tbody><tr v-for="p in people" :key="p.person.username"><td>{{ p.person.displayName }}{{ p.person.isActive ? '' : '（已停用）' }}</td><td>{{ p.person.department }}</td><td>{{ p.expected }}</td><td>{{ p.complete }}</td><td :class="{danger:p.missing}">{{ p.missing }}</td><td :class="{warning:p.short}">{{ p.short }}</td><td>{{ n(p.deficit) }}</td><td>{{ p.exempt }}</td><td>{{ !p.person.isActive ? '已停用，不提醒' : !p.reminderEnabled ? '当前不要求填报' : p.pending ? `${p.pending}天待处理` : p.muted ? '已暂停提醒' : '无需提醒' }}<small v-if="p.muted && p.pending">另有{{ p.muted }}天暂停提醒</small></td><td><div class="wh-person-actions"><button @click="show(p.person.username)">逐日检查</button><button v-if="canManage(p.person.username) && p.pending && p.person.isActive" :disabled="busy" @click="send([p.person.username])">提醒</button></div></td></tr></tbody></table></div>
    <p v-if="!people.length" class="wh-check-help">当前范围没有已生效的应填人员或已到检查时间的工作日。请由负责人或技术助理在工时设置中配置名单。</p>
    <el-drawer v-model="open" :title="`${current?.person.displayName || ''} · 逐日填报检查`" size="min(950px,96vw)" append-to-body>
      <p v-if="error" class="wh-check-error" role="alert">{{ error }}</p>
      <div class="wh-check-actions"><label>处理日期<input v-model="selectedDate" type="date" :max="view.today" aria-label="完整性处理日期" :disabled="busy"></label><template v-if="current && canManage(current.person.username)"><button :disabled="busy" @click="openException">调整应填／暂停提醒</button><button :disabled="busy" @click="openCorrection">代更正实际工时</button></template></div>
      <div class="wh-check-table"><table><thead><tr><th>日期</th><th>应填小时</th><th>实际小时</th><th>状态</th><th>提醒</th><th>处理原因</th><th>操作</th></tr></thead><tbody><tr v-for="d in current?.days" :key="d.date" :class="{selected:d.date === selectedDate}"><td>{{ d.date }}</td><td>{{ n(d.expected) }}</td><td>{{ n(d.actual) }}</td><td :class="{danger:d.actual < d.expected}">{{ d.status }}</td><td>{{ d.muted ? '暂停' : d.actual < d.expected ? '待处理' : '无需提醒' }}</td><td class="wh-check-reason">{{ d.reason || '—' }}</td><td><button @click="selectDate(d)">选择日期</button></td></tr></tbody></table></div>
    </el-drawer>
    <el-dialog v-model="exceptionOpen" title="特殊情况处理" width="min(620px,95vw)" append-to-body :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy">
      <form class="wh-check-form" @submit.prevent="saveException"><label>日期范围<el-date-picker v-model="exceptionDateRange" type="daterange" value-format="YYYY-MM-DD" format="YYYY-MM-DD" aria-label="特殊处理日期区间" range-separator="-" start-placeholder="开始日期" end-placeholder="结束日期" unlink-panels :clearable="false" :disabled="busy" /></label><label>处理方式<select v-model="exception.kind" aria-label="特殊处理方式" :disabled="busy"><option value="target">调整应填工时／豁免</option><option value="mute">暂停提醒（仍计缺填）</option></select></label><label v-if="exception.kind === 'target'">每日应填小时<input v-model.number="exception.hours" type="number" min="0" max="24" step="0.5" required aria-label="特殊情况应填小时" :disabled="busy"></label><p class="wh-check-help">应填0小时表示豁免；暂停提醒只关闭所选日期的通知，不改变缺填统计。暂停至结束日期，当天之后仍未补齐的历史工时也恢复提醒；豁免按所选工时日期保留，原填报记录不删除。</p><label>原因<textarea v-model="exception.reason" maxlength="500" required aria-label="特殊处理原因" :disabled="busy"></textarea></label><p v-if="error" class="wh-check-error" role="alert">{{ error }}</p><button class="pdm-primary-action" :disabled="busy">保存特殊处理</button></form>
    </el-dialog>
    <el-dialog v-model="correctionOpen" :title="`${current?.person.displayName || ''} · ${selectedDate} · 代更正工时`" width="min(900px,96vw)" append-to-body :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy">
      <p class="wh-check-help">保存会更新这一天的工时，保留修改前后内容及操作人，并通知本人核对。全部移除后保存表示删除当日记录。</p><p v-if="error" class="wh-check-error" role="alert">{{ error }}</p>
      <div v-for="r in correctionRows" :key="r.key" class="wh-correction-row"><el-select v-model="r.projectId" filterable placeholder="项目或子项目" aria-label="代更正项目" :disabled="busy"><el-option v-for="p in correctionProjects" :key="p.id" :value="p.id" :label="`${p.code} · ${p.name}`" /></el-select><select v-model="r.location" aria-label="代更正所在地" :disabled="busy"><option>厂内</option><option>厂外</option></select><input v-model.number="r.hours" type="number" min="0.5" max="24" step="0.5" aria-label="代更正小时" :disabled="busy"><button :disabled="busy" @click="correctionRows = correctionRows.filter(x => x.key !== r.key)">移除</button></div>
      <div class="wh-check-actions"><button :disabled="busy" @click="correctionRows.push({key:++sequence,date:selectedDate,projectId:'',location:'厂内',hours:4})">增加工时记录</button></div><form class="wh-check-form" @submit.prevent="saveCorrection"><label>代更正原因<textarea v-model="correctionReason" maxlength="500" required aria-label="代更正原因" :disabled="busy"></textarea></label><button class="pdm-primary-action" :disabled="busy">保存代更正</button></form>
      <details class="wh-check-history"><summary>本月修改历史（{{ revisions.length }}次）</summary><details v-for="h in revisions" :key="h.id"><summary>{{ new Date(h.at).toLocaleString('zh-CN',{hour12:false}) }} · {{ h.actor }} · {{ h.dates.join('、') }}</summary><p v-if="h.reason">原因：{{ h.reason }}</p><div v-for="(list,index) in [h.before,h.after]" :key="index"><b>{{ index ? '修改后' : '修改前' }}</b><p v-if="!list.length">无记录</p><p v-for="e in list" :key="e.id">{{ e.date }} · {{ e.projectCode }} · {{ e.projectName }} · {{ e.location }} · {{ n(e.hours) }}小时</p></div></details></details>
    </el-dialog>
  </section>
</template>

<style scoped>
.wh-compliance>.wh-check-table>table.wh-compliance-table{min-width:780px!important;max-width:none!important}.wh-compliance>.wh-check-table>table.wh-compliance-table:not(.pdm-fit-columns)>colgroup>col{width:var(--wh-column-width)!important}.wh-person-actions{display:flex;align-items:center;justify-content:center;gap:4px;flex-wrap:wrap}.wh-person-actions button{white-space:nowrap}
.wh-compliance{display:flex;flex-direction:column;min-height:0;overflow:hidden}.wh-compliance>header{min-height:56px;box-sizing:border-box;flex-shrink:0}.wh-compliance>.wh-check-table{flex:1;min-height:0;max-height:none}.wh-compliance>p{flex-shrink:0}
.wh-compliance{flex-shrink:0;min-width:0;padding:8px 12px}.wh-compliance header,.wh-check-actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-bottom:10px}.wh-compliance header>span,.wh-check-help{color:var(--pdm-muted);font-size:12px;line-height:1.7}.wh-header-actions{margin-left:auto;display:flex;gap:8px;flex-wrap:wrap}.wh-check-table{overflow:auto;max-height:370px}.wh-check-table table{border-collapse:collapse;width:100%;text-align:center;font-size:12px}.wh-check-table th,.wh-check-table td{padding:8px;border-bottom:1px solid var(--pdm-border);white-space:nowrap}.wh-check-table th{background:var(--pdm-table-header);position:sticky;top:0}.wh-check-table small{display:block;color:var(--pdm-muted)}.wh-check-table .wh-check-reason{white-space:normal;min-width:160px;max-width:300px}.wh-check-table .selected{background:var(--pdm-theme-accent-soft)}.danger,.wh-check-error{color:var(--pdm-danger)}.warning{color:var(--pdm-orange)}.wh-check-error{background:var(--pdm-danger-soft);padding:10px}.wh-compliance button,.wh-check-table button,.wh-check-actions button,.wh-check-actions input,.wh-check-form input,.wh-check-form select,.wh-check-form textarea,.wh-check-form button,.wh-correction-row select,.wh-correction-row input,.wh-correction-row button{font:inherit;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);background:var(--pdm-surface);color:var(--pdm-text);padding:5px 8px;min-height:28px}.wh-check-form{display:flex;flex-direction:column;gap:12px}.wh-check-form label{display:flex;flex-direction:column;gap:5px}.wh-check-form :deep(.el-date-editor){width:100%;min-width:0;box-sizing:border-box}.wh-check-form :deep(.el-range-input){min-width:0}.wh-check-form textarea{min-height:65px}.wh-check-form .pdm-primary-action{background:var(--pdm-theme-accent);color:var(--pdm-theme-on-accent,#fff)}.wh-correction-row{display:flex;gap:8px;align-items:center;margin-bottom:8px}.wh-correction-row .el-select{flex:1;min-width:160px}.wh-correction-row input{width:90px}.wh-check-history{margin-top:20px;font-size:12px}.wh-check-history details{padding:10px;border-bottom:1px solid var(--pdm-border)}.wh-check-history p{overflow-wrap:anywhere}.wh-check-form button:disabled,.wh-check-actions button:disabled{opacity:.5}@media(max-width:760px){.wh-correction-row{flex-wrap:wrap}.wh-correction-row .el-select{flex-basis:100%}.wh-check-table table{min-width:780px}}
</style>
