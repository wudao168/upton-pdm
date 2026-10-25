import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MaterialImagePreview from '../src/components/MaterialImagePreview.vue'
import type { MaterialAttachment } from '../src/types'

const api = vi.hoisted(() => ({ listMaterialAttachments: vi.fn(), materialAttachmentObjectUrl: vi.fn() }))
vi.mock('../src/api', () => api)
const image = (id: string) => ({ id, materialId: 'material', kind: 'CoverImage', originalFileName: `${id}.png` } as MaterialAttachment)
let wrapper: ReturnType<typeof mount> | undefined
const open = (props = {}) => wrapper = mount(MaterialImagePreview, { props: { modelValue: true, materialId: 'material', token: 'token', ...props }, global: { plugins: [ElementPlus] } })

describe('MaterialImagePreview', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.listMaterialAttachments.mockResolvedValue([image('first'), image('second')])
    api.materialAttachmentObjectUrl.mockImplementation(async (_material: string, id: string) => `blob:${id}`)
  })
  afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })

  it('loads original images and starts at the selected cover, then browses all pictures', async () => {
    const revoke = vi.spyOn(URL, 'revokeObjectURL')
    open({ initialId: 'second' }); await flushPromises()
    expect(api.listMaterialAttachments).toHaveBeenCalledWith('material', 'token', 'CoverImage')
    expect(api.materialAttachmentObjectUrl).toHaveBeenCalledWith('material', 'second', 'token')
    expect(document.querySelector('.material-image-stage img')?.getAttribute('src')).toBe('blob:second')
    expect(document.querySelector('.material-image-navigation')?.textContent).toContain('2 / 2')
    const previous = Array.from(document.querySelectorAll('button')).find(button => button.textContent === '上一张')!
    previous.click(); await flushPromises()
    expect(api.materialAttachmentObjectUrl).toHaveBeenLastCalledWith('material', 'first', 'token')
    expect(revoke).toHaveBeenCalledWith('blob:second')
    expect(document.querySelector('.material-image-navigation')?.textContent).toContain('1 / 2')
    expect(previous.disabled).toBe(true)
  })

  it('uses editor attachment metadata and excludes documents from the image gallery', async () => {
    open({ attachments: [image('one'), { ...image('document'), kind: 'Document' }] }); await flushPromises()
    expect(api.listMaterialAttachments).not.toHaveBeenCalled()
    expect(document.querySelector('.material-image-navigation')?.textContent).toContain('1 / 1')
    expect(Array.from(document.querySelectorAll('button')).find(button => button.textContent === '下一张')?.disabled).toBe(true)
  })

  it('releases a late original image after the preview closes', async () => {
    let finish!: (url: string) => void
    api.materialAttachmentObjectUrl.mockImplementationOnce(() => new Promise<string>(resolve => { finish = resolve }))
    const revoke = vi.spyOn(URL, 'revokeObjectURL')
    open(); await flushPromises()
    await wrapper!.setProps({ modelValue: false }); finish('blob:late'); await flushPromises()
    expect(revoke).toHaveBeenCalledWith('blob:late')
    expect(document.querySelector('.material-image-stage img')).toBeNull()
  })

  it('does not fetch an image after closing while the attachment list is pending', async () => {
    let finish!: (items: MaterialAttachment[]) => void
    api.listMaterialAttachments.mockImplementationOnce(() => new Promise<MaterialAttachment[]>(resolve => { finish = resolve }))
    open(); await wrapper!.setProps({ modelValue: false }); finish([image('late')]); await flushPromises()
    expect(api.materialAttachmentObjectUrl).not.toHaveBeenCalled()
  })

  it('shows a Chinese error and retries the failed original', async () => {
    api.materialAttachmentObjectUrl.mockRejectedValueOnce(new Error('Failed to fetch'))
    open(); await flushPromises()
    expect(document.querySelector('[role="alert"]')?.textContent).toContain('无法连接服务器')
    Array.from(document.querySelectorAll('button')).find(button => button.textContent === '重新加载')!.click(); await flushPromises()
    expect(document.querySelector('[role="alert"]')).toBeNull()
    expect(document.querySelector('.material-image-stage img')?.getAttribute('src')).toBe('blob:first')
  })
})
