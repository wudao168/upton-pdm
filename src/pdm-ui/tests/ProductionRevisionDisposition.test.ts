import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ProductionRevisionDisposition from '../src/components/ProductionRevisionDisposition.vue'
import type { ProductionLot, ProductionReleaseState } from '../src/productionTracking'

function props() {
  const lot: ProductionLot = { id: 'old-lot', lineId: 'line', route: 'Self', quantity: 10,
    frozen: true, frozenByReleasePackageId: 'new-release', freezeReason: '发布包 RP-new 的图纸或BOM已变更', paused: false, schedule: [] }
  const state: ProductionReleaseState = { releasePackageId: 'old-release', projectId: 'project', companyId: 'company',
    projectCode: 'P001', projectName: '变更测试', packageNumber: 'RP-old', scope: 'NonStandardWithDrawing', publishedAt: '',
    lots: [lot], lines: [], movements: ['Issue', 'Cancelled', 'Transferred'].map((kind, index) => ({
      id: `${index}`, requestId: `${index}`, lotId: lot.id, kind, quantity: 1, actor: 'admin', occurredAt: '' })) }
  return { state, lot, unit: '件', canResolve: true, pending: false }
}
describe('变更处置指引', () => {
  it('展示冻结原因和待处置数量，必须明确选择方式及填写原因', async () => {
    const wrapper = mount(ProductionRevisionDisposition, { props: props() })
    expect(wrapper.text()).toContain('RP-new'); expect(wrapper.text()).toContain('7 件')
    expect(wrapper.get('button').attributes()).toHaveProperty('disabled')
    await wrapper.get('form').trigger('submit'); expect(wrapper.emitted('resolve')).toBeUndefined()
    await wrapper.get('select').setValue('ContinueOld')
    expect(wrapper.text()).toContain('旧版图档已作废时不可使用')
    await wrapper.get('input').setValue('   ')
    expect(wrapper.get('button').attributes()).toHaveProperty('disabled')
    await wrapper.get('input').setValue('  技术确认可继续  '); await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('resolve')![0][0]).toMatchObject({ lotId: 'old-lot', changedReleasePackageId: 'new-release', action: 'ContinueOld', reason: '技术确认可继续', requestId: expect.any(String) })
    wrapper.unmount()
  })
  it('转新版提示先接收、再分配，取消数量必须在剩余范围内', async () => {
    const wrapper = mount(ProductionRevisionDisposition, { props: props() })
    await wrapper.get('select').setValue('TransferNew')
    expect(wrapper.text()).toContain('须先由供应链接收新版发布包')
    expect(wrapper.text()).toContain('重新分配批次')
    await wrapper.get('input').setValue('变更重做'); await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('resolve')![0][0]).toMatchObject({ action: 'TransferNew', quantity: undefined })
    await wrapper.get('select').setValue('CancelRemaining')
    expect(wrapper.text()).toContain('部分取消时，其余数量保留在旧批次继续办理')
    for (const value of [0, 8]) {
      await wrapper.get('input[type=number]').setValue(value)
      await wrapper.get('form').trigger('submit'); expect(wrapper.emitted('resolve')).toHaveLength(1)
    }
    await wrapper.get('input[type=number]').setValue(2); await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('resolve')![1][0]).toMatchObject({ action: 'CancelRemaining', quantity: 2 })
    wrapper.unmount()
  })
  it('无权限或缺少变更包时说明找谁处理，不提供提交表单', async () => {
    const wrapper = mount(ProductionRevisionDisposition, { props: { ...props(), canResolve: false } })
    expect(wrapper.text()).toContain('“分配非标件自制与外协”或“安排自制生产”')
    expect(wrapper.find('form').exists()).toBe(false)
    await wrapper.setProps({ lot: { ...props().lot, route: 'External' } })
    expect(wrapper.text()).not.toContain('安排自制生产')
    await wrapper.setProps({ lot: { ...props().lot, frozenByReleasePackageId: null }, canResolve: true })
    expect(wrapper.text()).toContain('请先刷新'); expect(wrapper.find('form').exists()).toBe(false)
    wrapper.unmount()
  })
  it('取消时默认带出扣除已领用、已取消和已转版后的剩余数量，允许手动修改', async () => {
    const wrapper = mount(ProductionRevisionDisposition, { props: props() })
    await wrapper.get('select').setValue('CancelRemaining')
    expect(wrapper.get<HTMLInputElement>('input[type=number]').element.value).toBe('7')
    expect(wrapper.get('button').attributes()).toHaveProperty('disabled')
    await wrapper.get('input[placeholder]').setValue('核对后全部取消')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('resolve')![0][0]).toMatchObject({ action: 'CancelRemaining', quantity: 7 })
    await wrapper.get('input[type=number]').setValue(2.5)
    await wrapper.get('input[placeholder]').setValue('核对后部分取消')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('resolve')![1][0]).toMatchObject({ quantity: 2.5 })
    await wrapper.get('select').setValue('ContinueOld')
    await wrapper.get('select').setValue('CancelRemaining')
    expect(wrapper.get<HTMLInputElement>('input[type=number]').element.value).toBe('7')
    await wrapper.setProps({ lot: { ...props().lot, id: 'another', quantity: 4.5 } })
    expect(wrapper.get('select').element.value).toBe('')
    await wrapper.get('select').setValue('CancelRemaining')
    expect(wrapper.get<HTMLInputElement>('input[type=number]').element.value).toBe('4.5')
    wrapper.unmount()
  })
  it('提交期间不重复提交，切换批次清空旧的处置选择', async () => {
    const wrapper = mount(ProductionRevisionDisposition, { props: props() })
    await wrapper.get('select').setValue('ContinueOld'); await wrapper.get('input').setValue('按旧版')
    await wrapper.setProps({ pending: true }); await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('resolve')).toBeUndefined()
    await wrapper.setProps({ pending: false, lot: { ...props().lot, id: 'another' } })
    expect(wrapper.get('select').element.value).toBe(''); expect(wrapper.get('input').element.value).toBe('')
    wrapper.unmount()
  })
})
