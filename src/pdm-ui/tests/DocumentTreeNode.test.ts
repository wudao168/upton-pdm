import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import DocumentTreeNode from '../src/components/DocumentTreeNode.vue'
import type { DocumentNode } from '../src/types'

function node(id: string, name: string, children: DocumentNode[] = []): DocumentNode {
  return {
    id,
    documentId: id,
    drawingNumber: id,
    name,
    fileName: `${id}.SLDASM`,
    kind: 'Assembly',
    configuration: '默认',
    quantity: 1,
    version: 'W1',
    snapshotVersion: 'W1',
    status: 'Normal',
    children,
  }
}

describe('DocumentTreeNode', () => {
  it('收到客户端本地轻量预览缩略图后在图标前显示小图，未返回时只显示原图标', async () => {
    const root = node('thumb-1', '切料机构')
    const wrapper = mount(DocumentTreeNode, { props: { node: root, selectedId: '' } })

    expect(wrapper.find('.pdm-tree-row__thumb').exists()).toBe(false)

    window.dispatchEvent(new CustomEvent('pdm-tree-thumbnail-status', {
      detail: { documentId: 'thumb-1', dataUrl: 'data:image/jpeg;base64,thumb' },
    }))
    await wrapper.vm.$nextTick()

    const thumb = wrapper.get('.pdm-tree-row__thumb')
    expect(thumb.attributes('src')).toBe('data:image/jpeg;base64,thumb')
    // 缩略图仍在 CAD 图标之前。
    expect(thumb.element.nextElementSibling?.getAttribute('class') ?? '').toContain('pdm-cad-icon')
  })

  it('initially expands only the root level and allows deeper levels to be opened manually', async () => {
    const grandchild = node('grandchild', '第二级')
    const child = node('child', '第一级', [grandchild])
    const root = node('root', '根节点', [child])
    const wrapper = mount(DocumentTreeNode, { props: { node: root, selectedId: '' } })

    const initialRows = wrapper.findAll('.pdm-tree-row')
    expect(initialRows.map(row => row.text())).toEqual(expect.arrayContaining([expect.stringContaining('根节点'), expect.stringContaining('第一级')]))
    expect(initialRows.some(row => row.text().includes('第二级'))).toBe(false)
    expect(initialRows[0].attributes('aria-expanded')).toBe('true')
    expect(initialRows[1].attributes('aria-expanded')).toBe('false')

    await initialRows[1].get('.pdm-tree-row__toggle').trigger('click')

    expect(wrapper.findAll('.pdm-tree-row').some(row => row.text().includes('第二级'))).toBe(true)
  })

  it('does not repeat the drawing number as a meaningless second line', () => {
    const wrapper = mount(DocumentTreeNode, { props: { node: node('R70000050.02-01', 'R70000050.02-01'), selectedId: '' } })

    expect(wrapper.find('.pdm-tree-row__label strong').text()).toBe('R70000050.02-01')
    expect(wrapper.find('.pdm-tree-row__label small').exists()).toBe(false)
  })
})
