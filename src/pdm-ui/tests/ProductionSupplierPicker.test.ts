import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProductionSupplierPicker from '../src/components/ProductionSupplierPicker.vue'
import * as api from '../src/api'
import type { ProductionSupplierCatalog } from '../src/productionSuppliers'

afterEach(() => vi.restoreAllMocks())

describe('生产供应商选择', () => {
  it('排除停用供应商，按分类筛选，并在原选择不再符合时清空编号和名称', async () => {
    const supplier = { fullName: '供应商有限公司', address: '', phone: '', contact: '', contactPhone: '', remarks: '', updatedAt: '', isActive: true }
    const catalog: ProductionSupplierCatalog = { companyId: 'company', rowVersion: 1,
      categories: [{ id: 'machining', name: '机加工', isActive: true }, { id: 'surface', name: '表面处理', isActive: true }],
      suppliers: [{ ...supplier, id: 'one', code: 'S001', name: '机加厂', categoryIds: ['machining'] },
        { ...supplier, id: 'two', code: 'S002', name: '表处厂', categoryIds: ['surface'] },
        { ...supplier, id: 'stopped', code: 'S003', name: '停用厂', categoryIds: ['machining'], isActive: false }] }
    vi.spyOn(api, 'readProductionSupplierChoices').mockResolvedValue(catalog)
    const wrapper = mount(ProductionSupplierPicker, { props: { token: 'token', modelValue: 'one' } })
    await flushPromises()
    expect(wrapper.findAll('.production-supplier-picker__supplier option').map(item => item.text())).toEqual(['请选择供应商', 'S001 · 机加厂', 'S002 · 表处厂'])
    await wrapper.findAll('select')[0].setValue('surface')
    expect(wrapper.findAll('.production-supplier-picker__supplier option').map(item => item.text())).toEqual(['请选择供应商', 'S002 · 表处厂'])
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual([''])
    expect(wrapper.emitted('selected')?.at(-1)).toEqual([undefined])
    wrapper.unmount()
  })
})
