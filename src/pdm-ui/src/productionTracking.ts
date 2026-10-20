export type ProductionRoute = 'Self' | 'External'
export type ProductionMovementKind = 'ProductionReceived' | 'Start' | 'MachiningCompleted' | 'PackingStarted' | 'Packed' | 'OutsideSent' | 'OutsideReturned'
  | 'ExternalDelivered' | 'QualityReceived' | 'Pass' | 'Rework' | 'Scrap' | 'ReworkCompleted' | 'Issue'

export interface ProductionLine {
  id: string
  materialKey: string
  drawingNumber: string
  materialCode?: string
  name: string
  revision: string
  unit: string
  requiredQuantity: number
  priority: string
  requiredOn?: string | null
  material?: string | null
  surfaceTreatment?: string | null
  heatTreatment?: string | null
  sourceDocumentId?: string | null
  sourceVersionId?: string | null
  sourceBomItemIds?: string[]
}
export interface ProductionScheduleSlot { resourceGroupId: string; date: string; hours: number }
export interface ProductionLot {
  id: string
  lineId: string
  route: ProductionRoute
  quantity: number
  supplier?: string | null
  supplierId?: string | null
  assignee?: string | null
  promisedOn?: string | null
  paused: boolean
  frozen: boolean
  freezeReason?: string | null
  frozenByReleasePackageId?: string | null
  schedule: ProductionScheduleSlot[]
}
export interface ProductionMovement {
  id: string
  requestId: string
  lotId: string
  kind: string
  quantity: number
  actor: string
  occurredAt: string
  actorName?: string | null
  actorRoles?: string | null
  bindingId?: string | null
  post?: string | null
  workstationId?: string | null
  workstationName?: string | null
  resourceGroup?: string | null
  fromStage?: string | null
  toStage?: string | null
  resetsFlow?: boolean
  note?: string | null
  recipient?: string | null
}
export interface ProductionReleaseState {
  releasePackageId: string
  projectId: string
  companyId: string
  projectCode: string
  projectName: string
  packageNumber: string
  scope: string
  publishedAt: string
  lines: ProductionLine[]
  lots: ProductionLot[]
  movements: ProductionMovement[]
  receivedBy?: string | null
  receivedAt?: string | null
  formalReleasePackageId?: string | null
}
export interface ProductionReleaseRecord { state: ProductionReleaseState; rowVersion: number }
export interface ProductionWorkstation { id: string; name: string; resourceGroup: string; isActive: boolean }
export interface ProductionPostBinding { id: string; subjectType: 'User' | 'Role'; subject: string; post: string; workstationIds: string[]; isActive: boolean }
export interface ProductionScanCatalog { companyId: string; workstations: ProductionWorkstation[]; bindings: ProductionPostBinding[]; rowVersion: number }
export interface ProductionScanAction { lotId: string; kind: ProductionMovementKind; label: string; remainingQuantity: number; bindingId: string; post: string; workstationId: string; workstationName: string; resourceGroup: string }
export interface ProductionScanContext { actions: ProductionScanAction[]; hint: string }
export const productionPosts: Record<string, string> = { Reception: '生产签收', Machining: '机加', Packing: '包装', QualityReceiving: '质检收料', Inspection: '质量检验', Issue: '领用', SupplyChain: '供应链流转' }
export const productionMovementLabels: Record<string, string> = { ProductionReceived: '生产签收', Start: '机加开工', MachiningCompleted: '机加完工', PackingStarted: '包装接收', Packed: '包装完成／交检', QualityReceived: '质检收料', Pass: '检验合格', Rework: '检验返工', Scrap: '检验报废', ReworkCompleted: '返工完成', Issue: '确认领用', OutsideSent: '中间委外送出', OutsideReturned: '中间委外返回', ExternalDelivered: '外协交货', Assigned: '分配批次', Scheduled: '确认排程', AllocationChanged: '修改分配', PromiseChanged: '调整承诺日期', Cancelled: '取消数量', Transferred: '转新版' }
export function selfProductionState(state: ProductionReleaseState): ProductionReleaseState | undefined {
  const lots = state.lots.filter(lot => lot.route === 'Self')
  if (!lots.length) return undefined
  const quantities = new Map<string, number>()
  for (const lot of lots) quantities.set(lot.lineId, (quantities.get(lot.lineId) ?? 0) + lot.quantity)
  const lotIds = new Set(lots.map(lot => lot.id))
  return { ...state, lots, lines: state.lines.filter(line => quantities.has(line.id))
    .map(line => ({ ...line, requiredQuantity: quantities.get(line.id)! })),
  movements: state.movements.filter(item => lotIds.has(item.lotId)) }
}
export interface UpdateProductionAllocation {
  lotId: string; route: ProductionRoute; quantity: number; supplier?: string; supplierId?: string | null
  assignee?: string; promisedOn?: string | null; reason: string; requestId: string; expectedRowVersion: number
}
export interface BatchProductionAllocation extends Omit<UpdateProductionAllocation, 'lotId' | 'quantity'> { lineIds: string[] }
export function allocationChangeBlockReason(state: ProductionReleaseState, lot: ProductionLot) {
  if (lot.frozen || lot.paused) return '该批次已冻结或暂停，请先处理后再修改分配。'
  return state.movements.some(item => item.lotId === lot.id && !['Assigned', 'Scheduled', 'PromiseChanged', 'AllocationChanged', 'ProductionReceived'].includes(item.kind))
    ? '该批次已有加工、交付、取消或转版记录，不能修改分配。' : ''
}
export type ProductionOpeningStage = 'Unstarted' | 'Started' | 'MachiningCompleted' | 'Packed'
  | 'ExternalDelivered' | 'QualityReceived' | 'Passed' | 'Issued'
export interface ProductionOpeningLot {
  materialKey: string
  route: ProductionRoute
  quantity: number
  stage: ProductionOpeningStage
  supplier?: string
  assignee?: string
  promisedOn?: string | null
  recipient?: string
}
export interface ProductionReleaseSummary {
  releasePackageId: string
  projectId: string
  projectCode: string
  projectName: string
  packageNumber: string
  scope: string
  publishedAt: string
  status: string
  totalLines: number
  totalQuantity: number
  qualifiedLines: number
  issuedLines: number
  urgentLines: number
  priorityLines: number
  firstRequiredOn?: string | null
  forecastOn?: string | null
  atRiskLines: number
  unplannedLines: number
}
export interface ProductionProject { id: string; code: string; name: string; packages: ProductionReleaseSummary[] }
export interface ProductionCapacityDay { resourceGroupId: string; date: string; availableHours: number }
export interface ProductionCapacityUsage extends ProductionCapacityDay { committedHours: number }
export interface ProductionCapacityView {
  calendar: { companyId: string; rowVersion: number; days: ProductionCapacityDay[] }
  usage: ProductionCapacityUsage[]
}
export interface ProductionScanResult {
  releasePackageId: string
  lineId: string
  lotId?: string | null
  packageNumber: string
  drawingNumber: string
  name: string
  revision: string
  status: string
}

/** 可在内网 HTTP 页面使用；此处不能依赖仅安全上下文可用的 randomUUID。 */
export function productionRequestId() {
  const bytes = new Uint8Array(16)
  if (globalThis.crypto?.getRandomValues) globalThis.crypto.getRandomValues(bytes)
  else for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256)
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export function movementTotal(state: ProductionReleaseState, lotId: string, kind: string) {
  return activeProductionMovements(state, lotId).filter(item => item.kind === kind)
    .reduce((total, item) => total + item.quantity, 0)
}
function activeProductionMovements(state: ProductionReleaseState, lotId: string) {
  let reset = -1
  for (let index = state.movements.length - 1; index >= 0; index--)
    if (state.movements[index]!.lotId === lotId && state.movements[index]!.resetsFlow) { reset = index; break }
  return state.movements.slice(reset + 1).filter(item => item.lotId === lotId)
}
export function lotStage(state: ProductionReleaseState, lot: ProductionLot) {
  const sum = (kind: string) => movementTotal(state, lot.id, kind)
  const demand = lot.quantity - sum('Cancelled') - sum('Transferred')
  if (demand <= 0) return '已取消／转版'
  if (lot.frozen) return '变更待处理'
  if (lot.paused) return '已暂停'
  if (sum('Scrap') >= demand) return '已报废'
  if (sum('Issue') + sum('Scrap') >= demand) return sum('Scrap') > 0 ? '已领用／含报废' : '已领用'
  if (sum('Pass') + sum('Scrap') >= demand) return sum('Scrap') > 0 ? '质检完成／含报废' : '质检完成／合格待领'
  if (sum('Rework') > sum('ReworkCompleted')) return '返工中'
  if (sum('QualityReceived') > 0) return '质检中'
  if (sum('Packed') > 0 || sum('ExternalDelivered') > 0) return '待质检收料'
  if (sum('PackingStarted') > 0) return '包装中'
  if (lot.route === 'External') return '外协待交货'
  const groups = [...new Set(lot.schedule.map(slot => slot.resourceGroupId))]
  const groupSum = (kind: string, group: string) => activeProductionMovements(state, lot.id)
    .filter(item => item.kind === kind && (!item.resourceGroup || item.resourceGroup.toLocaleLowerCase() === group.toLocaleLowerCase())).reduce((total, item) => total + item.quantity, 0)
  if (Math.min(...(groups.length ? groups : ['']).map(group => groupSum('MachiningCompleted', group))) >= demand) return '待包装'
  if (sum('Start') > 0) return '机加中'
  if (lot.schedule.length) return '已排产／待开工'
  return sum('ProductionReceived') > 0 ? '生产已签收／待排产' : '待生产签收'
}
export function lineTotal(state: ProductionReleaseState, lineId: string, kind: string) {
  return state.lots.filter(lot => lot.lineId === lineId)
    .reduce((total, lot) => total + movementTotal(state, lot.id, kind), 0)
}

export function unassignedQuantity(state: ProductionReleaseState, line: ProductionLine) {
  return Math.max(0, line.requiredQuantity + state.lots.filter(lot => lot.lineId === line.id)
    .reduce((total, lot) => total + movementTotal(state, lot.id, 'Scrap')
      - movementTotal(state, lot.id, 'Cancelled') - movementTotal(state, lot.id, 'Transferred')
      - lot.quantity, 0))
}
export function lineState(state: ProductionReleaseState, line: ProductionLine) {
  if (lineTotal(state, line.id, 'Issue') >= line.requiredQuantity) return '已领用'
  if (lineTotal(state, line.id, 'Issue') + lineTotal(state, line.id, 'Transferred')
    + lineTotal(state, line.id, 'Cancelled') >= line.requiredQuantity)
    return lineTotal(state, line.id, 'Transferred') > 0 ? '已转新版'
      : lineTotal(state, line.id, 'Issue') > 0 ? '变更后已领用' : '已取消剩余需求'
  if (lineTotal(state, line.id, 'Pass') >= line.requiredQuantity) return '质检完成／合格待领'
  const lots = state.lots.filter(lot => lot.lineId === line.id)
  if (lots.some(lot => lot.frozen)) return '变更待处理'
  const stages = [...new Set(lots.map(lot => lotStage(state, lot)))]
  return stages.length > 1 ? '分批流转' : stages[0] || '待分配'
}

export function productionPriority(value: string) {
  return value === 'Urgent' ? '紧急' : value === 'Priority' ? '加急' : '普通'
}
