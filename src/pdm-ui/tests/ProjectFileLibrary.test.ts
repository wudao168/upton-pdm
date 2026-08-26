import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectFileLibrary from '../src/components/ProjectFileLibrary.vue'
import type { ProjectFolder } from '../src/types'

const api = vi.hoisted(() => ({
  listProjectFiles: vi.fn(), uploadProjectFile: vi.fn(), createProjectFolder: vi.fn(), deleteProjectFolder: vi.fn(),
  renameProjectFolder: vi.fn(), moveProjectFolder: vi.fn(), renameProjectFile: vi.fn(), moveProjectFile: vi.fn(),
  deleteProjectFile: vi.fn(), restoreProjectFile: vi.fn(), listProjectFileVersions: vi.fn(), downloadProjectFile: vi.fn(),
}))
vi.mock('../src/api', () => api)

const businessFolder = { id: 'folder-1', rootProjectId: 'project-1', folderKey: 'project-files', templateKey: 'project-files', name: '项目文件', purpose: 'Standard', sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 31, permissions: [] } satisfies ProjectFolder
const controlledFolder = { ...businessFolder, id: 'folder-2', name: 'P700001-0', purpose: 'ProjectContainer', effectiveAccess: 127 } satisfies ProjectFolder
const file = { id: 'file-1', rootProjectId: 'project-1', folderId: 'folder-1', fileName: '会议纪要.pdf', createdBy: 'engineer', createdAt: '2026-08-26T00:00:00Z', updatedBy: 'engineer', updatedAt: '2026-08-26T00:00:00Z', currentVersion: { id: 'version-1', projectFileId: 'file-1', versionNumber: 1, fileName: '会议纪要.pdf', fileLength: 1024, sha256: 'A'.repeat(64), uploadedBy: 'engineer', uploadedAt: '2026-08-26T00:00:00Z' } }

function mountLibrary(folders: ProjectFolder[] = [businessFolder]) {
  return mount(ProjectFileLibrary, { props: { projectId: 'project-1', token: 'token', folders, documents: [], users: [{ username: 'engineer', displayName: '工程师甲', role: 'Engineer', isActive: true }], roles: [], administrator: false, pending: false, onUpdatePermissions: vi.fn(), onReload: vi.fn() }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })
}

describe('ProjectFileLibrary', () => {
  beforeEach(() => { vi.clearAllMocks(); api.listProjectFiles.mockResolvedValue([file]) })

  it('在普通业务目录按目录权限显示文件操作并使用人名', async () => {
    const wrapper = mountLibrary()
    await flushPromises()
    expect(api.listProjectFiles).toHaveBeenCalledWith('project-1', 'folder-1', false, 'token')
    expect(wrapper.text()).toContain('上传文件')
    expect(wrapper.text()).toContain('新建文件夹')
    expect(wrapper.text()).toContain('会议纪要.pdf')
    expect(wrapper.text()).toContain('工程师甲')
    expect(wrapper.find('button[title="下载"]').exists()).toBe(true)
    expect(wrapper.find('button[title="删除"]').exists()).toBe(true)
  })

  it('受控项目图档目录不显示普通文件上传和新建文件夹', async () => {
    const wrapper = mountLibrary([controlledFolder])
    await flushPromises()
    expect(api.listProjectFiles).not.toHaveBeenCalled()
    expect(wrapper.text()).not.toContain('上传文件')
    expect(wrapper.text()).not.toContain('新建文件夹')
    expect(wrapper.text()).toContain('受控图档由SolidWorks存档')
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
