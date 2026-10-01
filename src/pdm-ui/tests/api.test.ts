import { afterEach, describe, expect, it, vi } from 'vitest'
import { downloadProductionDrawingArchive, downloadReleasePreviewArchive, listProjects, mapApiReleasePackage, readProjectValidationPlan } from '../src/api'

describe('API JSON response handling', () => {
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
