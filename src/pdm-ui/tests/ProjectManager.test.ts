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
  rootProjectId: 'parent-1',
  bomItemCategoryCode: '0301',
  executionUnitName: '自动化事业部',
  primaryProjectManager: 'project-manager',
  designLead: 'design-lead',
  collaborativeProjectManagers: ['project-manager-2'],
  designers: [],
  canAssignExecutionUnit: false,
  canManageMainStaffing: false,
  canAssignDesigners: false,
  canReadContent: true,
  effectiveProjectPermissions: ['project.edit', 'project.child.create', 'project.delete', 'project.designer.assign'],
}

const child: ProjectSummary = {
  ...parent,
  id: 'child-1',
  code: 'P700001-1',
  name: '气密设备子项目',
  parentProjectId: parent.id,
  rootProjectId: parent.id,
  bomItemCategoryCode: '0302',
  childSequence: 1,
  deviceModel: 'AK-2-C001-001-01',
  serialNumbers: ['70000002'],
}

const emptyDirectory: OrganizationDirectory = { organizations: [], units: [], memberships: [], managers: [], users: [] }

function mountProjectManager(directory = emptyDirectory, sourceProjects = [parent, child], administrator = false, canEdit = true, canDelete = false, canCreate = false, canCreateSubproject = false) {
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
      canCreateSubproject,
      pending: false,
      onCreate: vi.fn(),
      onCreateSubproject: vi.fn(),
      onUpdateProject: vi.fn().mockResolvedValue(parent),
      onDeleteProject: vi.fn().mockResolvedValue(undefined),
      onUpdateExecutionUnit: vi.fn(),
      onUpdateMainStaffing: vi.fn(),
      onUpdateDesigners: vi.fn(),
      onUpdateChildManager: vi.fn(),
    },
    global: { plugins: [ElementPlus] },
  })
}

describe('ProjectManager', () => {
  it('按项目类型切换主项目，子项目随主项目显示并保留搜索筛选', async () => {
    const research = { ...parent, id: 'research', code: 'R700001', projectTypeCode: 'R', name: '研发主项目' }
    const researchChild = { ...child, id: 'research-child', code: 'R700001-1', parentProjectId: research.id, rootProjectId: research.id, projectTypeCode: undefined, name: '研发下级设备' }
    const wrapper = mountProjectManager(emptyDirectory, [parent, child, research, researchChild])
    await wrapper.setProps({ numberingOptions: { ...wrapper.props('numberingOptions'), projectTypes: [
      { code: 'P', name: '标准项目', isActive: true }, { code: 'R', name: '研发项目', isActive: true }, { code: 'W', name: '外发项目', isActive: true },
    ] } })
    const tabs = () => wrapper.findAll('.pdm-project-type-tabs [role="tab"]')
    expect(tabs().map(item => item.text())).toEqual(['全部项目', '标准项目', '研发项目', '外发项目'])
    expect(tabs().find(item => item.text() === '全部项目')!.attributes('aria-selected')).toBe('true')
    await tabs().find(item => item.text() === '研发项目')!.trigger('click')
    expect(wrapper.get('tbody').text()).toContain('R700001')
    expect(wrapper.get('tbody').text()).not.toContain('P700001')
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.get('tbody').text()).toContain('研发下级设备')
    await wrapper.get('[aria-label="搜索项目"]').setValue('研发下级设备')
    await wrapper.get('[aria-label="搜索项目"]').trigger('keyup.enter')
    expect(wrapper.get('tbody').text()).toContain('R700001')
    await tabs().find(item => item.text() === '标准项目')!.trigger('click')
    expect(wrapper.find('tbody').exists()).toBe(false)
    await wrapper.get('[aria-label="搜索项目"]').setValue('')
    await wrapper.get('[aria-label="搜索项目"]').trigger('keyup.enter')
    expect(wrapper.get('tbody').text()).toContain('P700001')
    expect(wrapper.get('tbody').text()).not.toContain('R700001')
    await tabs().find(item => item.text() === '外发项目')!.trigger('click')
    expect(wrapper.text()).toContain('未找到匹配项目')
    wrapper.unmount()
  })

  it('旧项目、未配置类型和停用但已有项目的类型仍有独立入口', async () => {
    const legacy = { ...parent, id: 'legacy', code: 'OLD-001', projectTypeCode: undefined }
    const retired = { ...parent, id: 'retired', code: 'W700001', projectTypeCode: 'W' }
    const custom = { ...parent, id: 'custom', code: 'X700001', projectTypeCode: 'X' }
    const wrapper = mountProjectManager(emptyDirectory, [parent, legacy, retired, custom])
    await wrapper.setProps({ numberingOptions: { ...wrapper.props('numberingOptions'), projectTypes: [
      { code: 'P', name: '标准项目', isActive: true }, { code: 'W', name: '外发项目', isActive: false }, { code: 'S', name: '售后项目', isActive: false },
    ] } })
    const tabs = () => wrapper.findAll('.pdm-project-type-tabs [role="tab"]')
    expect(tabs().map(item => item.text())).toEqual(['全部项目', '标准项目', '外发项目', 'X 类项目', '未分类'])
    await tabs().find(item => item.text() === '未分类')!.trigger('click')
    expect(wrapper.get('tbody').text()).toContain('OLD-001')
    expect(wrapper.get('tbody').text()).not.toContain('P700001')
    await tabs().find(item => item.text() === '外发项目')!.trigger('click')
    expect(wrapper.get('tbody').text()).toContain('W700001')
    await wrapper.setProps({ projects: [parent, legacy, custom] })
    expect(tabs().find(item => item.text() === '全部项目')!.attributes('aria-selected')).toBe('true')
    wrapper.unmount()
  })

  it('从类型页签创建主项目时默认选中当前类型', async () => {
    const wrapper = mountProjectManager(emptyDirectory, [parent], false, true, false, true)
    await wrapper.setProps({ numberingOptions: { ...wrapper.props('numberingOptions'), projectTypes: [
      { code: 'P', name: '标准项目', isActive: true }, { code: 'R', name: '研发项目', isActive: true },
    ] } })
    await wrapper.findAll('.pdm-project-type-tabs [role="tab"]').find(item => item.text() === '研发项目')!.trigger('click')
    await wrapper.get('.pdm-project-create-action').trigger('click')
    expect((wrapper.get('[name="projectTypeCode"]').element as HTMLSelectElement).value).toBe('R')
    wrapper.unmount()
  })

  it('制造部门顺序与组织架构一致，按编码而非排序号排列', () => {
    const directory: OrganizationDirectory = { ...emptyDirectory, units: [
      { id: 't3', organizationId: 'org-1', code: 'T3', name: 'T3事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 0 },
      { id: 't1', organizationId: 'org-1', code: 'T1', name: 'T1事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 2 },
      { id: 't2', organizationId: 'org-1', code: 'T2', name: 'T2事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 1 },
    ] }
    const wrapper = mountProjectManager(directory)
    expect(wrapper.findAll('.pdm-project-classification__division').map(item => item.text())).toEqual(['T1事业部', 'T2事业部', 'T3事业部', '待分配'])
    wrapper.unmount()
  })
  it('待分配筛选仅保留当前公司未分配事业部的项目，且可展开子项目并切回公司或事业部', async () => {
    const directory: OrganizationDirectory = {...emptyDirectory, units:[
      {id:'division-1',organizationId:'org-1',code:'T1',name:'T1事业部',kind:'BusinessDivision',canManufacture:true,isActive:true,sortOrder:1},
    ]}
    const unassigned = {...parent,id:'unassigned',code:'P700002',executionUnitId:undefined,executionUnitName:undefined}
    const unassignedChild = {...child,id:'unassigned-child',code:'P700002-1',parentProjectId:unassigned.id,rootProjectId:unassigned.id,executionUnitId:undefined,executionUnitName:undefined}
    const assigned = {...parent,executionUnitId:'division-1',executionUnitName:'T1事业部'}
    const legacyAssigned = {...parent,id:'legacy',code:'P700003',executionUnitId:undefined,executionUnitName:'T1事业部'}
    const idOnlyAssigned = {...parent,id:'id-only',code:'P700004',executionUnitId:'division-1',executionUnitName:undefined}
    const otherCompany = {...unassigned,id:'other-company',code:'P700005',organizationId:'org-2'}
    const wrapper = mountProjectManager(directory,[assigned,unassigned,unassignedChild,legacyAssigned,idOnlyAssigned,otherCompany])
    const pending = wrapper.get('.pdm-project-classification__unassigned')
    await pending.trigger('click')
    expect(pending.attributes('aria-selected')).toBe('true')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.get('tbody').text()).toContain('P700002')
    expect(wrapper.get('tbody').text()).not.toMatch(/P700001|P700003|P700004|P700005/)
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    expect(wrapper.get('tbody').text()).toContain('P700002-1')
    await wrapper.get('.pdm-project-classification__division:not(.pdm-project-classification__unassigned)').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    expect(pending.attributes('aria-selected')).toBe('false')
    await wrapper.get('.pdm-project-classification__company [role="treeitem"]').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(4)
    wrapper.unmount()
  })

  it('列表仅允许分配事业部，管理员也不能从此修改其他项目字段或人员', async () => {
    const wrapper = mountProjectManager(emptyDirectory, [{ ...parent, executionUnitId: 'division-1', canAssignExecutionUnit: true, canManageMainStaffing: true }, { ...child, canAssignDesigners: true }], true, true)
    expect(wrapper.find('[aria-label="分配事业部 P700001"]').exists()).toBe(true)
    for (const name of ['配置项目经理（含协同） P700001', '配置主设 P700001', '配置子项目负责人 P700001-1', '分配工程师 P700001-1']) expect(wrapper.find('[aria-label="' + name + '"]').exists()).toBe(false)
    const menuItems = wrapper.findAllComponents({ name: 'ElDropdownItem' }).map(item => item.props('command'))
    expect(menuItems).not.toContain('edit')
    expect(menuItems).not.toContain('configure-staffing')
    expect(menuItems).not.toContain('assign-designers')
    await wrapper.get('[aria-label="分配事业部 P700001"]').trigger('click')
    expect(wrapper.text()).toContain('分配执行事业部 · P700001')
  })

  it('按公司与制造部门筛选，区分同名事业部并排除非制造部门', async () => {
    const directory: OrganizationDirectory = {
      ...emptyDirectory,
      units: [
        { id: 'division-1', organizationId: 'org-1', code: 'T1', name: 'T1事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 1 },
        { id: 'division-2', organizationId: 'org-1', code: 'T2', name: 'T1事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 2 },
        { id: 'office', organizationId: 'org-1', code: 'OFFICE', name: '行政部门', kind: 'Department', canManufacture: false, isActive: true, sortOrder: 3 },
      ],
    }
    const otherProject = { ...parent, id: 'parent-2', code: 'P700002', executionUnitId: 'division-2', executionUnitName: 'T1事业部' }
    const wrapper = mountProjectManager(directory, [{ ...parent, executionUnitId: 'division-1', executionUnitName: 'T1事业部' }, otherProject])
    const tree = wrapper.get('[aria-label="项目事业部分类"]')
    expect(tree.text()).not.toContain('行政部门')
    await tree.findAll('.pdm-project-classification__division')[0].trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.get('tbody').text()).toContain('P700001')
    await tree.findAll('.pdm-project-classification__division')[1].trigger('click')
    expect(wrapper.get('tbody').text()).toContain('P700002')
    await tree.get('[role="treeitem"]').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
  })
  it('按业务顺序显示居中项目列表', () => {
    const wrapper = mountProjectManager()
    const headers = wrapper.findAll('.pdm-project-number-table th').map(item => item.text())
    const cells = wrapper.get('.pdm-project-number-table tbody tr').findAll('td')

    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.find('.pdm-project-filter-panel').exists()).toBe(false)
    expect(wrapper.get('[aria-label="项目筛选"]').element.parentElement).toBe(wrapper.get('[aria-label="项目列表"]').element)
    expect(wrapper.find('.pdm-project-table-panel .pdm-panel-heading').exists()).toBe(false)
    expect(wrapper.findAllComponents({ name: 'ElDropdownItem' }).some(item => item.props('command') === 'copy-content')).toBe(false)
    expect(headers).toEqual(['项目号', '项目名称', '别名', '型号', '序列号', '客户', '事业部', '项目经理', '主设', '状态', '订单日期', '铭牌', '操作'])
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

  it('项目人员全局显示姓名且未分配子项目工程师保持空白', async () => {
    const directory: OrganizationDirectory = {
      ...emptyDirectory,
      users: [
        { username: 'project-manager', displayName: '项目经理甲', role: 'ProjectManager', isActive: true },
        { username: 'project-manager-2', displayName: '项目经理乙', role: 'ProjectManager', isActive: true },
        { username: 'design-lead', displayName: '主设甲', role: 'Engineer', isActive: true },
      ],
    }
    const wrapper = mountProjectManager(directory)

    const parentCells = wrapper.get('tbody tr').findAll('td')
    expect(parentCells[7].text()).toBe('项目经理甲、项目经理乙')
    expect(parentCells[8].text()).toBe('主设甲')
    expect(wrapper.get('[aria-label="项目经理筛选"]').text()).toContain('项目经理甲')
    expect(wrapper.get('[aria-label="主设工程师筛选"]').text()).toContain('主设甲')
    expect(wrapper.text()).not.toContain('project-manager')
    expect(wrapper.text()).not.toContain('design-lead')

    await wrapper.get('[aria-label="展开P700001的子项目"]').trigger('click')
    expect(wrapper.get('tr.is-child').findAll('td')[8].text()).toBe('待分配')
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
    expect(toolbar.element.children[1].classList.contains('pdm-project-collapse-action')).toBe(true)
    expect(toolbar.element.children[2].classList.contains('pdm-project-filters')).toBe(true)
    expect(createButton.text()).toContain('创建主项目')
    expect(toolbar.find('[aria-label="搜索项目"]').exists()).toBe(true)
    expect(toolbar.findAll('.pdm-project-filters > *')).toHaveLength(6)
    expect(list.find('.pdm-project-number-scroll').exists()).toBe(true)
  })

  it('无项目时说明当前公司尚未创建项目，而不是账号未分配', () => {
    const wrapper = mountProjectManager(emptyDirectory, [], false, true, false, false)

    expect(wrapper.text()).toContain('当前公司还没有项目')
    expect(wrapper.text()).toContain('请联系有创建权限的人员新建项目')
    expect(wrapper.text()).not.toContain('当前账号还没有分配到项目')
  })

  it('创建主项目时可按客户名称或编码筛选并选中客户', async () => {
    const wrapper = mountProjectManager(emptyDirectory, [], false, true, false, true)
    await wrapper.setProps({
      customers: [
        { id: 'customer-1', code: 'C001', name: '测试客户', isActive: true, sourceSystem: 'crm' },
        { id: 'customer-2', code: '0-000003', name: '深圳比亚迪汽车实业有限公司', isActive: true, sourceSystem: 'u9c' },
      ],
    })
    await wrapper.get('.pdm-project-create-action').trigger('click')

    const customerSelect = wrapper.getComponent({ name: 'ElSelect' })
    expect(customerSelect.props('filterable')).toBe(true)
    expect(customerSelect.props('placeholder')).toBe('输入客户名称或编码筛选')
    expect(customerSelect.findAllComponents({ name: 'ElOption' }).map(item => item.props('label'))).toEqual([
      '测试客户（C001）',
      '深圳比亚迪汽车实业有限公司（0-000003）',
    ])

    customerSelect.vm.$emit('update:modelValue', 'customer-2')
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('客户编码由U9C客户数据自动带出：0-000003')
  })

  it('直接建项的订单日期也可留空，且不自动填入当天日期', async () => {
    const wrapper = mountProjectManager(emptyDirectory, [], false, true, false, true)
    await wrapper.get('.pdm-project-create-action').trigger('click')
    const form = wrapper.get('[aria-label="创建PLM项目"]')
    expect((form.get('[name="signedDate"]').element as HTMLInputElement).value).toBe('')
    await form.get('[name="projectName"]').setValue('未签合同项目')
    await wrapper.findAll('button').find(item => item.text() === '创建并取号')!.trigger('click')
    await flushPromises()
    expect(wrapper.props('onCreate')).toHaveBeenCalledWith(expect.objectContaining({ name: '未签合同项目', signedDate: null }))
  })

  it('创建子项目时不显示设备类型并由后端继承上级项目', async () => {
    const wrapper = mountProjectManager(emptyDirectory, [parent], false, true, false, false, true)
    const menu = wrapper.findAllComponents({ name: 'ElDropdown' }).find(item => item.attributes('aria-label') === '操作项目P700001')
    expect(menu).toBeDefined()
    menu!.vm.$emit('command', 'create-child')
    await wrapper.vm.$nextTick()

    const form = wrapper.get('[aria-label="创建PLM子项目"]')
    expect(form.find('[name="childEquipmentTypeCode"]').exists()).toBe(false)
    await form.get('[name="childProjectName"]').setValue('继承型号设备')
    await form.get('[name="childQuantity"]').setValue(2)
    await form.trigger('submit')
    await flushPromises()

    expect(wrapper.props('onCreateSubproject')).toHaveBeenCalledWith(parent.id, {
      name: '继承型号设备',
      projectAlias: '',
      quantity: 2,
    })
  })

  it('主项目行提供创建子项目快捷入口', async () => {
    const wrapper = mountProjectManager(emptyDirectory, [parent, child], false, true, false, false, true)

    expect(wrapper.find('[aria-label="为P700001-1创建子项目"]').exists()).toBe(false)
    await wrapper.get('[aria-label="为P700001创建子项目"]').trigger('click')

    expect(wrapper.find('[aria-label="创建PLM子项目"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('创建0302设备子项目 · P700001')
  })

  it('子项目不能继续创建二级子项目', async () => {
    const wrapper = mountProjectManager(emptyDirectory, [parent, child], false, true, false, false, true)
    await wrapper.get('[aria-label="展开P700001的子项目"]').trigger('click')

    const childMenu = wrapper.findAllComponents({ name: 'ElDropdown' }).find(item => item.attributes('aria-label') === '操作项目P700001-1')
    expect(childMenu).toBeDefined()
    expect(childMenu!.findAllComponents({ name: 'ElDropdownItem' }).map(item => item.props('command'))).not.toContain('create-child')
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

  it('可一键展开和折叠当前项目明细', async () => {
    const wrapper = mountProjectManager()

    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    await wrapper.get('[aria-label="折叠全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
  })

  it('0302设备子项目可继续递归展开0302子项', async () => {
    const nested: ProjectSummary = {
      ...child,
      id: 'child-2',
      code: 'P700001-1-1',
      name: '设备模块',
      parentProjectId: child.id,
      rootProjectId: parent.id,
      childSequence: 1,
      deviceModel: 'AK-2-C001-001-01-01',
    }
    const wrapper = mountProjectManager(emptyDirectory, [parent, child, nested])

    await wrapper.get('[aria-label="展开P700001的子项目"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    await wrapper.get('[aria-label="展开P700001-1的子项目"]').trigger('click')

    expect(wrapper.findAll('tr.is-child')).toHaveLength(2)
    expect(wrapper.text()).toContain('P700001-1-1')
    expect(wrapper.findAll('tr.is-child')[1].findAll('td')[3].text()).toBe('AK-2-C001-001-01-01')
  })

  it('执行单位候选将事业部置顶并按名称自然排序', async () => {
    const directory: OrganizationDirectory = {
      ...emptyDirectory,
      units: [
        { id: 'division-10', organizationId: 'org-1', code: 'T10', name: 'T10事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 1 },
        { id: 'standardization', organizationId: 'org-1', code: 'STD', name: '标准化', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 2 },
        { id: 'division-2', organizationId: 'org-1', code: 'T2', name: 'T2事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 3 },
        { id: 'ordinary', organizationId: 'org-1', code: 'ORD', name: '普通部门', kind: 'BusinessDivision', canManufacture: false, isActive: true, sortOrder: 4 },
        { id: 'inactive-division', organizationId: 'org-1', code: 'T1', name: 'T1事业部', kind: 'BusinessDivision', canManufacture: true, isActive: false, sortOrder: 5 },
        { id: 'department', organizationId: 'org-1', code: 'D1', name: 'A部门', kind: 'Department', canManufacture: true, isActive: true, sortOrder: 6 },
      ],
    }
    const wrapper = mountProjectManager(directory, [{ ...parent, canAssignExecutionUnit: true }])

    await wrapper.get('[aria-label="分配事业部 P700001"]').trigger('click')
    const options = wrapper.findAllComponents({ name: 'ElOption' })

    expect(options.map(item => item.props('label'))).toEqual(['T2事业部', 'T10事业部', '标准化', '普通部门'])
    expect(options.map(item => item.props('disabled'))).toEqual([false, false, false, true])
  })

  it('搜索后默认折叠，支持展开、折叠以及清空搜索', async () => {
    const wrapper = mountProjectManager()
    await wrapper.get('[aria-label="搜索项目"]').setValue('子项目')
    await wrapper.findAll('.pdm-project-filters button').find(button => button.text() === '搜索')!.trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    await wrapper.get('[aria-label="折叠全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    await wrapper.get('[aria-label="搜索项目"]').setValue('')
    await wrapper.get('[aria-label="搜索项目"]').trigger('keyup', { key: 'Enter' })
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    wrapper.unmount()
  })
  it('选择制造部门后默认折叠且仍可展开、折叠全部明细', async () => {
    const directory: OrganizationDirectory = { ...emptyDirectory, units: [
      { id: 'division', organizationId: 'org-1', code: 'T2', name: '自动化事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 0 },
    ] }
    const wrapper = mountProjectManager(directory, [{...parent, executionUnitId:'division'}, {...child, executionUnitId:'division'}])
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    await wrapper.get('.pdm-project-classification__division').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    expect(wrapper.get('[aria-label="展开全部项目明细"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    await wrapper.get('[aria-label="折叠全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    wrapper.unmount()
  })
  it('可分别按事业部、项目经理和主设工程师筛选，且筛选后默认折叠，可手动展开匹配子项目', async () => {
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
    expect(wrapper.findAll('tr.is-child')).toHaveLength(0)
    expect(wrapper.get('[aria-label="展开全部项目明细"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('[aria-label="展开全部项目明细"]').trigger('click')
    expect(wrapper.findAll('tr.is-child')).toHaveLength(1)
    expect(wrapper.get('tr.is-child').text()).toContain('P700001-1')
    expect(wrapper.text()).not.toContain('P700002')
  })

  it('操作菜单移除进入与事业部分配，并仅向有权限账号开放项目删除', async () => {
    const withoutPermission = mountProjectManager(emptyDirectory, [parent, child], true)
    const hiddenCommands = withoutPermission.findAllComponents({ name: 'ElDropdownItem' }).map(item => item.props('command'))
    expect(hiddenCommands).not.toContain('open')
    expect(hiddenCommands).not.toContain('assign-execution')
    expect(hiddenCommands).not.toContain('delete')

    const wrapper = mountProjectManager(emptyDirectory, [parent, child], true, true, true)
    const commands = wrapper.findAllComponents({ name: 'ElDropdownItem' }).map(item => item.props('command'))
    expect(commands).not.toContain('edit')
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
