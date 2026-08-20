import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import AppHeader from '../src/components/AppHeader.vue'
import LoginView from '../src/components/LoginView.vue'

afterEach(() => {
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('CRM-aligned personal settings', () => {
  it('shows the same profile and password functions when clicking the user name', async () => {
    const wrapper = mount(AppHeader, {
      attachTo: document.body,
      global: { plugins: [ElementPlus] },
      props: {
        online: true,
        userName: '系统管理员',
        username: 'admin',
        role: 'Administrator',
        profile: { username: 'admin', displayName: '系统管理员', gender: 'unspecified' },
        onSaveProfile: vi.fn(),
        onChangePassword: vi.fn(),
      },
    })

    await wrapper.get('.pdm-user-profile-trigger').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('个人设置')
    expect(document.body.textContent).toContain('个人资料')
    expect(document.body.textContent).toContain('修改密码')
    expect(document.body.textContent).toContain('昵称')
    expect(document.body.textContent).toContain('移动电话')
    wrapper.unmount()
  })

  it('opens the CRM-style account and name reset request', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('true', { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    const wrapper = mount(LoginView, {
      attachTo: document.body,
      global: { plugins: [ElementPlus] },
      props: { pending: false, error: '', online: true },
    })

    await wrapper.get('.pdm-login-forgot').trigger('click')
    await flushPromises()
    const inputs = Array.from(document.body.querySelectorAll<HTMLInputElement>('.el-dialog input'))
    expect(document.body.textContent).toContain('申请重置密码')
    expect(inputs).toHaveLength(2)
    inputs[0].value = 'admin'
    inputs[0].dispatchEvent(new Event('input'))
    inputs[1].value = '系统管理员'
    inputs[1].dispatchEvent(new Event('input'))
    const send = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.el-dialog button')).find(button => button.textContent?.includes('发送申请'))
    send?.click()
    await flushPromises()

    const [url, request] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain('/api/auth/password-reset-request')
    expect(JSON.parse(String(request.body))).toEqual({ username: 'admin', displayName: '系统管理员' })
    wrapper.unmount()
  })
})
