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
  it('同时展示图纸版本和发布版本，升档后仍保持上次发布版本', async () => {
    const root = { ...node('part', '零件'), drawingVersionNumber: 3, publishedVersion: 'A' }
    const wrapper = mount(DocumentTreeNode, { props: { node: root, selectedId: '' } })
    expect(wrapper.get('.pdm-tree-drawing-version').text()).toBe('V3')
    expect(wrapper.get('.pdm-tree-published-version').text()).toBe('A')
    await wrapper.setProps({ node: { ...root, drawingVersionNumber: 4, version: 'A-W1' } })
    expect(wrapper.get('.pdm-tree-drawing-version').text()).toBe('V4')
    expect(wrapper.get('.pdm-tree-published-version').text()).toBe('A')
    expect(wrapper.text()).not.toContain('B-D')
  })
  it('引用旧版不再占用文字行，保留版本和悬停原因，审核状态位于上行', () => {
    const root: DocumentNode = { ...node('700022.00-01', '顶板-30'), drawingVersionNumber: 4, publishedVersion: 'A', snapshotVersion: 'W1', version: 'A-W1', versionAlignment: 'StructureStale' }
    const wrapper = mount(DocumentTreeNode, { props: { node: root, selectedId: '', reviewStates: { [root.documentId!]: { label: '已批准', tone: 'success' } } } })
    expect(wrapper.get('.pdm-tree-row__states').text()).toBe('已批准')
    expect(wrapper.get('.pdm-tree-row__versions').text()).toBe('V4A')
    expect(wrapper.text()).not.toContain('引用版本较旧')
    expect(wrapper.text()).not.toContain('图纸版本')
    expect(wrapper.text()).not.toContain('发布版本')
    expect(wrapper.get('.pdm-tree-row').classes()).toContain('has-version-warning')
    expect(wrapper.get('.pdm-tree-drawing-version').attributes('title')).toContain('引用版本较旧')
  })
  it('嵌入组件显示随父装配存档，不显示缺失或未入库', () => {
    const embedded: DocumentNode = { ...node('embedded', '嵌入零件'), documentId: undefined, status: 'Virtual', version: '—', snapshotVersion: '—' }
    const wrapper = mount(DocumentTreeNode, { props: { node: embedded, selectedId: '' } })
    expect(wrapper.text()).toContain('随父装配')
    expect(wrapper.text()).toContain('嵌入存储')
    expect(wrapper.text()).not.toContain('未入库')
    expect(wrapper.get('.pdm-tree-row').classes()).not.toContain('has-warning')
  })

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
