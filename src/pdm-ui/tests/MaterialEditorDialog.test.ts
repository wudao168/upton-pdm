import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MaterialEditorDialog from '../src/components/MaterialEditorDialog.vue'
import type { MaterialAttachment, SaveMaterialInput } from '../src/types'

const api = vi.hoisted(() => ({ materialAttachmentObjectUrl: vi.fn(), listMaterialAttachments: vi.fn(), deleteMaterialAttachment: vi.fn() }))
vi.mock('../src/api', () => api)
const picture = (id: string) => ({ id, materialId: 'material', kind: 'CoverImage', originalFileName: `${id}.png` } as MaterialAttachment)
let wrapper: ReturnType<typeof mount> | undefined
function open(extra = {}) {
  return wrapper = mount(MaterialEditorDialog, { attachTo: document.body, props: { modelValue: true, editingId: 'material', token: 'token', form: { name: '测试料品' } as SaveMaterialInput, categories: [], attachments: [], saving: false, materialCodePlaceholder: '', canDeleteAttachments: true, ...extra }, global: { plugins: [ElementPlus] } })
}
function selectFiles(count: number) {
  const input = document.querySelector<HTMLInputElement>('input[aria-label="选择料品图片"]')!
  Object.defineProperty(input, 'files', { configurable: true, value: Array.from({ length: count }, (_, index) => new File(['image'], `${index}.png`, { type: 'image/png' })) })
  input.dispatchEvent(new Event('change'))
}
describe('MaterialEditorDialog attachments', () => {
  beforeEach(() => { vi.clearAllMocks(); api.materialAttachmentObjectUrl.mockImplementation(async (_id: string, id: string) => `blob:${id}`); vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never); api.deleteMaterialAttachment.mockReset().mockResolvedValue(undefined) })
  afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })

  it('removes the numbering explanation and places pictures above the form and file lists below it', async () => {
    const model = { ...picture('model'), kind: 'Model3D', originalFileName: '模型.SLDPRT' }
    const document = { ...picture('document'), kind: 'Document', originalFileName: '产品说明.pdf' }
    open({ attachments: [model, document, picture('image')] }); await flushPromises()
    expect(documentBody().textContent).not.toContain('保存时从PLM分类当前基线')
    expect(documentBody().querySelector('.material-editor-grid .material-attachment-input')).toBeNull()
    const assets = documentBody().querySelector('.material-editor-assets')!
    expect([...assets.querySelectorAll('section')].map(section => section.getAttribute('aria-label'))).toEqual(['3D附件', '资料附件'])
    const images = documentBody().querySelector('.material-editor-images')!
    const form = documentBody().querySelector('.material-editor-grid')!
    expect(images.compareDocumentPosition(form) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(form.compareDocumentPosition(assets) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(assets.textContent).toContain('模型.SLDPRT')
    expect(assets.textContent).toContain('产品说明.pdf')
    ;(documentBody().querySelector('button[aria-label="下载 产品说明.pdf"]') as HTMLButtonElement).click()
    expect(wrapper!.emitted('downloadAttachment')?.[0]?.[0]).toEqual(document)
  })

  it('rejects the whole batch when existing pictures plus selected files exceed three', async () => {
    open({ attachments: [picture('one'), picture('two')] }); await flushPromises()
    selectFiles(2); await flushPromises()
    expect(wrapper!.emitted('attachmentFiles')).toBeUndefined()
    expect(documentBody().querySelector('[role="alert"]')?.textContent).toContain('当前已有2张，本次选择2张')
    selectFiles(1); await flushPromises()
    expect(wrapper!.emitted('attachmentFiles')).toHaveLength(1)
    expect(documentBody().querySelector('[role="alert"]')).toBeNull()
  })

  it('marks the selected cover and requests a switch to another existing image', async () => {
    const second = picture('two')
    open({ attachments: [picture('one'), second], coverAttachmentId: 'one' }); await flushPromises()
    expect(documentBody().querySelectorAll('.is-cover')).toHaveLength(1)
    ;(documentBody().querySelector('button[aria-label="将 two.png 设为封面"]') as HTMLButtonElement).click()
    expect(wrapper!.emitted('setCover')?.[0]?.[0]).toEqual(second)
    await wrapper!.setProps({ changingCover: true })
    expect((documentBody().querySelector('button[aria-label="将 two.png 设为封面"]') as HTMLButtonElement).disabled).toBe(true)
    expect((documentBody().querySelector('.material-image-add') as HTMLButtonElement).disabled).toBe(true)
  })

  it('preserves historical pictures beyond the limit while preventing new uploads', async () => {
    open({ attachments: ['one', 'two', 'three', 'four'].map(picture) }); await flushPromises()
    expect(documentBody().querySelectorAll('.material-image-card')).toHaveLength(4)
    expect(documentBody().querySelector('.material-image-add')).toBeNull()
    expect(documentBody().textContent).toContain('已有4张历史图片，继续保留')
    expect(wrapper!.emitted('setCover')).toBeUndefined()
    selectFiles(1); await flushPromises()
    expect(wrapper!.emitted('attachmentFiles')).toBeUndefined()
  })

  it('allows read-only attachment downloads while disabling uploads and cover changes', async () => {
    open({ canEdit: false, attachments: [picture('one'), { ...picture('file'), kind: 'Document' }] }); await flushPromises()
    expect((documentBody().querySelector('.material-image-add') as HTMLButtonElement).disabled).toBe(true)
    expect((documentBody().querySelector('button[aria-label="将 one.png 设为封面"]') as HTMLButtonElement).disabled).toBe(true)
    const download = documentBody().querySelector('button[aria-label="下载 file.png"]') as HTMLButtonElement
    expect(download.disabled).toBe(false)
    download.click(); expect(wrapper!.emitted('downloadAttachment')).toHaveLength(1)
  })

  it('releases thumbnails returned after the editor closes', async () => {
    let finish!: (url: string) => void
    api.materialAttachmentObjectUrl.mockImplementationOnce(() => new Promise<string>(resolve => { finish = resolve }))
    const revoke = vi.spyOn(URL, 'revokeObjectURL')
    open({ attachments: [picture('one')] }); await flushPromises()
    await wrapper!.setProps({ modelValue: false }); finish('blob:late'); await flushPromises()
    expect(revoke).toHaveBeenCalledWith('blob:late')
  })

  it('confirms the selected attachment before deleting and locks other mutations until completion', async () => {
    let complete!: () => void
    api.deleteMaterialAttachment.mockImplementationOnce(() => new Promise<void>(resolve => { complete = resolve }))
    const file = { ...picture('file'), kind: 'Document' }
    open({ attachments: [file, { ...picture('model'), kind: 'Model3D' }] }); await flushPromises()
    ;(documentBody().querySelector('button[aria-label="删除 file.png"]') as HTMLButtonElement).click(); await flushPromises()
    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('file.png'), '删除附件', expect.any(Object))
    expect(api.deleteMaterialAttachment).toHaveBeenCalledWith('material', 'file', 'token')
    expect((documentBody().querySelector('button[aria-label="删除 model.png"]') as HTMLButtonElement).disabled).toBe(true)
    expect(wrapper!.emitted('attachmentDeleted')).toBeUndefined()
    complete(); await flushPromises()
    expect(wrapper!.emitted('attachmentDeleted')?.[0]?.[0]).toEqual(file)
  })

  it('keeps the attachment when deletion is cancelled or fails', async () => {
    const file = { ...picture('file'), kind: 'Document' }
    open({ attachments: [file] }); await flushPromises()
    vi.mocked(ElMessageBox.confirm).mockRejectedValueOnce('cancel')
    const button = documentBody().querySelector('button[aria-label="删除 file.png"]') as HTMLButtonElement
    button.click(); await flushPromises()
    expect(api.deleteMaterialAttachment).not.toHaveBeenCalled()
    api.deleteMaterialAttachment.mockRejectedValueOnce(new Error('附件删除失败，请刷新后重试。'))
    button.click(); await flushPromises()
    expect(wrapper!.emitted('attachmentDeleted')).toBeUndefined()
    expect(documentBody().textContent).toContain('附件删除失败')
    expect(button.disabled).toBe(false)
  })

  it('hides deletion when permission or the entry capability is absent', async () => {
    open({ canEdit: false, attachments: [{ ...picture('file'), kind: 'Document' }] }); await flushPromises()
    expect(documentBody().querySelector('button[aria-label="删除 file.png"]')).toBeNull()
    await wrapper!.setProps({ canEdit: true, canDeleteAttachments: false }); await flushPromises()
    expect(documentBody().querySelector('button[aria-label="删除 file.png"]')).toBeNull()
  })
})
function documentBody() { return globalThis.document.body }
