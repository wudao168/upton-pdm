import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectOplManager from '../src/components/ProjectOplManager.vue'
import type { ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({ readProjectOpl: vi.fn(), saveProjectOpl: vi.fn(), readOplAttachment: vi.fn(), completeProjectOpl: vi.fn(), getProjectExportMetadata: vi.fn() }))
vi.mock('../src/api', () => ({ ...api, PdmApiError: class extends Error { status = 409 } }))
const exporter = vi.hoisted(() => ({ downloadOplWorkbook: vi.fn() }))
vi.mock('../src/oplWorkbook', () => exporter)
let wrapper: ReturnType<typeof mount>
const issue = { id: 'i1', projectId: 'p1', number: 1, description: '相机支架松动', priority: '高', status: '待处理', owner: '', dueDate: '2020-01-01', solution: '', createdBy: 'admin', createdAt: '2026-10-12T00:00:00Z', updatedBy: 'admin', rowVersion: 3, attachments: [] }
async function setup(project = { id: 'p1', code: 'P700012', name: 'TEST 4' } as ProjectSummary) {
  wrapper = mount(ProjectOplManager, { attachTo: document.body, props: { project, token: 'token' }, global: { plugins: [ElementPlus], stubs: { PdfDrawingViewer: { props: ['url'], template: '<div class="pdf-preview-stub" :data-url="url" />' } } } })
  await flushPromises()
}
const click = async (label: string, root: ParentNode = document.body) => { (Array.from(root.querySelectorAll('button')).find(b => b.textContent?.trim() === label) as HTMLButtonElement).click(); await flushPromises() }
beforeEach(() => { vi.clearAllMocks(); api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: true, canComplete: true }], people: [] }); api.saveProjectOpl.mockResolvedValue(issue); api.getProjectExportMetadata.mockResolvedValue({title:'项目资料',company:'公司',scope:'当前项目',versionLabel:'版本',version:'—',exportedAt:'2026-10-14T12:00:00+08:00',exportedBy:'管理员',fields:[]}) })
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ''; vi.unstubAllGlobals() })
describe('OPL drawer', () => {
  it('lets the user send an update or completion to the group and displays delivery failures without losing the save', async () => {
    api.saveProjectOpl.mockResolvedValue({ ...issue, notification: { sent: false, message: '群通知未发送。问题已保存，无需重复提交。' } })
    api.completeProjectOpl.mockResolvedValue({ ...issue, status: '已完成', notification: { sent: true, message: '群通知已发送。' } })
    await setup(); await click('编辑')
    const checkbox = Array.from(document.body.querySelectorAll('label.el-checkbox')).find(label => label.textContent?.includes('保存后通知钉钉群'))!.querySelector('input')!
    checkbox.click(); await flushPromises()
    await click('保存问题')
    expect(api.saveProjectOpl).toHaveBeenCalledWith('p1','i1',expect.objectContaining({ notifyGroup: true }),[],'token')
    expect(document.body.textContent).toContain('问题已保存，无需重复提交')
    await click('完成',wrapper.element)
    expect(api.completeProjectOpl).toHaveBeenCalledWith('p1','i1',3,'token',true)
  })
  it('exports every scoped issue regardless of filters or pagination, retaining complete content', async () => {
    const rows = Array.from({length:27}, (_, index) => ({issue:{...issue,id:`export-${index}`,number:index+1,description:`完整描述${index}\n第二行`,solution:'完整解决措施',priority:'高',relatedProjectId:'child-1',owner:'engineer',attachments:[{id:'a',name:'证据.txt',length:8}]},canEdit:true,canComplete:true}))
    api.readProjectOpl.mockResolvedValue({rows:[...rows,{issue:{...issue,id:'closed',status:'已完成'},canEdit:true,canComplete:false},{issue:{...issue,id:'low',priority:'低'},canEdit:true,canComplete:true},{issue:{...issue,id:'void',status:'已作废'},canEdit:true,canComplete:false}],people:[{username:'engineer',displayName:'王工'}],subprojects:[{id:'child-1',code:'P700012-1',name:'子设备'}]})
    await setup()
    wrapper.findAllComponents({name:'ElSelect'}).find(item=>item.props('multiple'))!.vm.$emit('update:modelValue',['高'])
    await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(20)
    await wrapper.get('input[aria-label="搜索开口问题"]').setValue('完整描述0')
    wrapper.findAllComponents({name:'ElSelect'})[1]!.vm.$emit('update:modelValue','engineer')
    await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    await click('导出Excel')
    expect(api.getProjectExportMetadata).toHaveBeenCalledWith('p1','token')
    const [file, exported, metadata] = exporter.downloadOplWorkbook.mock.calls[0]!
    expect(file).toBe('P700012_OPL_202610141200.xlsx')
    expect(exported).toHaveLength(30)
    expect(exported[26]).toEqual([27,'P700012-1','高','待处理','完整描述26\n第二行','证据.txt','完整解决措施','2020-01-01','王工','—','2026-10-12','admin'])
    expect(metadata).toMatchObject({title:'OPL开口问题清单',scope:'当前主项目及子项目 · 全部30条',notes:'完整导出，包含全部状态，不受列表筛选与分页影响'})
  })

  it('keeps full export available with no filter matches and preserves the selected child scope', async () => {
    api.readProjectOpl.mockResolvedValue({rows:[
      {issue:{...issue,id:'whole'},canEdit:true,canComplete:true},
      {issue:{...issue,id:'child-active',relatedProjectId:'child-1'},canEdit:true,canComplete:true},
      {issue:{...issue,id:'child-completed',relatedProjectId:'child-1',status:'已完成'},canEdit:true,canComplete:false},
      {issue:{...issue,id:'other-child',relatedProjectId:'child-2'},canEdit:true,canComplete:true},
    ],people:[],subprojects:[{id:'child-1',code:'P700012-1',name:'子设备'}]})
    await setup({id:'child-1',parentProjectId:'p1',code:'P700012-1',name:'子设备'} as ProjectSummary)
    await wrapper.get('input[aria-label="搜索开口问题"]').setValue('没有匹配的问题')
    const button=wrapper.findAll('button').find(button=>button.text()==='导出Excel')!
    expect(button.attributes('disabled')).toBeUndefined()
    await click('导出Excel')
    const [,rows,metadata]=exporter.downloadOplWorkbook.mock.calls[0]!
    expect(api.getProjectExportMetadata).toHaveBeenCalledWith('child-1','token')
    expect(rows).toHaveLength(2)
    expect(rows.map((row:(string|number)[])=>row[1])).toEqual(['P700012-1','P700012-1'])
    expect(rows.map((row:(string|number)[])=>row[3])).toEqual(['待处理','已完成'])
    expect(metadata.scope).toBe('当前子项目 · 全部2条')
  })

  it('filters several priorities together and clearing restores every row', async () => {
    api.readProjectOpl.mockResolvedValue({rows:['紧急','普通','低'].map((priority,index)=>({issue:{...issue,id:`i${index}`,priority,number:index+1},canEdit:true,canComplete:true})),people:[]})
    await setup()
    const select=wrapper.findAllComponents({name:'ElSelect'}).find(item=>item.props('multiple'))!
    expect(select.props('multiple')).toBe(true)
    select.vm.$emit('update:modelValue',['紧急','低']);await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('tbody tr td:nth-child(3)').map(cell=>cell.text())).toEqual(['紧急','低'])
    select.vm.$emit('update:modelValue',[]);await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
  })
  it('completes a row without opening its drawer and displays the server completion date', async () => {
    api.completeProjectOpl.mockResolvedValue({ ...issue, status: '已完成', completedAt: '2026-10-13T00:01:00Z', rowVersion: 4 })
    await setup(); await click('完成', wrapper.element)
    expect(api.completeProjectOpl).toHaveBeenCalledWith('p1', 'i1', 3, 'token', false)
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
