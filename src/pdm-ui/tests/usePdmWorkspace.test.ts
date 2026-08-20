import { describe, expect, it } from 'vitest'
import { formatBomGenerationConfirmation } from '../src/composables/usePdmWorkspace'
import type { BomGenerationResult, BomItem } from '../src/types'

describe('formatBomGenerationConfirmation', () => {
  it('lists every BOM count on its own line', () => {
    const items = (count: number) => Array.from({ length: count }, () => ({} as BomItem))
    const preview: BomGenerationResult = {
      standardItems: items(16),
      nonStandardItems: items(4),
      electricalItems: [],
      unclassifiedItems: [],
      virtualCount: 6,
      unclassifiedCount: 1,
      pendingRemovalCount: 2,
      manualUnmatchedCount: 3,
      applied: false,
    }

    expect(formatBomGenerationConfirmation(preview).split('\n')).toEqual([
      '将按最新设计树更新机械BOM：',
      '• 标准件：16 条',
      '• 非标件：4 条',
      '• 待分类：1 条',
      '• 待移除：2 条',
      '• 人工待确认：3 条',
      '• 虚拟件排除：6 条',
      '',
      '待处理项不会静默删除，并会阻止发布。是否应用本次更新？',
    ])
  })
})
