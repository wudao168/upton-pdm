import { afterEach, describe, expect, it, vi } from 'vitest'
import { createClientUuid } from '../src/clientId'

afterEach(() => vi.unstubAllGlobals())
describe('HTTP compatible client UUIDs', () => {
  it.each([undefined, { getRandomValues: (bytes: Uint8Array) => { bytes.fill(3); return bytes } }])('creates a valid UUID without randomUUID', crypto => {
    vi.stubGlobal('crypto', crypto)
    expect(createClientUuid()).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  })
})
