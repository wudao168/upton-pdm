import type { ReleaseDrawingReview } from './types'

export function drawingReviewStatus(item: ReleaseDrawingReview['items'][number], roundId?: string, taskId?: string, username?: string) {
  if (!taskId) return { latest: item.status, hasIssue: item.status === 'Issue' }
  const byActor = new Map<string, string>()
  for (const note of item.notes) {
    if (note.approvalTaskId === taskId && note.roundId === roundId && note.token === item.token)
      byActor.set(note.actor.toLowerCase(), note.status)
  }
  return {
    latest: byActor.get(username?.toLowerCase() ?? '') ?? 'Pending',
    hasIssue: [...byActor.values()].includes('Issue'),
  }
}
