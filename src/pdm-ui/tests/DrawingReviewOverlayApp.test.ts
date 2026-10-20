import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DrawingReviewOverlayApp from '../src/components/DrawingReviewOverlayApp.vue'

describe('审核浮层全局主题', () => {
  afterEach(() => { delete document.documentElement.dataset.pdmTheme })

  it.each(['a', 'c', 'p', 'o', 'g'])('接收到%s主题时同步全局变量，覆盖浮层及传送弹窗', async theme => {
    let receive: (event: MessageEvent) => void = () => {}
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
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
})
