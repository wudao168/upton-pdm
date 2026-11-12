export interface PurchaseFilter {
  from: string; through: string; organizations: string[]; types: string[]; categories: string[]; suppliers: string[]; buyers: string[]; projects: string[]
  query: string; currency: string; unit: string; minOrders?: number; maxOrders?: number; minQuantity?: number; maxQuantity?: number
  minAmount?: number; maxAmount?: number; minPriceChange?: number; frequentOrders: number; fragmentDays: number; smallOrderAmount: number
  top: number; dimension: string; detailKey: string; page: number
}
export interface PurchaseMetric {
  key: string; code: string; name: string; unit: string; currency: string; orders: number; quantity: number; amount?: number | null
  averagePrice?: number | null; lastPrice?: number | null; minPrice?: number | null; maxPrice?: number | null; priceChange?: number | null
  suppliers: number; projects: number; ordersPerMonth: number; averageInterval?: number | null; averageBatch: number; fragmentOrders: number
  smallOrders?: number | null; reason: string; share?: number | null; cumulativeShare?: number | null; abc?: string
}
export interface PurchaseLine {
  id: string; organization: string; orderId: string; orderNumber: string; lineNumber: number; date: string; typeCode: string; typeName: string
  materialCode: string; materialName: string; specification: string; brand: string; categoryCode: string; categoryName: string
  supplierCode: string; supplierName: string; buyerCode: string; buyerName: string; projectCode: string; subprojectCode: string
  unitCode: string; unitName: string; quantity: number; currency: string; netAmount?: number | null; unitPrice?: number | null
}
export interface PurchaseAnalysis {
  status: { lastAttempt?: string; lastSuccess?: string; from?: string; through?: string; error?: string; sourceCount: number }
  from: string; through: string; options: Record<string, { value: string; label: string }[]>
  frequent: PurchaseMetric[]; volume: PurchaseMetric[]; spend: PurchaseMetric[]; prices: PurchaseMetric[]; fragmented: PurchaseMetric[]; suppliers: PurchaseMetric[]
  trend: { month: string; orders: number; quantity: number; amount?: number | null; averagePrice?: number | null }[]
  details: PurchaseLine[]; detailCount: number; orderCount: number; materialCount: number; supplierCount: number; frequentCount: number
  priceComparisons: PurchaseMetric[]
  amounts: Record<string, number | null>; missingAmounts: number; canCost: boolean; canExport: boolean; canSync: boolean; canConfigure: boolean; notices: string[]
}
export interface PurchaseSettings { revision: number; organizations: string[]; historyMonths: number; intervalMinutes: number; autoSync: boolean }
export function defaultPurchaseFilter(): PurchaseFilter {
  const today = new Date(); const past = new Date(today); past.setFullYear(past.getFullYear() - 1); past.setDate(past.getDate() + 1)
  const date = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
  return { from: date(past), through: date(today), organizations: [], types: [], categories: [], suppliers: [], buyers: [], projects: [], query: '', currency: '', unit: '', minOrders: undefined, maxOrders: undefined, minQuantity: undefined, maxQuantity: undefined, minAmount: undefined, maxAmount: undefined, minPriceChange: undefined, frequentOrders: 6, fragmentDays: 7, smallOrderAmount: 1000, top: 20, dimension: 'material', detailKey: '', page: 1 }
}
