import ElementPlus from 'element-plus'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import QualityInspectionPanel from '../src/components/QualityInspectionPanel.vue'
import type { ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({ readQualityInspections: vi.fn(), uploadQualityInspection: vi.fn(), readQualityInspectionFile: vi.fn() }))
vi.mock('../src/api', () => api)
vi.mock('../src/components/PdfDrawingViewer.vue', () => ({ default: { template: '<div />' } }))

describe('QualityInspectionPanel multiple files', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.readQualityInspections.mockResolvedValue([])
    api.uploadQualityInspection.mockResolvedValue({})
  })

  it.each(['incoming', 'assembly', 'preAcceptance', 'finalAcceptance'] as const)('%s keeps failed files for retry without uploading successful files twice', async kind => {
    const project = { id: 'project-1', code: 'P1', name: '项目', canReadContent: true, effectiveProjectPermissions: ['validation-plan.edit'] } as ProjectSummary
    const wrapper = mount(QualityInspectionPanel, {
      props: { kind, projects: [project], projectId: project.id, token: 'token', canEdit: true },
      global: { plugins: [ElementPlus], stubs: { teleport: true } },
    })
    await flushPromises()
    await wrapper.get('header button').trigger('click')
    await flushPromises()
    const customer = kind === 'preAcceptance' || kind === 'finalAcceptance'
    const savedKind = customer ? (kind === 'preAcceptance' ? 'finalAcceptance' : 'preAcceptance') : kind
    if (customer) {
      expect(wrapper.find('input[maxlength="200"]').exists()).toBe(false)
      const selector = wrapper.get('select[aria-label="检验内容"]')
      expect(selector.findAll('option').map(option => option.text())).toEqual(['预验收', '终验收'])
      await selector.setValue(savedKind)
    } else await wrapper.get('input[maxlength="200"]').setValue('检验报告')
    const input = wrapper.get('input[type="file"]')
    expect(input.attributes('multiple')).toBeDefined()
    const files = [new File(['first'], 'one.pdf'), new File(['second'], 'two.pdf')]
    Object.defineProperty(input.element, 'files', { value: [files[0]], configurable: true })
    await input.trigger('change')
    Object.defineProperty(input.element, 'files', { value: [files[1]], configurable: true })
    await input.trigger('change')
    Object.defineProperty(input.element, 'files', { value: [files[0]], configurable: true })
    await input.trigger('change')
    expect(input.attributes('required')).toBeUndefined()
    expect(wrapper.findAll('.quality-inspection__upload-list li').map(row => row.text())).toEqual(['one.pdf待上传', 'two.pdf待上传'])
    api.uploadQualityInspection.mockResolvedValueOnce({}).mockRejectedValueOnce(new Error('连接中断')).mockResolvedValueOnce({})
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('.quality-inspection__upload-progress').text()).toContain('已保存 1 个文件，待上传 1 个')
    expect(wrapper.findAll('.quality-inspection__upload-list li').map(row => row.text())).toEqual(['one.pdf已保存', 'two.pdf上传失败'])
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.uploadQualityInspection.mock.calls.map(call => call[5].name)).toEqual(['one.pdf', 'two.pdf', 'two.pdf'])
    expect(api.uploadQualityInspection.mock.calls.every(call => call[1] === savedKind && call[2] === '' && call[3] === (customer ? (savedKind === 'preAcceptance' ? '预验收' : '终验收') : '检验报告'))).toBe(true)
    wrapper.unmount()
  })
})
