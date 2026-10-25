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
  afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = ''; vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers() })

  it('reveals fallback native bars only at their gutter and retains visibility while dragging', async () => {
    vi.stubGlobal('CSS', { supports: () => false })
    const textarea = document.createElement('textarea')
    textarea.style.overflowX = 'auto'; textarea.style.overflowY = 'auto'
    Object.defineProperties(textarea, { clientWidth:{value:88},clientHeight:{value:88},offsetWidth:{value:100},offsetHeight:{value:100},scrollWidth:{value:200},scrollHeight:{value:200} })
    vi.spyOn(textarea,'getBoundingClientRect').mockReturnValue({left:10,top:10,right:110,bottom:110,width:100,height:100,x:10,y:10,toJSON:()=>({})})
    document.body.append(textarea);install();await flush()
    const pointer = (type:string,x:number,y:number) => textarea.dispatchEvent(new MouseEvent(type,{bubbles:true,clientX:x,clientY:y,button:0}))
    pointer('pointermove',50,50);expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(false)
    pointer('pointermove',105,50);expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(true)
    pointer('pointermove',50,50);expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(false)
    pointer('pointermove',50,105);expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(true)
    pointer('pointerdown',105,50);pointer('pointermove',200,200)
    expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(true)
    pointer('pointerup',200,200);expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(false)
    pointer('pointermove',105,50);dispose?.();dispose=undefined
    expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(false)
    pointer('pointermove',105,50);expect(textarea.hasAttribute('data-pdm-native-scrollbar-hover')).toBe(false)
  })

  it('keeps native hover handling in CSS for Chromium', async () => {
    vi.stubGlobal('CSS', { supports: () => true })
    const add=vi.spyOn(document,'addEventListener');install();await flush()
    expect(add.mock.calls.some(([name])=>name==='pointermove')).toBe(false)
  })

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
