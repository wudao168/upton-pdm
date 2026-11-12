import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { computed, ref, type Ref } from 'vue'
import WorkbenchHome from '../src/components/WorkbenchHome.vue'
import { projectCenterAccessKey, projectCenterSchema } from '../src/projectCenterAccess'
import type { DocumentNode, ProjectSummary } from '../src/types'

const apiMocks = vi.hoisted(() => ({
  listProjectPlanVersions: vi.fn().mockResolvedValue([]),
  readProjectPlanPortfolio: vi.fn(),
  readProjectRiskOverview: vi.fn(),
  readProjectValidationPlan: vi.fn(),
  getMaterialRelationCompleteness: vi.fn(),
  getProjectProcurementTracking: vi.fn(),
  readProjectOverviewRecords: vi.fn(),
  readProjectOpl: vi.fn(),
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
  effectiveProjectPermissions: ['project.opl.view'],
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
  effectiveProjectPermissions: ['project.opl.view'],
} satisfies ProjectSummary

const selected = {
  drawingNumber: '123',
  name: '123',
  fileName: '123.SLDASM',
  version: 'W18',
  configuration: '默认',
} as DocumentNode

const oplRows = (count: number, relatedProjectId = project.id, status = '待处理') => Array.from({ length: count }, (_, index) => ({
  issue: { id: `${relatedProjectId}-${status}-${index}`, relatedProjectId, status },
}))

function mountOverview(overrides: Partial<InstanceType<typeof WorkbenchHome>['$props']> = {}, permissions?: Ref<string[]>) {
  return mount(WorkbenchHome, {
    props: {
      project, projects: [project, childProject], users: [], selected, hasDocuments: false,
      modelCount: 0, drawingCount: 0, bomPendingCount: 0, pendingBomItems: [], standardCount: 0,
      nonStandardCount: 0, electricalCount: 0, standardItemIds: [], nonStandardItemIds: [], electricalItemIds: [],
      drawingReviews: [], materialApplications: [], releasePackages: [], releasePackage: null,
      pending: false, token: 'token', canAddManagerNote: false, ...overrides,
    },
    global: { plugins: [ElementPlus], provide: permissions ? { [projectCenterAccessKey as symbol]: computed(() => permissions.value) } : {} },
  })
}

function detailRows(label: '待处理' | '审批中') {
  return [...document.body.querySelectorAll(`[aria-label="${label}事项明细"] tbody tr`)]
    .map(row => [...row.querySelectorAll('td')].map(cell => cell.textContent))
}

describe('WorkbenchHome', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers()
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false)
    vi.setSystemTime(new Date('2026-09-25T08:00:00+08:00'))
    apiMocks.readProjectRiskOverview.mockResolvedValue([])
    apiMocks.readProjectOpl.mockResolvedValue({ rows: [] })
    apiMocks.readProjectPlanPortfolio.mockResolvedValue({
      rootProjectId: 'project-root', currentStage: 'Design', completionPercent: 62, laggingProjectCount: 0, riskProjectCount: 0, plannedFinish: '2026-10-18',
      projects: [{ projectId: 'project-root', projectCode: 'P700001', projectName: '气密设备', isRoot: true, hasPlan: true, currentStage: 'Design', completionPercent: 62, plannedFinish: '2026-10-18', isLagging: false, isAtRisk: false,
        plan: { baselineVersion: 1, updatedAt: '2026-09-01', currentStage: 'Design', stages: [{ code: 'Design', name: '设计' }], tasks: [
          { id: 'task-1', name: '完成图纸审核', stage: 'Design', assignee: 'engineer', plannedStart: '2026-09-16', plannedFinish: '2026-09-18', baselineStart: '2026-09-16', baselineFinish: '2026-09-18', status: 'InProgress', sortOrder: 1 },
          { id: 'task-2', name: '标准件BOM核对', stage: 'Design', assignee: 'lead', plannedStart: '2026-09-17', plannedFinish: '2026-09-19', baselineStart: '2026-09-17', baselineFinish: '2026-09-19', status: 'NotStarted', sortOrder: 2 },
        ] } }],
    })
    apiMocks.readProjectValidationPlan.mockResolvedValue({ state: 'PendingApproval' })
    apiMocks.getMaterialRelationCompleteness.mockResolvedValue({ projectId: 'project-root', isComplete: false, mainMaterialCount: 2, incompleteGroupCount: 2, mainMaterials: [] })
    apiMocks.getProjectProcurementTracking.mockResolvedValue({ projectId: 'project-root', projectCode: 'P700001', hasPublishedBom: true, items: [
      { impactStage: 'Assembly', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购' },
      { impactStage: null, purchaseOrderNumbers: [], purchaseOrderStatus: '未采购' },
    ] })
    apiMocks.readProjectOverviewRecords.mockResolvedValue([
      { id: 'note-2', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'project-root', detail: '最新采购风险已同步', occurredAt: '2026-09-25T08:31:00Z' },
      { id: 'note-1', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'project-root', detail: '旧备注', occurredAt: '2026-09-25T08:30:00Z' },
      { id: 'child-note', actor: 'engineer', action: 'project.manager-note', entityType: 'Project', entityId: 'project-child', detail: '子项目装配准备完成', occurredAt: '2026-09-25T08:29:00Z' },
    ])
    apiMocks.addProjectManagerNote.mockResolvedValue({ id: 'note-3', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'project-root', detail: '风险已同步', occurredAt: '2026-09-25T08:32:00Z' })
    apiMocks.createProjectTodo.mockResolvedValue([])
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('待处理按事项类别计数，抽屉保留各类明细数量并打开对应BOM或OPL', async () => {
    apiMocks.getMaterialRelationCompleteness.mockResolvedValue({ incompleteGroupCount: 0 })
    apiMocks.readProjectOpl.mockResolvedValue({ rows: [
      ...oplRows(5), ...oplRows(2, project.id, '已完成'), ...oplRows(1, project.id, '已作废'),
    ] })
    const wrapper = mountOverview({
      bomPendingCount: 25,
      pendingBomItems: [
        ...Array.from({ length: 15 }, (_, index) => ({ id: `standard-${index}`, kind: 'Standard' })),
        ...Array.from({ length: 10 }, (_, index) => ({ id: `non-standard-${index}`, kind: 'NonStandard' })),
        { id: 'excluded', kind: 'Electrical', manuallyExcluded: true },
      ] as never,
    })
    await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('3')
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    expect(detailRows('待处理')).toEqual([
      ['标准件BOM', '待处理', '15', '查看明细'],
      ['非标件BOM', '待处理', '10', '查看明细'],
      ['OPL', '待处理', '5', '查看明细'],
    ])
    ;(document.body.querySelector('[aria-label="查看标准件BOM待处理明细"]') as HTMLButtonElement).click()
    await flushPromises()
    expect(wrapper.emitted('bom')).toEqual([['Standard']])
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    ;(document.body.querySelector('[aria-label="查看OPL待处理明细"]') as HTMLButtonElement).click()
    await flushPromises()
    expect(wrapper.emitted('opl')).toEqual([[]])
    wrapper.unmount()
  })

  it('待分类行即使已有物料类型也归入BOM分类，明细进入源结构分类入口', async () => {
    apiMocks.getMaterialRelationCompleteness.mockResolvedValue({ incompleteGroupCount: 0 })
    const wrapper = mountOverview({ pendingBomItems: [
      { id: 'standard', kind: 'Standard' }, { id: 'non-standard', kind: 'NonStandard' },
      { id: 'pending-standard', kind: 'Standard', pendingClassification: true },
      { id: 'pending-non-standard', kind: 'NonStandard', pendingClassification: true },
      { id: 'unknown', kind: 'Unclassified' },
    ] as never })
    await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('3')
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    expect(detailRows('待处理')).toEqual([
      ['标准件BOM', '待处理', '1', '查看明细'],
      ['非标件BOM', '待处理', '1', '查看明细'],
      ['BOM分类', '待处理', '3', '查看明细'],
    ])
    ;(document.body.querySelector('[aria-label="查看BOM分类待处理明细"]') as HTMLButtonElement).click()
    await flushPromises()
    expect(wrapper.emitted('bom')).toEqual([['Unclassified']])
    wrapper.unmount()
  })

  it('审批中按类别合并单据，包含工艺审核、待批准和主管审核，当前发布单不重复计算', async () => {
    apiMocks.readProjectValidationPlan.mockResolvedValue({ state: 'Draft' })
    const releases = [
      { id: 'standard-a', scope: 'StandardFormal', state: '工艺审核' },
      { id: 'standard-b', scope: 'StandardFormal', state: '待批准' },
      { id: 'non-standard', scope: 'NonStandardWithDrawing', state: '审批中' },
      { id: 'released', scope: 'ElectricalFormal', state: '已发布' },
    ]
    const wrapper = mountOverview({
      releasePackages: releases as never, releasePackage: releases[0] as never,
      drawingReviews: [
        { id: 'drawing-a', state: 'InReview' }, { id: 'drawing-b', state: 'PendingSupervisorApproval' },
        { id: 'drawing-c', state: 'WritingProperties' }, { id: 'drawing-d', state: 'Approved' },
      ] as never,
    })
    await flushPromises()
    expect(wrapper.get('[aria-label="查看审批中"] strong').text()).toBe('3')
    await wrapper.get('[aria-label="查看审批中"]').trigger('click'); await flushPromises()
    expect(detailRows('审批中')).toEqual([
      ['图档审核', '审批中', '3', '查看明细'],
      ['标准件BOM', '审批中', '2', '查看明细'],
      ['非标件BOM', '审批中', '1', '查看明细'],
    ])
    ;(document.body.querySelector('[aria-label="查看标准件BOM审批中明细"]') as HTMLButtonElement).click()
    await flushPromises()
    expect(wrapper.emitted('release')).toEqual([[]])
    wrapper.unmount()
  })

  it('没有未处理和审批事项时，两张卡片及抽屉均显示零和中文空态', async () => {
    apiMocks.getMaterialRelationCompleteness.mockResolvedValue({ incompleteGroupCount: 0 })
    apiMocks.readProjectValidationPlan.mockResolvedValue({ state: 'Approved' })
    const wrapper = mountOverview()
    await flushPromises()
    for (const label of ['待处理', '审批中'] as const) {
      expect(wrapper.get(`[aria-label="查看${label}"] strong`).text()).toBe('0')
      await wrapper.get(`[aria-label="查看${label}"]`).trigger('click'); await flushPromises()
      expect(detailRows(label)).toEqual([[`暂无${label}事项`]])
      ;([...document.body.querySelectorAll('button')].find(button => button.textContent === '关闭') as HTMLButtonElement).click()
      await flushPromises()
    }
    wrapper.unmount()
  })

  it('未授权不请求受限事项，授权后可见，动态撤销立即清空且刷新不再请求', async () => {
    const permissions = ref([projectCenterSchema, 'project.tab.overview.view'])
    apiMocks.readProjectOpl.mockResolvedValue({ rows: oplRows(5) })
    const wrapper = mountOverview({ pendingBomItems: [{ id: 'standard', kind: 'Standard' }] as never }, permissions)
    await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    expect(apiMocks.readProjectOpl).not.toHaveBeenCalled()
    expect(apiMocks.getMaterialRelationCompleteness).not.toHaveBeenCalled()
    expect(apiMocks.readProjectValidationPlan).not.toHaveBeenCalled()
    permissions.value.push('project.tab.bom.view', 'project.content.write', 'project.tab.opl.view', 'project.tab.validation-plan.view')
    await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('3')
    expect(wrapper.get('[aria-label="查看审批中"] strong').text()).toBe('1')
    expect(apiMocks.readProjectOpl).toHaveBeenCalledWith(project.id, 'token', false)
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    expect(detailRows('待处理').map(row => row[0])).toEqual(['标准件BOM', '关联物料', 'OPL'])
    permissions.value = [projectCenterSchema, 'project.tab.overview.view']
    await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    expect(wrapper.get('[aria-label="查看审批中"] strong').text()).toBe('0')
    vi.clearAllMocks()
    await wrapper.get('[aria-label="刷新项目概览"]').trigger('click'); await flushPromises()
    expect(apiMocks.readProjectOpl).not.toHaveBeenCalled()
    expect(apiMocks.getMaterialRelationCompleteness).not.toHaveBeenCalled()
    expect(apiMocks.readProjectValidationPlan).not.toHaveBeenCalled()
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    expect(detailRows('待处理')).toEqual([['暂无待处理事项']])
    wrapper.unmount()
  })

  it('保留OPL页签时仍须项目OPL权限，单独撤销立即隐藏且迟到响应不能回填或打开', async () => {
    const permissions = ref([projectCenterSchema, 'project.tab.overview.view', 'project.tab.opl.view'])
    const restricted = { ...project, effectiveProjectPermissions: [] }
    const wrapper = mountOverview({ project: restricted, projects: [restricted, childProject] }, permissions)
    await flushPromises()
    expect(apiMocks.readProjectOpl).not.toHaveBeenCalled()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    apiMocks.readProjectOpl.mockResolvedValue({ rows: oplRows(5) })
    await wrapper.setProps({ project, projects: [project, childProject] }); await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('1')
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    expect(detailRows('待处理')).toEqual([['OPL', '待处理', '5', '查看明细']])
    const oldButton = document.body.querySelector('[aria-label="查看OPL待处理明细"]') as HTMLButtonElement
    let finishOld!: (value: { rows: ReturnType<typeof oplRows> }) => void
    apiMocks.readProjectOpl.mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve }))
    await wrapper.get('[aria-label="刷新项目概览"]').trigger('click'); await flushPromises()
    await wrapper.setProps({ project: restricted, projects: [restricted, childProject] }); await flushPromises()
    expect(permissions.value).toContain('project.tab.opl.view')
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    oldButton.click(); await flushPromises()
    expect(wrapper.emitted('opl')).toBeUndefined()
    finishOld({ rows: oplRows(99) }); await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    apiMocks.readProjectOpl.mockClear()
    await wrapper.get('[aria-label="刷新项目概览"]').trigger('click'); await flushPromises()
    expect(apiMocks.readProjectOpl).not.toHaveBeenCalled()
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    expect(detailRows('待处理')).toEqual([['暂无待处理事项']])
    wrapper.unmount()
  })

  it('切换项目清空旧OPL并忽略迟到响应，子项目只统计关联到自己的未完成问题', async () => {
    apiMocks.getMaterialRelationCompleteness.mockResolvedValue({ incompleteGroupCount: 0 })
    let finishOld!: (value: { rows: ReturnType<typeof oplRows> }) => void
    let finishChild!: (value: { rows: ReturnType<typeof oplRows> }) => void
    apiMocks.readProjectOpl.mockImplementation((id: string) => id === project.id
      ? new Promise(resolve => { finishOld = resolve })
      : new Promise(resolve => { finishChild = resolve }))
      .mockResolvedValueOnce({ rows: oplRows(5) })
    const wrapper = mountOverview()
    await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('1')
    await wrapper.get('[aria-label="刷新项目概览"]').trigger('click'); await flushPromises()
    await wrapper.setProps({ project: childProject }); await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    finishOld({ rows: oplRows(99) }); await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    finishChild({ rows: [...oplRows(5), ...oplRows(2, childProject.id), ...oplRows(1, childProject.id, '已完成')] })
    await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('1')
    await wrapper.get('[aria-label="查看待处理"]').trigger('click'); await flushPromises()
    expect(detailRows('待处理')).toEqual([['OPL', '待处理', '2', '查看明细']])
    await wrapper.setProps({ token: '' }); await flushPromises()
    expect(wrapper.get('[aria-label="查看待处理"] strong').text()).toBe('0')
    wrapper.unmount()
  })

  it('仅可见项目的普通人员可查看记录，计划不可读也不影响，维护和待办表单保持关闭', async () => {
    vi.clearAllMocks()
    apiMocks.readProjectPlanPortfolio.mockRejectedValue(new Error('计划无查看权限'))
    const note={id:'readable',actor:'another-user',action:'project.manager-note',entityType:'Project',entityId:project.id,detail:'其他人员记录的项目情况',occurredAt:'2026-09-25T08:30:00Z'}
    apiMocks.readProjectOverviewRecords.mockResolvedValue([note,{...note,id:'child-record',entityId:childProject.id,detail:'可见子项目的历史记录'},{...note,id:'unrelated-record',entityId:'another-root',detail:'其他项目不应显示'}])
    const wrapper=mount(WorkbenchHome,{props:{project:{...project,canReadContent:false},projects:[project,childProject,{...project,id:'another-root',code:'OTHER'}],users:[],selected,hasDocuments:false,modelCount:0,drawingCount:0,bomPendingCount:0,standardCount:0,nonStandardCount:0,electricalCount:0,standardItemIds:[],nonStandardItemIds:[],electricalItemIds:[],drawingReviews:[],materialApplications:[],releasePackages:[],releasePackage:null,pending:false,token:'token',canAddManagerNote:false},global:{plugins:[ElementPlus]}})
    await flushPromises();await wrapper.findAll('button').find(b=>b.text()==='查看记录')!.trigger('click');await flushPromises()
    const history=document.body.querySelector('[aria-label="当前项目及子项目历史记录"]')!
    expect(history.textContent).toContain('其他人员记录的项目情况');expect(history.textContent).toContain('可见子项目的历史记录');expect(history.textContent).not.toContain('其他项目不应显示');expect(history.querySelectorAll('article')).toHaveLength(2)
    expect(document.body.querySelector('[aria-label="项目备注内容"]')).toBeNull();expect(document.body.querySelector('[aria-label="待办接收人"]')).toBeNull();expect([...document.body.querySelectorAll('button')].some(b=>b.textContent==='保存记录')).toBe(false)
    expect(apiMocks.readProjectOverviewRecords).toHaveBeenCalledWith(project.id,'token');expect(apiMocks.readProjectOverviewRecords).toHaveBeenCalledTimes(1);expect(apiMocks.readProjectOverviewRecords).not.toHaveBeenCalledWith('another-root','token');expect(apiMocks.addProjectManagerNote).not.toHaveBeenCalled();expect(apiMocks.createProjectTodo).not.toHaveBeenCalled();wrapper.unmount()
  })

  it('切换项目会关闭原记录并清空内容，旧请求迟到不会覆盖新项目记录', async () => {
    apiMocks.readProjectPlanPortfolio.mockRejectedValue(new Error('计划不可见'))
    let finishOld!: (value:any[])=>void;const old=new Promise<any[]>(resolve=>{finishOld=resolve})
    apiMocks.readProjectOverviewRecords.mockImplementation((id:string)=>id===project.id?old:Promise.resolve(id==='new-project'?[{id:'new-record',actor:'manager',action:'project.manager-note',entityType:'Project',entityId:id,detail:'新项目可见记录',occurredAt:'2026-09-25T08:31:00Z'}]:[]))
    const wrapper=mount(WorkbenchHome,{props:{project,projects:[project,childProject],users:[],selected,hasDocuments:false,modelCount:0,drawingCount:0,bomPendingCount:0,standardCount:0,nonStandardCount:0,electricalCount:0,standardItemIds:[],nonStandardItemIds:[],electricalItemIds:[],drawingReviews:[],materialApplications:[],releasePackages:[],releasePackage:null,pending:false,token:'token',canAddManagerNote:false},global:{plugins:[ElementPlus]}})
    await flushPromises();const next={...project,id:'new-project',code:'NEW'};await wrapper.setProps({project:next,projects:[next]});await flushPromises();await wrapper.findAll('button').find(b=>b.text()==='查看记录')!.trigger('click');await flushPromises()
    finishOld([{id:'old-record',actor:'manager',action:'project.manager-note',entityType:'Project',entityId:project.id,detail:'旧项目迟到内容',occurredAt:'2026-09-25T08:31:00Z'}]);await flushPromises()
    const history=document.body.querySelector('[aria-label="当前项目及子项目历史记录"]')!;expect(history.textContent).toContain('新项目可见记录');expect(history.textContent).not.toContain('旧项目迟到内容')
    await wrapper.setProps({token:''});await flushPromises();expect(history.textContent).not.toContain('新项目可见记录');wrapper.unmount()
  })

  it('记录未返回时先展示计划，记录失败不覆盖计划，隐藏页不轮询', async () => {
    vi.clearAllMocks()
    let rejectRecords!: (reason: Error) => void
    apiMocks.readProjectOverviewRecords.mockImplementation(() => new Promise((_, reject) => { rejectRecords = reject }))
    const wrapper = mount(WorkbenchHome, { props: { project, projects: [project, childProject], users: [], selected, hasDocuments: false, modelCount: 0, drawingCount: 0, bomPendingCount: 0, standardCount: 0, nonStandardCount: 0, electricalCount: 0, standardItemIds: [], nonStandardItemIds: [], electricalItemIds: [], drawingReviews: [], materialApplications: [], releasePackages: [], releasePackage: null, pending: false, token: 'token', canAddManagerNote: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('项目进度 62%')
    expect(apiMocks.readProjectOverviewRecords).toHaveBeenCalledTimes(1)
    rejectRecords(new Error('读取失败')); await flushPromises()
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('项目进度 62%')
    await wrapper.setProps({ active: false }); await vi.advanceTimersByTimeAsync(300001)
    expect(apiMocks.readProjectOverviewRecords).toHaveBeenCalledTimes(1)
    wrapper.unmount()
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
          { username: 'lead', displayName: '主设计丙', role: 'Engineer', isActive: true },
          { username: 'engineer', displayName: '工程师丁', role: 'Engineer', isActive: true },
        ],
        selected,
        hasDocuments: true,
        modelCount: 30,
        drawingCount: 18,
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
          steps: [{ id: 'step-1', stage: '主设计审核', assignee: 'lead', status: 'current', detail: '待处理' }],
        }] as never,
        releasePackage: {
          state: '审批中',
          steps: [{ id: 'step-1', stage: '主设计审核', assignee: 'lead', status: 'current', detail: '待处理' }],
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
        onUpdateMainStaffing: vi.fn(async () => project),
        onUpdateDesigners: vi.fn(async () => project),
        onUpdatePhaseOwners: vi.fn(async () => project),
      },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const initialRefreshCount = apiMocks.readProjectPlanPortfolio.mock.calls.length
    expect(document.hidden).toBe(false)
    expect(wrapper.get('[aria-label="刷新项目概览"]').attributes('disabled')).toBeUndefined()
    await vi.advanceTimersByTimeAsync(299_999)
    expect(apiMocks.readProjectPlanPortfolio).toHaveBeenCalledTimes(initialRefreshCount)
    await vi.advanceTimersByTimeAsync(1)
    await flushPromises()
    expect(apiMocks.readProjectPlanPortfolio).toHaveBeenCalledTimes(initialRefreshCount + 1)
    let finishBaselineRefresh!: (value: never[]) => void
    apiMocks.listProjectPlanVersions.mockImplementationOnce(() => new Promise(resolve => { finishBaselineRefresh = resolve }))
    await wrapper.get('[aria-label="刷新项目概览"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="初始基线"]').find('.pdm-overview-timeline__segment').exists()).toBe(true)
    finishBaselineRefresh([])
    await flushPromises()
    expect(apiMocks.readProjectPlanPortfolio).toHaveBeenCalledTimes(initialRefreshCount + 2)
    expect(wrapper.find('.pdm-project-overview-heading').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('当前工作图档')
    expect(wrapper.text()).not.toContain('项目尚未关联图纸')
    expect(wrapper.find('.pdm-current-document').exists()).toBe(false)
    expect(wrapper.find('.pdm-project-link-guide').exists()).toBe(false)
    expect(wrapper.find('.pdm-page-actions').exists()).toBe(false)
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('设计阶段 · 正常')
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('项目进度 62%')
    expect(wrapper.get('[aria-label="项目状态与下一步"]').text()).toContain('计划完成 2026/10/18')
    expect(wrapper.get('[aria-label="三行项目进度"]').text()).toContain('设计')
    expect(wrapper.get('[aria-label="当前计划"]').find('button').attributes('title')).toContain('2026-09-16 — 2026-09-19')
    const shippingCountdown = wrapper.get('[aria-label="发货倒计时"]')
    expect(shippingCountdown.text()).toContain('已超期6天')
    expect(shippingCountdown.text()).toContain('2026/09/19')
    expect(shippingCountdown.text()).not.toContain('计划发货')
    expect(wrapper.get('button[aria-label="查看待处理"] strong').text()).toBe('2')
    expect(wrapper.find('button[aria-label="查看关键物料"]').exists()).toBe(false)
    expect(wrapper.get('[aria-label="项目待办与风险"]').find('.pdm-overview-alerts').element.children).toHaveLength(3)
    expect(wrapper.get('[aria-label="项目核心业务概览"]').find('[aria-label="项目风险"]').exists()).toBe(true)
    expect(wrapper.get('[aria-label="项目核心业务概览"]').findAll('article')).toHaveLength(1)
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('P700001')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('设计')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('工程师丁')
    expect(wrapper.get('[aria-label="项目总览"]').findAll('thead th').map(cell => cell.text())).toEqual(['项目', '执行', '阶段', '计划完成', '剩余工期', '进度', '负责人', '当前', '状态', '备注'])
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
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('备注')
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
    const riskPanel = wrapper.get('[aria-label="项目风险"]')
    expect(riskPanel.text()).toContain('当前暂无风险')
    expect(wrapper.find('[aria-label="项目团队"]').exists()).toBe(false)
    const rootRisk = { project: { id: project.id, code: project.code, deliveryDate: '2026-09-22' }, plan: null, procurement: null, releases: [], incomplete: false }
    const childRisk = {
      project: { id: childProject.id, code: childProject.code }, plan: null, releases: [], incomplete: false,
      procurement: { delayed: 2, items: [
        { sequence: 1, materialCode: 'M001', materialName: '导轨', requiredOn: '2026-09-22', expectedOn: '2026-09-28', purchaseOrders: ['PO001'] },
        { sequence: 2, materialCode: 'M002', materialName: '丝杆', requiredOn: '2026-09-23', expectedOn: '2026-09-30', purchaseOrders: ['PO002'] },
      ] },
    }
    apiMocks.readProjectRiskOverview.mockResolvedValueOnce([rootRisk, childRisk])
    await wrapper.get('[aria-label="刷新项目概览"]').trigger('click')
    await flushPromises()
    expect(riskPanel.text()).toContain('项目交期已逾期 3 天')
    expect(riskPanel.findAll('tbody tr')).toHaveLength(3)
    await riskPanel.get('[aria-label="查看 P700001-1 采购交付延期 M001 · 导轨"]').trigger('click')
    await flushPromises()
    const riskDetail = document.body.querySelector('[aria-label="风险明细"]')!
    expect(riskDetail.textContent).toContain('PO001')
    expect(riskDetail.textContent).not.toContain('PO002')
    expect(wrapper.emitted('openRisk')).toBeUndefined()
    ;[...document.body.querySelectorAll('button')].find(button => button.textContent === '前往备料')!.click()
    await flushPromises()
    expect(wrapper.emitted('openRisk')).toEqual([[childProject.id, 'procurement']])

    apiMocks.readProjectRiskOverview.mockResolvedValueOnce([childRisk])
    await wrapper.setProps({ project: childProject })
    await flushPromises()
    expect(riskPanel.text()).not.toContain('项目交期')
    expect(riskPanel.findAll('tbody tr')).toHaveLength(2)

    apiMocks.readProjectRiskOverview.mockRejectedValueOnce(new Error('读取失败'))
    await wrapper.get('[aria-label="刷新项目概览"]').trigger('click')
    await flushPromises()
    expect(riskPanel.text()).toContain('项目风险读取失败')
    expect(riskPanel.text()).not.toContain('当前暂无风险')
    await riskPanel.get('button').trigger('click')
    await flushPromises()
    expect(riskPanel.text()).toContain('当前暂无风险')

    await wrapper.findAll('button').find(button => button.text().includes('进入项目计划'))!.trigger('click')

    expect(wrapper.emitted('projectPlan')).toEqual([[]])
  })
})
