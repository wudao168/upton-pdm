import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import { ElTable, ElTableColumn } from 'element-plus'
import { installColumnResize, readableColumnWidths, resizeToFit } from '../src/columnResize'
import * as tablePreferences from '../src/tablePreferences'

let dispose: () => void

it('宽字段目录恢复旧列宽时保留可读下限和固定选择列', () => {
  expect(readableColumnWidths([12, 40, 60], [38, 140, 180], 240, [true, false, false])).toEqual([38, 140, 180])
  const wide = readableColumnWidths([38, 140, 180], [38, 140, 180], 800, [true, false, false])
  expect(wide[0]).toBe(38)
  expect(wide[1]).toBeCloseTo(762 * 140 / 320)
  expect(wide.reduce((sum, width) => sum + width, 0)).toBeCloseTo(800)
})

it('宽字段目录保留用户放大的列，不挤压其他列来适应窄窗口', () => {
  expect(readableColumnWidths([38, 240, 220], [38, 140, 180], 300, [true, false, false])).toEqual([38, 240, 220])
})
function setup(html: string, width: number) {
  document.body.innerHTML = html
  Object.defineProperty(document.body.firstElementChild!, 'clientWidth', { configurable: true, value: width })
  const app = createApp({})
  vi.spyOn(app, 'onUnmount').mockImplementation(callback => { dispose = callback; return app })
  installColumnResize(app)
}
function rect(element: Element, left: number, width: number) {
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({ left, right: left + width, width, top: 0, bottom: 30, height: 30, x: left, y: 0, toJSON: () => ({}) })
}
function pointer(element: Element | Document, type: string, x: number) {
  element.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, button: 0 }))
}
afterEach(() => { dispose?.(); document.body.innerHTML = ''; window.localStorage.clear(); vi.restoreAllMocks() })

describe('global column resizing', () => {
  it('keeps native fixed columns at 50px while resizing and restoring other columns', () => {
    const html = '<div data-pdm-preference-scope=\'["alice","company"]\'><table><colgroup><col data-pdm-fixed-width="50" style="width:50px"><col><col data-pdm-fixed-width="50" style="width:50px"><col></colgroup><thead><tr><th>选择</th><th>图号</th><th>等级</th><th>材质</th></tr></thead></table></div>'
    setup(html, 400)
    const headers = document.querySelectorAll('th')
    ;[[0,50],[50,150],[200,50],[250,150]].forEach(([left,width],index) => rect(headers[index]!,left!,width!))
    pointer(headers[0]!, 'pointerdown', 48)
    expect(document.body.classList.contains('pdm-column-resizing')).toBe(false)
    pointer(headers[1]!, 'pointerdown', 198); pointer(document, 'pointermove', 238); pointer(document, 'pointerup', 238)
    const columns = document.querySelectorAll('col')
    expect(columns[0]!.style.width).toBe('50px')
    expect(columns[2]!.style.width).toBe('50px')
    expect(columns[1]!.style.width).toContain('calc(')
    const flexibleWidth = columns[1]!.style.width
    dispose(); setup(html, 800)
    const restored = document.querySelectorAll('col')
    expect(restored[0]!.style.width).toBe('50px')
    expect(restored[2]!.style.width).toBe('50px')
    expect(restored[1]!.style.width).toBe(flexibleWidth)
  })
  it('restores native column proportions after remount and isolates accounts and changed schemas', () => {
    const html = '<div data-pdm-preference-scope=\'["alice","company"]\' data-navigation-page="projects"><table><thead><tr><th>名称</th><th>操作</th></tr></thead></table></div>'
    setup(html,400)
    const headers=document.querySelectorAll('th');rect(headers[0]!,0,160);rect(headers[1]!,160,240)
    pointer(headers[0]!,'pointerdown',158);pointer(document,'pointermove',198);pointer(document,'pointerup',198)
    expect(window.localStorage.length).toBe(1)
    dispose();setup(html,800)
    expect([...document.querySelectorAll('col')].map(x=>parseFloat(x.style.width))).toEqual([50,50])
    dispose();setup(html.replace('alice','bob'),800)
    expect(document.querySelector('colgroup')).toBeNull()
    dispose();setup(html.replace('名称','料号'),800)
    expect(document.querySelector('colgroup')).toBeNull()
    dispose();setup(html.replace('company','another-company'),800)
    expect(document.querySelector('colgroup')).toBeNull()
  })
  it('restores lazily recreated tables and ignores corrupt stored widths', async () => {
    const html='<div data-pdm-preference-scope=\'["alice","company"]\'><table><thead><tr><th>名称</th><th>操作</th></tr></thead></table></div>'
    setup(html,300)
    const headers=document.querySelectorAll('th');rect(headers[0]!,0,150);rect(headers[1]!,150,150)
    pointer(headers[0]!,'pointerdown',148);pointer(document,'pointermove',178);pointer(document,'pointerup',178)
    document.body.firstElementChild!.innerHTML='<table><thead><tr><th>名称</th><th>操作</th></tr></thead></table>'
    await new Promise(resolve=>setTimeout(resolve,0))
    expect(parseFloat(document.querySelector('col')!.style.width)).toBeCloseTo(60)
    const key=window.localStorage.key(0)!
    window.localStorage.setItem(key,'[-1,0]')
    dispose();setup(html,300)
    expect(document.querySelector('colgroup')).toBeNull()
  })
  it.each(['gantt','browser'])('restores saved %s grid columns after remount', kind => {
    const gantt=kind==='gantt'
    const rootClass=gantt?'pdm-gantt-table':'pdm-project-browser-list'
    const headClass=gantt?'pdm-gantt-info-head':'pdm-project-browser-table-head'
    const html=`<div class="${rootClass}" data-pdm-preference-scope='["alice","company"]'><div class="${headClass}"><span>名称</span><span>责任人</span></div></div>`
    setup(html,400)
    const headers=document.querySelectorAll(`.${headClass} span`);rect(headers[0]!,0,150);rect(headers[1]!,150,250)
    pointer(headers[0]!,'pointerdown',148);pointer(document,'pointermove',178);pointer(document,'pointerup',178)
    dispose();setup(html,400)
    expect((document.body.firstElementChild as HTMLElement).style.getPropertyValue(gantt?'--pdm-resized-gantt-columns':'--pdm-resized-list-columns')).toBe(gantt?'180px 220px':'180fr 220fr')
  })
  it('continues resizing when browser storage is unavailable', () => {
    setup('<div data-pdm-preference-scope=\'["alice","company"]\'><table><thead><tr><th>名称</th><th>操作</th></tr></thead></table></div>',300)
    vi.spyOn(Storage.prototype,'setItem').mockImplementation(()=>{throw new Error('quota')})
    const headers=document.querySelectorAll('th');rect(headers[0]!,0,150);rect(headers[1]!,150,150)
    pointer(headers[0]!,'pointerdown',148);pointer(document,'pointermove',178)
    expect(()=>pointer(document,'pointerup',178)).not.toThrow()
    expect(parseFloat(document.querySelector('col')!.style.width)).toBeCloseTo(60)
  })
  it('fits initial Element Plus columns and refits after a window resize', async () => {
    const Original = globalThis.ResizeObserver
    const observers: Array<{ callback: ResizeObserverCallback; root?: Element }> = []
    globalThis.ResizeObserver = class {
      entry: { callback: ResizeObserverCallback; root?: Element }
      constructor(callback: ResizeObserverCallback) { this.entry = { callback }; observers.push(this.entry) }
      observe(root: Element) { this.entry.root = root }
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver
    const wrapper = mount(defineComponent({ components: { ElTable, ElTableColumn }, template: '<el-table :data="[]"><el-table-column label="名称" width="200"/><el-table-column label="操作" width="100"/></el-table>' }), { global: { plugins: [{ install: installColumnResize }] } })
    try {
      await nextTick()
      const root = wrapper.element
      const table = wrapper.findComponent({ name: 'ElTable' }).vm.$.exposed!
      const observer = observers.find(item => item.root === root && item.callback.toString().includes('const space'))!
      Object.defineProperty(root, 'clientWidth', { configurable: true, value: 600 })
      observer.callback([], {} as ResizeObserver)
      await nextTick()
      expect(table.store.states.columns.value.reduce((sum: number, column: { realWidth: number }) => sum + column.realWidth, 0)).toBeCloseTo(600)
      Object.defineProperty(root, 'clientWidth', { configurable: true, value: 240 })
      observer.callback([], {} as ResizeObserver)
      await nextTick()
      expect(table.store.states.columns.value.reduce((sum: number, column: { realWidth: number }) => sum + column.realWidth, 0)).toBeCloseTo(240)
    } finally { wrapper.unmount(); globalThis.ResizeObserver = Original }
  })
  it('redistributes width to the right and keeps the total fixed', () => {
    expect(resizeToFit([100, 100, 100], 0, 40)).toEqual([140, 80, 80])
    expect(resizeToFit([100, 100, 100], 1, -30)).toEqual([100, 70, 130])
    expect(resizeToFit([100, 100, 100], 2, 40)).toEqual([100, 60, 140])
    expect(resizeToFit([100, 100, 100], 0, 500)).toEqual([236, 32, 32])
    expect(resizeToFit([100], 0, 40)).toEqual([100])
  })
  it('恢复账号列宽和窗口缩放时保留固定状态列，其余列按保存比例适配且不反复重排', async () => {
    const Original = globalThis.ResizeObserver
    const observers: Array<{ callback: ResizeObserverCallback; root?: Element }> = []
    globalThis.ResizeObserver = class {
      entry: { callback: ResizeObserverCallback; root?: Element }
      constructor(callback: ResizeObserverCallback) { this.entry = { callback }; observers.push(this.entry) }
      observe(root: Element) { this.entry.root = root }
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver
    vi.spyOn(tablePreferences, 'readTablePreference').mockReturnValue('[56,150,300]')
    const host = document.createElement('div')
    host.dataset.pdmPreferenceScope = '["alice","company"]'
    document.body.append(host)
    const wrapper = mount(defineComponent({ components: { ElTable, ElTableColumn }, template: '<el-table :data="[]"><el-table-column label="状态" width="56" class-name="pdm-column-fixed-width"/><el-table-column label="说明" width="200"/><el-table-column label="操作" width="100"/></el-table>' }), { attachTo: host, global: { plugins: [{ install: installColumnResize }] } })
    try {
      await nextTick()
      const root = wrapper.element
      const table = wrapper.findComponent({ name: 'ElTable' }).vm.$.exposed!
      const observer = observers.find(item => item.root === root && item.callback.toString().includes('const space'))!
      for (const space of [1200, 540, 240]) {
        Object.defineProperty(root, 'clientWidth', { configurable: true, value: space })
        observer.callback([], {} as ResizeObserver)
        await nextTick()
        const columns = table.store.states.columns.value
        expect(columns[0]!.realWidth).toBe(56)
        expect(columns[2]!.realWidth).toBeCloseTo(columns[1]!.realWidth * 2)
        expect(columns.reduce((sum: number, column: { realWidth: number }) => sum + column.realWidth, 0)).toBeCloseTo(space)
        const settled = columns.map((column: { realWidth: number }) => column.realWidth)
        observer.callback([], {} as ResizeObserver)
        await nextTick()
        expect(columns.map((column: { realWidth: number }) => column.realWidth)).toEqual(settled)
      }
      expect(table.store.states.columns.value.map((column: { resizable: boolean }) => column.resizable)).toEqual([false, true, true])
    } finally { wrapper.unmount(); globalThis.ResizeObserver = Original }
  })
  it('enables real Element Plus selection and index columns after mount', async () => {
    const wrapper = mount(defineComponent({
      components: { ElTable, ElTableColumn },
      template: '<el-table border :data="[{ name: \'零件\' }]"><el-table-column type="selection"/><el-table-column type="index"/><el-table-column prop="name" label="名称"/></el-table>',
    }), { global: { plugins: [{ install: installColumnResize }] } })
    await nextTick()
    try {
      const table = wrapper.findComponent({ name: 'ElTable' }).vm
      expect(table.$.exposed!.store.states.columns.value.map((column: { resizable: boolean }) => column.resizable)).toEqual([true, true, true])
    } finally { wrapper.unmount() }
  })
  it('resizes the last column while fitting the container and suppressing sorting clicks', () => {
    setup('<div><table><thead><tr><th>名称</th><th>操作</th></tr></thead><tbody><tr><td>零件</td><td><button>打开</button></td></tr></tbody></table></div>', 180)
    const [first, last] = document.querySelectorAll('th')
    rect(first!, 0, 100); rect(last!, 100, 80)
    const sort = vi.fn(); last!.addEventListener('click', sort)
    pointer(last!, 'pointerdown', 178); pointer(document, 'pointermove', 218); pointer(document, 'pointerup', 218)
    last!.click()
    expect(sort).not.toHaveBeenCalled()
    const widths = [...document.querySelectorAll('col')].map(col => parseFloat(col.style.width))
    expect(widths[0]).toBeCloseTo(100 / 3)
    expect(widths[1]).toBeCloseTo(200 / 3)
    expect(widths.reduce((sum, width) => sum + width, 0)).toBeCloseTo(100)
    expect(document.querySelector('table')!.style.width).toBe('100%')
    expect(document.querySelector('tbody button')!.textContent).toBe('打开')
  })
  it('窄屏内部滚动的原生表格按实际表宽调整，不放大拖动位移', () => {
    setup('<div><table><thead><tr><th>料号</th><th>名称</th></tr></thead></table></div>', 180)
    Object.defineProperty(document.querySelector('table')!, 'clientWidth', { configurable: true, value: 300 })
    const headers = document.querySelectorAll('th')
    rect(headers[0]!, 0, 100); rect(headers[1]!, 100, 200)
    pointer(headers[0]!, 'pointerdown', 98); pointer(document, 'pointermove', 138)
    expect(parseFloat(document.querySelector('col')!.style.width)).toBeCloseTo(140 / 300 * 100)
    pointer(document, 'pointerup', 138)
  })
  it('leaves ordinary clicks and initial layout unchanged and cancels dragging cleanly', () => {
    setup('<div><table><thead><tr><th>名称</th><th>操作</th></tr></thead></table></div>', 180)
    const [header, last] = document.querySelectorAll('th');rect(header!, 0, 100);rect(last!,100,80)
    pointer(header!, 'pointerdown', 40); pointer(document, 'pointermove', 80)
    expect(document.querySelector('colgroup')).toBeNull()
    pointer(header!, 'pointerdown', 98); pointer(document, 'pointermove', -100)
    expect(parseFloat(document.querySelector('col')!.style.width)).toBeCloseTo(32 / 180 * 100)
    pointer(document, 'pointercancel', -100)
    expect(document.documentElement.classList.contains('pdm-column-resizing')).toBe(false)
  })
  it('keeps grouped headers aligned with resized leaf columns', () => {
    setup('<div><table><thead><tr><th rowspan="2">名称</th><th colspan="2">数量</th></tr><tr><th>计划</th><th>实际</th></tr></thead></table></div>',260)
    const headers = document.querySelectorAll('th')
    rect(headers[0]!, 0, 100);rect(headers[1]!, 100, 160);rect(headers[2]!, 100, 80);rect(headers[3]!, 180, 80)
    pointer(headers[2]!, 'pointerdown', 178); pointer(document, 'pointermove', 218)
    const widths=[...document.querySelectorAll('col')].map(col=>parseFloat(col.style.width))
    expect(widths[1]).toBeCloseTo(120 / 260 * 100)
    expect(widths[2]).toBeCloseTo(40 / 260 * 100)
    expect(parseFloat(headers[1]!.style.width)).toBeCloseTo(160 / 260 * 100)
  })
  it('keeps the gantt information pane width fixed and its row columns synchronized', () => {
    setup('<div><div class="pdm-gantt-table"><div class="pdm-gantt-info-head"><span>任务</span><span>责任人</span></div><div class="pdm-gantt-info-row"><span>设计</span><span>工程师</span></div></div></div>',500)
    const headers = document.querySelectorAll('.pdm-gantt-info-head span')
    rect(headers[0]!, 0, 150);rect(headers[1]!, 150, 80)
    pointer(headers[0]!, 'pointerdown', 148);pointer(document, 'pointermove', 178)
    const gantt = document.querySelector<HTMLElement>('.pdm-gantt-table')!
    expect(gantt.style.getPropertyValue('--pdm-resized-gantt-columns')).toBe('180px 50px')
    expect(gantt.style.getPropertyValue('--pdm-resized-gantt-width')).toBe('230px')
  })
  it('also resizes project browser grid columns without changing option buttons', () => {
    setup('<div class="pdm-project-browser-list"><div class="pdm-project-browser-table-head"><span>项目号</span><span>名称</span></div><button role="option"><strong>P700014</strong><span>测试项目</span></button></div>',400)
    const headers=document.querySelectorAll('.pdm-project-browser-table-head span')
    rect(headers[0]!,0,150);rect(headers[1]!,150,250)
    pointer(headers[0]!,'pointerdown',148);pointer(document,'pointermove',178)
    const list=document.querySelector<HTMLElement>('.pdm-project-browser-list')!
    expect(list.style.getPropertyValue('--pdm-resized-list-columns')).toBe('180fr 220fr')
    expect(list.querySelector('button')!.getAttribute('role')).toBe('option')
  })
  it('removes global listeners on teardown', () => {
    setup('<div><table><thead><tr><th>名称</th></tr></thead></table></div>',100)
    const header = document.querySelector('th')!;rect(header,0,100)
    dispose();pointer(header,'pointerdown',98);pointer(document,'pointermove',140)
    expect(document.querySelector('colgroup')).toBeNull()
  })
})
