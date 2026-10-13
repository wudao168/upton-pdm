import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '../src/api'
import ProjectWorkbench from '../src/components/ProjectWorkbench.vue'
import { projectActions, relevantProject, resourceOverlaps, progress } from '../src/projectWorkbench'
import type { WorkbenchData, WorkbenchProject, WorkbenchView } from '../src/projectWorkbench'
import type { ProjectPlanTask } from '../src/types'

vi.mock('../src/api', () => ({ readProjectWorkbench: vi.fn(), registerWorkbenchRisk: vi.fn(), closeWorkbenchRisk: vi.fn(), readProjectOpl: vi.fn(), saveProjectOpl: vi.fn(), listProjectAudit: vi.fn(), addProjectManagerNote: vi.fn() }))
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
function mountWorkbench() {
  return mount(ProjectWorkbench, { props: { users: [{ username: 'engineer', displayName: '工程师一', role: 'Engineer', isActive: true }], token: 'token', currentUsername: 'pm', companyId: 'company' }, global: { stubs: { ElDatePicker: { props: ['modelValue'], emits: ['update:modelValue'], template: `<input type="date" :value="modelValue" @input="$emit('update:modelValue', $event.target.value)">` }, ElDialog: { props: ['modelValue'], template: '<div v-if="modelValue"><slot /></div>' } } } })
}
beforeEach(() => { vi.clearAllMocks(); localStorage.clear(); vi.mocked(api.readProjectOpl).mockResolvedValue({ rows: [], people: [] }); vi.mocked(api.listProjectAudit).mockResolvedValue([]) })

describe('岗位工作台规则', () => {
  it('未审批计划不能形成逾期或进度，缺失数据不视为正常', () => {
    const entry = item(); entry.plan!.approvalStatus = 'Draft'; entry.planState = 'Draft'
    const actions = projectActions(entry, 'manager', today, 14, 'pm')
    expect(progress(entry)).toBeNull()
    expect(actions.some(action => action.title.includes('逾期'))).toBe(false)
    expect(actions.map(action => action.title)).toContain('计划未生效')
    expect(actions.map(action => action.title)).toContain('预算数据待补充')
  })
  it('工程师只收到本人任务，销售只收到里程碑，周期边界包含第14天', () => {
    const entry = item('root', [task(), task('other', today, '2026-10-12', 'other'), task('soon', '2026-10-19', '2026-10-20'), task('far', '2026-10-20', '2026-10-22')])
    expect(projectActions(entry, 'engineer', today, 14, 'engineer').map(action => action.taskId)).toEqual(['t1', 'soon'])
    expect(projectActions(entry, 'sales', today, 14, 'sales')).toHaveLength(0)
    entry.plan!.tasks[1]!.isMilestone = true
    expect(projectActions(entry, 'sales', today, 14, 'sales')[0]!.taskId).toBe('other')
  })
  it('采购待下单不因排期较远消失，生产按照近期阶段筛选，质量显示未通过', () => {
    const entry = item('future', [task('far', '2026-11-01', '2026-11-03')])
    expect(relevantProject(entry, 'production', '2026-10-19')).toBe(false)
    entry.procurement = { published: true, pendingOrder: 1, missing: 1, delayed: 0, refreshedAt: today, syncError: null }
    expect(relevantProject(entry, 'procurement', '2026-10-19')).toBe(true)
    entry.quality = { state: 'Effective', total: 2, pending: 1, failed: 1, records: 0 }
    expect(projectActions(entry, 'quality', today, 14, 'quality').some(action => action.title === '1 项验收未通过')).toBe(true)
  })
  it('多人分配识别重叠，跨周期与完成任务不计入，父子传播任务不重复判定', () => {
    const first = item('a', [task('a1', today, '2026-10-10', 'engineer、other')])
    const second = item('b', [task('b1', '2026-10-08', '2026-10-12', 'ENGINEER')])
    expect(resourceOverlaps([first, second], today, '2026-10-19').find(person => person.person === 'engineer')?.overlapCount).toBe(2)
    second.plan!.tasks[0]!.sourceTaskId = 'a1'
    expect(resourceOverlaps([first, second], today, '2026-10-19').every(person => person.overlapCount === 0)).toBe(true)
    second.plan!.tasks[0]!.status = 'Completed'
    expect(resourceOverlaps([second], today, '2026-10-19')).toHaveLength(0)
  })
})

describe('逐项目跟进工作台', () => {
  it('阶段和项目经理与事业部组合筛选，未生效计划不会误报进度阶段', async () => {
    const design=item('design'), assembly=item('assembly'), draft=item('draft'), missing=item('missing')
    design.project.executionUnitName='T2事业部';assembly.project.executionUnitName='T2事业部';draft.project.executionUnitName='T2事业部';missing.project.executionUnitName='T3事业部'
    assembly.plan!.currentStage='Assembly';assembly.plan!.tasks[0]!.stage='Assembly';assembly.project.primaryProjectManager='PM'
    draft.plan!.approvalStatus='Draft';draft.project.primaryProjectManager='boss'
    missing.plan=null;missing.project.primaryProjectManager=undefined
    vi.mocked(api.readProjectWorkbench).mockResolvedValue(data([design,assembly,draft,missing]))
    const wrapper=mountWorkbench();await flushPromises()
    expect(wrapper.findAll('.wr-project-second').map(row=>row.text())).toEqual(expect.arrayContaining([expect.stringContaining('设计'),expect.stringContaining('装配'),expect.stringContaining('计划未生效'),expect.stringContaining('未排程')]))
    expect(wrapper.find('.wr-project-second').text()).not.toContain('主设')
    await wrapper.get('[aria-label="筛选事业部"]').setValue('T2事业部')
    await wrapper.get('[aria-label="筛选阶段"]').setValue('设计')
    await wrapper.get('[aria-label="筛选项目经理"]').setValue('pm');await flushPromises()
    expect(wrapper.findAll('.wr-project-first b').map(code=>code.text())).toEqual(['P-design'])
    await wrapper.get('[aria-label="筛选阶段"]').setValue('装配');await flushPromises()
    expect(wrapper.findAll('.wr-project-first b').map(code=>code.text())).toEqual(['P-assembly'])
    await wrapper.get('[aria-label="筛选阶段"]').setValue('设计')
    await wrapper.get('[aria-label="筛选项目经理"]').setValue('boss');await flushPromises()
    expect(wrapper.findAll('.wr-project')).toHaveLength(0)
    await wrapper.get('[aria-label="筛选阶段"]').setValue('');await flushPromises()
    expect(wrapper.findAll('.wr-project-first b').map(code=>code.text())).toEqual(['P-draft'])
    await wrapper.get('[aria-label="筛选事业部"]').setValue('T3事业部')
    await wrapper.get('[aria-label="筛选项目经理"]').setValue('__unassigned');await flushPromises()
    expect(wrapper.findAll('.wr-project-first b').map(code=>code.text())).toEqual(['P-missing'])
    wrapper.unmount()
  })

  it('按立项时间从新到旧排序，缺失日期放最后，不按交付日期排序', async () => {
    const older=item('older',[task('old-task','2026-01-01','2026-01-02')])
    const newer=item('newer',[task('new-task','2027-01-01','2027-01-02')])
    const missing=item('missing')
    older.project.createdAt='2026-01-01T08:00:00Z'
    newer.project.createdAt='2026-10-12T08:00:00Z'
    older.project.signedDate='2099-01-01'
    newer.project.signedDate='2020-01-01'
    vi.mocked(api.readProjectWorkbench).mockResolvedValue(data([older,missing,newer]))
    const wrapper=mountWorkbench();await flushPromises()
    expect(wrapper.findAll('.wr-project-first b').map(code=>code.text())).toEqual(['P-newer','P-older','P-missing'])
    wrapper.unmount()
  })

  it('首屏不等待业务详情，不为46个项目读取完整OPL', async () => {
    const projects=Array.from({length:46},(_,i)=>({...item(String(i)),oplOpenCount:i}))
    let finish!: (value: WorkbenchData) => void
    vi.mocked(api.readProjectWorkbench).mockImplementation((_view,_token,_scope,options)=>options?.projectId?new Promise(resolve=>{finish=resolve}):Promise.resolve(data(projects)))
    const wrapper=mountWorkbench();await flushPromises()
    expect(wrapper.findAll('.wr-project')).toHaveLength(46)
    expect(wrapper.findAll('.wr-project')[1]!.text()).not.toContain('OPL')
    expect(wrapper.find('.wr-project-first time').exists()).toBe(false)
    expect(wrapper.find('.wr-project-second time').exists()).toBe(true)
    expect(api.readProjectOpl).toHaveBeenCalledTimes(1)
    expect(api.readProjectOpl).toHaveBeenCalledWith('0','token',false)
    expect(wrapper.text()).toContain('详情加载中')
    finish(data(projects));await flushPromises()
    expect(wrapper.text()).not.toContain('详情加载中')
    wrapper.unmount()
  })

  it('事业部按组织层级排序，进度只突出当前阶段', async () => {
    const first=item('t3'), second=item('t2'), childUnit=item('team')
    first.project.executionUnitName='T3事业部';second.project.executionUnitName='T2事业部';childUnit.project.executionUnitName='T2部门'
    first.plan!.currentStage='Assembly'
    first.plan!.stages=[{code:'Design',name:'设计'},{code:'Assembly',name:'装配'},{code:'Commissioning',name:'调试'}]
    first.plan!.tasks=[{...task('design'),status:'Completed'},{...task('assembly'),stage:'Assembly'},{...task('commissioning'),stage:'Commissioning'}]
    vi.mocked(api.readProjectWorkbench).mockResolvedValue(data([first,second,childUnit]))
    const wrapper=mountWorkbench();await flushPromises()
    await wrapper.setProps({organizationUnits:[
      {id:'t3',organizationId:'company',name:'T3事业部',code:'T3',kind:'BusinessDivision',sortOrder:20,isActive:true,canManufacture:true},
      {id:'dept',parentUnitId:'t2',organizationId:'company',name:'T2部门',code:'D',kind:'Department',sortOrder:99,isActive:true,canManufacture:true},
      {id:'t2',organizationId:'company',name:'T2事业部',code:'T2',kind:'BusinessDivision',sortOrder:10,isActive:true,canManufacture:true}
    ]})
    expect(wrapper.findAll('[aria-label="筛选事业部"] option').map(option=>option.text())).toEqual(['全部事业部','T2事业部','T2部门','T3事业部'])
    expect(wrapper.find('[aria-label="项目排序"]').exists()).toBe(false)
    expect(wrapper.find('input[type=checkbox]').exists()).toBe(false)
    await wrapper.get('[aria-label="选择项目 P-t3"]').trigger('click');await flushPromises()
    expect(wrapper.get('[aria-label="项目阶段进度"] [aria-current="step"]').text()).toBe('装配')
    expect(wrapper.findAll('.wr-phases .done').map(phase=>phase.text())).toEqual(['设计'])
    expect(wrapper.findAll('.wr-phases i')).toHaveLength(3)
    wrapper.unmount()
  })

  it('两行项目选择、公司范围切换与子项目跳转', async () => {
    const root=item(), other=item('second'), child=item('child'); child.project.parentProjectId='root'
    vi.mocked(api.readProjectWorkbench).mockResolvedValue({...data([root,other,child]),scopes:['personal','company']})
    const wrapper=mountWorkbench(); await flushPromises()
    expect(wrapper.findAll('.wr-project')).toHaveLength(2)
    expect(wrapper.get('.wr-project-second').text()).toContain('pm')
    await wrapper.get('[aria-label="下一项目"]').trigger('click'); await flushPromises()
    expect(wrapper.get('.wr-identity').text()).toContain('P-second')
    await wrapper.findAll('button').find(button=>button.text()==='公司')!.trigger('click'); await flushPromises()
    expect(api.readProjectWorkbench).toHaveBeenCalledWith('', 'token', 'company', {summaryOnly:true})
    await wrapper.get('[aria-label="选择项目 P-root"]').trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(button=>button.text()==='子项目总览')!.trigger('click')
    expect(wrapper.get('[aria-label="子项目总览"]').text()).toContain('P-child')
    await wrapper.get('[aria-label="子项目总览"] button').trigger('click')
    expect(wrapper.emitted('open')?.at(-1)).toEqual(['root','project-plan'])
    wrapper.unmount()
  })
  it('OPL按编号由新到旧排序，保存保留附件、版本和所属项目', async () => {
    const base={projectId:'root',number:1,description:'普通问题',priority:'普通',status:'待处理',owner:'engineer',dueDate:'2099-01-01',solution:'待复测',createdBy:'pm',createdAt:'',updatedBy:'pm',updatedAt:'',completedAt:null,rowVersion:7,attachments:[{id:'file',name:'图纸.pdf',length:1}],relatedProjectId:'child'}
    const rows=[{issue:{...base,id:'ordinary',number:3},canEdit:true,canComplete:true},{issue:{...base,id:'late',number:2,description:'逾期问题',dueDate:'2020-01-01'},canEdit:true,canComplete:true},{issue:{...base,id:'urgent',number:1,priority:'紧急',description:'紧急问题'},canEdit:true,canComplete:true}]
    vi.mocked(api.readProjectWorkbench).mockResolvedValue(data([item()]))
    vi.mocked(api.readProjectOpl).mockResolvedValue({rows,people:[]})
    const wrapper=mountWorkbench();await flushPromises()
    expect(wrapper.findAll('.wr-opl tbody tr').map(row=>row.text())).toEqual([expect.stringContaining('普通问题'),expect.stringContaining('逾期问题'),expect.stringContaining('紧急问题')])
    await wrapper.get('.wr-opl tbody tr:first-child td:last-child button').trigger('click')
    await wrapper.get('.wr-form').trigger('submit');await flushPromises()
    expect(api.saveProjectOpl).toHaveBeenCalledWith('root','ordinary',expect.objectContaining({expectedRowVersion:7,attachmentIds:['file'],relatedProjectId:'child'}),[],'token')
    wrapper.unmount()
  })
  it('记录下一步行动需要责任人和期限，成功后进入项目历史', async () => {
    vi.mocked(api.readProjectWorkbench).mockResolvedValue(data([{...item(),canMaintainRecord:true}]))
    const wrapper=mountWorkbench();await flushPromises()
    await wrapper.findAll('button').find(button=>button.text()==='记录跟进')!.trigger('click')
    const textareas=wrapper.findAll('.wr-form textarea')
    await textareas[0]!.setValue('例会结论');await textareas[1]!.setValue('确认到货')
    await wrapper.get('.wr-form').trigger('submit')
    expect(wrapper.text()).toContain('请明确负责人和完成期限')
    expect(api.addProjectManagerNote).not.toHaveBeenCalled()
    await wrapper.get('.wr-form select').setValue('engineer');await wrapper.get('.wr-form input[type=date]').setValue('2026-10-20')
    await wrapper.get('.wr-form').trigger('submit');await flushPromises()
    expect(api.addProjectManagerNote).toHaveBeenCalledWith('root',expect.stringContaining('完成期限：2026-10-20'),'token')
    wrapper.unmount()
  })
  it('旧项目记录的迟到响应不会覆盖新选中项目，失败提示为中文', async () => {
    let resolveOld!: (value: any[]) => void
    vi.mocked(api.readProjectWorkbench).mockResolvedValue(data([item(),item('second')]))
    vi.mocked(api.listProjectAudit).mockImplementation(id=>id==='root'?new Promise(resolve=>{resolveOld=resolve}):Promise.resolve([{id:'new',occurredAt:'2026-10-12',actor:'pm',action:'project.manager-note',entityType:'Project',entityId:'second',detail:'新项目结论'}]))
    const wrapper=mountWorkbench();await flushPromises()
    await wrapper.get('[aria-label="选择项目 P-second"]').trigger('click');await flushPromises()
    resolveOld([{id:'old',occurredAt:'2026-10-12',actor:'pm',action:'project.manager-note',entityType:'Project',entityId:'root',detail:'旧项目结论'}]);await flushPromises()
    expect(wrapper.get('[aria-label="跟进记录"]').text()).toContain('新项目结论')
    expect(wrapper.text()).not.toContain('旧项目结论')
    vi.mocked(api.readProjectWorkbench).mockRejectedValueOnce(new Error('Failed to fetch'))
    await wrapper.get('.wr-projects > header button').trigger('click');await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('项目总览读取失败')
    expect(wrapper.find('.wr-content').exists()).toBe(false)
    wrapper.unmount()
  })
})
