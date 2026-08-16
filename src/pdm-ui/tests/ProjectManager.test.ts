import { mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import ProjectManager from '../src/components/ProjectManager.vue'
import type { OrganizationDirectory, ProjectSummary } from '../src/types'

const parent: ProjectSummary = {
  id: 'parent-1',
  code: 'P700001',
  name: '气密设备',
  owner: 'engineer',
  stage: '设计中',
  vaultName: 'PDM',
  vaultLocation: 'D:\\PDM\\P700001',
  releaseLocation: 'D:\\Release\\P700001',
  deviceModel: 'AK-2-C001-001-00',
  customerCode: 'C001',
  customerName: '测试客户',
  quantity: 1,
  serialNumbers: ['70000001'],
  responsibleUsers: ['engineer'],
  organizationId: 'org-1',
  executionUnitName: '自动化事业部',
  primaryProjectManager: 'project-manager',
  designLead: 'design-lead',
  collaborativeProjectManagers: ['project-manager-2'],
  designers: [],
  canAssignExecutionUnit: false,
  canManageMainStaffing: false,
  canAssignDesigners: false,
  canReadContent: true,
}

const child: ProjectSummary = {
  ...parent,
  id: 'child-1',
  code: 'P700001-1',
  name: '气密设备子项目',
  parentProjectId: parent.id,
  childSequence: 1,
  deviceModel: 'AK-2-C001-001-01',
  serialNumbers: ['70000002'],
}

const emptyDirectory: OrganizationDirectory = { organizations: [], units: [], memberships: [], managers: [], users: [] }

function mountProjectManager(directory = emptyDirectory, sourceProjects = [parent, child], administrator = false) {
  return mount(ProjectManager, {
    props: {
      projects: sourceProjects,
      numberingOptions: { organizations: [], projectTypes: [], equipmentTypes: [] },
      customers: [],
      users: [],
      organizationDirectory: directory,
      currentUsername: 'design-lead',
      administrator,
      canCreate: false,
      canCreateSubproject: false,
      pending: false,
      onCreate: vi.fn(),
      onCreateSubproject: vi.fn(),
      onUpdateExecutionUnit: vi.fn(),
      onUpdateMainStaffing: vi.fn(),
      onUpdateDesigners: vi.fn(),
    },
    global: { plugins: [ElementPlus] },
  })
}

describe('ProjectManager', () => {
  it('按业务顺序显示居中项目列表', () => {
    const wrapper = mountProjectManager()
    const headers = wrapper.findAll('.pdm-project-number-table th').map(item => item.text())
    const cells = wrapper.get('.pdm-project-number-table tbody tr').findAll('td')

    expect(wrapper.find('.pdm-pagebar p').exists()).toBe(false)
    expect(headers).toEqual(['项目号', '项目名称', '别名', '型号', '序列号', '客户', '事业部', '项目经理', '设计负责人', '状态', '操作'])
    expect(cells[1].text()).toBe('气密设备')
    expect(cells[2].text()).toBe('—')
    expect(cells[3].text()).toBe('AK-2-C001-001-00')
    expect(cells[4].text()).toBe('70000001')
    expect(cells[5].text()).toBe('测试客户')
    expect(cells[6].text()).toBe('自动化事业部')
    expect(cells[7].text()).toBe('project-manager、project-manager-2')
    expect(cells[8].text()).toBe('design-lead')
  })

  it('点击主项目或子项目项目号进入对应项目中心', async () => {
    const wrapper = mountProjectManager()
    await wrapper.get('[aria-label="进入项目 P700001"]').trigger('click')
    expect(wrapper.emitted('open')).toEqual([[parent.id]])

    await wrapper.get('[aria-label="展开P700001的子项目"]').trigger('click')
    await wrapper.get('[aria-label="进入项目 P700001-1"]').trigger('click')
    expect(wrapper.emitted('open')).toEqual([[parent.id], [child.id]])
  })

  it('默认折叠子项目，并可手动展开', async () => {
    const wrapper = mountProjectManager()

    expect(wrapper.get('.pdm-project-number-table').text()).toContain('序列号')
    expect(wrapper.get('.pdm-project-number-table').text()).toContain('70000001')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    await wrapper.get('[aria-label="展开P700001的子项目"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    expect(wrapper.get('tr.is-child').text()).toContain('气密设备子项目')
    expect(wrapper.get('tr.is-child').text()).toContain('70000002')
    expect(wrapper.get('tr.is-child').findAll('td')[5].text()).toBe('测试客户')
  })

  it('点击对应列可直接分配事业部、项目经理和设计负责人', async () => {
    const editableParent = { ...parent, executionUnitId: 'division-1', canAssignExecutionUnit: true, canManageMainStaffing: true }
    const executionWrapper = mountProjectManager(emptyDirectory, [editableParent])
    await executionWrapper.get('[aria-label="分配事业部 P700001"]').trigger('click')
    expect(executionWrapper.text()).toContain('分配执行事业部 · P700001')

    const managerWrapper = mountProjectManager(emptyDirectory, [editableParent])
    await managerWrapper.get('[aria-label="配置项目经理（含协同） P700001"]').trigger('click')
    expect(managerWrapper.text()).toContain('项目经理（限1名）')
    expect(managerWrapper.text()).toContain('协同项目经理（可多选）')
    expect(managerWrapper.findAll('.is-staffing-target')).toHaveLength(2)

    const designWrapper = mountProjectManager(emptyDirectory, [editableParent])
    await designWrapper.get('[aria-label="配置设计负责人 P700001"]').trigger('click')
    expect(designWrapper.text()).toContain('设计负责人（限1名）')
    expect(designWrapper.findAll('.is-staffing-target')).toHaveLength(1)
  })

  it('管理员可分配事业部、项目经理和设计负责人', async () => {
    const administratorProject = { ...parent, executionUnitId: 'division-1' }
    const wrapper = mountProjectManager(emptyDirectory, [administratorProject], true)

    expect(wrapper.find('[aria-label="分配事业部 P700001"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="配置项目经理（含协同） P700001"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="配置设计负责人 P700001"]').exists()).toBe(true)
  })

  it('可搜索子项目并按项目层级筛选', async () => {
    const wrapper = mountProjectManager()
    const search = wrapper.get('[aria-label="搜索项目"]')
    const filter = wrapper.get('[aria-label="项目层级筛选"]')

    await search.setValue('子项目')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)

    await filter.setValue('parent')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)

    await search.setValue('')
    await filter.setValue('child')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    expect(wrapper.findAll('.pdm-project-number-table tbody tr')).toHaveLength(1)
  })

  it('可分别按事业部、项目经理和设计负责人筛选，并自动展开匹配子项目', async () => {
    const otherParent: ProjectSummary = {
      ...parent,
      id: 'parent-2',
      code: 'P700002',
      name: '机器人项目',
      executionUnitName: '机器人事业部',
      primaryProjectManager: 'robot-manager',
      collaborativeProjectManagers: [],
      designLead: 'robot-lead',
    }
    const childWithDesigner: ProjectSummary = { ...child, designers: ['child-designer'] }
    const wrapper = mountProjectManager(emptyDirectory, [parent, childWithDesigner, otherParent])

    await wrapper.get('[aria-label="事业部筛选"]').setValue('自动化事业部')
    expect(wrapper.text()).toContain('P700001')
    expect(wrapper.text()).not.toContain('P700002')

    await wrapper.get('[aria-label="事业部筛选"]').setValue('')
    await wrapper.get('[aria-label="项目经理筛选"]').setValue('project-manager-2')
    expect(wrapper.text()).toContain('P700001')
    expect(wrapper.text()).not.toContain('P700002')

    await wrapper.get('[aria-label="项目经理筛选"]').setValue('')
    await wrapper.get('[aria-label="设计负责人筛选"]').setValue('child-designer')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    expect(wrapper.get('tr.is-child').text()).toContain('P700001-1')
    expect(wrapper.text()).not.toContain('P700002')
  })

  it('子项目设计人员优先显示本事业部，并将其他事业部单独分组提示', async () => {
    const directory: OrganizationDirectory = {
      organizations: [{ id: 'org-1', name: '昆山公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山公司', currentProjectSequence: 1, currentSerialSequence: 1 }],
      units: [
        { id: 'division-own', organizationId: 'org-1', code: 'A', name: '自动化事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 },
        { id: 'division-other', organizationId: 'org-1', code: 'B', name: '机器人事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 2 },
      ],
      memberships: [
        { unitId: 'division-own', username: 'design-lead', isPrimary: true },
        { unitId: 'division-own', username: 'designer-own', isPrimary: true },
        { unitId: 'division-other', username: 'designer-other', isPrimary: true },
      ],
      managers: [],
      users: [
        { username: 'design-lead', displayName: '设计负责人', role: 'Engineer', isActive: true },
        { username: 'designer-own', displayName: '本部设计', role: 'Engineer', isActive: true },
        { username: 'designer-other', displayName: '跨部设计', role: 'Engineer', isActive: true },
      ],
    }
    const wrapper = mountProjectManager(directory, [parent, { ...child, canAssignDesigners: true }])
    await wrapper.get('[aria-label="展开P700001的子项目"]').trigger('click')
    const childMenu = wrapper.findAllComponents({ name: 'ElDropdown' }).find(item => item.attributes('aria-label') === '操作项目P700001-1')
    expect(childMenu).toBeDefined()
    childMenu!.vm.$emit('command', 'assign-designers')
    await wrapper.vm.$nextTick()
    const text = wrapper.text()
    expect(text.indexOf('本事业部（优先）')).toBeLessThan(text.indexOf('其他事业部'))
    expect(text).toContain('默认优先显示设计负责人所在事业部人员')
  })

  it('客户端不显示高风险项目删除入口', () => {
    const wrapper = mountProjectManager(emptyDirectory, [parent, child], true)

    expect(wrapper.text()).not.toContain('删除')
    expect(wrapper.emitted('delete')).toBeUndefined()
  })
})
