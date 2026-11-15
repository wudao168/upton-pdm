import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import LoginView from '../src/components/LoginView.vue'

describe('LoginView', () => {
  beforeEach(() => { vi.useFakeTimers(); vi.stubGlobal('isSecureContext', true); vi.stubGlobal('navigator', { ...navigator, gpu: {} }) })

  afterEach(() => {
    vi.clearAllTimers()
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('keeps brand artwork and login usable on intranet HTTP', async () => {
    vi.stubGlobal('isSecureContext', false)
    const wrapper = mount(LoginView, { props: { pending: false, error: '', online: true }, global: { stubs: { ElDialog: true } } })
    expect(wrapper.find('iframe').exists()).toBe(false)
    expect(wrapper.get('.pdm-login-artwork__image').attributes('src')).toContain('hubble-m106.jpg')
    await wrapper.get('input[name="username"]').setValue('tester')
    await wrapper.get('input[name="password"]').setValue('local-test')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('submit')?.[0]).toEqual(['tester', 'local-test', false])
    wrapper.unmount()
  })

  it('uses the same UPTON logo asset as CRM', () => {
    const wrapper = mount(LoginView, {
      props: { pending: false, error: '', online: true },
      global: {
        stubs: {
          ElButton: true,
          ElDialog: true,
          ElForm: true,
          ElFormItem: true,
          ElInput: true,
        },
      },
    })

    expect(wrapper.get('.pdm-login-brand__logo').attributes('src')).toContain('company-logo-white.png')
    wrapper.unmount()
  })

  it('renders only the local account login controls', () => {
    const wrapper = mount(LoginView, {
      props: { pending: false, error: '', online: true, compact: true },
      global: {
        stubs: {
          ElButton: true,
          ElDialog: true,
          ElForm: true,
          ElFormItem: true,
          ElInput: true,
        },
      },
    })

    expect(wrapper.findAll('.pdm-login-input-frame')).toHaveLength(2)
    expect(wrapper.get('button[type="submit"]').text()).toBe('登录')
    expect(wrapper.text()).not.toMatch(/注册|Google|Apple/i)
    wrapper.unmount()
  })

  it('keeps login submission and password visibility independent from the artwork', async () => {
    const wrapper = mount(LoginView, {
      props: { pending: false, error: '', online: true, compact: true },
      global: { stubs: { ElButton: true, ElDialog: true, ElForm: true, ElFormItem: true, ElInput: true } },
    })
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined()
    await wrapper.get('input[name="username"]').setValue(' test-user ')
    await wrapper.get('input[name="password"]').setValue('test-only-password')
    await wrapper.get('button[aria-label="显示密码"]').trigger('click')
    expect(wrapper.get('input[name="password"]').attributes('type')).toBe('text')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('submit')?.[0]).toEqual(['test-user', 'test-only-password', false])
    await wrapper.setProps({ pending: true })
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })


})
