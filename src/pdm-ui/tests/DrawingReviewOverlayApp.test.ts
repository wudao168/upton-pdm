import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DrawingReviewOverlayApp from '../src/components/DrawingReviewOverlayApp.vue'

describe('审核浮层全局主题', () => {
  afterEach(() => { delete document.documentElement.dataset.pdmTheme })

  it('requests the current state after mounting so navigation does not lose the initial theme', () => {
    const postMessage = vi.fn()
    const addEventListener = vi.fn()
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: { postMessage, addEventListener, removeEventListener: vi.fn() } } })
    const wrapper = mount(DrawingReviewOverlayApp, { global: { stubs: { DrawingReviewPanel: true } } })
    expect(addEventListener).toHaveBeenCalledWith('message', expect.any(Function))
    expect(postMessage).toHaveBeenCalledWith({ type: 'review-overlay-action', payload: { action: 'state-ready' } })
    expect(addEventListener.mock.invocationCallOrder[0]).toBeLessThan(postMessage.mock.invocationCallOrder[0]!)
    wrapper.unmount()
    delete window.chrome
  })

  it.each(['a', 'c', 'g'])('接收到%s主题时同步全局变量，覆盖浮层及传送弹窗', async theme => {
    let receive: (event: MessageEvent) => void = () => {}
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      postMessage: vi.fn(),
      addEventListener: vi.fn((_name, handler) => { receive = handler }),
      removeEventListener: vi.fn(),
    } } })
    const wrapper = mount(DrawingReviewOverlayApp, { global: { stubs: { DrawingReviewPanel: true } } })
    receive(new MessageEvent('message', { data: { type: 'review-overlay-state', payload: { theme } } }))
    await nextTick()
    expect(document.documentElement.dataset.pdmTheme).toBe(theme)
    expect(wrapper.classes()).toContain(`theme-${theme}`)
    wrapper.unmount()
    delete window.chrome
  })

  it('审核浮层按当前授权更新，撤销后不因默认审核人名单保留权限', async () => {
    let receive: (event: MessageEvent) => void = () => {}
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      postMessage: vi.fn(), addEventListener: vi.fn((_name, handler) => { receive = handler }), removeEventListener: vi.fn(),
    } } })
    const wrapper = mount(DrawingReviewOverlayApp, { global: { stubs: { DrawingReviewPanel: true } } })
    for (const canDecide of [false, true, false]) {
      receive(new MessageEvent('message', { data: { type: 'review-overlay-state', payload: { visible: true, currentUsername: 'reviewer', defaultReviewers: ['reviewer'], canDecide } } }))
      await nextTick()
      expect(wrapper.getComponent({ name: 'DrawingReviewPanel' }).props('canDecide')).toBe(canDecide)
    }
    wrapper.unmount()
    delete window.chrome
  })
})
