import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ApprovalWorkflowSettings from '../src/components/ApprovalWorkflowSettings.vue'
import type { PdmSystemSettings } from '../src/types'

describe('ApprovalWorkflowSettings', () => {
  it('shows organization hierarchy sources and saves administrator-managed change reasons', async () => {
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
      releaseChangeReasonTypes: ['设计变更', '客户需求'],
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
    expect(wrapper.findAll('.change-reason-list input').map(input => (input.element as HTMLInputElement).value)).toEqual(['设计变更', '客户需求'])
    await wrapper.findAll('.change-reason-list input')[0].setValue('设计优化')
    await wrapper.findAll('button').find(button => button.text() === '保存发布设置')!.trigger('click')
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ releaseChangeReasonTypes: ['设计优化', '客户需求'] }))
  })
})
