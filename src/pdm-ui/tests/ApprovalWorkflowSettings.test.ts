import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ApprovalWorkflowSettings from '../src/components/ApprovalWorkflowSettings.vue'
import type { PdmSystemSettings } from '../src/types'

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

    const sourceValues = wrapper.findAll('input:disabled').map(input => (input.element as HTMLInputElement).value)
    expect(sourceValues).toContain('提交人主部门负责人')
    expect(sourceValues).toContain('上级部门负责人')
    expect(wrapper.text()).toContain('组织调整不影响在途审批')
    expect(wrapper.find('select').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('指定账号')
    expect(wrapper.text()).toContain('物料下单晚')
    expect(wrapper.text()).toContain('客户未及时提供产品')
    expect(wrapper.find('.change-reason-list input').exists()).toBe(false)
    const standardCount = wrapper.find('.formal-supplement-policy-card input[type="number"]')
    expect((standardCount.element as HTMLInputElement).value).toBe('3')
    await standardCount.setValue(4)
    await wrapper.findAll('button').find(button => button.text() === '保存发布设置')!.trigger('click')
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      releaseChangeReasonTypes: expect.arrayContaining(['正式补充', '物料问题 / 交期不满足', '设计问题 / 设计错误', '其他']),
      formalSupplementPolicies: expect.objectContaining({ standard: { maximumCount: 4, validDays: 10 } }),
    }))
  })
})
