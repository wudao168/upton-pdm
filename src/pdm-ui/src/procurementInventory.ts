import type { U9InventoryRow } from './types'

export const inventoryScopes = ['当前项目仓', '呆滞仓', '常备仓', '退料仓', '应急仓'] as const
export function isDefaultInventoryWarehouse(name: string) {
  return inventoryScopes.some(scope => name.trim().endsWith(scope === '当前项目仓' ? '项目仓' : scope))
}

export function sumProcurementInventory(rows: U9InventoryRow[], projectCode: string, warehouses: string[] | null) {
  const project = projectCode.trim()
  return rows.reduce((sum, row) => {
    const warehouse = row.warehouseName.trim()
    const selected = warehouses === null ? isDefaultInventoryWarehouse(warehouse) : warehouses.includes(warehouse)
    const included = selected && (!warehouse.endsWith('项目仓') || Boolean(project) && row.projectCode?.trim() === project)
    return included ? sum + row.stockQuantity : sum
  }, 0)
}
