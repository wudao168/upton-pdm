import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import StandardMaterialDrawings from '../src/components/StandardMaterialDrawings.vue'
import BomFilesDialog from '../src/components/BomFilesDialog.vue'
const api = vi.hoisted(() => ({ listMaterialAttachments: vi.fn(), uploadMaterialAttachment: vi.fn(), enableMaterialDrawing: vi.fn(),
  downloadMaterialAttachment: vi.fn(), materialAttachmentObjectUrl: vi.fn(), readBomFiles: vi.fn(), adoptBomLibraryFiles: vi.fn(),
  removeBomFile: vi.fn(), uploadBomFile: vi.fn(), readBomFileBlob: vi.fn() }))
vi.mock('../src/api', () => api)
afterEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks() })
describe('标准物料图纸版本与BOM资料', () => {
  it('上传自动升版后仍保留原启用版本，启用需要标准化明确选择', async () => {
    let files = [{ id: 'v1', materialId: 'm1', kind: 'Drawing2D', originalFileName: '原图.pdf', versionNumber: 1, isEnabled: true, uploadedBy: '标准化', uploadedAt: '2026-10-29T10:00:00Z' }]
    api.listMaterialAttachments.mockImplementation(async () => files)
    api.uploadMaterialAttachment.mockImplementation(async () => { const uploaded = { ...files[0]!, id: 'v2', versionNumber: 2, originalFileName: '新图.pdf', isEnabled: false }; files = [...files, uploaded]; return uploaded })
    api.enableMaterialDrawing.mockImplementation(async () => { files = files.map(file => ({ ...file, isEnabled: file.id === 'v2' })); return files[1] })
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(StandardMaterialDrawings, { attachTo: document.body, props: { materialId: 'm1', materialCode: 'STD-001', kind: 'Drawing2D', token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const input = wrapper.get('input[type=file]')
    Object.defineProperty(input.element, 'files', { value: [new File(['pdf'], '新图.pdf')], configurable: true })
    await input.trigger('change'); await flushPromises()
    expect(api.uploadMaterialAttachment).toHaveBeenCalledWith('m1', 'Drawing2D', expect.any(File), 'token', expect.any(Function))
    expect(api.enableMaterialDrawing).not.toHaveBeenCalled()
    expect(wrapper.get('.el-dialog__body > p > strong').text()).toBe('V01')
    await wrapper.findAll('button').find(button => button.text() === '启用')!.trigger('click'); await flushPromises()
    expect(api.enableMaterialDrawing).toHaveBeenCalledWith('m1', 'v2', 'v1', 'token')
    expect(wrapper.get('.el-dialog__body > p > strong').text()).toBe('V02')
    expect(wrapper.text()).toContain('原图.pdf')
    expect(wrapper.findAll('button').some(button => button.text() === '删除')).toBe(false)
    wrapper.unmount()
  })
  it('历史BOM只读取其固定文件版本，不出现更新引用和上传入口', async () => {
    const file = { id: 'old', materialId: 'm1', kind: 'Drawing2D', fileName: '历史V01.pdf', versionNumber: 1, fileLength: 10, sha256: 'A', uploadedBy: '标准化', uploadedAt: '', source: '标准物料', purpose: '图纸' }
    api.readBomFiles.mockResolvedValue({ files: [file], libraryUpdateAvailable: false, rowVersion: 0 })
    const wrapper = mount(BomFilesDialog, { attachTo: document.body, props: { projectId: 'p1', row: { id: 'b1', sequence: 1, drawingNumber: 'STD-001', name: '标准件', quantity: 1, unit: '件', revision: 'V01', complete: true, files: [file] }, bomVersionId: 'released-v1', token: 'token', canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(api.readBomFiles).toHaveBeenCalledWith('p1', 'b1', 'token', 'released-v1')
    expect(wrapper.text()).toContain('历史V01.pdf')
    expect(wrapper.find('input[type=file]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('采用当前启用版本及资料')
    wrapper.unmount()
  })
})
