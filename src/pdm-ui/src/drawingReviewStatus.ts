import type { ApprovalStep, ReleaseDrawingReview } from './types'

type ReviewDrawing = ReleaseDrawingReview['items'][number]

export function drawingReviewStatus(item: ReviewDrawing, review?: ReleaseDrawingReview | null, taskId?: string, username?: string) {
  if (!taskId) return { latest: item.status, hasIssue: item.status === 'Issue' }
  const byActor = new Map<string, string>()
  for (const note of item.notes) {
    if (note.approvalTaskId === taskId && note.roundId === review?.roundId && note.token === item.token)
      byActor.set(note.actor.toLowerCase(), note.status)
  }
  const own = byActor.get(username?.toLowerCase() ?? '')
  const accepted = review?.preservedConfirmations?.[taskId]?.includes(item.token) ?? false
  return {
    latest: own === 'Issue' ? own : accepted ? 'Confirmed' : (own ?? 'Pending'),
    hasIssue: [...byActor.values()].includes('Issue'),
  }
}

export function drawingHasActiveReturn(item: ReviewDrawing, review: ReleaseDrawingReview | null | undefined, steps: ApprovalStep[]) {
  return steps.some(step => (step.status === 'current' || step.status === 'waiting')
    && drawingReviewStatus(item, review, step.id).hasIssue)
}
