import { addDays } from './projectWorkbench'

export interface WorkHourInput { date: string; location: string; hours: number; projectId: string }
export interface WorkHourEntry extends WorkHourInput { id: string; projectCode: string; projectName: string; rootProjectId: string; rootProjectCode: string; rootProjectName: string }
export interface WorkHourRevision { id: string; at: string; actor: string; dates: string[]; before: WorkHourEntry[]; after: WorkHourEntry[]; reason?: string }
export interface WorkHoursSheet { companyId: string; username: string; month: string; rowVersion: number; entries: WorkHourEntry[]; history: WorkHourRevision[] }
export interface WorkHoursPerson { username: string; displayName: string; department: string; isActive: boolean; departmentId?: string | null }
export interface WorkHoursOrganizationUnit { id: string; parentUnitId: string | null; name: string; sortOrder: number; isActive: boolean; code?: string | null }
export interface WorkHoursDepartmentNode { id: string; name: string; children?: WorkHoursDepartmentNode[] }
export interface WorkHoursProject { id: string; code: string; name: string; rootProjectId: string; rootProjectCode: string; rootProjectName: string }
export interface WorkHoursView { today: string; startDate: string; username: string; displayName: string; scope: string; people: WorkHoursPerson[]; projects: WorkHoursProject[]; sheets: WorkHoursSheet[]; canManage?: boolean; canManageSettings?: boolean; manageableUsernames?: string[]; defaultDepartment?: string; defaultDepartmentId?: string | null; organizationUnits?: WorkHoursOrganizationUnit[]; participatingDepartmentIds?: string[]; compliance?: WorkHoursCompliance[] }
export interface WorkHoursDayCheck { date: string; expected: number; actual: number; status: string; muted: boolean; reason: string }
export interface WorkHoursCompliance { username: string; days: WorkHoursDayCheck[]; reminderEnabled?: boolean }
export interface WorkHoursRequirement { id: string; username: string; from: string; to: string | null; hours: number }
export interface WorkHoursException { id: string; username: string; from: string; to: string; kind: 'target' | 'mute'; hours: number | null; reason: string }
export interface WorkHoursGrant { username: string; unitIds: string[] }
export interface WorkHoursDepartmentPeriod { unitId: string; from: string; to: string | null }
export interface WorkHoursDepartmentPerson extends WorkHoursPerson { required: boolean; canEdit: boolean }
export interface WorkHoursDepartmentGroup { id: string; name: string; participating: boolean; people: WorkHoursDepartmentPerson[] }
export interface SaveWorkHoursGroupSettings { companyId: string; rowVersion: number; departments: Array<{unitId: string; participating: boolean}>; people: Array<{username: string; required: boolean}> }
export interface WorkHoursSettingRevision { id: string; at: string; actor: string; action: string; reason: string; beforeRequirements: WorkHoursRequirement[]; afterRequirements: WorkHoursRequirement[]; beforeExceptions: WorkHoursException[]; afterExceptions: WorkHoursException[]; beforeGrants: WorkHoursGrant[]; afterGrants: WorkHoursGrant[]; beforeDepartments?: WorkHoursDepartmentPeriod[]; afterDepartments?: WorkHoursDepartmentPeriod[] }
export interface WorkHoursManagement { today: string; rowVersion: number; canAuthorize: boolean; scope: string; people: WorkHoursPerson[]; units: Array<{ id: string; name: string }>; assistants: WorkHoursPerson[]; requirements: WorkHoursRequirement[]; exceptions: WorkHoursException[]; grants: WorkHoursGrant[]; history: WorkHoursSettingRevision[]; departmentGroups?: WorkHoursDepartmentGroup[]; unassignedPeopleCount?: number; canAdjust?: boolean }
export function complianceSummary(days: WorkHoursDayCheck[]) {
  return { expected: days.filter(x => x.expected > 0).length, complete: days.filter(x => x.status === '完整').length,
    missing: days.filter(x => x.status === '未填写').length, short: days.filter(x => x.status === '填写不完整').length,
    exempt: days.filter(x => x.status === '已豁免').length, deficit: days.reduce((n,x) => n + Math.max(0,x.expected-x.actual),0),
    pending: days.filter(x => x.actual < x.expected && !x.muted).length, muted: days.filter(x => x.actual < x.expected && x.muted).length }
}
export interface SaveWorkHours { from: string; to: string; entries: WorkHourInput[]; versions: Array<{ month: string; rowVersion: number }> }
export interface MoveWorkHoursDay { companyId: string; sourceDate: string; targetDate: string; sourceRowVersion: number; targetRowVersion: number }
export type HolidayMap = Map<string, { name: string; isOffDay: boolean }>
export function weekDates(date: string) {
  const offset = (new Date(date + 'T00:00:00Z').getUTCDay() + 6) % 7
  return Array.from({ length: 7 }, (_, index) => addDays(date, index - offset))
}
export function dateRange(from: string, to: string) {
  const result: string[] = []
  for (let day = from; day <= to && result.length < 366; day = addDays(day, 1)) result.push(day)
  return result
}
export function workingDay(day: string, holidays: HolidayMap) {
  const override = holidays.get(day)
  return override ? !override.isOffDay : new Date(day + 'T00:00:00Z').getUTCDay() !== 0
}
export function hoursOn(entries: WorkHourInput[], day: string) { return entries.filter(x => x.date === day).reduce((n, x) => n + x.hours, 0) }
export function dayStatus(day: string, hours: number, start: string, today: string, holidays: HolidayMap) {
  if (hours > 0) return day < today && workingDay(day, holidays) && hours < 8 ? '待核对' : '已填'
  if (day > today) return ''
  if (day === today) return '待填写'
  return day >= start && workingDay(day, holidays) ? '未填' : ''
}
export function missingDays(entries: WorkHourInput[], from: string, to: string, start: string, today: string, holidays: HolidayMap) {
  return dateRange(from > start ? from : start, to).filter(day => day < today && workingDay(day, holidays) && hoursOn(entries, day) === 0)
}
export function validateHours(entries: WorkHourInput[]) {
  if (entries.some(x => !x.projectId || !['厂内', '厂外'].includes(x.location) || !Number.isFinite(x.hours) || x.hours <= 0 || x.hours > 24 || !Number.isInteger(x.hours * 2)))
    return '请选择项目和所在地，工时须大于0并按0.5小时填写。'
  const keys = entries.map(x => `${x.date}/${x.projectId}/${x.location}`)
  if (new Set(keys).size !== keys.length) return '同一天、同一项目、同一所在地存在重复记录，请合并工时。'
  if ([...new Set(entries.map(x => x.date))].some(day => hoursOn(entries, day) > 24)) return '同一天所有项目的工时合计不能超过24小时。'
  return ''
}

export function appendWorkHoursDay(existing: WorkHourInput[], copied: WorkHourInput[], date: string): WorkHourInput[] {
  const result = new Map<string, WorkHourInput>()
  for (const entry of [...existing, ...copied]) {
    const key = JSON.stringify([entry.projectId, entry.location])
    result.set(key, { date, projectId: entry.projectId, location: entry.location, hours: (result.get(key)?.hours ?? 0) + Number(entry.hours) })
  }
  return [...result.values()]
}
export interface WorkHoursReportRow { entry: WorkHourEntry; person: WorkHoursPerson }
export const unassignedWorkHoursDepartment = 'work-hours-unassigned'
export function departmentPeople(view: WorkHoursView, department = '') {
  if (!department) return view.people
  if (department === unassignedWorkHoursDepartment) return view.people.filter(p => !p.departmentId && p.department === '未分配')
  const units = view.organizationUnits ?? []
  if (!units.some(u => u.id === department)) return view.people.filter(p => p.department === department)
  const ids = new Set([department])
  for (let count = -1; count !== ids.size;) {
    count = ids.size
    for (const unit of units) if (unit.parentUnitId && ids.has(unit.parentUnitId)) ids.add(unit.id)
  }
  return view.people.filter(p => p.departmentId ? ids.has(p.departmentId) : units.some(u => ids.has(u.id) && u.name === p.department))
}
export function workHoursDepartmentTree(view: WorkHoursView): WorkHoursDepartmentNode[] {
  const participating = view.participatingDepartmentIds === undefined ? undefined : new Set(view.participatingDepartmentIds)
  const units = (view.organizationUnits ?? []).filter(unit => !participating || participating.has(unit.id))
  if (participating && !units.length) return []
  if (!units.length) return [...new Set(view.people.map(p => p.department))].map(name => ({id:name,name}))
  const nodes = new Map(units.map(u => [u.id, {id:u.id,name:u.isActive ? u.name : `${u.name}（已停用）`,children:[]} as WorkHoursDepartmentNode]))
  const roots: WorkHoursDepartmentNode[] = []
  for (const unit of [...units].sort((a,b) => a.code && b.code ? a.code.localeCompare(b.code,'zh-CN') : a.sortOrder-b.sortOrder || a.name.localeCompare(b.name,'zh-CN'))) {
    const node = nodes.get(unit.id)!
    const parent = unit.parentUnitId ? nodes.get(unit.parentUnitId) : undefined
    if (parent) parent.children!.push(node)
    else roots.push(node)
  }
  if (!participating && view.people.some(p => !p.departmentId && p.department === '未分配')) roots.push({id:unassignedWorkHoursDepartment,name:'未分配'})
  return roots
}
export function reportRows(view: WorkHoursView, from: string, to: string, username = '', department = '', projectId = '', location = ''): WorkHoursReportRow[] {
  const people = departmentPeople(view, department).filter(p => !username || p.username === username)
  return view.sheets.flatMap(sheet => {
    const person = people.find(p => p.username.toLowerCase() === sheet.username.toLowerCase())
    return person ? sheet.entries.filter(e => e.date >= from && e.date <= to && (!projectId || e.projectId === projectId || e.rootProjectId === projectId) && (!location || e.location === location)).map(entry => ({ entry, person })) : []
  }).sort((a, b) => b.entry.date.localeCompare(a.entry.date) || a.person.displayName.localeCompare(b.person.displayName, 'zh-CN'))
}
