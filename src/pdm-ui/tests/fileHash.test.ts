import { describe, expect, it, vi } from 'vitest'
import { sha256Hex } from '../src/fileHash'

const bytes = (value: string) => new TextEncoder().encode(value).buffer as ArrayBuffer

describe('sha256Hex', () => {
  it.each([
    ['', 'E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855'],
    ['abc', 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD'],
    ['UPLM 3D attachment', 'CAF8AE656312B39E44218D50C71E64045D1DA6F266931CD6A2DC91EE88B591D6'],
  ])('uses the compatibility implementation for %j', async (value, expected) => {
    await expect(sha256Hex(bytes(value), null)).resolves.toBe(expected)
  })

  it('keeps using native Web Crypto when it is available', async () => {
    const data = bytes('native')
    const nativeDigest = new Uint8Array(32).fill(0xab).buffer
    const digest = vi.fn().mockResolvedValue(nativeDigest)

    await expect(sha256Hex(data, { digest } as unknown as SubtleCrypto))
      .resolves.toBe('AB'.repeat(32))
    expect(digest).toHaveBeenCalledWith('SHA-256', data)
  })
})
