import { describe, expect, it } from 'vitest'
import { countUniqueDocumentKinds } from '../src/composables/usePdmWorkspace'
import type { DocumentNode } from '../src/types'

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
