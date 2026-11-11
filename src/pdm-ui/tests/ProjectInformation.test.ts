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
].map(user => ({ ...user, isActive: true, canBeDesigner: user.role === 'Engineer' })) as PdmUser[]
const Select = defineComponent({ methods: { blur() { (this.$el as HTMLSelectElement).blur() } }, props: { modelValue: {}, multiple: Boolean }, emits: ['update:modelValue'], template: '<select :value="modelValue" :multiple="multiple" @change="$emit(\'update:modelValue\', multiple ? Array.from($event.target.selectedOptions).map(option => option.value) : $event.target.value)"><slot /></select>' })
const Dialog = defineComponent({ props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /><slot name="footer" /></div>' })
let wrapper: ReturnType<typeof mount>
function setup(allow = true) {
  const root = { id: 'root', code: 'P1', name: '主项目', organizationId: 'org', executionUnitId: 'unit', primaryProjectManager: 'pm', quantity: 1, serialNumbers: [], designers: ['mechanical'], collaborativeProjectManagers: [], canReadContent: true, canAssignDesigners: allow, effectiveProjectPermissions: allow ? ['project.phase-owners.manage'] : [], phaseOwners: { ElectricalEngineer: 'electrical', ElectricalHardware: 'hardware', MechanicalAssembly: 'fitter', ElectricalAssembly: 'electrician', Acceptance: 'mechanical' } } as unknown as ProjectSummary
  const child = { ...root, id: 'child', code: 'P1-1', parentProjectId: root.id, designers: [], phaseOwners: {} } as ProjectSummary
  const projects = [root, child]
  const save = vi.fn(async (id, owners) => ({ ...projects.find(item => item.id === id)!, phaseOwners: Object.fromEntries(Object.entries(owners).filter(([, value]) => value)) }))
  const saveDesigners = vi.fn(async (id, designers) => ({ ...projects.find(item => item.id === id)!, designers }))
  wrapper = mount(ProjectInformation, { props: { project: root, projects, users, directory: { users, units: [{ id: 'unit', organizationId: 'org', isActive: true }], memberships: users.map(user => ({ unitId: 'unit', username: user.username })), managers: [] } as unknown as OrganizationDirectory, token: 'test', currentUsername: 'pm', onUpdateMainStaffing: vi.fn(), onUpdateDesigners: saveDesigners, onUpdatePhaseOwners: save }, global: { provide: { [userDisplayNameKey as symbol]: (username: string) => resolveUserDisplayName(users, username) }, stubs: { ProjectNameplateDialog: true, ProjectContactInformation: { template: '<div><slot :staff="{salesPeople: \'\',presalesPeople: \'\'}" :can-edit="false" :save="async () => true" /></div>' }, 'el-dialog': Dialog, 'el-select': Select, 'el-option': { props: ['value', 'label'], template: '<option :value="value">{{ label }}</option>' }, 'el-button': { template: '<button><slot /></button>' } } } })
  const table = wrapper.find('[aria-label="项目基本信息列表"] table')
  const rows = () => table.findAll('tbody tr').map(row => row.findAll('td').slice(1).map(cell => cell.text()))
  return { table, rows, save, saveDesigners, root, child, projects }
}
afterEach(() => wrapper?.unmount())
it('只向有主项目人员分配权限的用户开放协同项目经理快捷入口', async () => {
  const { root, child } = setup()
  const entry = 'button[aria-label="分配协同项目经理"]'
  expect(wrapper.find(entry).exists()).toBe(false)
  const editableRoot = { ...root, canManageMainStaffing: true, designLead: 'fitter' }
  const saveStaffing = vi.fn(async (_id, input) => ({ ...editableRoot, ...input }))
  await wrapper.setProps({ project: child, projects: [editableRoot, child], onUpdateMainStaffing: saveStaffing })
  expect(wrapper.get(entry).text()).toBe('—')
  await wrapper.get(entry).trigger('click')
  expect(wrapper.find('select[aria-label="项目经理"]').exists()).toBe(false)
  expect(wrapper.find('select[aria-label="主设计"]').exists()).toBe(false)
  await wrapper.get('select[aria-label="协同项目经理"]').setValue(['electrical'])
  const dialog = wrapper.get('[role="dialog"]')
  await dialog.findAll('button').find(button => button.text() === '保存')!.trigger('click')
  await flushPromises()
  expect(saveStaffing).toHaveBeenCalledWith('root', { primaryProjectManager: 'pm', collaborativeProjectManagers: ['electrical'], designLeads: ['fitter'] })
  expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
})
it('团队分配包含设计人员，回填并保存到当前主项目且同步人员列表', async () => {
  const { saveDesigners, root } = setup()
  await wrapper.get('button[aria-label="分配团队人员"]').trigger('click'); await flushPromises()
  const designers = wrapper.get('select[aria-label="设计人员"]')
  expect((designers.element as HTMLSelectElement).selectedOptions[0]?.value).toBe('mechanical')
  await designers.setValue(['electrical'])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(saveDesigners).toHaveBeenCalledWith('root', ['electrical'])
  expect(root.designers).toEqual(['electrical'])
  expect(wrapper.get('.project-information__people').text()).toContain('电气员')
})
it('人员空值显示横杠，团队分配无设计权限时禁用设计人员', async () => {
  setup(false)
  expect(wrapper.get('.project-information__people tbody').text()).not.toContain('待分配')
  expect(wrapper.get('.project-information__people tbody').text()).toContain('—')
})
it('项目族铭牌状态只请求一次，并按各项目保留编辑状态', async () => {
  vi.mocked(api.readProjectNameplate).mockClear()
  vi.mocked(api.readProjectNameplate).mockResolvedValueOnce([
    { projectId: 'root', nameplate: { projectId: 'root', enabled: false }, canEdit: false },
    { projectId: 'child', nameplate: { projectId: 'child', enabled: true }, canEdit: true },
  ] as any)
  setup(); await flushPromises()
  expect(api.readProjectNameplate).toHaveBeenCalledTimes(1)
  expect(api.readProjectNameplate).toHaveBeenCalledWith('root', 'test', true)
  expect(wrapper.find('button[aria-label="查看铭牌 P1-1"]').exists()).toBe(true)
})
it('shows the four clickable columns in order with independent child assignments', () => {
  const { table, rows } = setup()
  expect(table.findAll('th').map(cell => cell.text()).slice(7, 13)).toEqual(['设计人员', '电气工程师', '硬件工程师', '机械装配', '电气装配', '数量'])
  expect(rows()[0]!.slice(7, 11)).toEqual(['电气员', '硬件员', '装配员', '接线员'])
  expect(rows()[1]!.slice(7, 11)).toEqual(['—', '—', '—', '—'])
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
  expect(rows()[0]![7]).toBe('—')
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
  expect(rows()[1]![7]).toBe('—')
  expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('electrical')
})
it('cancels without saving and hides assignment buttons without permission', async () => {
  const { save } = setup()
  await wrapper.get('button[aria-label="分配机械装配 P1-1"]').trigger('click')
  await wrapper.get('button[aria-label="取消人员分配"]').trigger('click'); expect(save).not.toHaveBeenCalled()
  wrapper.unmount(); setup(false)
  expect(wrapper.find('button[aria-label="分配电气工程师 P1"]').exists()).toBe(false)
})

it('selects individual projects, shows mixed selection, and selects only eligible rows', async () => {
  const { root, child } = setup()
  const all = wrapper.get<HTMLInputElement>('input[aria-label="全选可分配项目"]')
  const action = wrapper.get('button[aria-label="批量分配人员"]')
  expect(action.attributes('disabled')).toBeDefined()
  await wrapper.get('input[aria-label="选择项目 P1-1"]').setValue(true)
  expect(all.element.indeterminate).toBe(true)
  expect(wrapper.text()).toContain('已选 1 项')
  await all.setValue(true)
  expect(wrapper.text()).toContain('已选 2 项')
  expect(all.element.indeterminate).toBe(false)
  await wrapper.setProps({ project: { ...root }, projects: [{ ...root }, { ...child }] })
  expect(wrapper.text()).toContain('已选 2 项')
  await wrapper.setProps({ projects: [root, { ...child, canAssignDesigners: false }] })
  expect(wrapper.get('input[aria-label="选择项目 P1-1"]').attributes('disabled')).toBeDefined()
  expect(wrapper.text()).toContain('已选 1 项')
  expect(all.element.checked).toBe(true)
  await all.setValue(false)
  expect(action.attributes('disabled')).toBeDefined()
})

it('assigns multiple engineers only to checked projects and leaves other positions intact', async () => {
  const { saveDesigners, save, rows } = setup()
  await wrapper.get('input[aria-label="选择项目 P1-1"]').setValue(true)
  await wrapper.get('button[aria-label="批量分配人员"]').trigger('click')
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click')
  expect(wrapper.get('[role="alert"]').text()).toBe('请选择至少一名人员。')
  expect(saveDesigners).not.toHaveBeenCalled()
  await wrapper.get('select[aria-label="设计人员"]').setValue(['mechanical', 'electrical'])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(saveDesigners.mock.calls).toEqual([['child', ['mechanical', 'electrical']]])
  expect(save).not.toHaveBeenCalled()
  expect(rows()[1]![6]).toBe('机械员、电气员')
  expect(rows()[0]!.slice(7, 11)).toEqual(['电气员', '硬件员', '装配员', '接线员'])
  expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
})

it.each([
  ['ElectricalEngineer', 'electrical'], ['ElectricalHardware', 'hardware'],
  ['MechanicalAssembly', 'fitter'], ['ElectricalAssembly', 'electrician'],
] as const)('assigns %s to all selected projects while preserving each project’s other positions', async (key, username) => {
  const { save, root, child } = setup()
  const before = { ...root.phaseOwners }
  await wrapper.get('input[aria-label="全选可分配项目"]').setValue(true)
  await wrapper.get('button[aria-label="批量分配人员"]').trigger('click')
  await wrapper.get('select[aria-label="批量分配岗位"]').setValue(key)
  await wrapper.get('select[multiple]').setValue([username])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(save.mock.calls).toEqual([['root', { ...before, [key]: username }], ['child', { [key]: username }]])
  expect(child.phaseOwners?.[key]).toBe(username)
  expect(root.designers).toEqual(['mechanical'])
})

it('reports partial completion and retries only the unsaved projects', async () => {
  const { save, root, child } = setup()
  save.mockResolvedValueOnce({ ...root, phaseOwners: { ...root.phaseOwners, MechanicalAssembly: 'electrician' } }).mockRejectedValueOnce(new Error('private failure'))
  await wrapper.get('input[aria-label="全选可分配项目"]').setValue(true)
  await wrapper.get('button[aria-label="批量分配人员"]').trigger('click')
  await wrapper.get('select[aria-label="批量分配岗位"]').setValue('MechanicalAssembly')
  await wrapper.get('select[aria-label="机械装配"]').setValue(['electrician'])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('已保存 1 个项目，剩余 1 个未保存（从 P1-1 开始）')
  expect(wrapper.text()).not.toContain('private failure')
  expect(root.phaseOwners?.MechanicalAssembly).toBe('electrician')
  expect(child.phaseOwners).toEqual({})
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(save.mock.calls.map(call => call[0])).toEqual(['root', 'child', 'child'])
  expect(child.phaseOwners?.MechanicalAssembly).toBe('electrician')
  expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
})

it('clears selection when changing project family and limits batch positions to existing permissions', async () => {
  const { root, child, save, saveDesigners } = setup()
  const designOnlyRoot = { ...root, effectiveProjectPermissions: [] }
  await wrapper.setProps({ project: designOnlyRoot, projects: [designOnlyRoot, child], currentUsername: 'mechanical' })
  await wrapper.get('input[aria-label="全选可分配项目"]').setValue(true)
  await wrapper.get('button[aria-label="批量分配人员"]').trigger('click')
  expect(wrapper.get('select[aria-label="批量分配岗位"]').findAll('option').map(option => option.text())).toEqual(['设计人员'])
  await wrapper.get('button[aria-label="取消人员分配"]').trigger('click')
  expect(save).not.toHaveBeenCalled(); expect(saveDesigners).not.toHaveBeenCalled()
  const other = { ...root, id: 'other', code: 'P2' }
  await wrapper.setProps({ project: other, projects: [root, child, other] })
  expect(wrapper.text()).toContain('已选 0 项')
  expect(wrapper.get('button[aria-label="批量分配人员"]').attributes('disabled')).toBeDefined()
})

it('系统管理员不必是项目经理即可单项和批量分配岗位人员', async () => {
  const { save } = setup()
  await wrapper.setProps({ currentUsername: 'admin', administrator: true })
  expect(wrapper.find('button[aria-label="分配电气工程师 P1-1"]').exists()).toBe(true)
  await wrapper.get('input[aria-label="全选可分配项目"]').setValue(true)
  await wrapper.get('button[aria-label="批量分配人员"]').trigger('click')
  await wrapper.get('select[aria-label="批量分配岗位"]').setValue('ElectricalEngineer')
  await wrapper.get('select[aria-label="电气工程师"]').setValue(['electrical'])
  await wrapper.get('button[aria-label="保存人员分配"]').trigger('click'); await flushPromises()
  expect(save.mock.calls.map(call => call[0])).toEqual(['root', 'child'])
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
