import type { BomItem, BomVersion, ReleasePackageSummary } from './types'
import { bomRevisionLabel, packageRevisionLabel } from './drawingRevision'

export interface BomComparisonSnapshot {
  id: string
  label: string
  packageNumber?: string
  releasedAt: string
  versionNumber: number
  items: BomItem[]
}

export function releaseComparisonSnapshots(kind: BomVersion['kind'], versions: BomVersion[], packages: ReleasePackageSummary[]): BomComparisonSnapshot[] {
  const scopes = kind === 'Standard' ? ['StandardLongLead', 'StandardFormal', 'StandardSupplement']
    : kind === 'NonStandard' ? ['NonStandardLongLead', 'NonStandardWithDrawing', 'NonStandardSupplement']
      : ['ElectricalLongLead', 'ElectricalFormal', 'ElectricalSupplement']
  const published = packages.filter(item => item.state === '已发布' && scopes.includes(item.scope))
  const coveredVersions = new Set<string>()
  const snapshots = published.map(item => {
    const versionId = kind === 'Standard' ? item.standardBomVersionId : kind === 'NonStandard' ? item.nonStandardBomVersionId : item.electricalBomVersionId
    if (versionId) coveredVersions.add(versionId)
    const version = versions.find(row => row.id === versionId && row.kind === kind)
    return {
      id: `package:${item.id}`, label: version ? version.label.replace(/-B(?=\d+$)/, '-V') : item.number, packageNumber: item.number,
      releasedAt: item.publishedAt || item.createdAt || '', versionNumber: version?.versionNumber ?? 0,
      items: (kind === 'Standard' ? item.standardBomSnapshot : kind === 'NonStandard' ? item.nonStandardBomSnapshot : item.electricalBomSnapshot)
        .map(row => ({ ...row, displayRevision: packageRevisionLabel(row, item) })),
    }
  })
  // 历史正式版本可能没有关联发布包记录，仍使用其冻结版本，绝不回退到当前工作区。
  return [...snapshots, ...versions.filter(item => item.kind === kind && item.state === 'Released' && !coveredVersions.has(item.id))
    .map(item => ({ id: `version:${item.id}`, label: item.label.replace(/-B(?=\d+$)/, '-V'), releasedAt: item.releasedAt || item.createdAt, versionNumber: item.versionNumber, items: item.items }))]
    .sort(compareSnapshotTime)
}

export function compareSnapshotTime(a: BomComparisonSnapshot, b: BomComparisonSnapshot) {
  const time = (Date.parse(a.releasedAt) || 0) - (Date.parse(b.releasedAt) || 0)
  return time || a.versionNumber - b.versionNumber || a.id.localeCompare(b.id)
}

export type BomSnapshotDifference = 'base' | 'same' | 'added' | 'removed' | 'missing' | 'changed'
export interface BomSnapshotCell {
  item?: BomItem
  status: BomSnapshotDifference
  label: string
  changes: string[]
}
export interface BomSnapshotComparisonRow { key: string; code: string; cells: BomSnapshotCell[] }

const fields: Array<[keyof BomItem, string]> = [
  ['name', '名称'], ['specification', '型号'], ['revision', '版次'], ['quantity', '数量'], ['unit', '单位'],
  ['brand', '品牌'], ['material', '材质'], ['surfaceTreatment', '表面处理'], ['heatTreatment', '热处理'],
  ['weight', '重量'], ['remark', '备注'], ['isWearPart', '易损件'], ['impactStage', '关键阶段'],
  ['engineeringKitCode', '套件料号'], ['engineeringKitVersionNumber', '套件版本'],
]
const normalized = (value: unknown) => value == null || value === false || String(value).trim() === '' ? ''
  : typeof value === 'number' ? String(Number(value.toFixed(4))) : String(value).trim()
const display = (field: keyof BomItem, value: unknown) => field === 'isWearPart' ? value ? '是' : '否'
  : field === 'impactStage' ? value === 'Assembly' ? '装配' : value === 'Commissioning' ? '调试' : '—'
    : normalized(value) || '—'

function snapshotRows(snapshot: BomComparisonSnapshot) {
  const result = new Map<string, BomItem>()
  snapshot.items.filter(item => !item.manuallyExcluded && !item.pendingClassification && !item.releaseExcluded).forEach((item, index) => {
    const code = item.drawingNumber.trim().toLocaleUpperCase()
    const key = code ? `code:${code}` : item.sourceDocumentId ? `document:${item.sourceDocumentId}:${item.sourceConfiguration || ''}`
      : item.id ? `id:${item.id}` : `manual:${item.name}:${item.specification || ''}:${item.brand || ''}:${index}`
    const existing = result.get(key)
    result.set(key, existing ? { ...existing, quantity: Number((existing.quantity + item.quantity).toFixed(4)) } : { ...item })
  })
  return result
}

export function compareReleaseSnapshots(snapshots: BomComparisonSnapshot[]): BomSnapshotComparisonRow[] {
  const indexed = snapshots.map(snapshotRows)
  const keys = [...new Set(indexed.flatMap(items => [...items.keys()]))]
  return keys.map(key => {
    const base = indexed[0]?.get(key)
    const sample = indexed.map(items => items.get(key)).find(Boolean)!
    return {
      key, code: sample.drawingNumber || '未编码', cells: indexed.map((items, index): BomSnapshotCell => {
        const item = items.get(key)
        if (!item) return { status: base && index > 0 ? 'removed' : 'missing', label: base && index > 0 ? '已删除' : '未包含', changes: [] }
        if (index === 0) return { item, status: 'base', label: '基准', changes: [] }
        if (!base) return { item, status: 'added', label: '新增', changes: [] }
        const changes = fields.filter(([field]) => field === 'revision' ? bomRevisionLabel(base) !== bomRevisionLabel(item) : normalized(base[field]) !== normalized(item[field]))
          .map(([field, label]) => `${label} ${field === 'revision' ? bomRevisionLabel(base) : display(field, base[field])} → ${field === 'revision' ? bomRevisionLabel(item) : display(field, item[field])}`)
        return { item, status: changes.length ? 'changed' : 'same', label: changes.length === 1 ? changes[0]! : changes.length ? `修改（${changes.length}项）` : '一致', changes }
      }),
    }
  }).sort((a, b) => a.code.localeCompare(b.code, 'zh-CN', { numeric: true }) || a.key.localeCompare(b.key))
}
