import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import LoginView from '../src/components/LoginView.vue'
import PdmLoginCharacters from '../src/components/PdmLoginCharacters.vue'

describe('LoginView', () => {
  beforeEach(() => vi.useFakeTimers())

  afterEach(() => {
    vi.clearAllTimers()
    vi.useRealTimers()
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

  it('moves the character bodies and pupils with the pointer', async () => {
    const wrapper = mount(PdmLoginCharacters)
    expect(wrapper.findAll('.pupil-dot')).toHaveLength(4)
    expect(wrapper.findAll('.pdm-character-mouth')).toHaveLength(1)
    const purple = wrapper.get('.is-purple')
    const pupil = wrapper.get('.purple-eye i')
    const initialBodyTransform = purple.attributes('style')
    const initialPupilTransform = pupil.attributes('style')
    const pointerMove = new Event('pointermove')
    Object.defineProperties(pointerMove, {
      clientX: { value: 1500 },
      clientY: { value: 700 },
    })

    window.dispatchEvent(pointerMove)
    await nextTick()

    expect(purple.attributes('style')).not.toBe(initialBodyTransform)
    expect(pupil.attributes('style')).not.toBe(initialPupilTransform)
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
