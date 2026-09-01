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
  it('shows refresh progress and blocks duplicate refresh clicks', async () => {
    const wrapper = mountTree(true)
    const button = wrapper.get('button[aria-label="正在刷新设计树"]')

    expect(button.attributes('aria-busy')).toBe('true')
    expect(button.attributes()).toHaveProperty('disabled')
    expect(wrapper.get('[role="progressbar"][aria-label="正在刷新设计树"]').attributes('aria-valuetext')).toBe('刷新中')

    await button.trigger('click')
    expect(wrapper.emitted('refresh')).toBeUndefined()
  })

  it('emits one refresh request when idle', async () => {
    const wrapper = mountTree(false)
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
    await wrapper.get('button[aria-label="刷新设计树"]').trigger('click')
    expect(wrapper.emitted('refresh')).toHaveLength(1)
  })

  it('shows local workspace state and opens the controlled folder from the context menu', async () => {
    const root: DocumentNode = { id: 'node-1', documentId: 'document-1', drawingNumber: 'ASM-001', name: '总装配', fileName: 'ASM-001.SLDASM', kind: 'Assembly', configuration: 'Default', quantity: 1, version: 'W2', status: 'Normal', children: [] }
    const state: WorkspaceLocalFileState = { documentId: 'document-1', fileName: root.fileName, fullPath: 'E:\\Workspace\\View\\P700002\\ASM-001.SLDASM', localState: 'NeedsUpdate', localStateLabel: '需要更新', localRevision: 'W1', latestRevision: 'W2', message: '本地 W1，PLM最新 W2。', isReadOnly: true }
    const wrapper = mount(DocumentTree, { props: { query: '', filter: 'all', root, drawings: [], selectedId: root.id, allCount: 1, modelCount: 1, drawingCount: 0, warningCount: 0, localStates: { [state.documentId]: state } } })

    expect(wrapper.get('.pdm-local-state').text()).toBe('需要更新')
    expect(wrapper.text()).toContain('本地状态 / PLM版本')
    await wrapper.get('[role="treeitem"]').trigger('contextmenu')
    const folder = wrapper.findAll('[role="menuitem"]').find(item => item.text().includes('打开所在文件夹'))
    expect(folder).toBeTruthy()
    await folder!.trigger('click')
    expect(wrapper.emitted('openFolder')).toEqual([[root]])
  })
})
