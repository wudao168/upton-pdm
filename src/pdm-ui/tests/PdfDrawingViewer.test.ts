import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import PdfDrawingViewer from '../src/components/PdfDrawingViewer.vue'

const pdf = vi.hoisted(() => ({ getPage: vi.fn(), destroy: vi.fn() }))
vi.mock('pdfjs-dist/legacy/build/pdf.mjs', () => ({
  GlobalWorkerOptions: {},
  getDocument: () => ({ promise: Promise.resolve({ numPages: 3, getPage: pdf.getPage }), destroy: pdf.destroy }),
}))
vi.mock('pdfjs-dist/legacy/build/pdf.worker.min.mjs?url', () => ({ default: 'pdf-worker.mjs' }))
let now = 1000
beforeEach(() => {
  now = 1000
  vi.spyOn(performance, 'now').mockImplementation(() => now)
  pdf.getPage.mockClear().mockResolvedValue({
    getViewport: ({ scale }: { scale: number }) => ({ width: 800 * scale, height: 600 * scale }),
    render: () => ({ promise: Promise.resolve(), cancel: vi.fn() }),
  })
  pdf.destroy.mockResolvedValue(undefined)
})
afterEach(() => vi.restoreAllMocks())

async function viewer() {
  const wrapper = mount(PdfDrawingViewer, { props: { url: 'sample.pdf' } })
  await flushPromises()
  const frame = wrapper.get('[aria-label="图纸查看区域"]').element as HTMLDivElement
  Object.defineProperties(frame, { clientHeight: { value: 600 }, scrollHeight: { value: 600, configurable: true } })
  frame.scrollTo = vi.fn((leftOrOptions: number | ScrollToOptions = 0, top = 0) => {
    const y = typeof leftOrOptions === 'object' ? leftOrOptions.top ?? frame.scrollTop : top
    frame.scrollTop = Math.max(0, Math.min(y, frame.scrollHeight - frame.clientHeight))
  })
  const wheel = async (deltaY: number, extra: WheelEventInit = {}) => {
    const event = new WheelEvent('wheel', { deltaY, bubbles: true, cancelable: true, ...extra })
    frame.dispatchEvent(event);await flushPromises();return event
  }
  return { wrapper, frame, wheel }
}

it('适应窗口时普通滚轮上下翻页，首尾不会越界', async () => {
  const { wrapper, wheel } = await viewer()
  expect((await wheel(100)).defaultPrevented).toBe(true)
  expect(wrapper.text()).toContain('2 / 3 页')
  now += 301;await wheel(100);expect(wrapper.text()).toContain('3 / 3 页')
  now += 301;await wheel(100);expect(wrapper.text()).toContain('3 / 3 页')
  now += 301;await wheel(-100);expect(wrapper.text()).toContain('2 / 3 页')
  now += 301;await wheel(-100);expect(wrapper.text()).toContain('1 / 3 页')
  now += 301;await wheel(-100)
  expect(pdf.getPage.mock.calls.every(([page]) => page >= 1 && page <= 3)).toBe(true)
  wrapper.unmount()
})

it('放大后先滚动本页，到底翻下一页，向上翻页后从上一页底部继续', async () => {
  const { wrapper, frame, wheel } = await viewer()
  Object.defineProperty(frame, 'scrollHeight', { value: 1200, configurable: true })
  frame.scrollTop = 100
  expect((await wheel(100)).defaultPrevented).toBe(false)
  expect(wrapper.text()).toContain('1 / 3 页')
  frame.scrollTop = 600;await wheel(100);expect(wrapper.text()).toContain('2 / 3 页')
  expect(frame.scrollTop).toBe(0)
  now += 301;await wheel(-100);expect(wrapper.text()).toContain('1 / 3 页')
  expect(frame.scrollTop).toBe(600)
  wrapper.unmount()
})

it('连续滚轮事件不会瞬间跳多页，横向或Shift滚动不翻页', async () => {
  const { wrapper, wheel } = await viewer()
  await wheel(100);await wheel(100);await wheel(100)
  expect(wrapper.text()).toContain('2 / 3 页')
  now += 301;await wheel(10, { deltaX: 100 });await wheel(100, { shiftKey: true })
  expect(wrapper.text()).toContain('2 / 3 页')
  await wheel(100);expect(wrapper.text()).toContain('3 / 3 页')
  wrapper.unmount()
})

it('Ctrl加滚轮仍只缩放，切换文件后从第一页开始并清除翻页节流', async () => {
  const { wrapper, wheel } = await viewer()
  await wheel(-100, { ctrlKey: true });expect(wrapper.text()).toContain('125%');expect(wrapper.text()).toContain('1 / 3 页')
  await wheel(100);expect(wrapper.text()).toContain('2 / 3 页')
  await wrapper.setProps({ url: 'another.pdf' });await flushPromises()
  expect(wrapper.text()).toContain('1 / 3 页')
  await wheel(100);expect(wrapper.text()).toContain('2 / 3 页')
  wrapper.unmount()
})
