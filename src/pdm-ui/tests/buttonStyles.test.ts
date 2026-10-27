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
  install('<button>新增物料</button><button>确认保存</button><button>删除</button><button>刷新</button><button class="is-link">查看资料</button><nav><button>新增记录</button></nav><button role="treeitem">添加组件</button><button class="production-workbench__project">项目一</button>')
  const buttons = [...document.querySelectorAll('button')]
  expect(buttons.map(button => button.dataset.pdmButtonKind)).toEqual(['add', 'confirm', 'delete', 'outline', undefined, undefined, undefined, undefined])
})

it('进度条与部门分类恢复原样式，不再套用普通按钮边框和颜色', () => {
  install('<div class="pdm-overview-timeline"><button class="pdm-overview-timeline__segment">设计</button><button class="pdm-overview-timeline__milestone" aria-label="计划里程碑"></button></div><div class="wh-department-options"><button>生产部<span>参与填报</span></button></div><button class="production-workbench__all-categories">全部分类</button>')
  for (const button of document.querySelectorAll('button')) expect(button.dataset.pdmButtonKind).toBeUndefined()
  expect(document.body.textContent).toContain('参与填报')
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
