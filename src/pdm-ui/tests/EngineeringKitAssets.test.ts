import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import EngineeringKitAssets from '../src/components/EngineeringKitAssets.vue'
import type { EngineeringKit } from '../src/types'
const api = vi.hoisted(() => ({ listMaterialAttachments: vi.fn(), uploadMaterialAttachment: vi.fn(), downloadMaterialAttachment: vi.fn(), saveEngineeringKitLibraryMetadata: vi.fn(), materialAttachmentObjectUrl: vi.fn() }))
vi.mock('../src/api', () => api)
const kit: EngineeringKit = { id:'kit-1',code:'UKIT-000001',model:'KIT-MODEL',name:'支架组件',brand:'UPTON',description:'套件说明',
  revisions:[],createdBy:'admin',createdAt:'',updatedBy:'admin',updatedAt:'',rowVersion:2,referenceCount:2 }
const mountAssets = (canEdit = true) => mount(EngineeringKitAssets, {props:{kit,token:'token',canEdit,canManage:canEdit},global:{plugins:[ElementPlus],stubs:{teleport:true}}})
beforeEach(() => {
  vi.clearAllMocks(); api.listMaterialAttachments.mockResolvedValue([]); api.uploadMaterialAttachment.mockResolvedValue({id:'attachment-1'}); api.downloadMaterialAttachment.mockResolvedValue(undefined)
  api.materialAttachmentObjectUrl.mockResolvedValue('data:image/png;base64,fixture')
  api.saveEngineeringKitLibraryMetadata.mockImplementation(async (_id, input) => ({ ...kit, description: input.remark, selectionAdvice: input.selectionAdvice,
    referencePrice: input.referencePrice, isRecommended: input.isRecommended, coverImageAttachmentId: input.coverImageAttachmentId, rowVersion: input.expectedRowVersion + 1 }))
})
describe('套件资料维护', () => {
  it('uploads against the kit identity, reloads attachments and keeps its released composition untouched', async () => {
    const w=mountAssets();await flushPromises()
    const file = new File(['step-data'],'kit.step')
    const input = w.get('[aria-label="上传套件3D附件"]')
    Object.defineProperty(input.element,'files',{value:[file]})
    await input.trigger('change');await flushPromises()
    expect(api.uploadMaterialAttachment).toHaveBeenCalledWith(kit.id,'Model3D',file,'token',expect.any(Function))
    expect(api.listMaterialAttachments).toHaveBeenCalledTimes(2);expect(w.emitted('changed')).toHaveLength(1)
    expect(kit.rowVersion).toBe(2);expect(kit.revisions).toEqual([]);w.unmount()
  })
  it('retains already uploaded files when a later file fails', async () => {
    api.uploadMaterialAttachment.mockResolvedValueOnce({id:'a'}).mockRejectedValueOnce(new Error('第二个资料上传失败，请重试。'))
    const w=mountAssets();await flushPromises();const input=w.get('[aria-label="上传套件资料附件"]')
    Object.defineProperty(input.element,'files',{value:[new File(['a'],'a.pdf'),new File(['b'],'b.pdf')]})
    await input.trigger('change');await flushPromises()
    expect(w.get('[role="alert"]').text()).toContain('第二个资料上传失败')
    expect(api.listMaterialAttachments).toHaveBeenCalledTimes(2);expect(w.emitted('changed')).toHaveLength(1);w.unmount()
  })
  it('lets view-only users download without showing upload or category changes', async () => {
    const attachment={id:'a',kind:'Document',originalFileName:'说明.pdf'}
    api.listMaterialAttachments.mockResolvedValue([attachment])
    const w=mountAssets(false);await flushPromises()
    expect(w.findAll('input[type=file]').every(input => input.attributes('disabled') !== undefined)).toBe(true);expect(w.text()).not.toContain('调整分类')
    await w.get('[aria-label="下载 说明.pdf"]').trigger('click');await flushPromises()
    expect(api.downloadMaterialAttachment).toHaveBeenCalledWith(kit.id,attachment,'token');expect(api.uploadMaterialAttachment).not.toHaveBeenCalled();w.unmount()
  })
  it('uses the material editor while keeping kit identity read-only and saves only library metadata', async () => {
    const w = mountAssets(); await flushPromises()
    const field = (label: string) => w.findAll('.el-form-item').find(item => item.find('.el-form-item__label').exists() && item.find('.el-form-item__label').text() === label)!.get('input')
    for (const label of ['料号', '名称', '型号', '品牌']) expect(field(label).attributes('disabled')).toBeDefined()
    expect((field('料号').element as HTMLInputElement).value).toBe(kit.code)
    expect((field('名称').element as HTMLInputElement).value).toBe(kit.name)
    await field('备注').setValue('维护后的备注'); await field('选型建议').setValue('适用于真空系统')
    await w.findAll('button').find(button => button.text() === '保存修改')!.trigger('click'); await flushPromises()
    expect(api.saveEngineeringKitLibraryMetadata).toHaveBeenCalledWith(kit.id, expect.objectContaining({ remark:'维护后的备注',selectionAdvice:'适用于真空系统',expectedRowVersion:2 }), 'token')
    const input = api.saveEngineeringKitLibraryMetadata.mock.calls[0]![1]
    expect(input).not.toHaveProperty('name'); expect(input).not.toHaveProperty('components'); expect(w.emitted('close')).toHaveLength(1)
    w.unmount()
  })
  it('uploads a picture using kit identity and selects the first cover without saving draft text', async () => {
    const w = mountAssets(); await flushPromises()
    const input = w.get('[aria-label="选择套件图片"]')
    Object.defineProperty(input.element, 'files', { value: [new File(['png'], '泵.png', { type: 'image/png' })] })
    await input.trigger('change'); await flushPromises()
    expect(api.uploadMaterialAttachment).toHaveBeenCalledWith(kit.id, 'CoverImage', expect.any(File), 'token', expect.any(Function))
    expect(api.saveEngineeringKitLibraryMetadata).toHaveBeenCalledWith(kit.id, expect.objectContaining({ remark:kit.description, coverImageAttachmentId:'attachment-1', expectedRowVersion:2 }), 'token')
    expect(w.emitted('changed')).toHaveLength(1); w.unmount()
  })
  it('rejects a fourth picture and preserves entered text after a version conflict', async () => {
    const w = mountAssets(); await flushPromises()
    const input = w.get('[aria-label="选择套件图片"]')
    Object.defineProperty(input.element, 'files', { value: Array.from({length:4}, (_, i) => new File(['png'], i+'.png')) })
    await input.trigger('change'); await flushPromises()
    expect(w.text()).toContain('最多上传3张图片'); expect(api.uploadMaterialAttachment).not.toHaveBeenCalled()
    api.saveEngineeringKitLibraryMetadata.mockRejectedValueOnce(new Error('套件已被其他用户修改，请刷新后重试。'))
    const remark = w.findAll('.el-form-item').find(item => item.find('.el-form-item__label').exists() && item.find('.el-form-item__label').text() === '备注')!.get('input')
    await remark.setValue('保留的输入')
    await w.findAll('button').find(button => button.text() === '保存修改')!.trigger('click'); await flushPromises()
    expect(w.text()).toContain('套件已被其他用户修改'); expect((remark.element as HTMLInputElement).value).toBe('保留的输入')
    expect(w.emitted('close')).toBeUndefined(); w.unmount()
  })
})
