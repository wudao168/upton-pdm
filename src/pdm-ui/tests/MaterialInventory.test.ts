import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MaterialInventory from '../src/components/MaterialInventory.vue'

const api = vi.hoisted(() => ({
  listMaterialInventory: vi.fn(),
  refreshMaterialInventory: vi.fn(),
}))

vi.mock('../src/api', () => api)

const inventoryRow = {
  organizationCode: '7',
  warehouseCode: '01',
  warehouseName: '1号常备仓',
  materialCode: '01020141559',
  itemName: 'LED背光板',
  brand: '国优',
  specification: '60W5件套',
  projectCode: 'P700005',
  projectName: '测试项目',
  subproject: 'P700005-6',
  stockQuantity: 16,
  availableQuantity: 16,
  reservedQuantity: 0,
  unavailableQuantity: 0,
  refreshedAt: '2026-09-05T01:02:03Z',
}

describe('MaterialInventory', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.listMaterialInventory.mockResolvedValue({
      items: [inventoryRow], total: 1, page: 1, pageSize: 50,
      warehouseNames: ['1号常备仓', '2号常备仓'],
      brandNames: ['FESTO', '国优'],
      projectCodes: ['P700005', 'P700006'],
      subprojectOptions: [
        { projectCode: 'P700005', subproject: 'P700005-6' },
        { projectCode: 'P700006', subproject: 'P700006-1' },
      ],
      lastSuccessfulRefreshAt: '2026-09-05T01:02:03Z',
    })
    api.refreshMaterialInventory.mockResolvedValue({
      items: [inventoryRow], total: 1, page: 1, pageSize: 50,
      warehouseNames: ['1号常备仓', '2号常备仓'],
      brandNames: ['FESTO', '国优'],
      projectCodes: ['P700005', 'P700006'],
      subprojectOptions: [{ projectCode: 'P700005', subproject: 'P700005-6' }],
      lastSuccessfulRefreshAt: '2026-09-05T01:02:03Z',
    })
  })

  it('按确认顺序显示库存快照字段和最近刷新时间', async () => {
    const wrapper = mount(MaterialInventory, {
      props: { token: 'token' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const headers = wrapper.findAll('.el-table__header-wrapper th .cell').map(item => item.text().trim())
    expect(headers).toEqual([
      '存储地点名称', '料号', '品名', '品牌', '规格', '项目号', '项目名称', '子项目',
      '现存量（库存单位）', '最近刷新时间',
    ])
    expect(wrapper.text()).toContain('1号常备仓')
    expect(wrapper.text()).toContain('01020141559')
    expect(wrapper.text()).toContain('LED背光板')
    expect(wrapper.text()).toContain('国优')
    expect(wrapper.text()).toContain('16.00')
    expect(wrapper.text()).toContain('最近全量刷新')
    const columns = wrapper.findAllComponents({ name: 'ElTableColumn' })
    expect(columns).toHaveLength(10)
    expect(columns.every(column => column.props('align') === 'center')).toBe(true)
    for (const selector of ['.el-table__header-wrapper th', '.el-table__body-wrapper td', '.el-table__footer-wrapper td']) {
      const cells = wrapper.findAll(selector)
      expect(cells).toHaveLength(10)
      expect(cells.every(cell => cell.classes().includes('is-center'))).toBe(true)
    }
    const searchPanel = wrapper.get('[aria-label="库存搜索"]')
    expect(searchPanel.text()).toContain('库存搜索')
    const indexedSelects = searchPanel.findAllComponents({ name: 'ElSelect' })
    expect(indexedSelects).toHaveLength(5)
    expect(indexedSelects[0].props('filterable')).toBe(true)
    expect(indexedSelects[1].props('filterable')).toBe(true)
    expect(indexedSelects[2].props('filterable')).toBe(true)
    expect(indexedSelects[3].props('filterable')).toBe(true)
    expect(searchPanel.find('input[aria-label="库存项目号"]').exists()).toBe(true)
    expect(searchPanel.find('input[aria-label="库存子项目号"]').exists()).toBe(true)
    expect(searchPanel.find('.el-table').exists()).toBe(false)
    expect(wrapper.get('.material-inventory__results').find('.el-table').exists()).toBe(true)
  })

  it('修改筛选条件后只有点击查询才请求库存', async () => {
    const wrapper = mount(MaterialInventory, {
      props: { token: 'token' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    const callsAfterInitialLoad = api.listMaterialInventory.mock.calls.length

    const warehouseSelect = wrapper.findComponent({ name: 'ElSelect' })
    warehouseSelect.vm.$emit('update:modelValue', '2号常备仓')
    warehouseSelect.vm.$emit('change', '2号常备仓')
    const indexedSelects = wrapper.get('[aria-label="库存搜索"]').findAllComponents({ name: 'ElSelect' })
    const brandSelect = indexedSelects[1]
    const projectSelect = indexedSelects[2]
    const subprojectSelect = indexedSelects[3]
    brandSelect.vm.$emit('update:modelValue', '国优')
    projectSelect.vm.$emit('update:modelValue', 'P700005')
    projectSelect.vm.$emit('change', 'P700005')
    subprojectSelect.vm.$emit('update:modelValue', 'P700005-6')
    await wrapper.get('input[aria-label="库存项目号"]').trigger('keydown.enter')
    await flushPromises()

    expect(api.listMaterialInventory).toHaveBeenCalledTimes(callsAfterInitialLoad)

    wrapper.findComponent({ name: 'ElPagination' }).vm.$emit('current-change', 2)
    await flushPromises()

    expect(api.listMaterialInventory).toHaveBeenCalledTimes(callsAfterInitialLoad + 1)
    expect(api.listMaterialInventory).toHaveBeenLastCalledWith(expect.objectContaining({
      warehouse: '',
      projectCode: '',
      subproject: '',
    }), 'token')

    await wrapper.findAll('button').find(button => button.text().trim() === '查询')!.trigger('click')
    await flushPromises()

    expect(api.listMaterialInventory).toHaveBeenCalledTimes(callsAfterInitialLoad + 2)
    expect(api.listMaterialInventory).toHaveBeenLastCalledWith(expect.objectContaining({
      warehouse: '2号常备仓',
      brand: '国优',
      projectCode: 'P700005',
      subproject: 'P700005-6',
    }), 'token')
  })

  it('品牌和项目索引可检索并按项目联动子项目', async () => {
    const wrapper = mount(MaterialInventory, {
      props: { token: 'token' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    const callsAfterInitialLoad = api.listMaterialInventory.mock.calls.length
    const indexedSelects = wrapper.get('[aria-label="库存搜索"]').findAllComponents({ name: 'ElSelect' })
    const brandSelect = indexedSelects[1]
    const projectSelect = indexedSelects[2]
    const subprojectSelect = indexedSelects[3]

    expect(brandSelect.findAllComponents({ name: 'ElOption' })).toHaveLength(2)
    expect(projectSelect.findAllComponents({ name: 'ElOption' })).toHaveLength(2)
    expect(subprojectSelect.findAllComponents({ name: 'ElOption' })).toHaveLength(2)

    subprojectSelect.vm.$emit('update:modelValue', 'P700006-1')
    projectSelect.vm.$emit('update:modelValue', 'P700005')
    await flushPromises()
    projectSelect.vm.$emit('change', 'P700005')
    await flushPromises()

    expect(subprojectSelect.props('modelValue')).toBe('')
    expect(subprojectSelect.findAllComponents({ name: 'ElOption' })).toHaveLength(1)
    expect(api.listMaterialInventory).toHaveBeenCalledTimes(callsAfterInitialLoad)
  })

  it('相似查询独立于普通筛选，分页沿用已查询条件，返回普通查询恢复条件', async () => {
    const wrapper = mount(MaterialInventory, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get('input[aria-label="库存料号"]').setValue('OLD')
    await wrapper.findAll('button').find(button => button.text() === '查询')!.trigger('click')
    await flushPromises()
    const result = { ...(await api.listMaterialInventory()), items: [{ ...inventoryRow, similarityPercent: 75 }], total: 60 }
    api.listMaterialInventory.mockResolvedValue(result)
    await wrapper.get('input[aria-label="相似库存规格"]').setValue('60W5')
    wrapper.get('[aria-label="相似库存查询"]').findComponent({ name: 'ElSelect' }).vm.$emit('update:modelValue', '国优')
    await wrapper.findAll('button').find(button => button.text() === '查询相似')!.trigger('click')
    await flushPromises()
    expect(api.listMaterialInventory).toHaveBeenLastCalledWith({ similarSpecification: '60W5', brand: '国优', positiveStockOnly: true, page: 1, pageSize: 50 }, 'token')
    expect(wrapper.text()).toContain('相似查询中')
    expect(wrapper.text()).toContain('75.0%')
    await wrapper.get('input[aria-label="相似库存规格"]').setValue('OTHER')
    const pagination = wrapper.findComponent({ name: 'ElPagination' })
    pagination.vm.$emit('update:current-page', 2)
    pagination.vm.$emit('current-change', 2)
    await flushPromises()
    expect(api.listMaterialInventory).toHaveBeenLastCalledWith(expect.objectContaining({ similarSpecification: '60W5', page: 2 }), 'token')
    await wrapper.findAll('button').find(button => button.text() === '普通查询')!.trigger('click')
    await flushPromises()
    expect(api.listMaterialInventory).toHaveBeenLastCalledWith(expect.objectContaining({ materialCode: 'OLD', page: 1 }), 'token')
    expect(wrapper.find('.el-table__header-wrapper').text()).not.toContain('相似度')
    expect(api.refreshMaterialInventory).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('短型号不发起相似查询', async () => {
    const wrapper = mount(MaterialInventory, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const calls = api.listMaterialInventory.mock.calls.length
    await wrapper.get('input[aria-label="相似库存规格"]').setValue('A-1')
    await wrapper.findAll('button').find(button => button.text() === '查询相似')!.trigger('click')
    await flushPromises()
    expect(api.listMaterialInventory).toHaveBeenCalledTimes(calls)
    wrapper.unmount()
  })

  it('收到料品行查询请求后实时刷新该料号并显示零库存', async () => {
    const wrapper = mount(MaterialInventory, {
      props: { token: 'token' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await wrapper.setProps({ requestedMaterialCode: '01020141559', requestKey: 1 })
    await flushPromises()

    expect(api.refreshMaterialInventory).toHaveBeenCalledWith('01020141559', 'token')
    expect(wrapper.get('input[aria-label="库存料号"]').element).toHaveProperty('value', '01020141559')
  })
})
