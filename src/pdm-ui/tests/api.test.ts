import { afterEach, describe, expect, it, vi } from 'vitest'
import { loadProjectDocumentWorkspace, loadProjectWorkspace, saveOrganizationUnit } from '../src/api'

describe('PDM API client', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('serializes a company-level department with the numeric API enum and null parent', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      id: 'unit-1',
      organizationId: 'organization-1',
      parentUnitId: null,
      code: 'DESIGN',
      name: '设计部',
      kind: 0,
      isActive: true,
      sortOrder: 0,
    }), { status: 201, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)

    const saved = await saveOrganizationUnit({
      organizationId: 'organization-1',
      parentUnitId: undefined,
      code: 'DESIGN',
      name: '设计部',
      kind: 'BusinessDivision',
      isActive: true,
      sortOrder: 0,
    }, 'token')

    const [, request] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(JSON.parse(String(request.body))).toMatchObject({ parentUnitId: null, kind: 0 })
    expect(saved.kind).toBe('BusinessDivision')
  })

  it('does not present a registered document without a stored version as W1', async () => {
    const document = {
      id: 'document-1', projectId: 'project-1', folderId: 'folder-1', drawingNumber: 'P-001',
      name: '未存档零件', fileName: 'P-001.SLDPRT', kind: 'Part', state: 'Work',
      revision: { display: 'W1' }, storedVersionCount: 0,
    }
    const reference = {
      nodeId: 'node-1', documentId: document.id, instancePath: 'P-001-1', fileName: document.fileName,
      displayName: document.name, kind: 'Part', configuration: '默认', quantity: 1, status: 'Normal',
      revision: { display: 'W1' }, children: [],
    }
    const responses = new Map<string, unknown>([
      ['/api/projects/project-1', { id: 'project-1', code: 'P700001-1', name: '机架', owner: 'admin', vaultLocation: 'D:/PDM/P700001-1', releaseLocation: 'D:/PDM/Release/P700001-1', isActive: true }],
      ['/api/projects/project-1/documents', [document]],
      ['/api/projects/project-1/folder-documents', [document]],
      ['/api/projects/project-1/document-relations', []],
      ['/api/projects/project-1/folders', []],
      ['/api/projects/project-1/reference-tree', reference],
      ['/api/projects/project-1/boms/Standard', []],
      ['/api/projects/project-1/boms/NonStandard', []],
      ['/api/projects/project-1/boms/Unclassified', []],
      ['/api/projects/project-1/boms/Electrical', []],
      ['/api/projects/project-1/bom-source-data', []],
      ['/api/projects/project-1/boms/empty-declarations', []],
      ['/api/projects/project-1/release-packages', []],
    ])
    vi.stubGlobal('fetch', vi.fn(async (input: string | URL | Request) => {
      const path = new URL(typeof input === 'string' || input instanceof URL ? input : input.url).pathname
      return new Response(JSON.stringify(responses.get(path)), { status: responses.has(path) ? 200 : 404, headers: { 'Content-Type': 'application/json' } })
    }))

    const workspace = await loadProjectWorkspace('project-1', 'token')

    expect(workspace.root).toMatchObject({ version: '—', status: 'Unarchived' })
    expect(workspace.documents[0]).toMatchObject({ revision: '—', storedVersionCount: 0 })
  })

  it('does not use a historical snapshot checkout owner as the current edit state', async () => {
    const document = {
      id: 'document-1', projectId: 'project-1', drawingNumber: 'P-001', name: '装配体',
      fileName: 'P-001.SLDASM', kind: 'Assembly', state: 'Work', revision: { display: 'W2' },
      storedVersionCount: 2, checkedOutBy: null,
    }
    const reference = {
      nodeId: 'node-1', documentId: document.id, instancePath: 'P-001-1', fileName: document.fileName,
      displayName: document.name, kind: 'Assembly', configuration: '默认', quantity: 1, status: 'Normal',
      revision: { display: 'W2' }, checkedOutBy: 'admin', children: [],
    }
    const responses = new Map<string, unknown>([
      ['/api/projects/project-1', { id: 'project-1', code: 'P700001-1', name: '机架', owner: 'admin', vaultLocation: 'D:/PDM/P700001-1', releaseLocation: 'D:/PDM/Release/P700001-1', isActive: true }],
      ['/api/projects/project-1/documents', [document]],
      ['/api/projects/project-1/folder-documents', [document]],
      ['/api/projects/project-1/document-relations', []],
      ['/api/projects/project-1/folders', []],
      ['/api/projects/project-1/reference-tree', reference],
      ['/api/projects/project-1/boms/Standard', []],
      ['/api/projects/project-1/boms/NonStandard', []],
      ['/api/projects/project-1/boms/Unclassified', []],
      ['/api/projects/project-1/boms/Electrical', []],
      ['/api/projects/project-1/bom-source-data', []],
      ['/api/projects/project-1/boms/empty-declarations', []],
      ['/api/projects/project-1/release-packages', []],
    ])
    vi.stubGlobal('fetch', vi.fn(async (input: string | URL | Request) => {
      const path = new URL(typeof input === 'string' || input instanceof URL ? input : input.url).pathname
      return new Response(JSON.stringify(responses.get(path)), { status: responses.has(path) ? 200 : 404, headers: { 'Content-Type': 'application/json' } })
    }))

    const workspace = await loadProjectWorkspace('project-1', 'token')

    expect(workspace.root.checkedOutBy).toBeUndefined()
  })

  it('shows a child latest version without advancing the parent structure snapshot', async () => {
    const rootDocument = {
      id: 'document-root', projectId: 'project-1', drawingNumber: 'ASM-001', name: '主装配体',
      fileName: 'ASM-001.SLDASM', kind: 'Assembly', state: 'Work', revision: { display: 'W3' }, storedVersionCount: 3,
    }
    const childDocument = {
      id: 'document-child', projectId: 'project-1', drawingNumber: 'PRT-001', name: '子零件',
      fileName: 'PRT-001.SLDPRT', kind: 'Part', state: 'Work', revision: { display: 'W5' }, storedVersionCount: 5,
    }
    const reference = {
      nodeId: 'node-root', documentId: rootDocument.id, instancePath: 'ASM-001', fileName: rootDocument.fileName,
      displayName: rootDocument.name, kind: 'Assembly', configuration: '默认', quantity: 1, status: 'Normal',
      revision: { display: 'W3' }, children: [{
        nodeId: 'node-child', documentId: childDocument.id, instancePath: 'ASM-001/PRT-001-1', fileName: childDocument.fileName,
        displayName: childDocument.name, kind: 'Part', configuration: '默认', quantity: 1, status: 'Normal',
        revision: { display: 'W4' }, children: [],
      }],
    }
    const responses = new Map<string, unknown>([
      ['/api/projects/project-1/documents', [rootDocument]],
      ['/api/projects/project-1/folder-documents', [rootDocument, childDocument]],
      ['/api/projects/project-1/document-relations', []],
      ['/api/projects/project-1/reference-tree', reference],
    ])
    vi.stubGlobal('fetch', vi.fn(async (input: string | URL | Request) => {
      const path = new URL(typeof input === 'string' || input instanceof URL ? input : input.url).pathname
      return new Response(JSON.stringify(responses.get(path)), { status: responses.has(path) ? 200 : 404, headers: { 'Content-Type': 'application/json' } })
    }))

    const workspace = await loadProjectDocumentWorkspace('project-1', 'token')

    expect(workspace.root).toMatchObject({ version: 'W3', snapshotVersion: 'W3', versionAlignment: 'Synced' })
    expect(workspace.root.children[0]).toMatchObject({
      documentId: childDocument.id,
      snapshotVersion: 'W4',
      version: 'W5',
      versionAlignment: 'StructureStale',
    })
  })
})
