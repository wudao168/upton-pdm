import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionOperationPanel from '../src/components/ProductionOperationPanel.vue'
import * as api from '../src/api'
import type { ProductionReleaseRecord, ProductionScanAction } from '../src/productionTracking'
const record = (): ProductionReleaseRecord => ({ rowVersion: 1, state: { releasePackageId: 'release', projectId: 'project', companyId: 'company', projectCode: 'P1', projectName: '项目', packageNumber: 'RP1', scope: 'NonStandardWithDrawing', publishedAt: '', receivedAt: '2026-10-20T00:00:00Z',
  lines: [{ id: 'line', materialKey: 'M', drawingNumber: 'MODEL', name: '支架', revision: 'W1', requiredQuantity: 4, unit: '001', priority: 'Normal' }],
  lots: ['one', 'two'].map(id => ({ id, lineId: 'line', route: 'Self', quantity: 2, paused: false, frozen: false, schedule: [] })), movements: [] } })
const action = (lotId = 'one', workstationId = 'station'): ProductionScanAction => ({ lotId, kind: 'ProductionReceived', label: '生产签收', remainingQuantity: 2, bindingId: 'binding', post: 'Reception', workstationId, workstationName: workstationId, resourceGroup: '' })
const create = (value = record()) => mount(ProductionOperationPanel, { props: { token: 'token', username: 'operator', record: value, lineId: 'line' } })
beforeEach(() => { sessionStorage.clear(); vi.spyOn(api, 'readProductionTrackingRelease').mockResolvedValue(record()) })
afterEach(() => vi.restoreAllMocks())
describe('按岗位扫码办理', () => {
  it('只显示服务端允许的步骤，多批次不默认选择第一批', async () => {
    vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [action('one'), action('two')], hint: '核对数量后确认' })
    const save = vi.spyOn(api, 'recordProductionMovement').mockResolvedValue(record())
    const wrapper = create(); await flushPromises()
    expect(wrapper.find('form').exists()).toBe(false)
    expect(wrapper.get('select').element).toHaveProperty('value', '')
    await wrapper.get('select').setValue('two'); await flushPromises()
    expect(wrapper.findAll('form select')[0].findAll('option').map(option => option.text())).toEqual(['生产签收'])
    expect(wrapper.get('input[type=number]').element).toHaveProperty('value', '2')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save).toHaveBeenCalledWith('release', expect.objectContaining({ lotId: 'two', kind: 'ProductionReceived', workstationId: 'station', quantity: 2 }), 'token')
    wrapper.unmount()
  })
  it('多个工位时必须明确选择工位，超量不发送请求', async () => {
    vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [action('one', 'A'), action('one', 'B')], hint: '' })
    const save = vi.spyOn(api, 'recordProductionMovement')
    const value = record(); value.state.lots = [value.state.lots[0]]
    const wrapper = create(value); await flushPromises()
    expect(wrapper.findAll('form select')[1].element).toHaveProperty('value', '')
    await wrapper.findAll('form select')[1].setValue('A'); await wrapper.get('input[type=number]').setValue(3)
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save).not.toHaveBeenCalled(); expect(wrapper.text()).toContain('不超过可办理数量')
    wrapper.unmount()
  })
  it('保存后刷新记录仍保留本次批次，不跳到下一可办理批次', async () => {
    const actions = vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [action('one'), action('two')], hint: '' })
    const updated = record(); updated.rowVersion = 2
    updated.state.movements = [{ id: 'received', requestId: 'request', lotId: 'one', kind: 'ProductionReceived', quantity: 2, actor: 'operator', occurredAt: '2026-10-20T01:00:00Z' }]
    vi.spyOn(api, 'recordProductionMovement').mockResolvedValue(updated)
    const wrapper = create(); await flushPromises(); await wrapper.get('select').setValue('one')
    actions.mockResolvedValue({ actions: [action('two')], hint: '' })
    await wrapper.get('form').trigger('submit'); await flushPromises()
    await wrapper.setProps({ record: updated }); await flushPromises()
    expect(wrapper.get('select').element).toHaveProperty('value', 'one')
    expect(wrapper.text()).toContain('生产签收已确认')
    expect(wrapper.text()).toContain('当前环节：生产已签收／待排产')
    expect(wrapper.find('form').exists()).toBe(false)
    wrapper.unmount()
  })
  it('响应丢失后锁定输入并沿用相同操作编号重试，保存成功后清除重试状态', async () => {
    vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [action()], hint: '' })
    const save = vi.spyOn(api, 'recordProductionMovement').mockRejectedValueOnce(new Error('保存结果未确认，请重试。')).mockResolvedValueOnce(record())
    const wrapper = create(); await flushPromises(); await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(wrapper.get('fieldset').attributes()).toHaveProperty('disabled')
    expect(wrapper.text()).toContain('待核实上次操作：MODEL · 支架')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save.mock.calls[1][1]).toEqual(save.mock.calls[0][1])
    expect(sessionStorage.getItem('pdm-production-pending-operator-release')).toBeNull()
    wrapper.unmount()
  })
  it('明确拒绝保存时允许刷新后重新选择，不把过期步骤锁死为重试', async () => {
    vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [action()], hint: '' })
    vi.spyOn(api, 'recordProductionMovement').mockRejectedValue(new api.PdmApiError('可办理数量已变化，请重新确认。', 409))
    const wrapper = create(); await flushPromises(); await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(wrapper.get('fieldset').attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).toContain('可办理数量已变化')
    expect(sessionStorage.getItem('pdm-production-pending-operator-release')).toBeNull()
    wrapper.unmount()
  })
  it('未绑定时没有步骤表单，历史记录完整显示服务器记录的工位与前后状态', async () => {
    vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [], hint: '当前账号未配置岗位和工位' })
    const value = record(); value.state.lots = [value.state.lots[0]]
    value.state.movements = Array.from({ length: 12 }, (_, index) => ({ id: `move-${index}`, requestId: `request-${index}`, lotId: 'one', kind: 'Start', quantity: 1, actor: 'machine', actorName: '机加员', occurredAt: '2026-10-20T01:00:00Z', workstationName: 'CNC01', post: 'Machining', resourceGroup: 'CNC组', fromStage: '待开工', toStage: '机加中' }))
    const wrapper = create(value); await flushPromises()
    expect(wrapper.find('form').exists()).toBe(false)
    expect(wrapper.text()).toContain('当前账号未配置岗位和工位')
    expect(wrapper.findAll('.production-operation__history li')).toHaveLength(12)
    expect(wrapper.text()).toContain('CNC01'); expect(wrapper.text()).toContain('待开工 → 机加中')
    wrapper.unmount()
  })
})
