import type { BomItem, ReleasePackageSummary } from './types'

const unitAliases: Record<string, string> = { EA: '001', 件: '001', 个: '001', 台: '002', 盒: '004', 卷: '005', 捆: '006', 双: '007', 片: '008', 桶: '009', 支: '010', 组: '011', 套: '011', 箱: '012', 包: '013' }
export const releaseUnitCode = (unit: string) => unitAliases[unit.trim().toUpperCase()] ?? unit.trim().toUpperCase()
export const releaseMaterialKey = (item: BomItem) => `${item.drawingNumber.trim().toUpperCase()}|${releaseUnitCode(item.unit)}`

export function releaseEarlyCredits(release: ReleasePackageSummary, history: ReleasePackageSummary[]) {
  const kind = release.scope.startsWith('NonStandard') ? 'NonStandard' : release.scope.startsWith('Electrical') ? 'Electrical' : 'Standard'
  const itemsFor = (item: ReleasePackageSummary) => (kind === 'NonStandard' ? item.nonStandardBomSnapshot : kind === 'Electrical' ? item.electricalBomSnapshot : item.standardBomSnapshot)
    .filter(row => !row.manuallyExcluded && !row.releaseExcluded && !row.pendingRemoval)
  const current = new Map(itemsFor(release).filter(item => item.releaseTrackingId).map(item => [item.releaseTrackingId!, item]))
  const keyFor = (item: BomItem) => {
    const mapped = item.releaseTrackingId ? current.get(item.releaseTrackingId) : undefined
    return releaseMaterialKey(mapped && releaseUnitCode(mapped.unit) === releaseUnitCode(item.unit) ? mapped : item)
  }
  const precedes = (item: ReleasePackageSummary) => !release.publishedAt || Date.parse(item.publishedAt!) < Date.parse(release.publishedAt)
    || Date.parse(item.publishedAt!) === Date.parse(release.publishedAt) && (Date.parse(item.createdAt || '') < Date.parse(release.createdAt || '')
      || item.createdAt === release.createdAt && item.number.localeCompare(release.number) < 0)
  const previous = history.filter(item => item.id !== release.id && item.state === '已发布' && item.publishedAt && precedes(item)
    && (!release.rootDocumentId || !item.rootDocumentId || item.rootDocumentId === release.rootDocumentId))
  const supplement = release.scope.endsWith('Supplement')
  const formalScope = kind === 'NonStandard' ? 'NonStandardWithDrawing' : `${kind}Formal`
  const baseline = previous.filter(item => [formalScope, `${kind}Supplement`, 'LegacyCombined'].includes(item.scope))
  const covered = new Set(supplement ? baseline.flatMap(item => itemsFor(item).map(keyFor)) : [])
  const credits = new Map<string, number>()
  if (release.scope !== formalScope && !(supplement && baseline.length)) return credits
  for (const item of previous.filter(item => item.scope === `${kind}LongLead`)) {
    for (const row of itemsFor(item)) {
      const key = keyFor(row)
      if (!covered.has(key)) credits.set(key, Number(((credits.get(key) ?? 0) + Number(row.quantity) * (item.wholeSetMultiplier ?? 1)).toFixed(8)))
    }
  }
  return credits
}
