import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectOplManager from '../src/components/ProjectOplManager.vue'
import type { ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({ readProjectOpl: vi.fn(), saveProjectOpl: vi.fn(), readOplAttachment: vi.fn(), completeProjectOpl: vi.fn() }))
vi.mock('../src/api', () => ({ ...api, PdmApiError: class extends Error { status = 409 } }))
let wrapper: ReturnType<typeof mount>
const issue = { id: 'i1', projectId: 'p1', number: 1, description: '相机支架松动', priority: '高', status: '待处理', owner: '', dueDate: '2020-01-01', solution: '', createdBy: 'admin', createdAt: '2026-10-12T00:00:00Z', updatedBy: 'admin', rowVersion: 3, attachments: [] }
async function setup(project = { id: 'p1', code: 'P700012', name: 'TEST 4' } as ProjectSummary) {
  wrapper = mount(ProjectOplManager, { attachTo: document.body, props: { project, token: 'token' }, global: { plugins: [ElementPlus], stubs: { PdfDrawingViewer: { props: ['url'], template: '<div class="pdf-preview-stub" :data-url="url" />' } } } })
  await flushPromises()
}
const click = async (label: string, root: ParentNode = document.body) => { (Array.from(root.querySelectorAll('button')).find(b => b.textContent?.trim() === label) as HTMLButtonElement).click(); await flushPromises() }
beforeEach(() => { vi.clearAllMocks(); api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: true, canComplete: true }], people: [] }); api.saveProjectOpl.mockResolvedValue(issue) })
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ''; vi.unstubAllGlobals() })
describe('OPL drawer', () => {
  it('completes a row without opening its drawer and displays the server completion date', async () => {
    api.completeProjectOpl.mockResolvedValue({ ...issue, status: '已完成', completedAt: '2026-10-13T00:01:00Z', rowVersion: 4 })
    await setup(); await click('完成', wrapper.element)
    expect(api.completeProjectOpl).toHaveBeenCalledWith('p1', 'i1', 3, 'token')
    expect(wrapper.findComponent({ name: 'ElDrawer' }).props('modelValue')).toBe(false)
    expect(wrapper.text()).not.toContain('相机支架松动')
    expect(wrapper.text()).toContain('已完成 1')
    wrapper.findAllComponents({ name: 'ElSelect' })[0]!.vm.$emit('update:modelValue', '')
    await flushPromises()
    expect(wrapper.text()).toContain('2026-10-13')
    expect(wrapper.text()).toContain('相机支架松动')
  })
  it('hides completion from unauthorized users and keeps a row when completion fails', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: false, canComplete: false }], people: [] })
    await setup()
    expect(wrapper.findAll('button').some(button => button.text() === '完成')).toBe(false)
    wrapper.unmount()
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: true, canComplete: true }], people: [] })
    api.completeProjectOpl.mockRejectedValue(new Error('network failure'))
    await setup(); await click('完成', wrapper.element)
    expect(wrapper.text()).toContain('相机支架松动')
    expect(wrapper.findComponent({ name: 'ElDrawer' }).props('modelValue')).toBe(false)
  })
  it('previews an image attachment without editing the row', async () => {
    const NativeURL = URL
    vi.stubGlobal('URL', class extends NativeURL { static createObjectURL = vi.fn(()=>'blob:opl-image'); static revokeObjectURL = vi.fn() })
    api.readOplAttachment.mockResolvedValue(new Blob(['image'], {type:'image/png'}))
    api.readProjectOpl.mockResolvedValue({rows:[{issue:{...issue, attachments:[{id:'image', name:'现场.png', length:5}]}, canEdit:true, canComplete:true}],people:[]})
    await setup()
    await wrapper.get('[aria-label="查看附件 现场.png"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('img[alt="问题附件预览"]')?.getAttribute('src')).toBe('blob:opl-image')
    expect(wrapper.findComponent({name:'ElDrawer'}).props('modelValue')).toBe(false)
  })
  it('opens an editable row on click and respects read-only permissions', async () => {
    await setup()
    expect(wrapper.get('tbody tr td').text()).toBe('1')
    await wrapper.get('tbody tr').trigger('click')
    expect(document.body.textContent).toContain('编辑开口问题')
    wrapper.unmount()
    api.readProjectOpl.mockResolvedValue({ rows: [{issue, canEdit:false, canComplete:false}], people:[] })
    await setup()
    await wrapper.get('tbody tr').trigger('click')
    expect(document.body.textContent).toContain('查看开口问题')
    expect(document.body.querySelector('textarea')?.disabled).toBe(true)
  })
  it('previews PDF attachments without opening the row editor and releases the URL', async () => {
    const NativeURL = URL
    const revoke = vi.fn()
    vi.stubGlobal('URL', class extends NativeURL { static createObjectURL = vi.fn(()=>'blob:opl-pdf'); static revokeObjectURL = revoke })
    api.readOplAttachment.mockResolvedValue(new Blob(['%PDF-1.4']))
    api.readProjectOpl.mockResolvedValue({rows:[{issue:{...issue, attachments:[{id:'pdf', name:'问题.PDF', length:8}]}, canEdit:true, canComplete:true}],people:[]})
    await setup()
    await wrapper.get('[aria-label="查看附件 问题.PDF"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('.pdf-preview-stub')?.getAttribute('data-url')).toBe('blob:opl-pdf')
    expect(wrapper.findComponent({ name: 'ElDrawer' }).props('modelValue')).toBe(false)
    expect(api.readOplAttachment).toHaveBeenCalledWith('p1','i1','pdf','token')
    wrapper.unmount()
    expect(revoke).toHaveBeenCalledWith('blob:opl-pdf')
  })
  it('filters the shared list to the selected child and defaults new issues to that optional child', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [
      { issue: { ...issue, id: 'whole', description: '项目整体问题' }, canEdit: true, canComplete: true },
      { issue: { ...issue, id: 'child', description: '当前子项目问题', relatedProjectId: 'child-1' }, canEdit: true, canComplete: true },
      { issue: { ...issue, id: 'other', description: '其他子项目问题', relatedProjectId: 'child-2' }, canEdit: true, canComplete: true },
    ], people: [], rootProject: { id: 'root', code: 'P700012', name: '主设备' }, subprojects: [{ id: 'child-1', code: 'P700012-1', name: '子设备' }] })
    await setup({ id: 'child-1', parentProjectId: 'root', code: 'P700012-1', name: '子设备' } as ProjectSummary)
    expect(wrapper.text()).toContain('当前子项目问题')
    expect(wrapper.text()).not.toContain('项目整体问题')
    expect(wrapper.text()).not.toContain('其他子项目问题')
    await click('新增问题')
    const input = document.body.querySelector('textarea')!
    input.value = '子项目新增'; input.dispatchEvent(new Event('input', { bubbles: true })); await flushPromises()
    await click('保存问题')
    expect(api.saveProjectOpl).toHaveBeenCalledWith('child-1', null, expect.objectContaining({ relatedProjectId: 'child-1' }), [], 'token')
    wrapper.unmount()
    await setup()
    expect(wrapper.text()).toContain('项目整体问题')
    expect(wrapper.text()).toContain('当前子项目问题')
    expect(wrapper.text()).toContain('其他子项目问题')
  })
  it('shows the main project and offers optional children when opened from a child', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue: { ...issue, relatedProjectId: 'child-1' }, canEdit: true, canComplete: true }], people: [], rootProject: { id: 'root-1', code: 'P700012', name: '主设备' }, subprojects: [{ id: 'child-1', code: 'P700012-1', name: '子设备' }] })
    await setup(); await click('编辑')
    const projectInput = document.body.querySelector('input[disabled]') as HTMLInputElement
    expect(projectInput.value).toBe('P700012 · 主设备')
    expect(document.body.textContent).toContain('子项目')
    await click('保存问题')
    expect(api.saveProjectOpl).toHaveBeenCalledWith('p1', 'i1', expect.objectContaining({ relatedProjectId: 'child-1' }), [], 'token')
  })
  it('only mounts the form on demand, validates required description, and saves server version', async () => {
    await setup()
    expect(document.body.querySelector('textarea')).toBeNull()
    await click('新增问题')
    await click('保存问题')
    expect(api.saveProjectOpl).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain('请填写问题描述。')
    const input = document.body.querySelector('textarea')!
    input.value = '现场问题'; input.dispatchEvent(new Event('input', { bubbles: true })); await flushPromises()
    await click('保存问题')
    expect(api.saveProjectOpl).toHaveBeenCalledWith('p1', null, expect.objectContaining({ description: '现场问题', expectedRowVersion: 0, relatedProjectId: null }), [], 'token')
  })
  it('edits through the right drawer and retains input when saving fails', async () => {
    await setup(); await click('编辑')
    expect(document.body.querySelector('.el-drawer.rtl')).not.toBeNull()
    expect(document.body.querySelector('textarea')?.value).toBe('相机支架松动')
    api.saveProjectOpl.mockRejectedValue(new Error('network failure'))
    await click('保存问题')
    expect(api.saveProjectOpl).toHaveBeenCalledWith('p1', 'i1', expect.objectContaining({ expectedRowVersion: 3 }), [], 'token')
    expect(document.body.querySelector('textarea')?.value).toBe('相机支架松动')
    expect(document.body.textContent).toContain('填写内容已保留')
    expect(document.body.textContent).not.toContain('network failure')
  })
})
