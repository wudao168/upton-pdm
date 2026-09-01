import { ElMessageBox } from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { confirmBomGeneration, countUniqueDocumentKinds, formatBomGenerationConfirmation } from '../src/composables/usePdmWorkspace'
import type { BomGenerationResult, BomItem, DocumentNode } from '../src/types'

describe('countUniqueDocumentKinds', () => {
  it('counts one controlled document once even when the design tree contains repeated instances', () => {
    const part = (id: string, instance: string): DocumentNode => ({
      id: instance,
      documentId: id,
      drawingNumber: id,
      name: id,
      fileName: `${id}.SLDPRT`,
      kind: 'Part',
      configuration: '默认',
      quantity: 1,
      version: 'W1',
      status: 'Normal',
      children: [],
    })
    const root: DocumentNode = {
      ...part('assembly', 'root'),
      kind: 'Assembly',
      fileName: 'assembly.SLDASM',
      children: [part('part-1', 'instance-1'), part('part-1', 'instance-2'), part('part-2', 'instance-3')],
    }
    const drawing: DocumentNode = { ...part('drawing-1', 'drawing-1'), kind: 'Drawing', fileName: 'drawing-1.SLDDRW' }

    expect(countUniqueDocumentKinds(root, [drawing])).toEqual({ all: 4, model: 3, drawing: 1 })
  })
})

describe('formatBomGenerationConfirmation', () => {
  it('lists every BOM count on its own line', () => {
    const items = (count: number) => Array.from({ length: count }, () => ({} as BomItem))
    const preview: BomGenerationResult = {
      standardItems: items(16),
      nonStandardItems: items(4),
      electricalItems: [],
      unclassifiedItems: [],
      virtualItems: items(6),
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
      '• 虚拟件（仅源数据）：6 条',
      '',
      '待处理项不会静默删除，并会阻止发布。是否应用本次更新？',
    ])
  })
})

describe('confirmBomGeneration', () => {
  const preview: BomGenerationResult = {
    standardItems: [],
    nonStandardItems: [],
    electricalItems: [],
    unclassifiedItems: [],
    virtualItems: [],
    virtualCount: 0,
    unclassifiedCount: 0,
    pendingRemovalCount: 0,
    manualUnmatchedCount: 0,
    applied: false,
  }

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('uses the in-app confirmation dialog before applying the update', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)

    await expect(confirmBomGeneration(preview)).resolves.toBe(true)
    expect(confirm).toHaveBeenCalledWith(
      formatBomGenerationConfirmation(preview),
      '确认重新对账',
      expect.objectContaining({
        confirmButtonText: '确认更新',
        cancelButtonText: '取消',
        closeOnClickModal: false,
      }),
    )
  })

  it.each(['cancel', 'close'])('treats %s as an explicit cancellation', async action => {
    vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue(action)

    await expect(confirmBomGeneration(preview)).resolves.toBe(false)
  })
})
