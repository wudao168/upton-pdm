import type { SettlementLine } from './projectBudget'

export const applicationSettlementMaxLines = 100
export const applicationSettlementMaxQuantity = 1_000_000
export const applicationSettlementMaxAmount = 1_000_000_000
const maximumCents = applicationSettlementMaxAmount * 100
export const newApplicationSettlementLine = (): SettlementLine => ({ content: '', unit: '项', quantity: null, unitPrice: null, note: null })
export const copyApplicationSettlement = (lines?: readonly SettlementLine[] | null): SettlementLine[] =>
  (lines ?? []).map(line => ({ content: line.content ?? '', unit: line.unit ?? '项', quantity: line.quantity ?? null, unitPrice: line.unitPrice ?? null, note: line.note ?? null }))

function priceCents(value: number): number | null {
  if (!Number.isFinite(value) || value < 0 || value > applicationSettlementMaxAmount) return null
  const cents = Math.round(value * 100)
  return value === Number(value.toFixed(2)) ? cents : null
}
function lineCents(line: SettlementLine): number | null {
  if (line.quantity == null || line.unitPrice == null || !Number.isInteger(line.quantity) || line.quantity < 1 || line.quantity > applicationSettlementMaxQuantity) return null
  const cents = priceCents(line.unitPrice)
  // 先验证乘积上限，再相乘，避免超过安全整数的分单位金额。
  if (cents == null || cents > 0 && line.quantity > maximumCents / cents) return null
  return line.quantity * cents
}
export const applicationSettlementLineAmount = (line: SettlementLine): number | null => {
  const cents = lineCents(line)
  return cents == null ? null : cents / 100
}
export function applicationSettlementTotal(lines?: readonly SettlementLine[] | null): number | null {
  if (!lines?.length) return null
  let total = 0
  for (const line of lines) {
    const cents = lineCents(line)
    if (cents == null || cents > maximumCents - total) return null
    total += cents
  }
  return total / 100
}
export function applicationSettlementError(lines?: readonly SettlementLine[] | null, requireComplete = false): string | null {
  if (!lines?.length) return null
  if (lines.length > applicationSettlementMaxLines) return '项目结算明细最多100行，请删除多余明细。'
  let total = 0
  for (const [index, line] of lines.entries()) {
    const row = `项目结算第${index + 1}行`
    if (requireComplete && (!line.content.trim() || line.quantity == null || line.unitPrice == null))
      return `${row}请填写内容、数量和单价；单价为0时请明确填写0，也可先保存草稿。`
    if (line.content.length > 200) return `${row}内容不能超过200个字。`
    if (line.quantity != null && (!Number.isInteger(line.quantity) || line.quantity < 1 || line.quantity > applicationSettlementMaxQuantity))
      return `${row}数量须为1至1,000,000之间的整数。`
    if (line.unitPrice != null && priceCents(line.unitPrice) == null)
      return `${row}单价须为0至1,000,000,000元，最多保留2位小数。`
    if (line.quantity != null && line.unitPrice != null) {
      const cents = lineCents(line)
      if (cents == null) return `${row}小计不能超过10亿元。`
      if (cents > maximumCents - total) return '项目结算总额不能超过10亿元。'
      total += cents
    }
  }
  return null
}
