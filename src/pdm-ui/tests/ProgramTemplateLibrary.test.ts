import { flushPromises, mount } from '@vue/test-utils'
import { ElMessage } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
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

  it('缺少受控文件时不创建草稿或提交审核', async () => {
    const warning = vi.spyOn(ElMessage, 'warning').mockImplementation(() => undefined as never)
    const wrapper = mount(ProgramTemplateLibrary, {
      props: {
        token: 'token',
        username: 'developer',
        permissions: ['program-template.view', 'program-template.submit'],
      },
      global: {
        stubs: {
          ElButton: { template: '<button type="button" @click="$emit(\'click\')"><slot /></button>' },
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
    await wrapper.findAll('button').find(button => button.text().includes('提交审核'))!.trigger('click')
    await flushPromises()

    expect(warning).toHaveBeenCalledWith('请先上传ZIP程序包')
    expect(api.createProgramTemplate).not.toHaveBeenCalled()
    expect(api.submitProgramTemplateRevision).not.toHaveBeenCalled()
  })
})
