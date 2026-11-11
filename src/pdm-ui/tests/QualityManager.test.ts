import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import QualityManager from '../src/components/QualityManager.vue'
import { readQualityUploadAccess } from '../src/api'
import { computed } from 'vue'
import { projectCenterAccessKey, projectCenterSchema } from '../src/projectCenterAccess'
vi.mock('../src/api', () => ({ readQualityUploadAccess: vi.fn().mockResolvedValue(['quality', 'incoming', 'assembly', 'preAcceptance', 'finalAcceptance']) }))
vi.mock('../src/statusMessage', () => ({ ElMessage: { error: vi.fn() } }))
vi.mock('../src/components/ValidationPlanManager.vue', () => ({ default: { name: 'ValidationPlanManager', props: { qualityAcceptance: Boolean, canEdit: Boolean }, template: '<section>{{ qualityAcceptance ? "质量验收" : "验证计划" }}</section>' } }))
vi.mock('../src/components/QualityInspectionPanel.vue', () => ({ default: { props: ['kind'], template: '<section>{{ kind }}</section>' } }))
beforeEach(() => vi.mocked(readQualityUploadAccess).mockResolvedValue(['quality', 'incoming', 'assembly', 'preAcceptance', 'finalAcceptance']))

describe('quality secondary pages', () => {
 it('主设计验证计划编辑权限不受质量验收资料上传权限限制', async () => {
  vi.mocked(readQualityUploadAccess).mockResolvedValue([])
  const wrapper = mount(QualityManager, { props: { projectId: 'p', projectCode: 'P1', projectName: '项目', projects: [{ id: 'p', canReadContent: true } as never], token: 'token', currentUsername: 'lead', currentDisplayName: '主设计', canEdit: true, canManageCatalog: false } })
  await flushPromises()
  const plans = wrapper.findAllComponents({ name: 'ValidationPlanManager' })
  expect(plans).toHaveLength(2)
  expect(plans[0]!.props('canEdit')).toBe(true)
  expect(plans[1]!.props('canEdit')).toBe(false)
  wrapper.unmount()
 })
 it('缺少项目写权限时，主设计也不能编辑验证计划', async () => {
  const wrapper = mount(QualityManager, { props: { projectId: 'p', projectCode: 'P1', projectName: '项目', projects: [{ id: 'p', canReadContent: true } as never], token: 'token', currentUsername: 'lead', currentDisplayName: '主设计', canEdit: true, canManageCatalog: false }, global: { provide: { [projectCenterAccessKey as symbol]: computed(() => [projectCenterSchema]) } } })
  await flushPromises()
  expect(wrapper.findAllComponents({ name: 'ValidationPlanManager' }).every(plan => !plan.props('canEdit'))).toBe(true)
  wrapper.unmount()
 })
 it('shows the correct two panels for each page', async () => {
  const wrapper = mount(QualityManager, { props: { projectId: 'p', projectCode: 'P1', projectName: '项目', projects: [{ id: 'p', canReadContent: true } as never], token: 'token', currentUsername: 'admin', currentDisplayName: '管理', canEdit: true, canManageCatalog: true } })
  await flushPromises()
  const tabs = wrapper.findAll('nav button')
  for (const [index, expected] of [['0', ['验证计划', '质量验收']], ['1', ['incoming', 'assembly']], ['2', ['preAcceptance', 'finalAcceptance']]] as const) {
   await tabs[Number(index)]!.trigger('click')
   const section = wrapper.findAll('.quality-manager__sections').find(x => (x.element as HTMLElement).style.display !== 'none')!
   expect(section.findAll('section').map(x => x.text())).toEqual(expected)
  }
  wrapper.unmount()
 })
})
