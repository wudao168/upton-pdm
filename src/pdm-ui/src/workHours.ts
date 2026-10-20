import { addDays } from './projectWorkbench'

export interface WorkHourInput { date: string; location: string; hours: number; projectId: string }
export interface WorkHourEntry extends WorkHourInput { id: string; projectCode: string; projectName: string; rootProjectId: string; rootProjectCode: string; rootProjectName: string }
export interface WorkHourRevision { id: string; at: string; actor: string; dates: string[]; before: WorkHourEntry[]; after: WorkHourEntry[] }
export interface WorkHoursSheet { companyId: string; username: string; month: string; rowVersion: number; entries: WorkHourEntry[]; history: WorkHourRevision[] }
export interface WorkHoursPerson { username: string; displayName: string; department: string; isActive: boolean }
export interface WorkHoursProject { id: string; code: string; name: string; rootProjectId: string; rootProjectCode: string; rootProjectName: string }
export interface WorkHoursView { today: string; startDate: string; username: string; displayName: string; scope: string; people: WorkHoursPerson[]; projects: WorkHoursProject[]; sheets: WorkHoursSheet[] }
export interface SaveWorkHours { from: string; to: string; entries: WorkHourInput[]; versions: Array<{ month: string; rowVersion: number }> }
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
export interface WorkHoursReportRow { entry: WorkHourEntry; person: WorkHoursPerson }
export function reportRows(view: WorkHoursView, from: string, to: string, username = '', department = '', projectId = '', location = ''): WorkHoursReportRow[] {
  const people = view.people.filter(p => (!username || p.username === username) && (!department || p.department === department))
  return view.sheets.flatMap(sheet => {
    const person = people.find(p => p.username.toLowerCase() === sheet.username.toLowerCase())
    return person ? sheet.entries.filter(e => e.date >= from && e.date <= to && (!projectId || e.projectId === projectId || e.rootProjectId === projectId) && (!location || e.location === location)).map(entry => ({ entry, person })) : []
  }).sort((a, b) => b.entry.date.localeCompare(a.entry.date) || a.person.displayName.localeCompare(b.person.displayName, 'zh-CN'))
}
