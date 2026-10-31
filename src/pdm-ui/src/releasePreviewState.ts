import type { ProjectSummary, ReleasePackageSummary } from './types'

export function projectReleaseAttention(project: Pick<ProjectSummary, 'hasReleaseAttention' | 'hasUnreceivedNonStandardRelease' | 'latestPurchasedBomPublishedAt'>,
  packages: ReleasePackageSummary[] | undefined, failedPreviewItems: boolean, now: number): 'error' | 'new' | '' {
  if (failedPreviewItems || project.hasReleaseAttention || packages?.some(item => item.state === '发布失败' || releasePreviewUnfinished(item))) return 'error'
  if (project.hasUnreceivedNonStandardRelease) return 'new'
  const purchasedScopes = ['StandardFormal', 'StandardSupplement', 'StandardLongLead', 'ElectricalFormal', 'ElectricalSupplement', 'ElectricalLongLead']
  const publishedTimes = [project.latestPurchasedBomPublishedAt, ...(packages ?? []).filter(item => item.state === '已发布' && purchasedScopes.includes(item.scope)).map(item => item.publishedAt)]
    .map(value => Date.parse(value ?? '')).filter(Number.isFinite)
  const latest = Math.max(...publishedTimes)
  return now >= latest && now - latest < 24 * 60 * 60 * 1000 ? 'new' : ''
}

export function requiresReleasePreview(release: ReleasePackageSummary) {
  return release.locksDocuments !== false && ['NonStandardWithDrawing', 'NonStandardSupplement', 'LegacyCombined'].includes(release.scope)
}

export function releasePreviewUnfinished(release: ReleasePackageSummary) {
  return requiresReleasePreview(release) && ['已发布', '发布中'].includes(release.state)
    && ['Pending', 'WaitingForServer', 'Running', 'Failed'].includes(release.previewState ?? '')
}

export function releasePreviewNeedsAttention(release: ReleasePackageSummary) {
  return requiresReleasePreview(release) && (release.previewState === 'Failed'
    || ['Pending', 'WaitingForServer'].includes(release.previewState ?? '') && (release.previewAttempts ?? 0) >= 5)
}

/** 转图（发布预览生成）状态：与发布解耦后单独展示、单独重试。 */
export function releasePreviewStateLabel(releasePackage: ReleasePackageSummary) {
  if (!requiresReleasePreview(releasePackage)) return ''
  if (releasePackage.previewState !== 'Succeeded' && (releasePackage.previewAttempts ?? 0) >= 5)
    return `自动重试已暂停（已尝试 ${releasePackage.previewAttempts}/5 次）`
  return ({
    None: '未开始',
    Pending: `排队等待转图（每3分钟自动重试，已尝试 ${releasePackage.previewAttempts ?? 0}/5 次）`,
    WaitingForServer: '等待转图服务器恢复或空闲（不消耗失败次数）',
    Running: '转换中',
    Succeeded: '已完成',
    Failed: `转换失败（已重试 ${releasePackage.previewAttempts ?? 0} 次）`,
  } as Record<string, string>)[releasePackage.previewState ?? 'None'] ?? String(releasePackage.previewState)
}

/** 列表里的转图状态短标签；没有转图事项（长交期等）时返回空串。 */
export function releasePreviewStateTag(releasePackage: ReleasePackageSummary) {
  if (!requiresReleasePreview(releasePackage)) return ''
  if (releasePreviewNeedsAttention(releasePackage)) return '转图失败'
  const state = releasePackage.previewState ?? 'None'
  if (state === 'None') return ''
  return ({ Pending: '待转图', WaitingForServer: '等待服务器', Running: '转换中', Succeeded: '转图完成', Failed: '转图失败' } as Record<string, string>)[state]
    ?? String(releasePackage.previewState)
}
