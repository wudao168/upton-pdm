import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, nextTick } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { ElTable, ElTableColumn } from 'element-plus'
import { installColumnResize, fitColumnWidths, readableColumnWidths, resizeColumn } from '../src/columnResize'
import * as tablePreferences from '../src/tablePreferences'

let dispose: () => void

it('旧像素列宽在不同容器中适配左右边界且保留用户分配', () => {
  expect(fitColumnWidths([50, 200, 150], 200)).toEqual([25, 100, 75])
  expect(fitColumnWidths([50, 200, 150], 800)).toEqual([100, 400, 300])
})

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
  it('BOM紧凑列兼容旧列宽，其余列保留分配；再次拖动后记忆新宽度', () => {
    const html = '<div data-pdm-preference-scope=\'["alice","company"]\'><table class="pdm-bom-table"><thead><tr><th>选择</th><th>操作</th><th>序号</th><th>名称</th><th>型号</th></tr></thead></table></div>'
    setup(html, 1000)
    let headers = document.querySelectorAll('th')
    ;[[0,100],[100,150],[250,100],[350,300],[650,350]].forEach(([left,width],index) => rect(headers[index]!,left!,width!))
    pointer(headers[0]!, 'pointerdown', 98); pointer(document, 'pointermove', 98); pointer(document, 'pointerup', 98)
    const compact = html.replace('class="pdm-bom-table"', 'class="pdm-bom-table" data-pdm-compact-columns="28,80,36"')
    dispose(); setup(compact, 1000)
    let columns = document.querySelectorAll('col')
    expect([...columns].slice(0,3).map(col=>col.style.width)).toEqual(['28px','80px','36px'])
    expect(Number(columns[3]!.style.width.match(/calc\(([\d.]+)%/)?.[1])).toBeCloseTo(300 / 650 * 100, 3)
    expect(Number(columns[4]!.style.width.match(/calc\(([\d.]+)%/)?.[1])).toBeCloseTo(350 / 650 * 100, 3)
    expect(columns[0]!.style.getPropertyPriority('width')).toBe('important')
    headers = document.querySelectorAll('th')
    ;[[0,28],[28,80],[108,36],[144,856*300/650],[144+856*300/650,856*350/650]].forEach(([left,width],index)=>rect(headers[index]!,left!,width!))
    pointer(headers[0]!, 'pointerdown', 26); pointer(document, 'pointermove', 38); pointer(document, 'pointerup', 38)
    expect(window.localStorage.length).toBe(2)
    dispose(); setup(compact, 1000)
    columns = document.querySelectorAll('col')
    expect(parseFloat(columns[0]!.style.width)).toBeCloseTo(4)
    expect(parseFloat(columns[1]!.style.width)).toBeCloseTo(6.8)
    expect(parseFloat(columns[2]!.style.width)).toBeCloseTo(3.6)
  })
  it('自由调整原固定列并保持表格百分比总宽', () => {
    const html = '<div data-pdm-preference-scope=\'["alice","company"]\'><table><colgroup><col data-pdm-fixed-width="50" style="width:50px"><col><col data-pdm-fixed-width="50" style="width:50px"><col></colgroup><thead><tr><th>选择</th><th>图号</th><th>等级</th><th>材质</th></tr></thead></table></div>'
    setup(html, 400)
    const headers = document.querySelectorAll('th')
    ;[[0,50],[50,150],[200,50],[250,150]].forEach(([left,width],index) => rect(headers[index]!,left!,width!))
    pointer(headers[0]!, 'pointerdown', 48)
    pointer(document, 'pointermove', 68); pointer(document, 'pointerup', 68)
    ;[[0,70],[70,130],[200,50],[250,150]].forEach(([left,width],index) => rect(headers[index]!,left!,width!))
    pointer(headers[1]!, 'pointerdown', 198); pointer(document, 'pointermove', 238); pointer(document, 'pointerup', 238)
    const columns = document.querySelectorAll('col')
    expect(columns[0]!.style.width).toBe('17.5%')
    expect(columns[2]!.style.width).toBe('8%')
    expect(columns[1]!.style.width).toBe('42.5%'); expect(columns[3]!.style.width).toBe('32%')
    const flexibleWidth = columns[1]!.style.width
    dispose(); setup(html, 800)
    const restored = document.querySelectorAll('col')
    expect(restored[0]!.style.width).toBe('17.5%')
    expect(restored[2]!.style.width).toBe('8%')
    expect(restored[1]!.style.width).toBe(flexibleWidth)
  })
  it('恢复用户列宽分配并隔离账号和表头结构', () => {
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
    expect((document.body.firstElementChild as HTMLElement).style.getPropertyValue(gantt?'--pdm-resized-gantt-columns':'--pdm-resized-list-columns')).toBe(gantt ? '180px 220px' : 'minmax(0, 180fr) minmax(0, 220fr)')
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
  it('相邻列逐个补偿并固定总宽，单列及极限拖动也不溢出', () => {
    expect(resizeColumn([100, 100, 100], 0, 40)).toEqual([140, 60, 100])
    expect(resizeColumn([100, 100, 100], 1, -30)).toEqual([100, 70, 130])
    expect(resizeColumn([100, 100, 100], 2, 40)).toEqual([100, 60, 140])
    expect(resizeColumn([100, 100, 100], 0, 500)).toEqual([236, 32, 32])
    expect(resizeColumn([100], 0, 40)).toEqual([100])
  })
  it('恢复账号列宽和窗口缩放时始终贴齐右侧且不反复重排', async () => {
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
        expect(columns[0]!.realWidth).toBeCloseTo(56 / 506 * space)
        expect(columns[2]!.realWidth).toBeCloseTo(columns[1]!.realWidth * 2)
        expect(columns.reduce((sum: number, column: { realWidth: number }) => sum + column.realWidth, 0)).toBeCloseTo(space)
        const settled = columns.map((column: { realWidth: number }) => column.realWidth)
        observer.callback([], {} as ResizeObserver)
        await nextTick()
        expect(columns.map((column: { realWidth: number }) => column.realWidth)).toEqual(settled)
      }
      expect(table.store.states.columns.value.map((column: { resizable: boolean }) => column.resizable)).toEqual([true, true, true])
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
  it.each([false, true])('Element Plus 调整固定列及宽字段列，由相邻列补偿（滚动模式：%s）', async scroll => {
    const wrapper = mount(defineComponent({
      components: { ElTable, ElTableColumn },
      template: `<el-table ${scroll ? 'data-pdm-column-layout="scroll"' : ''} :data="[]"><el-table-column label="选择" width="50" class-name="pdm-column-fixed-width"/><el-table-column label="名称" width="200"/><el-table-column label="操作" width="100"/></el-table>`,
    }), { attachTo: document.body, global: { plugins: [{ install: installColumnResize }] } })
    try {
      await flushPromises()
      const table = wrapper.findComponent({ name: 'ElTable' }).vm.$.exposed!
      const headers = wrapper.findAll('thead th')
      rect(headers[0]!.element, 0, 50); rect(headers[1]!.element, 50, 200); rect(headers[2]!.element, 250, 100)
      pointer(headers[0]!.element, 'pointerdown', 48); pointer(document, 'pointermove', 78); pointer(document, 'pointerup', 78)
      await nextTick()
      expect(table.store.states.columns.value.map((column: { width: number }) => column.width)).toEqual([80, 170, 100])
      rect(headers[0]!.element, 0, 80); rect(headers[1]!.element, 80, 170); rect(headers[2]!.element, 250, 100)
      pointer(headers[1]!.element, 'pointerdown', 248); pointer(document, 'pointermove', 138); pointer(document, 'pointerup', 138)
      await nextTick()
      expect(table.store.states.columns.value.map((column: { width: number }) => column.width)).toEqual([80, 60, 210])
    } finally { wrapper.unmount() }
  })
  it('拖动末列时由前列补偿并抑制排序点击', () => {
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
  it('旧版超宽原生表格先适配容器，再按实际拖动位移分配', () => {
    setup('<div><table><thead><tr><th>料号</th><th>名称</th></tr></thead></table></div>', 180)
    Object.defineProperty(document.querySelector('table')!, 'clientWidth', { configurable: true, value: 300 })
    const headers = document.querySelectorAll('th')
    rect(headers[0]!, 0, 100); rect(headers[1]!, 100, 200)
    pointer(headers[0]!, 'pointerdown', 98); pointer(document, 'pointermove', 138)
    expect(parseFloat(document.querySelector('col')!.style.width)).toBeCloseTo(100 / 180 * 100)
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
  it('保持计划信息区总宽及行列同步', () => {
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
    expect(list.style.getPropertyValue('--pdm-resized-list-columns')).toBe('minmax(0, 180fr) minmax(0, 220fr)')
    expect(list.querySelector('button')!.getAttribute('role')).toBe('option')
  })
  it('removes global listeners on teardown', () => {
    setup('<div><table><thead><tr><th>名称</th></tr></thead></table></div>',100)
    const header = document.querySelector('th')!;rect(header,0,100)
    dispose();pointer(header,'pointerdown',98);pointer(document,'pointermove',140)
    expect(document.querySelector('colgroup')).toBeNull()
  })
})
