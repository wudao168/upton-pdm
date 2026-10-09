import { afterEach, describe, expect, it, vi } from 'vitest'
import { downloadProductionDrawingArchive, downloadReleasePreviewArchive, listProjects, mapApiReleasePackage, readProjectValidationPlan, saveBom } from '../src/api'
import type { BomItem } from '../src/types'

describe('API JSON response handling', () => {
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
    } as never)

    expect(mapped.previewState).toBe('Failed')
    expect(mapped.previewError).toBe('发布包尚未进入服务器转换状态。')
    expect(mapped.previewAttempts).toBe(5)
    expect(mapped.previewUpdatedAt).toBe('2026-09-20T15:00:00Z')
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
