import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionBatchDialog from '../src/components/ProductionBatchDialog.vue'
import * as api from '../src/api'
import { ElMessage } from '../src/statusMessage'
import type { ProductionReleaseRecord } from '../src/productionTracking'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))

beforeEach(() => vi.spyOn(api, 'readProductionSuppliers').mockResolvedValue({ companyId: 'company', rowVersion: 1,
  categories: [{ id: 'category', name: '外协', isActive: true }], suppliers: [{ id: 'supplier', code: 'S001', name: '外协供应商',
    fullName: '外协供应商有限公司', categoryIds: ['category'], isActive: true, address: '', phone: '', contact: '', contactPhone: '', remarks: '', updatedAt: '' }] }))

function record(): ProductionReleaseRecord {
  return { rowVersion: 1, state: { releasePackageId: 'release', projectId: 'project', companyId: 'company',
    projectCode: 'P-001', projectName: '测试项目', packageNumber: 'RP-001', scope: 'NonStandardWithDrawing',
    publishedAt: '2026-10-19T00:00:00Z', receivedAt: '2026-10-19T01:00:00Z',
    lines: [{ id: 'one', materialKey: 'one', drawingNumber: 'D-001', name: '机架', revision: 'W1',
      requiredQuantity: 2, unit: '件', priority: 'Normal', requiredOn: '2026-10-25' },
    { id: 'two', materialKey: 'two', drawingNumber: 'D-002', name: '盖板', revision: 'W1',
      requiredQuantity: 5, unit: '件', priority: 'Urgent', requiredOn: '2026-10-26' }],
    lots: [{ id: 'old', lineId: 'two', route: 'Self', quantity: 2, paused: false, frozen: false, schedule: [] }], movements: [] } }
}

afterEach(() => vi.restoreAllMocks())

describe('生产批量操作', () => {
  it('数量只读随BOM，已分配完也可整批修改，未确认响应时使用同一请求重试', async () => {
    const value = record()
    value.state.lots.push({ id: 'one-lot', lineId: 'one', route: 'Self', quantity: 2, paused: false, frozen: false, schedule: [] })
    value.state.lots[0].quantity = 5
    const assign = vi.spyOn(api, 'batchAllocateProduction').mockRejectedValueOnce(new Error('保存结果未确认，请重试。')).mockResolvedValue(value)
    const legacy = vi.spyOn(api, 'assignProductionLot')
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const wrapper = mount(ProductionBatchDialog, { props: { token: 'token', mode: 'assign', record: value, lines: value.state.lines } })
    expect(wrapper.findAll('input[type=number]')).toHaveLength(0)
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[1].text())).toEqual(['2', '5'])
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(assign).toHaveBeenCalledWith('release', expect.objectContaining({ lineIds: ['one', 'two'], route: 'Self', expectedRowVersion: 1, reason: '', assignee: '' }), 'token')
    expect(assign.mock.calls[0][1]).not.toHaveProperty('quantity')
    expect(wrapper.text()).toContain('保存结果未确认，请重试。')
    expect(wrapper.text()).toContain('重试未完成项')
    expect(wrapper.emitted('completed')).toBeUndefined()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(assign).toHaveBeenCalledTimes(2)
    expect(assign.mock.calls[1][1]).toEqual(assign.mock.calls[0][1])
    expect(legacy).not.toHaveBeenCalled()
    expect(wrapper.emitted('updated')?.map(event => event[1])).toEqual(['one', 'two'])
    expect(wrapper.emitted('completed')).toEqual([[2]])
    wrapper.unmount()
  })

  it('只批量调整日期时保留每项原有紧急程度', async () => {
    const value = record()
    const update = vi.spyOn(api, 'updateProductionLineDelivery').mockResolvedValue(value)
    const wrapper = mount(ProductionBatchDialog, { props: { token: 'token', mode: 'delivery', record: value, lines: value.state.lines } })
    await wrapper.get('input[type=date]').setValue('2026-11-02')
    await wrapper.get('input[placeholder="填写本次批量调整的原因"]').setValue('统一项目交期')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(update.mock.calls.map(call => [call[1].lineId, call[1].priority, call[1].requiredOn])).toEqual([
      ['one', 'Normal', '2026-11-02'], ['two', 'Urgent', '2026-11-02'],
    ])
    expect(update.mock.calls[0][1].requestId).not.toBe(update.mock.calls[1][1].requestId)
    wrapper.unmount()
  })

  it('外协信息不完整时阻止整批提交，并跳过无剩余数量的物料', async () => {
    const value = record()
    value.state.lines[0].requiredQuantity = 0
    const assign = vi.spyOn(api, 'batchAllocateProduction').mockResolvedValue(value)
    const wrapper = mount(ProductionBatchDialog, { props: { token: 'token', mode: 'assign', record: value, lines: value.state.lines } })
    await wrapper.get('select').setValue('External')
    await wrapper.get('form').trigger('submit')
    expect(assign).not.toHaveBeenCalled()
    expect(ElMessage.warning).toHaveBeenCalledWith('批量外协分配必须填写供应商和承诺日期。')
    await flushPromises()
    await wrapper.get('.production-supplier-picker__supplier select').setValue('supplier')
    await wrapper.get('input[type=date]').setValue('2026-11-01')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(assign).toHaveBeenCalledTimes(1)
    expect(assign.mock.calls[0][1]).toMatchObject({ lineIds: ['two'], route: 'External', supplierId: 'supplier', supplier: '外协供应商', promisedOn: '2026-11-01', reason: '', assignee: '' })
    expect(wrapper.emitted('completed')).toEqual([[1]])
    wrapper.unmount()
  })
  it('包含已加工批次时显示原因并阻止整批提交', async () => {
    const value = record()
    value.state.movements = [{ id: 'start', requestId: 'start', lotId: 'old', kind: 'Start', quantity: 1, actor: 'operator', occurredAt: '' }]
    const assign = vi.spyOn(api, 'batchAllocateProduction')
    const wrapper = mount(ProductionBatchDialog, { props: { token: 'token', mode: 'assign', record: value, lines: value.state.lines } })
    expect(wrapper.text()).toContain('该批次已有加工')
    expect(wrapper.get('button[type=submit]').attributes()).toHaveProperty('disabled')
    await wrapper.get('form').trigger('submit')
    expect(assign).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
