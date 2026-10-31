import { afterEach, expect, it, vi } from 'vitest'
import { createApp } from 'vue'
import { installButtonTypography } from '../src/buttonTypography'
let dispose: () => void
function button(width: number, height: number, role?: string) {
  const element = document.createElement('button')
  element.textContent = '刷新'
  Object.defineProperties(element, { offsetWidth: { configurable: true, value: width }, offsetHeight: { configurable: true, value: height } })
  if (role) element.setAttribute('role', role)
  document.body.append(element)
  return element
}
function install() {
  const app = createApp({})
  vi.spyOn(app, 'onUnmount').mockImplementation(callback => { dispose = callback; return app })
  installButtonTypography(app)
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0))
afterEach(() => { dispose?.(); document.body.innerHTML = ''; vi.restoreAllMocks() })
it('按用途标记普通按钮，旧宽高不再决定字体', () => {
  const target = button(80, 30)
  const others = [button(80, 32), button(60, 30), button(90, 30), button(80, 30, 'tab')]
  install()
  expect(target.dataset.pdmButtonSize).toBe('default')
  expect(others.slice(0, 3).every(item => item.dataset.pdmButtonSize === 'default')).toBe(true)
  expect(others[3]!.dataset.pdmButtonSize).toBeUndefined()
})
it('covers lazy dialogs and restores typography after Vue replaces classes', async () => {
  install()
  const target = button(80, 30)
  await settle()
  expect(target.dataset.pdmButtonSize).toBe('default')
  target.className = 'el-button is-disabled'
  await settle()
  expect(target.dataset.pdmButtonSize).toBe('default')
  expect(target.classList.contains('is-disabled')).toBe(true)
  Object.defineProperty(target, 'offsetWidth', { value: 90 })
  target.style.width = '90px'
  await settle()
  expect(target.dataset.pdmButtonSize).toBe('default')
})

it('区分普通、行内、工具图标、行内图标和整行主按钮', () => {
  document.body.innerHTML = '<button>刷新</button><table><tbody><tr><td><button>编辑</button><button aria-label="上移"><svg></svg></button></td></tr></tbody></table><button aria-label="关闭"><svg></svg></button><button class="pdm-login-submit">登录</button>'
  install()
  expect([...document.querySelectorAll('button')].map(item => item.dataset.pdmButtonSize)).toEqual(['default', 'inline', 'inline-icon', 'icon', 'block'])
})

it('导航、进度条、分类、空白日期和单元格保持内容布局', async () => {
  document.body.innerHTML = '<nav><button>图档</button></nav><div class="pdm-overview-timeline"><button>设计</button><button aria-label="计划里程碑"></button></div><div class="wh-department-options"><button>生产部</button></div><button class="wh-day"></button><button class="pdm-bom-cell-edit"></button><button class="material-image-add">＋</button><aside class="pdm-bom-release-history"><button>发布记录</button></aside><div><button>刷新</button></div>'
  install()
  const buttons = [...document.querySelectorAll('button')]
  expect(buttons.slice(0, -1).every(item => item.dataset.pdmButtonSize === undefined)).toBe(true)
  expect(buttons.at(-1)!.dataset.pdmButtonSize).toBe('default')
  buttons.at(-1)!.parentElement!.className = 'wh-department-options'
  await settle()
  expect(buttons.at(-1)!.dataset.pdmButtonSize).toBeUndefined()
})

it('流程节点和标签关闭按钮保留原生尺寸，延迟挂载也不套用操作按钮尺寸', async () => {
  install()
  const host = document.createElement('div')
  host.innerHTML = '<button class="production-scan-settings__node"><strong>包装接收</strong><span>机加主管、机加人员</span></button><button class="el-tag__close" aria-label="关闭此标签">×</button>'
  document.body.append(host); await settle()
  for (const button of host.querySelectorAll('button')) expect(button.dataset.pdmButtonSize).toBeUndefined()
})

it('缩短的按钮文字使用统一宽度，保留原节点与点击事件', async () => {
  const target = button(104, 30)
  target.innerHTML = '<span>保存可用工时</span>'
  const span = target.firstElementChild
  const click = vi.fn()
  target.addEventListener('click', click)
  install()
  target.dataset.pdmButtonLabel = '保存'
  await settle()
  expect(target.dataset.pdmButtonSize).toBe('default')
  expect(target.dataset.pdmButtonFit).toBe('compact')
  expect(target.firstElementChild).toBe(span)
  target.click()
  expect(click).toHaveBeenCalledTimes(1)
})

it('表头排序保持专用尺寸，后挂载的排序按钮也不标记为操作图标', async () => {
  document.body.innerHTML = '<div class="el-table"><button class="caret-wrapper" aria-label="按 名称 排序"></button></div>'
  install()
  expect(document.querySelector('button')!.dataset.pdmButtonSize).toBeUndefined()
  document.querySelector('.el-table')!.insertAdjacentHTML('beforeend', '<button class="caret-wrapper" aria-label="按 创建时间 排序"></button>')
  await settle()
  expect([...document.querySelectorAll('button')].every(item => !item.dataset.pdmButtonSize)).toBe(true)
})
