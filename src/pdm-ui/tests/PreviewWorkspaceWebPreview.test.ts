import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import PreviewWorkspace from '../src/components/PreviewWorkspace.vue'
import type { DocumentNode, DocumentVersionSummary } from '../src/types'

const api = vi.hoisted(() => ({
  listDocumentVersions: vi.fn(),
  readDocumentPreviewFile: vi.fn(),
  postDesktopMessage: vi.fn(),
}))
vi.mock('../src/api', () => api)

const selected: DocumentNode = {
  id: 'node-1', documentId: 'document-1', drawingNumber: '3D打印盖子', name: '3D打印盖子', fileName: '3D打印盖子.SLDPRT',
  kind: 'Part', configuration: '默认', quantity: 1, version: 'A-W1', status: 'Normal', children: [],
}

function version(id: string, display: string, status: 'Work' | 'Released', preview?: DocumentVersionSummary['preview']): DocumentVersionSummary {
  return { id, documentId: 'document-1', revision: { display }, status, fileLength: 10, sha256: 'A'.repeat(64), createdBy: 'engineer', createdAt: '2026-09-20T00:00:00Z', changeNote: '', preview }
}

describe('PreviewWorkspace 网页端预览版本回退', () => {
  beforeEach(() => { vi.clearAllMocks() })

  it('工作版本没有预览时回退到正式版本的PDF预览并提示实际版本', async () => {
    api.listDocumentVersions.mockResolvedValue([
      version('v-work-2', 'A-W1', 'Work'),
      version('v-released', 'A', 'Released', { format: 'Pdf', storageRelativePath: '.release-previews/a.pdf', fileLength: 10, sha256: 'B'.repeat(64), sourceSha256: 'C'.repeat(64) }),
      version('v-work-1', 'W1', 'Work'),
    ])
    api.readDocumentPreviewFile.mockResolvedValue(new Blob(['pdf'], { type: 'application/pdf' }))
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], desktopAvailable: false, accessToken: 'token' },
    })

    await wrapper.get('.pdm-preview-state-content button').trigger('click')
    await vi.waitFor(() => expect(api.readDocumentPreviewFile).toHaveBeenCalled())

    // 不是报“该历史版本尚未生成预览”，而是取正式版本的预览文件
    expect(api.readDocumentPreviewFile).toHaveBeenCalledWith('document-1', 'v-released', 'token')
    expect(wrapper.get('.pdm-preview-notice').text()).toContain('A-W1 版本还没有预览')
    expect(wrapper.get('.pdm-preview-notice').text()).toContain('A 版本')
    wrapper.unmount()
  })

  it('所有版本都没有预览时提示该图档尚未生成预览', async () => {
    api.listDocumentVersions.mockResolvedValue([version('v-work-1', 'A-W1', 'Work')])
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], desktopAvailable: false, accessToken: 'token' },
    })

    await wrapper.get('.pdm-preview-state-content button').trigger('click')
    await vi.waitFor(() => expect(wrapper.text()).toContain('该图档还没有生成STP/PDF预览'))

    expect(api.readDocumentPreviewFile).not.toHaveBeenCalled()
    expect(wrapper.find('.pdm-preview-notice').exists()).toBe(false)
    wrapper.unmount()
  })
})
