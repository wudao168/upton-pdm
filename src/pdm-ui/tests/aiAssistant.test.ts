import { afterEach, describe, expect, it, vi } from 'vitest'
import { streamAi, aiError } from '../src/aiAssistant'
import { canViewNavigation, systemManagementTabs } from '../src/navigationAccess'
vi.mock('../src/api', () => ({ apiBase: '', authenticatedHeaders: () => new Headers({ Authorization: 'Bearer test', 'X-Company-Id': 'company-a' }), requestJson: vi.fn() }))
afterEach(() => vi.unstubAllGlobals())
describe('AI对话传输与入口', () => {
  it('逐字节拆分中文及CRLF事件仍正确，携带公司和认证', async () => {
    const bytes = new TextEncoder().encode('event: delta\r\ndata: {"text":"中文回复"}\r\n\r\nevent: done\ndata: {"status":"complete"}\n\n')
    const fetch = vi.fn().mockResolvedValue(new Response(new ReadableStream({ start(c) { for (const byte of bytes) c.enqueue(new Uint8Array([byte])); c.close() } })))
    vi.stubGlobal('fetch', fetch); const events: unknown[] = []
    await streamAi('conversation', {}, 'test', new AbortController().signal, (event, data) => events.push([event, data]))
    expect(events).toEqual([['delta', { text: '中文回复' }], ['done', { status: 'complete' }]])
    const headers = fetch.mock.calls[0]![1].headers as Headers
    expect(headers.get('X-Company-Id')).toBe('company-a'); expect(headers.get('Authorization')).toBe('Bearer test')
  })
  it('未收到结束事件不将断流误报完成', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('event: delta\ndata: {"text":"部分回复"}\n\n')))
    await expect(streamAi('c', {}, '', new AbortController().signal, () => {})).rejects.toThrow('连接中断')
  })
  it('英文上游错误转换中文，保留中文权限原因', async () => {
    expect(aiError(new Error('Failed to fetch'))).toContain('请检查连接')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"message":"当前账号未获授权"}', { status: 403 })))
    await expect(streamAi('c', {}, '', new AbortController().signal, () => {})).rejects.toThrow('当前账号未获授权')
  })
  it('对话和服务管理独立授权，撤权隐藏入口', () => {
    const can = (permission: string) => permission === 'ai.settings.manage'
    expect(systemManagementTabs(can)).toEqual(['ai']); expect(canViewNavigation('ai-assistant', can)).toBe(false)
    expect(canViewNavigation('ai-assistant', p => p === 'ai.chat.use')).toBe(true)
    expect(canViewNavigation('ai-assistant', () => false)).toBe(false)
  })
})
