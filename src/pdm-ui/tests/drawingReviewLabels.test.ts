import { describe, expect, it } from 'vitest'
import { drawingReviewCandidateBadge, drawingReviewTreeBadges } from '../src/drawingReviewLabels'
import type { DrawingReviewCandidate, DrawingReviewPackage } from '../src/types'

function candidate(index: number, state: DrawingReviewCandidate['state']): DrawingReviewCandidate {
  return { candidateId: `${index}`, modelDocumentId: `model-${index}`, drawingDocumentId: `drawing-${index}`, drawingNumber: `7002541.00-0${index}`, name: '', bomKinds: [], modelRevision: 'A', drawingRevision: state === 'Ready' ? 'A-W1' : 'A', state, selectable: state === 'Ready' }
}

describe('drawingReviewTreeBadges', () => {
  it('只改动04时，01至03的模型与工程图均沿用已批准，04才待发起审核', () => {
    const candidates = [candidate(1, 'ApprovedCurrent'), candidate(2, 'ApprovedCurrent'), candidate(3, 'ApprovedCurrent'), candidate(4, 'Ready')]
    const badges = drawingReviewTreeBadges(candidates, [])
    for (const index of [1, 2, 3]) {
      expect(badges[`model-${index}`]?.label).toBe('已批准')
      expect(badges[`drawing-${index}`]?.label).toBe('已批准')
    }
    expect(badges['model-4']?.label).toBe('待发起审核')
    expect(badges['drawing-4']).toEqual(drawingReviewCandidateBadge(candidates[3]!, []))
    expect(badges['unrelated-model']).toBeUndefined()
  })

  it('新图纸版本的待审核状态不被旧审核单的已批准覆盖', () => {
    const review = { state: 'Approved', items: [{ drawingDocumentId: 'drawing-4', modelDocumentId: 'model-4', drawingState: 'Marked' }] } as DrawingReviewPackage
    expect(drawingReviewTreeBadges([candidate(4, 'Ready')], [review])['drawing-4']?.label).toBe('待发起审核')
  })

  it('同一个模型关联多张图纸时保留尚未完成的审核提示', () => {
    const ready = candidate(4, 'Ready')
    const approved = { ...candidate(1, 'ApprovedCurrent'), modelDocumentId: ready.modelDocumentId }
    expect(drawingReviewTreeBadges([ready, approved], [])['model-4']?.label).toBe('待发起审核')
  })
})
