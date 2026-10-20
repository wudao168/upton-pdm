import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionCameraScanner from '../src/components/ProductionCameraScanner.vue'
import * as qr from '../src/productionQr'

function environment(secure = false, getUserMedia = vi.fn()) {
  vi.stubGlobal('isSecureContext', secure)
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } })
  return getUserMedia
}
beforeEach(() => {
  vi.spyOn(qr, 'readProductionQrFrame').mockReturnValue(undefined)
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
})
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('移动生产摄像头扫码', () => {
  it('内网HTTP使用后置相机拍照入口，照片识别后只发出定位内容', async () => {
    const media = environment(), decode = vi.spyOn(qr, 'readProductionQrPhoto').mockResolvedValue('UPLM-PRODUCTION|release|lot')
    const wrapper = mount(ProductionCameraScanner)
    expect(wrapper.text()).toContain('当前内网地址使用拍照扫码')
    const input = wrapper.get('input[capture=environment]'), click = vi.spyOn(input.element as HTMLInputElement, 'click')
    await wrapper.findAll('button')[0].trigger('click')
    expect(click).toHaveBeenCalledOnce(); expect(media).not.toHaveBeenCalled()
    const file = new File(['photo'], 'photo.jpg', { type: 'image/jpeg' })
    Object.defineProperty(input.element, 'files', { configurable: true, value: [file] })
    await input.trigger('change'); await flushPromises()
    expect(decode).toHaveBeenCalledWith(file)
    expect(wrapper.emitted('decoded')).toEqual([['UPLM-PRODUCTION|release|lot']])
    expect(wrapper.text()).toContain('请核对批次后确认本次数量')
    wrapper.unmount()
  })
  it('照片没有二维码时给出中文提示，取消拍照不发出定位', async () => {
    environment(); vi.spyOn(qr, 'readProductionQrPhoto').mockRejectedValue(new Error('未识别到二维码，请重新拍摄。'))
    const wrapper = mount(ProductionCameraScanner), input = wrapper.get('input[capture=environment]')
    await input.trigger('change'); expect(wrapper.emitted('decoded')).toBeUndefined()
    Object.defineProperty(input.element, 'files', { configurable: true, value: [new File(['x'], 'photo.png', { type: 'image/png' })] })
    await input.trigger('change'); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('未识别到二维码')
    expect(wrapper.emitted('decoded')).toBeUndefined(); wrapper.unmount()
  })
  it('拒绝实时相机授权后可以改用拍照，不展示英文原始异常', async () => {
    environment(true, vi.fn().mockRejectedValue(new DOMException('Permission denied', 'NotAllowedError')))
    const wrapper = mount(ProductionCameraScanner)
    await wrapper.findAll('button')[0].trigger('click'); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('未获得相机权限')
    expect(wrapper.text()).not.toContain('Permission denied')
    expect(wrapper.find('video').exists()).toBe(false)
    const click = vi.spyOn(wrapper.get('input[capture=environment]').element as HTMLInputElement, 'click')
    await wrapper.findAll('button')[1].trigger('click'); expect(click).toHaveBeenCalledOnce(); wrapper.unmount()
  })
  it('实时识别成功只定位一次并释放摄像头', async () => {
    const stop = vi.fn(), media = environment(true, vi.fn().mockResolvedValue({ getTracks: () => [{ stop }] }))
    vi.mocked(qr.readProductionQrFrame).mockReturnValue('MODEL\nA')
    const wrapper = mount(ProductionCameraScanner)
    await wrapper.findAll('button')[0].trigger('click'); await flushPromises()
    expect(media).toHaveBeenCalledWith(expect.objectContaining({ audio: false, video: expect.objectContaining({ facingMode: { ideal: 'environment' } }) }))
    expect(wrapper.emitted('decoded')).toEqual([['MODEL\nA']]); expect(stop).toHaveBeenCalledOnce()
    expect(wrapper.find('video').exists()).toBe(false); wrapper.unmount()
  })
  it('关闭授权等待窗口后，迟到的摄像头流立即关闭', async () => {
    let complete!: (value: unknown) => void
    environment(true, vi.fn().mockReturnValue(new Promise(resolve => { complete = resolve })))
    const stop = vi.fn(), wrapper = mount(ProductionCameraScanner)
    await wrapper.findAll('button')[0].trigger('click')
    await wrapper.get('.production-camera__preview button').trigger('click')
    complete({ getTracks: () => [{ stop }] }); await flushPromises()
    expect(stop).toHaveBeenCalledOnce(); expect(wrapper.emitted('decoded')).toBeUndefined(); wrapper.unmount()
  })
  it('页面卸载时停止取景和摄像头', async () => {
    const stop = vi.fn(); environment(true, vi.fn().mockResolvedValue({ getTracks: () => [{ stop }] }))
    const wrapper = mount(ProductionCameraScanner)
    await wrapper.findAll('button')[0].trigger('click'); await flushPromises(); expect(wrapper.find('video').exists()).toBe(true)
    wrapper.unmount(); expect(stop).toHaveBeenCalledOnce()
  })
})
