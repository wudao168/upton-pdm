import type { ProjectProcurementTrackingItem } from './types'

export const isManufacturing = (row: ProjectProcurementTrackingItem) => !!row.manufacturing || /非标件|NonStandard/.test(row.bomKind)
export function manufacturingValue(row: ProjectProcurementTrackingItem, key: string): unknown {
  const m = row.manufacturing
  switch (key) {
    case 'dataSource': return isManufacturing(row) ? '制造中心' : 'U9C'
    case 'revision': return m?.revision
    case 'supplier': return m?.suppliers
    case 'productionReceivedAt': return m?.receivedAt
  }
  if (!isManufacturing(row)) return row[key as keyof ProjectProcurementTrackingItem]
  switch (key) {
    case 'purchaseRequisitionNumbers': return row.releasePackageNumber?.split('、') ?? []
    case 'purchaseRequisitionStatus': return m?.receivedAt ? '已接收' : '待生产接收'
    case 'purchaseRequisitionCreatedAt': return m?.publishedAt
    case 'purchaseRequisitionDeliveryDate': return m?.requiredOn
    case 'purchaseOrderNumbers': return m?.batches.map(b => `${b.route === 'Self' ? '自制' : '外协'} · ${b.id.slice(0,8)}`) ?? []
    case 'purchaseOrderStatus': return m?.status ?? '待生产接收'
    case 'buyerName': return m?.responsible
    case 'purchaseQuantity': return m?.assignedQuantity ?? 0
    case 'arrivedQuantity': return m?.submittedQuantity ?? 0
    case 'purchaseRemark': return m?.latestNote
    case 'purchaseDeliveryDate': case 'latestDeliveryDate': return m?.forecastOn
    case 'requestedQuantity': case 'approvedQuantity': return row.quantity
    case 'inventoryQuantity': return Math.max(0, (m?.qualifiedQuantity ?? 0) - (m?.issuedQuantity ?? 0))
    case 'receiptQuantity': return m?.qualifiedQuantity ?? 0
    case 'receiptDate': return m?.qualifiedAt
    case 'issueQuantity': return m?.issuedQuantity ?? 0
    case 'issueDate': return m?.issuedAt
    default: return row[key as keyof ProjectProcurementTrackingItem]
  }
}

export function manufacturingStats(items: ProjectProcurementTrackingItem[], today: string) {
  const rows = items.filter(r => isManufacturing(r) && (r.quantity ?? 0) > 0)
  const complete = (r: ProjectProcurementTrackingItem) => (r.manufacturing?.qualifiedQuantity ?? 0) >= (r.quantity ?? 0)
  const open = rows.filter(r => !complete(r))
  const pendingDates = (r: ProjectProcurementTrackingItem) => r.manufacturing?.batches.filter(b=>b.quantity>b.qualifiedQuantity&&b.promisedOn).map(b=>b.promisedOn!) ?? []
  const late = (r: ProjectProcurementTrackingItem, target?: string | null) => !!target && [r.manufacturing?.forecastOn,...pendingDates(r)].some(date=>!!date&&date.slice(0,10)>target.slice(0,10))
  const critical = rows.filter(r => r.impactStage === 'Assembly' || r.impactStage === 'Commissioning')
  return {
    total:rows.length, complete:rows.filter(complete).length,
    overdue:open.filter(r => [r.manufacturing?.forecastOn,...pendingDates(r)].some(date=>!!date&&date.slice(0,10)<today)).length,
    mismatch:open.filter(r => late(r,r.manufacturing?.requiredOn)).length,
    shortage:open.filter(r => late(r,r.impactStage === 'Assembly' ? r.assemblyStartDate : r.impactStage === 'Commissioning' ? r.commissioningStartDate : null)).length,
    critical:critical.length,criticalComplete:critical.filter(complete).length,
    unreceived:rows.filter(r => !r.manufacturing?.receivedAt).length,
    unassigned:rows.filter(r => (r.manufacturing?.assignedQuantity ?? 0) < (r.quantity ?? 0)).length,
  }
}
