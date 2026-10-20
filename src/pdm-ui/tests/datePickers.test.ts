import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, type App } from 'vue'
import { installDatePickers } from '../src/datePickers'

let app: App | null
let host: HTMLDivElement
let root: HTMLElement
const click = (input: HTMLInputElement, options: MouseEventInit = {}) => {
  const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, detail: 1, ...options })
  input.dispatchEvent(event)
  return event
}
function field(type = 'date') {
  const input = document.createElement('input')
  input.type = type
  input.showPicker = vi.fn()
  root.append(input)
  return input
}

describe('全局日期和时间框整框点击', () => {
  beforeEach(() => {
    host = document.createElement('div')
    document.body.append(host)
    app = createApp({ render: () => h('section') })
    installDatePickers(app)
    app.mount(host)
    root = host.firstElementChild as HTMLElement
  })
  afterEach(() => { app?.unmount(); host.remove(); vi.restoreAllMocks() })

  it.each([
    ['date', '2026-10-20'], ['datetime-local', '2026-10-20T08:00'], ['month', '2026-10'], ['week', '2026-W43'], ['time', '08:30'],
  ])('%s 点击框内打开原生选择器，保留值和约束', (type, value) => {
    const input = field(type)
    input.value = value
    input.min = value
    input.max = value
    input.required = true
    const event = click(input)
    expect(input.showPicker).toHaveBeenCalledOnce()
    expect(document.activeElement).toBe(input)
    expect(event.defaultPrevented).toBe(true)
    expect(input.value).toBe(value)
    expect(input.min).toBe(value)
    expect(input.max).toBe(value)
    expect(input.required).toBe(true)
  })

  it.each(['date', 'time'])('动态加入的弹窗 %s 框及阻止冒泡的单元格也生效', type => {
    const input = field(type)
    input.addEventListener('click', event => event.stopPropagation())
    click(input)
    expect(input.showPicker).toHaveBeenCalledOnce()
  })

  it.each(['date', 'time'].flatMap(type => ['disabled', 'readonly', 'fieldset', 'aria'].map(mode => [type, mode])))('%s 的 %s 状态不打开选择器', (type, mode) => {
    const input = field(type)
    if (mode === 'disabled') input.disabled = true
    if (mode === 'readonly') input.readOnly = true
    if (mode === 'aria') input.setAttribute('aria-disabled', 'true')
    if (mode === 'fieldset') {
      const wrapper = document.createElement('fieldset')
      wrapper.disabled = true
      root.append(wrapper)
      wrapper.append(input)
    }
    const event = click(input)
    expect(input.showPicker).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)
  })

  it.each([['date', '2026-10-19'], ['time', '09:45']])('%s 键盘聚焦和输入不自动弹出，也不拦截原有按键', (type, value) => {
    const input = field(type)
    input.focus()
    const key = new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true })
    input.dispatchEvent(key)
    input.value = value
    input.dispatchEvent(new Event('input', { bubbles: true }))
    expect(input.showPicker).not.toHaveBeenCalled()
    expect(key.defaultPrevented).toBe(false)
    expect(input.value).toBe(value)
  })

  it('不重复处理键盘激活、非主键点击及已取消的点击', () => {
    const input = field()
    click(input, { detail: 0 })
    click(input, { button: 2 })
    const cancelled = new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 })
    cancelled.preventDefault()
    input.dispatchEvent(cancelled)
    expect(input.showPicker).not.toHaveBeenCalled()
  })

  it.each(['text', 'color', 'number'])('不影响 %s 输入或组件日期框的文本输入', type => {
    const input = field(type)
    const event = click(input)
    expect(input.showPicker).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)
  })

  it('浏览器不支持或拒绝 showPicker 时保留默认行为', () => {
    const input = field()
    input.showPicker = vi.fn(() => { throw new DOMException('User activation required', 'NotAllowedError') })
    expect(click(input).defaultPrevented).toBe(false)
    Object.defineProperty(input, 'showPicker', { configurable: true, value: undefined })
    expect(click(input).defaultPrevented).toBe(false)
  })

  it('卸载界面后移除全局监听', () => {
    const input = field()
    app!.unmount()
    app = null
    host.append(input)
    click(input)
    expect(input.showPicker).not.toHaveBeenCalled()
  })
})
