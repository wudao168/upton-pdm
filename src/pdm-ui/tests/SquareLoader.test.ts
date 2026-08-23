import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SquareLoader from '../src/components/SquareLoader.vue'

describe('SquareLoader', () => {
  it('renders the black eight-square loading animation', () => {
    const wrapper = mount(SquareLoader, { props: { label: '正在加载图纸预览', overlay: true } })

    expect(wrapper.attributes('role')).toBe('status')
    expect(wrapper.attributes('aria-label')).toBe('正在加载图纸预览')
    expect(wrapper.classes()).toContain('is-overlay')
    expect(wrapper.findAll('.pdm-square-loader__square')).toHaveLength(8)
  })
})
