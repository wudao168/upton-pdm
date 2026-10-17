import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ApprovalWorkflowSettings from '../src/components/ApprovalWorkflowSettings.vue'
import type { OrganizationDirectory, PdmSystemSettings } from '../src/types'

describe('ApprovalWorkflowSettings', () => {
  it('shows fixed specific reasons and saves formal supplement limits by BOM type', async () => {
    const settings = {
      approvalWorkflows: {
        mechanical: { code: 'mechanical-release', name: '机械发布审批', version: 2, steps: [
          { stage: 'MechanicalEngineer', name: '机械工程师自检', assigneeSource: 'Submitter' },
          { stage: 'MainDesigner', name: '主设审核', assigneeSource: 'ProjectDesignLead' },
          { stage: 'MechanicalSupervisor', name: '机械主管批准', assigneeSource: 'PrimaryUnitManager' },
        ] },
        electrical: { code: 'electrical-release', name: '电气发布审批', version: 2, steps: [
          { stage: 'HardwareEngineer', name: '硬件工程师自检', assigneeSource: 'Submitter' },
          { stage: 'HardwareSupervisor', name: '硬件主管审核', assigneeSource: 'PrimaryUnitManager' },
          { stage: 'StandardizationSupervisor', name: '标准化主管批准', assigneeSource: 'ParentUnitManager' },
        ] },
        emergencySubstituteRoleCode: 'BusinessUnitManager',
      },
      formalSupplementPolicies: {
        standard: { maximumCount: 3, validDays: 10 },
        electrical: { maximumCount: 2, validDays: 30 },
      },
    } as PdmSystemSettings
    const onSave = vi.fn().mockResolvedValue(settings)

    const wrapper = mount(ApprovalWorkflowSettings, {
      props: { settings, pending: false, onSave },
    })

    const sourceValues = wrapper.findAll('input:disabled, select').map(input => (input.element as HTMLInputElement | HTMLSelectElement).value)
    expect(sourceValues).toContain('PrimaryUnitManager')
    expect(sourceValues).toContain('ParentUnitManager')
    expect(wrapper.find('.workflow-heading h1').exists()).toBe(false)
    expect(wrapper.find('.workflow-heading p').exists()).toBe(false)
    expect(wrapper.find('.workflow-heading').exists()).toBe(false)
    expect(wrapper.findAll('.workflow-card')).toHaveLength(5)
    expect(wrapper.findAll('.workflow-card').map(card => card.attributes('aria-label'))).toEqual([
      '标准件BOM审批', '非标件BOM与图纸审批', '电气发布审批', '验证计划审批', '料号申请审批',
    ])
    expect(wrapper.find('select').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('指定账号')
    expect(wrapper.get('input[aria-label="物料问题具体原因 物料下单晚"]').element).toHaveProperty('value', '物料下单晚')
    expect(wrapper.get('input[aria-label="客户原因具体原因 客户未及时提供产品"]').element).toHaveProperty('value', '客户未及时提供产品')
    expect(wrapper.find('.change-reason-list input').exists()).toBe(true)
    const standardCount = wrapper.find('.formal-supplement-policy-card input[type="number"]')
    expect((standardCount.element as HTMLInputElement).value).toBe('3')
    await standardCount.setValue(4)
    await wrapper.get('input[aria-label="物料问题具体原因 交期不满足"]').setValue('库存交期不足')
    await wrapper.get('.change-reason-settings button').trigger('click')
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      releaseChangeReasonLabels: expect.objectContaining({ '正式补充': '正式补充', '物料问题 / 交期不满足': '库存交期不足' }),
      formalSupplementPolicies: expect.objectContaining({ standard: { maximumCount: 4, validDays: 10 } }),
    }))
  })

  it('edits BOM chains independently, reorders and removes nodes, and preserves settings after reload', async () => {
    const settings = { approvalWorkflows: {
      mechanical: { code: 'mechanical-release', name: '机械发布审批', version: 4, steps: [
        { stage: 'MechanicalEngineer', name: '机械自检', assigneeSource: 'Submitter' },
        { stage: 'MainDesigner', name: '主设审核', assigneeSource: 'ProjectDesignLead' },
        { stage: 'MechanicalSupervisor', name: '主管批准', assigneeSource: 'PrimaryUnitManager' },
      ] },
      electrical: { code: 'electrical-release', name: '电气发布审批', version: 2, steps: [
        { stage: 'HardwareEngineer', name: '硬件自检', assigneeSource: 'Submitter' },
        { stage: 'HardwareSupervisor', name: '硬件审核', assigneeSource: 'PrimaryUnitManager' },
        { stage: 'StandardizationSupervisor', name: '标准化批准', assigneeSource: 'ParentUnitManager' },
      ] },
      emergencySubstituteRoleCode: 'BusinessUnitManager',
    } } as PdmSystemSettings
    const onSave = vi.fn().mockImplementation(async value => value)
    const wrapper = mount(ApprovalWorkflowSettings, { props: { settings, pending: false, onSave } })
    const standard = wrapper.find('[aria-label="标准件BOM审批"]')
    const nonStandard = wrapper.find('[aria-label="非标件BOM与图纸审批"]')
    await standard.findAll('li')[1]!.find('input').setValue('标准采购审核')
    await standard.find('select').setValue('ParentUnitManager')
    await standard.findAll('[aria-label="删除节点"]')[1]!.trigger('click')
    expect(standard.findAll('li')).toHaveLength(2)
    expect(standard.find('[aria-label="删除节点"]').attributes('disabled')).toBeDefined()
    expect(nonStandard.findAll('li')).toHaveLength(3)
    expect((nonStandard.find('select').element as HTMLSelectElement).value).toBe('ProjectDesignLead')
    await nonStandard.find('[aria-label="下移节点"]').trigger('click')
    expect((nonStandard.find('select').element as HTMLSelectElement).value).toBe('PrimaryUnitManager')
    await wrapper.find('button').trigger('click')
    const saved = onSave.mock.calls[0]![0] as PdmSystemSettings
    expect(saved.approvalWorkflows!.standard!.steps).toHaveLength(2)
    expect(saved.approvalWorkflows!.standard!.steps[1]).toMatchObject({ name: '标准采购审核', assigneeSource: 'ParentUnitManager' })
    expect(saved.approvalWorkflows!.nonStandard).toBe(settings.approvalWorkflows!.nonStandard)
    expect(saved.approvalWorkflows!.mechanical).toEqual(settings.approvalWorkflows!.mechanical)
    expect(saved.approvalWorkflows!.electrical).toEqual(settings.approvalWorkflows!.electrical)
    await wrapper.setProps({ settings: saved })
    expect((standard.find('select').element as HTMLSelectElement).value).toBe('ParentUnitManager')
    await standard.find('.workflow-card-footer button').trigger('click')
    expect(standard.findAll('li')).toHaveLength(3)
    expect(nonStandard.findAll('li')).toHaveLength(3)
  })

  it('selects fixed people and department managers, validates required targets, and clears stale targets', async () => {
    const directory = {
      organizations: [{ id: 'company', name: '测试公司' }],
      units: [
        { id: 'department', organizationId: 'company', name: '研发部', isActive: true },
        { id: 'empty', organizationId: 'company', name: '无负责人部门', isActive: true },
        { id: 'inactive', organizationId: 'company', name: '停用部门', isActive: false },
      ],
      managers: [{ unitId: 'department', primaryManager: 'approver' }], memberships: [],
      users: [
        { username: 'approver', displayName: '张审核', isActive: true },
        { username: 'disabled', displayName: '停用人员', isActive: false },
      ],
    } as unknown as OrganizationDirectory
    const onSave = vi.fn().mockImplementation(async value => value)
    const wrapper = mount(ApprovalWorkflowSettings, { props: { settings: {} as PdmSystemSettings, directory, pending: false, onSave } })
    const standard = wrapper.find('[aria-label="标准件BOM审批"]')
    const electrical = wrapper.find('[aria-label="电气发布审批"]')
    await standard.find('select').setValue('FixedUser')
    await wrapper.find('button').trigger('click')
    expect(onSave).not.toHaveBeenCalled()
    expect(wrapper.find('[role="alert"]').text()).toContain('请选择有效的指定人员')
    expect(standard.find('.workflow-target').text()).toContain('张审核（approver）')
    expect(standard.find('.workflow-target').text()).not.toContain('停用人员')
    await standard.find('.workflow-target select').setValue('approver')
    await electrical.find('select').setValue('FixedUnitManager')
    expect(electrical.find('.workflow-target').text()).toContain('研发部（负责人：张审核） · 测试公司')
    expect(electrical.find('.workflow-target option[value="empty"]').attributes('disabled')).toBeDefined()
    expect(electrical.find('.workflow-target option[value="inactive"]').exists()).toBe(false)
    await electrical.find('button').trigger('click')
    expect(onSave).not.toHaveBeenCalled()
    expect(wrapper.find('[role="alert"]').text()).toContain('请选择已配置有效负责人的部门')
    await electrical.find('.workflow-target select').setValue('department')
    await electrical.find('button').trigger('click')
    const saved = onSave.mock.calls[0]![0] as PdmSystemSettings
    expect(saved.approvalWorkflows!.standard).toBeUndefined()
    expect(saved.approvalWorkflows!.electrical.steps[1]).toMatchObject({ assigneeSource: 'FixedUnitManager', fixedUnitId: 'department' })
    await wrapper.setProps({ settings: saved })
    expect((electrical.find('.workflow-target select').element as HTMLSelectElement).value).toBe('department')
    await standard.find('select').setValue('PrimaryUnitManager')
    await electrical.find('select').setValue('ProjectDesignLead')
    expect(standard.find('.workflow-target').exists()).toBe(false)
    expect(electrical.find('.workflow-target').exists()).toBe(false)
    await wrapper.find('button').trigger('click')
    const changed = onSave.mock.calls[1]![0] as PdmSystemSettings
    expect(changed.approvalWorkflows!.standard!.steps[1]).not.toHaveProperty('fixedAssignee')
    expect(changed.approvalWorkflows!.electrical.steps[1]!.fixedUnitId).toBe('department')
  })
  it('configures validation-plan nodes like BOM approvals and preserves its independent settings', async () => {
    const directory = { users: [{ username: 'reviewer', displayName: '审核员', isActive: true }],
      units: [{ id: 'unit', organizationId: 'company', name: '验证部门', isActive: true }],
      organizations: [{ id: 'company', name: '公司' }], managers: [{ unitId: 'unit', primaryManager: 'reviewer' }], memberships: [] } as unknown as OrganizationDirectory
    const onSave = vi.fn().mockImplementation(async value => value)
    const wrapper = mount(ApprovalWorkflowSettings, { props: { settings: {} as PdmSystemSettings, directory, pending: false, onSave } })
    const plan = wrapper.get('[aria-label="验证计划审批"]')
    expect(plan.findAll('.workflow-node-actions')).toHaveLength(2)
    expect(plan.find('li .workflow-node-assignee input:disabled').exists()).toBe(true)
    await plan.find('select[aria-label="审批人来源"]').setValue('FixedUser')
    await plan.find('button').trigger('click')
    expect(onSave).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('验证计划审批')
    await plan.get('select[aria-label="指定人员"]').setValue('reviewer')
    await plan.findAll('select[aria-label="审批人来源"]')[1]!.setValue('FixedUnitManager')
    await plan.get('select[aria-label="指定部门"]').setValue('unit')
    await plan.findAll('li')[1]!.get('[aria-label="下移节点"]').trigger('click')
    expect(plan.findAll('li')[1]!.get('select[aria-label="指定部门"]').element).toHaveProperty('value', 'unit')
    await plan.findAll('li')[2]!.get('[aria-label="删除节点"]').trigger('click')
    expect(plan.findAll('li')).toHaveLength(2)
    expect(plan.get('[aria-label="删除节点"]').attributes('disabled')).toBeDefined()
    await plan.get('.workflow-card-footer button').trigger('click')
    expect(plan.findAll('li')).toHaveLength(3)
    await plan.find('button').trigger('click')
    const saved = onSave.mock.calls[0]![0] as PdmSystemSettings
    expect(saved.approvalWorkflows!.validationPlan!.steps[1]).toMatchObject({ stage: 'MechanicalSupervisor', assigneeSource: 'FixedUnitManager', fixedUnitId: 'unit' })
    expect(saved.approvalWorkflows!.standard).toBeUndefined()
    await wrapper.setProps({ settings: saved })
    expect(plan.get('select[aria-label="指定部门"]').element).toHaveProperty('value', 'unit')
  })

  it('saves independent emergency roles and a material role-pool workflow', async () => {
    const onSave = vi.fn().mockImplementation(async value => value)
    const wrapper = mount(ApprovalWorkflowSettings, { props: { settings: {} as PdmSystemSettings, pending: false, onSave } })
    const standard = wrapper.get('[aria-label="标准件BOM审批"]')
    const electrical = wrapper.get('[aria-label="电气发布审批"]')
    const material = wrapper.get('[aria-label="料号申请审批"]')
    await standard.get('.workflow-emergency select').setValue('Administrator')
    await electrical.get('.workflow-emergency select').setValue('')
    await material.get('.workflow-emergency select').setValue('BusinessUnitManager')
    expect(material.get('.workflow-role-choices').text()).toContain('审批角色')
    await material.get('.workflow-card-footer button').trigger('click')
    expect(material.findAll('li')).toHaveLength(3)
    await material.findAll('[aria-label="上移节点"]')[1]!.trigger('click')
    await material.find('button').trigger('click')
    const saved = onSave.mock.calls[0]![0] as PdmSystemSettings
    expect(saved.approvalWorkflows!.standard).toBeUndefined()
    expect(saved.approvalWorkflows!.electrical.emergencyRoleCodes).toBeUndefined()
    expect(saved.approvalWorkflows!.materialCodeApplication!.emergencyRoleCodes).toEqual(['BusinessUnitManager'])
    expect(saved.approvalWorkflows!.materialCodeApplication!.steps).toHaveLength(3)
    expect(saved.approvalWorkflows!.materialCodeApplication!.steps[2]).toMatchObject({ assigneeSource: 'Role', roleCodes: ['ProcessReviewer', 'Approver'] })
    await wrapper.setProps({ settings: saved })
    expect(material.get('.workflow-emergency select').element).toHaveProperty('value', 'BusinessUnitManager')
  })

  it('switches sections through tabs without losing unsaved workflow edits', async () => {
    const onSave = vi.fn().mockImplementation(async value => value)
    const wrapper = mount(ApprovalWorkflowSettings, { props: { settings: {} as PdmSystemSettings, pending: false, onSave } })
    const standard = wrapper.get('[aria-label="标准件BOM审批"]')
    await standard.findAll('li')[1]!.find('input').setValue('保留未保存审核名称')
    await wrapper.get('[role="tab"][id="tab-electrical-release"]').trigger('click')
    expect(wrapper.get('#pane-electrical-release').isVisible()).toBe(true)
    expect(wrapper.get('#pane-standard-release').isVisible()).toBe(false)
    await wrapper.get('[role="tab"][id="tab-change-rules"]').trigger('click')
    expect(wrapper.get('#pane-change-rules').isVisible()).toBe(true)
    await wrapper.get('[role="tab"][id="tab-standard-release"]').trigger('click')
    expect(standard.findAll('li')[1]!.find('input').element).toHaveProperty('value', '保留未保存审核名称')
    wrapper.unmount()
  })

  it('saves only one workflow while retaining other unsaved drafts after the parent updates', async () => {
    let wrapper: ReturnType<typeof mount>
    const onSave = vi.fn().mockImplementation(async settings => {
      await wrapper.setProps({ settings })
      return settings
    })
    wrapper = mount(ApprovalWorkflowSettings, { props: { settings: {} as PdmSystemSettings, pending: false, onSave } })
    const standard = wrapper.get('[aria-label="标准件BOM审批"]')
    const electrical = wrapper.get('[aria-label="电气发布审批"]')
    await standard.findAll('li')[1]!.find('input').setValue('标准已保存')
    await electrical.find('select').setValue('FixedUser')
    await standard.find('button').trigger('click'); await flushPromises()
    expect(onSave).toHaveBeenCalledTimes(1)
    expect(onSave.mock.calls[0]![0].approvalWorkflows.electrical.steps[1].assigneeSource).toBe('PrimaryUnitManager')
    expect(electrical.find('select').element).toHaveProperty('value', 'FixedUser')
    expect(standard.findAll('li')[1]!.find('input').element).toHaveProperty('value', '标准已保存')
    wrapper.unmount()
  })

  it('adds and removes reasons and saves their stable keys and display labels', async () => {
    const onSave = vi.fn().mockImplementation(async value => value)
    const wrapper = mount(ApprovalWorkflowSettings, { props: { settings: {} as PdmSystemSettings, pending: false, onSave } })
    const group = wrapper.findAll('.change-reason-list section')[1]!
    await group.get('button[aria-label="删除物料问题原因 交期不满足"]').trigger('click')
    await group.findAll('button').find(b => b.text() === '新增原因')!.trigger('click')
    const added = group.findAll('input').at(-1)!
    await added.setValue('临时库存替代')
    await wrapper.get('.change-reason-settings header button').trigger('click'); await flushPromises()
    const saved = onSave.mock.lastCall![0] as PdmSystemSettings
    expect(saved.releaseChangeReasonTypes).not.toContain('物料问题 / 交期不满足')
    const key = saved.releaseChangeReasonTypes!.find(key => key.startsWith('物料问题 / custom-'))!
    expect(saved.releaseChangeReasonLabels![key]).toBe('临时库存替代')
    await wrapper.setProps({ settings: saved })
    expect(group.findAll('input').some(input => (input.element as HTMLInputElement).value === '临时库存替代')).toBe(true)
    wrapper.unmount()
  })

  it('persists reason order and keeps move controls within group bounds', async () => {
    const onSave = vi.fn().mockImplementation(async value => value)
    const wrapper = mount(ApprovalWorkflowSettings, { props: { settings: {} as PdmSystemSettings, pending: false, onSave } })
    const group = wrapper.findAll('.change-reason-list section')[1]!
    expect(group.get('[aria-label="上移物料问题原因 交期不满足"]').attributes('disabled')).toBeDefined()
    expect(group.get('[aria-label="下移物料问题原因 物料漏买"]').attributes('disabled')).toBeDefined()
    await group.get('[aria-label="下移物料问题原因 交期不满足"]').trigger('click')
    expect(group.findAll('input').map(input => (input.element as HTMLInputElement).value)).toEqual(['物料下单晚', '交期不满足', '买错物料', '物料漏买'])
    await group.get('[aria-label="上移物料问题原因 买错物料"]').trigger('click')
    await wrapper.get('.change-reason-settings header button').trigger('click'); await flushPromises()
    const saved = onSave.mock.lastCall![0] as PdmSystemSettings
    expect(saved.releaseChangeReasonTypes!.filter(key => key.startsWith('物料问题 / '))).toEqual(['物料问题 / 物料下单晚', '物料问题 / 买错物料', '物料问题 / 交期不满足', '物料问题 / 物料漏买'])
    await wrapper.setProps({ settings: saved })
    expect(group.findAll('input')[1]!.element).toHaveProperty('value', '买错物料')
    wrapper.unmount()
  })

})
