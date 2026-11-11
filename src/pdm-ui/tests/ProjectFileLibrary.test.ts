import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectFileLibrary from '../src/components/ProjectFileLibrary.vue'
import type { ProjectFolder } from '../src/types'

const api = vi.hoisted(() => ({
  listProjectFiles: vi.fn(), uploadProjectFile: vi.fn(), createProjectFolder: vi.fn(), deleteProjectFolder: vi.fn(),
  renameProjectFolder: vi.fn(), moveProjectFolder: vi.fn(), renameProjectFile: vi.fn(), updateProjectFileDescription: vi.fn(), moveProjectFile: vi.fn(),
  deleteProjectFile: vi.fn(), restoreProjectFile: vi.fn(), listProjectFileVersions: vi.fn(), downloadProjectFile: vi.fn(), readProjectFileContent: vi.fn(),
  readFolderDocumentIndex: vi.fn(), readFolderDocuments: vi.fn(),
  listControlledDocumentRecycleBin: vi.fn(), getControlledDocumentRecycleReadiness: vi.fn(), recycleControlledDocument: vi.fn(), recycleControlledDocumentsBatch: vi.fn(), restoreControlledDocument: vi.fn(),
  listRecycleArchiveBatches: vi.fn(), listArchiveRecycleSets: vi.fn(), previewRecycleArchive: vi.fn(), recycleArchiveSet: vi.fn(), restoreArchiveSet: vi.fn(),
}))
vi.mock('../src/api', () => api)

const businessFolder = { id: 'folder-1', rootProjectId: 'project-1', folderKey: 'project-files', templateKey: 'project-files', name: '项目文件', purpose: 'Standard', sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 31, permissions: [] } satisfies ProjectFolder
const controlledFolder = { ...businessFolder, id: 'folder-2', name: 'P700001-0', purpose: 'ProjectContainer', effectiveAccess: 127 } satisfies ProjectFolder
const file = { id: 'file-1', rootProjectId: 'project-1', folderId: 'folder-1', fileName: '会议纪要.pdf', createdBy: 'engineer', createdAt: '2026-08-26T00:00:00Z', updatedBy: 'engineer', updatedAt: '2026-08-26T00:00:00Z', currentVersion: { id: 'version-1', projectFileId: 'file-1', versionNumber: 1, fileName: '会议纪要.pdf', fileLength: 1024, sha256: 'A'.repeat(64), uploadedBy: 'engineer', uploadedAt: '2026-08-26T00:00:00Z' } }

function mountLibrary(folders: ProjectFolder[] = [businessFolder], documents: any[] = [], canRecycleDocuments = false) {
  api.readFolderDocuments.mockResolvedValue(documents)
  api.readFolderDocumentIndex.mockResolvedValue({ documentCounts: Object.fromEntries(folders.map(folder => [folder.id, documents.filter(item => item.folderId === folder.id).length])) })
  return mount(ProjectFileLibrary, { props: { projectId: 'project-1', token: 'token', folders, documents, users: [{ username: 'engineer', displayName: '工程师甲', role: 'Engineer', isActive: true }], roles: [], administrator: false, canRecycleDocuments, pending: false, onUpdatePermissions: vi.fn(), onReload: vi.fn() }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })
}

describe('ProjectFileLibrary', () => {
  it('切换目录和刷新目录数据不重开已折叠分支，子级展开状态互不影响', async () => {
    const root = { ...businessFolder, id: 'root', name: '根目录', purpose: 'Root' as const }
    const mechanical = { ...businessFolder, id: 'mechanical', name: '机械图纸', purpose: 'MechanicalRoot' as const, parentFolderId: root.id }
    const branch = { ...controlledFolder, id: 'branch', name: '机械子目录', parentFolderId: mechanical.id }
    const electrical = { ...businessFolder, id: 'electrical', name: '电气图纸', purpose: 'ElectricalRoot' as const, parentFolderId: root.id }
    const folders = [root, mechanical, branch, { ...controlledFolder, id: 'part', name: '零件目录', parentFolderId: branch.id }, electrical,
      { ...controlledFolder, id: 'electric-part', name: '电气子目录', parentFolderId: electrical.id }, { ...businessFolder, parentFolderId: root.id }]
    const wrapper = mountLibrary(folders); await flushPromises()
    const tree = wrapper.findComponent({ name: 'ElTree' }).vm as unknown as { getNode(id: string): { expanded: boolean } }
    const toggle = async (name: string) => {
      const label = wrapper.findAll('.pdm-folder-node').find(node => node.text().startsWith(name))!
      ;(label.element.closest('.el-tree-node__content')!.querySelector('.el-tree-node__expand-icon') as HTMLElement).click()
      await flushPromises()
    }
    expect(tree.getNode(mechanical.id).expanded).toBe(true)
    await toggle(branch.name); await toggle(mechanical.name)
    expect(tree.getNode(mechanical.id).expanded).toBe(false)
    expect(tree.getNode(branch.id).expanded).toBe(true)
    await wrapper.findAll('.pdm-folder-node').find(node => node.text().startsWith(businessFolder.name))!.trigger('click'); await flushPromises()
    await wrapper.findAll('.pdm-folder-node').find(node => node.text().startsWith('电气子目录'))!.trigger('click'); await flushPromises()
    expect(tree.getNode(mechanical.id).expanded).toBe(false)
    expect(tree.getNode(electrical.id).expanded).toBe(true)
    await wrapper.setProps({ folders: folders.map(folder => ({ ...folder })) }); await flushPromises()
    expect(tree.getNode(mechanical.id).expanded).toBe(false)
    expect(tree.getNode(branch.id).expanded).toBe(true)
    wrapper.unmount()
  })
  it('有回收权限时可从受控目录打开批次回收入口', async () => {
    api.listRecycleArchiveBatches.mockResolvedValue([]); api.listArchiveRecycleSets.mockResolvedValue([])
    const wrapper = mountLibrary([controlledFolder], [], true); await flushPromises()
    const button = wrapper.findAll('button').find(item => item.text() === '按存档批次回收 / 恢复')!
    expect(button.exists()).toBe(true); await button.trigger('click'); await flushPromises()
    expect(api.listRecycleArchiveBatches).toHaveBeenCalledWith('project-1', 'token'); wrapper.unmount()
  })
  beforeEach(() => { vi.clearAllMocks(); api.listProjectFiles.mockResolvedValue([file]); api.listControlledDocumentRecycleBin.mockResolvedValue([]); api.readProjectFileContent.mockResolvedValue(new Blob(['media'])) })

  it('仅读取当前目录图档，切换目录时复用计数而不加载完整项目树', async () => {
    const wrapper = mountLibrary([businessFolder, controlledFolder])
    await flushPromises()
    expect(api.readFolderDocumentIndex).toHaveBeenCalledTimes(1)
    expect(api.readFolderDocuments).toHaveBeenLastCalledWith('project-1', 'folder-1', 'token')
    await wrapper.findAll('.pdm-folder-node').find(node => node.text().includes('P700001-0'))!.trigger('click')
    await flushPromises()
    expect(api.readFolderDocuments).toHaveBeenLastCalledWith('project-1', 'folder-2', 'token')
    expect(api.readFolderDocumentIndex).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  it('切换项目后丢弃原目录迟到的图档响应', async () => {
    const wrapper = mountLibrary([controlledFolder])
    await flushPromises()
    let finish!: (documents: any[]) => void
    api.readFolderDocuments.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    await wrapper.setProps({ documents: [] })
    await flushPromises()
    api.readFolderDocuments.mockResolvedValue([])
    await wrapper.setProps({ projectId: 'project-2' })
    await flushPromises()
    finish([{ id: 'old', projectId: 'project-1', folderId: 'folder-2', name: '过期图档', fileName: 'OLD.SLDPRT', kind: 'Part', revision: 'W1' }])
    await flushPromises()
    expect(wrapper.text()).not.toContain('过期图档')
    wrapper.unmount()
  })

  it('在普通业务目录按目录权限显示文件操作并使用人名', async () => {
    const wrapper = mountLibrary()
    await flushPromises()
    expect(api.listProjectFiles).toHaveBeenCalledWith('project-1', undefined, false, 'token')
    expect(wrapper.text()).toContain('上传文件')
    expect(wrapper.text()).toContain('新建文件夹')
    expect(wrapper.text()).toContain('会议纪要.pdf')
    expect(wrapper.text()).toContain('工程师甲')
    expect(wrapper.text()).toContain('说明')
    const descriptionButton = wrapper.find('button[aria-label="编辑 会议纪要.pdf 的说明"]')
    expect(descriptionButton.exists()).toBe(true)
    expect(descriptionButton.find('svg').exists()).toBe(false)
    expect(wrapper.find('button[title="下载"]').exists()).toBe(true)
    expect(wrapper.find('button[title="删除"]').exists()).toBe(true)
  })

  it('项目文件默认按更新时间倒序显示', async () => {
    const newer = { ...file, id: 'file-newer', fileName: '最新文件.pdf', updatedAt: '2026-08-28T00:00:00Z', currentVersion: { ...file.currentVersion, id: 'version-newer', projectFileId: 'file-newer', fileName: '最新文件.pdf' } }
    api.listProjectFiles.mockResolvedValue([file, newer])
    const wrapper = mountLibrary()
    await flushPromises()

    expect(wrapper.findAll('[aria-label="项目资料文件"] tbody tr td:nth-child(2)').map(cell => cell.text())).toEqual(['最新文件.pdf', '会议纪要.pdf'])
  })

  it('可预览文件可直接点击文件名打开预览', async () => {
    const wrapper = mountLibrary()
    await flushPromises()

    await wrapper.find('button[title="预览 会议纪要.pdf"]').trigger('click')

    expect(api.readProjectFileContent).toHaveBeenCalledWith('project-1', file.id, 'token')
    expect(api.downloadProjectFile).not.toHaveBeenCalled()
  })

  it('项目文件列表支持选择全部并按目录权限显示批量操作', async () => {
    const wrapper = mountLibrary()
    await flushPromises()

    await wrapper.get('input[aria-label="选择当前列表全部项目文件"]').setValue(true)

    expect(wrapper.text()).toContain('已选 1 项')
    expect(wrapper.text()).toContain('批量下载')
    expect(wrapper.text()).toContain('批量移动')
    expect(wrapper.text()).toContain('批量删除')
  })

  it('图片和视频在页面内打开预览播放窗口', async () => {
    const video = { ...file, id: 'file-video', fileName: 'normal_video.mp4', currentVersion: { ...file.currentVersion, id: 'version-video', projectFileId: 'file-video', fileName: 'normal_video.mp4' } }
    api.listProjectFiles.mockResolvedValue([video])
    const wrapper = mountLibrary()
    await flushPromises()

    await wrapper.get('button[title="预览播放 normal_video.mp4"]').trigger('click')
    await flushPromises()

    expect(api.readProjectFileContent).toHaveBeenCalledWith('project-1', 'file-video', 'token')
    expect(wrapper.find('video').exists()).toBe(true)
  })

  it('取消文件说明编辑后失焦不保存，也不清空原说明', async () => {
    api.listProjectFiles.mockResolvedValue([{ ...file, description: '保留说明' }])
    const wrapper = mountLibrary(); await flushPromises()
    await wrapper.get('button.pdm-file-description').trigger('click')
    const input = wrapper.get('input.pdm-file-description-input')
    await input.setValue('临时输入')
    await input.trigger('keydown', { key: 'Escape' }); await input.trigger('blur'); await flushPromises()
    expect(api.updateProjectFileDescription).not.toHaveBeenCalled()
    expect(wrapper.get('button.pdm-file-description').text()).toBe('保留说明')
    wrapper.unmount()
  })

  it('未修改文件说明时失焦不提交更新', async () => {
    api.listProjectFiles.mockResolvedValue([{ ...file, description: '保留说明' }])
    const wrapper = mountLibrary(); await flushPromises()
    await wrapper.get('button.pdm-file-description').trigger('click')
    await wrapper.get('input.pdm-file-description-input').trigger('blur'); await flushPromises()
    expect(api.updateProjectFileDescription).not.toHaveBeenCalled()
    expect(wrapper.find('input.pdm-file-description-input').exists()).toBe(false)
    wrapper.unmount()
  })

  it('文件说明在单元格内编辑并保存，不打开弹窗', async () => {
    const wrapper = mountLibrary()
    await flushPromises()

    await wrapper.find('button[aria-label="编辑 会议纪要.pdf 的说明"]').trigger('click')
    const input = wrapper.find('input[aria-label="编辑 会议纪要.pdf 的说明"]')
    expect(input.exists()).toBe(true)
    expect(wrapper.find('.el-message-box').exists()).toBe(false)

    await input.setValue('会议结论')
    await input.trigger('keydown.enter')
    await flushPromises()

    expect(api.updateProjectFileDescription).toHaveBeenCalledWith('project-1', 'file-1', '会议结论', 'token')
    expect(wrapper.text()).toContain('会议结论')
  })

  it('受控项目图档目录不显示普通文件上传和新建文件夹', async () => {
    const document = { id: 'document-1', projectId: 'project-1', folderId: 'folder-2', drawingNumber: 'A-001', name: '测试零件', fileName: 'A-001.SLDPRT', kind: 'Part', state: 'Work', revision: 'W1', rowVersion: 3 }
    const wrapper = mountLibrary([controlledFolder], [document])
    await flushPromises()
    expect(api.listProjectFiles).toHaveBeenCalledWith('project-1', undefined, false, 'token')
    expect(wrapper.text()).not.toContain('上传文件')
    expect(wrapper.text()).not.toContain('新建文件夹')
    expect(wrapper.find('input[aria-label="选择当前列表全部受控图档"]').exists()).toBe(true)
    await wrapper.get('input[aria-label="选择当前列表全部受控图档"]').setValue(true)
    expect((wrapper.get('input[aria-label="选择 A-001"]').element as HTMLInputElement).checked).toBe(true)
  })

  it('发布目录只读汇总审批发布流程生成的成品文件', async () => {
    const releaseFolder = { ...businessFolder, id: 'folder-release', folderKey: 'mechanical.release', templateKey: 'mechanical.release', name: '机械发布', purpose: 'Release', sortOrder: 30, effectiveAccess: 3 } satisfies ProjectFolder
    const stepFile = { ...file, id: 'file-step', folderId: 'folder-release', fileName: '7080113.00-01.step', currentVersion: { ...file.currentVersion, id: 'version-step', projectFileId: 'file-step', fileName: '7080113.00-01.step', fileLength: 301975 } }
    api.listProjectFiles.mockResolvedValue([file, stepFile])
    const wrapper = mountLibrary([releaseFolder])
    await flushPromises()

    expect(wrapper.text()).toContain('按发布范围自动汇总的发布成品')
    expect(wrapper.text()).toContain('7080113.00-01.step')
    expect(wrapper.text()).not.toContain('会议纪要.pdf')
    expect(wrapper.text()).not.toContain('上传文件')
    expect(wrapper.text()).not.toContain('新建文件夹')
    expect(wrapper.find('button[title="下载"]').exists()).toBe(true)
    expect(wrapper.find('button[title="重命名"]').exists()).toBe(false)
    expect(wrapper.find('button[title="删除"]').exists()).toBe(false)
  })

  it('管理员删除受控图档前显示影响检查和双重确认', async () => {
    const document = { id: 'document-1', projectId: 'project-1', folderId: 'folder-2', drawingNumber: 'A-001', name: '测试零件', fileName: 'A-001.SLDPRT', kind: 'Part', state: 'Work', revision: 'W1', rowVersion: 3, storedVersionCount: 2 }
    api.getControlledDocumentRecycleReadiness.mockResolvedValue({ document, canRecycle: true, blockers: [], storedVersionCount: 2, whereUsedCount: 0 })
    const wrapper = mountLibrary([controlledFolder], [document], true)
    await flushPromises()

    await wrapper.find('button[title="删除前检查"]').trigger('click')
    await flushPromises()

    expect(api.getControlledDocumentRecycleReadiness).toHaveBeenCalledWith('project-1', 'document-1', 'token')
    expect(wrapper.text()).toContain('进入回收站后30天内可恢复')
    expect(wrapper.text()).toContain('确认图号或文件名')
  })

  it('管理员可多选受控图档并批量移入回收站', async () => {
    const documents = [
      { id: 'document-1', projectId: 'project-1', folderId: 'folder-2', drawingNumber: 'A-001', name: '测试零件一', fileName: 'A-001.SLDPRT', kind: 'Part', state: 'Work', revision: 'W1', rowVersion: 3 },
      { id: 'document-2', projectId: 'project-1', folderId: 'folder-2', drawingNumber: 'A-002', name: '测试零件二', fileName: 'A-002.SLDPRT', kind: 'Part', state: 'Work', revision: 'W1', rowVersion: 5 },
    ]
    api.recycleControlledDocumentsBatch.mockResolvedValue({ recycledDocumentIds: ['document-1', 'document-2'], failures: [] })
    const wrapper = mountLibrary([controlledFolder], documents, true)
    await flushPromises()

    await wrapper.find('input[aria-label="选择当前列表全部受控图档"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text().includes('批量移入回收站'))!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('已选择 2 项受控图档')
    expect(wrapper.text()).toContain('允许部分成功')
  })

  it('本层直接含文件的文件夹使用主题色图标', async () => {
    const emptyFolder = { ...businessFolder, id: 'folder-empty', folderKey: 'empty', templateKey: 'empty', name: '空目录', sortOrder: 20 } satisfies ProjectFolder
    const wrapper = mountLibrary([businessFolder, emptyFolder])
    await flushPromises()

    const nodes = wrapper.findAll('.pdm-folder-node')
    const populated = nodes.find(node => node.text().includes('项目文件'))!
    const empty = nodes.find(node => node.text().includes('空目录'))!
    expect(populated.get('.pdm-folder-node__icon').classes()).toContain('is-populated')
    expect(empty.get('.pdm-folder-node__icon').classes()).not.toContain('is-populated')
  })

  it('业务目录在内容区显示直接子文件夹并可进入', async () => {
    const childFolder = { ...businessFolder, id: 'folder-child', folderKey: 'acceptance-items', templateKey: 'acceptance-items', parentFolderId: businessFolder.id, name: '验收记录', sortOrder: 20 } satisfies ProjectFolder
    api.listProjectFiles.mockResolvedValue([])
    const wrapper = mountLibrary([businessFolder, childFolder])
    await flushPromises()

    expect(wrapper.get('[aria-label="当前目录的子文件夹"]').text()).toContain('验收记录')
    expect(wrapper.text()).not.toContain('此目录暂无文件')
    await wrapper.get('button[aria-label="进入文件夹 验收记录"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('.pdm-folder-toolbar h2').text()).toBe('验收记录')
  })

  it('缺少上传编辑删除权限时只显示查看提示', async () => {
    const wrapper = mountLibrary([{ ...businessFolder, effectiveAccess: 3 }])
    await flushPromises()
    expect(wrapper.text()).not.toContain('上传文件')
    expect(wrapper.text()).not.toContain('新建文件夹')
    expect(wrapper.find('button[title="删除"]').exists()).toBe(false)
  })

  it('移动文件时使用目录树选择目标文件夹', async () => {
    const targetFolder = { ...businessFolder, id: 'folder-target', folderKey: 'purchase', templateKey: 'purchase', name: '采购清单', sortOrder: 20 } satisfies ProjectFolder
    const wrapper = mountLibrary([businessFolder, targetFolder])
    await flushPromises()

    await wrapper.find('button[title="移动"]').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('移动文件')
    expect(wrapper.find('.pdm-move-folder-tree').exists()).toBe(true)
    expect(wrapper.find('.pdm-move-folder-dialog .el-input').exists()).toBe(false)

    const trees = wrapper.findAllComponents({ name: 'ElTree' })
    await trees.at(-1)!.vm.$emit('current-change', targetFolder)
    await wrapper.findAll('button').find(button => button.text() === '确定')!.trigger('click')
    await flushPromises()

    expect(api.moveProjectFile).toHaveBeenCalledWith('project-1', 'file-1', 'folder-target', 'token')
  })
})
