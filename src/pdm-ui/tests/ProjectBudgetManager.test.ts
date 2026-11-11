import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import ProjectBudgetManager from '../src/components/ProjectBudgetManager.vue'
import { calculateBudgetRow, budgetCategories, uppercaseMoney } from '../src/projectBudget'
import type { BudgetLine } from '../src/projectBudget'

const api = vi.hoisted(() => ({ getBudgetAssessments: vi.fn(), getProjectBudget: vi.fn(), saveProjectBudget: vi.fn(), saveBudgetAssessment: vi.fn(), saveAssessmentLaborRates: vi.fn(), addProjectBudgetNote: vi.fn() }))
vi.mock('../src/api', () => api)
const line = (category: string): BudgetLine => ({ category, budgetAmount: 1000, actualAmount: 100, remainingAmount: 0,
  plannedHours: 10, hourlyRate: 100, actualHours: 1, actualHourlyRate: 100, remainingHours: 0, note: null })
const response = (editable = true) => ({ projectId: 'p1', rows: budgetCategories.filter(category => category.key !== 'RiskReserve').map(category => calculateBudgetRow(line(category.key), null)),
  orders: [], canViewSettlement: true, canEdit: editable, canViewReserve: false, rowVersion: 1, updatedAt: null, updatedBy: null })
beforeEach(() => { vi.clearAllMocks(); api.getBudgetAssessments.mockResolvedValue({ sheets: [], amounts: { Standard: 250, MechanicalDesign: 800 } }); api.getProjectBudget.mockResolvedValue(response()); api.saveProjectBudget.mockResolvedValue(response()) })

describe('project budget', () => {
  it('保存评估保留当前编辑页，更新版本号且继续编辑不会丢失其他项目评估', async () => {
    const group = { id: 'g1', name: '控制部分', category: 'Standard', laborCategory: null, departmentId: 'old-department', items: [{ id: 'i1', name: '模块', model: null, brand: null, note: null, quantity: 1, unitPrice: 100 }] }
    const root = { projectId: 'p1', projectCode: 'P1', projectName: '主项目', designLead: 'engineer', canEdit: true, rowVersion: 1, total: 100, updatedBy: null, updatedAt: null, groups: [group] }
    const child = { ...root, projectId: 'p2', projectCode: 'P1-1', projectName: '子项目', groups: [{ ...group, id: 'g2' }] }
    api.getBudgetAssessments.mockResolvedValue({ sheets: [root, child], amounts: { Standard: 200 }, departments: [{ id: 'old-department', name: '原部门' }] })
    api.saveBudgetAssessment.mockImplementation(async (id, input) => ({ sheets: [{ ...root, projectId: id, groups: JSON.parse(JSON.stringify(input.groups)), rowVersion: input.expectedRowVersion + 1 }], amounts: { Standard: 100 } }))
    const wrapper = mount(ProjectBudgetManager, { props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      await wrapper.findAll('[role="tab"]').find(tab => tab.text() === '预算评估')!.trigger('click')
      await wrapper.findAll('button').find(button => button.text() === '编辑评估')!.trigger('click')
      expect(wrapper.find('[aria-label="评估归属部门"]').exists()).toBe(false)
      await wrapper.get('input[aria-label="分组名称"]').setValue('已修改的控制部分')
      await wrapper.findAll('button').find(button => button.text() === '保存评估')!.trigger('click'); await flushPromises()
      expect((wrapper.get('input[aria-label="分组名称"]').element as HTMLInputElement).value).toBe('已修改的控制部分')
      expect(wrapper.findAll('button').find(button => button.text() === '保存评估')!.attributes('disabled')).toBeDefined()
      expect(api.saveBudgetAssessment).toHaveBeenLastCalledWith('p1', expect.objectContaining({ expectedRowVersion: 1, groups: [expect.objectContaining({ departmentId: 'old-department' })] }), 'token')
      await wrapper.get('input[aria-label="分组名称"]').setValue('继续修改')
      await wrapper.findAll('button').find(button => button.text() === '保存评估')!.trigger('click'); await flushPromises()
      expect(api.saveBudgetAssessment).toHaveBeenLastCalledWith('p1', expect.objectContaining({ expectedRowVersion: 2 }), 'token')
      expect(wrapper.find('input[aria-label="分组名称"]').exists()).toBe(true)
      expect(api.getBudgetAssessments).toHaveBeenCalledTimes(1)
      await wrapper.findAll('button').find(button => button.text() === '返回列表')!.trigger('click'); await flushPromises()
      expect(wrapper.text()).toContain('子项目')
      await wrapper.findAll('[role="tab"]').find(tab => tab.text() === '预算汇总')!.trigger('click')
      await wrapper.get('input[aria-label="预算金额"]').setValue('1500')
      await wrapper.findAll('button').find(button => button.text() === '保存预算')!.trigger('click'); await flushPromises()
      expect(api.saveProjectBudget).toHaveBeenLastCalledWith('p1', expect.objectContaining({ expectedRowVersion: 3 }), 'token')
    } finally { wrapper.unmount() }
  })

  it('keeps summary edits when the same project refreshes and the login token renews', async () => {
    const wrapper = mount(ProjectBudgetManager, { props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      await wrapper.get('input[aria-label="预算金额"]').setValue('1500')
      await wrapper.setProps({ project: { id: 'p1' } })
      await flushPromises()
      await wrapper.setProps({ token: 'renewed-token' })
      await flushPromises()
      expect(api.getProjectBudget).toHaveBeenCalledTimes(1)
      expect(api.getBudgetAssessments).toHaveBeenCalledTimes(1)
      expect(Number((wrapper.get('input[aria-label="预算金额"]').element as HTMLInputElement).value)).toBe(1500)
      await wrapper.findAll('button').find(button => button.text() === '保存预算')!.trigger('click')
      await flushPromises()
      expect(api.saveProjectBudget).toHaveBeenCalledWith('p1', expect.objectContaining({ lines: expect.arrayContaining([expect.objectContaining({ budgetAmount: 1500 })]) }), 'renewed-token')
    } finally { wrapper.unmount() }
  })

  it('keeps the assessment tab, selected sheet and edits during background project updates', async () => {
    api.getBudgetAssessments.mockResolvedValue({ sheets: [{ projectId: 'p1', projectCode: 'P700016-2', projectName: '评估测试', designLead: 'engineer', canEdit: true, rowVersion: 1, total: 100,
      updatedBy: 'engineer', updatedAt: null, groups: [{ id: 'g1', name: '原分组', category: 'Standard', laborCategory: null, items: [] }] }], amounts: {} })
    const wrapper = mount(ProjectBudgetManager, { props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      await wrapper.findAll('[role="tab"]').find(tab => tab.text() === '预算评估')!.trigger('click')
      await wrapper.findAll('button').find(button => button.text() === '编辑评估')!.trigger('click')
      await wrapper.get('input[aria-label="分组名称"]').setValue('尚未保存的评估分组')
      await wrapper.setProps({ project: { id: 'p1' } })
      await flushPromises()
      await wrapper.setProps({ token: 'renewed-token' })
      await flushPromises()
      expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toBe('预算评估')
      expect((wrapper.get('input[aria-label="分组名称"]').element as HTMLInputElement).value).toBe('尚未保存的评估分组')
      expect(api.getProjectBudget).toHaveBeenCalledTimes(1)
      expect(api.getBudgetAssessments).toHaveBeenCalledTimes(1)
      expect(wrapper.findAll('button').find(button => button.text() === '保存评估')!.attributes('disabled')).toBeUndefined()
      api.getProjectBudget.mockResolvedValue({ ...response(), projectId: 'p2' })
      await wrapper.setProps({ project: { id: 'p2' } })
      await flushPromises()
      expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toBe('预算汇总')
      expect(api.getProjectBudget).toHaveBeenLastCalledWith('p2', 'renewed-token')
    } finally { wrapper.unmount() }
  })

  it('lets a viewer add notes, shows newest content, and opens the full history', async () => {
    const oldNote = { id: 'n1', category: 'Standard', content: '原备注', createdBy: 'planner', createdAt: '2026-10-01T12:00:00Z' }
    const newNote = { id: 'n2', category: 'Standard', content: '新备注', createdBy: 'viewer', createdAt: '2026-10-02T01:00:00Z' }
    api.getProjectBudget.mockResolvedValue({ ...response(false), notes: [oldNote] })
    api.addProjectBudgetNote.mockResolvedValue({ ...response(false), notes: [oldNote, newNote], rowVersion: 2 })
    const wrapper = mount(ProjectBudgetManager, { attachTo: document.body, props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.find('button[aria-label="标准件备注"]').text()).toBe('原备注')
    await wrapper.find('button[aria-label="标准件备注"]').trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('[role="dialog"]')!
    expect(dialog.textContent).toContain('原备注')
    const input = dialog.querySelector('textarea')!
    input.value = '新备注'; input.dispatchEvent(new Event('input', { bubbles: true }))
    await flushPromises()
    const add = Array.from(dialog.querySelectorAll('button')).find(button => button.textContent?.trim() === '添加备注')!
    add.click(); await flushPromises()
    expect(api.addProjectBudgetNote).toHaveBeenCalledWith('p1', { category: 'Standard', content: '新备注', expectedRowVersion: 1 }, 'token')
    expect(wrapper.find('button[aria-label="标准件备注"]').text()).toBe('新备注')
    expect(dialog.textContent).toContain('姓名待补充')
    expect(dialog.textContent).not.toContain('viewer')
    expect(dialog.textContent).toContain('原备注')
    expect(dialog.querySelector('article')?.textContent).toContain('新备注')
    expect(wrapper.text()).not.toContain('保存预算')
    wrapper.unmount()
  })

  it('formats Chinese uppercase money across sections and cents', () => {
    expect(uppercaseMoney(900)).toBe('人民币玖佰元整')
    expect(uppercaseMoney(10001000.05)).toBe('人民币壹仟万壹仟元零伍分')
    expect(uppercaseMoney(100000001.10)).toBe('人民币壹亿零壹元壹角')
    expect(uppercaseMoney(0)).toBe('人民币零元整')
    expect(uppercaseMoney(null)).toBe('—')
    expect(uppercaseMoney(0.01)).toBe('人民币零元零壹分')
    expect(uppercaseMoney(200.80)).toBe('人民币贰佰元捌角')
    expect(uppercaseMoney(720.75)).toBe('人民币柒佰贰拾元柒角伍分')
    expect(uppercaseMoney(1_000_000_000)).toBe('人民币壹拾亿元整')
    for (const invalid of [Number.NaN, Number.POSITIVE_INFINITY, -1]) expect(uppercaseMoney(invalid)).toBe('—')
  })
  it('compares cost sources without duplicating orders and retains unknown values', () => {
    expect(calculateBudgetRow(line('Standard'), 600).estimatedAmount).toBe(600)
    expect(calculateBudgetRow({ ...line('SiteElectrical'), budgetAmount: 1600, hourlyRate: 900, remainingHours: 2 }, null).budgetAmount).toBe(1600)
    expect(calculateBudgetRow({ ...line('SiteElectrical'), hourlyRate: 900, remainingHours: 2 }, null).estimatedAmount).toBe(300)
    expect(calculateBudgetRow({ ...line('SiteElectrical'), actualAmount: 777, actualHours: 90 }, null).actualAmount).toBe(777)
    expect(calculateBudgetRow({ ...line('SiteElectrical'), actualAmount: null }, null).actualAmount).toBeNull()
    expect(calculateBudgetRow({ ...line('Standard'), actualAmount: null }, 600).estimatedAmount).toBeNull()
    expect(calculateBudgetRow(line('Standard'), null, true).alert).toBe('Incomplete')
    expect(calculateBudgetRow({ ...line('SiteElectrical'), remainingHours: 8 }, null).alert).toBe('Warning')
  })
  it('renders four summaries, excludes hidden reserve, and persists manual amounts', async () => {
    const wrapper = mount(ProjectBudgetManager, { props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('.pdm-budget__cards article')).toHaveLength(6)
    expect(wrapper.text()).not.toContain('风险预留')
    expect(wrapper.text()).not.toContain('现场电气工程师调试')
    expect(wrapper.findAll('thead tr')).toHaveLength(2)
    expect(wrapper.find('.pdm-budget__header').exists()).toBe(false)
    expect(wrapper.findAll('.pdm-budget__costs')).toHaveLength(1)
    expect(wrapper.findAll('.pdm-budget__costs .pdm-budget__group')).toHaveLength(2)
    expect(wrapper.find('.pdm-budget__costs .pdm-primary-action').exists()).toBe(true)
    expect(wrapper.findAll('thead th')).toHaveLength(16)
    expect(wrapper.findAll('tfoot tr')).toHaveLength(2)
    expect(wrapper.findAll('tfoot')[0]!.text()).toContain('¥ 5,000.00')
    expect(wrapper.findAll('tfoot')[0]!.text()).toContain('¥ 4,500.00')
    expect(wrapper.findAll('.pdm-budget__cards article')[3]!.text()).toContain('10.0%')
    expect(wrapper.findAll('.pdm-budget__cards article')[4]!.text()).toContain('50.0%')
    expect(wrapper.text()).toContain('剩余工时费用')
    expect(wrapper.find('[aria-label="计划工时"]').exists()).toBe(false)
    expect(wrapper.findAll('input[aria-label="预算金额"]')).toHaveLength(10)
    expect(wrapper.find('.pdm-budget__hint').exists()).toBe(false)
    expect(wrapper.findAll('input[aria-label="实际金额"]')).toHaveLength(10)
    const input = wrapper.find('input[aria-label="预算金额"]')
    await input.setValue('1500')
    await input.trigger('blur')
    await flushPromises()
    const save = wrapper.findAll('button').find(button => button.text().includes('保存预算'))!
    expect(save.attributes('disabled')).toBeUndefined()
    await save.trigger('click')
    await flushPromises()
    expect(api.saveProjectBudget).toHaveBeenCalledWith('p1', expect.objectContaining({ expectedRowVersion: 1, lines: expect.arrayContaining([expect.objectContaining({ category: 'Standard', budgetAmount: 1500 })]) }), 'token')
    wrapper.unmount()
  })
  it('sums entered amounts when other categories are empty', async () => {
    const result = response()
    result.rows = result.rows.map(row => calculateBudgetRow({ ...row.input, budgetAmount: row.input.category === 'Standard' ? 1000 : null, actualAmount: row.input.category === 'Standard' ? 900 : null }, null))
    api.getProjectBudget.mockResolvedValue(result)
    const wrapper = mount(ProjectBudgetManager, { props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('tfoot')[0]!.text()).toContain('¥ 1,000.00')
    expect(wrapper.findAll('tfoot')[0]!.text()).toContain('¥ 900.00')
    expect(wrapper.findAll('tfoot')[0]!.text()).toContain('¥ 100.00')
    expect(wrapper.findAll('.pdm-budget__cards article')[3]!.text()).toContain('90.0%')
    expect(wrapper.findAll('tfoot')[1]!.text()).toContain('—')
    wrapper.unmount()
  })
  it('lets finance enter actual hours and amounts without editing budgets', async () => {
    api.getProjectBudget.mockResolvedValue({ ...response(), canEditBudget: false, canEditActual: true })
    const wrapper = mount(ProjectBudgetManager, { props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.find('input[aria-label="预算金额"]').attributes('disabled')).toBeDefined()
    const input = wrapper.findAll('input[aria-label="实际金额"]')[5]!
    await input.setValue('777')
    await input.trigger('blur')
    await wrapper.findAll('button').find(button => button.text().includes('保存预算'))!.trigger('click')
    await flushPromises()
    expect(api.saveProjectBudget).toHaveBeenCalledWith('p1', expect.objectContaining({ lines: expect.arrayContaining([expect.objectContaining({ category: 'MechanicalDesign', actualAmount: 777, actualHours: 1 })]) }), 'token')
    wrapper.unmount()
  })
  it('renders a read only view for viewers and reports denied access', async () => {
    api.getProjectBudget.mockResolvedValue(response(false))
    const wrapper = mount(ProjectBudgetManager, { props: { project: { id: 'p1' }, token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.text()).not.toContain('保存预算')
    expect(wrapper.findAll('input').every(input => input.attributes('disabled') !== undefined)).toBe(true)
    api.getProjectBudget.mockRejectedValue(new Error('没有预算查看权限'))
    await wrapper.setProps({ project: { id: 'p2' } })
    await flushPromises()
    expect(wrapper.text()).toContain('没有预算查看权限')
    expect(wrapper.findAll('.pdm-budget__cards article')).toHaveLength(0)
    wrapper.unmount()
  })
})
