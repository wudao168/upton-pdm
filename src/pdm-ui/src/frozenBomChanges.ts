import type { BomItem, ReleasePackageSummary } from './types'
import { releaseMaterialKey } from './releaseEarlyCredits'

export const releaseQuantity = (value: number) => Number(value.toFixed(8))
export const releaseInstanceIds = (item: BomItem): string[] => item.summaryItemIds?.length
  ? item.summaryItemIds : item.id ? [item.id] : []

export const itemKey = (item: BomItem) => `${(item.drawingNumber || `${item.name}|${item.specification}`).trim().toLocaleLowerCase()}|${item.unit?.trim().toLocaleLowerCase() ?? ''}`
const comparedFields: Array<{ key: keyof BomItem; label: string }> = [
  { key: 'kind', label: '分类' }, { key: 'unit', label: '单位' }, { key: 'drawingNumber', label: '物料编码' },
  { key: 'name', label: '名称' }, { key: 'specification', label: '型号' }, { key: 'remark', label: '备注' },
  { key: 'brand', label: '品牌' }, { key: 'material', label: '材质' }, { key: 'surfaceTreatment', label: '表面处理' },
  { key: 'heatTreatment', label: '热处理' }, { key: 'weight', label: '重量' }, { key: 'quantity', label: '数量' }, { key: 'revision', label: '版本' },
]
const fieldValue = (item: BomItem, key: keyof BomItem) => String(item[key] ?? '').trim()
const itemSignature = (item: BomItem) => JSON.stringify(comparedFields.map(field => fieldValue(item, field.key)))
function isFormalRevisionPromotion(previous: string, current: string) {
  const work = /^(?:([A-Z]+)-)?W1$/i.exec(previous)
  return Boolean(work && current.toUpperCase() === (work[1]?.toUpperCase() ?? 'A'))
}
export function changedRows(current: BomItem[], previous: BomItem[], multiplier = 1, previousMultiplier = 1) {
  const remaining = new Set(previous)
  const matches = new Map<BomItem, BomItem>()
  const sameContext = (item: BomItem, old: BomItem) => (item.engineeringKitReferenceId ?? '') === (old.engineeringKitReferenceId ?? '')
  // Reserve stable identities first, so repeated material codes cannot consume another instance's baseline.
  for (const item of current) {
    const match = [...remaining].find(old => Boolean(item.id) && old.id === item.id)
    if (match) { matches.set(item, match); remaining.delete(match) }
  }
  for (const item of current.filter(row => !matches.has(row))) {
    const match = [...remaining].find(old => Boolean(item.releaseTrackingId) && old.releaseTrackingId === item.releaseTrackingId)
    if (match) { matches.set(item, match); remaining.delete(match) }
  }
  // 汇总代表实例或料号可能在重新提取后改变，来源模型和配置仍能识别同一零件。
  for (const item of current.filter(row => !matches.has(row) && row.summaryItemIds?.length)) {
    const candidates = [...remaining].filter(old => sameContext(item, old) && Boolean(item.sourceDocumentId) && old.sourceDocumentId === item.sourceDocumentId
      && old.sourceConfiguration === item.sourceConfiguration && old.unit === item.unit)
    if (candidates.length === 1 && current.filter(row => !matches.has(row) && row.sourceDocumentId === item.sourceDocumentId
      && row.sourceConfiguration === item.sourceConfiguration && row.unit === item.unit).length === 1) {
      matches.set(item, candidates[0]!); remaining.delete(candidates[0]!)
    }
  }
  for (const item of current.filter(row => !matches.has(row))) {
    const match = [...remaining].find(old => sameContext(item, old) && Boolean(item.sourceDocumentId && item.sourceInstancePath)
      && old.sourceDocumentId === item.sourceDocumentId && old.sourceInstancePath === item.sourceInstancePath
      && old.sourceConfiguration === item.sourceConfiguration)
      ?? [...remaining].find(old => sameContext(item, old) && itemSignature(old) === itemSignature(item))
    if (match) { matches.set(item, match); remaining.delete(match) }
  }
  for (const item of current.filter(row => !matches.has(row))) {
    const candidates = [...remaining].filter(old => sameContext(item, old) && itemKey(old) === itemKey(item))
    if (candidates.length === 1 && current.filter(row => !matches.has(row) && sameContext(item, row) && itemKey(row) === itemKey(item)).length === 1) {
      matches.set(item, candidates[0]!); remaining.delete(candidates[0]!)
    }
  }
  const rows: Array<{ item: BomItem; previousItem?: BomItem; change: string; details: string[] }> = []
  for (const item of current) {
    const old = matches.get(item)
    if (!old) { rows.push({ item, change: '新增', details: ['上一发布版不存在此项'] }); continue }
    const value = (row: BomItem, key: keyof BomItem, factor: number) => key === 'quantity' ? String(releaseQuantity(Number(row.quantity) * factor)) : fieldValue(row, key)
    const details = comparedFields.filter(field => value(old, field.key, previousMultiplier) !== value(item, field.key, multiplier)
      && !(field.key === 'revision' && isFormalRevisionPromotion(fieldValue(old, field.key), fieldValue(item, field.key))))
      .map(field => `${field.label}：${value(old, field.key, previousMultiplier) || '—'} → ${value(item, field.key, multiplier) || '—'}`)
    if (multiplier !== previousMultiplier) details.unshift(`整套倍率：×${previousMultiplier} → ×${multiplier}`)
    if (details.length) rows.push({ item, previousItem: old, change: '修改', details })
  }
  rows.push(...[...remaining].map(item => ({ item, previousItem: item, change: '删除', details: ['当前发布范围已移除此项'] })))
  return rows
}
export const frozenMaterialKey = (item: BomItem) => {
  const materialCode = item.drawingNumber?.trim().toLocaleLowerCase()
  return materialCode ? `material:${releaseMaterialKey(item).toLocaleLowerCase()}` : `item:${item.id?.toLocaleLowerCase() ?? ''}`
}
export const frozenSummaryKey = (item: BomItem) => {
  if (item.drawingNumber?.trim() || !item.sourceDocumentId) return frozenMaterialKey(item)
  return ['document', item.sourceDocumentId, item.sourceConfiguration, item.kind, item.unit,
    item.name, item.specification, item.brand, item.material, item.surfaceTreatment,
    item.weight, item.remark, item.revision]
    .map(value => String(value ?? '').trim().toLocaleLowerCase()).join('|')
}

function summarizeFrozenItems(items: BomItem[]) {
  const groups = new Map<string, BomItem>()
  for (const item of items) {
    const key = frozenSummaryKey(item)
    const existing = groups.get(key)
    if (existing) {
      existing.quantity = Number(existing.quantity) + Number(item.quantity)
      existing.summaryItemIds!.push(...releaseInstanceIds(item))
    } else groups.set(key, { ...item, summaryItemIds: [...releaseInstanceIds(item)] })
  }
  return [...groups.values()].map(item => ({ ...item, engineeringKitReferenceId: undefined }))
}
function frozenScopedChanges(rows: ReturnType<typeof changedRows>, items: BomItem[], previous: BomItem[], release: ReleasePackageSummary) {
  const selected = new Set(release.selectedBomItemIds ?? [])
  const removed = new Set(release.confirmedRemovedBomItemIds ?? [])
  const result = rows.filter(row => !row.item.releaseConfirmation && (!selected.size && !removed.size
    || releaseInstanceIds(row.item).some(id => selected.has(id))
    || releaseInstanceIds(row.previousItem ?? row.item).some(id => removed.has(id))))
  if (release.scope === 'NonStandardSupplement') {
    for (const item of items) {
      if (item.releaseConfirmation || !releaseInstanceIds(item).some(id => selected.has(id))
        || result.some(row => row.item === item)) continue
      result.push({ item, previousItem: previous.find(old => frozenSummaryKey(old) === frozenSummaryKey(item)),
        change: '修改', details: ['图纸纳入本次发布（以冻结版本为准）'] })
    }
  }
  return result
}

export function frozenBomChanges(release: ReleasePackageSummary, current: BomItem[], baseline: BomItem[], previousMultiplier: number) {
  const multiplier = release.wholeSetMultiplier ?? 1
  const previousItems = summarizeFrozenItems(baseline)
  const previous = new Map(previousItems
    .map(item => [frozenSummaryKey(item), item]))
  const items = summarizeFrozenItems(current)
  const rows = items.flatMap(item => {
    const key = frozenSummaryKey(item)
    const old = previous.get(key)
    previous.delete(key)
    return changedRows([item], old ? [old] : [], multiplier, previousMultiplier)
  })
  return frozenScopedChanges([...rows, ...changedRows([], [...previous.values()], multiplier, previousMultiplier)], items, previousItems, release)
    .map(row => ({...row,
      item: {...row.item,quantity:releaseQuantity(Number(row.item.quantity) * (row.change === '删除' ? previousMultiplier : multiplier))},
      previousItem: row.previousItem ? {...row.previousItem,quantity:releaseQuantity(Number(row.previousItem.quantity) * previousMultiplier)} : undefined,
    }))
}

export function frozenNetChange(row: ReturnType<typeof frozenBomChanges>[number], earlyCredit: number) {
  return releaseQuantity((row.change === '删除' ? 0 : row.item.quantity) - (row.previousItem?.quantity ?? 0) - earlyCredit)
}

export function previousReleaseBaseline(kind: string, selected: ReleasePackageSummary | null, history: ReleasePackageSummary[]) {
  const scopes = kind === 'Standard' ? ['StandardFormal','StandardSupplement']
    : kind === 'NonStandard' ? ['NonStandardWithDrawing','NonStandardSupplement'] : ['ElectricalFormal','ElectricalSupplement']
  const time = selected?.publishedAt
  return history.filter(item => item.state === '已发布' && scopes.includes(item.scope) && item.id !== selected?.id
    && (!selected?.rootDocumentId || !item.rootDocumentId || item.rootDocumentId === selected.rootDocumentId)
    && (!time || Date.parse(item.publishedAt || item.createdAt || '') < Date.parse(time)
      || Date.parse(item.publishedAt || item.createdAt || '') === Date.parse(time)
        && (!selected?.publishedAt || Date.parse(item.createdAt || '') < Date.parse(selected.createdAt || '')
          || item.createdAt === selected.createdAt && item.number.localeCompare(selected.number) < 0)))
    .sort((left,right) => Date.parse(right.publishedAt || '') - Date.parse(left.publishedAt || '')
      || Date.parse(right.createdAt || '') - Date.parse(left.createdAt || '') || right.number.localeCompare(left.number))[0]
}
