import { mount, flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ConnectionLights from '../src/components/ConnectionLights.vue'

describe('ConnectionLights', () => {
  const wrappers: ReturnType<typeof mount>[] = []
  beforeEach(() => {
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false)
    vi.spyOn(console, 'warn').mockImplementation(() => {})
    Object.defineProperty(window, 'chrome', { configurable: true, value: undefined })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => ({ pdf: { state: 'connected', message: 'PDF：已连接转图服务器。' }, u9c: { state: 'disconnected', message: 'U9C：认证失败，请检查配置。' } }) }))
  })
  afterEach(() => { wrappers.forEach(wrapper => wrapper.unmount()); wrappers.length = 0; vi.useRealTimers(); vi.restoreAllMocks(); Object.defineProperty(window, 'chrome', { configurable: true, value: undefined }) })
  function render(token = 'session-token') { const wrapper = mount(ConnectionLights, { props: { token } }); wrappers.push(wrapper); return wrapper }

  it('shows independent server results and keeps the browser SW light unconfigured', async () => {
    const wrapper = render()
    expect(wrapper.findAll('[role="status"]').map(light => light.text())).toEqual(['SW', 'PDF', 'U9C'])
    expect(wrapper.findAll('[role="status"]')[1]!.attributes('data-state')).toBe('checking')
    await flushPromises()
    const lights = wrapper.findAll('[role="status"]')
    expect(lights.map(light => light.attributes('data-state'))).toEqual(['unconfigured', 'connected', 'disconnected'])
    expect(lights[2]!.attributes('title')).toContain('认证失败')
    expect(fetch).toHaveBeenCalledWith('/api/system-connections', expect.objectContaining({ headers: { Authorization: 'Bearer session-token' } }))
  })

  it('distinguishes installed plugins from responsive plugins', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: { postMessage } } })
    const wrapper = render()
    expect(postMessage).toHaveBeenCalledWith({ type: 'solidworks-capability-request', payload: undefined })
    window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true, connected: false } }))
    await flushPromises()
    expect(wrapper.find('[role="status"]').attributes('data-state')).toBe('warning')
    window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true, connected: true } }))
    await flushPromises()
    expect(wrapper.find('[role="status"]').attributes('data-state')).toBe('connected')
    window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: false, connected: false } }))
    await flushPromises()
    expect(wrapper.find('[role="status"]').attributes('data-state')).toBe('disconnected')
  })

  it('polls SW every five seconds and server status every thirty seconds, then stops on unmount', async () => {
    vi.useFakeTimers()
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: { postMessage } } })
    const wrapper = render()
    await flushPromises()
    await vi.advanceTimersByTimeAsync(5000)
    expect(postMessage).toHaveBeenCalledTimes(2)
    expect(fetch).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(25000)
    expect(fetch).toHaveBeenCalledTimes(2)
    wrapper.unmount(); wrappers.length = 0
    await vi.advanceTimersByTimeAsync(30000)
    expect(fetch).toHaveBeenCalledTimes(2)
    expect(postMessage).toHaveBeenCalledTimes(7)
  })

  it('shows a Chinese unknown-state explanation after transport failures, without exposing raw errors', async () => {
    vi.mocked(fetch).mockRejectedValueOnce(new Error('private technical error'))
    const wrapper = render()
    await flushPromises()
    expect(wrapper.findAll('[role="status"]').slice(1).map(light => light.attributes('data-state'))).toEqual(['warning', 'warning'])
    expect(wrapper.html()).not.toContain('private technical error')
    expect(wrapper.html()).toContain('暂时无法读取')
  })

  it('does not query without a session or while the document is hidden', async () => {
    const wrapper = render('')
    await flushPromises()
    expect(fetch).not.toHaveBeenCalled()
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true)
    await wrapper.setProps({ token: 'session' })
    expect(fetch).not.toHaveBeenCalled()
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false)
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(fetch).toHaveBeenCalledTimes(1)
  })
})
