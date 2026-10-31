import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionSupplierManager from '../src/components/ProductionSupplierManager.vue'
import * as api from '../src/api'
import type { ProductionSupplierCatalog } from '../src/productionSuppliers'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))

const initialCatalog = (): ProductionSupplierCatalog => ({ companyId: 'company', rowVersion: 3,
  categories: [{ id: 'machining', name: '机加工', isActive: true }, { id: 'surface', name: '表面处理', isActive: true }],
  suppliers: [{ id: 'supplier', code: 'S001', name: '综合加工厂', fullName: '综合加工有限公司', categoryIds: ['machining', 'surface'],
    isActive: true, address: '', phone: '', contact: '', contactPhone: '', remarks: '', updatedAt: '' }] })
const createWrapper = (canManage = true) => mount(ProductionSupplierManager, { props: { token: 'token', canManage }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })

beforeEach(() => vi.spyOn(api, 'readProductionSuppliers').mockResolvedValue(initialCatalog()))
afterEach(() => vi.restoreAllMocks())

describe('供应商分类维护', () => {
  it('移除顶部说明行，新建与刷新在列表筛选栏中保留原操作', async () => {
    const wrapper = createWrapper(); await flushPromises()
    expect(wrapper.find('.production-suppliers > header').exists()).toBe(false)
    const actions = wrapper.get('.production-suppliers__filters .production-suppliers__actions')
    expect(actions.text()).toContain('共 1 家')
    await actions.findAll('button')[1]!.trigger('click'); await flushPromises()
    expect(api.readProductionSuppliers).toHaveBeenCalledTimes(2)
    await actions.findAll('button')[0]!.trigger('click')
    expect(wrapper.find('[aria-label="维护供应商"]').exists()).toBe(true)
    await wrapper.get('[aria-label="关闭供应商表单"]').trigger('click')
    expect(wrapper.find('[aria-label="维护供应商"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('只读权限可查看台账但没有新增、编辑、停用和分类维护入口，撤销维护权限会关闭表单', async () => {
    const wrapper=createWrapper(false);await flushPromises();expect(wrapper.text()).toContain('综合加工有限公司')
    expect(wrapper.find('.production-suppliers__category-actions').exists()).toBe(false);expect(wrapper.findAll('tbody button')).toHaveLength(0);expect(wrapper.findAll('button').some(button=>button.text()==='新建供应商')).toBe(false)
    await wrapper.setProps({canManage:true});await wrapper.findAll('tbody button').find(button=>button.text()==='编辑')!.trigger('click');expect(wrapper.find('[aria-label="维护供应商"]').exists()).toBe(true)
    await wrapper.setProps({canManage:false});expect(wrapper.find('[aria-label="维护供应商"]').exists()).toBe(false);wrapper.unmount()
  })
  it('从维护弹窗新增分类，保存后保持维护列表打开，并使用最新版本编辑已有分类', async () => {
    const savedCatalog = initialCatalog()
    const saveCategory = vi.spyOn(api, 'saveProductionSupplierCategory').mockImplementation(async (id, input) => {
      savedCatalog.categories = savedCatalog.categories.filter(item => item.id !== id).concat({ id, name: input.name, isActive: input.isActive })
      savedCatalog.rowVersion++
      return { ...savedCatalog }
    })
    const wrapper = createWrapper()
    await flushPromises()
    expect(wrapper.find('.production-suppliers__categories form').exists()).toBe(false)
    await wrapper.find('.production-suppliers__category-actions button').trigger('click')
    await flushPromises()
    expect(wrapper.get('.supplier-category-manager__placeholder').text()).toContain('分类编辑')
    await wrapper.get('.supplier-category-manager__header button').trigger('click')
    await wrapper.get('input[aria-label="分类名称"]').setValue('钣金')
    await wrapper.get('.supplier-category-manager form').trigger('submit')
    await flushPromises()
    expect(saveCategory).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ name: '钣金', isActive: true, expectedRowVersion: 3 }), 'token')
    expect(wrapper.get('.supplier-category-manager__rows').text()).toContain('钣金')
    expect(wrapper.find('.supplier-category-manager form').exists()).toBe(false)
    const category = wrapper.findAll('.production-suppliers__category').find(item => item.text().includes('机加工'))!
    await category.get('button').trigger('click')
    await wrapper.get('input[aria-label="分类名称"]').setValue('精密机加工')
    await wrapper.get('.supplier-category-manager form').trigger('submit')
    await flushPromises()
    expect(saveCategory).toHaveBeenLastCalledWith('machining', expect.objectContaining({ name: '精密机加工', expectedRowVersion: 4 }), 'token')
    expect(wrapper.get('.production-suppliers__categories').text()).toContain('精密机加工')
    wrapper.unmount()
  })

  it('取消不改分类，保存失败保留草稿并沿用同一分类编号重试', async () => {
    const saveCategory = vi.spyOn(api, 'saveProductionSupplierCategory').mockRejectedValueOnce(new Error('台账已被修改，请刷新。')).mockResolvedValue(initialCatalog())
    const wrapper = createWrapper()
    await flushPromises()
    await wrapper.get('.production-suppliers__category-actions button').trigger('click')
    await flushPromises()
    await wrapper.get('.production-suppliers__category button').trigger('click')
    await wrapper.get('input[aria-label="分类名称"]').setValue('不应保存')
    await wrapper.get('.supplier-category-manager__editor-actions button').trigger('click')
    expect(saveCategory).not.toHaveBeenCalled()
    expect(wrapper.get('.production-suppliers__categories').text()).toContain('机加工')
    expect(wrapper.get('.production-suppliers__categories').text()).not.toContain('不应保存')
    await wrapper.get('.supplier-category-manager__header button').trigger('click')
    await wrapper.get('input[aria-label="分类名称"]').setValue('钣金')
    await wrapper.get('.supplier-category-manager form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('input[aria-label="分类名称"]').element).toHaveProperty('value', '钣金')
    const firstId = saveCategory.mock.calls[0][0]
    await wrapper.get('.supplier-category-manager form').trigger('submit')
    await flushPromises()
    expect(saveCategory.mock.calls[1][0]).toBe(firstId)
    wrapper.unmount()
  })

  it('同一供应商保存多个分类，重新编辑保留勾选，并出现在每个所属分类中', async () => {
    const catalog = initialCatalog()
    catalog.suppliers[0].categoryIds = ['machining']
    vi.mocked(api.readProductionSuppliers).mockResolvedValue(catalog)
    const saveSupplier = vi.spyOn(api, 'saveProductionSupplier').mockImplementation(async (id, input) => {
      catalog.suppliers = [{ ...input, id, updatedAt: '' }]
      catalog.rowVersion++
      return { ...catalog }
    })
    const wrapper = createWrapper()
    await flushPromises()
    await wrapper.get('tbody button').trigger('click')
    const classification = wrapper.get('.production-suppliers__classification')
    await classification.get('input[value="surface"]').setValue(true)
    await wrapper.get('.production-suppliers__editor form').trigger('submit')
    await flushPromises()
    expect(saveSupplier).toHaveBeenCalledWith('supplier', expect.objectContaining({ categoryIds: ['machining', 'surface'] }), 'token')
    for (const name of ['机加工', '表面处理']) {
      const category = wrapper.findAll('.production-suppliers__category-filter').find(item => item.text() === name)!
      await category.trigger('click')
      expect(wrapper.findAll('tbody tr')).toHaveLength(1)
      expect(wrapper.get('tbody').text()).toContain('机加工、表面处理')
    }
    await wrapper.get('tbody button').trigger('click')
    expect(wrapper.get('.production-suppliers__classification input[value="machining"]').element).toHaveProperty('checked', true)
    expect(wrapper.get('.production-suppliers__classification input[value="surface"]').element).toHaveProperty('checked', true)
    wrapper.unmount()
  })
})
