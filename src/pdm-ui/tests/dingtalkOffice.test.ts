import { afterEach, expect, it, vi } from 'vitest'
import { dingTalkOfficeAuthCode } from '../src/dingtalkOffice'
afterEach(() => { delete (window as Window & { dd?: unknown }).dd; vi.useRealTimers() })
it('passes the configured enterprise and app key to the current H5 authorization API', async () => {
  const requestAuthCode = vi.fn(input => input.success({ code: 'one-use-code' }))
  Object.assign(window, { dd: { env: { platform: 'ios' }, ready: (callback: () => void) => callback(), requestAuthCode } })
  await expect(dingTalkOfficeAuthCode('corp-1', 'app-1')).resolves.toBe('one-use-code')
  expect(requestAuthCode).toHaveBeenCalledWith(expect.objectContaining({ corpId: 'corp-1', clientId: 'app-1' }))
})
it('supports the existing DingTalk runtime permission API', async () => {
  Object.assign(window, { dd: { env: { platform: 'pc' }, ready: (callback: () => void) => callback(), runtime: { permission: { requestAuthCode: (input: { onSuccess: (result: { code: string }) => void }) => input.onSuccess({ code: 'legacy-code' }) } } } })
  await expect(dingTalkOfficeAuthCode('corp', 'app')).resolves.toBe('legacy-code')
})
it('rejects ordinary browsers before requesting any authorization code', async () => {
  const requestAuthCode = vi.fn()
  Object.assign(window, { dd: { env: { platform: 'notInDingTalk' }, requestAuthCode } })
  await expect(dingTalkOfficeAuthCode('corp', 'app')).rejects.toThrow('请在钉钉工作台中打开')
  expect(requestAuthCode).not.toHaveBeenCalled()
})
it('reports a Chinese error if the SDK never becomes ready', async () => {
  vi.useFakeTimers()
  Object.assign(window, { dd: { env: { platform: 'android' }, ready: () => {} } })
  const result = expect(dingTalkOfficeAuthCode('corp', 'app')).rejects.toThrow('钉钉身份识别超时')
  await vi.advanceTimersByTimeAsync(15_000); await result
})
