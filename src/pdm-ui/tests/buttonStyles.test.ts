import { afterEach, expect, it } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import { installButtonStyles } from '../src/buttonStyles'

let app: App | undefined
const settle = () => new Promise(resolve => setTimeout(resolve, 0))
function install(html: string) {
  document.body.innerHTML = html
  app = createApp({ render: () => null })
  installButtonStyles(app)
  app.mount(document.createElement('div'))
}
afterEach(() => { app?.unmount(); document.body.innerHTML = '' })

it('recognizes operation types without styling navigation, content rows or unrelated links', () => {
  install('<button>新增物料</button><button>确认保存</button><button>删除</button><button>刷新</button><button class="is-link">查看资料</button><nav><button>新增记录</button></nav><button role="treeitem">添加组件</button><button class="production-workbench__project">项目一</button><aside class="pdm-bom-release-history"><button>RP-P700016-1-20261027-0001<small>ECN-P700016-1-20261027-93679717</small><em>已发布</em></button></aside>')
  const buttons = [...document.querySelectorAll('button')]
  expect(buttons.map(button => button.dataset.pdmButtonKind)).toEqual(['add', 'confirm', 'delete', 'outline', undefined, undefined, undefined, undefined, undefined])
})

it('信息展示与BOM内容链接不套用操作按钮外框', () => {
  install('<button class="wr-next"><span>下一节点</span></button><button class="pdm-plan-summary-action"><strong>阶段进度</strong></button><button class="bom-overview__link">标准件BOM</button><button class="pdm-bom-issue-button">未填写料号</button><button class="pdm-bom-files-link">查看（3）</button>')
  for (const button of document.querySelectorAll('button')) expect(button.dataset.pdmButtonKind).toBeUndefined()
  expect(document.querySelector('.wr-next span')!.textContent).toBe('下一节点')
})

it('进度条与部门分类恢复原样式，不再套用普通按钮边框和颜色', () => {
  install('<div class="pdm-overview-timeline"><button class="pdm-overview-timeline__segment">设计</button><button class="pdm-overview-timeline__milestone" aria-label="计划里程碑"></button></div><div class="wh-department-options"><button>生产部<span>参与填报</span></button></div><button class="production-workbench__all-categories">全部分类</button>')
  for (const button of document.querySelectorAll('button')) expect(button.dataset.pdmButtonKind).toBeUndefined()
  expect(document.body.textContent).toContain('参与填报')
})

it('扫码流程图节点和标签关闭按钮不显示操作按钮的方框', () => {
  install('<button class="production-scan-settings__node">包装接收</button><button class="el-tag__close" aria-label="关闭此标签">×</button>')
  for (const button of document.querySelectorAll('button')) expect(button.dataset.pdmButtonKind).toBeUndefined()
})

it('updates asynchronous dialog labels and icon-only accessibility labels', async () => {
  install('')
  const dialog = document.createElement('div')
  dialog.innerHTML = '<button aria-label="删除图片"><svg></svg></button><button>新增字段</button>'
  document.body.append(dialog)
  await settle()
  const [remove, action] = [...dialog.querySelectorAll('button')]
  expect(remove!.dataset.pdmButtonKind).toBe('delete')
  expect(remove!.dataset.pdmButtonIconOnly).toBe('true')
  action!.firstChild!.textContent = '确认修改'
  await settle()
  expect(action!.dataset.pdmButtonKind).toBe('confirm')
  expect(action!.dataset.pdmButtonLabel).toBe('确认修改')
  action!.textContent = '取消'
  await settle()
  expect(action!.dataset.pdmButtonKind).toBe('outline')
  expect(action!.dataset.pdmButtonLabel).toBeUndefined()
})

it('keeps deletion confirmations red and standard-library inclusion green', () => {
  install('<button>确认移除</button><button>确认清空</button><button>加入标准库</button><button>确认保存</button><button>批量加入指定分类</button>')
  expect([...document.querySelectorAll('button')].map(button => button.dataset.pdmButtonKind)).toEqual(['delete', 'delete', 'add', 'confirm', 'add'])
})

it('preserves Vue-owned nodes, click handlers and disabled pending states', async () => {
  const pending = ref(false)
  const clicks = ref(0)
  const host = document.createElement('div')
  document.body.append(host)
  app = createApp({ render: () => h('button', { disabled: pending.value, onClick: () => { clicks.value++; pending.value = true } }, [h('span', pending.value ? '正在保存…' : '保存BOM')]) })
  installButtonStyles(app)
  app.mount(host)
  await settle()
  const button = host.querySelector('button')!
  const span = button.firstElementChild
  expect(button.dataset.pdmButtonKind).toBe('confirm')
  expect(button.dataset.pdmButtonLabel).toBe('保存')
  expect(button.title).toBe('保存BOM')
  button.click()
  await nextTick(); await settle()
  expect(clicks.value).toBe(1)
  expect(button.disabled).toBe(true)
  expect(button.dataset.pdmButtonKind).toBe('confirm')
  expect(button.dataset.pdmButtonLabel).toBe('正在保存…')
  expect(button.firstElementChild).toBe(span)
  pending.value = false
  await nextTick(); await settle()
  expect(button.textContent).toBe('保存BOM')
  expect(button.dataset.pdmButtonLabel).toBe('保存')
  expect(button.title).toBe('保存BOM')
})

it('removes operation decoration when an existing control becomes a tab', async () => {
  install('<div><button>添加</button></div>')
  const button = document.querySelector('button')!
  expect(button.dataset.pdmButtonKind).toBe('add')
  button.parentElement!.className = 'pdm-tabs'
  await settle()
  expect(button.dataset.pdmButtonKind).toBeUndefined()
  expect(button.dataset.pdmButtonLabel).toBeUndefined()
})

it('表头排序不套用操作按钮外观，保留原尖角节点和点击事件', async () => {
  install('<div class="el-table"><button class="caret-wrapper" aria-label="按 物料编码 排序"><i class="sort-caret ascending"></i><i class="sort-caret descending"></i></button></div>')
  const button = document.querySelector<HTMLButtonElement>('button')!
  const caret = button.firstElementChild
  let clicks = 0
  button.addEventListener('click', () => { clicks++ })
  expect(button.dataset.pdmButtonKind).toBeUndefined()
  button.firstElementChild!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  expect(clicks).toBe(1)
  expect(button.firstElementChild).toBe(caret)
  button.classList.add('is-active'); await settle()
  expect(button.dataset.pdmButtonKind).toBeUndefined()
})

it('统一标记文字、图标和链接刷新按钮，保留文字与可访问名称', () => {
  install('<button>刷新</button><button>刷新BOM</button><button>立即全量刷新</button><button aria-label="刷新设计树"><svg></svg></button><button title="刷新审核状态"><svg></svg></button><button class="el-button--text" aria-label="刷新标准结构"><i class="el-icon"><svg></svg></i></button><button>恢复</button>')
  const buttons = [...document.querySelectorAll('button')]
  expect(buttons.map(button => button.dataset.pdmButtonRefresh)).toEqual(['true', 'true', 'true', 'true', 'true', 'true', undefined])
  expect(buttons[1]!.textContent).toBe('刷新BOM')
  expect(buttons[3]!.getAttribute('aria-label')).toBe('刷新设计树')
  expect(buttons[5]!.dataset.pdmButtonKind).toBeUndefined()
})

it('刷新时保留 Vue 图标节点、点击与禁用状态，切换为其他操作时移除刷新图标', async () => {
  const pending = ref(false)
  const clicks = ref(0)
  const host = document.createElement('div')
  document.body.append(host)
  app = createApp({ render: () => h('button', { disabled: pending.value, onClick: () => { clicks.value++; pending.value = true } }, [h('svg'), h('span', pending.value ? '刷新中…' : '刷新')]) })
  installButtonStyles(app)
  app.mount(host)
  await settle()
  const button = host.querySelector('button')!
  const icon = button.firstElementChild
  button.click()
  await nextTick(); await settle()
  expect(clicks.value).toBe(1)
  expect(button.disabled).toBe(true)
  expect(button.dataset.pdmButtonRefresh).toBe('true')
  expect(button.firstElementChild).toBe(icon)
  button.click()
  expect(clicks.value).toBe(1)
  button.textContent = '搜索'
  await settle()
  expect(button.dataset.pdmButtonRefresh).toBeUndefined()
})

it('导航和内容行不添加刷新图标，控件移入导航后清除标记', async () => {
  install('<nav><button>刷新</button></nav><button role="treeitem">刷新组件</button><div><button>刷新</button></div>')
  const buttons = [...document.querySelectorAll('button')]
  expect(buttons.map(button => button.dataset.pdmButtonRefresh)).toEqual([undefined, undefined, 'true'])
  buttons[2]!.parentElement!.className = 'pdm-tabs'
  await settle()
  expect(buttons[2]!.dataset.pdmButtonRefresh).toBeUndefined()
})
