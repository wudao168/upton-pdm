import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { clearTablePreferencesSession, initializeTablePreferences, readTablePreference, refreshTablePreferences, writeTablePreference } from '../src/tablePreferences'
import { PdmApiError } from '../src/api'

const api = vi.hoisted(() => ({ readUserTablePreferences: vi.fn(), saveUserTablePreference: vi.fn() }))
vi.mock('../src/api', () => ({ ...api, PdmApiError: class extends Error { constructor(message: string, public status: number) { super(message) } } }))
const remote = new Map<string, Record<string, string>>()
const key = (user: string) => `upton-pdm:procurement-columns:${user}`
beforeEach(() => {
  clearTablePreferencesSession(); window.localStorage.clear(); remote.clear(); vi.resetAllMocks()
  api.readUserTablePreferences.mockImplementation(async (token: string, company: string) => ({ preferences: { ...remote.get(`${token}:${company}`) } }))
  api.saveUserTablePreference.mockImplementation(async (name: string, value: string, token: string, company: string) => {
    const scope = `${token}:${company}`
    remote.set(scope, { ...remote.get(scope), [name]: value })
  })
})

describe('账号列设置', () => {
  it('清空浏览器缓存后从服务端恢复所有列设置和列宽，账号与公司互相隔离', async () => {
    await initializeTablePreferences('alice', 'company', 'alice-token')
    const entries = [
      [key('alice'), '{"visible":["materialName","purchaseOrderNumbers"]}'],
      ['upton-pdm:bom-columns:alice:NonStandard', '{"visible":["name","revision"]}'],
      ['upton-pdm:production-drawing-columns:alice', '{"visible":["model","revision"]}'],
      ['upton-pdm-column-widths:v1:' + JSON.stringify([['alice', 'company'], [], ['名称','版次']]), '[80,20]'],
    ] as const
    for (const [name, value] of entries) await writeTablePreference(name, value)
    clearTablePreferencesSession(); window.localStorage.clear()
    await initializeTablePreferences('alice', 'company', 'alice-token')
    for (const [name, value] of entries) expect(readTablePreference(name)).toBe(value)
    await initializeTablePreferences('bob', 'company', 'bob-token')
    expect(readTablePreference(key('alice'))).toBeNull()
    await writeTablePreference(key('bob'), '{"visible":["sequence"]}')
    await initializeTablePreferences('alice', 'other-company', 'alice-token')
    expect(readTablePreference(key('alice'))).toBeNull()
    await initializeTablePreferences('alice', 'company', 'alice-token')
    expect(readTablePreference(key('alice'))).toBe(entries[0][1])
  })

  it('首次迁移已有本地设置，服务端已保存的值优先，不迁移其他账号或登录信息', async () => {
    window.localStorage.setItem(key('alice'), '{"visible":["sequence"]}')
    window.localStorage.setItem(key('bob'), '{"visible":["materialName"]}')
    window.localStorage.setItem('pdm_session', 'private')
    await initializeTablePreferences('alice', 'company', 'alice-token'); await flushPromises()
    expect(api.saveUserTablePreference).toHaveBeenCalledTimes(1)
    expect(remote.get('alice-token:company')?.[key('alice')]).toBe('{"visible":["sequence"]}')
    clearTablePreferencesSession()
    remote.set('alice-token:company', { [key('alice')]: '{"visible":["brand"]}' })
    await initializeTablePreferences('alice', 'company', 'alice-token')
    expect(readTablePreference(key('alice'))).toBe('{"visible":["brand"]}')
  })

  it('服务端保存失败时返回失败并保留本机选择，重试后可保存', async () => {
    await initializeTablePreferences('alice', 'company', 'alice-token')
    api.saveUserTablePreference.mockRejectedValueOnce(new Error('offline'))
    await expect(writeTablePreference(key('alice'), '{"visible":["brand"]}')).rejects.toThrow()
    expect(readTablePreference(key('alice'))).toBe('{"visible":["brand"]}')
    await writeTablePreference(key('alice'), '{"visible":["brand"]}')
    expect(remote.get('alice-token:company')?.[key('alice')]).toBe('{"visible":["brand"]}')
  })

  it('切换账号后忽略旧账号迟到的读取结果', async () => {
    let finish!: (value: { preferences: Record<string, string> }) => void
    api.readUserTablePreferences.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const reading = initializeTablePreferences('alice', 'company', 'alice-token')
    await initializeTablePreferences('bob', 'company', 'bob-token')
    await writeTablePreference(key('bob'), '{"visible":["brand"]}')
    finish({ preferences: { [key('alice')]: '{"visible":["sequence"]}' } }); await reading
    expect(readTablePreference(key('alice'))).toBeNull()
    expect(readTablePreference(key('bob'))).toBe('{"visible":["brand"]}')
    await expect(writeTablePreference(key('alice'), '{}')).rejects.toThrow('账号已切换')
  })
  it.each([404,405])('旧服务器返回%s时仅报告本机保存，不误报账号同步', async status => {
    await initializeTablePreferences('alice', 'company', 'alice-token')
    api.saveUserTablePreference.mockRejectedValueOnce(new PdmApiError('旧服务器', status))
    expect(await writeTablePreference(key('alice'), '{"visible":["brand"]}')).toBe(false)
    expect(readTablePreference(key('alice'))).toBe('{"visible":["brand"]}')
  })
  it('再次打开页面时读取其他客户端的新设置，迟到的读取不能覆盖本次保存', async () => {
    await initializeTablePreferences('alice', 'company', 'alice-token')
    remote.set('alice-token:company', { [key('alice')]: '{"visible":["brand"]}' })
    await refreshTablePreferences()
    expect(readTablePreference(key('alice'))).toBe('{"visible":["brand"]}')
    let finish!: (value: { preferences: Record<string, string> }) => void
    api.readUserTablePreferences.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const reading = refreshTablePreferences()
    await writeTablePreference(key('alice'), '{"visible":["sequence"]}')
    finish({ preferences: { [key('alice')]: '{"visible":["brand"]}' } }); await reading
    expect(readTablePreference(key('alice'))).toBe('{"visible":["sequence"]}')
  })
  it('保存未完成时不会重新读取旧设置覆盖当前选择', async () => {
    await initializeTablePreferences('alice', 'company', 'alice-token')
    let finish!: () => void
    api.saveUserTablePreference.mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve }))
    const writing = writeTablePreference(key('alice'), '{"visible":["brand"]}')
    await flushPromises()
    const reads = api.readUserTablePreferences.mock.calls.length
    await refreshTablePreferences()
    expect(api.readUserTablePreferences).toHaveBeenCalledTimes(reads)
    expect(readTablePreference(key('alice'))).toBe('{"visible":["brand"]}')
    finish(); await writing
  })
  it('保存过程中切换账号，旧账号的成功响应不能成为当前账号的保存成功', async () => {
    await initializeTablePreferences('alice', 'company', 'alice-token')
    let finish!: () => void
    api.saveUserTablePreference.mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve }))
    const writing = writeTablePreference(key('alice'), '{"visible":["brand"]}')
    const rejected = expect(writing).rejects.toThrow('账号已切换')
    await flushPromises()
    await initializeTablePreferences('bob', 'company', 'bob-token')
    finish(); await rejected
    expect(readTablePreference(key('alice'))).toBeNull()
  })
})
