import { mount } from '@vue/test-utils'
import { expect, it } from 'vitest'
import MoreActions from '../src/components/MoreActions.vue'

it('选择可用操作后收起，禁用项保持展开，Esc 恢复焦点', async () => {
  const wrapper = mount(MoreActions, { attachTo: document.body, slots: { default: '<button disabled>删除</button><button>导出</button>' } })
  const root = wrapper.get('details').element as HTMLDetailsElement
  root.open = true
  await wrapper.get('button[disabled]').trigger('click')
  expect(root.open).toBe(true)
  await wrapper.get('button:not([disabled])').trigger('click')
  expect(root.open).toBe(false)
  root.open = true
  await wrapper.get('summary').trigger('keydown', { key: 'Escape' })
  expect(root.open).toBe(false)
  expect(document.activeElement).toBe(wrapper.get('summary').element)
  wrapper.unmount()
})
