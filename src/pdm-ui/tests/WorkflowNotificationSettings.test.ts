import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ElementPlus from 'element-plus'
import WorkflowNotificationSettings from '../src/components/WorkflowNotificationSettings.vue'
import ApprovalWorkflowSettings from '../src/components/ApprovalWorkflowSettings.vue'
import type { OrganizationDirectory, PdmSystemSettings, RolePermissionSettings } from '../src/types'
import type { WorkflowNotificationCatalog } from '../src/workflowNotifications'

const api = vi.hoisted(() => ({ readWorkflowNotifications: vi.fn(), saveWorkflowNotification: vi.fn(), readWorkflowNotificationRecords: vi.fn() }))
vi.mock('../src/api', () => api)
const directory = {
  organizations: [{ id: 'company', name: '当前公司', isActive: true }],
  units: [{ id: 't1', name: 'T1事业部', organizationId: 'company', isActive: true }, { id: 'supply', name: '供应链', organizationId: 'company', isActive: true }, { id: 'foreign', name: '其他公司部门', organizationId: 'other', isActive: true }, { id: 'inactive', name: '停用部门', organizationId: 'company', isActive: false }, { id: 'hidden-child', name: '停用部门下级', organizationId: 'company', parentUnitId: 'inactive', isActive: true }],
  memberships: [], managers: [], users: [{ username: 'assistant', displayName: '技术助理甲', companyId: 'company', isActive: true }, { username: 'supply-user', displayName: '供应链主管', companyId: 'company', isActive: true }, { username: 'foreign-user', displayName: '外公司人员', companyId: 'other', isActive: true }]
} as unknown as OrganizationDirectory
function data(): WorkflowNotificationCatalog {
  return { companyId: 'company', rowVersion: 3, positions: [], assignments: [], flows: [{ flowCode: 'standard-release', enabled: true, recipients: [{ departmentSource: 'SubmitterDepartment', departmentId: null, roleCode: 'TechnicalAssistant' }] }] }
}
const roles = [{ role: 'TechnicalAssistant', name: '技术助理' }, { role: 'SupplyChain', name: '供应链' }, { role: 'custom-manager', name: '供应链主管' }] as RolePermissionSettings[]
const props = { roles, token: 'token', companyId: 'company', flowCode: 'standard-release', directory, active: true }
describe('department and role workflow notifications', () => {
  beforeEach(() => {
    vi.clearAllMocks(); api.readWorkflowNotifications.mockResolvedValue(data()); api.saveWorkflowNotification.mockResolvedValue({ ...data(), rowVersion: 4 }); api.readWorkflowNotificationRecords.mockResolvedValue([])
  })
  it('shows all system roles for submitter and fixed departments without a separate position catalog', async () => {
    const w = mount(WorkflowNotificationSettings, { props }); await flushPromises()
    expect(w.get('[aria-label="接收对象1部门"]').element).toHaveProperty('value', '__submitter_department__')
    const role = w.get('[aria-label="接收对象1角色"]')
    expect(role.text()).toContain('技术助理'); expect(role.text()).toContain('供应链主管'); expect(w.text()).not.toContain('岗位')
    await w.get('[aria-label="接收对象1部门"]').setValue('supply')
    expect(role.element).toHaveProperty('value', 'TechnicalAssistant')
    await role.setValue('custom-manager')
    await w.findAll('button').find(button => button.text() === '保存通知设置')!.trigger('click'); await flushPromises()
    expect(api.saveWorkflowNotification).toHaveBeenCalledWith('standard-release', expect.objectContaining({ recipients: [{ departmentSource: 'Fixed', departmentId: 'supply', roleCode: 'custom-manager' }] }), 'token')
    w.unmount()
  })
  it('saves submitter, fixed department and all-company role recipients together through a separate API', async () => {
    const w = mount(WorkflowNotificationSettings, { props }); await flushPromises()
    await w.findAll('button').find(button => button.text().includes('添加接收对象'))!.trigger('click')
    const department = w.get('[aria-label="接收对象2部门"]')
    expect(department.text()).toContain('不限部门'); expect(department.text()).not.toContain('其他公司部门'); expect(department.text()).not.toContain('停用部门下级')
    expect(w.get('[aria-label="接收对象2角色"]').attributes('disabled')).toBeUndefined()
    await w.get('[aria-label="接收对象2角色"]').setValue('TechnicalAssistant')
    await w.findAll('button').find(button => button.text().includes('添加接收对象'))!.trigger('click')
    await w.get('[aria-label="接收对象3部门"]').setValue('supply'); await w.get('[aria-label="接收对象3角色"]').setValue('SupplyChain')
    await w.findAll('button').find(button => button.text() === '保存通知设置')!.trigger('click'); await flushPromises()
    expect(api.saveWorkflowNotification).toHaveBeenCalledWith('standard-release', { companyId: 'company', enabled: true, expectedRowVersion: 3, recipients: [{ departmentSource: 'SubmitterDepartment', departmentId: null, roleCode: 'TechnicalAssistant' }, { departmentSource: 'CompanyRole', departmentId: null, roleCode: 'TechnicalAssistant' }, { departmentSource: 'Fixed', departmentId: 'supply', roleCode: 'SupplyChain' }] }, 'token')
    expect(w.text()).toContain('审批流程保持不变'); w.unmount()
  })
  it('validates missing roles and preserves form after conflicts', async () => {
    const w = mount(WorkflowNotificationSettings, { props }); await flushPromises()
    await w.findAll('button').find(button => button.text().includes('添加接收对象'))!.trigger('click')
    await w.findAll('button').find(button => button.text() === '保存通知设置')!.trigger('click')
    expect(api.saveWorkflowNotification).not.toHaveBeenCalled(); expect(w.get('[role="alert"]').text()).toContain('选择角色')
    await w.get('[aria-label="接收对象2角色"]').setValue('SupplyChain')
    api.saveWorkflowNotification.mockRejectedValue(new Error('通知设置已被其他人修改，请刷新后重试。'))
    await w.findAll('button').find(button => button.text() === '保存通知设置')!.trigger('click'); await flushPromises()
    expect(w.get('[role="alert"]').text()).toContain('已被其他人修改'); expect(w.get('[aria-label="接收对象2角色"]').element).toHaveProperty('value', 'SupplyChain'); w.unmount()
  })
  it('does not silently translate legacy appointments into different role recipients', async () => {
    const legacy = data(); legacy.flows[0]!.recipients = [{ departmentSource: 'Fixed', departmentId: 't1', positionId: 'legacy-post' }]
    api.readWorkflowNotifications.mockResolvedValue(legacy)
    const w = mount(WorkflowNotificationSettings, { props }); await flushPromises()
    expect(w.get('[role="alert"]').text()).toContain('重新选择角色'); expect(w.get('[aria-label="接收对象1角色"]').element).toHaveProperty('value', '')
    await w.findAll('button').find(button => button.text() === '保存通知设置')!.trigger('click'); expect(api.saveWorkflowNotification).not.toHaveBeenCalled(); w.unmount()
  })
  it('reloads all-company role rows and rejects deleted roles', async () => {
    const company = data(); company.flows[0]!.recipients = [{ departmentSource: 'CompanyRole', departmentId: null, roleCode: 'deleted-role' }]
    api.readWorkflowNotifications.mockResolvedValue(company)
    const w = mount(WorkflowNotificationSettings, { props }); await flushPromises()
    expect(w.get('[aria-label="接收对象1部门"]').element).toHaveProperty('value', ''); expect(w.get('[aria-label="接收对象1角色"]').text()).toContain('原角色已失效')
    await w.findAll('button').find(button => button.text() === '保存通知设置')!.trigger('click'); expect(api.saveWorkflowNotification).not.toHaveBeenCalled(); w.unmount()
  })
  it('places approval and notification settings in sibling panels and saves notifications independently', async () => {
    const settings = { approvalWorkflows: { mechanical: { code: 'mechanical-release', version: 1, name: '机械审批', steps: [{ stage: 'MechanicalEngineer', name: '自检', assigneeSource: 'Submitter' }, { stage: 'MainDesigner', name: '主设审核', assigneeSource: 'ProjectDesignLead' }] }, electrical: { code: 'electrical-release', version: 1, name: '电气审批', steps: [{ stage: 'HardwareEngineer', name: '自检', assigneeSource: 'Submitter' }, { stage: 'HardwareSupervisor', name: '主管审核', assigneeSource: 'PrimaryUnitManager' }] }, emergencySubstituteRoleCode: 'BusinessUnitManager' } } as PdmSystemSettings
    const before = JSON.stringify(settings), onSave = vi.fn()
    const w = mount(ApprovalWorkflowSettings, { props: { settings, pending: false, onSave, roles, token: 'token', companyId: 'company', directory }, global: { plugins: [ElementPlus] } }); await flushPromises()
    const card = w.findAll('.workflow-card')[0]!
    expect(card.find('.workflow-steps').exists()).toBe(true)
    expect(card.find('.workflow-notifications').exists()).toBe(false)
    const layouts = w.findAll('.workflow-layout')
    expect(layouts).toHaveLength(5)
    for (const layout of layouts) {
      expect(layout.find(':scope > .workflow-card').exists()).toBe(true)
      expect(layout.find(':scope > .workflow-notification-card').exists()).toBe(true)
    }
    const notification = layouts[0]!.get('.workflow-notifications')
    await notification.findAll('button').find(button => button.text() === '保存通知设置')!.trigger('click'); await flushPromises()
    expect(onSave).not.toHaveBeenCalled(); expect(JSON.stringify(settings)).toBe(before)
    w.unmount()
  })
})
