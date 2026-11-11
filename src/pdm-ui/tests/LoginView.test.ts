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

  it('keeps login usable on intranet HTTP without starting unsupported WebGPU', async () => {
    vi.stubGlobal('isSecureContext', false)
    const wrapper = mount(LoginView, { props: { pending: false, error: '', online: true }, global: { stubs: { ElDialog: true } } })
    expect(wrapper.find('iframe').exists()).toBe(false)
    expect(wrapper.find('[aria-label="西瓜软糖展示"]').exists()).toBe(true)
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

  it('keeps login submission and password visibility independent from the WebGPU frame', async () => {
    const wrapper = mount(LoginView, {
      props: { pending: false, error: '', online: true, compact: true },
      global: { stubs: { ElButton: true, ElDialog: true, ElForm: true, ElFormItem: true, ElInput: true } },
    })
    expect(wrapper.get('iframe').attributes('src')).toContain('melon-jelly.html')
    expect(wrapper.get('iframe').attributes('sandbox')).toBe('allow-scripts')
    expect(wrapper.get('iframe').attributes('title')).toContain('西瓜软糖')
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

  it('switches one sandboxed scene without losing login credentials or password visibility', async () => {
    const wrapper = mount(LoginView, {
      props: { pending: false, error: '', online: true },
      global: { stubs: { ElButton: true, ElDialog: true, ElForm: true, ElFormItem: true, ElInput: true } },
    })
    const initialFrame = wrapper.get('iframe').element
    await wrapper.get('input[name="username"]').setValue(' switch-user ')
    await wrapper.get('input[name="password"]').setValue('test-only-password')
    await wrapper.get('button[aria-label="显示密码"]').trigger('click')
    const choices = wrapper.findAll('.pdm-login-scene-picker button')
    expect(choices.map(choice => choice.text())).toEqual(['西瓜软糖', '海盗果冻'])
    expect(choices[0].attributes('aria-pressed')).toBe('true')
    await choices[1].trigger('click')
    expect(wrapper.findAll('iframe')).toHaveLength(1)
    expect(wrapper.get('iframe').element).not.toBe(initialFrame)
    expect(wrapper.get('iframe').attributes('src')).toContain('pirate-jelly.html?embed=login')
    expect(wrapper.get('iframe').attributes('sandbox')).toBe('allow-scripts')
    expect(wrapper.get('iframe').attributes('title')).toContain('海盗果冻')
    expect(choices[1].attributes('aria-pressed')).toBe('true')
    expect((wrapper.get('input[name="username"]').element as HTMLInputElement).value).toBe(' switch-user ')
    expect(wrapper.get('input[name="password"]').attributes('type')).toBe('text')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('submit')?.[0]).toEqual(['switch-user', 'test-only-password', false])
    await choices[0].trigger('click')
    expect(wrapper.findAll('iframe')).toHaveLength(1)
    expect(wrapper.get('iframe').attributes('src')).toContain('melon-jelly.html?embed=login')
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

})
