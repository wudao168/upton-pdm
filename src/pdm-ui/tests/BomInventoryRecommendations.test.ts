import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import BomInventoryRecommendations from '../src/components/BomInventoryRecommendations.vue'
import { inventoryBomSignature } from '../src/bomInventoryRecommendations'
import type { InventoryBomRow } from '../src/bomInventoryRecommendations'
import type { PdmMaterial, U9InventoryPage, U9InventoryRow } from '../src/types'
const api = vi.hoisted(() => ({ listMaterialInventory: vi.fn(), matchApprovedMaterials: vi.fn(), refreshMaterialInventory: vi.fn() }))
vi.mock('../src/api', () => api)
const original = { key: 'bom-1', sequence: 1, drawingNumber: 'ORIGINAL', name: '气缸', specification: 'ABC-100', brand: 'SMC', quantity: 3, unit: '个' }
const item: InventoryBomRow = { ...original, signature: inventoryBomSignature(original) }
const stock: U9InventoryRow = { organizationCode: '7', warehouseCode: '01', warehouseName: '常备仓', materialCode: 'MAT-1', itemName: '候选气缸', specification: 'ABC-110', brand: 'SMC', stockQuantity: 5,
  availableQuantity: 0, reservedQuantity: 0, unavailableQuantity: 0, refreshedAt: '2026-10-15T00:00:00Z', similarityPercent: 90, similarityReason: '型号近似；品牌一致' }
const page = (items: U9InventoryRow[] = [stock], total = items.length): U9InventoryPage => ({ items, total, page: 1, pageSize: 200, warehouseNames: ['常备仓', '呆滞仓', '应急仓', '项目仓', '其他仓'], brandNames: [], projectCodes: [], subprojectOptions: [] })
const material = { id: 'mat-1', materialCode: 'MAT-1', name: '候选气缸', kind: 'Standard', specification: 'ABC-110', brand: 'SMC', unitCode: '个', approvalStatus: 'Approved', isArchived: false } as PdmMaterial
let wrapper: ReturnType<typeof mount>
function open(items: InventoryBomRow[] = [item], kind: 'Standard' | 'Electrical' = 'Standard', initialKey?: string) {
  wrapper = mount(BomInventoryRecommendations, { props: { token: 'token', kind, items, initialKey }, global: { stubs: { Teleport: true } } })
  return wrapper
}
function button(text: string) { return wrapper.findAll('button').find(value => value.text() === text)! }
async function inspect() { await button('查看对比').trigger('click'); await flushPromises() }
beforeEach(() => {
  vi.resetAllMocks()
  window.localStorage.clear()
  api.listMaterialInventory.mockResolvedValue(page())
  api.matchApprovedMaterials.mockResolvedValue([material])
  api.refreshMaterialInventory.mockResolvedValue(page())
})
afterEach(() => { wrapper?.unmount() })
describe('BomInventoryRecommendations', () => {
  it('shows only BOM items with inventory candidates and automatically selects the first match', async () => {
    api.listMaterialInventory.mockImplementation(filters => Promise.resolve(filters.similarSpecification === 'NONE-000' ? page([]) : page()))
    open([{ ...item, key: 'none', name: '不应显示的物料', specification: 'NONE-000' }, item, { ...item, key: 'short', specification: '' }])
    await flushPromises()
    const rows = wrapper.findAll('.bom-inventory__existing-table tbody tr')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.attributes('aria-selected')).toBe('true')
    expect(wrapper.text()).not.toContain('不应显示的物料')
    expect(wrapper.text()).not.toContain('候选 0')
    expect(wrapper.get('[aria-label="现有BOM物料"]').text()).toContain('有推荐 1 项')
    expect(wrapper.get('[aria-label="库存物料推荐"]').text()).toContain('MAT-1')
  })
  it('keeps the requested matching BOM item selected when opened from its row badge', async () => {
    open([item, { ...item, key: 'requested', sequence: 2 }], 'Standard', 'requested')
    await flushPromises()
    const rows = wrapper.findAll('.bom-inventory__existing-table tbody tr')
    expect(rows[0]!.attributes('aria-selected')).toBe('false')
    expect(rows[1]!.attributes('aria-selected')).toBe('true')
  })
  it('shows no zero-candidate rows after a successful scan with no matches', async () => {
    api.listMaterialInventory.mockResolvedValue(page([]))
    open(); await flushPromises()
    expect(wrapper.findAll('.bom-inventory__existing-table tbody tr')).toHaveLength(0)
    expect(wrapper.findAll('.bom-inventory__candidates tbody tr')).toHaveLength(0)
    expect(wrapper.text()).toContain('暂无有库存推荐的物料')
    expect(wrapper.text()).not.toContain('ORIGINAL')
  })
  it('selects a remaining recommended item after refreshed stock removes the selected one', async () => {
    api.listMaterialInventory.mockImplementation(filters => Promise.resolve(filters.similarSpecification === 'OTHER-100'
      ? page([{ ...stock, materialCode: 'MAT-2', specification: 'OTHER-110' }]) : page()))
    api.refreshMaterialInventory.mockResolvedValue(page([]))
    open([item, { ...item, key: 'other', specification: 'OTHER-100' }]); await flushPromises(); await inspect()
    await button('刷新库存').trigger('click'); await flushPromises()
    const rows = wrapper.findAll('.bom-inventory__existing-table tbody tr')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.attributes('aria-selected')).toBe('true')
    expect(wrapper.get('[aria-label="库存物料推荐"]').text()).toContain('MAT-2')
    expect(wrapper.get('[aria-label="库存物料推荐"]').text()).not.toContain('MAT-1')
  })

  it('scans identical models once, preserves warehouse details and requires explicit confirmation', async () => {
    open([item, { ...item, key: 'bom-2', sequence: 2 }])
    await flushPromises()
    expect(api.listMaterialInventory.mock.calls.filter(([filters]) => filters.similarSpecification)).toHaveLength(1)
    expect(api.listMaterialInventory).toHaveBeenCalledWith(expect.objectContaining({ similarSpecification: 'ABC-100', positiveStockOnly: true }), 'token', expect.any(AbortSignal))
    expect(wrapper.text()).toContain('扫描完成 2 / 2')
    expect(wrapper.emitted('replace')).toBeUndefined()
    await inspect()
    expect(wrapper.findAll('mark').map(value => value.text()).join('')).toContain('1')
    expect(wrapper.text()).toContain('常备仓')
    expect(button('确认替换').attributes('disabled')).toBeDefined()
    await wrapper.get('input[type="checkbox"]').setValue(true)
    await button('确认替换').trigger('click')
    expect(wrapper.emitted('replace')?.[0]).toEqual([item, material])
    expect(wrapper.text()).toContain('已暂存替换 → MAT-1')
  })
  it('reads all inventory pages rather than truncating matches at 200', async () => {
    api.listMaterialInventory.mockImplementation(filters => Promise.resolve(!filters.similarSpecification ? page([]) : filters.page === 1 ? page([stock], 2) : page([{ ...stock, warehouseName: '项目仓', projectCode: 'P2' }], 2)))
    open(); await flushPromises()
    expect(api.listMaterialInventory).toHaveBeenCalledWith( expect.objectContaining({ page: 2 }), 'token', expect.any(AbortSignal))
    await inspect()
    expect(wrapper.text()).toContain('项目仓')
    expect(wrapper.text()).toContain('P2')
  })
  it.each([
    [{ ...material, kind: 'Electrical' }, '分类与当前BOM不一致'],
    [{ ...material, isArchived: true }, '未批准或已停用'],
    [{ ...material, approvalStatus: 'Draft' }, '未批准或已停用'],
    [{ ...material, unitCode: 'kg' }, '单位与原BOM不一致'],
    [{ ...material, specification: 'DIFFERENT' }, '库存型号与料品主档不一致'],
  ])('blocks unsuitable master data: %s', async (value, message) => {
    api.matchApprovedMaterials.mockResolvedValue([value])
    open(); await flushPromises(); await inspect()
    expect(wrapper.text()).toContain(message)
    expect(button('确认替换').attributes('disabled')).toBeDefined()
  })
  it('supports electrical materials using the same explicit review flow', async () => {
    api.matchApprovedMaterials.mockResolvedValue([{ ...material, kind: 'Electrical' }])
    open([item], 'Electrical'); await flushPromises(); await inspect()
    await wrapper.get('input[type="checkbox"]').setValue(true)
    expect(button('确认替换').attributes('disabled')).toBeUndefined()
  })
  it('retains the original material without emitting a replacement', async () => {
    open(); await flushPromises(); await inspect()
    await button('保留原物料').trigger('click')
    expect(wrapper.text()).toContain('保留原物料')
    expect(wrapper.emitted('replace')).toBeUndefined()
  })
  it('hides a material that already has positive stock under its original code', async () => {
    api.listMaterialInventory.mockResolvedValue(page([{ ...stock, materialCode: 'ORIGINAL' }]))
    open(); await flushPromises()
    expect(wrapper.findAll('.bom-inventory__existing-table tbody tr')).toHaveLength(0)
    expect(api.listMaterialInventory.mock.calls.filter(([filters]) => filters.similarSpecification)).toHaveLength(0)
  })
  it('matches the exact model and specified brand when the original code is missing', async () => {
    api.listMaterialInventory.mockResolvedValue(page([{ ...stock, specification: 'ABC-100' }]))
    open([{ ...item, drawingNumber: '' }]); await flushPromises()
    expect(wrapper.findAll('.bom-inventory__existing-table tbody tr')).toHaveLength(0)
  })
  it('filters unselected warehouses before grouping stock and counts', async () => {
    window.localStorage.setItem('upton-pdm:bom-inventory-warehouses:anonymous', JSON.stringify({ warehouses: ['呆滞仓'] }))
    api.listMaterialInventory.mockResolvedValue(page([stock, { ...stock, warehouseName: '呆滞仓', stockQuantity: 2 }]))
    open(); await flushPromises(); await inspect()
    const locations = wrapper.findAll('.bom-inventory__locations tbody tr')
    expect(locations).toHaveLength(1)
    expect(locations[0]!.text()).toContain('呆滞仓')
    expect(wrapper.get('.bom-inventory__candidates').text()).toContain('1 条库位明细')
    expect(wrapper.get('.bom-inventory__scope').text()).not.toContain('常备仓')
  })
  it('ignores original stock outside the selected warehouses', async () => {
    window.localStorage.setItem('upton-pdm:bom-inventory-warehouses:anonymous', JSON.stringify({ warehouses: ['呆滞仓'] }))
    api.listMaterialInventory.mockResolvedValue(page([{ ...stock, materialCode: 'ORIGINAL' }, { ...stock, warehouseName: '呆滞仓' }]))
    open(); await flushPromises()
    expect(wrapper.findAll('.bom-inventory__existing-table tbody tr')).toHaveLength(1)
    expect(wrapper.get('.bom-inventory__candidates').text()).not.toContain('ORIGINAL')
  })
  it('saves warehouse choices and rescans, retaining an explicit empty selection', async () => {
    open(); await flushPromises()
    await button('库存设置').trigger('click')
    for (const checkbox of wrapper.findAll('.bom-inventory__warehouses input[type="checkbox"]')) await checkbox.setValue(false)
    await button('保存并扫描').trigger('click'); await flushPromises()
    expect(JSON.parse(localStorage.getItem('upton-pdm:bom-inventory-warehouses:anonymous')!).warehouses).toEqual([])
    expect(wrapper.findAll('.bom-inventory__existing-table tbody tr')).toHaveLength(0)
    expect(wrapper.get('.bom-inventory__scope').text()).toContain('未选择仓库')
  })
  it('hides empty, too short and kit models and shows the unified empty state', async () => {
    open([{ ...item, specification: '' }, { ...item, key: 'short', specification: 'A1' }, { ...item, key: 'kit', disabledReason: '套件子料请通过套件维护' }])
    await flushPromises()
    expect(api.listMaterialInventory.mock.calls.filter(([filters]) => filters.similarSpecification)).toHaveLength(0)
    expect(wrapper.findAll('.bom-inventory__existing-table tbody tr')).toHaveLength(0)
    expect(wrapper.text()).toContain('暂无有库存推荐的物料')
    expect(wrapper.text()).not.toContain('型号不足4个有效字符')
    expect(wrapper.text()).not.toContain('套件子料请通过套件维护')
  })
  it('translates request errors to Chinese and permits retry', async () => {
    api.listMaterialInventory.mockRejectedValueOnce(new Error('Failed to fetch'))
    open(); await flushPromises()
    expect(wrapper.text()).toContain('无法连接服务器')
    expect(wrapper.text()).not.toContain('Failed to fetch')
    await button('重新扫描').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('候选 1')
  })
  it('invalidates adoption when refreshed stock is gone', async () => {
    api.refreshMaterialInventory.mockResolvedValue(page([]))
    open(); await flushPromises(); await inspect()
    await wrapper.get('input[type="checkbox"]').setValue(true)
    await button('刷新库存').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('已无正库存')
    expect(wrapper.findAll('.bom-inventory__candidates tbody button')).toHaveLength(0)
    expect((wrapper.emitted('scanned')!.at(-1)![0] as Array<{ count: number }>)[0]!.count).toBe(0)
    expect(wrapper.find('.bom-inventory__comparison').exists()).toBe(false)
    expect(wrapper.text()).toContain('暂无有库存推荐的物料')
  })
  it('ignores a material lookup completed after selecting a different BOM row', async () => {
    let resolve!: (value: PdmMaterial[]) => void
    api.matchApprovedMaterials.mockReturnValue(new Promise(value => { resolve = value }))
    open([item, { ...item, key: 'other', sequence: 2 }]); await flushPromises()
    await button('查看对比').trigger('click')
    await wrapper.get('.bom-inventory__existing-table tbody tr:nth-child(2)').trigger('click')
    resolve([material]); await flushPromises()
    expect(wrapper.find('[aria-label="替换物料对比"]').exists()).toBe(false)
    expect(wrapper.emitted('replace')).toBeUndefined()
  })
  it('cancels queued scans and ignores results after closing', async () => {
    let resolve!: (value: U9InventoryPage) => void
    api.listMaterialInventory.mockReturnValue(new Promise(value => { resolve = value }))
    open(); await flushPromises()
    const signal = api.listMaterialInventory.mock.calls[0]![2] as AbortSignal
    wrapper.unmount()
    resolve(page()); await flushPromises()
    expect(signal.aborted).toBe(true)
    expect(wrapper.emitted('scanned')).toBeUndefined()
  })
})
