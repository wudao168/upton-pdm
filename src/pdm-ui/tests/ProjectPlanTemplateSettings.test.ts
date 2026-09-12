import ElementPlus from 'element-plus'
import { mount, flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectPlanTemplateSettings from '../src/components/ProjectPlanTemplateSettings.vue'
import SystemManagement from '../src/components/SystemManagement.vue'
import type { ProjectPlanTemplate } from '../src/types'

const api = vi.hoisted(() => ({ listProjectPlanTemplates: vi.fn(), saveProjectPlanTemplate: vi.fn() }))
vi.mock('../src/api', () => api)
const template: ProjectPlanTemplate = {
  id: 'template', name: '设备模板', isActive: true, rowVersion: 1,
  createdBy: 'admin', updatedBy: 'admin', createdAt: '', updatedAt: '',
  stages: [{ code: 'design', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 }],
  tasks: [{ id: 'task', name: '设计评审', stage: 'design', sortOrder: 10, durationRatio: .5, weight: 1, predecessorSortOrders: [], defaultAssigneeRole: 'DesignLead', isRequired: true, isMilestone: false }],
}
const wrappers: ReturnType<typeof mount>[] = []
function render(canManage = true) {
  const wrapper = mount(ProjectPlanTemplateSettings, { props: { token: 'test', currentUsername: 'admin', canManage }, global: { plugins: [ElementPlus] } })
  wrappers.push(wrapper)
  return wrapper
}
beforeEach(() => {
  vi.clearAllMocks()
  api.listProjectPlanTemplates.mockResolvedValue([structuredClone(template)])
  api.saveProjectPlanTemplate.mockImplementation(async (id, input) => ({ ...structuredClone(template), ...JSON.parse(JSON.stringify(input)), id: id || 'copy', rowVersion: 2 }))
})
afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))
describe('设置页项目计划模板', () => {
  function groupedTemplate() {
    return { ...structuredClone(template), stages: [
      { code: 'design', name: '设计', participatesInDelivery: true, durationRatio: .5, progressRatio: .5 },
      { code: 'deliver', name: '交付', participatesInDelivery: true, durationRatio: .5, progressRatio: .5 },
    ], tasks: [
      structuredClone(template.tasks[0]!),
      { ...structuredClone(template.tasks[0]!), id: 'second', name: '设计输出', sortOrder: 20, predecessorSortOrders: [10] },
      { ...structuredClone(template.tasks[0]!), id: 'third', name: '交付检查', stage: 'deliver', sortOrder: 30, predecessorSortOrders: [20] },
    ] }
  }
  it('左侧按阶段包含子任务，选择父项时右侧显示其子任务', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([groupedTemplate()])
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('[data-stage="design"] .pdm-template-children').text()).toContain('设计输出')
    expect(wrapper.find('[data-stage="deliver"] .pdm-template-children').text()).toContain('交付检查')
    expect(wrapper.findAll('.pdm-template-row')).toHaveLength(2)
    await wrapper.find('[aria-label="选择阶段：交付"]').trigger('click')
    expect(wrapper.findAll('.pdm-template-row')).toHaveLength(1)
    expect(wrapper.find('.pdm-template-row').attributes('data-task-id')).toBe('third')
    await wrapper.find('[aria-label="折叠阶段：交付"]').trigger('click')
    expect(wrapper.find('[data-stage="deliver"] .pdm-template-children').exists()).toBe(false)
  })
  it('移动任务同步重编号且前置依赖不串项，取消可恢复', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([groupedTemplate()])
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('[aria-label="上移任务：设计评审"]').attributes('disabled')).toBeDefined()
    await wrapper.find('[aria-label="下移任务：设计评审"]').trigger('click')
    expect(wrapper.findAll('.pdm-template-row').map(row => row.attributes('data-task-id'))).toEqual(['second', 'task'])
    await wrapper.find('footer .pdm-primary-action').trigger('click')
    await flushPromises()
    const saved = api.saveProjectPlanTemplate.mock.calls[0]![1].tasks
    expect(saved).toEqual(expect.arrayContaining([
      expect.objectContaining({ id: 'second', sortOrder: 10, predecessorSortOrders: [20] }),
      expect.objectContaining({ id: 'task', sortOrder: 20, predecessorSortOrders: [] }),
      expect.objectContaining({ id: 'third', sortOrder: 30, predecessorSortOrders: [10] }),
    ]))
    await wrapper.find('[aria-label="上移任务：设计评审"]').trigger('click')
    await wrapper.find('footer .pdm-secondary-action').trigger('click')
    expect(wrapper.findAll('.pdm-template-row').map(row => row.attributes('data-task-id'))).toEqual(['second', 'task'])
  })
  it('移动父阶段保持子任务归属，并同步跨阶段依赖序号', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([groupedTemplate()])
    const wrapper = render()
    await flushPromises()
    await wrapper.find('[aria-label="上移阶段：交付"]').trigger('click')
    expect(wrapper.findAll('.pdm-template-stage-group').map(group => group.attributes('data-stage'))).toEqual(['deliver', 'design'])
    expect(wrapper.find('[data-stage="deliver"] .pdm-template-children').text()).toContain('交付检查')
    await wrapper.find('footer .pdm-primary-action').trigger('click')
    await flushPromises()
    const saved = api.saveProjectPlanTemplate.mock.calls[0]![1]
    expect(saved.stages.map((stage: { code: string }) => stage.code)).toEqual(['deliver', 'design'])
    expect(saved.tasks).toEqual(expect.arrayContaining([
      expect.objectContaining({ id: 'third', stage: 'deliver', sortOrder: 10, predecessorSortOrders: [30] }),
      expect.objectContaining({ id: 'second', stage: 'design', sortOrder: 30, predecessorSortOrders: [20] }),
    ]))
  })
  it('新任务归属当前父阶段，跨阶段移动保留任务依赖', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([groupedTemplate()])
    const wrapper = render()
    await flushPromises()
    const stageSelect = wrapper.find('[data-task-id="task"]').findAllComponents({ name: 'ElSelect' })[0]!
    stageSelect.vm.$emit('update:modelValue', 'deliver')
    await flushPromises()
    expect(wrapper.find('[data-stage="deliver"] .pdm-template-children').text()).toContain('设计评审')
    await wrapper.findAll('button').find(button => button.text() === '增加子任务')!.trigger('click')
    expect(wrapper.findAll('.pdm-template-row')).toHaveLength(3)
    await wrapper.find('footer .pdm-primary-action').trigger('click')
    await flushPromises()
    expect(api.saveProjectPlanTemplate.mock.calls[0]![1].tasks).toEqual(expect.arrayContaining([
      expect.objectContaining({ id: 'second', stage: 'design', sortOrder: 10, predecessorSortOrders: [30] }),
      expect.objectContaining({ name: '新任务', stage: 'deliver', sortOrder: 40, predecessorSortOrders: [] }),
    ]))
  })

  it('可修改初始值并带版本保存，复制时不会直接写入', async () => {
    const wrapper = render()
    await flushPromises()
    expect(api.listProjectPlanTemplates).toHaveBeenCalledWith('test', true)
    await wrapper.find('input[aria-label="模板名称"]').setValue('标准设备')
    await wrapper.find('.pdm-plan-stage-editor input').setValue('方案确认')
    await wrapper.find('footer .pdm-primary-action').trigger('click')
    await flushPromises()
    expect(api.saveProjectPlanTemplate).toHaveBeenCalledWith('template', expect.objectContaining({ name: '标准设备', expectedRowVersion: 1, stages: expect.arrayContaining([expect.objectContaining({ name: '方案确认' })]) }), 'test')
    api.saveProjectPlanTemplate.mockClear()
    await wrapper.findAll('button').find(button => button.text() === '复制为新模板')!.trigger('click')
    expect(api.saveProjectPlanTemplate).not.toHaveBeenCalled()
    await wrapper.find('footer .pdm-secondary-action').trigger('click')
    expect((wrapper.find('input[aria-label="模板名称"]').element as HTMLInputElement).value).toBe('标准设备')
  })
  it('保存后保留当前阶段、选中任务、折叠状态和滚动位置', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([groupedTemplate()])
    const wrapper = render()
    await flushPromises()
    await wrapper.find('[aria-label="折叠阶段：设计"]').trigger('click')
    await wrapper.find('[aria-label="选择任务：交付检查"]').trigger('click')
    const tree = wrapper.find('.pdm-template-tree__body').element as HTMLElement
    const table = wrapper.find('.pdm-template-table').element as HTMLElement
    tree.scrollTop = 120; table.scrollLeft = 200
    await wrapper.find('footer .pdm-primary-action').trigger('click')
    await flushPromises()
    expect(wrapper.find('.pdm-plan-stage-editor input').element).toHaveProperty('value', '交付')
    expect(wrapper.find('.pdm-template-row.is-selected').attributes('data-task-id')).toBe('third')
    expect(wrapper.find('[data-stage="design"] .pdm-template-children').exists()).toBe(false)
    expect(tree.scrollTop).toBe(120)
    expect(table.scrollLeft).toBe(200)
    expect(api.listProjectPlanTemplates).toHaveBeenCalledTimes(1)
  })
  it('阶段内权重自动汇总，0权重不贡献，并行工期比例不限制100%', async () => {
    const source = groupedTemplate()
    source.tasks[0]!.weight = 6; source.tasks[1]!.weight = 4
    source.tasks[0]!.durationRatio = 1; source.tasks[1]!.durationRatio = 1
    api.listProjectPlanTemplates.mockResolvedValue([source])
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-task-allocation-summary').text()).toContain('200%')
    expect(wrapper.find('.pdm-task-allocation-summary').text()).toContain('总权重 10')
    expect(wrapper.findAll('.pdm-task-contribution').map(item => item.text())).toEqual(['60%', '40%'])
    const weights = wrapper.findAll('.pdm-template-row').map(row => row.findAllComponents({ name: 'ElInputNumber' })[1]!)
    weights[0]!.vm.$emit('update:modelValue', 0)
    await flushPromises()
    expect(wrapper.findAll('.pdm-task-contribution').map(item => item.text())).toEqual(['0%', '100%'])
    weights[1]!.vm.$emit('update:modelValue', 0)
    await flushPromises()
    expect(wrapper.find('footer .pdm-primary-action').attributes('disabled')).toBeDefined()
  })
  it('旧模板缺少阶段分配时明确提示并阻止保存，不悄悄赋予比例', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([{ ...structuredClone(template), stages: [{ code: 'design', name: '设计' }] }])
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('footer').text()).toContain('交付阶段工期占比、进度占比须分别合计100%')
    expect(wrapper.text()).not.toContain('旧模板待配置')
    expect(wrapper.find('footer .pdm-primary-action').attributes('disabled')).toBeDefined()
    expect(api.saveProjectPlanTemplate).not.toHaveBeenCalled()
  })
  it('启动或审核可设固定2天，与阶段比例任务混用并保留权重', async () => {
    const wrapper = render()
    await flushPromises()
    const row = wrapper.find('.pdm-template-row')
    row.findAllComponents({ name: 'ElSelect' })[1]!.vm.$emit('update:modelValue', 'fixed')
    await flushPromises()
    row.findAllComponents({ name: 'ElInputNumber' })[0]!.vm.$emit('update:modelValue', 2)
    await flushPromises()
    expect(wrapper.find('.pdm-task-allocation-summary').text()).toContain('固定任务合计 2 天')
    expect(wrapper.find('.pdm-task-allocation-summary').text()).toContain('比例任务合计 0%')
    await wrapper.find('footer .pdm-primary-action').trigger('click')
    await flushPromises()
    expect(api.saveProjectPlanTemplate.mock.calls[0]![1].tasks[0]).toEqual(expect.objectContaining({ fixedDurationDays: 2, weight: 1 }))
    expect(wrapper.find('input[aria-label="固定工期天数"]').element).toHaveProperty('value', '2')
  })
  it('精简顶部布局，元信息同一栏且刷新位于增加阶段左侧', async () => {
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-plan-template-settings > header').exists()).toBe(false)
    expect(wrapper.find('.pdm-template-fields > .pdm-allocation-summary').exists()).toBe(false)
    expect(wrapper.find('.pdm-template-picker').text()).toContain('选择模板')
    expect(wrapper.find('.pdm-template-picker').text()).toContain('模板名称')
    expect(wrapper.find('.pdm-template-picker').text()).toContain('项目类型')
    expect(wrapper.find('.pdm-template-picker').text()).toContain('启用模板')
    expect(wrapper.findAll('.pdm-template-tree-actions button').map(button => button.text())).toEqual(['刷新', '增加阶段'])
    expect(wrapper.find('.pdm-template-tree > header + .pdm-template-delivery-totals').text()).toBe('交付工期 100%交付进度 100%')
    const stageRatio = wrapper.find('.pdm-stage-allocation-controls').findAllComponents({ name: 'ElInputNumber' })[0]!
    stageRatio.vm.$emit('update:modelValue', 60)
    await flushPromises()
    expect(wrapper.find('.pdm-template-delivery-totals').text()).toContain('交付工期 60%')
    expect(wrapper.find('.pdm-task-allocation-summary').exists()).toBe(true)
    await wrapper.find('.pdm-template-tree-actions button').trigger('click')
    await flushPromises()
    expect(api.listProjectPlanTemplates).toHaveBeenCalledTimes(2)
    expect(api.saveProjectPlanTemplate).not.toHaveBeenCalled()
  })
  it('无管理权限时不读取停用模板，也不显示任何编辑控件', async () => {
    const wrapper = render(false)
    await flushPromises()
    expect(wrapper.text()).toContain('仅开发者和管理员')
    expect(wrapper.find('input').exists()).toBe(false)
    expect(api.listProjectPlanTemplates).not.toHaveBeenCalled()
    expect(api.saveProjectPlanTemplate).not.toHaveBeenCalled()
  })
  it('设置页只为获准角色显示模板入口，普通设置权限不授予编辑权', async () => {
    const wrapper = mount(SystemManagement, { props: { permissions: ['settings.storage.manage'], canManageProjectPlanTemplates: false } as InstanceType<typeof SystemManagement>['$props'], global: { stubs: { U9IntegrationManagement: true, StorageSettings: true, ProjectPlanTemplateSettings: true } } })
    wrappers.push(wrapper)
    expect(wrapper.find('nav').text()).not.toContain('项目计划模板')
    await wrapper.setProps({ canManageProjectPlanTemplates: true })
    await wrapper.findAll('nav button').find(button => button.text() === '项目计划模板')!.trigger('click')
    expect(wrapper.findComponent(ProjectPlanTemplateSettings).exists()).toBe(true)
    await wrapper.setProps({ canManageProjectPlanTemplates: false })
    expect(wrapper.findComponent(ProjectPlanTemplateSettings).exists()).toBe(false)
  })
})
