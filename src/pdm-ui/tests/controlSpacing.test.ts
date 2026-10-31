import { afterEach, expect, it } from 'vitest'
import { installControlSpacing } from '../src/controlSpacing'

let dispose: (() => void) | undefined
const settle = () => new Promise<void>(resolve => requestAnimationFrame(() => resolve()))
afterEach(() => { dispose?.(); document.body.innerHTML = '' })

it('按钮、输入框和搜索组件共用间距，组件内部保持原结构', async () => {
  document.body.innerHTML = '<div class="toolbar" style="display:flex"><button>刷新</button><div class="el-input"><input></div><input type="search"></div>'
  dispose = installControlSpacing()
  await settle()
  const toolbar = document.querySelector('.toolbar')!
  expect(toolbar.getAttribute('data-pdm-control-layout')).toBe('flow')
  expect(toolbar.querySelectorAll(':scope > [data-pdm-control-item]')).toHaveLength(3)
  expect(toolbar.querySelector('.el-input')!.getAttribute('data-pdm-control-layout')).toBeNull()
  expect(toolbar.querySelector('button')!.textContent).toBe('刷新')
})

it('识别纵向表单和表格行内操作，不改写它们的显示布局', async () => {
  document.body.innerHTML = '<form><label style="display:block">账号<input></label><button>登录</button></form><table><tbody><tr><td><button>编辑</button><button>删除</button></td></tr></tbody></table>'
  dispose = installControlSpacing()
  await settle()
  expect(document.querySelector('form')!.getAttribute('data-pdm-control-layout')).toBe('stack')
  expect(document.querySelector('td')!.getAttribute('data-pdm-control-layout')).toBe('inline')
  expect(getComputedStyle(document.querySelector('td')!).display).toBe('table-cell')
})

it('异步控件会加入规则，进度条、分类、侧栏及日期网格不受影响', async () => {
  document.body.innerHTML = '<div class="toolbar" style="display:grid"><button>刷新</button></div><div class="pdm-overview-timeline"><button>设计</button><button>装配</button></div><div class="wh-department-options"><button>生产部</button><button>质量部</button></div><div class="pc-grid" style="display:grid"><button>1</button><button>2</button></div><div class="wh-day-grid" style="display:grid"><button>1</button><button>2</button></div>'
  dispose = installControlSpacing()
  await settle()
  const toolbar = document.querySelector('.toolbar')!
  expect(toolbar.hasAttribute('data-pdm-control-layout')).toBe(false)
  toolbar.append(document.createElement('input'))
  await new Promise(resolve => setTimeout(resolve, 0))
  await settle()
  expect(toolbar.getAttribute('data-pdm-control-layout')).toBe('flow')
  expect(document.querySelector('.pdm-overview-timeline')!.hasAttribute('data-pdm-control-layout')).toBe(false)
  expect(document.querySelector('.wh-department-options')!.hasAttribute('data-pdm-control-layout')).toBe(false)
  for (const calendar of document.querySelectorAll('.pc-grid, .wh-day-grid')) {
    expect(calendar.hasAttribute('data-pdm-control-layout')).toBe(false)
    expect(calendar.querySelector('[data-pdm-control-item]')).toBeNull()
    expect(calendar.textContent).toBe('12')
  }
})
