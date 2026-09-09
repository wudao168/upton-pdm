import { describe, expect, it } from 'vitest'
import { isDefaultInventoryWarehouse, sumProcurementInventory } from '../src/procurementInventory'
import type { U9InventoryRow } from '../src/types'

const row = (warehouseName: string, stockQuantity: number, projectCode?: string, subproject?: string) => ({ warehouseName, stockQuantity, projectCode, subproject } as U9InventoryRow)

describe('备料库存汇总范围', () => {
  const rows = [
    row('2号项目仓', 1, 'P700005', 'P700005-1'),
    row('2号项目仓', 2, 'P700005', 'P700005-3'),
    row('2号项目仓', 100, 'P7000050'),
    row('2号项目仓', 100),
    row('零成本呆滞仓', 3), row('1号常备仓', 4), row('2号常备仓', 5),
    row('项目退料仓', 6, 'P999999'), row('昆山应急仓', 7),
    row('昆山自制品仓', 100), row('机加工仓库', 100), row('昆山厂内成品仓', 100),
  ]
  it('默认仅含五类仓库，项目号精确匹配且包含同项目子项目', () => {
    expect(sumProcurementInventory(rows, 'P700005', null)).toBe(28)
    expect(rows.filter(r => isDefaultInventoryWarehouse(r.warehouseName))).toHaveLength(9)
  })
  it('勾选范围只计一次，未勾选不计入', () => {
    expect(sumProcurementInventory(rows, 'P700005', ['1号常备仓', '1号常备仓', '2号常备仓', '昆山应急仓'])).toBe(16)
    expect(sumProcurementInventory(rows, 'P700005', [])).toBe(0)
    expect(sumProcurementInventory(rows, '', ['2号项目仓'])).toBe(0)
    expect(sumProcurementInventory(rows, 'P700005', ['机加工仓库', '昆山自制品仓'])).toBe(200)
  })
  it('保留负库存和小数，不变更输入数据', () => {
    const input = [row('2号常备仓', 2.5), row('2号常备仓', -1)]
    expect(sumProcurementInventory(input, 'P700005', ['2号常备仓'])).toBe(1.5)
    expect(input[1].stockQuantity).toBe(-1)
  })
})
