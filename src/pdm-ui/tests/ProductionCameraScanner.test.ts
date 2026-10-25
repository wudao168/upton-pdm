import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionCameraScanner from '../src/components/ProductionCameraScanner.vue'
import * as qr from '../src/productionQr'
import * as cameraAccess from '../src/productionCameraAccess'

function environment(secure = false, getUserMedia = vi.fn()) {
  vi.stubGlobal('isSecureContext', secure)
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } })
  return getUserMedia
}
beforeEach(() => {
  vi.spyOn(qr, 'readProductionQrFrame').mockResolvedValue(undefined)
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
  vi.spyOn(cameraAccess, 'readProductionCameraAccess').mockResolvedValue({ httpsScanUrl: 'https://192.168.2.8:5443/?productionScan=1',
    certificateUrl: './production-camera-ca.cer', androidCertificateUrl: './production-camera-ca.crt', certificateName: 'UPLM内网扫码根证书', certificateSha256: 'SHA256' })
})
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('移动生产摄像头扫码', () => {
  it('内网HTTP使用后置相机拍照入口，照片识别后只发出定位内容', async () => {
    const media = environment(), decode = vi.spyOn(qr, 'readProductionQrPhoto').mockResolvedValue('UPLM-PRODUCTION|release|lot')
    const wrapper = mount(ProductionCameraScanner)
    expect(wrapper.find('.production-camera__hint').exists()).toBe(false)
    const input = wrapper.get('input[capture=environment]'), click = vi.spyOn(input.element as HTMLInputElement, 'click')
    await wrapper.findAll('button').find(button => button.text() === '拍照扫码')!.trigger('click')
    expect(click).toHaveBeenCalledOnce(); expect(media).not.toHaveBeenCalled()
    const file = new File(['photo'], 'photo.jpg', { type: 'image/jpeg' })
    Object.defineProperty(input.element, 'files', { configurable: true, value: [file] })
    await input.trigger('change'); await flushPromises()
    expect(decode).toHaveBeenCalledWith(file)
    expect(wrapper.emitted('decoded')).toEqual([['UPLM-PRODUCTION|release|lot']])
    expect(wrapper.find('[role=status]').exists()).toBe(false)
    wrapper.unmount()
  })
  it('HTTP也显示摄像头扫码，点击后提供HTTPS入口和手机证书配置，不伪装为拍照', async () => {
    const media = environment(), wrapper = mount(ProductionCameraScanner)
    const click = vi.spyOn(wrapper.get('input[capture=environment]').element as HTMLInputElement, 'click')
    await wrapper.findAll('button').find(button => button.text() === '摄像头扫码')!.trigger('click'); await flushPromises()
    const setup = wrapper.get('[aria-label="开启浏览器摄像头扫码"]')
    expect(setup.get('a').attributes('href')).toBe('https://192.168.2.8:5443/?productionScan=1')
    expect(setup.text()).toContain('证书信任设置'); expect(setup.text()).toContain('Android首次配置')
    expect(media).not.toHaveBeenCalled(); expect(click).not.toHaveBeenCalled(); wrapper.unmount()
  })
  it('安全扫码入口读取失败时展示中文原因，保留摄像头和拍照入口', async () => {
    environment(); vi.mocked(cameraAccess.readProductionCameraAccess).mockRejectedValue(new Error('Failed to fetch'))
    const wrapper = mount(ProductionCameraScanner)
    await wrapper.findAll('button')[0].trigger('click'); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toBe('暂时无法读取安全扫码配置，请刷新后重试。')
    expect(wrapper.findAll('.production-camera__actions button').map(button => button.text())).toEqual(['摄像头扫码','拍照扫码','识别相册图片'])
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
    vi.mocked(qr.readProductionQrFrame).mockResolvedValue('MODEL\nA')
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
  it('异步识别期间关闭相机，不使用迟到的结果定位任务', async () => {
    let complete!: (code: string) => void
    vi.mocked(qr.readProductionQrFrame).mockReturnValue(new Promise(resolve => { complete = resolve }))
    const stop = vi.fn(); environment(true, vi.fn().mockResolvedValue({ getTracks: () => [{ stop }] }))
    const wrapper = mount(ProductionCameraScanner)
    await wrapper.findAll('button')[0].trigger('click'); await flushPromises()
    await wrapper.get('.production-camera__preview button').trigger('click')
    complete('MODEL\nW1'); await flushPromises()
    expect(wrapper.emitted('decoded')).toBeUndefined(); expect(stop).toHaveBeenCalledOnce(); wrapper.unmount()
  })
  it('支持的相机启用连续对焦，放大倍率只在支持的设备上显示和调整', async () => {
    const track = { stop: vi.fn(), applyConstraints: vi.fn().mockResolvedValue(undefined),
      getCapabilities: () => ({ focusMode: ['continuous'], zoom: { min: 1, max: 3, step: .1 } }), getSettings: () => ({ zoom: 1 }) }
    const media = environment(true, vi.fn().mockResolvedValue({ getTracks: () => [track], getVideoTracks: () => [track] }))
    const wrapper = mount(ProductionCameraScanner)
    await wrapper.findAll('button')[0].trigger('click'); await flushPromises()
    expect(media).toHaveBeenCalledWith(expect.objectContaining({ video: expect.objectContaining({ width: { ideal: 1920 }, height: { ideal: 1080 } }) }))
    expect(track.applyConstraints).toHaveBeenCalledWith({ advanced: [{ focusMode: 'continuous' }] })
    const zoom = wrapper.get('input[aria-label="相机放大倍率"]')
    await zoom.setValue(2); await flushPromises()
    expect(track.applyConstraints).toHaveBeenCalledWith({ advanced: [{ zoom: 2 }] }); wrapper.unmount()
  })
})
