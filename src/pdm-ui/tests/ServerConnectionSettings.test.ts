import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ServerConnectionSettings from '../src/components/ServerConnectionSettings.vue'
import { postDesktopMessage } from '../src/api'

vi.mock('../src/api', () => ({ postDesktopMessage: vi.fn() }))
vi.mock('../src/statusMessage', () => ({ ElMessage: { error: vi.fn() } }))
afterEach(() => vi.clearAllMocks())

describe('shared server settings', () => {
  it('loads the shared address and submits the chosen IP and port', async () => {
    const wrapper = mount(ServerConnectionSettings)
    window.dispatchEvent(new CustomEvent('pdm-desktop-settings', { detail: { available: true, serverAddress: 'http://192.168.2.8:5173' } }))
    await wrapper.vm.$nextTick()
    expect((wrapper.get('input').element as HTMLInputElement).value).toBe('http://192.168.2.8:5173')
    await wrapper.get('input').setValue('192.168.2.9:5173')
    await wrapper.get('button').trigger('click')
    expect(postDesktopMessage).toHaveBeenCalledWith('desktop-settings-save', { serverAddress: '192.168.2.9:5173' })
    expect(wrapper.get('button').text()).toBe('正在验证连接…')
    wrapper.unmount()
  })

  it('retains the saved address when connection validation fails', async () => {
    const wrapper = mount(ServerConnectionSettings)
    window.dispatchEvent(new CustomEvent('pdm-desktop-settings', { detail: { available: true, serverAddress: 'http://192.168.2.8:5173', error: '服务器设置未保存' } }))
    await wrapper.vm.$nextTick()
    expect((wrapper.get('input').element as HTMLInputElement).value).toBe('http://192.168.2.8:5173')
    expect(wrapper.get('button').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('disables native settings in an ordinary browser', () => {
    const wrapper = mount(ServerConnectionSettings)
    expect(wrapper.get('input').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('请在 UPLM 客户端中设置服务器地址')
    wrapper.unmount()
  })
})
