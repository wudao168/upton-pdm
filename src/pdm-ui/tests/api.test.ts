import { afterEach, describe, expect, it, vi } from 'vitest'
import { downloadProductionDrawingArchive, downloadReleasePreviewArchive, listProjects, loadBomSourceData, loadProjectDocumentWorkspace, logoutSession, mapApiReleasePackage, readProjectValidationPlan, restoreDocumentVersion, saveBom } from '../src/api'
import type { BomItem } from '../src/types'

describe('API JSON response handling', () => {
  it('合并同时读取且每个页面得到独立数据，完成后重新读取', async () => {
    let resolve!: (response: Response) => void
    const fetchMock = vi.fn().mockImplementationOnce(() => new Promise<Response>(done => { resolve = done }))
      .mockImplementation(async () => new Response('{"items":[]}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const first = readProjectValidationPlan('same-project', 'token')
    const second = readProjectValidationPlan('same-project', 'token')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    resolve(new Response('{"items":[{"validationContent":"原始任务"}]}', { status: 200 }))
    const [left, right] = await Promise.all([first, second])
    expect(left).toEqual(right)
    expect(left).not.toBe(right)
    expect(left!.items).not.toBe(right!.items)
    await readProjectValidationPlan('same-project', 'token')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('不同账号和公司不共用进行中的读取', async () => {
    const resolvers: Array<(response: Response) => void> = []
    const fetchMock = vi.fn().mockImplementation(() => new Promise<Response>(resolve => resolvers.push(resolve)))
    vi.stubGlobal('fetch', fetchMock)
    window.localStorage.setItem('pdm_active_organization', 'company-a')
    const first = readProjectValidationPlan('project', 'first-token')
    const second = readProjectValidationPlan('project', 'second-token')
    window.localStorage.setItem('pdm_active_organization', 'company-b')
    const third = readProjectValidationPlan('project', 'first-token')
    expect(fetchMock).toHaveBeenCalledTimes(3)
    resolvers.forEach(resolve => resolve(new Response('null', { status: 200 })))
    await Promise.all([first, second, third])
  })

  it('写入后重新读取，旧请求完成不会删除新的进行中请求', async () => {
    const resolvers: Array<(response: Response) => void> = []
    const fetchMock = vi.fn().mockImplementation(() => new Promise<Response>(resolve => resolvers.push(resolve)))
    vi.stubGlobal('fetch', fetchMock)
    const first = readProjectValidationPlan('project', 'token')
    const write = logoutSession('token')
    const second = readProjectValidationPlan('project', 'token')
    resolvers[0]!(new Response('null', { status: 200 }))
    await first
    const third = readProjectValidationPlan('project', 'token')
    expect(fetchMock).toHaveBeenCalledTimes(3)
    resolvers[1]!(new Response('true', { status: 200 }))
    resolvers[2]!(new Response('null', { status: 200 }))
    await Promise.all([write, second, third])
  })

  it('失败的共同读取可重试', async () => {
    const fetchMock = vi.fn().mockRejectedValueOnce(new Error('offline'))
      .mockImplementation(async () => new Response('null', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const results = await Promise.allSettled([readProjectValidationPlan('project', 'token'), readProjectValidationPlan('project', 'token')])
    expect(results.map(result => result.status)).toEqual(['rejected', 'rejected'])
    await expect(readProjectValidationPlan('project', 'token')).resolves.toBeNull()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
  it.each([4, 'Virtual'])('嵌入组件保留虚拟状态，不因没有独立图档编号误报未入库（%s）', async status => {
    const documents = [{ id: 'model', projectId: 'project', kind: 0, fileName: 'A.SLDASM', storedVersionCount: 1, revision: { display: 'W1' } }]
    const root = { documentId: 'model', nodeId: 'root', instancePath: 'root', fileName: 'A.SLDASM', kind: 0, status: 0, children: [
      { nodeId: 'embedded', instancePath: 'root/embedded', fileName: 'PART^A.SLDPRT', kind: 1, status, children: [] },
      { nodeId: 'unregistered', instancePath: 'root/external', fileName: 'EXTERNAL.SLDPRT', kind: 1, status: 0, children: [] },
    ] }
    vi.stubGlobal('fetch', vi.fn(async (path: string) => new Response(JSON.stringify(path.endsWith('/documents') || path.endsWith('/folder-documents') ? documents
      : path.endsWith('/reference-tree') ? root : []), { status: 200 })))
    const result = await loadProjectDocumentWorkspace('project', 'token')
    expect(result.root.children.find(node => node.id === 'embedded')).toMatchObject({ status: 'Virtual', documentId: undefined, versionAlignment: undefined })
    expect(result.root.children.find(node => node.id === 'unregistered')).toMatchObject({ status: 'Unregistered' })
  })

  it('关联树工程图与2D列表统一使用正式版本数，登记不等于存档', async () => {
    const documents = [
      { id: 'model', projectId: 'project', kind: 0, fileName: 'A.SLDASM', storedVersionCount: 1, revision: { display: 'W1' } },
      { id: 'drawing', projectId: 'project', kind: 2, fileName: 'A.SLDDRW', storedVersionCount: 0, revision: { display: 'W1' } },
    ]
    vi.stubGlobal('fetch', vi.fn(async (path: string) => new Response(JSON.stringify(path.endsWith('/documents') || path.endsWith('/folder-documents') ? documents
      : path.endsWith('/document-relations') ? [{ modelDocumentId: 'model', drawingDocumentId: 'drawing' }]
        : path.endsWith('/reference-tree') ? { documentId: 'model', nodeId: 'root', instancePath: 'root', fileName: 'A.SLDASM', kind: 0, children: [] } : []), { status: 200 })))
    const result = await loadProjectDocumentWorkspace('project', 'token')
    expect(result.root.children[0]).toMatchObject({ documentId: 'drawing', status: 'Unarchived', version: '—' })
    expect(result.documents.find(document => document.id === 'drawing')?.storedVersionCount).toBe(0)
  })

  it('恢复版本携带固定操作号、原基准与编辑会话', async () => {
    const fetchMock = vi.fn().mockImplementation(async () => new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const input = { changeNote: '恢复检查', operationId: 'operation', expectedVersionId: 'latest', checkoutSessionId: 'session' }
    await restoreDocumentVersion('doc', 'old', input, 'token')
    await restoreDocumentVersion('doc', 'old', input, 'token')
    expect(fetchMock.mock.calls.map(call => JSON.parse(call[1].body))).toEqual([input, input])
  })
  it('保留服务端BOM实例所属装配根路径，供独立树统计和发布范围筛选使用', async () => {
    const path = 'roots/11111111111111111111111111111111/root/part'
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify([
      { id: 'source', sourceDocumentId: 'part', sourceConfiguration: 'Default', sourceInstancePath: path, kind: 'NonStandard', quantity: 3, name: '零件', revision: 'W1' },
    ]), { status: 200 })))
    const result = await loadBomSourceData('project', 'token')
    expect(result[0]?.sourceInstancePath).toBe(path)
    expect(result[0]?.sourceDocumentId).toBe('part')
  })
  it.each(['headers', 'body'])('bounds a stalled BOM save %s without retrying the write', async phase => {
    vi.useFakeTimers()
    const stalled = new Promise<never>(() => {})
    const fetchMock = vi.fn().mockImplementation(() => phase === 'headers'
      ? stalled : Promise.resolve({ ok: true, status: 200, text: () => stalled }))
    vi.stubGlobal('fetch', fetchMock)
    const result = saveBom('project', 'Standard', [], 'token')
    const rejected = expect(result).rejects.toThrow('服务端保存结果尚未确认')
    await vi.advanceTimersByTimeAsync(60_000)
    await rejected
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]?.[1].signal.aborted).toBe(true)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('rebuilds the saved draft in snapshot order and preserves snapshot overrides without refetching versions', async () => {
    const items = [{ id: 'a', name: '当前名称', kind: 'Standard' }, { id: 'b', name: '复用明细', kind: 'Standard' }, { id: 'excluded', name: '不发布', kind: 'Standard' }]
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      items, draft: { id: 'draft', kind: 'Standard', state: 'Draft', items: [] },
      draftItemIds: ['b', 'a'], draftItemOverrides: [{ ...items[0], name: '快照名称' }],
    }), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const input: BomItem[] = items.map((item, index) => ({ ...item, kind: 'Standard', sequence: index + 1,
      drawingNumber: `CODE-${item.id}`, quantity: 1, unit: '001', revision: 'W1', complete: true }))
    const result = await saveBom('project', 'Standard', input, 'token')
    expect(result.draftVersion?.items.map(item => item.name)).toEqual(['复用明细', '快照名称'])
    expect(result.draftVersion?.items[0]).toEqual(result.items[1])
    expect(result.draftVersion?.items[0]).not.toBe(result.items[1])
    expect(result.items[0]?.name).toBe('当前名称')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]?.[0]).toContain('/api/projects/project/boms/Standard?includeDraft=true')
    expect(JSON.parse(fetchMock.mock.calls[0]?.[1].body)).toEqual({ items: input.map(item => ({ ...item, isComplete: true })) })
  })

  it('requests a normal version refresh for old responses or missing snapshot references', async () => {
    const input: BomItem[] = [{ id: 'a', sequence: 1, drawingNumber: 'CODE-A', name: '已有实例',
      quantity: 1, unit: '001', revision: 'W1', complete: true }]
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response('[]', { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ items: [], draft: { items: [] }, draftItemIds: ['missing'], draftItemOverrides: [] }), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    expect((await saveBom('project', 'Standard', input, 'token')).draftVersion).toBeUndefined()
    expect((await saveBom('project', 'Standard', input, 'token')).draftVersion).toBeUndefined()
    expect(fetchMock).toHaveBeenCalledTimes(2)
    for (const call of fetchMock.mock.calls) expect(call[0]).toContain('/api/projects/project/boms/Standard?includeDraft=true')
  })

  it('saves an empty BOM through the summary contract and handles a missing draft', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ items: [], draft: null }), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    expect(await saveBom('project', 'Standard', [], 'token')).toEqual({ items: [], draftVersion: null })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]?.[0]).toContain('/api/projects/project/boms/Standard?summary=true')
    expect(fetchMock.mock.calls[0]?.[1].method).toBe('PUT')
    expect(JSON.parse(fetchMock.mock.calls[0]?.[1].body)).toEqual({ items: [], summaryOrder: [], summaryRows: [] })
  })

  it('saves summary edits with all instance ids and maps the returned draft independently', async () => {
    const edited: BomItem = { id: 'a', summaryItemIds: ['a', 'b'], summaryFingerprint: 'current-fingerprint',
      editedFields: ['name'], sequence: 1, drawingNumber: 'CODE-A', name: '修改后的名称', quantity: 2,
      unit: '001', revision: 'W1', complete: true }
    const unchanged: BomItem = { ...edited, id: 'c', summaryItemIds: ['c'], summaryFingerprint: 'unchanged-fingerprint', editedFields: [], sequence: 2 }
    const added: BomItem = { sequence: 3, drawingNumber: '', name: '新增物料', quantity: 1, unit: '001', revision: 'W1', complete: false }
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      items: [{ ...edited, isComplete: true }],
      draft: { id: 'draft', kind: 'Standard', state: 'Draft', items: [{ ...edited, name: '草稿快照名称', isComplete: false }] },
    }), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const result = await saveBom('project', 'Standard', [edited, unchanged, added], 'token')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]?.[0]).toContain('/api/projects/project/boms/Standard?summary=true')
    expect(JSON.parse(fetchMock.mock.calls[0]?.[1].body)).toEqual({
      items: [{ ...added, isComplete: false }], summaryOrder: ['a', 'c'],
      summaryRows: [{ itemIds: ['a', 'b'], fingerprint: 'current-fingerprint', fields: ['name'], values: { ...edited, isComplete: true } }],
    })
    expect(result.items[0]).toMatchObject({ name: '修改后的名称', complete: true, summaryItemIds: ['a', 'b'] })
    expect(result.draftVersion?.items[0]).toMatchObject({ name: '草稿快照名称', complete: false, summaryItemIds: ['a', 'b'] })
    expect(result.draftVersion?.items[0]).not.toBe(result.items[0])
  })

  it('loads project paths when the browser does not support Array.at', async () => {
    const descriptor = Object.getOwnPropertyDescriptor(Array.prototype, 'at')!
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify([
      { id: 'project-1', code: 'P1', name: 'Project', owner: 'engineer', isActive: true, vaultLocation: 'D:\\PDM\\P1', releaseLocation: 'D:\\Release\\P1' },
    ]), { status: 200 })))
    Object.defineProperty(Array.prototype, 'at', { ...descriptor, value: undefined })
    let projects
    try {
      projects = await listProjects('token')
    } finally {
      Object.defineProperty(Array.prototype, 'at', descriptor)
    }
    expect(projects[0]?.vaultName).toBe('P1')
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.useRealTimers()
    vi.unstubAllGlobals()
    window.localStorage.clear()
  })

  it('将成功的空响应作为空项目验证计划处理', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 200 })))

    await expect(readProjectValidationPlan('project-1', 'token')).resolves.toBeNull()
  })

  it('保留发布包的转图（STEP/PDF）状态字段', () => {
    const mapped = mapApiReleasePackage({
      id: 'release-1', number: 'RP-1', state: 'Published', scope: 'NonStandardWithDrawing',
      previewState: 'Failed', previewError: '发布包尚未进入服务器转换状态。', previewAttempts: 5,
      previewUpdatedAt: '2026-09-20T15:00:00Z',
      versionBindings: [{ documentId: 'model', versionId: 'frozen-work', revision: 'A-W1', sha256: 'hash', publish: true, requiresPromotion: true, targetRevision: 'B' }],
    } as never)

    expect(mapped.previewState).toBe('Failed')
    expect(mapped.previewError).toBe('发布包尚未进入服务器转换状态。')
    expect(mapped.previewAttempts).toBe(5)
    expect(mapped.previewUpdatedAt).toBe('2026-09-20T15:00:00Z')
    expect(mapped.versionBindings?.[0]).toMatchObject({ documentId: 'model', versionId: 'frozen-work', targetRevision: 'B' })
  })

  it.each(['Pdf', 'Step'] as const)('names %s archive with release package number', async format => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(new Blob(['zip']))))
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:archive')
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    await downloadProductionDrawingArchive('project-1', ['version-1'], format, 'token', 'RP-P700012-0-20260927-4621D865')
    expect((click.mock.instances[0] as HTMLAnchorElement).download).toBe(`RP-P700012-0-20260927-4621D865-${format === 'Pdf' ? 'PDF' : 'STEP'}.zip`)
  })

  it.each([
    ['生产图纸', () => downloadProductionDrawingArchive('project-1', ['version-1'], 'Pdf', 'test-token')],
    ['发布转图', () => downloadReleasePreviewArchive('project-1', 'package-1', ['document-1'], 'test-token')],
  ])('%s批量下载携带登录令牌和企业范围', async (_label, download) => {
    window.localStorage.setItem('pdm_active_organization', 'company-1')
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 401 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(download()).rejects.toThrow()

    const headers = new Headers((fetchMock.mock.calls[0][1] as RequestInit).headers)
    expect(headers.get('Authorization')).toBe('Bearer test-token')
    expect(headers.get('X-Company-Id')).toBe('company-1')
    expect(headers.get('Content-Type')).toBe('application/json')
  })

})
