import type { BomItem, BomVersion, ReleasePackageSummary } from './types'

export interface BomRemovalHistoryEntry {
  key: string
  item: BomItem
  packageNumber?: string
  releasedAt: string
  baselineLabel: string
}

interface FrozenRelease {
  id: string
  label: string
  time: string
  releasedAt: string
  createdAt: string
  items: BomItem[]
  multiplier: number
  versionNumber: number
  package?: ReleasePackageSummary
}

const ids = (item: BomItem) => [item.id, item.releaseTrackingId, ...item.summaryItemIds ?? [], ...item.consolidatedSourceIds ?? []]
  .filter((id): id is string => !!id).map(id => id.toLowerCase())
const active = (item: BomItem) => !item.manuallyExcluded && !item.pendingClassification && !item.releaseExcluded && !item.pendingRemoval
const text = (value?: string) => value?.trim().toUpperCase() ?? ''
const context = (item: BomItem) => JSON.stringify([item.engineeringKitReferenceId, item.relationMainBomItemId, item.relationOptionId, item.sourceConfiguration])
const groupKey = (item: BomItem) => JSON.stringify([text(item.drawingNumber) || item.sourceDocumentId || item.id, text(item.unit), context(item), item.specification, item.brand])
const compare = (a: FrozenRelease, b: FrozenRelease) => (Date.parse(a.time) || 0) - (Date.parse(b.time) || 0)
  || (Date.parse(a.createdAt) || 0) - (Date.parse(b.createdAt) || 0) || a.versionNumber - b.versionNumber || a.id.localeCompare(b.id)

/** 只读发布历史；不以当前工作区、回收站或补充发布未勾选项推断历史删除。 */
export function bomRemovalHistory(kind: BomVersion['kind'], versions: BomVersion[], packages: ReleasePackageSummary[]): BomRemovalHistoryEntry[] {
  const scopes = kind === 'Standard' ? ['StandardFormal', 'StandardSupplement', 'LegacyCombined']
    : kind === 'NonStandard' ? ['NonStandardWithDrawing', 'NonStandardSupplement', 'LegacyCombined']
      : ['ElectricalFormal', 'ElectricalSupplement', 'LegacyCombined']
  const covered = new Set<string>()
  const events: FrozenRelease[] = packages.filter(release => release.state === '已发布' && scopes.includes(release.scope)).map(release => {
    const versionId = kind === 'Standard' ? release.standardBomVersionId : kind === 'NonStandard' ? release.nonStandardBomVersionId : release.electricalBomVersionId
    if (versionId) covered.add(versionId)
    const version = versions.find(row => row.id === versionId && row.kind === kind)
    return { id: release.number, label: version?.label ?? (kind === 'Standard' ? release.standardBomRevision : kind === 'NonStandard' ? release.nonStandardBomRevision : release.electricalBomRevision) ?? release.number,
      time: release.publishedAt || release.createdAt || '', releasedAt: release.publishedAt || '', createdAt: release.createdAt || '', versionNumber: version?.versionNumber ?? 0,
      items: kind === 'Standard' ? release.standardBomSnapshot : kind === 'NonStandard' ? release.nonStandardBomSnapshot : release.electricalBomSnapshot,
      multiplier: release.wholeSetMultiplier ?? version?.wholeSetMultiplier ?? 1, package: release }
  })
  events.push(...versions.filter(version => version.kind === kind && version.state === 'Released' && !covered.has(version.id))
    .map(version => ({ id: version.id, label: version.label, time: version.releasedAt || version.createdAt, releasedAt: version.releasedAt || '', createdAt: version.createdAt,
      versionNumber: version.versionNumber, items: version.items, multiplier: version.wholeSetMultiplier ?? 1 })))
  events.sort(compare)
  const history: BomRemovalHistoryEntry[] = []
  events.forEach((event, index) => {
    const previous = events[index - 1]
    if (!previous) return
    const confirmed = new Set(event.package?.confirmedRemovedBomItemIds?.map(id => id.toLowerCase()) ?? [])
    const current = event.items.filter(active)
    const removed = previous.items.filter(active).filter(item => {
      if (event.package && event.package.scope !== 'LegacyCombined') return ids(item).some(id => confirmed.has(id))
      // 旧版全量发布未保存确认删除ID时，仅比较相邻的冻结全量快照。
      return ids(item).some(id => confirmed.has(id)) || !current.some(row => ids(row).some(id => ids(item).includes(id))
        || context(row) === context(item) && (row.sourceDocumentId && row.sourceDocumentId === item.sourceDocumentId
          || text(row.drawingNumber) && text(row.drawingNumber) === text(item.drawingNumber) && text(row.unit) === text(item.unit)))
    })
    const groups = new Map<string, BomItem>()
    for (const item of removed) {
      const key = groupKey(item)
      const quantity = item.quantity * previous.multiplier
      const old = groups.get(key)
      groups.set(key, { ...item, quantity: Number(((old?.quantity ?? 0) + quantity).toFixed(4)) })
    }
    groups.forEach((item, key) => history.push({ key: `${event.package?.id ?? event.id}:${key}`, item,
      packageNumber: event.package?.number, releasedAt: event.releasedAt, baselineLabel: previous.label.replace(/-B(?=\d+$)/, '-V') }))
  })
  return history.reverse()
}
