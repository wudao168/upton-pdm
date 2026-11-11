import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectApplications from '../src/components/ProjectApplications.vue'
import * as api from '../src/api'
import type { ProjectApplication, ProjectApplicationDetail, ProjectApplicationView } from '../src/projectApplications'
import { budgetCategories } from '../src/projectBudget'
import ProjectApplicationSettlement from '../src/components/ProjectApplicationSettlement.vue'
import QuantityInputNumber from '../src/components/QuantityInputNumber.vue'

vi.mock('../src/api', () => ({
  PdmApiError: class extends Error {}, readProjectApplications: vi.fn(), readProjectApplication: vi.fn(),
  saveProjectApplication: vi.fn(), actProjectApplication: vi.fn(), approveProjectApplication: vi.fn(),
}))

const app: ProjectApplication = { id: 'app-1', number: 'LX20261019-001', companyId: 'company', applicant: 'assistant', applicantName: '销售助理甲', status: '待审批', revision: 1,
  data: { canViewBudget: true, input: { customerId: null, customerName: '新客户', projectName: '提前启动设备', orderDate: null, deliveryDate: '2026-12-30', salesUsername: 'sales', presalesUsernames: ['pre1', 'pre2'], remarks: '合同待签', isSpecial: true,
    budgetAmounts: Object.fromEntries(budgetCategories.map(category => [category.key, 0])) },
    salesName: '销售甲', presalesPeople: [{ username: 'pre1', displayName: '售前甲' }, { username: 'pre2', displayName: '售前乙' }] },
  rowVersion: 1, createdAt: '2026-10-19T02:00:00Z', updatedAt: '2026-10-19T02:00:00Z', projectId: null, projectCode: null }
const view: ProjectApplicationView = { canViewBudget: true, applications: [app], canApply: true, canApprove: true, people: [{ username: 'sales', displayName: '销售甲' }, { username: 'sales2', displayName: '销售乙' }, { username: 'pre3', displayName: '售前丙' }, ...app.data.presalesPeople],
  customers: [{ id: 'customer-1', code: 'C001', name: '正式客户', isActive: true, sourceSystem: 'u9c' }],
  numberingOptions: { organizations: [], projectTypes: [{ code: 'P', name: '标准项目', isActive: true }], equipmentTypes: [{ code: 2, name: '设备', isActive: true }] } }
const detail: ProjectApplicationDetail = { application: app, canEdit: true, canWithdraw: true, canApprove: true,
  history: [{ id: 'event-1', revision: 1, action: '提交申请', actor: 'assistant', actorName: '销售助理甲', at: app.createdAt, data: app.data, comment: null, approval: null, projectId: null, projectCode: null }] }
const wrappers: ReturnType<typeof mount>[] = []
function render() { const wrapper = mount(ProjectApplications, { props: { token: 'token', active: true }, attachTo: document.body, global: { plugins: [ElementPlus], stubs: { ElDialog: { props: ['modelValue'], template: '<div v-if="modelValue"><slot /><slot name="footer" /></div>' } } } }); wrappers.push(wrapper); return wrapper }

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.readProjectApplications).mockResolvedValue(structuredClone(view))
  vi.mocked(api.readProjectApplication).mockResolvedValue(structuredClone(detail))
  vi.mocked(api.saveProjectApplication).mockResolvedValue(structuredClone(app))
})
afterEach(() => { wrappers.forEach(x => x.unmount()); wrappers.length = 0; document.body.innerHTML = '' })

describe('立项申请', () => {
  it('无预算权限时隐藏录入及历史金额，仍可提交完整业务资料', async () => {
    const redacted = structuredClone(app)
    redacted.data.canViewBudget = false
    redacted.data.input.budgetAmounts = null
    vi.mocked(api.readProjectApplications).mockResolvedValue({ ...view, canApprove: false, canViewBudget: false, applications: [redacted] })
    vi.mocked(api.readProjectApplication).mockResolvedValue({ ...detail, application: redacted, history: [{ ...detail.history[0]!, data: redacted.data }] })
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '查看详情')!.trigger('click'); await flushPromises()
    expect(wrapper.find('[aria-label="立项预算"]').exists()).toBe(false)
    await wrapper.findAll('button').find(x => x.text() === '修改申请')!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('当前账号不展示预算金额')
    expect(wrapper.findAllComponents({ name: 'ProjectApplicationBudget' })).toHaveLength(0)
    await wrapper.findAll('button').find(x => x.text() === '保存修改并重新提交')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).toHaveBeenCalledWith('app-1', expect.objectContaining({ submit: true, data: expect.objectContaining({ budgetAmounts: null }) }), 'token')
  })
  it('半填结算可存草稿并重新编辑，补齐后才允许提交且保留原快照', async () => {
    const draft = structuredClone(app)
    draft.status = '草稿'
    draft.data.input.settlementLines = [{ content: '合同设备', unit: '项', quantity: 2, unitPrice: null, note: null }]
    vi.mocked(api.readProjectApplications).mockResolvedValue({ ...structuredClone(view), applications: [draft] })
    vi.mocked(api.readProjectApplication).mockResolvedValue({ ...structuredClone(detail), application: draft, canApprove: false, canWithdraw: false })
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '查看详情')!.trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '修改申请')!.trigger('click'); await flushPromises()
    const editor = () => wrapper.findAllComponents(ProjectApplicationSettlement).find(component => !component.props('readonly'))!
    expect(editor().props('modelValue')).toEqual(draft.data.input.settlementLines)
    await wrapper.findAll('button').find(x => x.text() === '提交申请')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).not.toHaveBeenCalled()
    await wrapper.findAll('button').find(x => x.text() === '保存草稿')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).toHaveBeenCalledWith('app-1', expect.objectContaining({ submit: false, data: expect.objectContaining({ settlementLines: draft.data.input.settlementLines }) }), 'token')
    await wrapper.findAll('button').find(x => x.text() === '修改申请')!.trigger('click'); await flushPromises()
    editor().vm.$emit('update:modelValue', [{ ...draft.data.input.settlementLines![0]!, unitPrice: 100.25 }]); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '提交申请')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).toHaveBeenLastCalledWith('app-1', expect.objectContaining({ submit: true, data: expect.objectContaining({ settlementLines: [expect.objectContaining({ quantity: 2, unitPrice: 100.25 })] }) }), 'token')
    expect(draft.data.input.settlementLines![0]!.unitPrice).toBeNull()
  })
  it('预算草稿可留空，提交须补齐，输入不会改写原申请快照', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '引用并审批')!.trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '修改申请')!.trigger('click'); await flushPromises()
    const inputs = wrapper.findAllComponents({ name: 'ElInputNumber' }).filter(input => input.element.closest('.application-budget'))
    expect(inputs).toHaveLength(11)
    inputs[0]!.vm.$emit('update:modelValue', null)
    await wrapper.findAll('button').find(x => x.text() === '保存修改并重新提交')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).not.toHaveBeenCalled()
    inputs[0]!.vm.$emit('update:modelValue', 100.25)
    await flushPromises()
    inputs[6]!.vm.$emit('update:modelValue', 200.5)
    await flushPromises()
    expect(wrapper.get('.application-create-form .application-budget__total').text()).toContain('300.75')
    await wrapper.findAll('button').find(x => x.text() === '保存修改并重新提交')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).toHaveBeenCalledWith('app-1', expect.objectContaining({ data: expect.objectContaining({ budgetAmounts: expect.objectContaining({ Standard: 100.25, MechanicalDesign: 200.5 }) }) }), 'token')
    expect(app.data.input.budgetAmounts!.Standard).toBe(0)
  })
  it('展示特殊立项、空订单日期及可筛选列表', async () => {
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('.application-table').text()).toContain('特殊立项')
    expect(wrapper.get('.application-table').text()).toContain('未填写')
    await wrapper.get('[aria-label="搜索立项申请"]').setValue('不匹配')
    expect(wrapper.get('.application-table').text()).toContain('没有符合条件的申请')
  })
  it('申请表支持特殊立项、多位售前工程师及空订单日期提交', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '新建申请')!.trigger('click'); await flushPromises()
    const selects = wrapper.findAllComponents({ name: 'ElSelect' })
    selects[0]!.vm.$emit('update:modelValue', '新客户')
    selects[1]!.vm.$emit('update:modelValue', 'sales')
    selects[2]!.vm.$emit('update:modelValue', ['pre1', 'pre2'])
    wrapper.findComponent({ name: 'ElRadioGroup' }).vm.$emit('update:modelValue', true)
    await wrapper.get('[aria-label="项目名称"]').setValue('提前启动设备')
    await wrapper.get('[aria-label="主项目 1名称"]').setValue('合同所列设备')
    await wrapper.get('[aria-label="交货日期"]').setValue('2026-12-30')
    for (const input of wrapper.findAllComponents({ name: 'ElInputNumber' }).filter(input => input.element.closest('.application-budget'))) { input.vm.$emit('update:modelValue', 0); await flushPromises() }
    await wrapper.findAll('button').find(x => x.text() === '提交申请')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).toHaveBeenCalledWith(undefined, expect.objectContaining({ submit: true, data: expect.objectContaining({ projectName: '提前启动设备', isSpecial: true, orderDate: null, presalesUsernames: ['pre1', 'pre2'] }) }), 'token')
    expect(wrapper.text()).toContain('原申请资料')
  })
  it('审批引用原资料，未关联正式客户时阻止建项，已选后发送版本号', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '引用并审批')!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('售前甲、售前乙')
    await wrapper.findAll('button').find(x => x.text() === '引用申请并编辑建项资料')!.trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '批准并创建主项目')!.trigger('click')
    expect(api.approveProjectApplication).not.toHaveBeenCalled()
    wrapper.getComponent({ name: 'ElSelect' }).vm.$emit('update:modelValue', 'customer-1')
    vi.mocked(api.approveProjectApplication).mockResolvedValue({ ...app, status: '已批准', projectId: 'project-1', projectCode: 'P700001' })
    await wrapper.findAll('button').find(x => x.text() === '批准并创建主项目')!.trigger('click'); await flushPromises()
    expect(api.approveProjectApplication).toHaveBeenCalledWith('app-1', expect.objectContaining({ expectedRowVersion: 1, customerId: 'customer-1' }), 'token')
  })
  it('退回必须填写原因，历史内容保持展示', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '引用并审批')!.trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '退回修改')!.trigger('click')
    expect(api.actProjectApplication).not.toHaveBeenCalled()
    await wrapper.get('[aria-label="退回原因"]').setValue('请确认交期')
    vi.mocked(api.actProjectApplication).mockResolvedValue({ ...app, status: '已退回', rowVersion: 2 })
    await wrapper.findAll('button').find(x => x.text() === '退回修改')!.trigger('click'); await flushPromises()
    expect(api.actProjectApplication).toHaveBeenCalledWith('app-1', 'return', { expectedRowVersion: 1, comment: '请确认交期' }, 'token')
    expect(wrapper.get('.application-history').text()).toContain('提交申请')
  })
  it('申请人可直接修改待审批资料并生成新的提交版本', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '引用并审批')!.trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '修改申请')!.trigger('click'); await flushPromises()
    expect(wrapper.findAll('button').some(x => x.text() === '保存草稿')).toBe(false)
    await wrapper.get('[aria-label="项目名称"]').setValue('申请人修改后的项目')
    await wrapper.get('[aria-label="交货日期"]').setValue('2027-01-20')
    await wrapper.findAll('button').find(x => x.text() === '保存修改并重新提交')!.trigger('click'); await flushPromises()
    expect(api.saveProjectApplication).toHaveBeenCalledWith('app-1', expect.objectContaining({ expectedRowVersion: 1, submit: true,
      data: expect.objectContaining({ projectName: '申请人修改后的项目', deliveryDate: '2027-01-20', orderDate: null }) }), 'token')
    expect(app.data.input.projectName).toBe('提前启动设备')
  })
  it('引用后可修改项目、日期、人员、备注及立项方式，并单独显示最终快照', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '引用并审批')!.trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(x => x.text() === '引用申请并编辑建项资料')!.trigger('click'); await flushPromises()
    await wrapper.get('[aria-label="审批项目名称"]').setValue('审批确认的项目')
    await wrapper.get('[aria-label="审批订单日期"]').setValue('2026-11-01')
    await wrapper.get('[aria-label="审批交货日期"]').setValue('2027-01-20')
    await wrapper.get('[aria-label="审批备注"]').setValue('审批调整后的备注')
    const selects = wrapper.findAllComponents({ name: 'ElSelect' })
    selects[0]!.vm.$emit('update:modelValue', 'customer-1')
    selects[1]!.vm.$emit('update:modelValue', 'sales2')
    selects[2]!.vm.$emit('update:modelValue', ['pre3'])
    wrapper.getComponent({ name: 'ElRadioGroup' }).vm.$emit('update:modelValue', false)
    await wrapper.findAll('button').find(x => x.text() === '批准并创建主项目')!.trigger('click'); await flushPromises()
    expect(api.approveProjectApplication).not.toHaveBeenCalled()
    const contract = { number: 'HT-2026-01', details: '',
      groups: [{ main: { name: '合同项目', specification: '设备一套', quantity: 1, remarks: '' }, children: [] }] }
    await wrapper.get('[aria-label="合同编号"]').setValue(contract.number)
    await wrapper.get('[aria-label="主项目 1名称"]').setValue('合同项目')
    await wrapper.get('[aria-label="主项目 1规格"]').setValue('设备一套')
    wrapper.findComponent(QuantityInputNumber).vm.$emit('update:modelValue', 1)
    const finalData = { input: { ...app.data.input, customerId: 'customer-1', customerName: '正式客户', projectName: '审批确认的项目', orderDate: '2026-11-01', deliveryDate: '2027-01-20', salesUsername: 'sales2', presalesUsernames: ['pre3'], remarks: '审批调整后的备注', isSpecial: false, contract },
      salesName: '销售乙', presalesPeople: [{ username: 'pre3', displayName: '售前丙' }] }
    vi.mocked(api.approveProjectApplication).mockResolvedValue({ ...app, status: '已批准', projectId: 'project-1', projectCode: 'P700001' })
    vi.mocked(api.readProjectApplication).mockResolvedValue({ ...detail, application: { ...app, status: '已批准' }, canEdit: false, canApprove: false, canWithdraw: false,
      history: [...detail.history, { ...detail.history[0]!, id: 'approval-event', action: '批准并创建主项目', approvedData: finalData }] })
    await wrapper.findAll('button').find(x => x.text() === '批准并创建主项目')!.trigger('click'); await flushPromises()
    expect(api.approveProjectApplication).toHaveBeenCalledWith('app-1', expect.objectContaining({ expectedRowVersion: 1, projectData: expect.objectContaining({ projectName: '审批确认的项目',
      orderDate: '2026-11-01', deliveryDate: '2027-01-20', salesUsername: 'sales2', presalesUsernames: ['pre3'], remarks: '审批调整后的备注', isSpecial: false, contract }) }), 'token')
    expect(wrapper.get('[aria-label="最终建项资料"]').text()).toContain('审批确认的项目')
    expect(wrapper.get('[aria-label="最终建项资料"]').text()).toContain('销售乙')
    expect(wrapper.get('.application-history details').text()).toContain('提前启动设备')
    expect(wrapper.findAll('button').some(x => x.text() === '修改申请')).toBe(false)
  })
})
