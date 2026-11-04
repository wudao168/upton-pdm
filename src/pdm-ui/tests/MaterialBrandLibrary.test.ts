import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import MaterialBrandLibrary from '../src/components/MaterialBrandLibrary.vue'
import type { MaterialBrand } from '../src/api'

const api = vi.hoisted(() => ({ listMaterialBrandCatalog: vi.fn(), saveMaterialBrand: vi.fn() }))
vi.mock('../src/api', () => api)
let rows: MaterialBrand[]
let wrapper: ReturnType<typeof mount>
beforeEach(() => {
  vi.clearAllMocks()
  rows = Array.from({ length: 125 }, (_, index) => ({ id: `brand-${index + 1}`, name: `Brand-${String(index + 1).padStart(3, '0')}`, isActive: true, rowVersion: 1, previousNames: [`旧品牌${index + 1}`] }))
  api.listMaterialBrandCatalog.mockImplementation(async () => structuredClone(rows))
  api.saveMaterialBrand.mockImplementation(async (id, input) => {
    const current = rows.find(row => row.id === id)!
    Object.assign(current, input, { rowVersion: current.rowVersion + 1, previousNames: [...current.previousNames, current.name] })
    return structuredClone(current)
  })
})
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })
async function render(canManage = true) {
  wrapper = mount(MaterialBrandLibrary, { props: { token: 'token', canManage }, global: { plugins: [ElementPlus], stubs: {
    Teleport: true,
    ElPagination: { name: 'ElPagination', props: ['currentPage', 'pageSize', 'pageSizes', 'total', 'layout'], emits: ['update:currentPage', 'update:pageSize'], template: '<div />' },
  } } })
  await flushPromises()
  return { table: wrapper.findComponent({ name: 'ElTable' }), pager: wrapper.findComponent({ name: 'ElPagination' }) }
}

it('默认50条，沿用全局50/100/200选项，换页和切换每页条数正确', async () => {
  const { table, pager } = await render()
  expect(pager.props()).toMatchObject({ currentPage: 1, pageSize: 50, pageSizes: [50, 100, 200], total: 125, layout: 'total, sizes, prev, pager, next' })
  expect(table.props('data')).toHaveLength(50)
  pager.vm.$emit('update:currentPage', 3); await flushPromises()
  expect(table.props('data')).toHaveLength(25)
  expect(table.props('data')[0].id).toBe('brand-101')
  pager.vm.$emit('update:pageSize', 100); await flushPromises()
  expect(pager.props('currentPage')).toBe(1)
  expect(table.props('data')).toHaveLength(100)
  pager.vm.$emit('update:pageSize', 200); await flushPromises()
  expect(table.props('data')).toHaveLength(125)
  expect(api.listMaterialBrandCatalog).toHaveBeenCalledTimes(1)
})

it('搜索品牌和曾用名称，忽略大小写与首尾空格，搜索及清空回到第一页', async () => {
  const { table, pager } = await render()
  pager.vm.$emit('update:currentPage', 3); await flushPromises()
  const search = wrapper.get('[aria-label="搜索品牌"]')
  await search.setValue('  旧品牌117  '); await flushPromises()
  expect(pager.props()).toMatchObject({ currentPage: 1, total: 1 })
  expect(table.props('data')[0].id).toBe('brand-117')
  await search.setValue('bRAND-012'); await flushPromises()
  expect(table.props('data').map((row: MaterialBrand) => row.id)).toEqual(['brand-12'])
  await search.setValue('不存在的品牌'); await flushPromises()
  expect(table.props('data')).toEqual([])
  expect(pager.props()).toMatchObject({ currentPage: 1, total: 0 })
  await search.setValue(''); await flushPromises()
  expect(pager.props()).toMatchObject({ currentPage: 1, total: 125 })
  expect(table.props('data')).toHaveLength(50)
  expect(api.listMaterialBrandCatalog).toHaveBeenCalledTimes(1)
})

it('翻页后仍保存原选中的品牌，曾用名称搜索能找到改名结果', async () => {
  const { table, pager } = await render()
  pager.vm.$emit('update:currentPage', 3); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
  pager.vm.$emit('update:currentPage', 1); await flushPromises()
  await wrapper.get('[aria-label="品牌名称"]').setValue('新规范品牌')
  await wrapper.findAll('button').find(button => button.text() === '保存品牌')!.trigger('click'); await flushPromises()
  expect(api.saveMaterialBrand).toHaveBeenCalledWith('brand-101', { name: '新规范品牌', isActive: true, rowVersion: 1 }, 'token')
  await wrapper.get('[aria-label="搜索品牌"]').setValue('Brand-101'); await flushPromises()
  expect(table.props('data')[0].name).toBe('新规范品牌')
})

it('只读人员也可搜索和分页，不能维护品牌', async () => {
  const { table, pager } = await render(false)
  expect(wrapper.find('[aria-label="品牌名称"]').exists()).toBe(false)
  expect(wrapper.findAll('button').some(button => button.text() === '编辑')).toBe(false)
  pager.vm.$emit('update:currentPage', 2); await flushPromises()
  expect(table.props('data')[0].id).toBe('brand-51')
  await wrapper.get('[aria-label="搜索品牌"]').setValue('旧品牌125'); await flushPromises()
  expect(table.props('data')[0].id).toBe('brand-125')
  expect(api.saveMaterialBrand).not.toHaveBeenCalled()
})
