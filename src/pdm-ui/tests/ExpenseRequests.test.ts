import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ExpenseRequests from '../src/components/ExpenseRequests.vue'

const api = vi.hoisted(() => ({ getExpenseRequests: vi.fn(), saveExpenseRequest: vi.fn(), previewExpenseRequest: vi.fn(), decideExpenseRequest: vi.fn(), withdrawExpenseRequest: vi.fn() }))
vi.mock('../src/api', () => api)
beforeEach(() => {
  vi.clearAllMocks()
  api.getExpenseRequests.mockResolvedValue({ projectId: 'project', projectCode: 'P700016', projectName: '测试项目', canCreate: true, requests: [], departments: [
    { code: 'Procurement', name: '采购部', configured: true }, { code: 'SupplyChain', name: '供应链', configured: true },
  ] })
})

describe('费用申请', () => {
  it('masks every amount for an approver without financial access', async () => {
    const view = { request: { id:'expense', projectCode:'P1', applicant:'manager', purpose:'SitePurchase', department:'Procurement', requiredOn:'2026-12-01', reason:'采购', status:'Pending', totalAmount:123456, rowVersion:4, steps:[], lines:[{kind:'Material', name:'配件', unit:'个', quantity:2, unitPrice:61728}] }, canEdit:false, canApprove:true, canViewAmount:false, canWithdraw:false }
    api.getExpenseRequests.mockResolvedValue({projectId:'project', canCreate:false, requests:[view], departments:[]})
    const wrapper=mount(ExpenseRequests,{props:{projectId:'project',token:'token',requestedId:'expense'},global:{plugins:[ElementPlus]}})
    try { await flushPromises(); expect(wrapper.text()).toContain('无权查看'); expect(wrapper.text()).not.toMatch(/123,?456|61,?728/); expect(wrapper.text()).not.toContain('撤回申请') }
    finally { wrapper.unmount() }
  })

  it('withdraws the current revision and refreshes the closed approval state', async () => {
    const view = { request:{id:'expense',projectCode:'P1',applicant:'manager',purpose:'SitePurchase',department:'Procurement',reason:'采购',status:'Pending',totalAmount:10,rowVersion:4,steps:[],lines:[]},canEdit:false,canApprove:false,canViewAmount:true,canWithdraw:true }
    const withdrawn={...view,request:{...view.request,status:'Withdrawn',rowVersion:5},canEdit:true,canWithdraw:false}
    api.getExpenseRequests.mockResolvedValueOnce({projectId:'project',canCreate:true,requests:[view],departments:[]}).mockResolvedValue({projectId:'project',canCreate:true,requests:[withdrawn],departments:[]})
    api.withdrawExpenseRequest.mockResolvedValue(withdrawn)
    const confirm=vi.spyOn(ElMessageBox,'confirm').mockResolvedValue('confirm' as Awaited<ReturnType<typeof ElMessageBox.confirm>>)
    const wrapper=mount(ExpenseRequests,{props:{projectId:'project',token:'token',requestedId:'expense'},global:{plugins:[ElementPlus]}})
    try { await flushPromises(); await wrapper.findAll('button').find(button=>button.text()==='撤回申请')!.trigger('click'); await flushPromises(); expect(api.withdrawExpenseRequest).toHaveBeenCalledWith('project','expense',4,'token'); expect(wrapper.text()).toContain('已撤回'); expect(wrapper.text()).toContain('编辑申请'); expect(wrapper.text()).not.toContain('撤回申请') }
    finally {wrapper.unmount();confirm.mockRestore()}
  })

  it('requires a demand date and retains department when changing purpose, with strict 2000 threshold', async () => {
    const wrapper = mount(ExpenseRequests, { props: { projectId: 'project', token: 'token' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      await wrapper.findAll('button').find(button => button.text() === '新增费用申请')!.trigger('click')
      expect(wrapper.get('input[aria-label="需求日期"]').attributes('required')).toBeDefined()
      await wrapper.get('select[aria-label="审批部门"]').setValue('SupplyChain')
      await wrapper.get('select[aria-label="申请用途"]').setValue('RepairSupplement')
      expect((wrapper.get('select[aria-label="审批部门"]').element as HTMLSelectElement).value).toBe('SupplyChain')
      await wrapper.get('input[aria-label="需求日期"]').setValue('2026-12-01')
      await wrapper.get('textarea[aria-label="申请原因"]').setValue('设备维修补订')
      await wrapper.get('input[aria-label="第1行名称"]').setValue('密封圈')
      await wrapper.get('.el-input-number input').setValue(1)
      await wrapper.get('.el-input-number input').trigger('change')
      await wrapper.get('input[aria-label="第1行预计单价"]').setValue(2000)
      expect(wrapper.text()).not.toContain('部门审批后将增加超额审批节点')
      await wrapper.get('input[aria-label="第1行预计单价"]').setValue(2000.01)
      expect(wrapper.text()).toContain('审批节点和条件按所选部门的审批设置执行')
      api.previewExpenseRequest.mockResolvedValue({ steps: ['部门审批（供应链主管）', '超额审批（指定人员）'], totalAmount: 2000.01 })
      await wrapper.findAll('button').find(button => button.text() === '预览审批流程')!.trigger('click')
      await flushPromises()
      expect(api.previewExpenseRequest).toHaveBeenCalledWith('project', expect.objectContaining({ department: 'SupplyChain', purpose: 'RepairSupplement', requiredOn: '2026-12-01' }), 'token')
      expect(wrapper.get('[aria-label="审批路径"]').text()).toContain('超额审批（指定人员）')
    } finally { wrapper.unmount() }
  })

  it('renders network failures as Chinese messages', async () => {
    api.getExpenseRequests.mockRejectedValue(new Error('Failed to fetch'))
    const wrapper = mount(ExpenseRequests, { props: { projectId: 'project', token: 'token' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      expect(wrapper.get('[role="alert"]').text()).toContain('无法连接服务器')
      expect(wrapper.text()).not.toContain('Failed to fetch')
    } finally { wrapper.unmount() }
  })
})
