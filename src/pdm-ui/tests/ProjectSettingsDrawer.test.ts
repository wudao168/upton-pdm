import { flushPromises, mount } from '@vue/test-utils'
import { ElMessageBox } from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProjectSettingsDrawer from '../src/components/ProjectSettingsDrawer.vue'
import type { ProjectSummary } from '../src/types'

const target: ProjectSummary = {
  id: 'target-project', code: 'P700002', name: '新建目标项目', owner: 'admin', stage: '进行中', vaultName: 'P700002',
  vaultLocation: 'D:\\PDM\\P700002', releaseLocation: 'D:\\Release\\P700002', quantity: 1, serialNumbers: [],
  responsibleUsers: [], collaborativeProjectManagers: [], designers: [], canReadContent: true,
  canAssignExecutionUnit: false, canManageMainStaffing: false, canAssignDesigners: false,
}
const source: ProjectSummary = { ...target, id: 'source-project', code: 'P700001', name: '现有源项目', vaultName: 'P700001' }

describe('ProjectSettingsDrawer', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  it('在项目设置中按确认范围复制项目内容', async () => {
    const preview = vi.fn().mockResolvedValue({
      sourceProjectId: source.id, targetProjectId: target.id,
      modelCount: 2, drawingCount: 2, bomItemCount: 8, validationItemCount: 3, projectFileCount: 1, totalBytes: 2048,
      folders: [
        { id: 'gas-folder', name: '气路时序', path: '机械设计 / 气路时序', templateKey: 'mechanical.air-sequence', fileCount: 1, totalBytes: 512, defaultSelected: true },
        { id: 'other-folder', name: '其他资料', path: '机械设计 / 其他资料', templateKey: 'mechanical.other', fileCount: 2, totalBytes: 1024, defaultSelected: false },
      ],
      blockingReasons: [], warnings: [], canExecute: true,
    })
    const copy = vi.fn().mockResolvedValue({ sourceProjectId: source.id, targetProjectId: target.id, documentCount: 4, bomItemCount: 8, validationItemCount: 3, projectFileCount: 1, totalBytes: 2048 })
    const wrapper = mount(ProjectSettingsDrawer, {
      props: {
        modelValue: true, project: target, projects: [target, source], canCopyContent: true, pending: false,
        onPreviewProjectCopy: preview, onCopyProjectContent: copy,
      },
      global: { stubs: {
        ElDrawer: { props: ['modelValue', 'title'], emits: ['update:modelValue'], template: '<section role="dialog"><h2>{{ title }}</h2><slot /><footer><slot name="footer" /></footer></section>' },
        ElInput: { props: ['modelValue'], template: '<input :value="modelValue" disabled>' },
        ElSelect: { name: 'ElSelect', props: ['modelValue', 'placeholder'], emits: ['update:modelValue', 'change'], template: '<div><slot /></div>' },
        ElOption: { props: ['label', 'value'], template: '<span>{{ label }}</span>' },
        ElCheckbox: { props: ['modelValue', 'value'], emits: ['update:modelValue', 'change'], template: '<label><slot /></label>' },
        ElCheckboxGroup: { props: ['modelValue'], emits: ['update:modelValue', 'change'], template: '<div><slot /></div>' },
      } },
    })

    expect(wrapper.text()).toContain('项目内容复制')
    expect(wrapper.text()).toContain('验证计划检查项目')
    const sourceSelect = wrapper.findAllComponents({ name: 'ElSelect' }).find(item => item.props('placeholder') === '选择已有项目')
    expect(sourceSelect).toBeDefined()
    sourceSelect!.vm.$emit('update:modelValue', source.id)
    sourceSelect!.vm.$emit('change', source.id)
    await flushPromises()

    expect(preview).toHaveBeenCalledWith(target.id, expect.objectContaining({
      sourceProjectId: source.id,
      copyModels: true,
      copyDrawings: true,
      copyBom: true,
      copyValidationItems: true,
      folderIds: null,
    }))
    expect(wrapper.text()).toContain('仅“气路时序”默认勾选')
    expect(wrapper.text()).toContain('验证检查项 3')

    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    await wrapper.findAll('button').find(item => item.text() === '确认复制')!.trigger('click')
    await flushPromises()
    expect(copy).toHaveBeenCalledWith(target.id, expect.objectContaining({ folderIds: ['gas-folder'] }))
    expect(wrapper.emitted('update:modelValue')).toContainEqual([false])
    wrapper.unmount()
  })
})
