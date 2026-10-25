import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import DocumentTree from '../src/components/DocumentTree.vue'
import type { DocumentNode, WorkspaceLocalFileState } from '../src/types'

function mountTree(refreshing: boolean) {
  return mount(DocumentTree, {
    props: {
      query: '',
      filter: 'all',
      drawings: [],
      selectedId: '',
      allCount: 0,
      modelCount: 0,
      drawingCount: 0,
      warningCount: 0,
      refreshing,
    },
  })
}

describe('DocumentTree', () => {
  it('嵌入组件的菜单提示打开父装配，不要求独立登记', async () => {
    const embedded: DocumentNode = { id: 'embedded', drawingNumber: 'PART', name: '嵌入零件', fileName: 'PART^ASM.SLDPRT', kind: 'Part', configuration: '', quantity: 1, version: '—', status: 'Virtual', children: [] }
    const wrapper = mountTree(false)
    await wrapper.setProps({ root: embedded })
    await wrapper.get('[role="treeitem"]').trigger('contextmenu')
    expect(wrapper.text()).toContain('内容随父装配存档')
    expect(wrapper.text()).not.toContain('该引用尚未入库')
  })

  it('虚拟汇总节点使用分组图标且不显示未入库，真实子节点仍提示', async () => {
    const child: DocumentNode = { id: 'part', drawingNumber: 'PART', name: '零件', fileName: 'PART.SLDPRT', kind: 'Part', configuration: '', quantity: 1, version: '—', status: 'Unregistered', children: [] }
    const wrapper = mountTree(false)
    await wrapper.setProps({ root: { ...child, id: 'project', drawingNumber: '', name: '项目装配体（汇总）', isVirtualGroup: true, children: [child] } })
    const group = wrapper.findAll('[role="treeitem"]')[0]!
    expect(group.text()).not.toContain('未入库')
    expect(group.classes()).not.toContain('has-warning')
    expect(group.find('[aria-label="装配体汇总分组"]').exists()).toBe(true)
    expect(wrapper.findAll('[role="treeitem"]')[1]!.text()).toContain('未入库')
    await group.trigger('contextmenu')
    expect(wrapper.text()).not.toContain('该引用尚未入库')
  })
  it('搜索时展开已折叠的根节点及所有匹配层级，切换关键词或刷新结果后再次展开', async () => {
    const leaf: DocumentNode = { id: 'part', drawingNumber: '355.0700A.01', name: '目标零件', fileName: '355.0700A.01.SLDPRT', kind: 'Part', configuration: 'Default', quantity: 1, version: 'W1', status: 'Normal', children: [] }
    const branch: DocumentNode = { ...leaf, id: 'branch', drawingNumber: 'SUB-001', name: '子装配', kind: 'Assembly', children: [leaf] }
    const root: DocumentNode = { ...branch, id: 'root', drawingNumber: 'CW0030002.10', name: '总装配', children: [branch] }
    const wrapper = mountTree(false)
    await wrapper.setProps({ root })
    expect(wrapper.text()).not.toContain('目标零件')
    await wrapper.get('.pdm-tree-row__toggle').trigger('click')
    expect(wrapper.findAll('[role="treeitem"]')).toHaveLength(1)

    await wrapper.setProps({ query: '355.0700A.01' })
    expect(wrapper.findAll('[role="treeitem"]')).toHaveLength(3)
    expect(wrapper.text()).toContain('目标零件')
    await wrapper.findAll('.pdm-tree-row__toggle')[1]!.trigger('click')
    expect(wrapper.text()).not.toContain('目标零件')
    await wrapper.setProps({ query: '目标零件' })
    expect(wrapper.text()).toContain('目标零件')
    await wrapper.get('.pdm-tree-row__toggle').trigger('click')
    await wrapper.setProps({ root: { ...root } })
    expect(wrapper.text()).toContain('目标零件')

    await wrapper.setProps({ query: '' })
    await wrapper.get('.pdm-tree-row__toggle').trigger('click')
    expect(wrapper.findAll('[role="treeitem"]')).toHaveLength(1)
    await wrapper.setProps({ query: '   ', root: { ...root } })
    expect(wrapper.findAll('[role="treeitem"]')).toHaveLength(1)
    wrapper.unmount()
  })

  it('renders large 2D drawing lists in bounded pages', async () => {
    const drawings: DocumentNode[] = Array.from({ length: 161 }, (_, index) => ({
      id: `drawing-${index}`, documentId: `document-${index}`,
      drawingNumber: `DRW-${index}`, name: `图纸 ${index}`, fileName: `DRW-${index}.SLDDRW`,
      kind: 'Drawing', configuration: 'Default', quantity: 1, version: 'W1', status: 'Normal', children: [],
    }))
    const wrapper = mount(DocumentTree, { props: {
      query: '', filter: 'drawing', drawings, selectedId: '', allCount: 161,
      modelCount: 0, drawingCount: 161, warningCount: 0,
    } })

    expect(wrapper.findAll('.pdm-drawing-list > li')).toHaveLength(81)
    expect(wrapper.text()).toContain('第 1 / 3 页')
    await wrapper.findAll('.pdm-drawing-pages button')[1].trigger('click')
    expect(wrapper.text()).toContain('DRW-80')
    expect(wrapper.text()).not.toContain('DRW-0')
    await wrapper.findAll('.pdm-drawing-pages button')[1].trigger('click')
    expect(wrapper.findAll('.pdm-drawing-list > li')).toHaveLength(2)
    expect(wrapper.text()).toContain('DRW-160')
  })

  it('rotates the refresh icon and blocks duplicate refresh clicks', async () => {
    const wrapper = mountTree(true)
    const button = wrapper.get('button[aria-label="正在刷新设计树"]')

    expect(button.attributes('aria-busy')).toBe('true')
    expect(button.attributes()).toHaveProperty('disabled')
    expect(button.get('.pdm-tree-refresh-icon').classes()).toContain('is-spinning')
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)

    await button.trigger('click')
    expect(wrapper.emitted('refresh')).toBeUndefined()
  })

  it('emits one refresh request when idle', async () => {
    const wrapper = mountTree(false)
    expect(wrapper.text()).not.toContain('导航窗格')
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
    const button = wrapper.get('.pdm-tree-search-row button[aria-label="刷新设计树"]')
    expect(button.get('.pdm-tree-refresh-icon').classes()).not.toContain('is-spinning')
    await button.trigger('click')
    expect(wrapper.emitted('refresh')).toHaveLength(1)
  })

  it('shows local workspace state and opens the controlled folder from the context menu', async () => {
    const root: DocumentNode = { id: 'node-1', documentId: 'document-1', drawingNumber: 'ASM-001', name: '总装配', fileName: 'ASM-001.SLDASM', kind: 'Assembly', configuration: 'Default', quantity: 1, version: 'W2', status: 'Normal', children: [] }
    const state: WorkspaceLocalFileState = { documentId: 'document-1', fileName: root.fileName, fullPath: 'E:\\Workspace\\View\\P700002\\ASM-001.SLDASM', localState: 'NeedsUpdate', localStateLabel: '需要更新', localRevision: 'W1', latestRevision: 'W2', message: '本地 W1，PLM最新 W2。', isReadOnly: true }
    const wrapper = mount(DocumentTree, { props: { query: '', filter: 'all', root, drawings: [], selectedId: root.id, allCount: 1, modelCount: 1, drawingCount: 0, warningCount: 0, localStates: { [state.documentId]: state } } })

    expect(wrapper.get('.pdm-local-state').text()).toBe('需要更新')
    expect(wrapper.text()).toContain('图纸版本 / 发布版本')
    await wrapper.get('[role="treeitem"]').trigger('contextmenu')
    const folder = wrapper.findAll('[role="menuitem"]').find(item => item.text().includes('打开所在文件夹'))
    expect(folder).toBeTruthy()
    await folder!.trigger('click')
    expect(wrapper.emitted('openFolder')).toEqual([[root]])
  })

  it('offers version selection from the design tree context menu', async () => {
    const root: DocumentNode = { id: 'node-1', documentId: 'document-1', drawingNumber: 'ASM-001', name: '总装配', fileName: 'ASM-001.SLDASM', kind: 'Assembly', configuration: 'Default', quantity: 1, version: 'B', status: 'Normal', children: [] }
    const wrapper = mount(DocumentTree, { props: { query: '', filter: 'all', root, drawings: [], selectedId: root.id, allCount: 1, modelCount: 1, drawingCount: 0, warningCount: 0 } })

    await wrapper.get('[role="treeitem"]').trigger('contextmenu')
    const versions = wrapper.findAll('[role="menuitem"]').find(item => item.text().includes('查看/对比历史版本'))
    expect(versions).toBeTruthy()
    await versions!.trigger('click')
    expect(wrapper.emitted('versions')).toEqual([[root]])
  })
})
