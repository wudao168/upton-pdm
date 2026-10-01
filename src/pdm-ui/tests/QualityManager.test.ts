import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import QualityManager from '../src/components/QualityManager.vue'
vi.mock('../src/api', () => ({ readQualityUploadAccess: vi.fn().mockResolvedValue(['quality', 'incoming', 'assembly', 'preAcceptance', 'finalAcceptance']) }))
vi.mock('../src/statusMessage', () => ({ ElMessage: { error: vi.fn() } }))
vi.mock('../src/components/ValidationPlanManager.vue', () => ({ default: { props: { qualityAcceptance: Boolean }, template: '<section>{{ qualityAcceptance ? "质量验收" : "验证计划" }}</section>' } }))
vi.mock('../src/components/QualityInspectionPanel.vue', () => ({ default: { props: ['kind'], template: '<section>{{ kind }}</section>' } }))
describe('quality secondary pages', () => {
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
