import { afterEach, expect, it } from 'vitest'
import { installControlSpacing } from '../src/controlSpacing'

let dispose: (() => void) | undefined
const settle = () => new Promise<void>(resolve => requestAnimationFrame(() => resolve()))
it('手机预览保持移动端原有模块间距', async () => {
  document.body.innerHTML = '<div class="layout-phone"><div class="mobile-layout-grid" style="display:grid"><button>待办</button><button>工时</button></div></div>'
  dispose = installControlSpacing(); await settle()
  expect(document.querySelector('.mobile-layout-grid')!.hasAttribute('data-pdm-control-layout')).toBe(false)
})
it('钉钉设置纵向面板及页面编辑器不被全局规则改为多列换行', async () => {
  document.body.innerHTML = '<section class="ding-settings" style="display:flex;flex-direction:column;height:400px"><button>页面维护</button><section class="office-layout-editor"><label>标题<input></label><button>保存</button></section><button>员工绑定</button></section>'
  dispose = installControlSpacing(); await settle()
  expect(document.querySelector('.ding-settings')!.hasAttribute('data-pdm-control-layout')).toBe(false)
  expect(document.querySelector('.office-layout-editor')!.hasAttribute('data-pdm-control-layout')).toBe(false)
})
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

it('保留关联配置表格分类控件的单行布局，不套用工具栏换行', async () => {
  document.body.innerHTML = '<table><tbody><tr><td><section class="material-relation-categories is-compact" style="display:flex;flex-wrap:nowrap"><span>未分类</span><div class="el-select"><input></div><button>保存分类</button></section></td></tr></tbody></table>'
  dispose = installControlSpacing()
  await settle()
  const row = document.querySelector('.material-relation-categories')!
  expect(row.hasAttribute('data-pdm-control-layout')).toBe(false)
  expect(getComputedStyle(row).flexWrap).toBe('nowrap')
})

it('BOM行操作保持紧凑单行，不套用工具栏换行和间距', async () => {
  document.body.innerHTML = '<div class="pdm-bom-row-actions" style="display:flex;flex-wrap:nowrap;gap:2px"><span>⠿</span><button>+</button><button>×</button></div>'
  dispose = installControlSpacing()
  await settle()
  const row = document.querySelector('.pdm-bom-row-actions')!
  expect(row.hasAttribute('data-pdm-control-layout')).toBe(false)
  expect(row.querySelector('[data-pdm-control-item]')).toBeNull()
  expect(getComputedStyle(row).flexWrap).toBe('nowrap')
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
