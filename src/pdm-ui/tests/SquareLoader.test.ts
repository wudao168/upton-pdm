import approvedAppIcon from '../src/assets/plm-app-icon.svg'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SquareLoader from '../src/components/SquareLoader.vue'

describe('SquareLoader', () => {
  it('uses the shared PLM artwork while exposing the loading status', () => {
    const wrapper = mount(SquareLoader, { props: { label: '正在加载图纸预览', overlay: true } })

    expect(wrapper.attributes('role')).toBe('status')
    expect(wrapper.attributes('aria-label')).toBe('正在加载图纸预览')
    expect(wrapper.classes()).toContain('is-overlay')
    expect(wrapper.getComponent({ name: 'PlmCubeIcon' }).props('motion')).toBe('ordered')
    expect(wrapper.get('.plm-cube-icon').classes()).toContain('pdm-loading-logo')
    expect(wrapper.get('.plm-cube-icon__image').attributes('src')).toBe(approvedAppIcon)
    expect(wrapper.get('.plm-cube-icon__image').attributes('alt')).toBe('')
  })
})
