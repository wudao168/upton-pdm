import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SquareLoader from '../src/components/SquareLoader.vue'

describe('SquareLoader', () => {
  it('places PLM on the front corner and 阿普顿 on the opposite back corner', () => {
    const wrapper = mount(SquareLoader, { props: { label: '正在加载图纸预览', overlay: true } })

    expect(wrapper.attributes('role')).toBe('status')
    expect(wrapper.attributes('aria-label')).toBe('正在加载图纸预览')
    expect(wrapper.classes()).toContain('is-overlay')
    expect(wrapper.getComponent({ name: 'PlmCubeIcon' }).props('motion')).toBe('axial')
    expect(wrapper.get('.plm-cube-icon').classes()).toContain('is-axial')
    expect(wrapper.findAll('.plm-cube-icon__face')).toHaveLength(6)
    expect(wrapper.findAll('.plm-cube-icon__tile')).toHaveLength(24)
    wrapper.findAll('.plm-cube-icon__face').forEach((face) => {
      const tileStyles = face.findAll('.plm-cube-icon__tile').map(tile => tile.attributes('style'))
      expect(new Set(tileStyles).size).toBe(4)
    })

    const faceStyles = (side: string) => wrapper
      .get(`.plm-cube-icon__face.is-${side}`)
      .findAll('.plm-cube-icon__tile')
      .map(tile => tile.attributes('style'))
    const [top, front, right, back, bottom, left] = ['top', 'front', 'right', 'back', 'bottom', 'left'].map(faceStyles)
    const vertices = [
      [front[0], top[2], left[1]],
      [front[1], top[3], right[0]],
      [back[1], top[0], left[0]],
      [back[0], top[1], right[1]],
      [front[2], bottom[0], left[3]],
      [front[3], bottom[1], right[2]],
      [back[3], bottom[2], left[2]],
      [back[2], bottom[3], right[3]],
    ]
    vertices.forEach(vertex => expect(new Set(vertex).size).toBe(3))
    expect(wrapper.findAll('.plm-cube-icon__label').map(label => label.text())).toEqual(['P', 'L', 'M', '阿', '普', '顿'])
    expect(['top', 'front', 'right'].map(side => wrapper.get(`.plm-cube-icon__face.is-${side} .plm-cube-icon__label`).text())).toEqual(['P', 'L', 'M'])
    expect(['back', 'bottom', 'left'].map(side => wrapper.get(`.plm-cube-icon__face.is-${side} .plm-cube-icon__label`).text())).toEqual(['阿', '普', '顿'])
  })
})
