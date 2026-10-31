import type { BomItem, ReleasePackageSummary } from './types'

export function drawingRevisionLabel(revision?: string | null): string {
  const original = revision?.trim() || ''
  if (!original) return '—'
  const dirty = original.endsWith('*') ? '*' : ''
  const value = original.replace(/\*+$/, '').toUpperCase()
  const work = /^(?:([A-Z])-)?W[1-9]\d*$/.exec(value)
  if (!work) return /^[A-Z]$/.test(value) ? value + dirty : original
  const previous = work[1]
  if (previous === 'Z') return `版次已达 Z（待处理）${dirty}`
  const next = previous ? String.fromCharCode(previous.charCodeAt(0) + 1) : 'A'
  return `${next}-D${dirty}`
}

export function drawingRevisionHint(revision: string, label = drawingRevisionLabel(revision)): string {
  if (/^[A-Z]-D\*?$/.test(label)) {
    const previous = /^([A-Z])-W[1-9]\d*\*?$/i.exec(revision.trim())?.[1]?.toUpperCase()
    return previous ? `修订中；当前生产使用 ${previous} 版` : '设计中；尚未正式发布'
  }
  return /^[A-Z]\*?$/.test(label) ? '已发布' : label
}

type DrawingVersionInfo = { drawingVersion?: string; drawingVersionNumber?: number | null; publishedVersion?: string; version?: string }
export function documentDrawingVersion(value: DrawingVersionInfo): string {
  if (value.drawingVersionNumber && value.drawingVersionNumber > 0) return `V${value.drawingVersionNumber}`
  return value.drawingVersion || (value.version === '—' ? '未存档' : '待核对')
}
export function documentPublishedVersion(value: DrawingVersionInfo): string {
  return value.publishedVersion || '未发布'
}
export function documentVersionSummary(value: DrawingVersionInfo): string {
  return `图纸版本 ${documentDrawingVersion(value)} · 发布版本 ${documentPublishedVersion(value)}`
}

type RevisionItem = Pick<BomItem, 'kind' | 'revision' | 'sourceDocumentId' | 'displayRevision'>
const isWork = (revision: string) => /^(?:[A-Z]-)?W[1-9]\d*$/i.test(revision)
const releasedLabel = (revision: string) => isWork(revision) ? '未发布' : revision || '未发布'
export const standardBomVersionLabel = (value?: string | null) => value?.trim().replace(/-B(?=\d+$)/, '-V') || ''
export function packageRevisionLabel(item: RevisionItem, release: ReleasePackageSummary): string {
  const binding = item.sourceDocumentId ? release.versionBindings?.find(row => row.documentId === item.sourceDocumentId) : undefined
  const published = release.state === '已发布' || release.state === 'Published'
  const standard = item.kind === 'Standard' || release.scope.startsWith('Standard')
  const unavailable = standard
    ? standardBomVersionLabel(release.standardBomRevision) || (published ? 'BOM发布版本待核对' : '待发布')
    : '未发布'
  if (binding) {
    if (published && binding.publish) {
      const previous = /^(?:([A-Z])-)?W[1-9]\d*$/i.exec(binding.revision)
      return binding.targetRevision || (previous ? previous[1] ? String.fromCharCode(previous[1].toUpperCase().charCodeAt(0) + 1) : 'A' : binding.revision)
    }
    return isWork(binding.revision) ? binding.previousReleasedRevision || unavailable : releasedLabel(binding.revision)
  }
  if (!standard && published && item.sourceDocumentId && isWork(item.revision)
    && ['NonStandardWithDrawing', 'NonStandardSupplement', 'LegacyCombined'].includes(release.scope)) return '历史版次待核对'
  return standard && (isWork(item.revision) || !item.revision?.trim()) ? unavailable : releasedLabel(item.revision)
}

export function packageRevisionHint(item: RevisionItem, release: ReleasePackageSummary): string {
  const label = packageRevisionLabel(item, release)
  if ((item.kind === 'Standard' || release.scope.startsWith('Standard'))
    && label === standardBomVersionLabel(release.standardBomRevision))
    return `标准件BOM${['已发布', 'Published'].includes(release.state) ? '发布版本' : '拟发布版本'}：${label}；取自本次发布包冻结记录`
  return `${drawingRevisionHint(item.revision, label)}；存档版本：${item.revision}`
}

export function bomRevisionLabel(item: RevisionItem, packages: ReleasePackageSummary[] = []): string {
  if (item.displayRevision) return item.displayRevision
  if (item.sourceDocumentId) {
    const release = packages.filter(row => row.state === '已发布' || row.state === 'Published')
      .sort((a, b) => (Date.parse(b.publishedAt || '') || 0) - (Date.parse(a.publishedAt || '') || 0))
      .find(row => row.versionBindings?.some(binding => binding.documentId === item.sourceDocumentId && binding.publish))
    if (release) return packageRevisionLabel(item, release)
    return '未发布'
  }
  return releasedLabel(item.revision)
}
