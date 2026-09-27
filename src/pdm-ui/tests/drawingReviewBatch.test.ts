import { describe, expect, it, vi } from 'vitest'
import { executeDrawingReviewBatch } from '../src/drawingReviewBatch'
import type { DrawingReviewBatchEntry } from '../src/types'

const entry: DrawingReviewBatchEntry = { kind: 'supervisor', packageId: 'package-1', itemId: 'item-1', decision: 'Approve', comment: '' }

describe('批量图纸审批', () => {
  it('同单多张只批准一次，不同单各批准一次', async () => {
    const supervisor = vi.fn().mockResolvedValue(undefined)
    const target = vi.fn().mockResolvedValue(undefined)
    await executeDrawingReviewBatch([entry, { ...entry, itemId: 'item-2' }, { ...entry, packageId: 'package-2' }], supervisor, target)
    expect(supervisor).toHaveBeenCalledTimes(2)
    expect(target).not.toHaveBeenCalled()
  })
  it('主管退回和普通审核仍逐张执行', async () => {
    const supervisor = vi.fn().mockResolvedValue(undefined)
    const target = vi.fn().mockResolvedValue(undefined)
    const entries: DrawingReviewBatchEntry[] = [
      { ...entry, decision: 'RequestChanges', comment: '缺尺寸' },
      { ...entry, decision: 'RequestChanges', itemId: 'item-2', comment: '缺尺寸' },
      { ...entry, kind: 'target', itemId: 'item-3' },
    ]
    await executeDrawingReviewBatch(entries, supervisor, target)
    expect(supervisor).not.toHaveBeenCalled()
    expect(target.mock.calls.map(call => call[0])).toEqual(entries)
  })
})
