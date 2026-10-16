import { describe, expect, it } from 'vitest'
import { groupInventoryCandidates, hasOriginalInventory, inventoryModelDifference, normalizeInventoryModel } from '../src/bomInventoryRecommendations'
import type { U9InventoryRow } from '../src/types'
const stock = (values: Partial<U9InventoryRow> = {}): U9InventoryRow => ({
  organizationCode: '7', warehouseCode: '01', warehouseName: '常备仓', materialCode: 'MAT-1', itemName: '气缸',
  specification: 'ABC-100', stockQuantity: 2, availableQuantity: 0, reservedQuantity: 0, unavailableQuantity: 0,
  refreshedAt: '2026-10-15T00:00:00Z', similarityPercent: 90, ...values,
})
describe('inventory model comparison', () => {
  it('recognizes stock by original code, using exact model and brand only without a code', () => {
    const item = { key: 'one', signature: '', sequence: 1, drawingNumber: 'MAT-1', name: '', specification: 'OTHER', brand: 'SMC', quantity: 1, unit: '个' }
    expect(hasOriginalInventory(item, [stock()])).toBe(true)
    expect(hasOriginalInventory({ ...item, drawingNumber: 'OTHER' }, [stock()])).toBe(false)
    expect(hasOriginalInventory({ ...item, drawingNumber: '', specification: 'ABC-100' }, [stock({ brand: 'SMC' })])).toBe(true)
    expect(hasOriginalInventory({ ...item, drawingNumber: '', specification: 'ABC-100' }, [stock({ brand: 'FESTO' })])).toBe(false)
    expect(hasOriginalInventory({ ...item, drawingNumber: '', specification: 'ABC-100', brand: '' }, [stock()])).toBe(true)
    expect(hasOriginalInventory(item, [stock({ stockQuantity: 0 })])).toBe(false)
  })

  it('normalizes width, case and separators, preserving numeric model variants', () => {
    expect(normalizeInventoryModel('ＡＢＣ－１００ / a')).toBe('ABC100A')
  })
  it('groups one code across warehouses and preserves ownership and location detail', () => {
    const rows = [stock(), stock({ warehouseCode: '02', warehouseName: '项目仓', projectCode: 'P2', stockQuantity: 3 })]
    expect(groupInventoryCandidates(rows)).toEqual([expect.objectContaining({ stockQuantity: 5, locations: rows })])
  })
  it('excludes nonpositive stock and scores below 80, sorting the remaining matches', () => {
    const grouped = groupInventoryCandidates([stock(), stock({ materialCode: 'LOW', similarityPercent: 79 }),
      stock({ materialCode: 'EMPTY', stockQuantity: 0 }), stock({ materialCode: 'NEG', stockQuantity: -1 }),
      stock({ materialCode: 'HIGH', similarityPercent: 100 })])
    expect(grouped.map(item => item.materialCode)).toEqual(['HIGH', 'MAT-1'])
  })
  it.each([
    ['ABC-100', 'ABC-110', '1'], ['ABC100', 'ABC100A', 'A'], ['ABC100A', 'ABC100', ''],
    ['ＡＢＣ－１００', 'abc-100', ''], ['ABC100', 'ABC200', '2'],
  ])('preserves text and highlights changed model characters: %s → %s', (left, right, changed) => {
    const parts = inventoryModelDifference(left, right)
    expect(parts.map(part => part.text).join('')).toBe(right)
    expect(parts.filter(part => part.changed).map(part => part.text).join('')).toBe(changed)
  })
})
