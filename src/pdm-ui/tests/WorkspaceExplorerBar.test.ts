import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import WorkspaceExplorerBar from '../src/components/WorkspaceExplorerBar.vue'
import type { DocumentNode, ProjectSummary, WorkspaceLocalFileState } from '../src/types'

const project = { id: 'project-1', code: 'P700002', name: '装配项目' } as ProjectSummary
const selected: DocumentNode = {
  id: 'node-1',
  documentId: 'document-1',
  drawingNumber: 'ASM-001',
  name: '总装配',
  fileName: 'ASM-001.SLDASM',
  kind: 'Assembly',
  configuration: 'Default',
  quantity: 1,
  version: 'A.2',
  status: 'Normal',
  children: [],
}
const localState: WorkspaceLocalFileState = {
  documentId: 'document-1', fileName: selected.fileName, fullPath: 'E:\\Workspace\\View\\P700002\\ASM-001.SLDASM',
  localState: 'Editable', localStateLabel: '可编辑', localRevision: 'A.2', latestRevision: 'A.2', message: '已由你检出，可以继续编辑。', isReadOnly: false,
}

describe('WorkspaceExplorerBar', () => {
  it('uses a PLM breadcrumb instead of exposing the physical cache path', () => {
    const wrapper = mount(WorkspaceExplorerBar, { props: { project, selected, desktopAvailable: true } })

    expect(wrapper.get('[aria-label="工作区位置"]').text()).toBe('UPLMP700002工作区ASM-001.SLDASM')
    expect(wrapper.text()).toContain('PLM受控工作区')
    expect(wrapper.text()).not.toContain('AppData')
    expect(wrapper.text()).not.toContain('D:\\')
  })

  it('keeps only refresh and folder commands beside the workspace address', async () => {
    const wrapper = mount(WorkspaceExplorerBar, { props: { project, selected, localState, desktopAvailable: true } })

    const buttons = wrapper.findAll('button')
    await buttons[0].trigger('click')
    await buttons[1].trigger('click')

    expect(wrapper.emitted('refresh')).toHaveLength(1)
    expect(wrapper.emitted('openFolder')).toEqual([[selected]])
    expect(buttons).toHaveLength(2)
    expect(wrapper.text()).not.toContain('查看最新版')
    expect(wrapper.text()).not.toContain('检出并编辑')
    expect(wrapper.get('.pdm-workspace-address__state').text()).toBe('可编辑')
  })

  it('blocks SolidWorks commands when the desktop bridge is unavailable', () => {
    const wrapper = mount(WorkspaceExplorerBar, { props: { project, selected, desktopAvailable: false } })
    const buttons = wrapper.findAll('button')

    expect(buttons[1].attributes()).toHaveProperty('disabled')
  })
})
