import type { DrawingReviewPackage } from './types'

const packageStateLabels: Record<DrawingReviewPackage['state'], string> = {
  InReview: '待审核',
  PendingSupervisorApproval: '待批准',
  ChangesRequested: '已退回（待修改）',
  WritingProperties: '已批准',
  Approved: '已批准',
  Stale: '版本冲突',
  Withdrawn: '已撤销',
}

const targetStateLabels: Record<DrawingReviewPackage['items'][number]['drawingState'], string> = {
  Pending: '待审核',
  ChangesRequested: '已退回（待修改）',
  Approved: '待批准',
  Marked: '已批准',
  NotRequired: '无需审核',
}

export function drawingReviewPackageStateLabel(state: DrawingReviewPackage['state']) {
  return packageStateLabels[state]
}

export function drawingReviewTargetStateLabel(state: DrawingReviewPackage['items'][number]['drawingState']) {
  return targetStateLabels[state]
}

export function drawingReviewAssignedReviewerPool(packageValue: DrawingReviewPackage | undefined) {
  return packageValue?.assignedReviewers ?? (packageValue?.assignedReviewer ? [packageValue.assignedReviewer] : [])
}

export type DrawingReviewTone = 'neutral' | 'pending' | 'warning' | 'success' | 'danger'

/** 阶段配色：待提交=灰、待审核=橙、待批准=蓝、已批准=绿、已退回=红。 */
export function drawingReviewTargetStateTone(state: DrawingReviewPackage['items'][number]['drawingState']): DrawingReviewTone {
  return ({ Pending: 'warning', ChangesRequested: 'danger', Approved: 'pending', Marked: 'success', NotRequired: 'neutral' } as const)[state]
}

export function drawingReviewCandidateStateTone(state: string): DrawingReviewTone {
  return ({ Ready: 'neutral', InReview: 'warning', ApprovedCurrent: 'success', Unavailable: 'neutral' } as Record<string, DrawingReviewTone>)[state] ?? 'neutral'
}

export function drawingReviewPackageStateTone(state: DrawingReviewPackage['state']): DrawingReviewTone {
  return ({
    InReview: 'warning',
    PendingSupervisorApproval: 'pending',
    ChangesRequested: 'danger',
    WritingProperties: 'success',
    Approved: 'success',
    Stale: 'danger',
    Withdrawn: 'neutral',
  } as const)[state]
}

export function drawingReviewAssignedReviewerLabel(
  packageValue: DrawingReviewPackage | undefined,
  displayUserName: (username?: string | null, emptyText?: string) => string,
) {
  if (!packageValue) return ''
  const usernames = drawingReviewAssignedReviewerPool(packageValue)
  const names = packageValue.assignedReviewerNames
    ?? (packageValue.assignedReviewerName ? [packageValue.assignedReviewerName] : [])
  if (!usernames.length) return '审核权限人员均可处理'
  return usernames.map((username, index) => names[index] || displayUserName(username)).join('、')
}
