import { describe, expect, it } from 'vitest'
import { dayStatus, departmentPeople, missingDays, reportRows, validateHours, weekDates, workingDay, workHoursDepartmentTree, unassignedWorkHoursDepartment } from '../src/workHours'
import type { WorkHoursView } from '../src/workHours'

describe('工时日历与汇总口径', () => {
  it('汇总部门树与组织关系按同级部门编码排序，不采用排序号或名称顺序', () => {
    const view:WorkHoursView={today:'2026-10-21',startDate:'2026-10-01',username:'admin',displayName:'管理员',scope:'当前公司',projects:[],sheets:[],people:[],participatingDepartmentIds:['t1','t2','t3','a','b'],organizationUnits:[
      {id:'t3',parentUnitId:null,code:'T3',name:'甲部门',sortOrder:-1,isActive:true},
      {id:'b',parentUnitId:'t1',code:'T1-B',name:'甲组',sortOrder:1,isActive:true},
      {id:'t1',parentUnitId:null,code:'T1',name:'丙部门',sortOrder:999,isActive:true},
      {id:'a',parentUnitId:'t1',code:'T1-A',name:'乙组',sortOrder:99,isActive:true},
      {id:'t2',parentUnitId:null,code:'T2',name:'乙部门',sortOrder:0,isActive:true}
    ]}
    const tree=workHoursDepartmentTree(view)
    expect(tree.map(x=>x.id)).toEqual(['t1','t2','t3']);expect(tree[0]!.children!.map(x=>x.id)).toEqual(['a','b'])
  })
  it('部门选项仅保留参与部门及子部门，空名单不回退到历史人员部门', () => {
    const view = {people:[{username:'a',displayName:'甲',department:'未分配',isActive:true}],organizationUnits:[
      {id:'t1',parentUnitId:null,name:'T1事业部',sortOrder:1,isActive:true},
      {id:'child',parentUnitId:'t1',name:'机械组',sortOrder:1,isActive:true},
      {id:'t3',parentUnitId:null,name:'T3事业部',sortOrder:2,isActive:true},
      {id:'empty',parentUnitId:null,name:'参与但暂无人员',sortOrder:3,isActive:true}
    ],participatingDepartmentIds:['t1','child','empty']} as WorkHoursView
    const tree=workHoursDepartmentTree(view)
    expect(tree.map(x=>x.id)).toEqual(['t1','empty']);expect(tree[0]!.children!.map(x=>x.id)).toEqual(['child'])
    expect(workHoursDepartmentTree({...view,participatingDepartmentIds:[]})).toEqual([])
    expect(workHoursDepartmentTree({...view,organizationUnits:[],participatingDepartmentIds:[]})).toEqual([])
  })
  it('按组织层级构建部门树，父级包含下级且同名部门按ID区分', () => {
    const view = {people:[
      {username:'a',displayName:'甲',department:'机械组',departmentId:'t2-mech',isActive:true},
      {username:'b',displayName:'乙',department:'电气组',departmentId:'t2-elec',isActive:true},
      {username:'c',displayName:'丙',department:'机械组',departmentId:'t3-mech',isActive:true},
      {username:'d',displayName:'丁',department:'未分配',departmentId:null,isActive:true}
    ],organizationUnits:[
      {id:'t2',parentUnitId:null,name:'T2事业部',sortOrder:1,isActive:true},
      {id:'t2-mech',parentUnitId:'t2',name:'机械组',sortOrder:1,isActive:true},
      {id:'t2-elec',parentUnitId:'t2',name:'电气组',sortOrder:2,isActive:true},
      {id:'t3',parentUnitId:null,name:'T3事业部',sortOrder:2,isActive:true},
      {id:'t3-mech',parentUnitId:'t3',name:'机械组',sortOrder:1,isActive:false}
    ]} as WorkHoursView
    const tree=workHoursDepartmentTree(view)
    expect(tree.map(x=>x.id)).toEqual(['t2','t3',unassignedWorkHoursDepartment])
    expect(tree[0]!.children!.map(x=>x.id)).toEqual(['t2-mech','t2-elec'])
    expect(tree[1]!.children![0]!.name).toBe('机械组（已停用）')
    expect(departmentPeople(view,'t2').map(x=>x.username)).toEqual(['a','b'])
    expect(departmentPeople(view,'t2-mech').map(x=>x.username)).toEqual(['a'])
    expect(departmentPeople(view,'t3').map(x=>x.username)).toEqual(['c'])
    expect(departmentPeople(view,unassignedWorkHoursDepartment).map(x=>x.username)).toEqual(['d'])
    expect(departmentPeople(view)).toHaveLength(4)
  })
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
