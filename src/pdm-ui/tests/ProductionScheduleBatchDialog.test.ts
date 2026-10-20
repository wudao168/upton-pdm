import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProductionScheduleBatchDialog from '../src/components/ProductionScheduleBatchDialog.vue'
import * as api from '../src/api'
import type { ProductionReleaseRecord } from '../src/productionTracking'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))
afterEach(() => vi.restoreAllMocks())
function record(): ProductionReleaseRecord {
  return { rowVersion: 1, state: { releasePackageId: 'release', projectId: 'project', companyId: 'company', projectCode: 'P001', projectName: '项目', packageNumber: 'RP001', scope: 'NonStandardWithDrawing', publishedAt: '', movements: [],
    lines: [{ id: 'line', materialKey: 'M', drawingNumber: 'D001', name: '机架', revision: 'W1', requiredQuantity: 5, unit: '001', priority: 'Normal' }],
    lots: ['one', 'two'].map(id => ({ id, lineId: 'line', route: 'Self', quantity: 2, paused: false, frozen: false, schedule: [] })) } }
}
const create = (value: ProductionReleaseRecord) => mount(ProductionScheduleBatchDialog, { props: { token: 'token', record: value, lots: value.state.lots, resources: ['CNC组', '车床组'], date: '2026-11-01' }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })

describe('批量排程', () => {
  it('每个批次使用独立请求，工时分别提交，丢失响应后仅重试未确认项且不改变请求内容', async () => {
    const value = record()
    let failed = false
    const save = vi.spyOn(api, 'scheduleProductionLot').mockImplementation(async (_id, input) => {
      if (input.lotId === 'two' && !failed) { failed = true; throw new Error('保存结果未确认，请重试。') }
      return value
    })
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const wrapper = create(value)
    await flushPromises()
    await wrapper.get('input[aria-label="每批次工时"]').setValue(2.5)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save.mock.calls.map(call => call[1].lotId)).toEqual(['one', 'two'])
    expect(save.mock.calls.map(call => call[1].slots[0].hours)).toEqual([2.5, 2.5])
    expect(save.mock.calls[0][1].requestId).not.toBe(save.mock.calls[1][1].requestId)
    expect(wrapper.get('fieldset').attributes()).toHaveProperty('disabled')
    expect(wrapper.text()).toContain('保存结果未确认，请重试。')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save.mock.calls.map(call => call[1].lotId)).toEqual(['one', 'two', 'two'])
    expect(save.mock.calls[2][1]).toEqual(save.mock.calls[1][1])
    expect(wrapper.emitted('updated')?.map(event => event[1])).toEqual(['one', 'two'])
    expect(wrapper.emitted('completed')).toEqual([[2]])
    wrapper.unmount()
  })

  it('已有排程必须填写原因，冻结批次不能提交', async () => {
    const value = record()
    value.state.lots[0].schedule = [{ resourceGroupId: 'CNC组', date: '2026-10-31', hours: 1 }]
    const save = vi.spyOn(api, 'scheduleProductionLot').mockResolvedValue(value)
    const wrapper = create(value)
    await flushPromises()
    await wrapper.get('form').trigger('submit')
    expect(save).not.toHaveBeenCalled()
    await wrapper.get('input[placeholder="填写批量排程调整原因"]').setValue('项目插单调整')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save.mock.calls[0][1].changeReason).toBe('项目插单调整')
    wrapper.unmount()
    save.mockClear(); value.state.lots[1].frozen = true
    const frozen = create(value)
    await flushPromises()
    await frozen.get('input[placeholder="填写批量排程调整原因"]').setValue('调整')
    await frozen.get('form').trigger('submit')
    expect(save).not.toHaveBeenCalled()
    frozen.unmount()
  })
})
