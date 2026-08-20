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
})
