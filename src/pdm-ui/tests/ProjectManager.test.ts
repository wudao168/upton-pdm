import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
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
  signedDate: '2026-08-13',
  customerCode: 'C001',
  customerName: '测试客户',
  quantity: 1,
  serialNumbers: ['70000001'],
  responsibleUsers: ['engineer'],
  organizationId: 'org-1',
  projectTypeCode: 'P',
  equipmentTypeCode: 2,
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

function mountProjectManager(directory = emptyDirectory, sourceProjects = [parent, child], administrator = false, canEdit = true, canDelete = false, canCreate = false) {
  return mount(ProjectManager, {
    props: {
      projects: sourceProjects,
      numberingOptions: {
        organizations: [{ id: 'org-1', name: '昆山公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山公司', currentProjectSequence: 1, currentSerialSequence: 2 }],
        projectTypes: [{ code: 'P', name: '标准项目', isActive: true }],
        equipmentTypes: [{ code: 2, name: '气密设备', isActive: true }],
      },
      customers: [{ id: 'customer-1', code: 'C001', name: '测试客户', isActive: true, sourceSystem: 'crm' }],
      users: [],
      organizationDirectory: directory,
      currentUsername: 'design-lead',
      administrator,
      canCreate,
      canEdit,
      canDelete,
      canCreateSubproject: false,
      pending: false,
      onCreate: vi.fn(),
      onCreateSubproject: vi.fn(),
      onUpdateProject: vi.fn().mockResolvedValue(parent),
      onDeleteProject: vi.fn().mockResolvedValue(undefined),
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

    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.find('.pdm-project-filter-panel').exists()).toBe(false)
    expect(wrapper.get('[aria-label="项目筛选"]').element.parentElement).toBe(wrapper.get('[aria-label="项目列表"]').element)
    expect(wrapper.find('.pdm-project-table-panel .pdm-panel-heading').exists()).toBe(false)
    expect(headers).toEqual(['项目号', '项目名称', '别名', '型号', '序列号', '客户', '事业部', '项目经理', '主设／工程师', '状态', '订单日期', '操作'])
    expect(cells[1].text()).toBe('气密设备')
    expect(cells[2].text()).toBe('—')
    expect(cells[3].text()).toBe('AK-2-C001-001-00')
    expect(cells[4].text()).toBe('70000001')
    expect(cells[5].text()).toBe('测试客户')
    expect(cells[6].text()).toBe('自动化事业部')
    expect(cells[7].text()).toBe('project-manager、project-manager-2')
    expect(cells[8].text()).toBe('design-lead')
    expect(cells[10].text()).toBe('2026-08-13')
  })

  it('默认按项目号自然倒序排列主项目并保持子项目正序', async () => {
    const newerParent = { ...parent, id: 'parent-2', code: 'P700010', name: '较新项目' }
    const newerChild = { ...child, id: 'child-2', code: 'P700001-2', childSequence: 2 }
    const wrapper = mountProjectManager(emptyDirectory, [parent, child, newerParent, newerChild])

    expect(wrapper.findAll('.pdm-project-code-link').map(item => item.text())).toEqual(['P700010', 'P700001'])

    await wrapper.get('[aria-label="展开P700001的子项目"]').trigger('click')
    expect(wrapper.findAll('.pdm-project-code-link').map(item => item.text())).toEqual(['P700010', 'P700001', 'P700001-1', 'P700001-2'])
  })

  it('将创建入口、搜索和筛选集中放在全宽项目列表上方', () => {
    const wrapper = mountProjectManager(emptyDirectory, [parent, child], false, true, false, true)
    const list = wrapper.get('[aria-label="项目列表"]')
    const toolbar = list.get('[aria-label="项目筛选"]')
    const createButton = toolbar.get('.pdm-project-create-action')

    expect(toolbar.element.children[0]).toBe(createButton.element)
    expect(toolbar.element.children[1].classList.contains('pdm-project-filters')).toBe(true)
    expect(createButton.text()).toContain('创建主项目')
    expect(toolbar.find('[aria-label="搜索项目"]').exists()).toBe(true)
    expect(toolbar.findAll('.pdm-project-filters > *')).toHaveLength(7)
    expect(list.find('.pdm-project-number-scroll').exists()).toBe(true)
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
    expect(wrapper.get('tr.is-child .pdm-project-code-cell').classes()).toContain('is-child-code')
  })

  it('可从操作菜单编辑项目名称、别名和订单日期', async () => {
    const wrapper = mountProjectManager()
    const menu = wrapper.findAllComponents({ name: 'ElDropdown' }).find(item => item.attributes('aria-label') === '操作项目P700001')
    expect(menu).toBeDefined()
    menu!.vm.$emit('command', 'edit')
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[aria-label="编辑项目基本信息"]').exists()).toBe(true)
    await wrapper.get('input[name="editProjectName"]').setValue('气密设备升级版')
    await wrapper.get('input[name="editProjectAlias"]').setValue('气密升级')
    await wrapper.get('input[name="editSignedDate"]').setValue('2026-08-16')
    await wrapper.get('[aria-label="编辑项目基本信息"]').trigger('submit')
    await flushPromises()

    expect(wrapper.props('onUpdateProject')).toHaveBeenCalledWith(parent.id, {
      organizationId: 'org-1',
      projectTypeCode: 'P',
      equipmentTypeCode: 2,
      customerId: 'customer-1',
      name: '气密设备升级版',
      projectAlias: '气密升级',
      signedDate: '2026-08-16',
      quantity: 1,
    })
  })

  it('点击对应列可直接分配事业部、项目经理和主设', async () => {
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
    await designWrapper.get('[aria-label="配置主设 P700001"]').trigger('click')
    expect(designWrapper.text()).toContain('主设（限1名）')
    expect(designWrapper.findAll('.is-staffing-target')).toHaveLength(1)
  })

  it('管理员可分配事业部、项目经理和主设', async () => {
    const administratorProject = { ...parent, executionUnitId: 'division-1' }
    const wrapper = mountProjectManager(emptyDirectory, [administratorProject], true)

    expect(wrapper.find('[aria-label="分配事业部 P700001"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="配置项目经理（含协同） P700001"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="配置主设 P700001"]').exists()).toBe(true)
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

  it('可分别按事业部、项目经理和主设工程师筛选，并自动展开匹配子项目', async () => {
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
    await wrapper.get('[aria-label="主设工程师筛选"]').setValue('child-designer')
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
    expect(text).toContain('默认优先显示主设所在事业部人员')
  })

  it('操作菜单移除进入与事业部分配，并仅向有权限账号开放项目删除', async () => {
    const withoutPermission = mountProjectManager(emptyDirectory, [parent, child], true)
    const hiddenCommands = withoutPermission.findAllComponents({ name: 'ElDropdownItem' }).map(item => item.props('command'))
    expect(hiddenCommands).not.toContain('open')
    expect(hiddenCommands).not.toContain('assign-execution')
    expect(hiddenCommands).not.toContain('delete')

    const wrapper = mountProjectManager(emptyDirectory, [parent, child], true, true, true)
    const commands = wrapper.findAllComponents({ name: 'ElDropdownItem' }).map(item => item.props('command'))
    expect(commands).toContain('edit')
    expect(commands).toContain('delete')
    expect(commands).not.toContain('open')
    expect(commands).not.toContain('assign-execution')

    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const menu = wrapper.findAllComponents({ name: 'ElDropdown' }).find(item => item.attributes('aria-label') === '操作项目P700001')
    expect(menu).toBeDefined()
    menu!.vm.$emit('command', 'delete')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(
      expect.stringContaining('项目号、型号流水和序列号将释放'),
      '确认删除项目',
      expect.objectContaining({ type: 'warning' }),
    )
    expect(wrapper.props('onDeleteProject')).toHaveBeenCalledWith(parent.id)
    confirm.mockRestore()
  })
})
