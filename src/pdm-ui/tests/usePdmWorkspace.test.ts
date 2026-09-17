import { describe, expect, it } from 'vitest'
import { buildDocumentDisplayRoot, countUniqueDocumentKinds } from '../src/composables/usePdmWorkspace'
import type { DocumentNode, ManagedDocument } from '../src/types'

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

describe('buildDocumentDisplayRoot', () => {
  const placeholder: DocumentNode = {
    id: 'project-project-1',
    drawingNumber: '—',
    name: '尚未关联SolidWorks图档',
    fileName: '',
    kind: 'Assembly',
    configuration: '—',
    quantity: 0,
    version: '—',
    status: 'Normal',
    children: [],
  }

  const document = (id: string, kind: ManagedDocument['kind'], drawingNumber: string): ManagedDocument => ({
    id,
    projectId: 'project-1',
    drawingNumber,
    name: drawingNumber,
    fileName: `${drawingNumber}.${kind === 'Drawing' ? 'SLDDRW' : 'SLDPRT'}`,
    kind,
    state: 'Work',
    revision: 'W1',
    rowVersion: 1,
    storedVersionCount: 1,
  })

  it('shows stored 3D documents as a flat fallback when no assembly reference root exists', () => {
    const displayRoot = buildDocumentDisplayRoot(placeholder, [
      document('drawing-1', 'Drawing', 'A-01'),
      document('part-2', 'Part', 'A-10'),
      document('part-1', 'Part', 'A-2'),
    ], 'project-1')

    expect(displayRoot.name).toBe('三维图档（尚未建立装配引用结构）')
    expect(displayRoot.children.map(item => item.documentId)).toEqual(['part-1', 'part-2'])
    expect(countUniqueDocumentKinds(displayRoot, [document('drawing-1', 'Drawing', 'A-01')].map(item => ({
      ...displayRoot.children[0],
      id: `document-${item.id}`,
      documentId: item.id,
      fileName: item.fileName,
      kind: item.kind,
    })))).toEqual({ all: 3, model: 2, drawing: 1 })
  })

  it('keeps the archived assembly hierarchy when a reference root exists', () => {
    const archivedRoot = { ...placeholder, documentId: 'assembly-1', fileName: 'assembly.SLDASM' }
    expect(buildDocumentDisplayRoot(archivedRoot, [document('part-1', 'Part', 'A-1')], 'project-1')).toBe(archivedRoot)
  })
})
