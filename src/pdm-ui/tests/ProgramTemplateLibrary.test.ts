import { flushPromises, mount } from '@vue/test-utils'
import { ElMessage } from '../src/statusMessage'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProgramTemplateLibrary from '../src/components/ProgramTemplateLibrary.vue'

const api = vi.hoisted(() => ({
  listProgramTemplates: vi.fn(),
  listProgramTemplateTasks: vi.fn(),
  createProgramTemplate: vi.fn(),
  createProgramTemplateRevision: vi.fn(),
  decideProgramTemplateTask: vi.fn(),
  downloadProgramTemplate: vi.fn(),
  getProgramTemplate: vi.fn(),
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
    expect(wrapper.findAll('.select-stub[data-allow-create]').length).toBe(3)
    await wrapper.findAll('button').find(button => button.text().includes('保存草稿'))!.trigger('click')
    await flushPromises()

    expect(api.createProgramTemplate).toHaveBeenCalledOnce()
    expect(api.createProgramTemplate.mock.calls[0]![0]).toMatchObject({ name: '', vendor: '', platform: '', softwareVersion: '' })
  })
})
