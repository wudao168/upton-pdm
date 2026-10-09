import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '../src/api'
import ProjectWorkbench from '../src/components/ProjectWorkbench.vue'
import { projectActions, relevantProject, resourceOverlaps, progress } from '../src/projectWorkbench'
import type { WorkbenchData, WorkbenchProject, WorkbenchView } from '../src/projectWorkbench'
import type { ProjectPlanTask } from '../src/types'

vi.mock('../src/api', () => ({ readProjectWorkbench: vi.fn(), registerWorkbenchRisk: vi.fn(), closeWorkbenchRisk: vi.fn() }))
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
  return mount(ProjectWorkbench, { props: { users: [{ username: 'engineer', displayName: '工程师一', role: 'Engineer', isActive: true }], token: 'token', currentUsername: 'pm', companyId: 'company' }, global: { stubs: { ElDialog: true } } })
}
beforeEach(() => { vi.clearAllMocks(); localStorage.clear() })

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

describe('工作台交互', () => {
  it('展开子项目，任务跳转带任务ID，岗位变化重新读取并记忆', async () => {
    const root = item(), child = item('child', [task('child-task')]); child.project.parentProjectId = root.project.id
    vi.mocked(api.readProjectWorkbench).mockImplementation(async view => data([root, child], view === 'engineer' ? 'engineer' : 'manager'))
    const wrapper = mountWorkbench(); await flushPromises()
    expect(wrapper.get('[aria-label="项目关键指标"]').text()).toContain('关注项目1')
    expect(wrapper.get('[aria-label="项目总览"]').text()).not.toContain('P-child')
    await wrapper.get('[aria-label="展开子项目 P-root"]').trigger('click')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('P-child')
    await wrapper.get('[aria-label="当前待关注"] button.wb-action').trigger('click')
    expect(wrapper.emitted('open')?.[0]).toEqual(['root', 'project-plan', 't1'])
    await wrapper.get('[aria-label="岗位视角"]').setValue('engineer'); await flushPromises()
    expect(api.readProjectWorkbench).toHaveBeenLastCalledWith('engineer', 'token')
    expect(localStorage.getItem('pdm.workbench.company.pm')).toBe('engineer')
    expect(wrapper.find('[aria-label="项目总览"]').text()).not.toContain('预算 /')
  })
  it('读取失败清除旧数据，移除的角色回退到服务器默认视角', async () => {
    localStorage.setItem('pdm.workbench.company.pm', 'division')
    vi.mocked(api.readProjectWorkbench).mockRejectedValueOnce(Object.assign(new Error('禁止'), { status: 403 })).mockResolvedValueOnce(data([item()]))
    const wrapper = mountWorkbench(); await flushPromises()
    expect(api.readProjectWorkbench).toHaveBeenLastCalledWith('', 'token')
    vi.mocked(api.readProjectWorkbench).mockRejectedValueOnce(new Error('读取失败'))
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('读取失败')
    expect(wrapper.find('[aria-label="项目总览"]').exists()).toBe(false)
  })
})
