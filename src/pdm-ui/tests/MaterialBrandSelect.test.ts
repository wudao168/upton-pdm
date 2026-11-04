import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { expect, it, vi } from 'vitest'
import MaterialBrandSelect from '../src/components/MaterialBrandSelect.vue'
const api = vi.hoisted(() => ({ listMaterialBrandCatalog: vi.fn() }))
vi.mock('../src/api', () => api)
it('只允许选择库内品牌，停用品牌保留显示并禁止新选', async () => {
  api.listMaterialBrandCatalog.mockResolvedValue([{ id: 'one', name: 'UPTON', isActive: true, previousNames: [], rowVersion: 1 }, { id: 'old', name: '旧品牌', isActive: false, previousNames: [], rowVersion: 1 }])
  const wrapper = mount(MaterialBrandSelect, { props: { token: 'token', modelValue: '旧品牌' }, global: { plugins: [ElementPlus] } })
  await flushPromises()
  const select = wrapper.findComponent({ name: 'ElSelect' })
  expect(select.props('allowCreate')).toBe(false)
  expect(wrapper.findAllComponents({ name: 'ElOption' }).find(option => option.props('value') === '旧品牌')?.props('disabled')).toBe(true)
  select.vm.$emit('update:modelValue', 'UPTON')
  expect(wrapper.emitted('update:modelValue')).toEqual([['UPTON']])
  wrapper.unmount()
})
