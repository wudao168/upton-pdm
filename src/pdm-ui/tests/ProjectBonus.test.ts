import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import ProjectBonusManager from '../src/components/ProjectBonusManager.vue'
import ProjectBonusSettings from '../src/components/ProjectBonusSettings.vue'
import ApprovalFlowCanvas from '../src/components/ApprovalFlowCanvas.vue'
import ProjectBonusApprovalEditor from '../src/components/ProjectBonusApprovalEditor.vue'
import { canSignNode } from '../src/approvalParticipants'
const api = vi.hoisted(() => ({ getProjectBonus: vi.fn(), saveProjectBonus: vi.fn(), scoreProjectBonus: vi.fn(), actProjectBonus: vi.fn(), payProjectBonus: vi.fn(), getProjectBonusHistory: vi.fn(), getProjectBonusSettings: vi.fn(), saveProjectBonusSettings: vi.fn() }))
vi.mock('../src/api', () => api)
const indicator = { id: 'quality', name: '设计质量', maxScore: 100, weight: 100, guidance: '按评审与检验记录评分', evidence: '提供评审编号', departments: [{ departmentKey: 'technical', weight: 100 }] }
const settings = () => ({ version: 3, enabled: true, departments: [{ key: 'technical', name: '技术部', unitId: 'dept', reviewers: ['reviewer'] }], roles: [{ id: 'mechanical', name: '机械设计', phase: 'Shipment', suggestedWeight: 18, indicators: [indicator] }], projectIndicators: [], approvalNodes: [{ id: 'approval', name: '部门审批', departmentKeys: ['technical'], parallel: false }], paymentDepartmentKey: 'technical' })
const view = () => ({ projectId: 'p1', projectCode: 'P70001', projectName: '奖金验收', rowVersion: 2, pool: 10000, frozen: false, canCreate: true, actor: 'manager', applications: [], settings: settings(), users: [{ username: 'worker', name: '张工' }] })
beforeEach(() => { vi.clearAllMocks(); api.getProjectBonus.mockResolvedValue(view()); api.saveProjectBonus.mockResolvedValue(view()); api.getProjectBonusSettings.mockResolvedValue({ settings: settings(), canEdit: true, users: [{ username: 'reviewer', name: '负责人' }], departments: [{ id: 'dept', name: '技术部' }] }) })
describe('project bonus', () => {
  it('keeps an editable manual weight draft across token renewal and submits the current version', async () => {
    const wrapper = mount(ProjectBonusManager, { props: { projectId: 'p1', token: 'first' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(b => b.text() === '发起发货奖金申请')!.trigger('click')
    await wrapper.findAll('button').find(b => b.text() === '添加参与人员')!.trigger('click')
    const weight = wrapper.get('.el-input-number input'); await weight.setValue('25')
    await wrapper.setProps({ token: 'renewed' }); await flushPromises()
    expect(api.getProjectBonus).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.el-input-number__increase').exists()).toBe(false)
    expect((weight.element as HTMLInputElement).value).toContain('25')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(api.saveProjectBonus).toHaveBeenCalledWith('p1', 'Shipment', expect.objectContaining({ expectedRowVersion: 2, startScoring: false, people: [expect.objectContaining({ weight: 25 })] }), 'renewed')
    wrapper.unmount()
  })
  it('loads role presets as editable records and sends edited template data', async () => {
    api.saveProjectBonusSettings.mockResolvedValue({ settings: { ...settings(), version: 4 }, canEdit: true, users: [], departments: [] })
    const wrapper = mount(ProjectBonusSettings, { props: { token: 't', companyId: 'company' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get('input[maxlength="80"]').setValue('机械技术负责人')
    await wrapper.findAll('button').find(b => b.text() === '保存规则')!.trigger('click'); await flushPromises()
    expect(api.saveProjectBonusSettings).toHaveBeenCalledWith(expect.objectContaining({ version: 3, roles: [expect.objectContaining({ name: '机械技术负责人' })] }), 't')
    wrapper.unmount()
  })
  it('does not lose the second department task after the same person signed the first', () => {
    const departments = [{ unitId: 'a', name: '技术', members: ['reviewer'], requireAll: false, complete: true, signatures: [{ actor: 'reviewer', signedAt: '2026-11-08' }] }, { unitId: 'b', name: '质量', members: ['reviewer'], requireAll: false, complete: false, signatures: [] }]
    expect(canSignNode({ assignee: 'reviewer', participants: { departments, members: ['reviewer'], requireAll: true, signatures: departments[0]!.signatures } }, 'reviewer')).toBe(true)
    departments[1]!.complete = true
    expect(canSignNode({ assignee: 'reviewer', participants: { departments, members: ['reviewer'], requireAll: true, signatures: departments[0]!.signatures } }, 'reviewer')).toBe(false)
  })
  it('edits, inserts and reorders bonus approvals on the shared canvas and reloads the saved department routing', async () => {
    const configured = { ...settings(), departments: [...settings().departments, { key: 'finance', name: '财务部', unitId: 'finance', reviewers: ['finance'] }] }
    api.getProjectBonusSettings.mockResolvedValue({ settings: configured, canEdit: true, users: [], departments: [] })
    api.saveProjectBonusSettings.mockImplementation(async value => ({ settings: { ...JSON.parse(JSON.stringify(value)), version: 4 }, canEdit: true, users: [], departments: [] }))
    const options = { props: { token: 't', companyId: 'company' }, global: { plugins: [ElementPlus] } }
    const wrapper = mount(ProjectBonusSettings, options); await flushPromises()
    await wrapper.get('[role="tablist"]').findAll('button').find(button => button.text() === '审批与发放')!.trigger('click')
    const canvas = wrapper.getComponent(ApprovalFlowCanvas)
    expect(canvas.props('allowConditions')).toBe(false)
    canvas.vm.$emit('insert', canvas.props('flow').layout, 2, 'Approval'); await flushPromises()
    await wrapper.get('[aria-label="节点名称"]').setValue('联合会签')
    await wrapper.get('[aria-label="审批人来源"]').setValue('parallel')
    await wrapper.findAll('button').find(button => button.text() === '添加并行部门')!.trigger('click')
    await wrapper.get('[aria-label="并行审批部门"]').setValue('technical')
    await wrapper.findAll('button').find(button => button.text() === '添加并行部门')!.trigger('click')
    await wrapper.findAll('[aria-label="并行审批部门"]')[1]!.setValue('finance')
    await wrapper.get('[aria-label="上移节点"]').trigger('click')
    expect(canvas.props('flow').steps.map(step => step.name)).toEqual(['项目经理确认', '联合会签', '部门审批'])
    await wrapper.findAll('button').find(button => button.text() === '保存规则')!.trigger('click'); await flushPromises()
    const saved = api.saveProjectBonusSettings.mock.calls[0]![0]
    expect(saved.approvalNodes).toEqual([
      { id: expect.any(String), name: '联合会签', parallel: true, departmentKeys: ['technical', 'finance'] },
      configured.approvalNodes[0],
    ])
    expect(saved).not.toHaveProperty('layout')
    api.getProjectBonusSettings.mockResolvedValue({ settings: JSON.parse(JSON.stringify(saved)), canEdit: true, users: [], departments: [] })
    wrapper.unmount()
    const reloaded = mount(ProjectBonusSettings, options); await flushPromises()
    await reloaded.get('[role="tablist"]').findAll('button').find(button => button.text() === '审批与发放')!.trigger('click')
    expect(reloaded.getComponent(ApprovalFlowCanvas).props('descriptions')[1]).toContain('技术部 / 财务部 · 并行全部通过')
    reloaded.unmount()
  })
  it('keeps confirmation first during drag sorting and protects the last approval node', async () => {
    const approvals = [{ id: 'a', name: '技术审核', departmentKeys: ['technical'], parallel: false }, { id: 'b', name: '财务审核', departmentKeys: ['technical'], parallel: false }]
    const wrapper = mount(ProjectBonusApprovalEditor, { props: { modelValue: approvals, departments: settings().departments, version: 1,
      'onUpdate:modelValue': value => wrapper.setProps({ modelValue: value }) }, global: { plugins: [ElementPlus] } })
    const canvas = wrapper.getComponent(ApprovalFlowCanvas)
    canvas.vm.$emit('moveNode', 'BonusReview:b', canvas.props('flow').layout, 0); await flushPromises()
    expect(canvas.props('flow').steps.map(step => step.name)).toEqual(['项目经理确认', '技术审核', '财务审核'])
    canvas.vm.$emit('moveNode', 'BonusReview:b', canvas.props('flow').layout, 1); await flushPromises()
    expect(canvas.props('flow').steps.map(step => step.name)).toEqual(['项目经理确认', '财务审核', '技术审核'])
    await wrapper.get('[aria-label="删除节点"]').trigger('click'); await flushPromises()
    expect(wrapper.get('[aria-label="删除节点"]').attributes('disabled')).toBeDefined()
    expect(canvas.props('flow').steps).toHaveLength(2)
    canvas.vm.$emit('select', 'MechanicalEngineer'); await flushPromises()
    expect(wrapper.text()).toContain('此为固定发起节点')
    expect(wrapper.find('[aria-label="删除节点"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
