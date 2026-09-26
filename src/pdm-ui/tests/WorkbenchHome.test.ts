import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import WorkbenchHome from '../src/components/WorkbenchHome.vue'
import type { DocumentNode, ProjectSummary } from '../src/types'

const apiMocks = vi.hoisted(() => ({
  readProjectPlanPortfolio: vi.fn(),
  readProjectValidationPlan: vi.fn(),
  getMaterialRelationCompleteness: vi.fn(),
  getProjectProcurementTracking: vi.fn(),
  listProjectAudit: vi.fn(),
  addProjectManagerNote: vi.fn(),
  createProjectTodo: vi.fn(),
}))

vi.mock('../src/api', () => apiMocks)

const project = {
  id: 'project-root',
  code: 'P700001',
  name: '气密设备',
  owner: 'manager',
  stage: 'Design',
  vaultName: 'P700001',
  customerName: '宁波均普智能制造有限公司',
  executionUnitName: 'T1事业部',
  primaryProjectManager: 'manager',
  collaborativeProjectManagers: ['coordinator'],
  designLead: 'lead',
  designers: ['engineer'],
  vaultLocation: 'D:\\PDM\\Vault\\P700001',
  releaseLocation: 'D:\\PDM\\Release\\P700001',
  quantity: 1,
  serialNumbers: [],
  responsibleUsers: [],
  canAssignExecutionUnit: false,
  canManageMainStaffing: true,
  canAssignDesigners: true,
  canReadContent: true,
} satisfies ProjectSummary

const childProject = {
  id: 'project-child',
  parentProjectId: 'project-root',
  code: 'P700001-1',
  name: '机架',
  owner: 'manager',
  stage: 'Design',
  vaultName: 'P700001-1',
  vaultLocation: 'D:\\PDM\\Vault\\P700001-1',
  releaseLocation: 'D:\\PDM\\Release\\P700001-1',
  quantity: 1,
  serialNumbers: [],
  responsibleUsers: [],
  collaborativeProjectManagers: [],
  designers: ['engineer'],
  canAssignExecutionUnit: false,
  canManageMainStaffing: false,
  canAssignDesigners: true,
  canReadContent: true,
} satisfies ProjectSummary

const selected = {
  drawingNumber: '123',
  name: '123',
  fileName: '123.SLDASM',
  version: 'W18',
  configuration: '默认',
} as DocumentNode

describe('WorkbenchHome', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-25T08:00:00+08:00'))
    apiMocks.readProjectPlanPortfolio.mockResolvedValue({
      rootProjectId: 'project-root', currentStage: 'Design', completionPercent: 62, laggingProjectCount: 0, riskProjectCount: 0, plannedFinish: '2026-10-18',
      projects: [{ projectId: 'project-root', projectCode: 'P700001', projectName: '气密设备', isRoot: true, hasPlan: true, currentStage: 'Design', completionPercent: 62, plannedFinish: '2026-10-18', isLagging: false, isAtRisk: false,
        plan: { currentStage: 'Design', stages: [{ code: 'Design', name: '设计' }], tasks: [
          { id: 'task-1', name: '完成图纸审核', stage: 'Design', assignee: 'engineer', plannedStart: '2026-09-16', plannedFinish: '2026-09-18', status: 'InProgress', sortOrder: 1 },
          { id: 'task-2', name: '标准件BOM核对', stage: 'Design', assignee: 'lead', plannedStart: '2026-09-17', plannedFinish: '2026-09-19', status: 'NotStarted', sortOrder: 2 },
        ] } }],
    })
    apiMocks.readProjectValidationPlan.mockResolvedValue({ state: 'PendingApproval' })
    apiMocks.getMaterialRelationCompleteness.mockResolvedValue({ projectId: 'project-root', isComplete: false, mainMaterialCount: 2, incompleteGroupCount: 2, mainMaterials: [] })
    apiMocks.getProjectProcurementTracking.mockResolvedValue({ projectId: 'project-root', projectCode: 'P700001', hasPublishedBom: true, items: [
      { impactStage: 'Assembly', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购' },
      { impactStage: null, purchaseOrderNumbers: [], purchaseOrderStatus: '未采购' },
    ] })
    apiMocks.listProjectAudit.mockResolvedValue([
      { id: 'note-2', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'project-root', detail: '最新采购风险已同步', occurredAt: '2026-09-25T08:31:00Z' },
      { id: 'note-1', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'project-root', detail: '旧备注', occurredAt: '2026-09-25T08:30:00Z' },
      { id: 'child-note', actor: 'engineer', action: 'project.manager-note', entityType: 'Project', entityId: 'project-child', detail: '子项目装配准备完成', occurredAt: '2026-09-25T08:29:00Z' },
    ])
    apiMocks.addProjectManagerNote.mockResolvedValue({ id: 'note-3', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'project-root', detail: '风险已同步', occurredAt: '2026-09-25T08:32:00Z' })
    apiMocks.createProjectTodo.mockResolvedValue([])
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('以项目状态和下一步为核心，并进入对应业务页面', async () => {
    const wrapper = mount(WorkbenchHome, {
      props: {
        project,
        projects: [project, childProject],
        currentUsername: 'manager',
        users: [
          { username: 'manager', displayName: '项目经理甲', role: 'ProjectManager', isActive: true },
          { username: 'coordinator', displayName: '协同经理乙', role: 'ProjectManager', isActive: true },
          { username: 'lead', displayName: '主设丙', role: 'Engineer', isActive: true },
          { username: 'engineer', displayName: '工程师丁', role: 'Engineer', isActive: true },
        ],
        selected,
        hasDocuments: true,
        documentCount: 48,
        modelCount: 30,
        drawingCount: 18,
        warningCount: 0,
        bomPendingCount: 3,
        standardCount: 2,
        nonStandardCount: 1,
        electricalCount: 0,
        standardItemIds: ['standard-1', 'standard-2'],
        nonStandardItemIds: ['non-standard-1'],
        electricalItemIds: [],
        drawingReviews: [{
          id: 'review-1',
          projectId: 'project-1',
          number: 'DR-001',
          state: 'InReview',
          createdBy: 'engineer',
          createdAt: '2026-08-21T10:00:00Z',
          items: [{ modelState: 'Approved', drawingState: 'Marked' }, { modelState: 'Pending', drawingState: 'ChangesRequested' }],
          markups: [],
        } as never],
        materialApplications: [
          { status: 'Pending', bomItemId: 'standard-1' },
          { status: 'Approved', bomItemId: 'standard-2' },
          { status: 'Rejected', bomItemId: 'non-standard-1' },
        ] as never,
        releasePackages: [{
          state: '审批中', scope: 'StandardFormal', createdAt: '2026-08-21T10:00:00Z',
          steps: [{ id: 'step-1', stage: '主设审核', assignee: 'lead', status: 'current', detail: '待处理' }],
        }] as never,
        releasePackage: {
          state: '审批中',
          steps: [{ id: 'step-1', stage: '主设审核', assignee: 'lead', status: 'current', detail: '待处理' }],
        } as never,
        organizationDirectory: {
          organizations: [],
          units: [],
          memberships: [],
          managers: [],
          users: [],
        },
        pending: false,
        token: 'token',
        canAddManagerNote: true,
        onUpdateMainStaffing: async () => project,
        onUpdateDesigners: async () => project,
        onUpdatePhaseOwners: async () => project,
      },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.find('.pdm-project-overview-heading').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('当前工作图档')
    expect(wrapper.text()).not.toContain('项目尚未关联图纸')
    expect(wrapper.find('.pdm-current-document').exists()).toBe(false)
    expect(wrapper.find('.pdm-project-link-guide').exists()).toBe(false)
    expect(wrapper.find('.pdm-page-actions').exists()).toBe(false)
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('设计阶段 · 正常')
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('项目进度 62%')
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('计划完成 2026/10/18')
    expect(wrapper.get('[aria-label="五阶段计划"]').text()).toContain('设计')
    expect(wrapper.get('[aria-label="五阶段计划"]').text()).toContain('计划 2026/09/16 - 2026/09/19')
    const shippingCountdown = wrapper.get('[aria-label="发货倒计时"]')
    expect(shippingCountdown.text()).toContain('已超期6天')
    expect(shippingCountdown.text()).toContain('计划发货 2026/09/19')
    expect(wrapper.get('button[aria-label="查看待处理"]').text()).toContain('5')
    expect(wrapper.get('button[aria-label="查看关键物料"]').text()).toContain('1')
    expect(wrapper.get('[aria-label="图档与审核"]').text()).toContain('3D 30')
    expect(wrapper.get('[aria-label="图档与审核"]').text()).toContain('待审核 · 双审 2/4')
    expect(wrapper.get('[aria-label="BOM与物料"]').text()).toContain('标准件 2')
    expect(wrapper.get('[aria-label="BOM与物料"]').text()).toContain('待审 1 · 已批 1 · 退回 1')
    expect(wrapper.get('[aria-label="BOM与物料"]').text()).toContain('待核对 2')
    expect(wrapper.get('[aria-label="发布与备料"]').text()).toContain('关键物料1')
    expect(wrapper.get('[aria-label="发布与备料"]').text()).toContain('未采购2')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('P700001')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('设计')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('工程师丁')
    expect(wrapper.get('[aria-label="项目总览"]').findAll('thead th').map(cell => cell.text())).toEqual(['项目', '执行工程师', '当前阶段', '计划完成', '剩余工期', '进度', '阶段负责人', '当前子任务', '子任务状态', '备注日志'])
    const remainingWorkPeriod = wrapper.get('[aria-label="项目总览"]').find('td:nth-child(5) span')
    expect(remainingWorkPeriod.text()).toBe('逾期 6 天')
    const overdueTasks = wrapper.get('[aria-label="项目总览"]').find('td:nth-child(9) span')
    expect(overdueTasks.text()).toContain('完成图纸审核 延期 7 天')
    expect(overdueTasks.text()).toContain('标准件BOM核对 延期 6 天')
    expect(overdueTasks.attributes('title')).toContain('完成图纸审核')
    expect(overdueTasks.attributes('title')).toContain('2026/09/18')
    const currentStageFinish = wrapper.get('[aria-label="项目总览"]').find('td:nth-child(4)')
    expect(currentStageFinish.text()).toBe('2026/09/19')
    expect(currentStageFinish.attributes('title')).toContain('当前主任务“设计”')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('备注日志')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('最新采购风险已同步')
    expect(wrapper.get('[aria-label="项目总览"]').text()).not.toContain('旧备注')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('完成图纸审核')
    await wrapper.get('[aria-label="维护 P700001 的记录"]').trigger('click')
    await flushPromises()
    const history = document.body.querySelector('[aria-label="当前项目及子项目历史记录"]')
    expect(history?.textContent).toContain('P700001 · 气密设备')
    expect(history?.textContent).toContain('P700001-1 · 机架')
    expect(history?.textContent).toContain('子项目装配准备完成')
    const noteInput = document.body.querySelector('[aria-label="项目备注内容"]') as HTMLTextAreaElement
    expect(noteInput).not.toBeNull()
    noteInput.value = '风险已同步'
    noteInput.dispatchEvent(new Event('input'))
    ;[...document.body.querySelectorAll('button')].find(button => button.textContent === '保存记录')!.click()
    await flushPromises()
    expect(apiMocks.addProjectManagerNote).toHaveBeenCalledWith('project-root', '风险已同步', 'token')
    await wrapper.get('[aria-label="维护 P700001 的记录"]').trigger('click')
    await flushPromises()
    const todoContent = document.body.querySelector('[aria-label="项目备注内容"]') as HTMLTextAreaElement
    todoContent.value = '请确认客户现场准备情况'
    todoContent.dispatchEvent(new Event('input'))
    ;(document.body.querySelector('[aria-label="待办接收人"]') as HTMLElement).click()
    await flushPromises()
    ;[...document.body.querySelectorAll<HTMLElement>('.el-select-dropdown__item')].find(item => item.textContent?.includes('工程师丁'))!.click()
    await flushPromises()
    ;[...document.body.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent === '保存记录')!.click()
    await flushPromises()
    expect(apiMocks.addProjectManagerNote).toHaveBeenCalledWith('project-root', '请确认客户现场准备情况', 'token')
    expect(apiMocks.createProjectTodo).toHaveBeenCalledWith('project-root', {
      content: '请确认客户现场准备情况', dueDate: undefined, recipientUsernames: ['engineer'],
    }, 'token')
    expect([...document.body.querySelectorAll<HTMLButtonElement>('button')].some(button => button.textContent === '生成待办')).toBe(false)
    await wrapper.get('[aria-label="维护 P700001 的记录"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[aria-label="当前项目及子项目历史记录"]')?.textContent).toContain('待办推送')
    expect(document.body.querySelector('[aria-label="当前项目及子项目历史记录"]')?.textContent).toContain('工程师丁')
    ;[...document.body.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent === '取消')!.click()
    await flushPromises()
    expect(wrapper.emitted('records')).toBeUndefined()
    expect(wrapper.get('[aria-label="项目团队"]').text()).toContain('项目经理甲')
    expect(wrapper.get('[aria-label="项目团队"]').text()).toContain('主设丙')
    expect(wrapper.get('[aria-label="项目团队"]').text()).not.toContain('工程师丁')
    expect(wrapper.get('[aria-label="项目团队"]').text()).toContain('配置分工')
    expect(wrapper.get('[aria-label="项目团队"]').text()).toContain('配置负责人')
    const configureOwners = wrapper.get('[aria-label="项目团队"]').findAll('button').find(button => button.text() === '配置负责人')!
    await configureOwners.trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[aria-label="执行工程师"]')).not.toBeNull()
    for (const phase of ['标准件采购', '非标件采购', '非标件生产', '机械装配', '电气装配', '电气调试', '验收']) {
      expect(wrapper.get('[aria-label="项目团队"]').text()).toContain(phase)
    }
    expect(wrapper.findAll('.pdm-overview-team__table thead th').map(cell => cell.text())).toEqual(['职责', '负责人', '职责', '负责人'])
    expect(wrapper.findAll('.pdm-overview-team__table tbody tr')).toHaveLength(5)
    const assignedProject = { ...project, phaseOwners: { StandardProcurement: 'manager', Acceptance: 'coordinator' } }
    await wrapper.setProps({ projects: [assignedProject, childProject] })
    const standardOwnerRow = wrapper.findAll('.pdm-overview-team__table tbody tr').find(row => row.text().includes('标准件采购'))!
    expect(standardOwnerRow.text()).toContain('项目经理甲')
    expect(wrapper.get('[aria-label="项目团队"]').text()).toContain('验收协同经理乙')
    await wrapper.setProps({ projects: [{ ...assignedProject, phaseOwners: { ...assignedProject.phaseOwners, StandardProcurement: 'engineer' } }, childProject] })
    expect(standardOwnerRow.text()).toContain('工程师丁')
    expect(standardOwnerRow.text()).not.toContain('项目经理甲')
    expect(wrapper.find('[aria-label="当前阶段任务"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="项目位置"]').exists()).toBe(false)

    await wrapper.findAll('button').find(button => button.text().includes('配置负责人'))!.trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('配置项目阶段负责人 · P700001')
    for (const phase of ['标准件采购', '非标件采购', '非标件生产', '机械装配', '电气装配', '电气调试', '验收']) {
      expect(document.body.textContent).toContain(phase)
    }

    await wrapper.setProps({ projects: [{ ...project, collaborativeProjectManagers: [] }, childProject] })
    await wrapper.setProps({ project: { ...childProject, canAssignDesigners: false }, projects: [project, { ...childProject, canAssignDesigners: false }] })
    expect(wrapper.get('[aria-label="项目团队"]').text()).not.toContain('执行工程师')
    expect(wrapper.get('[aria-label="项目团队"]').text()).not.toContain('配置负责人')

    await wrapper.get('button[aria-label="进入项目图档"]').trigger('click')
    await wrapper.get('button[aria-label="进入BOM数据"]').trigger('click')
    await wrapper.findAll('button').find(button => button.text().includes('进入项目计划'))!.trigger('click')
    await wrapper.findAll('button').find(button => button.text().includes('查看备料'))!.trigger('click')
    await wrapper.findAll('button').find(button => button.text().includes('查看发布'))!.trigger('click')

    expect(wrapper.emitted('documents')).toEqual([[]])
    expect(wrapper.emitted('bom')).toEqual([[]])
    expect(wrapper.emitted('projectPlan')).toEqual([[]])
    expect(wrapper.emitted('procurement')).toEqual([[]])
    expect(wrapper.emitted('release')).toEqual([[]])
  })
})
