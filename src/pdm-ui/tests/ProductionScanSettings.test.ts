import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProductionScanSettings from '../src/components/ProductionScanSettings.vue'
import * as api from '../src/api'
import { ElOption, ElSelect } from 'element-plus'
const select = (wrapper: ReturnType<typeof mount>, label: string) => wrapper.findAllComponents(ElSelect).find(item => item.props('ariaLabel') === label)!
afterEach(() => vi.restoreAllMocks())
describe('生产扫码设置', () => {
  it('可生成七环节初版流程并编辑流程管理角色，包装PDA尚未指定', async () => {
    const roles = ['生产经理', '生产助理', '技术助理', '机加主管', '机加人员', '质量经理', '质检员', '生产物料员'].map((name, index) => ({ role: `role-${index}`, name }))
    vi.spyOn(api, 'readProductionScanSettings').mockResolvedValue({ companyId: 'company', workstations: [], bindings: [], rowVersion: 0 })
    vi.spyOn(api, 'readProductionScanOptions').mockResolvedValue({ users: [], roles })
    const save = vi.spyOn(api, 'saveProductionScanSettings').mockImplementation(async input => ({ ...input, companyId: 'company', rowVersion: 1, flowMode: 'DepartmentHandover' }))
    const wrapper = mount(ProductionScanSettings, { props: { token: 'token' } }); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '生成初版流程')!.trigger('click')
    expect(wrapper.findAll('.production-scan-settings__node')).toHaveLength(7)
    expect(wrapper.text()).toContain('待配置办理人')
    const control = select(wrapper, '流程管理授权角色')
    expect(control.props('modelValue')).toHaveLength(3)
    control.vm.$emit('update:modelValue', ['role-0', 'role-1']); await flushPromises()
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ flowMode: 'DepartmentHandover', controlRoleCodes: ['role-0', 'role-1'], labelWidthMm: 60, labelHeightMm: 40 }), 'token')
    wrapper.unmount()
  })
  it('无固定账号或角色绑定，可配置工位和角色岗位并保存版本', async () => {
    vi.spyOn(api, 'readProductionScanSettings').mockResolvedValue({ companyId: 'company', workstations: [], bindings: [], rowVersion: 0 })
    vi.spyOn(api, 'readProductionScanOptions').mockResolvedValue({ users: [{ username: 'machine', displayName: '机加员' }], roles: [{ role: 'custom-role', name: '自定义机加角色' }] })
    const save = vi.spyOn(api, 'saveProductionScanSettings').mockImplementation(async input => ({ companyId: 'company', workstations: input.workstations, bindings: input.bindings, rowVersion: 1 }))
    const wrapper = mount(ProductionScanSettings, { props: { token: 'token' } }); await flushPromises()
    expect(wrapper.findAll('.production-scan-settings__binding')).toHaveLength(0)
    await wrapper.findAll('button').find(button => button.text() === '新增工位')!.trigger('click')
    await wrapper.get('[aria-label="第1个工位名称"]').setValue('CNC01')
    await wrapper.get('[aria-label="第1个工位资源组"]').setValue('CNC组')
    await wrapper.findAll('button').find(button => button.text() === '新增绑定')!.trigger('click')
    await wrapper.get('[aria-label="第1条绑定对象类型"]').setValue('Role')
    select(wrapper, '第1条绑定账号或角色').vm.$emit('update:modelValue', ['custom-role']); await flushPromises()
    await wrapper.get('[aria-label="第1条绑定岗位"]').setValue('Machining')
    const stations = select(wrapper, '第1条绑定允许工位')
    stations.vm.$emit('update:modelValue', [stations.findComponent(ElOption).props('value')]); await flushPromises()
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ expectedRowVersion: 0, workstations: [expect.objectContaining({ name: 'CNC01', resourceGroup: 'CNC组' })], bindings: [expect.objectContaining({ subjectType: 'Role', subject: 'custom-role', post: 'Machining' })] }), 'token')
    expect(wrapper.text()).toContain('配置已保存')
    wrapper.unmount()
  })
  it('同一岗位多选角色后保存、回读和取消选择均保留原绑定编号与固定用户', async () => {
    const station = { id: 'station', name: '检验台', resourceGroup: '', isActive: true }
    const entries = [{ id: 'binding-a', subjectType: 'Role' as const, subject: 'a', post: 'Inspection', workstationIds: ['station'], isActive: true },
      { id: 'binding-b', subjectType: 'Role' as const, subject: 'b', post: 'Inspection', workstationIds: ['station'], isActive: true },
      { id: 'packing-user', subjectType: 'User' as const, subject: 'pda', post: 'Packing', workstationIds: ['station'], isActive: true }]
    vi.spyOn(api, 'readProductionScanSettings').mockResolvedValue({ companyId: 'company', rowVersion: 7, workstations: [station], bindings: entries })
    vi.spyOn(api, 'readProductionScanOptions').mockResolvedValue({ users: [{ username: 'pda', displayName: '包装PDA' }], roles: ['a', 'b', 'c'].map(role => ({ role, name: role })) })
    const save = vi.spyOn(api, 'saveProductionScanSettings').mockImplementation(async input => ({ ...input, companyId: 'company', rowVersion: input.expectedRowVersion + 1, flowMode: 'ResourceOperations' }))
    const wrapper = mount(ProductionScanSettings, { props: { token: 'token' } }); await flushPromises()
    expect(wrapper.findAll('.production-scan-settings__binding')).toHaveLength(2)
    expect(select(wrapper, '第1条绑定账号或角色').props('modelValue')).toEqual(['a', 'b'])
    select(wrapper, '第1条绑定账号或角色').vm.$emit('update:modelValue', ['a', 'b', 'c']); await flushPromises()
    await wrapper.get('form').trigger('submit'); await flushPromises()
    const first = save.mock.calls[0]![0]
    expect(first.bindings.slice(0, 2)).toEqual(entries.slice(0, 2)); expect(first.bindings[3]).toEqual(entries[2])
    const added = first.bindings.find(binding => binding.subject === 'c')!
    select(wrapper, '第1条绑定账号或角色').vm.$emit('update:modelValue', ['a', 'c']); await flushPromises()
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save.mock.calls[1]![0].bindings).toEqual([entries[0], added, entries[2]])
    wrapper.unmount()
  })
  it('部门流程可新增、编辑、排序和删除，包装也可按多角色绑定', async () => {
    vi.spyOn(api, 'readProductionScanSettings').mockResolvedValue({ companyId: 'company', rowVersion: 2, flowMode: 'DepartmentHandover', workstations: [{ id: 'station', name: '包装台', resourceGroup: '', isActive: true }], bindings: [] })
    vi.spyOn(api, 'readProductionScanOptions').mockResolvedValue({ users: [], roles: [{ role: 'a', name: '包装角色甲' }, { role: 'b', name: '包装角色乙' }] })
    const save = vi.spyOn(api, 'saveProductionScanSettings').mockImplementation(async input => ({ ...input, companyId: 'company', rowVersion: 3, flowMode: 'DepartmentHandover' }))
    const wrapper = mount(ProductionScanSettings, { props: { token: 'token' } }); await flushPromises()
    const click = async (name: string) => { await wrapper.findAll('button').find(button => button.text() === name)!.trigger('click') }
    await click('新增节点'); await wrapper.get('[aria-label="节点名称"]').setValue('清洗接收'); await wrapper.get('[aria-label="进入阶段"]').setValue('清洗')
    await click('上移'); expect(wrapper.findAll('.production-scan-settings__node strong').map(node => node.text()).indexOf('清洗接收')).toBeLessThan(wrapper.findAll('.production-scan-settings__node strong').map(node => node.text()).indexOf('包装接收'))
    await click('删除节点'); await click('确认删除节点'); expect(wrapper.findAll('.production-scan-settings__node')).toHaveLength(7)
    await wrapper.findAll('.production-scan-settings__node').find(node => node.text().includes('包装接收'))!.trigger('click')
    await click('新增绑定'); await wrapper.get('[aria-label="第1条绑定对象类型"]').setValue('Role')
    select(wrapper, '第1条绑定账号或角色').vm.$emit('update:modelValue', ['a', 'b']); select(wrapper, '第1条绑定允许工位').vm.$emit('update:modelValue', ['station']); await flushPromises()
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save.mock.calls[0]![0].bindings).toEqual(expect.arrayContaining(['a','b'].map(subject => expect.objectContaining({ subjectType: 'Role', subject, post: 'Packing', flowNodeId: '81000000-0000-0000-0000-000000000004' }))))
    wrapper.unmount()
  })
  it('新增工位可保存回读，删除已引用工位须确认并阻止留下无工位绑定', async () => {
    vi.spyOn(api, 'readProductionScanSettings').mockResolvedValue({ companyId: 'company', rowVersion: 0, workstations: [{ id: 'station', name: '接图台', resourceGroup: '', isActive: true }], bindings: [{ id: 'binding', subjectType: 'User', subject: 'user', post: 'Reception', workstationIds: ['station'], isActive: true }] })
    vi.spyOn(api, 'readProductionScanOptions').mockResolvedValue({ users: [{ username: 'user', displayName: '人员' }], roles: [] })
    const save = vi.spyOn(api, 'saveProductionScanSettings').mockImplementation(async input => ({ ...input, companyId: 'company', rowVersion: 1, flowMode: 'ResourceOperations' }))
    const wrapper = mount(ProductionScanSettings, { props: { token: 'token' } }); await flushPromises()
    const click = async (name: string) => { await wrapper.findAll('button').find(button => button.text() === name)!.trigger('click') }
    await click('新增工位'); await wrapper.get('[aria-label="第2个工位名称"]').setValue('CNC二号'); await wrapper.get('[aria-label="第2个工位资源组"]').setValue('CNC组')
    await wrapper.get('form').trigger('submit'); await flushPromises(); expect(save.mock.calls[0]![0].workstations).toHaveLength(2)
    await wrapper.get('[aria-label="删除工位 接图台"]').trigger('click'); expect(wrapper.get('[role="alert"]').text()).toContain('生产签收')
    await click('确认删除工位'); expect(wrapper.findAll('.production-scan-settings__station')).toHaveLength(1)
    await wrapper.get('form').trigger('submit'); await flushPromises(); expect(save).toHaveBeenCalledTimes(1); expect(wrapper.get('[role="alert"]').text()).toContain('重新选择')
    wrapper.unmount()
  })
})
