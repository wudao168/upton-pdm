import type { MaterialAttachment, PdmMaterial, StandardLibraryCategory } from './types'
import type { MaterialStockPlan } from './materialStockPlans'

export interface StockedMaterialSnapshot {
  materialId: string
  plan: (MaterialStockPlan & { unitCode?: string | null }) | null
  stockQuantity: number | null
  inventorySyncedAt: string | null
  isLowStock: boolean
  lastError: string | null
  isFormal?: boolean
  state?: 'Draft' | 'Formal'
}
export interface StockedMaterialRow {
  material: PdmMaterial
  categories: StandardLibraryCategory[]
  snapshot: StockedMaterialSnapshot
  coverImage: MaterialAttachment | null
  managementOnly?: boolean
}
export interface StockedMaterialScope { companyId: string; enterpriseCode: string; organizationCode: string }
export interface StockedMaterialPage {
  items: StockedMaterialRow[]; total: number; lowStockCount: number; page: number; pageSize: number
  scope: StockedMaterialScope; lastSuccessAt: string | null; lastError: string | null
  canSynchronize: boolean; canManageSettings: boolean
  formalCount?: number; draftCount?: number; managementOnlyCount?: number
}
export interface StockedMaterialSettings {
  syncIntervalMinutes: number; notifyLowStock: boolean; useStandardAssistant: boolean; recipients: string[]
}
export interface StockedMaterialSettingsView {
  settings: StockedMaterialSettings; rowVersion: number; scope: StockedMaterialScope
  users: Array<{ username: string; displayName: string; isDefault: boolean }>
  effectiveRecipients: string[]; lastSuccessAt: string | null; lastError: string | null
}
export const stockTime = (value: string | null | undefined) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
export const stockSyncLabel = (row: StockedMaterialRow) => row.snapshot.lastError ? '同步异常' : !row.snapshot.plan ? '待回读' : !row.snapshot.plan.isEffective ? 'U9C已失效' : '已同步'
export const formalStockedMaterial = (row: StockedMaterialRow) => row.snapshot.isFormal ?? (!!row.snapshot.plan?.isEffective && row.snapshot.plan.safetyStockQuantity > 0)
