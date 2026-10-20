import type { App } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OverlayScrollbars } from 'overlayscrollbars'
import { installGlobalScrollbars } from '../src/globalScrollbars'

vi.mock('overlayscrollbars', () => ({
  OverlayScrollbars: Object.assign(vi.fn(() => ({ destroy: vi.fn() })), { plugin: vi.fn() }),
  ClickScrollPlugin: {},
}))

describe('global content scrollbars', () => {
  let dispose: (() => void) | undefined
  const install = () => installGlobalScrollbars({ onUnmount(callback: () => void) { dispose = callback } } as App)
  const flush = () => vi.advanceTimersByTimeAsync(32)
  beforeEach(() => { vi.useFakeTimers(); vi.mocked(OverlayScrollbars).mockClear() })
  afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = ''; vi.useRealTimers() })

  it('uses each original scroll viewport without reparenting business content', async () => {
    document.body.innerHTML = '<section style="overflow-y:auto;overflow-x:hidden"><header>固定表头</header><div style="overflow-x:auto;overflow-y:auto">嵌套列表</div></section>'
    const section = document.querySelector('section')!
    const header = section.firstElementChild
    install(); await flush()
    expect(OverlayScrollbars).toHaveBeenCalledTimes(2)
    expect(OverlayScrollbars).toHaveBeenCalledWith({ target: section, elements: { viewport: section } }, {
      overflow: { x: 'hidden', y: 'scroll' },
      scrollbars: { theme: 'os-theme-pdm', autoHide: 'never', visibility: 'auto', clickScroll: true },
    })
    expect(section.firstElementChild).toBe(header)
    expect(header?.parentElement).toBe(section)
  })

  it('does not add a second scrollbar to Element Plus or the library measurement elements', async () => {
    document.body.innerHTML = '<div class="el-scrollbar"><div style="overflow:auto">表格</div></div><div class="os-environment" style="overflow:scroll"></div><textarea style="overflow:auto"></textarea>'
    install(); await flush()
    expect(OverlayScrollbars).not.toHaveBeenCalled()
  })

  it('covers lazy dialogs and changes in overflow, and releases removed viewports', async () => {
    install(); await flush()
    const viewport = document.createElement('section')
    viewport.style.overflowY = 'auto'
    document.body.append(viewport); await flush()
    expect(OverlayScrollbars).toHaveBeenCalledTimes(1)
    const instance = vi.mocked(OverlayScrollbars).mock.results[0]!.value
    viewport.remove(); await flush()
    expect(instance.destroy).toHaveBeenCalledOnce()
    const dialog = document.createElement('div')
    document.body.append(dialog); await flush()
    dialog.style.overflowX = 'auto'; await flush()
    expect(OverlayScrollbars).toHaveBeenCalledTimes(2)
    dispose?.(); dispose = undefined
    expect(vi.mocked(OverlayScrollbars).mock.results[1]!.value.destroy).toHaveBeenCalledOnce()
    document.body.append(document.createElement('section')); await flush()
    expect(OverlayScrollbars).toHaveBeenCalledTimes(2)
  })
})
