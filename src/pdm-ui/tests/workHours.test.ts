import { describe, expect, it } from 'vitest'
import { dayStatus, missingDays, reportRows, validateHours, weekDates, workingDay } from '../src/workHours'
import type { WorkHoursView } from '../src/workHours'

describe('工时日历与汇总口径', () => {
  const holidays = new Map([['2026-10-01', { name: '国庆', isOffDay: true }], ['2026-10-10', { name: '调休', isOffDay: false }]])
  it('正确处理跨月周、节假日、补班、启用前及未来日期', () => {
    expect(weekDates('2026-10-01')).toEqual(['2026-09-28','2026-09-29','2026-09-30','2026-10-01','2026-10-02','2026-10-03','2026-10-04'])
    expect(workingDay('2026-10-01', holidays)).toBe(false)
    expect(workingDay('2026-10-10', holidays)).toBe(true)
    expect(workingDay('2026-10-17', holidays)).toBe(true)
    expect(workingDay('2026-10-18', holidays)).toBe(false)
    expect(workingDay('2026-10-18', new Map([['2026-10-18', { name: '调休', isOffDay: false }]]))).toBe(true)
    expect(dayStatus('2026-10-18', 0, '2026-10-12', '2026-10-20', holidays)).toBe('')
    expect(dayStatus('2026-10-17', 0, '2026-10-12', '2026-10-20', holidays)).toBe('未填')
    expect(dayStatus('2026-10-09', 0, '2026-10-12', '2026-10-20', holidays)).toBe('')
    expect(dayStatus('2026-10-20', 0, '2026-10-12', '2026-10-20', holidays)).toBe('待填写')
    expect(dayStatus('2026-10-21', 0, '2026-10-12', '2026-10-20', holidays)).toBe('')
    expect(dayStatus('2026-10-19', 6, '2026-10-12', '2026-10-20', holidays)).toBe('待核对')
    expect(missingDays([{ date: '2026-10-12', projectId: 'p', location: '厂内', hours: 8 }], '2026-10-01', '2026-10-21', '2026-10-12', '2026-10-20', holidays)).toEqual(['2026-10-13','2026-10-14','2026-10-15','2026-10-16','2026-10-17','2026-10-19'])
  })
  it('校验重复、半小时步长、非法数字以及跨项目每日总量', () => {
    const entry = { date: '2026-10-12', projectId: 'p', location: '厂内', hours: 8 }
    expect(validateHours([entry, { ...entry, location: '厂外', hours: 0.5 }])).toBe('')
    expect(validateHours([entry, entry])).toContain('重复')
    expect(validateHours([{ ...entry, hours: 1.25 }])).toContain('0.5')
    expect(validateHours([{ ...entry, hours: NaN }])).toContain('0.5')
    expect(validateHours([{ ...entry, hours: 16 }, { ...entry, projectId: 'child', hours: 9 }])).toContain('24')
  })
  it('主项目筛选包含子项目，人员和所在地条件组合，不重复累计', () => {
    const person = { username: 'a', displayName: '甲', department: '设计部', isActive: true }
    const entry = { id: '1', date: '2026-10-12', projectId: 'child', projectCode: 'P-1', projectName: '子项目', rootProjectId: 'root', rootProjectCode: 'P', rootProjectName: '主项目', location: '厂外', hours: 6 }
    const view = { people: [person], sheets: [{ username: 'a', entries: [entry] }] } as WorkHoursView
    const rows = reportRows(view, '2026-10-01','2026-10-31','a','设计部','root','厂外')
    expect(rows).toHaveLength(1)
    expect(rows.reduce((n,x) => n+x.entry.hours,0)).toBe(6)
    expect(reportRows(view,'2026-10-01','2026-10-31','','','child','厂内')).toHaveLength(0)
  })
})
