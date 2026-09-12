import { afterEach, describe, expect, it, vi } from 'vitest'
import { readProjectValidationPlan } from '../src/api'

describe('API JSON response handling', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    window.localStorage.clear()
  })

  it('将成功的空响应作为空项目验证计划处理', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 200 })))

    await expect(readProjectValidationPlan('project-1', 'token')).resolves.toBeNull()
  })
})
