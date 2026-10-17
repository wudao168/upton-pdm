import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import * as api from '../src/api'
import ProjectCalendar from '../src/components/ProjectCalendar.vue'
import { loadGlobalStatusContent } from '../src/globalStatusContent'
import type { GlobalStatusContentLibrary } from '../src/globalStatusContent'
import { calendarEvents, monthDays, shiftMonth } from '../src/projectCalendar'
import type { WorkbenchProject, WorkbenchData, WorkbenchView } from '../src/projectWorkbench'
import type { ProjectPlanTask } from '../src/types'
vi.mock('../src/api', () => ({ readProjectCalendar: vi.fn(), readCalendarTodos: vi.fn(), createUserTodo: vi.fn() }))
vi.mock('../src/globalStatusContent', async importOriginal => ({
  ...await importOriginal<typeof import('../src/globalStatusContent')>(), loadGlobalStatusContent: vi.fn(),
}))
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

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.readCalendarTodos).mockResolvedValue([])
  vi.mocked(loadGlobalStatusContent).mockResolvedValue({ holidayCalendars: [{ year: 2026, papers: [], days: [
    { date: '2026-10-01', name: '国庆节', isOffDay: true },
    { date: '2026-10-07', name: '国庆节', isOffDay: true },
    { date: '2026-10-10', name: '国庆节', isOffDay: false },
  ] }] } as unknown as GlobalStatusContentLibrary)
})
describe('项目日历', () => {
  it('uses the shared todo fields, defaults the clicked date and creates a normal task-center todo', async () => {
    const clock = vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-10-15T04:00:00Z'))
    vi.mocked(api.readProjectCalendar).mockResolvedValue(data([]))
    vi.mocked(api.createUserTodo).mockResolvedValue({ id: 'private', recipient: 'pm', category: 'ProjectTodo', title: '日历待办', content: '跟进资料', sourceKey: 'calendar-todo:c:g', dueDate: '2026-10-16', createdAt: '' })
    const wrapper = mount(ProjectCalendar, { props: { token: 't', companyId: 'c', currentUsername: 'pm', users: [] }, global: { stubs: { teleport: true, ElSelect: true } } })
    await flushPromises()
    await wrapper.findAll('.pc-day').find(day => day.attributes('aria-label')?.startsWith('2026-10-16'))!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '新增待办')!.trigger('click'); await flushPromises()
    const fields = wrapper.findComponent({ name: 'ProjectTodoFields' })
    expect(fields.props('dueDate')).toBe('2026-10-16')
    expect(fields.props('recipients')).toEqual(['pm'])
    await wrapper.get('textarea[aria-label="待办内容"]').setValue('跟进资料')
    await wrapper.findAll('button').find(button => button.text() === '保存待办')!.trigger('click'); await flushPromises()
    expect(api.createUserTodo).toHaveBeenCalledWith({ content: '跟进资料', dueDate: '2026-10-16', recipientUsernames: ['pm'] }, 't')
    expect(wrapper.emitted('todosChanged')).toHaveLength(1)
    wrapper.unmount(); clock.mockRestore()
  })
  it('shows existing project todos from the same inbox and clears them when the company changes', async () => {
    vi.mocked(api.readProjectCalendar).mockResolvedValue(data([]))
    vi.mocked(api.readCalendarTodos).mockResolvedValue([{ id: 'private', recipient: 'pm', category: 'ProjectTodo', title: '日历待办', content: '跟进资料', sourceKey: 'calendar-todo:c:g', dueDate: '2026-10-16', createdAt: '' }])
    const clock = vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-10-15T04:00:00Z'))
    const wrapper = mount(ProjectCalendar, { props: { token: 't', companyId: 'c', currentUsername: 'pm', users: [] } })
    await flushPromises()
    expect(wrapper.text()).toContain('跟进资料')
    vi.mocked(api.readCalendarTodos).mockResolvedValue([])
    await wrapper.setProps({ companyId: 'other' }); await flushPromises()
    expect(wrapper.text()).not.toContain('跟进资料')
    wrapper.unmount(); clock.mockRestore()
  })

  it('月历和周历显示节日名称及补班，普通日期不提前显示节假日', async () => {
    const clock = vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-10-15T04:00:00Z'))
    vi.mocked(api.readProjectCalendar).mockResolvedValue(data([item()]))
    const wrapper = mount(ProjectCalendar, { props: { token: 't', companyId: 'c', currentUsername: 'pm', users: [] } })
    await flushPromises()
    const day = (date: string) => wrapper.findAll('.pc-day').find(node => node.attributes('aria-label')?.startsWith(date))!
    expect(day('2026-10-01').get('.pc-holiday').text()).toBe('国庆节')
    expect(day('2026-10-10').get('.pc-holiday').text()).toBe('补班')
    expect(day('2026-10-10').get('.pc-holiday').attributes('title')).toContain('调休上班')
    expect(day('2026-10-08').find('.pc-holiday').exists()).toBe(false)
    expect(day('2026-10-18').get('.pc-holiday').text()).toBe('重阳节')
    await wrapper.findAll('button').find(button => button.text() === '月')!.trigger('click')
    expect(wrapper.findAll('.pc-month')).toHaveLength(1)
    expect(day('2026-10-01').get('.pc-holiday').text()).toBe('国庆节')
    await day('2026-10-01').trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '周')!.trigger('click')
    expect(wrapper.get('.pc-week-view').text()).toContain('国庆节')
    wrapper.unmount()
    clock.mockRestore()
  })
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
    vi.mocked(api.readProjectCalendar).mockResolvedValue(data([item()]))
    const wrapper=mount(ProjectCalendar,{props:{token:'t',companyId:'c',currentUsername:'pm',users:[]}})
    await flushPromises()
    expect(api.readProjectCalendar).toHaveBeenCalledWith('t', expect.any(AbortSignal))
    expect(wrapper.findAll('.pc-month')).toHaveLength(2)
    expect(wrapper.find('.pc-heading').exists()).toBe(false)
    expect(wrapper.find('.pc-note').exists()).toBe(false)
    const board=wrapper.findAll('button').find(button=>button.text()==='最大化看板')!
    await board.trigger('click')
    expect(wrapper.get('.project-calendar').classes()).toContain('is-maximized')
    window.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))
    await flushPromises()
    expect(wrapper.get('.project-calendar').classes()).not.toContain('is-maximized')
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
  it('切换公司取消旧请求，旧响应不能覆盖新公司日历', async () => {
    let resolveOld!: (value: WorkbenchData) => void
    vi.mocked(api.readProjectCalendar).mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
      .mockResolvedValueOnce(data([item('new')]))
    const wrapper=mount(ProjectCalendar,{props:{token:'t',companyId:'old',currentUsername:'pm',users:[]}})
    const oldSignal=vi.mocked(api.readProjectCalendar).mock.calls[0]![1]!
    await wrapper.setProps({companyId:'new'}); await flushPromises()
    expect(oldSignal.aborted).toBe(true)
    resolveOld(data([item('old')])); await flushPromises()
    const titles=wrapper.findAll('.pc-event').map(event=>event.attributes('title')).join(' ')
    expect(titles).toContain('P-new'); expect(titles).not.toContain('P-old')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    const currentSignal=vi.mocked(api.readProjectCalendar).mock.calls[1]![1]!
    wrapper.unmount(); expect(currentSignal.aborted).toBe(true)
  })

  it('接口异常不会直接显示英文错误', async () => {
    vi.spyOn(console,'error').mockImplementation(()=>{})
    vi.mocked(api.readProjectCalendar).mockRejectedValue(new Error('Failed to fetch'))
    const wrapper=mount(ProjectCalendar,{props:{token:'t',companyId:'c',currentUsername:'pm',users:[]}})
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('请检查网络')
    expect(wrapper.text()).not.toContain('Failed to fetch')
    wrapper.unmount()
  })
})
