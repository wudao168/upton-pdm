import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import * as api from '../src/api'
import ProjectCalendar from '../src/components/ProjectCalendar.vue'
import { calendarEvents, monthDays, shiftMonth } from '../src/projectCalendar'
import type { WorkbenchProject, WorkbenchData, WorkbenchView } from '../src/projectWorkbench'
import type { ProjectPlanTask } from '../src/types'
vi.mock('../src/api', () => ({ readProjectWorkbench: vi.fn() }))
const today = '2026-10-05'
function task(id = 't1', start = '2026-10-01', finish = '2026-10-04', assignee = 'engineer'): ProjectPlanTask {
  return { id, name: '机械设计', stage: 'Design', assignee, plannedStart: start, plannedFinish: finish, durationDays: 4, completionPercent: 20, status: 'InProgress', predecessorTaskIds: [], weight: 1, isRequired: true, isMilestone: false, sortOrder: 0 }
}
function item(id = 'root', tasks = [task()]): WorkbenchProject {
  return { project: { id, code: 'P-' + id, name: '装配项目', owner: 'pm', stage: '进行中', vaultName: 'PDM', vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', quantity: 1, serialNumbers: [], responsibleUsers: ['pm'], primaryProjectManager: 'pm', collaborativeProjectManagers: [], designers: [], canAssignExecutionUnit: false, canManageMainStaffing: false, canAssignDesigners: false, canReadContent: true },
    plan: { id: 'plan-' + id, projectId: id, templateId: '', templateName: '', currentStage: 'Design', plannedStart: '2026-10-01', plannedFinish: '2026-11-10', forecastFinish: '2026-11-10', baselineVersion: 1, tasks, approvalStatus: 'Approved', createdBy: 'pm', createdAt: '', updatedBy: 'pm', updatedAt: '', rowVersion: 1 }, planState: 'Approved', completionPercent: 20,
    budget: null, procurement: null, quality: null, warnings: [], canRegisterRisk: true }
}
function data(projects: WorkbenchProject[], view: WorkbenchView = 'manager'): WorkbenchData {
  return { views: [{ key: 'manager', label: '项目经理' }, { key: 'engineer', label: '工程师' }], view, updatedAt: today + 'T00:00:00Z', projects, risks: [] }
}

beforeEach(() => vi.clearAllMocks())
describe('项目日历', () => {
  it('月历周一开始、跨年和闰年日期正确', () => {
    expect(monthDays('2026-11-01')[0]).toBe('2026-10-26')
    expect(monthDays('2026-11-01')).toHaveLength(42)
    expect(monthDays('2028-02-01')).toContain('2028-02-29')
    expect(shiftMonth('2026-12-31', 1)).toBe('2027-01-01')
  })
  it('草稿不形成正式事件，传播任务不重复，完成与实际日期保留', () => {
    const root=item(), child=item('child', [{...task('child-task'),sourceTaskId:'t1',isMilestone:true,status:'Completed',actualFinish:'2026-10-03'}])
    const events=calendarEvents([root,child], [], '2026-10-04')
    expect(events).toHaveLength(1)
    expect(events[0]).toMatchObject({projectId:'child',tone:'done',actual:'2026-10-03',date:'2026-10-04'})
    child.plan!.approvalStatus='Draft'
    expect(calendarEvents([child], [], today)).toHaveLength(0)
  })
  it('隐藏过去已完成事项，保留今天、未来及历史未完成事项', () => {
    const past={...task('past'),status:'Completed',actualFinish:'2026-10-04'} as ProjectPlanTask
    const current={...past,id:'current',plannedFinish:today}
    const future={...past,id:'future',plannedFinish:'2026-10-06'}
    const pending={...task('pending'),isMilestone:true}
    const projects=[item('past',[past]),item('today',[current]),item('future',[future]),item('pending',[pending])]
    projects[0]!.plan!.stages=[{code:'Design',name:'设计',participatesInDelivery:true}] as NonNullable<WorkbenchProject['plan']>['stages']
    const events=calendarEvents(projects,[],today)
    expect(events.some(event=>event.projectId==='past')).toBe(false)
    expect(events.map(event=>event.projectId)).toEqual(['pending','today','future'])
  })
  it('没有明确交付阶段时不凭项目结束日虚构发货', () => {
    expect(calendarEvents([item()], [], today).some(event=>event.kind==='shipping')).toBe(false)
  })
  it('历史逾期风险保留，已关闭风险不是逾期', () => {
    const risks=[{id:'r',projectId:'root',title:'整改',impact:'需处理',owner:'pm',dueDate:'2026-09-01',severity:'warning',createdBy:'pm',createdAt:'',closed:false,canClose:true}]
    expect(calendarEvents([item()],risks,today).find(event=>event.kind==='risk')?.tone).toBe('danger')
    risks[0]!.closed=true
    expect(calendarEvents([item()],risks,today).find(event=>event.kind==='risk')).toBeUndefined()
  })
  it('默认双月，翻月及筛选生效，跨月逾期可查看并跳转', async () => {
    vi.mocked(api.readProjectWorkbench).mockResolvedValue(data([item()]))
    const wrapper=mount(ProjectCalendar,{props:{token:'t',companyId:'c',currentUsername:'pm',users:[]}})
    await flushPromises()
    expect(wrapper.findAll('.pc-month')).toHaveLength(2)
    expect(wrapper.find('.pc-heading').exists()).toBe(false)
    expect(wrapper.find('.pc-note').exists()).toBe(false)
    const board=wrapper.findAll('button').find(button=>button.text()==='最大化看板')!
    await board.trigger('click')
    expect(wrapper.classes()).toContain('is-maximized')
    window.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))
    await flushPromises()
    expect(wrapper.classes()).not.toContain('is-maximized')
    const first=wrapper.find('.pc-month h2').text()
    await wrapper.get('[aria-label="下一期"]').trigger('click')
    expect(wrapper.find('.pc-month h2').text()).not.toBe(first)
    await wrapper.findAll('button').find(button=>button.text()==='回到本月')!.trigger('click')
    await wrapper.findAll('.pc-day').find(day=>day.attributes('aria-label')?.startsWith('2026-10-04'))!.trigger('click')
    expect(wrapper.find('.pc-details').text()).toContain('P-root')
    await wrapper.get('.pc-table button').trigger('click')
    expect(wrapper.emitted('open')?.[0]?.[0]).toBe('root')
    await wrapper.get('[aria-label="搜索项目"]').setValue('不存在的项目')
    expect(wrapper.find('.pc-details').text()).not.toContain('P-root')
    wrapper.unmount()
  })
  it('接口异常不会直接显示英文错误', async () => {
    vi.spyOn(console,'error').mockImplementation(()=>{})
    vi.mocked(api.readProjectWorkbench).mockRejectedValue(new Error('Failed to fetch'))
    const wrapper=mount(ProjectCalendar,{props:{token:'t',companyId:'c',currentUsername:'pm',users:[]}})
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('请检查网络')
    expect(wrapper.text()).not.toContain('Failed to fetch')
    wrapper.unmount()
  })
})
