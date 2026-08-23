import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import WorkbenchHome from '../src/components/WorkbenchHome.vue'
import type { DocumentNode, ProjectSummary } from '../src/types'

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
  designers: [],
  vaultLocation: 'D:\\PDM\\Vault\\P700001',
  releaseLocation: 'D:\\PDM\\Release\\P700001',
  quantity: 1,
  serialNumbers: [],
  responsibleUsers: [],
  canAssignExecutionUnit: false,
  canManageMainStaffing: false,
  canAssignDesigners: false,
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
  canAssignDesigners: false,
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
  it('删除概览页头，并通过统计卡直接进入图档和BOM', async () => {
    const wrapper = mount(WorkbenchHome, {
      props: {
        project,
        projects: [project, childProject],
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
      },
    })

    expect(wrapper.find('.pdm-project-overview-heading').exists()).toBe(false)
    expect(wrapper.find('.pdm-page-actions').exists()).toBe(false)
    expect(wrapper.get('button[aria-label="进入项目图档"]').text()).toContain('图纸审核：审核中 · 双审 2/4')
    expect(wrapper.get('button[aria-label="进入BOM数据"]').text()).toContain('BOM审批：审批中 · 主设审核')
    expect(wrapper.get('button[aria-label="进入BOM数据"]').text()).toContain('物料申请：待审 1 · 已批 1 · 驳回 1')
    expect(wrapper.get('[aria-label="3D图档统计"]').text()).toContain('3D图档30审核中 1/2')
    expect(wrapper.get('[aria-label="2D图纸统计"]').text()).toContain('2D图纸18已退改 1')
    expect(wrapper.get('[aria-label="标准件BOM统计"]').text()).toContain('标准件2审批中 · 主设审核待审1 · 已批1')
    expect(wrapper.get('[aria-label="非标件BOM统计"]').text()).toContain('非标件1未发起驳回1')
    expect(wrapper.get('[aria-label="电气BOM统计"]').text()).toContain('电气0未发起暂无')
    expect(wrapper.get('[aria-label="人员组织结构"]').text()).toContain('人员组织结构')
    expect(wrapper.get('[aria-label="项目阶段负责人"]').text()).toContain('项目经理甲')
    expect(wrapper.get('[aria-label="项目阶段负责人"]').text()).toContain('协同经理乙')
    expect(wrapper.get('[aria-label="项目阶段负责人"]').text()).toContain('主设丙')
    expect(wrapper.get('[aria-label="项目阶段负责人"]').text()).toContain('工程师丁')
    expect(wrapper.get('[aria-label="客户联络人"]').text()).toContain('宁波均普智能制造有限公司')
    expect(wrapper.get('[aria-label="客户联络人"]').text()).toContain('待维护')

    await wrapper.get('button[aria-label="进入项目图档"]').trigger('click')
    await wrapper.get('button[aria-label="进入BOM数据"]').trigger('click')

    expect(wrapper.emitted('documents')).toEqual([[]])
    expect(wrapper.emitted('bom')).toEqual([[]])
  })
})
