import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent } from 'vue'
import ProjectInformation from '../src/components/ProjectInformation.vue'
import { resolveUserDisplayName, userDisplayNameKey } from '../src/userDisplay'
import type { OrganizationDirectory, PdmUser, ProjectSummary } from '../src/types'
import type { ProjectNameplateView } from '../src/projectNameplate'
import * as api from '../src/api'
vi.mock('../src/api', async importOriginal => ({ ...await importOriginal<typeof import('../src/api')>(), readProjectNameplate: vi.fn(async () => ({ nameplate: { enabled: false }, canEdit: false })), getProjectPositionMembers: vi.fn(async () => ({ positions: [], members: {} })) }))
const users = [
  { username: 'mechanical', displayName: '机械员', role: 'Engineer', roles: [] },
  { username: 'electrical', displayName: '电气员', role: 'Engineer', roles: ['ElectricalEngineer'] },
  { username: 'hardware', displayName: '硬件员', role: 'HardwareEngineer', roles: [] },
  { username: 'fitter', displayName: '装配员', role: 'AssemblyFitter', roles: [] },
  { username: 'electrician', displayName: '接线员', role: 'AssemblyElectrician', roles: [] },
].map(user => ({ ...user, isActive: true })) as PdmUser[]
const Select = defineComponent({ props: ['modelValue', 'multiple'], emits: ['update:modelValue'], template: '<select :value="modelValue" :multiple="multiple" @change="$emit(\'update:modelValue\', Array.from($event.target.selectedOptions).map(option => option.value))"><slot /></select>' })
const Dialog = defineComponent({ props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /><slot name="footer" /></div>' })
let wrapper: ReturnType<typeof mount>
function setup(allow = true) {
  const root = { id: 'root', code: 'P1', name: '主项目', organizationId: 'org', executionUnitId: 'unit', primaryProjectManager: 'pm', quantity: 1, serialNumbers: [], designers: ['mechanical'], collaborativeProjectManagers: [], canReadContent: true, canAssignDesigners: allow, phaseOwners: { ElectricalEngineer: 'electrical', ElectricalHardware: 'hardware', MechanicalAssembly: 'fitter', ElectricalAssembly: 'electrician', Acceptance: 'mechanical' } } as unknown as ProjectSummary
  const child = { ...root, id: 'child', code: 'P1-1', parentProjectId: root.id, designers: [], phaseOwners: {} } as ProjectSummary
  const projects = [root, child]
  const save = vi.fn(async (id, owners) => ({ ...projects.find(item => item.id === id)!, phaseOwners: Object.fromEntries(Object.entries(owners).filter(([, value]) => value)) }))
  wrapper = mount(ProjectInformation, { props: { project: root, projects, users, directory: { users, units: [{ id: 'unit', organizationId: 'org', isActive: true }], memberships: users.map(user => ({ unitId: 'unit', username: user.username })), managers: [] } as unknown as OrganizationDirectory, token: 'test', currentUsername: 'pm', onUpdateMainStaffing: vi.fn(), onUpdateDesigners: vi.fn(), onUpdatePhaseOwners: save }, global: { provide: { [userDisplayNameKey as symbol]: (username: string) => resolveUserDisplayName(users, username) }, stubs: { ProjectNameplateDialog: true, ProjectContactInformation: { template: '<div><slot :staff="{salesPeople: \'\',presalesPeople: \'\'}" :can-edit="false" :save="async () => true" /></div>' }, 'el-dialog': Dialog, 'el-select': Select, 'el-option': { props: ['value', 'label'], template: '<option :value="value">{{ label }}</option>' }, 'el-button': { template: '<button><slot /></button>' } } } })
  const table = wrapper.find('[aria-label="项目基本信息列表"] table')
  const rows = () => table.findAll('tbody tr').map(row => row.findAll('td').map(cell => cell.text()))
  return { table, rows, save }
}
afterEach(() => wrapper?.unmount())
it('shows the four clickable columns in order with independent child assignments', () => {
  const { table, rows } = setup()
  expect(table.findAll('th').map(cell => cell.text()).slice(6, 12)).toEqual(['执行工程师', '电气工程师', '硬件工程师', '机械装配', '电气装配', '数量'])
  expect(rows()[0]!.slice(7, 11)).toEqual(['电气员', '硬件员', '装配员', '接线员'])
  expect(rows()[1]!.slice(7, 11)).toEqual(['待分配', '待分配', '待分配', '待分配'])
  expect(table.findAll('tbody tr')[1]!.findAll('button')).toHaveLength(5)
})
it.each([
  ['电气工程师', 'ElectricalEngineer', 'electrical', 7], ['硬件工程师', 'ElectricalHardware', 'hardware', 8],
  ['机械装配', 'MechanicalAssembly', 'fitter', 9], ['电气装配', 'ElectricalAssembly', 'electrician', 10],
] as const)('saves %s to the selected child and immediately displays the returned assignment', async (label, key, username, column) => {
  const { rows, save } = setup()
  await wrapper.get(`button[aria-label="分配${label} P1-1"]`).trigger('click')
  await wrapper.get(`select[aria-label="${label}"]`).setValue([username])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(save).toHaveBeenCalledWith('child', { [key]: username })
  expect(rows()[1]![column]).toBe(resolveUserDisplayName(users, username))
  expect(rows()[0]!.slice(7, 11)).toEqual(['电气员', '硬件员', '装配员', '接线员'])
  expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
})
it('clears the root assignment without restoring legacy designers or changing other positions', async () => {
  const { rows, save } = setup()
  await wrapper.get('button[aria-label="分配电气工程师 P1"]').trigger('click')
  await wrapper.get('select[aria-label="电气工程师"]').setValue([])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(rows()[0]![7]).toBe('待分配')
  expect(rows()[0]!.slice(8, 11)).toEqual(['硬件员', '装配员', '接线员'])
  expect(save.mock.calls[0]![1]).toMatchObject({ Acceptance: 'mechanical', ElectricalHardware: 'hardware' })
})
it('preserves draft and existing data on failure and displays a Chinese error', async () => {
  const { rows, save } = setup(); save.mockRejectedValueOnce(new Error('ECONNREFUSED internal-host'))
  await wrapper.get('button[aria-label="分配电气工程师 P1-1"]').trigger('click')
  await wrapper.get('select[aria-label="电气工程师"]').setValue(['electrical'])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toBe('人员分配保存失败，请检查网络后重试。')
  expect(wrapper.text()).not.toContain('internal-host')
  expect(rows()[1]![7]).toBe('待分配')
  expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('electrical')
})
it('cancels without saving and hides assignment buttons without permission', async () => {
  const { save } = setup()
  await wrapper.get('button[aria-label="分配机械装配 P1-1"]').trigger('click')
  await wrapper.get('button[aria-label="取消人员分配"]').trigger('click'); expect(save).not.toHaveBeenCalled()
  wrapper.unmount(); setup(false)
  expect(wrapper.find('button[aria-label="分配电气工程师 P1"]').exists()).toBe(false)
})

it.each([
  ['执行已填写', true, false, false], ['硬件已填写', false, true, false],
  ['待硬件确认', true, true, false], ['已发布', true, true, true],
] as const)('铭牌入口根据实际填写与发布记录显示%s', async (label, execution, hardware, published) => {
  vi.mocked(api.readProjectNameplate).mockResolvedValue({
    nameplate: { projectId: 'root', enabled: true, dimensions: execution ? '1500mm x 1800mm x 2200mm' : '', weight: execution ? '12000 kg' : '', airPressure: execution ? '0.6 MPa' : '', factoryDate: execution ? '2026-10-01' : null, powerSupply: hardware ? '380V 50Hz / 5A' : '', rowVersion: 1, executionSavedAt: execution ? '2026-10-30T01:00:00Z' : null, hardwareSavedAt: hardware ? '2026-10-30T01:01:00Z' : null, releasePackageId: published ? 'release' : null },
    name: '设备', model: 'AK-001', serialNumbers: ['70000001'], template: { title: '设备铭牌', labels: {}, rowVersion: 0 }, canEdit: !published, canEditExecution: true, canEditHardware: true, canConfirm: !published, canManageTemplate: false,
  } satisfies ProjectNameplateView)
  setup(); await flushPromises()
  const entry = wrapper.get('button[aria-label="查看铭牌 P1"]')
  expect(entry.text()).toBe(label)
  expect(entry.classes('is-nameplate-published')).toBe(published)
  expect(entry.attributes('title')).toContain(published ? '内容只读' : '可继续编辑')
})
