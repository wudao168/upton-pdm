import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import ProjectNameplateDialog from '../src/components/ProjectNameplateDialog.vue'
import * as api from '../src/api'
import type { ProjectNameplateView } from '../src/projectNameplate'
import type { ProjectSummary } from '../src/types'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), error: vi.fn() } }))
const base = (): ProjectNameplateView => ({ nameplate: { projectId: 'project', enabled: true, powerSupply: '', dimensions: '1500mm x 1800mm x 2200mm', airPressure: '0.5~0.7 MPa', weight: '12000 kg', factoryDate: '2026-10-01', rowVersion: 1, executionSavedBy: 'engineer', executionSavedAt: '2026-10-29T01:00:00Z' }, name: '设备一', model: 'AK-001', serialNumbers: ['70000001'], template: { title: '设备铭牌', labels: {}, rowVersion: 0 }, canEdit: true, canManageTemplate: false, canEditExecution: true, canEditHardware: false, canConfirm: false })
let wrapper: ReturnType<typeof mount>
const create = () => wrapper = mount(ProjectNameplateDialog, { props: { project: { id: 'project', code: 'P001' } as ProjectSummary, token: 'token', modelValue: true }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })
beforeEach(() => { vi.spyOn(api, 'readProjectNameplate').mockResolvedValue(base()); vi.spyOn(api, 'saveProjectNameplate'); vi.spyOn(api, 'confirmProjectNameplate') })
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks() })

it('执行工程师只能填写设备信息，不能修改电源或确认发布', async () => {
  create(); await flushPromises()
  await wrapper.get('button.el-button--primary').trigger('click')
  expect(wrapper.get('input[aria-label="长度"]').attributes('disabled')).toBeUndefined()
  expect(wrapper.get('input[aria-label="重量"]').attributes('disabled')).toBeUndefined()
  expect(wrapper.get('input[aria-label="电压"]').attributes('disabled')).toBeDefined()
  expect(wrapper.text()).toContain('保存执行信息')
  expect(wrapper.text()).not.toContain('确认并发布')
})

it('硬件填写电源后直接确认，先保存电源，再用新版本确认同一份内容', async () => {
  const hardware = { ...base(), canEditExecution: false, canEditHardware: true, canConfirm: true }
  vi.mocked(api.readProjectNameplate).mockResolvedValue(hardware)
  vi.mocked(api.saveProjectNameplate).mockImplementation(async value => ({ ...hardware, nameplate: { ...value, rowVersion: 2 } }))
  vi.mocked(api.confirmProjectNameplate).mockImplementation(async value => ({ ...hardware, canEdit: false, canConfirm: false, nameplate: { ...value, releasePackageId: 'release', releasePackageNumber: 'P001-MP-01', confirmedBy: 'hardware' } }))
  create(); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '编辑铭牌')!.trigger('click')
  expect(wrapper.get('input[aria-label="长度"]').attributes('disabled')).toBeDefined()
  expect(wrapper.get('input[aria-label="电压"]').attributes('disabled')).toBeUndefined()
  await wrapper.get('input[aria-label="电压"]').setValue('380')
  await wrapper.get('input[aria-label="频率"]').setValue('50')
  await wrapper.get('input[aria-label="电流"]').setValue('5')
  await wrapper.findAll('button').find(button => button.text() === '确认并发布')!.trigger('click')
  await flushPromises()
  expect(api.saveProjectNameplate).toHaveBeenCalledWith(expect.objectContaining({ powerSupply: '380V 50Hz / 5A', dimensions: base().nameplate.dimensions, rowVersion: 1 }), 'token')
  expect(api.confirmProjectNameplate).toHaveBeenCalledWith(expect.objectContaining({ powerSupply: '380V 50Hz / 5A', rowVersion: 2 }), 'token')
  expect(wrapper.text()).toContain('P001-MP-01')
  expect(wrapper.text()).toContain('已确认发布')
  expect(wrapper.get('[aria-label="铭牌状态"]').text()).toBe('铭牌已发布')
  expect(wrapper.text()).not.toContain('编辑铭牌')
})

it('保存不完整硬件信息时不标为硬件已填写', async () => {
  const view = base()
  vi.mocked(api.readProjectNameplate).mockResolvedValue({ ...view, nameplate: { ...view.nameplate, hardwareSavedAt: '2026-10-30T01:00:00Z', powerSupply: '380V / 5A' }, canEditExecution: false, canEditHardware: true, canConfirm: true })
  create(); await flushPromises()
  expect(wrapper.get('[aria-label="铭牌状态"]').text()).toContain('执行已填写')
  expect(wrapper.get('[aria-label="铭牌填写分工"]').text()).toContain('待填写完整')
})
