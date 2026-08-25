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
          theme: 'a',
        },
      },
    }))
    await flushPromises()

    expect(wrapper.find('[aria-label="图纸审核面板"]').exists()).toBe(true)
    await wrapper.get('[aria-label="关闭图纸审核面板"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'review-overlay-action', payload: { action: 'close' } })

    wrapper.unmount()
  })
})
