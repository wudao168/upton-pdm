import { computed } from 'vue'
import { projectCenterAccessKey, projectCenterSchema } from '../src/projectCenterAccess'
import { flushPromises, mount } from '@vue/test-utils'
import { ElMessageBox, ElOption, ElSelect } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectWorkspaceHeader from '../src/components/ProjectWorkspaceHeader.vue'
import type { ProjectSummary, ReleasePackageSummary } from '../src/types'
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

const browseCustomers = [
  { id: 'customer-2', code: 'C002', name: otherRootProject.customerName!, isActive: true },
  { id: 'customer-3', code: 'C003', name: '暂无项目的客户', isActive: true },
]
const browseDivision = { id: 'division-2', organizationId: 'company', code: 'D002', name: otherRootProject.executionUnitName!, kind: 'BusinessDivision' as const, canManufacture: true, isActive: true, sortOrder: 0 }
const browseUnits = [
  browseDivision,
  { ...browseDivision, id: 'division-3', code: 'D003', name: '暂无项目的事业部', canManufacture: false },
  { ...browseDivision, id: 'other-company-division', organizationId: 'other-company', name: '其他公司的事业部' },
  { ...browseDivision, id: 'team', name: '项目组', kind: 'Team' as const },
]

describe('ProjectWorkspaceHeader', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('转图排队不显示异常点，失败持续显示橙色标识且恢复后移除', async () => {
    const release = { scope:'NonStandardSupplement', locksDocuments:true, state:'已发布', previewState:'Pending' } as ReleasePackageSummary
    const wrapper = mount(ProjectWorkspaceHeader, { props:{ project:rootProject, projects:[rootProject], activeTab:'overview', releasePackages:[release] } })
    try {
      const tab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text() === '发布')!
      expect(tab.attributes('data-release-attention')).toBeUndefined()
      await wrapper.setProps({ releasePackages:[{ ...release, previewState:'Failed' }] })
      expect(tab.attributes('data-release-attention')).toBe('error')
      expect(tab.findAll('.pdm-release-attention-border')).toHaveLength(1)
      expect(tab.find('.pdm-tab-update-dot').exists()).toBe(false)
      await wrapper.setProps({ releasePackages:[{ ...release, previewState:'Succeeded' }] })
      expect(tab.attributes('data-release-attention')).toBeUndefined()
    } finally { wrapper.unmount() }
  })

  it('项目号和发布页签持续提示待供应链接收的非标包，接收完成后同时停止', async () => {
    vi.useFakeTimers()
    const current = { ...rootProject, hasUnreceivedNonStandardRelease: true }
    const wrapper = mount(ProjectWorkspaceHeader, { props: { project: current, projects: [current], activeTab: 'release' } })
    try {
      const tab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text() === '发布')!
      const row = wrapper.get('[aria-label="选择项目号 P700002"]')
      expect(tab.attributes('data-release-attention')).toBe('new')
      expect(row.attributes('data-release-attention')).toBe('new')
      expect(tab.attributes('title')).toContain('待供应链接收')
      vi.setSystemTime(Date.now() + 2 * 86400000)
      await vi.advanceTimersByTimeAsync(60000)
      expect(tab.attributes('data-release-attention')).toBe('new')
      const received = { ...current, hasUnreceivedNonStandardRelease: false }
      await wrapper.setProps({ project: received, projects: [received] })
      expect(tab.attributes('data-release-attention')).toBeUndefined()
      expect(row.attributes('data-release-attention')).toBeUndefined()
    } finally { wrapper.unmount(); vi.useRealTimers() }
  })

  it('非标转图未完成时发布页签持续闪烁，查看不清除，成功后停止', async () => {
    const release = { scope: 'NonStandardSupplement', locksDocuments: true, state: '已发布', previewState: 'Failed', previewAttempts: 5 } as ReleasePackageSummary
    const wrapper = mount(ProjectWorkspaceHeader, { props: { project: rootProject, projects: [rootProject], activeTab: 'overview', releasePackages: [release] } })
    const tab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text() === '发布')!
    expect(tab.attributes('data-release-attention')).toBe('error')
    expect(tab.attributes('title')).toContain('发布／转图失败 1 项')
    await tab.trigger('click')
    await wrapper.setProps({ activeTab: 'release' })
    expect(tab.attributes('data-release-attention')).toBe('error')
    expect(tab.classes()).toContain('is-active')
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Succeeded' }] })
    expect(tab.attributes('data-release-attention')).toBeUndefined()
    await wrapper.setProps({ releasePackages: [{ ...release, scope: 'StandardFormal' }, { ...release, scope: 'ElectricalFormal' }] })
    expect(tab.attributes('data-release-attention')).toBeUndefined()
    wrapper.unmount()
  })

  it('仅汇总的转图未完成标识不算失败，确认失败后列表显示异常，新发布提示保留24小时', async () => {
    const now = Date.now()
    vi.useFakeTimers(); vi.setSystemTime(now)
    const child = { ...childProject, hasReleaseAttention: true, latestPurchasedBomPublishedAt: new Date(now - 1000).toISOString() }
    const root = { ...rootProject, latestPurchasedBomPublishedAt: new Date(now - 24 * 3600000 + 30000).toISOString() }
    const wrapper = mount(ProjectWorkspaceHeader, { props: { project: root, projects: [root, child], activeTab: 'overview' } })
    try {
      const childButton = wrapper.get('[aria-label="选择项目号 P700002-1"]')
      expect(childButton.attributes('data-release-attention')).toBe('new')
      const tab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text() === '发布')!
      expect(tab.attributes('data-release-attention')).toBe('new')
      expect(tab.find('.pdm-release-attention-border.is-new').exists()).toBe(true)
      await vi.advanceTimersByTimeAsync(60000)
      expect(tab.attributes('data-release-attention')).toBeUndefined()
      expect(childButton.attributes('data-release-attention')).toBe('new')
      await wrapper.setProps({ project: child, releasePreviewIssues: { [child.id]: true } })
      expect(tab.attributes('data-release-attention')).toBe('error')
      await wrapper.setProps({ project: root })
      expect(childButton.attributes('data-release-attention')).toBe('error')
    } finally { wrapper.unmount(); vi.useRealTimers() }
  })

  it('选中子项目后左侧显示子项目型号和序列号，右侧不再重复显示项目摘要', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild], activeTab: 'overview', currentUsername: 'engineer' },
    })

    const sidebar = wrapper.get('[aria-label="项目基本信息与全部项目号"]')
    const context = sidebar.get('[aria-label="当前项目"]')
    expect(context.find('select[aria-label="切换项目"]').exists()).toBe(false)
    expect(context.find('input[aria-label="当前项目显示"]').exists()).toBe(false)
    expect(context.get('input[aria-label="快速搜索项目"]').attributes('placeholder')).toBe('P700002 · XXX设备')
    expect(context.get('button[aria-label="浏览项目"]').attributes('aria-haspopup')).toBe('dialog')
    expect(context.find('.pdm-project-switcher__search-icon').exists()).toBe(true)
    expect(context.find('.pdm-project-context__breadcrumb').exists()).toBe(false)
    expect(context.get('input[aria-label="快速搜索项目"]').element).toHaveProperty('value', '')
    expect(wrapper.find('.pdm-project-context').exists()).toBe(false)
    expect(sidebar.text()).not.toContain('项目基本信息')
    expect(sidebar.get('.pdm-project-sidebar__summary').find('small').text()).toBe('子项目')
    expect(sidebar.get('.pdm-project-sidebar__summary').findAll('dt').map(item => item.text())).toEqual(['状态', '型号', '序列号', '事业部', '项目经理', '主设计', '设计人员'])
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
    expect(projectTabs).toHaveLength(13)
    expect(projectTabs.map(button => button.text())).toEqual(['概览', '信息', '文件', '计划', '预算', '质量', 'OPL', '图档', 'BOM', '发布', '备料', '记录', '设置'])
    expect(projectTabs.at(-1)?.attributes('aria-label')).toBe('项目设置')
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
    expect(summary.findAll('dt').map(item => item.text())).toEqual(['状态', '型号', '序列号', '事业部', '项目经理', '主设计', '设计人员'])
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
    expect(wrapper.emitted('tab')).toEqual([['files', false]])
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

  it('浏览项目搜索后直接切换，不再二次确认', async () => {
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

    expect(confirm).not.toHaveBeenCalled()
    expect(wrapper.emitted('switch')).toEqual([[otherRootProject.id]])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })
  it('快速搜索可匹配项目资料，输入不切换，选择候选后直接切换且不打开抽屉', async () => {
    const target = { ...otherRootProject, deviceModel: 'CHECK-200', serialNumbers: ['80000003'], projectAlias: '机器视觉' }
    const wrapper = mount(ProjectWorkspaceHeader, { props: { project: childProject, projects: [rootProject, childProject, target], activeTab: 'documents' } })
    const confirm = vi.spyOn(ElMessageBox, 'confirm')
    try {
      const autocomplete = wrapper.getComponent({ name: 'ElAutocomplete' })
      const suggestions = autocomplete.props('fetchSuggestions') as (query: string, callback: (items: Array<{ id: string }>) => void) => void
      for (const query of ['p700003', '检测', '深圳测试', 'check-200', '80000003', '机器视觉']) {
        const result = vi.fn()
        suggestions(query, result)
        expect(result.mock.calls[0]?.[0].map((item: { id: string }) => item.id)).toEqual([target.id])
      }
      await wrapper.get('input[aria-label="快速搜索项目"]').setValue('检测')
      expect(wrapper.emitted('switch')).toBeUndefined()
      expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
      autocomplete.vm.$emit('select', { id: target.id, value: `${target.code} · ${target.name}` })
      await flushPromises()
      expect(wrapper.emitted('switch')).toEqual([[target.id]])
      expect(confirm).not.toHaveBeenCalled()
      expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
      await wrapper.setProps({ project: target })
      expect(wrapper.get('input[aria-label="快速搜索项目"]').attributes('placeholder')).toBe('P700003 · 检测设备')
    } finally { wrapper.unmount() }
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
    ['客户', '客户筛选', 'C002'],
    ['事业部', '事业部筛选', 'division-2'],
    ['项目经理', '人员筛选', 'manager-two'],
    ['主设计', '人员筛选', 'lead-two'],
  ])('浏览项目可按%s下拉筛选', async (_, filterLabel, filterValue) => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild, otherRootProject], activeTab: 'overview', customers: browseCustomers, organizationUnits: browseUnits, companyId: 'company' },
    })

    await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
    if (filterLabel === '人员筛选') { wrapper.findAllComponents(ElSelect).find(item => item.props('ariaLabel') === filterLabel)!.vm.$emit('update:modelValue', filterValue); await flushPromises() }
    else await wrapper.get(`select[aria-label="${filterLabel}"]`).setValue(filterValue)

    expect(wrapper.findAll('[role="option"]')).toHaveLength(1)
    expect(wrapper.get('button[aria-label="选择浏览项目 P700003"]').text()).toContain('检测设备')
    if (filterLabel === '客户筛选' || filterLabel === '事业部筛选') {
      const select = wrapper.get(`select[aria-label="${filterLabel}"]`)
      const [option, label] = filterLabel === '客户筛选' ? ['C003', '暂无项目的客户'] : ['division-3', '暂无项目的事业部']
      expect(select.get(`option[value="${option}"]`).text()).toBe(label)
      expect(select.text()).not.toContain('其他公司的事业部')
      await select.setValue(option)
      expect(wrapper.get('.pdm-empty-info').text()).toBe('没有符合搜索条件的项目。')
    }
  })

  it('默认只推荐及浏览 P 项目，多选前缀与客户组合筛选，清空显示全部且再次打开恢复 P', async () => {
    const repair = { ...otherRootProject, id: 'repair', code: 'R700028', projectTypeCode: 'R', customerCode: 'C002' }
    const qa = { ...rootProject, id: 'qa', code: 'QA-001' }
    const lowercase = { ...otherRootProject, id: 'lowercase', code: 'p700030' }
    const projectTypes = [{ code: 'P', name: '标准项目' }, { code: 'R', name: '研发项目' }, { code: 'S', name: '售后项目' }, { code: 'W', name: '外发项目' }]
    const wrapper = mount(ProjectWorkspaceHeader, { props: { project: rootProject, projects: [rootProject, otherRootProject, repair, qa, lowercase, childProject], activeTab: 'overview', projectTypes, customers: browseCustomers } })
    try {
      const suggest = wrapper.getComponent({ name: 'ElAutocomplete' }).props('fetchSuggestions') as (query: string, callback: (items: Array<{ id: string }>) => void) => void
      const candidates = vi.fn()
      suggest('', candidates)
      expect(candidates.mock.calls[0]?.[0].map((item: { id: string }) => item.id)).toEqual(['lowercase', otherRootProject.id, rootProject.id])
      suggest('R700028', candidates)
      expect(candidates.mock.calls[1]?.[0]).toEqual([])
      await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
      const prefix = wrapper.findAllComponents(ElSelect).find(item => item.props('multiple'))!
      expect(prefix.props('modelValue')).toEqual(['P'])
      expect(prefix.findAllComponents(ElOption).map(option => option.props('label'))).toEqual(['标准项目 P', '研发项目 R', '售后项目 S', '外发项目 W'])
      const codes = () => wrapper.findAll('.pdm-project-browser-list > button').map(item => item.attributes('aria-label'))
      expect(codes()).toEqual(['选择浏览项目 p700030', '选择浏览项目 P700003', '选择浏览项目 P700002'])
      prefix.vm.$emit('update:modelValue', ['P', 'R'])
      await flushPromises()
      expect(codes()).toContain('选择浏览项目 R700028')
      expect(codes()).not.toContain('选择浏览项目 QA-001')
      await wrapper.get('select[aria-label="客户筛选"]').setValue('C002')
      expect(codes()).not.toContain('选择浏览项目 P700002')
      await wrapper.get('select[aria-label="客户筛选"]').setValue('')
      prefix.vm.$emit('update:modelValue', [])
      await flushPromises()
      expect(codes()).toHaveLength(5)
      await wrapper.get('button[aria-label="关闭项目浏览"]').trigger('click')
      await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
      expect(wrapper.findAllComponents(ElSelect).find(item => item.props('multiple'))!.props('modelValue')).toEqual(['P'])
    } finally { wrapper.unmount() }
  })

  it('点击快捷搜索保留失焦前的关键词，跨前缀查找并切换非 P 项目', async () => {
    const repair = { ...otherRootProject, id: 'repair', code: 'R700028' }
    const wrapper = mount(ProjectWorkspaceHeader, { props: { project: rootProject, projects: [rootProject, repair], activeTab: 'overview' } })
    try {
      const input = wrapper.get('input[aria-label="快速搜索项目"]')
      await input.setValue(' r700028 ')
      await input.trigger('blur')
      await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
      expect(wrapper.get('input[aria-label="搜索项目"]').element).toHaveProperty('value', 'r700028')
      expect(wrapper.findAllComponents(ElSelect).find(item => item.props('multiple'))!.props('modelValue')).toEqual([])
      expect(wrapper.findAll('.pdm-project-browser-list > button')).toHaveLength(1)
      await wrapper.get('button[aria-label="选择浏览项目 R700028"]').trigger('click')
      expect(wrapper.emitted('switch')).toEqual([['repair']])
      await wrapper.setProps({ project: repair })
      expect(wrapper.get('input[aria-label="快速搜索项目"]').attributes('placeholder')).toBe('R700028 · 检测设备')
      expect(wrapper.get('.pdm-project-family__list').text()).toContain('R700028')
    } finally { wrapper.unmount() }
  })

  it('改变前缀后返回第一页，无匹配时显示空状态', async () => {
    const projects = Array.from({ length: 25 }, (_, index) => ({ ...otherRootProject, id: `paged-${index}`, code: `P${700100 + index}` }))
    const repair = { ...otherRootProject, id: 'repair', code: 'R700028' }
    const wrapper = mount(ProjectWorkspaceHeader, { props: { project: rootProject, projects: [rootProject, repair, ...projects], activeTab: 'overview' } })
    try {
      await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
      await wrapper.get('button[aria-label="下一页"]').trigger('click')
      wrapper.findAllComponents(ElSelect).find(item => item.props('multiple'))!.vm.$emit('update:modelValue', ['R'])
      await flushPromises()
      expect(wrapper.get('[aria-label="项目列表分页"]').text()).toContain('第 1 / 1 页')
      expect(wrapper.find('button[aria-label="选择浏览项目 R700028"]').exists()).toBe(true)
      await wrapper.get('input[aria-label="搜索项目"]').setValue('不存在的项目')
      expect(wrapper.findAll('.pdm-project-browser-list > button')).toHaveLength(0)
      expect(wrapper.get('.pdm-empty-info').text()).toBe('没有符合搜索条件的项目。')
    } finally { wrapper.unmount() }
  })

  it('取消项目浏览时保持当前项目，选取后直接切换', async () => {
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

    expect(wrapper.emitted('switch')).toEqual([[otherRootProject.id]])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.get('input[aria-label="快速搜索项目"]').attributes('placeholder')).toBe('P700002 · XXX设备')
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


describe('项目中心逐页授权', () => {
  it('装配现场账号只显示八个只读页签，授权变更后立即隐藏对应入口', async () => {
    const tabs = ['overview', 'project-plan', 'opl', 'validation-plan', 'documents', 'bom', 'release', 'procurement']
    const permissions = computed(() => [projectCenterSchema, ...tabs.map(tab => `project.tab.${tab}.view`)])
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: rootProject, projects: [rootProject], activeTab: 'overview' },
      global: { provide: { [projectCenterAccessKey as symbol]: permissions } },
    })
    expect(wrapper.findAll('.pdm-project-tabs button').map(button => button.text())).toEqual(['概览', '计划', '质量', 'OPL', '图档', 'BOM', '发布', '备料'])
    wrapper.unmount()
  })
})
