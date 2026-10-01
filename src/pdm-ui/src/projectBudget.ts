export interface BudgetLine {
  category: string
  budgetAmount: number | null
  actualAmount: number | null
  remainingAmount: number | null
  plannedHours: number | null
  hourlyRate: number | null
  actualHours: number | null
  actualHourlyRate: number | null
  remainingHours: number | null
  note: string | null
}
export interface BudgetRow {
  input: BudgetLine
  budgetAmount: number | null
  actualAmount: number | null
  orderAmount: number | null
  estimatedAmount: number | null
  variance: number | null
  alert: string
}
export interface BudgetOrder {
  key: string; documentNumber: string; lineNumber: number; materialCode: string
  itemName: string; amount: number | null; category: string
}
export interface ProjectBudget {
  projectId: string; rows: BudgetRow[]; orders: BudgetOrder[]
  budgetAmount: number | null; actualAmount: number | null
  estimatedAmount: number | null; overrunAmount: number | null
  canEdit: boolean; canEditBudget?: boolean; canEditActual?: boolean; canViewReserve: boolean; rowVersion: number
  updatedBy: string | null; updatedAt: string | null
}
export const budgetCategories = [
  { key: 'Standard', name: '标准件', labor: false },
  { key: 'Nonstandard', name: '非标件', labor: false },
  { key: 'Equipment', name: '外购设备', labor: false },
  { key: 'RiskReserve', name: '风险预留', labor: false },
  { key: 'Logistics', name: '物流费用', labor: false },
  { key: 'Other', name: '其他费用', labor: false },
  { key: 'MechanicalDesign', name: '机械设计', labor: true },
  { key: 'ElectricalDesign', name: '电气设计', labor: true },
  { key: 'InternalCommissioning', name: '装配调试', labor: true },
  { key: 'SiteElectrical', name: '现场电气', labor: true },
  { key: 'SiteCommissioning', name: '厂外调试', labor: true },
]
export function calculateBudgetRow(input: BudgetLine, orderAmount: number | null, missingOrder = false): BudgetRow {
  const labor = budgetCategories.find(category => category.key === input.category)?.labor
  const product = (hours: number | null, rate: number | null) => hours == null || rate == null ? null : Math.round(hours * rate * 100) / 100
  const budgetAmount = input.budgetAmount
  const actualAmount = input.actualAmount
  const remaining = labor ? product(input.remainingHours, input.actualHourlyRate) : input.remainingAmount
  const estimatedAmount = input.category === 'RiskReserve' ? actualAmount
    : actualAmount == null || remaining == null || missingOrder ? null : Math.max(actualAmount, orderAmount ?? 0) + remaining
  const variance = estimatedAmount == null || budgetAmount == null ? null : estimatedAmount - budgetAmount
  const alert = budgetAmount != null && [actualAmount, orderAmount, estimatedAmount].some(amount => amount != null && amount > budgetAmount) ? 'Overrun'
    : budgetAmount == null || estimatedAmount == null ? 'Incomplete'
    : budgetAmount > 0 && estimatedAmount >= budgetAmount * .9 ? 'Warning' : 'Normal'
  return { input, budgetAmount, actualAmount, orderAmount, estimatedAmount, variance, alert }
}

export interface AssessmentItem { id: string; name: string; model: string | null; brand: string | null; note: string | null; quantity: number | null; unitPrice: number | null; laborCategory?: string | null }
export interface AssessmentGroup { id: string; name: string; category: string; laborCategory: string | null; items: AssessmentItem[] }
export interface AssessmentSheet { projectId: string; projectCode: string; projectName: string; designLead: string | null; canEdit: boolean; rowVersion: number; groups: AssessmentGroup[]; total: number | null; updatedBy: string | null; updatedAt: string | null }
export interface AssessmentDirectory { sheets: AssessmentSheet[]; amounts: Record<string, number | null>; ratesProjectId?: string; ratesRowVersion?: number; canEditLaborRates?: boolean; laborRates?: Record<string, number | null> }
export const assessmentItemTotal = (item: AssessmentItem) => item.quantity == null || item.unitPrice == null ? null : Math.round((item.quantity * item.unitPrice + Number.EPSILON) * 100) / 100
export const assessmentSum = (values: Array<number | null>) => values.some(value => value != null) ? values.reduce<number>((sum, value) => sum + (value ?? 0), 0) : null
export function uppercaseMoney(amount: number | null): string {
  if (amount == null) return '—'
  const cents = Math.round(amount * 100)
  if (cents === 0) return '人民币零元整'
  const digits = '零壹贰叁肆伍陆柒捌玖'
  const section = (value: number) => {
    let result = ''; let zero = false
    for (let place = 3; place >= 0; place--) {
      const digit = Math.floor(value / 10 ** place) % 10
      if (digit) { if (zero) result += '零'; result += digits[digit] + ['','拾','佰','仟'][place]; zero = false }
      else if (result) zero = true
    }
    return result
  }
  const yuan = Math.floor(cents / 100)
  let whole = ''; let gap = false
  for (let index = 3; index >= 0; index--) {
    const part = Math.floor(yuan / 10000 ** index) % 10000
    if (part) { if (whole && (gap || part < 1000)) whole += '零'; whole += section(part) + ['', '万', '亿', '万亿'][index]; gap = false }
    else if (whole) gap = true
  }
  let result = '人民币' + (whole || '零') + '元'
  const jiao = Math.floor(cents / 10) % 10; const fen = cents % 10
  if (jiao) result += digits[jiao] + '角'
  if (fen) result += (jiao ? '' : '零') + digits[fen] + '分'
  return result + (!jiao && !fen ? '整' : '')
}
