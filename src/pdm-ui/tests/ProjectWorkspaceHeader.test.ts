import { flushPromises, mount } from '@vue/test-utils'
import { ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectWorkspaceHeader from '../src/components/ProjectWorkspaceHeader.vue'
import type { ProjectSummary } from '../src/types'
import { userDisplayNameKey } from '../src/userDisplay'

const rootProject: ProjectSummary = {
  id: 'root-1',
  code: 'P700002',
  name: 'XXX设备',
  owner: 'engineer',
  stage: '进行中',
  vaultName: 'P700002',
  vaultLocation: 'D:\\PDM\\P700002',
  releaseLocation: 'D:\\Release\\P700002',
  customerName: '中山联合光电科技有限公司',
  executionUnitName: '自动化事业部',
  primaryProjectManager: 'project-manager',
  designLead: 'design-lead',
  deviceModel: 'AK-1-C00003-001-00',
  quantity: 1,
  serialNumbers: ['70000001'],
  responsibleUsers: ['engineer'],
  collaborativeProjectManagers: [],
  designers: [],
  documentCount: 3,
  modelDocumentCount: 2,
  drawingDocumentCount: 1,
  businessStatus: '已检出',
  rootDocumentCheckedOutBy: 'engineer',
  canAssignExecutionUnit: false,
  canManageMainStaffing: false,
  canAssignDesigners: false,
  canReadContent: true,
}

const childProject: ProjectSummary = {
  ...rootProject,
  id: 'child-1',
  code: 'P700002-1',
  name: '机架',
  parentProjectId: rootProject.id,
  childSequence: 1,
  deviceModel: 'AK-1-C00003-001-01',
  serialNumbers: ['70000002'],
  executionUnitName: undefined,
  primaryProjectManager: undefined,
  designLead: undefined,
  designers: ['child-engineer'],
  documentCount: 0,
  modelDocumentCount: 0,
  drawingDocumentCount: 0,
  businessStatus: '待审批',
  rootDocumentCheckedOutBy: undefined,
}

const drawingChild: ProjectSummary = {
  ...childProject,
  id: 'child-2',
  code: 'P700002-2',
  name: '工装',
  childSequence: 2,
  documentCount: 2,
  modelDocumentCount: 1,
  drawingDocumentCount: 1,
  businessStatus: '正常',
  rootDocumentCheckedOutBy: 'other-user',
  designers: ['drawing-engineer', 'child-engineer'],
}

const otherRootProject: ProjectSummary = {
  ...rootProject,
  id: 'root-2',
  code: 'P700003',
  name: '检测设备',
  customerName: '深圳测试客户有限公司',
  executionUnitName: '检测事业部',
  primaryProjectManager: 'manager-two',
  designLead: 'lead-two',
}

describe('ProjectWorkspaceHeader', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('选中子项目后左侧显示子项目型号和序列号，右侧不再重复显示项目摘要', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild], activeTab: 'overview', currentUsername: 'engineer' },
    })

    const sidebar = wrapper.get('[aria-label="项目基本信息与全部项目号"]')
    const context = sidebar.get('[aria-label="当前项目"]')
    expect(context.find('select[aria-label="切换项目"]').exists()).toBe(false)
    expect(context.find('input[aria-label="当前项目显示"]').exists()).toBe(false)
    expect(context.get('button[aria-label="浏览项目"]').text()).toBe('P700002 · XXX设备')
    expect(context.get('button[aria-label="浏览项目"]').attributes('aria-haspopup')).toBe('dialog')
    expect(context.find('.pdm-project-switcher__search-icon').exists()).toBe(true)
    expect(context.find('.pdm-project-context__breadcrumb').exists()).toBe(false)
    expect(context.text()).toBe('P700002 · XXX设备')
    expect(wrapper.find('.pdm-project-context').exists()).toBe(false)
    expect(sidebar.text()).not.toContain('项目基本信息')
    expect(sidebar.get('.pdm-project-sidebar__summary').find('small').text()).toBe('子项目')
    expect(sidebar.get('.pdm-project-sidebar__summary').findAll('dt').map(item => item.text())).toEqual(['状态', '型号', '序列号', '事业部', '项目经理', '主设', '工程师'])
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).toContain('P700002-1 · 机架')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).toContain('AK-1-C00003-001-01')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).toContain('70000002')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).toContain('自动化事业部')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).toContain('project-manager')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).toContain('design-lead')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).toContain('child-engineer')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).not.toContain('drawing-engineer')
    expect(sidebar.get('.pdm-project-sidebar__summary').text()).not.toContain('中山联合光电科技有限公司')
    expect(wrapper.findAll('.pdm-project-family__list button')).toHaveLength(3)
    expect(wrapper.get('[aria-label="选择项目号 P700002-1"]').classes()).toContain('is-active')
    expect(wrapper.findAll('.pdm-project-family__state i')).toHaveLength(0)
    expect(wrapper.find('.pdm-project-selected-summary').exists()).toBe(false)
    const projectTabs = wrapper.findAll('.pdm-project-tabs button')
    expect(projectTabs).toHaveLength(10)
    expect(projectTabs.map(button => button.text())).toEqual(['概览', '文件', '项目计划', '验证计划', '图档', 'BOM', '发布', '备料', '版本', '记录'])
    expect(wrapper.text()).not.toContain('图纸审核')
    expect(sidebar.get('[aria-label="选择项目号 P700002"] .pdm-project-family__state').text()).toBe('可编辑')
    expect(sidebar.get('[aria-label="选择项目号 P700002-1"] .pdm-project-family__state').text()).toBe('正常')
    expect(sidebar.get('[aria-label="选择项目号 P700002-2"] .pdm-project-family__state').text()).toBe('other-user编辑中')
    expect(sidebar.get('[aria-label="选择项目号 P700002"] .pdm-project-family__document-counts').text()).toBe('21')
    expect(sidebar.get('[aria-label="选择项目号 P700002"] .pdm-project-family__document-counts').attributes('aria-label')).toBe('3D图档 2，2D图档 1')
    expect(sidebar.get('[aria-label="选择项目号 P700002-1"] .pdm-project-family__document-counts').text()).toBe('00')
    expect(sidebar.get('[aria-label="选择项目号 P700002-1"] .is-model').attributes('title')).toBe('3D图档 0')
    expect(sidebar.get('[aria-label="选择项目号 P700002-1"] .is-drawing').attributes('title')).toBe('2D图档 0')
    expect(sidebar.get('.pdm-project-sidebar__overview').text()).toContain('0图档')
    expect(sidebar.text()).not.toContain('已检出')
  })

  it('当前项目使用设计树唯一图档数覆盖项目登记汇总数', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: {
        project: childProject,
        projects: [rootProject, childProject, drawingChild],
        activeTab: 'documents',
        activeDocumentCounts: { all: 41, model: 41, drawing: 0 },
      },
    })

    expect(wrapper.get('.pdm-project-sidebar__overview').text()).toContain('41图档')
    expect(wrapper.get('[aria-label="选择项目号 P700002-1"] .pdm-project-family__document-counts').text()).toBe('410')
    expect(wrapper.get('[aria-label="选择项目号 P700002"] .pdm-project-family__document-counts').text()).toBe('21')
  })

  it('默认选中主项目时左侧显示主项目管理信息', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: rootProject, projects: [rootProject, childProject, drawingChild], activeTab: 'overview', currentUsername: 'engineer' },
    })

    const summary = wrapper.get('.pdm-project-sidebar__summary')
    expect(summary.find('small').text()).toBe('主项目')
    expect(summary.text()).toContain('P700002 · XXX设备')
    expect(summary.findAll('dt').map(item => item.text())).toEqual(['状态', '型号', '序列号', '事业部', '项目经理', '主设', '工程师'])
    expect(summary.text()).toContain('AK-1-C00003-001-00')
    expect(summary.text()).toContain('70000001')
    expect(summary.text()).toContain('自动化事业部')
    expect(summary.text()).toContain('child-engineer、drawing-engineer')
    expect(wrapper.get('.pdm-project-sidebar__overview').text()).toContain('3图档')
    expect(wrapper.get('[aria-label="选择项目号 P700002"] .pdm-project-family__document-counts').text()).toBe('21')
  })

  it('图档数量跟随当前选中的子项目号', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: drawingChild, projects: [rootProject, childProject, drawingChild], activeTab: 'documents' },
    })

    expect(wrapper.get('.pdm-project-sidebar__overview').text()).toContain('2图档')
  })

  it('点击项目号切换右侧数据范围，功能选项卡保持独立', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild], activeTab: 'overview' },
    })

    await wrapper.get('[aria-label="选择项目号 P700002-2"]').trigger('click')
    await flushPromises()
    expect(confirm).not.toHaveBeenCalled()
    expect(wrapper.emitted('switch')).toEqual([[drawingChild.id]])

    const fileTab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text() === '文件')
    expect(fileTab).toBeDefined()
    await fileTab!.trigger('click')
    expect(wrapper.emitted('tab')).toEqual([['files']])
  })

  it('切换项目时显示进度并阻止重复点击', async () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: {
        project: rootProject,
        projects: [rootProject, childProject, drawingChild],
        activeTab: 'documents',
        switchingProjectId: childProject.id,
      },
    })

    const target = wrapper.get('[aria-label="选择项目号 P700002-1"]')
    expect(target.attributes('aria-busy')).toBe('true')
    expect(target.attributes()).toHaveProperty('disabled')
    expect(target.get('.pdm-project-family__state').text()).toBe('切换中…')
    expect(wrapper.get('button[aria-label="浏览项目"]').attributes()).toHaveProperty('disabled')

    await target.trigger('click')
    expect(wrapper.emitted('switch')).toBeUndefined()
  })

  it('浏览项目时支持搜索并在二次确认后切换', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild, otherRootProject], activeTab: 'overview' },
    })

    await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('浏览项目')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(2)
    expect(wrapper.findAll('[role="option"]').map(item => item.attributes('aria-label'))).toEqual(['选择浏览项目 P700003', '选择浏览项目 P700002'])
    expect(wrapper.get('[aria-label="项目搜索结果"]').text()).toContain('客户名称')
    expect(wrapper.get('[aria-label="项目搜索结果"]').text()).toContain('序列号')

    await wrapper.get('input[aria-label="搜索项目"]').setValue('检测')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(1)
    expect(wrapper.find('button[aria-label="选择浏览项目 P700002"]').exists()).toBe(false)
    expect(wrapper.get('button[aria-label="选择浏览项目 P700003"]').text()).toContain('检测设备')

    await wrapper.get('button[aria-label="选择浏览项目 P700003"]').trigger('click')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(
      '确定切换到项目“P700003 · 检测设备”吗？',
      '确认切换项目',
      { confirmButtonText: '确认切换', cancelButtonText: '取消', type: 'warning' },
    )
    expect(wrapper.emitted('switch')).toEqual([[otherRootProject.id]])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('浏览项目默认每页20行，并支持切换50、100行', async () => {
    const projects = Array.from({ length: 52 }, (_, index) => ({
      ...otherRootProject,
      id: `root-page-${index}`,
      code: `P${String(index + 1).padStart(6, '0')}`,
      name: `分页项目${index + 1}`,
    }))
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, ...projects], activeTab: 'overview' },
    })

    await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(20)
    expect(wrapper.get('[aria-label="项目列表分页"]').text()).toContain('共 53 条')
    await wrapper.get('select[aria-label="每页行数"]').setValue('50')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(50)
    await wrapper.get('button[aria-label="下一页"]').trigger('click')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(3)
    await wrapper.get('select[aria-label="每页行数"]').setValue('100')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(53)
  })

  it.each([
    ['客户', '客户筛选', '深圳测试客户有限公司'],
    ['事业部', '事业部筛选', '检测事业部'],
    ['项目经理', '人员筛选', 'manager-two'],
    ['主设', '人员筛选', 'lead-two'],
  ])('浏览项目可按%s下拉筛选', async (_, filterLabel, filterValue) => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild, otherRootProject], activeTab: 'overview' },
    })

    await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
    await wrapper.get(`select[aria-label="${filterLabel}"]`).setValue(filterValue)

    expect(wrapper.findAll('[role="option"]')).toHaveLength(1)
    expect(wrapper.get('button[aria-label="选择浏览项目 P700003"]').text()).toContain('检测设备')
  })

  it('取消项目选择或取消二次确认时保持当前项目', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue('cancel')
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild, otherRootProject], activeTab: 'overview' },
    })

    await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
    await wrapper.get('[role="dialog"] footer button').trigger('click')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.emitted('switch')).toBeUndefined()

    await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
    await wrapper.get('input[aria-label="搜索项目"]').setValue('P700003')
    await wrapper.get('button[aria-label="选择浏览项目 P700003"]').trigger('click')
    await flushPromises()

    expect(wrapper.emitted('switch')).toBeUndefined()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(true)
    expect(wrapper.get('button[aria-label="浏览项目"]').text()).toBe('P700002 · XXX设备')
  })

  it('图档页左侧只显示项目摘要和全部项目号', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild], activeTab: 'documents' },
      slots: { sidebar: '<section aria-label="BOM完整性">BOM完整性</section><section aria-label="当前发布包">当前发布包</section>' },
    })

    const sidebar = wrapper.get('[aria-label="项目基本信息与全部项目号"]')
    expect(sidebar.text()).not.toContain('BOM完整性')
    expect(sidebar.text()).not.toContain('当前发布包')
    expect(sidebar.find('.pdm-project-sidebar__aux').exists()).toBe(false)
  })

  it('当前项目以实时图档状态覆盖旧的项目检出汇总', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: rootProject, projects: [rootProject], activeTab: 'documents', activeProjectDocumentStatus: '正常' },
    })

    expect(wrapper.get('[aria-label="选择项目号 P700002"] .pdm-project-family__state').text()).toBe('正常')
    expect(wrapper.text()).not.toContain('已检出')
  })

  it('其他用户编辑图档时显示姓名而不是账号', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: rootProject, projects: [rootProject, drawingChild], activeTab: 'documents', currentUsername: 'engineer' },
      global: { provide: { [userDisplayNameKey as symbol]: (username?: string | null) => username === 'other-user' ? '王勇煌' : username || '—' } },
    })

    const state = wrapper.get('[aria-label="选择项目号 P700002-2"] .pdm-project-family__state')
    expect(state.text()).toBe('王勇煌编辑中')
    expect(state.text()).not.toContain('other-user')
  })

  it('统一引用结构统计后，子项目选中和切走均保持41个三维图档', async () => {
    const child = { ...childProject, documentCount: 41, modelDocumentCount: 41, drawingDocumentCount: 0 }
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: rootProject, projects: [rootProject, child], activeTab: 'overview', activeDocumentCounts: { all: 3, model: 2, drawing: 1 } },
    })
    const counts = () => wrapper.get('[aria-label="选择项目号 P700002-1"] .pdm-project-family__document-counts').attributes('aria-label')
    expect(counts()).toBe('3D图档 41，2D图档 0')
    await wrapper.setProps({ project: child, activeDocumentCounts: { all: 41, model: 41, drawing: 0 } })
    expect(counts()).toBe('3D图档 41，2D图档 0')
    await wrapper.setProps({ project: rootProject, activeDocumentCounts: { all: 3, model: 2, drawing: 1 } })
    expect(counts()).toBe('3D图档 41，2D图档 0')
    wrapper.unmount()
  })
})
