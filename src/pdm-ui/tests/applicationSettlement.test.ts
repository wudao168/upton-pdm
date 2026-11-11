import { describe, expect, it } from 'vitest'
import { applicationSettlementError, applicationSettlementLineAmount, applicationSettlementTotal, copyApplicationSettlement, newApplicationSettlementLine } from '../src/applicationSettlement'
import type { SettlementLine } from '../src/projectBudget'

const line = (quantity: number | null, unitPrice: number | null): SettlementLine => ({ content: '设备合同结算', unit: '项', quantity, unitPrice, note: null })

describe('立项结算金额与提交校验', () => {
  it('空值和半填草稿保留未知，明确的零单价显示零', () => {
    expect(applicationSettlementTotal()).toBeNull()
    expect(applicationSettlementTotal([])).toBeNull()
    const incomplete = newApplicationSettlementLine()
    incomplete.content = '尚待确认报价'
    expect(applicationSettlementError([incomplete])).toBeNull()
    expect(applicationSettlementError([incomplete], true)).toContain('请填写内容、数量和单价')
    expect(applicationSettlementTotal([incomplete])).toBeNull()
    expect(applicationSettlementTotal([line(2, 0)])).toBe(0)
    expect(applicationSettlementError([line(2, 0)], true)).toBeNull()
  })
  it('按分累计每行金额，不产生浮点合计误差', () => {
    expect(applicationSettlementLineAmount(line(3, 0.29))).toBe(0.87)
    expect(applicationSettlementTotal([line(2, 100.25), line(3, 0.10)])).toBe(200.80)
    expect(applicationSettlementTotal(Array.from({ length: 100 }, () => line(3, 0.01)))).toBe(3)
  })
  it('拒绝小数计件数量、越界和无效金额，不自动取整', () => {
    for (const quantity of [0, -1, 1.5, 1_000_001, Number.NaN, Number.POSITIVE_INFINITY]) {
      const value = line(quantity, 1)
      expect(applicationSettlementError([value])).toContain('整数')
      expect(applicationSettlementLineAmount(value)).toBeNull()
      expect(value.quantity).toBe(quantity)
    }
    for (const price of [-1, 1.005, 1_000_000_001, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(applicationSettlementError([line(1, price)])).toContain('单价')
      expect(applicationSettlementTotal([line(1, price)])).toBeNull()
    }
  })
  it('先限制单行乘积和总额再计算，守住10亿元边界', () => {
    expect(applicationSettlementTotal([line(1_000_000, 1000)])).toBe(1_000_000_000)
    expect(applicationSettlementError([line(1_000_000, 1000)], true)).toBeNull()
    expect(applicationSettlementError([line(1_000_000, 1000.01)])).toContain('小计不能超过10亿元')
    expect(applicationSettlementTotal([line(1_000_000, 1_000_000_000)])).toBeNull()
    const overTotal = [line(1, 500_000_000), line(1, 500_000_000.01)]
    expect(applicationSettlementError(overTotal)).toContain('总额不能超过10亿元')
    expect(applicationSettlementTotal(overTotal)).toBeNull()
    expect(applicationSettlementError(Array.from({ length: 101 }, () => line(1, 0)))).toContain('最多100行')
  })
  it('复制编辑行不会改写原申请快照，历史空列表保持兼容', () => {
    const source = [line(2, 100.25)]
    const copy = copyApplicationSettlement(source)
    copy[0]!.unitPrice = 150.25
    copy[0]!.content = '审批确认后的结算'
    expect(source).toEqual([line(2, 100.25)])
    expect(copyApplicationSettlement(null)).toEqual([])
    expect(applicationSettlementError(undefined, true)).toBeNull()
  })
})
