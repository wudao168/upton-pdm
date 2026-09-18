import ElementPlus from 'element-plus'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import DrawingReviewOverlayApp from '../src/components/DrawingReviewOverlayApp.vue'

describe('DrawingReviewOverlayApp', () => {
  beforeEach(() => {
    window.localStorage.clear()
    Object.defineProperty(window, 'chrome', { configurable: true, value: undefined })
  })

  it('renders state from the desktop host and returns actions to the main page', async () => {
    const postMessage = vi.fn()
    let messageListener: ((event: MessageEvent) => void) | undefined
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: {
        webview: {
          postMessage,
          addEventListener: vi.fn((_type: 'message', listener: (event: MessageEvent) => void) => { messageListener = listener }),
          removeEventListener: vi.fn(),
        },
      },
    })
    const wrapper = mount(DrawingReviewOverlayApp, { attachTo: document.body, global: { plugins: [ElementPlus] } })

    messageListener?.(new MessageEvent('message', {
      data: {
        type: 'review-overlay-state',
        payload: {
          visible: true,
          packageId: '',
          packages: [],
          selectedDocumentId: 'doc-root',
          currentUsername: 'reviewer',
          pending: false,
          canSubmit: true,
          canAnnotate: true,
          canDecide: true,
          allowSelfReview: true,
          collapsed: false,
          theme: 'a',
        },
      },
    }))
    await flushPromises()

    expect(wrapper.find('[aria-label="图纸审核面板"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="关闭图纸审核面板"]').exists()).toBe(false)
    await wrapper.get('[aria-label="折叠图纸审核栏"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'review-overlay-action', payload: { action: 'collapse', collapsed: true } })

    messageListener?.(new MessageEvent('message', { data: { type: 'review-overlay-state', payload: { collapsed: true } } }))
    await flushPromises()
    expect(wrapper.find('[aria-label="展开图纸审核栏"]').exists()).toBe(true)

    wrapper.unmount()
  })
})
