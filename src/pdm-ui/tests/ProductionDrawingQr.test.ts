import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionDrawingQr from '../src/components/ProductionDrawingQr.vue'
import { listProductionDrawings } from '../src/api'
import { createProductionDrawingQr, createNameplateQr } from '../src/productionDrawingQr'
import type { ProductionDrawingItem } from '../src/types'
import type { ProductionLine, ProductionReleaseState } from '../src/productionTracking'

vi.mock('../src/api', () => ({ listProductionDrawings: vi.fn() }))
vi.mock('../src/productionDrawingQr', () => ({ createProductionDrawingQr: vi.fn(), createNameplateQr: vi.fn() }))
const state = { projectId: 'project', releasePackageId: 'tracking', formalReleasePackageId: 'frozen-package' } as ProductionReleaseState
const line = { id: 'line', materialKey: 'material', drawingNumber: 'DRAWING', revision: 'W1' } as ProductionLine
function drawing(versionId: string, releasePackageId = 'frozen-package'): ProductionDrawingItem {
  return { versionId, documentId: versionId, releasePackageId, projectId: 'project', drawingNumber: 'DRAWING', revision: 'A', pdfReady: false, qrContent: versionId === 'back' ? 'BACK\nB' : 'DRAWING\nA', bomQuantities: [{ materialKey: 'material' }] } as ProductionDrawingItem
}
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(listProductionDrawings).mockResolvedValue([drawing('formal-A'), drawing('newest-B', 'other-package')])
  vi.mocked(createProductionDrawingQr).mockResolvedValue({ content: 'DRAWING\nA', src: 'data:image/svg+xml,qr' })
  vi.mocked(createNameplateQr).mockImplementation(async serial => ({ content: serial, src: 'data:image/svg+xml,nameplate' }))
})
describe('右侧正式图纸二维码', () => {
  it('铭牌使用设备序列号直接画码，无需正式图纸', async () => {
    const nameplate = { serialNumber: '70000046', name: '设备', model: 'AK001', powerSupply: '380V 50Hz / 5A', ratedPower: 1.9, dimensions: '1500mm x 1800mm x 2200mm', airPressure: '0.6 MPa', weight: '12000 kg', factoryDate: '2026-10-01', confirmedBy: 'hardware', confirmedAt: '2026-10-30T01:00:00Z' }
    const wrapper = mount(ProductionDrawingQr, { props: { token: 'token', state, line: { ...line, nameplateContent: nameplate } } })
    await flushPromises()
    expect(createNameplateQr).toHaveBeenCalledWith('70000046')
    expect(wrapper.get('img').attributes('title')).toBe('70000046')
    expect(listProductionDrawings).not.toHaveBeenCalled()
    expect(createProductionDrawingQr).not.toHaveBeenCalled()
    expect(wrapper.text()).not.toContain('暂无对应的正式图纸')
    wrapper.unmount()
  })
  it('只用当前发布包的正式版次，PDF尚未生成时也能画码', async () => {
    const wrapper = mount(ProductionDrawingQr, { props: { token: 'token', state, line } })
    await flushPromises()
    expect(createProductionDrawingQr).toHaveBeenCalledWith('DRAWING\nA')
    expect(wrapper.get('img').attributes('src')).toBe('data:image/svg+xml,qr')
    expect(wrapper.get('img').attributes('title')).toBe('DRAWING\nA')
    expect(wrapper.text()).not.toContain('W1')
    wrapper.unmount()
  })
  it('对应多份图纸时先选择，避免默认使用错误的二维码', async () => {
    vi.mocked(listProductionDrawings).mockResolvedValue([drawing('front'), drawing('back')])
    const wrapper = mount(ProductionDrawingQr, { props: { token: 'token', state, line } })
    await flushPromises()
    expect(createProductionDrawingQr).not.toHaveBeenCalled()
    await wrapper.get('select').setValue('back'); await flushPromises()
    expect(createProductionDrawingQr).toHaveBeenCalledWith('BACK\nB')
    wrapper.unmount()
  })
  it('快速切换物料时清除旧二维码，并忽略旧请求的迟到结果', async () => {
    let finish!: (value: { content: string; src: string }) => void
    vi.mocked(createProductionDrawingQr).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const wrapper = mount(ProductionDrawingQr, { props: { token: 'token', state, line } })
    await flushPromises()
    vi.mocked(listProductionDrawings).mockResolvedValue([])
    await wrapper.setProps({ line: { ...line, id: 'next', materialKey: 'next-material' } }); await flushPromises()
    finish({ content: 'OLD\nA', src: 'old-qr' }); await flushPromises()
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('暂无对应的正式图纸')
    wrapper.unmount()
  })
})
