import { flushPromises, mount } from '@vue/test-utils'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../src/statusMessage'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProgramTemplateLibrary from '../src/components/ProgramTemplateLibrary.vue'

const api = vi.hoisted(() => ({
  listProgramTemplates: vi.fn(),
  listProgramTemplateTasks: vi.fn(),
  createProgramTemplate: vi.fn(),
  createProgramTemplateRevision: vi.fn(),
  decideProgramTemplateTask: vi.fn(),
  deleteProgramTemplateDraft: vi.fn(),
  downloadProgramTemplate: vi.fn(),
  getProgramTemplate: vi.fn(),
  getProgramTemplateOptions: vi.fn(),
  saveProgramTemplateOptions: vi.fn(),
  submitProgramTemplateRevision: vi.fn(),
  updateProgramTemplateDraft: vi.fn(),
  uploadProgramTemplateFile: vi.fn(),
}))

vi.mock('../src/api', () => api)

describe('ProgramTemplateLibrary', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.listProgramTemplates.mockResolvedValue([])
    api.listProgramTemplateTasks.mockResolvedValue([])
    api.getProgramTemplateOptions.mockResolvedValue({ categories: [], vendors: [], platforms: [] })
  })

  afterEach(() => vi.unstubAllGlobals())

  it('缺少受控文件时不创建草稿或提交审核', async () => {
    vi.stubGlobal('crypto', {})
    const warning = vi.spyOn(ElMessage, 'warning').mockImplementation(() => undefined as never)
    const wrapper = mount(ProgramTemplateLibrary, {
      props: {
        token: 'token',
        username: 'developer',
        permissions: ['program-template.view', 'program-template.submit'],
      },
      global: {
        stubs: {
          ElButton: { template: '<button type="button"><slot /></button>' },
          ElDrawer: { props: ['modelValue'], template: '<section v-if="modelValue"><slot /><slot name="footer" /></section>' },
          ElTable: { template: '<div><slot /></div>' },
          ElTableColumn: { props: ['label'], template: '<span class="table-column-label">{{ label }}</span>' },
          ElSelect: true,
          ElOption: true,
          ElDialog: true,
          ElForm: true,
          ElFormItem: true,
          ElInput: true,
          ElTag: true,
          ElEmpty: true,
          ElProgress: true,
          ElButtonGroup: true,
          ElCheckbox: true,
          ElCheckboxGroup: true,
          ElRadioButton: true,
          ElRadioGroup: true,
        },
      },
    })
    await flushPromises()

    expect(wrapper.find('.program-template-heading').exists()).toBe(false)
    expect(wrapper.get('.program-template-toolbar').text()).toContain('上传程序模板')
    expect(wrapper.findAll('.table-column-label').map(column => column.text())).toEqual(expect.arrayContaining(['程序名称', '功能说明']))
    await wrapper.findAll('button').find(button => button.text().includes('上传程序模板'))!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('保存草稿')
    await wrapper.findAll('button').find(button => button.text().includes('提交审核'))!.trigger('click')
    await flushPromises()

    expect(warning).toHaveBeenCalledWith('请先上传ZIP或RAR程序包')
    expect(api.createProgramTemplate).not.toHaveBeenCalled()
    expect(api.submitProgramTemplateRevision).not.toHaveBeenCalled()
  })

  it('草稿可以信息不完整保存，并开放RAR和Word文件', async () => {
    vi.stubGlobal('crypto', {})
    api.createProgramTemplate.mockResolvedValue({
      id: 'template-1',
      assetType: 'PlcFunctionBlock',
      revisions: [{ id: 'revision-1', version: 'v1.0.0', attemptNumber: 1, rowVersion: 1 }],
    })
    const wrapper = mount(ProgramTemplateLibrary, {
      props: { token: 'token', username: 'developer', permissions: ['program-template.view', 'program-template.submit'] },
      global: {
        stubs: {
          ElButton: { template: '<button type="button"><slot /></button>' },
          ElDrawer: { props: ['modelValue'], template: '<section v-if="modelValue"><slot /><slot name="footer" /></section>' },
          ElTable: { template: '<div><slot /></div>' }, ElTableColumn: true,
          ElSelect: { props: ['modelValue', 'allowCreate', 'filterable'], template: '<div class="select-stub" :data-allow-create="allowCreate" :data-filterable="filterable"><slot /></div>' },
          ElOption: true, ElDialog: true, ElForm: { template: '<form><slot /></form>' }, ElFormItem: { template: '<label><slot /></label>' },
          ElInput: true, ElTag: true, ElEmpty: true, ElProgress: true, ElButtonGroup: true,
          ElCheckbox: true, ElCheckboxGroup: true, ElRadioButton: true, ElRadioGroup: true,
        },
      },
    })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().includes('上传程序模板'))!.trigger('click')
    await flushPromises()

    expect(wrapper.find('input[accept=".zip,.rar"]').exists()).toBe(true)
    expect(wrapper.find('input[accept=".pdf,.doc,.docx,.xls,.xlsx,.png,.jpg,.jpeg"]').exists()).toBe(true)
    // 分类、厂商、平台改为选择维护好的选项，不再允许在下拉框里直接新建。
    expect(wrapper.findAll('.select-stub[data-allow-create]').length).toBe(0)
    await wrapper.findAll('button').find(button => button.text().includes('保存草稿'))!.trigger('click')
    await flushPromises()

    expect(api.createProgramTemplate).toHaveBeenCalledOnce()
    expect(api.createProgramTemplate.mock.calls[0]![0]).toMatchObject({ name: '', vendor: '', platform: '', softwareVersion: '' })
  })

  it('本人草稿可确认删除并刷新列表', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as Awaited<ReturnType<typeof ElMessageBox.confirm>>)
    const draft = {
      id: 'revision-1', version: 'v1.0.0', attemptNumber: 1, state: 'Draft', name: '草稿模板', category: '', description: '',
      vendor: '', platform: '', softwareVersion: '', applicableSeries: '', tags: [], changeNote: '', createdBy: 'developer',
      createdAt: '2026-09-17T00:00:00Z', submittedAt: null, publishedAt: null, rowVersion: 3, parameters: [],
      packageFileName: 'draft.rar', packageFileLength: 12, packageSha256: 'A', evidenceFileName: null, evidenceFileLength: null, evidenceSha256: null,
    }
    const template = { id: 'template-1', code: 'PT-PLC-0001', assetType: 'PlcProgram', currentPublishedRevisionId: null, isArchived: false, createdBy: 'developer', createdAt: '2026-09-17T00:00:00Z', revisions: [draft] }
    api.getProgramTemplate.mockResolvedValue(template)
    api.deleteProgramTemplateDraft.mockResolvedValue(undefined)
    api.listProgramTemplates.mockResolvedValue([])
    const wrapper = mount(ProgramTemplateLibrary, {
      props: { token: 'token', username: 'developer', permissions: ['program-template.view', 'program-template.submit'] },
      global: {
        stubs: {
          ElButton: { template: '<button type="button"><slot /></button>' },
          ElDrawer: { props: ['modelValue'], template: '<section v-if="modelValue"><slot /><slot name="footer" /></section>' },
          ElTable: { template: '<div><slot /></div>' }, ElTableColumn: true, ElSelect: true, ElOption: true, ElDialog: true,
          ElForm: true, ElFormItem: true, ElInput: true, ElTag: true, ElEmpty: true, ElProgress: true, ElButtonGroup: true,
          ElCheckbox: true, ElCheckboxGroup: true, ElRadioButton: true, ElRadioGroup: true,
        },
      },
    })
    await flushPromises()
    await (wrapper.vm as unknown as { openDetail: (id: string) => Promise<void> }).openDetail('template-1')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().includes('删除草稿'))!.trigger('click')
    await flushPromises()

    expect(api.deleteProgramTemplateDraft).toHaveBeenCalledWith('revision-1', 3, 'token')
    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('无法恢复'), '删除程序模板草稿', expect.objectContaining({ confirmButtonText: '确认删除' }))
    expect(wrapper.text()).not.toContain('删除草稿')
  })

  it('详情显示审核流程与审核人员', async () => {
    const pending = {
      id: 'revision-1', version: 'v1.0.0', attemptNumber: 1, state: 'PendingReview', name: '111', category: '', description: '',
      vendor: '', platform: '', softwareVersion: '', applicableSeries: '', tags: [], changeNote: '',
      createdBy: 'baihongju', createdAt: '2026-09-20T06:12:00Z', submittedAt: '2026-09-20T06:12:40Z', publishedAt: null,
      rowVersion: 1, parameters: [],
      approvalTasks: [
        { stage: 'Review', assignee: 'wangyonghuang', assigneeRoleCode: null, decision: null, decisionBy: null, comment: null, createdAt: '2026-09-20T06:12:40Z', decidedAt: null },
        { stage: 'Approval', assignee: null, assigneeRoleCode: 'Approver', decision: null, decisionBy: null, comment: null, createdAt: '2026-09-20T06:12:40Z', decidedAt: null },
      ],
    }
    api.getProgramTemplate.mockResolvedValue({
      id: 'template-1', code: 'PT-FB-0004', assetType: 'PlcFunctionBlock', currentPublishedRevisionId: null, isArchived: false,
      createdBy: 'baihongju', createdAt: '2026-09-20T06:12:00Z', revisions: [pending],
    })
    const wrapper = mount(ProgramTemplateLibrary, {
      props: { token: 'token', username: 'baihongju', permissions: ['program-template.view'] },
      global: {
        stubs: {
          ElButton: { template: '<button type="button"><slot /></button>' },
          ElDrawer: { props: ['modelValue'], template: '<section v-if="modelValue"><slot name="header" /><slot /><slot name="footer" /></section>' },
          ElTable: { template: '<div><slot /></div>' }, ElTableColumn: true, ElSelect: true, ElOption: true, ElDialog: true,
          ElForm: true, ElFormItem: true, ElInput: true, ElTag: true, ElEmpty: true, ElProgress: true, ElButtonGroup: true,
          ElCheckbox: true, ElCheckboxGroup: true, ElRadioButton: true, ElRadioGroup: true,
        },
      },
    })
    await flushPromises()
    await (wrapper.vm as unknown as { openDetail: (id: string) => Promise<void> }).openDetail('template-1')
    await flushPromises()

    const steps = wrapper.findAll('.program-template-flow li')
    expect(steps.map(step => step.text())).toEqual([
      expect.stringContaining('提交'),
      expect.stringContaining('电气组织审核'),
      expect.stringContaining('集团标准化批准'),
    ])
    expect(steps[0]!.text()).toContain('baihongju')
    expect(steps[1]!.text()).toContain('wangyonghuang')
    expect(steps[1]!.text()).toContain('进行中')
    expect(steps[2]!.text()).toContain('具“批准程序模板”权限的负责人')
    expect(steps[1]!.classes()).toContain('is-active')
    expect(steps[2]!.classes()).toContain('is-todo')
    expect(wrapper.get('.program-template-detail-title p').text()).toContain('当前 电气组织审核：wangyonghuang')
  })

  it('列表合并显示提交人，分类厂商平台只提供维护好的选项', async () => {
    api.getProgramTemplateOptions.mockResolvedValue({ categories: ['控制'], vendors: ['Siemens'], platforms: ['TIA Portal'] })
    const draft = {
      id: 'revision-draft', version: 'v1.0.0', attemptNumber: 1, state: 'Draft', name: '草稿模板', category: '控制', description: '',
      vendor: 'Siemens', platform: 'TIA Portal', softwareVersion: 'V19', applicableSeries: '', tags: [], changeNote: '',
      createdBy: 'engineer', createdAt: '2026-09-20T06:12:00Z', submittedAt: null, publishedAt: null, rowVersion: 1, parameters: [],
    }
    const template = { id: 'template-1', code: 'PT-FB-0001', assetType: 'PlcFunctionBlock', currentPublishedRevisionId: null, isArchived: false, createdBy: 'engineer', createdAt: '2026-09-20T06:12:00Z', revisions: [draft] }
    api.listProgramTemplates.mockImplementation((_token: string, mine?: boolean) => Promise.resolve(mine ? [template] : []))
    const wrapper = mount(ProgramTemplateLibrary, {
      props: { token: 'token', username: 'engineer', permissions: ['program-template.view', 'program-template.submit', 'program-template.manage'] },
      global: {
        stubs: {
          ElButton: { template: '<button type="button"><slot /></button>' },
          ElDrawer: { props: ['modelValue'], template: '<section v-if="modelValue"><slot /><slot name="footer" /></section>' },
          ElTable: { template: '<div><slot /></div>' },
          ElTableColumn: { props: ['label'], template: '<span class="table-column-label">{{ label }}</span>' },
          ElSelect: true, ElOption: true, ElDialog: true, ElForm: true, ElFormItem: true, ElInput: true, ElTag: true,
          ElEmpty: true, ElProgress: true, ElButtonGroup: true, ElCheckbox: true, ElCheckboxGroup: true, ElRadioButton: true, ElRadioGroup: true,
        },
      },
    })
    await flushPromises()

    expect(api.listProgramTemplates).toHaveBeenCalledWith('token')
    expect(api.listProgramTemplates).toHaveBeenCalledWith('token', true)
    expect(wrapper.findAll('.table-column-label').map(column => column.text())).toContain('提交人')
    expect((wrapper.vm as unknown as { categoryOptions: string[] }).categoryOptions).toEqual(['控制'])

    api.saveProgramTemplateOptions.mockResolvedValue({ categories: ['控制', '新分类'], vendors: ['Siemens'], platforms: ['TIA Portal'] })
    const vm = wrapper.vm as unknown as {
      openOptionMaintenance: () => void
      saveOptionMaintenance: () => Promise<void>
      optionDraft: { categories: string[] }
      optionDialogOpen: boolean
      categoryOptions: string[]
    }
    vm.openOptionMaintenance()
    expect(vm.optionDialogOpen).toBe(true)
    vm.optionDraft.categories.push('新分类')
    await vm.saveOptionMaintenance()

    expect(api.saveProgramTemplateOptions).toHaveBeenCalledWith({ categories: ['控制', '新分类'], vendors: ['Siemens'], platforms: ['TIA Portal'] }, 'token')
    expect(vm.categoryOptions).toEqual(['控制', '新分类'])
    expect(vm.optionDialogOpen).toBe(false)
  })
})
