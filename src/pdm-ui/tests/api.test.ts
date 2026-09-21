import { afterEach, describe, expect, it, vi } from 'vitest'
import { mapApiReleasePackage, readProjectValidationPlan } from '../src/api'

describe('API JSON response handling', () => {
  afterEach(() => {
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
})
