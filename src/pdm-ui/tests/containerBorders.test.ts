import { describe, expect, it } from 'vitest'
import { sharedContainerBorders, type ContainerBorderBox } from '../src/containerBorders'

function box(left: number, top: number, right: number, bottom: number, parent?: HTMLElement): ContainerBorderBox {
  const element = document.createElement('section')
  parent?.append(element)
  return { element, rect: { left, top, right, bottom }, borders: { top: 1, right: 1, bottom: 1, left: 1 } }
}

describe('相邻容器共用边框', () => {
  it('横向相邻只保留左侧容器的右边框', () => {
    const left = box(0, 0, 100, 100), right = box(100, 0, 200, 100)
    const result = sharedContainerBorders([left, right])
    expect(result.has(left.element)).toBe(false)
    expect(result.get(right.element)).toBe('left')
  })
  it('纵向相邻只保留上方容器的下边框', () => {
    const top = box(0, 0, 100, 100), bottom = box(0, 100, 100, 200)
    expect(sharedContainerBorders([top, bottom]).get(bottom.element)).toBe('top')
  })
  it('跨列容器上方由多个容器共同覆盖时只显示一条线', () => {
    const a = box(0, 0, 100, 100), b = box(100, 0, 200, 100), c = box(0, 100, 200, 200)
    expect(sharedContainerBorders([a, b, c]).get(c.element)).toBe('top')
  })
  it('侧边仅覆盖部分高度时保留完整边框', () => {
    const a = box(0, 0, 100, 50), b = box(100, 0, 200, 100)
    expect(sharedContainerBorders([a, b]).has(b.element)).toBe(false)
  })
  it('留有间距的容器不合并边框', () => {
    const a = box(0, 0, 100, 100), b = box(101, 0, 200, 100)
    expect(sharedContainerBorders([a, b]).has(b.element)).toBe(false)
  })
  it('网格换行后重新按实际位置处理顶部和左侧', () => {
    const a = box(0, 0, 100, 100), b = box(100, 0, 200, 100), c = box(0, 100, 100, 200), d = box(100, 100, 200, 200)
    const result = sharedContainerBorders([a, b, c, d])
    expect(result.get(d.element)?.split(' ')).toEqual(['top', 'left'])
    expect(result.get(c.element)).toBe('top')
  })
  it('嵌套容器紧贴父容器内侧时使用父容器边框', () => {
    const parent = box(0, 0, 102, 102), child = box(1, 1, 101, 101, parent.element)
    expect(sharedContainerBorders([parent, child]).get(child.element)?.split(' ')).toEqual(['top', 'right', 'bottom', 'left'])
  })
  it('父容器有内边距时保留独立子容器边框', () => {
    const parent = box(0, 0, 120, 120), child = box(10, 10, 110, 110, parent.element)
    expect(sharedContainerBorders([parent, child]).has(child.element)).toBe(false)
  })
  it('不使用无边框的邻居作为共用边', () => {
    const a = box(0, 0, 100, 100), b = box(100, 0, 200, 100)
    a.borders.right = 0
    expect(sharedContainerBorders([a, b]).has(b.element)).toBe(false)
  })
  it('浮层和背景页面不共用边框', () => {
    const overlay = document.createElement('div')
    overlay.className = 'el-overlay'
    const a = box(0, 0, 100, 100), b = box(100, 0, 200, 100, overlay)
    expect(sharedContainerBorders([a, b]).has(b.element)).toBe(false)
  })
})
