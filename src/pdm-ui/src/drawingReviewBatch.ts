import type { DrawingReviewBatchEntry } from './types'

export async function executeDrawingReviewBatch(
  entries: DrawingReviewBatchEntry[],
  supervisor: (entry: DrawingReviewBatchEntry) => Promise<unknown>,
  target: (entry: DrawingReviewBatchEntry) => Promise<unknown>,
) {
  const approvedPackages = new Set<string>()
  for (const entry of entries) {
    if (entry.kind === 'supervisor' && entry.decision === 'Approve') {
      if (approvedPackages.has(entry.packageId)) continue
      await supervisor(entry)
      approvedPackages.add(entry.packageId)
    } else {
      await target(entry)
    }
  }
}
