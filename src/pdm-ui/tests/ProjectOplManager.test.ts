import { computed } from 'vue'
import { projectCenterAccessKey, projectCenterSchema } from '../src/projectCenterAccess'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectOplManager from '../src/components/ProjectOplManager.vue'
import type { ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({ readProjectOpl: vi.fn(), markProjectOplViewed: vi.fn(), saveProjectOpl: vi.fn(), readOplAttachment: vi.fn(), completeProjectOpl: vi.fn(), getProjectExportMetadata: vi.fn() }))
vi.mock('../src/api', () => ({ ...api, PdmApiError: class extends Error { status = 409 } }))
const exporter = vi.hoisted(() => ({ downloadOplWorkbook: vi.fn() }))
vi.mock('../src/oplWorkbook', () => exporter)
let wrapper: ReturnType<typeof mount>
const issue = { id: 'i1', projectId: 'p1', number: 1, description: '相机支架松动', priority: '高', status: '待处理', owner: '', dueDate: '2020-01-01', solution: '', createdBy: 'admin', createdAt: '2026-10-12T00:00:00Z', updatedBy: 'admin', rowVersion: 3, attachments: [] }
async function setup(project = { id: 'p1', code: 'P700012', name: 'TEST 4' } as ProjectSummary, mode: 'list' | 'board' = 'list') {
  wrapper = mount(ProjectOplManager, { attachTo: document.body, props: { project, token: 'token' }, global: { plugins: [ElementPlus], stubs: { PdfDrawingViewer: { props: ['url'], template: '<div class="pdf-preview-stub" :data-url="url" />' } } } })
  await flushPromises()
  if (mode === 'list') await click('列表', wrapper.element)
}
const click = async (label: string, root: ParentNode = document.body) => { (Array.from(root.querySelectorAll('button')).find(b => b.textContent?.trim() === label) as HTMLButtonElement).click(); await flushPromises() }
const ResizeObserverMock = globalThis.ResizeObserver
beforeEach(() => { vi.stubGlobal('ResizeObserver', ResizeObserverMock); vi.clearAllMocks(); api.markProjectOplViewed.mockResolvedValue(undefined); api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: true, canComplete: true }], people: [] }); api.saveProjectOpl.mockResolvedValue(issue); api.getProjectExportMetadata.mockResolvedValue({title:'项目资料',company:'公司',scope:'当前项目',versionLabel:'版本',version:'—',exportedAt:'2026-10-14T12:00:00+08:00',exportedBy:'管理员',fields:[]}) })
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ''; vi.unstubAllGlobals() })
describe('OPL drawer', () => {
  it.each(['board', 'row', 'view'] as const)('opens %s directly in edit mode with the same saved values and version', async entry => {
    await setup(undefined, entry === 'board' ? 'board' : 'list')
    if (entry === 'board') await wrapper.get('[data-issue-id="i1"] .opl-board-card__open').trigger('click')
    else if (entry === 'row') await wrapper.get('tbody tr').trigger('click')
    else await click('查看', wrapper.element)
    await flushPromises()
    const description = document.body.querySelector('textarea[aria-label="问题描述"]') as HTMLTextAreaElement
    expect(description.value).toBe(issue.description)
    expect(description.disabled).toBe(false)
    expect(document.body.textContent).not.toContain('编辑问题')
    description.value = '已直接修改'; description.dispatchEvent(new Event('input', { bubbles: true })); await flushPromises()
    await click('保存问题')
    expect(api.saveProjectOpl).toHaveBeenCalledWith('p1', 'i1', expect.objectContaining({ description: '已直接修改', expectedRowVersion: 3 }), [], 'token')
  })
  it('keeps NEW until the individual detail is viewed, then clears only that marker and retains overdue', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: false, canComplete: false, isNew: true }, { issue: { ...issue, id: 'i2', number: 2 }, canEdit: true, canComplete: true, isNew: true }], people: [] })
    await setup()
    expect(wrapper.findAll('[aria-label="新增未查看"]')).toHaveLength(2)
    expect(wrapper.findAll('[aria-label="已逾期"]')).toHaveLength(2)
    expect(wrapper.findAll('thead th')[0]!.text()).toBe('状态')
    expect(wrapper.get('tbody tr td:first-child').findAll('.opl-capsule').map(marker => marker.text())).toEqual(['NEW', '逾期'])
    expect(wrapper.get('tbody tr td:nth-child(9)').text()).toBe(issue.dueDate)
    expect(wrapper.get('tbody tr td:nth-child(9)').findAll('.opl-capsule')).toHaveLength(0)
    expect(api.markProjectOplViewed).not.toHaveBeenCalled()
    await click('查看', wrapper.element)
    expect(api.markProjectOplViewed).toHaveBeenCalledWith('p1', 'i1', 'token')
    expect(wrapper.findAll('[aria-label="新增未查看"]')).toHaveLength(1)
    expect(wrapper.findAll('[aria-label="已逾期"]')).toHaveLength(2)
    expect(wrapper.get('tbody tr td:first-child').findAll('.opl-capsule').map(marker => marker.text())).toEqual(['逾期'])
    expect(document.body.textContent).toContain('查看开口问题')
  })
  it('retains NEW when the read receipt fails and displays a Chinese retry message', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: true, canComplete: true, isNew: true }], people: [] })
    api.markProjectOplViewed.mockRejectedValue(new Error('network failure'))
    await setup(); await click('编辑', wrapper.element)
    expect(wrapper.findAll('[aria-label="新增未查看"]')).toHaveLength(1)
    expect(document.body.textContent).toContain('查看状态同步失败')
    expect(document.body.textContent).not.toContain('network failure')
  })
  it('refreshes account read state on focus and skips refresh while editing', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: true, canComplete: true, isNew: true }], people: [] })
    await setup()
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: true, canComplete: true, isNew: false }], people: [] })
    window.dispatchEvent(new Event('focus')); await flushPromises()
    expect(wrapper.findAll('[aria-label="新增未查看"]')).toHaveLength(0)
    await click('编辑', wrapper.element)
    const count = api.readProjectOpl.mock.calls.length
    window.dispatchEvent(new Event('focus')); await flushPromises()
    expect(api.readProjectOpl).toHaveBeenCalledTimes(count)
  })
  it('marks only past-due unfinished issues overdue, excluding today, future, missing and closed dates', async () => {
    const now = new Date(), value = new Date(now.getFullYear(), now.getMonth(), now.getDate()), today = value.toLocaleDateString('sv-SE')
    value.setDate(value.getDate() - 1); const yesterday = value.toLocaleDateString('sv-SE')
    value.setDate(value.getDate() + 2); const tomorrow = value.toLocaleDateString('sv-SE')
    api.readProjectOpl.mockResolvedValue({ rows: [
      { ...issue, id: 'late', dueDate: yesterday }, { ...issue, id: 'today', dueDate: today }, { ...issue, id: 'future', dueDate: tomorrow },
      { ...issue, id: 'none', dueDate: null }, { ...issue, id: 'closed', dueDate: yesterday, status: '已完成' }, { ...issue, id: 'void', dueDate: yesterday, status: '已作废' },
    ].map(issue => ({ issue, canEdit: true, canComplete: true })), people: [] })
    await setup()
    wrapper.findAllComponents({ name: 'ElSelect' })[0]!.vm.$emit('update:modelValue', '')
    await flushPromises()
    expect(wrapper.findAll('[aria-label="已逾期"]')).toHaveLength(1)
  })
  it('shows the latest release in the list and every historical release in the drawer with working package links', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: false, canComplete: false, releases: [
      { releasePackageId: 'old', projectId: 'p1', packageNumber: 'RP-OLD', bomType: '标准件', drawingCount: 0, publishedAt: '2026-10-18T00:00:00Z' },
      { releasePackageId: 'new', projectId: 'p1', packageNumber: 'RP-NEW', bomType: '非标件', drawingCount: 8, publishedAt: '2026-10-19T00:00:00Z' },
    ] }], people: [] })
    await setup()
    expect(wrapper.text()).toContain('最近发布日期')
    expect(wrapper.find('tbody').text()).toContain('2026-10-19')
    await click('查看')
    const history = document.body.querySelector('[aria-label="关联发布记录"]')!
    expect(history.textContent).toContain('2 次')
    expect([...history.querySelectorAll('tbody tr')].map(row => row.textContent)).toEqual([
      'RP-NEW非标件82026-10-19', 'RP-OLD标准件02026-10-18',
    ])
    await click('RP-OLD', history)
    expect(wrapper.emitted('openRelease')).toEqual([['p1', 'old']])
    expect(api.completeProjectOpl).not.toHaveBeenCalled()
  })
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
    expect(exported[26]).toEqual([27,'P700012-1','高','待处理','完整描述26\n第二行','证据.txt','完整解决措施','2020-01-01','王工','—','2026-10-12','姓名待补充'])
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
    expect(wrapper.findAll('tbody tr td:nth-child(4)').map(cell=>cell.text())).toEqual(['紧急','低'])
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
    expect(wrapper.get('tbody tr td:nth-child(2)').text()).toBe('1')
    await wrapper.get('tbody tr').trigger('click')
    expect(document.body.textContent).toContain('编辑开口问题')
    wrapper.unmount()
    api.readProjectOpl.mockResolvedValue({ rows: [{issue, canEdit:false, canComplete:false}], people:[] })
    await setup()
    await wrapper.get('tbody tr').trigger('click')
    expect(document.body.textContent).toContain('查看开口问题')
    expect(document.body.querySelector('textarea')).toBeNull()
    expect(document.body.textContent).not.toContain('保存问题')
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
    expect(api.readOplAttachment).toHaveBeenCalledWith('p1','i1','pdf','token',true)
    wrapper.unmount()
    expect(revoke).toHaveBeenCalledWith('blob:opl-pdf')
  })
  it('filters the shared list to the selected child and defaults new issues to that optional child', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [
      { issue: { ...issue, id: 'whole', description: '项目整体问题' }, canEdit: true, canComplete: true },
      { issue: { ...issue, id: 'child', description: '当前子项目问题', relatedProjectId: 'child-1' }, canEdit: true, canComplete: true },
      { issue: { ...issue, id: 'other', description: '其他子项目问题', relatedProjectId: 'child-2' }, canEdit: true, canComplete: true },
    ], people: [], rootProject: { id: 'root', code: 'P700012', name: '主设计备' }, subprojects: [{ id: 'child-1', code: 'P700012-1', name: '子设备' }] })
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
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue: { ...issue, relatedProjectId: 'child-1' }, canEdit: true, canComplete: true }], people: [], rootProject: { id: 'root-1', code: 'P700012', name: '主设计备' }, subprojects: [{ id: 'child-1', code: 'P700012-1', name: '子设备' }] })
    await setup(); await click('编辑')
    const projectInput = document.body.querySelector('input[disabled]') as HTMLInputElement
    expect(projectInput.value).toBe('P700012 · 主设计备')
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


it('装配只读账号即使是问题负责人也不能新增、完成、编辑或导出', async () => {
  const permissions = computed(() => [projectCenterSchema, 'project.tab.opl.view'])
  wrapper = mount(ProjectOplManager, { attachTo: document.body, props: { project: { id: 'p1', code: 'P1', name: '装配指导' } as ProjectSummary, token: 'token' }, global: { plugins: [ElementPlus], provide: { [projectCenterAccessKey as symbol]: permissions } } })
  await flushPromises()
  await click('列表', wrapper.element)
  const buttons = wrapper.findAll('button').map(button => button.text().trim())
  expect(buttons).not.toContain('新增问题')
  expect(buttons).not.toContain('编辑')
  expect(buttons).not.toContain('完成')
  expect(buttons).not.toContain('导出Excel')
  await click('查看', wrapper.element)
  expect(document.body.textContent).toContain('查看开口问题')
  expect(Array.from(document.body.querySelectorAll('.opl-form input, .opl-form textarea')).every(control => (control as HTMLInputElement).disabled)).toBe(true)
  expect(api.saveProjectOpl).not.toHaveBeenCalled()
})

describe('OPL 状态看板', () => {
  async function drag(id: string, target: string) {
    const dataTransfer = { effectAllowed: '', dropEffect: '', setData: vi.fn() }
    await wrapper.get(`[data-issue-id="${id}"]`).trigger('dragstart', { dataTransfer })
    await wrapper.get(`[data-status="${target}"]`).trigger('dragover', { dataTransfer })
    await wrapper.get(`[data-status="${target}"]`).trigger('drop', { dataTransfer })
    await flushPromises()
  }
  it('展示五列和完整筛选结果，切回列表保留分页', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: Array.from({ length: 25 }, (_, i) => ({ issue: { ...issue, id: `b${i}`, number: i + 1, status: i === 24 ? '已完成' : '待处理' }, canEdit: true, canComplete: true })), people: [] })
    await setup(); await click('看板', wrapper.element)
    expect(wrapper.findAll('.opl-board-column')).toHaveLength(5)
    expect(wrapper.findAll('.opl-board-card')).toHaveLength(25)
    expect(wrapper.get('[data-status="已完成"]').findAll('.opl-board-card')).toHaveLength(1)
    await click('列表', wrapper.element)
    expect(wrapper.findAll('tbody tr')).toHaveLength(20)
  })
  it('拖入目标列自动保存状态并保留版本与其他字段', async () => {
    api.saveProjectOpl.mockResolvedValue({ ...issue, status: '处理中', rowVersion: 4 })
    await setup(); await click('看板', wrapper.element); await drag('i1', '处理中')
    expect(api.saveProjectOpl).toHaveBeenCalledWith('p1', 'i1', expect.objectContaining({ description: issue.description, status: '处理中', expectedRowVersion: 3, attachmentIds: [], relatedProjectId: null }), [], 'token')
    expect(wrapper.get('[data-status="处理中"]').text()).toContain(issue.description)
    expect(wrapper.get('[data-status="待处理"]').findAll('.opl-board-card')).toHaveLength(0)
    await click('列表', wrapper.element)
    expect(wrapper.find('tbody').text()).toContain('处理中')
  })
  it('拖入完成走确认接口', async () => {
    api.completeProjectOpl.mockResolvedValue({ ...issue, status: '已完成', completedAt: '2026-11-07T02:00:00Z', rowVersion: 4 })
    await setup(); await click('看板', wrapper.element); await drag('i1', '已完成')
    expect(api.completeProjectOpl).toHaveBeenCalledWith('p1', 'i1', 3, 'token', false)
    expect(api.saveProjectOpl).not.toHaveBeenCalled()
    expect(wrapper.get('[data-status="已完成"]').text()).toContain(issue.description)
  })
  it('失败及重复拖放保留原状态，不重复请求', async () => {
    let reject!: (reason: Error) => void
    api.saveProjectOpl.mockImplementation(() => new Promise((_, fail) => { reject = fail }))
    await setup(); await click('看板', wrapper.element); await drag('i1', '处理中'); await drag('i1', '已作废')
    expect(api.saveProjectOpl).toHaveBeenCalledTimes(1)
    reject(new Error('network failure')); await flushPromises()
    expect(wrapper.get('[data-status="待处理"]').text()).toContain(issue.description)
    expect(wrapper.get('[data-status="处理中"]').findAll('.opl-board-card')).toHaveLength(0)
    expect(document.body.textContent).toContain('卡片已保留在原状态')
    expect(document.body.textContent).not.toContain('network failure')
  })
  it('待确认缺少措施时先补充，保存前不改动状态', async () => {
    await setup(); await click('看板', wrapper.element); await drag('i1', '待确认')
    expect(api.saveProjectOpl).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain('请填写解决措施并保存')
    expect(wrapper.get('[data-status="待处理"]').text()).toContain(issue.description)
    expect(wrapper.findComponent({ name: 'ElDrawer' }).props('modelValue')).toBe(true)
  })
  it('只读可展开详情但不能拖动；可编辑者没有确认权限时不能拖入完成', async () => {
    api.readProjectOpl.mockResolvedValue({ rows: [{ issue, canEdit: false, canComplete: false }, { issue: { ...issue, id: 'edit', number: 2 }, canEdit: true, canComplete: false }], people: [] })
    await setup(); await click('看板', wrapper.element)
    expect(wrapper.get('[data-issue-id="i1"]').attributes('draggable')).toBe('false')
    await drag('i1', '处理中'); await drag('edit', '已完成')
    expect(api.saveProjectOpl).not.toHaveBeenCalled(); expect(api.completeProjectOpl).not.toHaveBeenCalled()
    await wrapper.get('[data-issue-id="i1"] .opl-board-card__open').trigger('click'); await flushPromises()
    expect(document.body.querySelector('[aria-label="开口问题详情"]')?.textContent).toContain(issue.description)
    expect(document.body.textContent).not.toContain('编辑问题')
  })
})

it('OPL 默认显示看板，并可切换现有列表', async () => {
  await setup(undefined, 'board')
  expect(wrapper.find('.opl-board').exists()).toBe(true)
  expect(wrapper.find('.opl-table').exists()).toBe(false)
  await click('列表', wrapper.element)
  expect(wrapper.find('.opl-table').exists()).toBe(true)
})
