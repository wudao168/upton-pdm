import type { ReleasePackageSummary } from './types'

/** 转图（发布预览生成）状态：与发布解耦后单独展示、单独重试。 */
export function releasePreviewStateLabel(releasePackage: ReleasePackageSummary) {
  return ({
    None: '未开始',
    Pending: '待转图（后台自动重试）',
    Running: '转换中',
    Succeeded: '已完成',
    Failed: `转换失败（已重试 ${releasePackage.previewAttempts ?? 0} 次）`,
  } as Record<string, string>)[releasePackage.previewState ?? 'None'] ?? String(releasePackage.previewState)
}

/** 列表里的转图状态短标签；没有转图事项（长交期等）时返回空串。 */
export function releasePreviewStateTag(releasePackage: ReleasePackageSummary) {
  const state = releasePackage.previewState ?? 'None'
  if (state === 'None') return ''
  return ({ Pending: '待转图', Running: '转换中', Succeeded: '转图完成', Failed: '转图失败' } as Record<string, string>)[state]
    ?? String(releasePackage.previewState)
}
