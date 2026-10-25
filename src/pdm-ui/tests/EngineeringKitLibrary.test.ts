import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import EngineeringKitLibrary from '../src/components/EngineeringKitLibrary.vue'

const api = vi.hoisted(() => ({
  listEngineeringKits: vi.fn(),
  listMaterials: vi.fn(),
  saveEngineeringKit: vi.fn(),
  publishEngineeringKit: vi.fn(),
  setEngineeringKitActive: vi.fn(),
  listStandardLibraryCategories: vi.fn(),
}))
vi.mock('../src/api', () => api)

const kit = {
  id: 'kit-1', code: 'UKIT-000001', model: 'UKIT-000001', name: '安装附件套件', brand: 'UPTON', description: '用于现场安装', currentReleasedRevisionId: 'revision-1',
  revisions: [
    { id: 'revision-1', kitId: 'kit-1', versionNumber: 1, state: 'Released', changeNote: '首次发布', createdBy: 'admin', createdAt: '2026-09-08T00:00:00Z', publishedBy: 'admin', publishedAt: '2026-09-08T00:00:00Z', components: [{ id: 'component-1', revisionId: 'revision-1', materialId: 'material-1', materialCode: '0102000001', materialName: '螺栓', quantity: 5, unit: '001', isOptional: false, sortOrder: 1 }] },
    { id: 'revision-2', kitId: 'kit-1', versionNumber: 2, state: 'Draft', changeNote: '增加垫圈', createdBy: 'admin', createdAt: '2026-09-08T01:00:00Z', components: [
      { id: 'component-2', revisionId: 'revision-2', materialId: 'material-1', materialCode: '0102000001', materialName: '螺栓', quantity: 5, unit: '001', isOptional: false, sortOrder: 1 },
      { id: 'component-3', revisionId: 'revision-2', materialId: 'material-2', materialCode: '0102000002', materialName: '垫圈', quantity: 2, unit: '001', isOptional: false, sortOrder: 2 },
    ] },
  ],
  createdBy: 'admin', createdAt: '2026-09-08T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-08T01:00:00Z', rowVersion: 3,
}

const realMaterial = {
  id: 'real-material', materialCode: '01021000185', name: 'T型支座', specification: 'GCL08-D12-H10',
  brand: 'YHDA', approvalStatus: 'Approved', isArchived: false,
}

describe('EngineeringKitLibrary', () => {
  afterEach(() => vi.unstubAllGlobals())
  beforeEach(() => {
    vi.restoreAllMocks()
    api.listEngineeringKits.mockReset().mockResolvedValue([kit])
    api.listMaterials.mockReset().mockResolvedValue([])
    api.listStandardLibraryCategories.mockReset().mockResolvedValue([
      { id: 'category-1', name: '结构件', code: 'UTJ-01', isActive: true, parentId: null },
      { id: 'category-child', name: '定位件', isActive: true, parentId: 'category-1' },
    ])
    api.saveEngineeringKit.mockReset()
    api.publishEngineeringKit.mockReset().mockResolvedValue({ ...kit, rowVersion: 4 })
  })

  it('手输小数按数值保存草稿，重新打开保留小数且不补零', async () => {
    api.saveEngineeringKit.mockImplementation(async input => ({ ...kit, standardLibraryCategoryId: 'category-child', rowVersion: 4,
      revisions: [kit.revisions[0], { ...kit.revisions[1], standardLibraryCategoryId: 'category-child', components: kit.revisions[1].components.map((item, index) => ({ ...item, quantity: input.components[index].quantity })) }],
    }))
    const wrapper = mount(EngineeringKitLibrary, { attachTo: document.body, props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click')
    wrapper.getComponent({ name: 'ElTreeSelect' }).vm.$emit('update:modelValue', 'category-child')
    const input = wrapper.get('.engineering-kit-group .el-input-number input')
    await input.setValue('1.0500'); await input.trigger('change'); await input.trigger('blur'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '保存草稿')!.trigger('click')
    await flushPromises()
    expect(api.saveEngineeringKit).toHaveBeenCalledWith(expect.objectContaining({ components: [expect.objectContaining({ quantity: 1.05 }), expect.objectContaining({ quantity: 2 })] }), 'token', 'kit-1')
    await wrapper.findAll('button').find(button => button.text() === '返回套件列表')!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click')
    expect((wrapper.get('.engineering-kit-group .el-input-number input').element as HTMLInputElement).value).toBe('1.05')
    wrapper.unmount()
  })

  it('shows the virtual kit fields, remark and component count without a category column', async () => {
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    expect(api.listEngineeringKits).toHaveBeenCalledWith('token', false)
    expect(wrapper.text()).toContain('套件料号')
    expect(wrapper.text()).toContain('套件内物料数量')
    expect(wrapper.text()).toContain('UKIT-000001')
    expect(wrapper.text()).toContain('UPTON')
    expect(wrapper.text()).toContain('用于现场安装')
    expect(wrapper.text()).toContain('V02 草稿')
    expect(wrapper.text()).not.toContain('分类')
    expect(wrapper.text()).not.toContain('可选')
  })

  it('uses one fixed detail group with a 200px name field and no add-group control', async () => {
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建套件')!.trigger('click')

    expect(wrapper.text()).toContain('套件主项为虚拟对象')
    expect(wrapper.text()).toContain('发布时生成')
    expect(wrapper.text()).toContain('备注信息')
    expect(wrapper.findAll('button').some(button => button.text() === '代码维护')).toBe(false)
    expect(wrapper.findComponent({ name: 'ElTreeSelect' }).exists()).toBe(true)
    expect(wrapper.findComponent({ name: 'ElTreeSelect' }).props('data')[0].children[0].label).toBe('定位件（UTJ-01）')
    expect(wrapper.findAll('th').map(cell => cell.text())).toContain('备注')
    expect(wrapper.find('.engineering-kit-group-name').exists()).toBe(true)
    expect((wrapper.find('.engineering-kit-group-name input').element as HTMLInputElement).value).toBe('套件明细')
    expect(wrapper.text()).not.toContain('添加明细组')
    expect(wrapper.text()).not.toContain('需要工程师核对')
  })

  it('publishes the current draft from the full-page editor', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '发布生效')!.trigger('click')
    await flushPromises()

    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('唯一明细组'), '发布 UKIT-000001 新版本', expect.any(Object))
    expect(api.publishEngineeringKit).toHaveBeenCalledWith('kit-1', 3, 'token')
  })

  it('套件列表与维护页共用启停状态，恢复后才能发布', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const stopped = { ...kit, isActive: false, rowVersion: 4 }
    api.setEngineeringKitActive.mockResolvedValueOnce(stopped).mockResolvedValueOnce({ ...kit, isActive: true, rowVersion: 5 })
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.getComponent({ name: 'EngineeringKitActivityButton' }).get('button').trigger('click'); await flushPromises()
    expect(api.setEngineeringKitActive).toHaveBeenLastCalledWith(kit.id, false, kit.rowVersion, 'token')
    expect(wrapper.text()).toContain('已停用')
    api.listEngineeringKits.mockResolvedValue([stopped])
    await wrapper.setProps({ active: false }); await wrapper.setProps({ active: true }); await flushPromises()
    expect(api.listEngineeringKits).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('已停用')
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    expect(wrapper.findAll('button').find(button => button.text() === '发布生效')!.attributes('disabled')).toBeDefined()
    await wrapper.getComponent({ name: 'EngineeringKitActivityButton' }).get('button').trigger('click'); await flushPromises()
    expect(api.setEngineeringKitActive).toHaveBeenLastCalledWith(kit.id, true, 4, 'token')
    expect(wrapper.findAll('button').find(button => button.text() === '发布生效')!.attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('工程师可点击数量查看已发布明细，且无维护入口', async () => {
    api.listEngineeringKits.mockResolvedValue([{ ...kit, revisions: [kit.revisions[0]] }])
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '1')!.trigger('click')
    expect(wrapper.getComponent({ name: 'EngineeringKitDetails' }).props('kit')?.id).toBe('kit-1')
    expect(wrapper.findAll('button').some(button => button.text() === '维护')).toBe(false)
    expect(api.listEngineeringKits).toHaveBeenCalledWith('token', true)
    wrapper.unmount()
  })

  it('识别实际接口的数字草稿状态，保留手动型号并在发布后显示已发布', async () => {
    const actualApi = await vi.importActual<typeof import('../src/api')>('../src/api')
    const draft = { ...kit, code: null, model: 'R722.S2.12-00', modelMode: 1,
      currentReleasedRevisionId: null, revisions: [{ ...kit.revisions[1], state: 0 }] }
    const released = { ...draft, code: 'UKIT-000002', rowVersion: 4, currentReleasedRevisionId: 'revision-2',
      revisions: [{ ...draft.revisions[0], state: 1 }] }
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify([draft]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(released), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    api.listEngineeringKits.mockImplementation(actualApi.listEngineeringKits)
    api.publishEngineeringKit.mockImplementation(actualApi.publishEngineeringKit)
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.text()).toContain('V02 草稿')
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click')
    await flushPromises()
    expect((wrapper.get('input[placeholder="请输入套件型号"]').element as HTMLInputElement).value).toBe('R722.S2.12-00')
    expect(wrapper.get('.engineering-kit-group__count').text()).toBe('共 2 项')
    const publish = wrapper.findAll('button').find(button => button.text() === '发布生效')!
    expect(publish.attributes('disabled')).toBeUndefined()
    await publish.trigger('click')
    await flushPromises()
    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.any(String), '首次发布并生成套件料号和型号', expect.any(Object))
    expect(wrapper.get('.engineering-kit-summary').text()).toContain('已发布')
    expect(publish.attributes('disabled')).toBeDefined()
    expect(fetchMock).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('searches the server for real material outside the initial list and keeps selected details across searches', async () => {
    api.listMaterials.mockImplementation(async (_token: string, query: string) => query === '01021000185' ? [realMaterial] : [])
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建套件')!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '添加物料')!.trigger('click')
    const select = wrapper.get('.engineering-kit-group').getComponent({ name: 'ElSelect' })
    await select.props('remoteMethod')('01021000185')
    await flushPromises()
    expect(api.listMaterials).toHaveBeenCalledWith('token', '01021000185', false, 100)
    expect(select.findComponent({ name: 'ElOption' }).props('label')).toContain('01021000185 · T型支座')
    select.vm.$emit('update:modelValue', realMaterial.id)
    await flushPromises()
    expect(wrapper.get('.engineering-kit-group').text()).toContain('T型支座')
    expect(wrapper.get('.engineering-kit-group').text()).toContain('GCL08-D12-H10')
    expect(wrapper.get('.engineering-kit-group').text()).toContain('YHDA')
    await select.props('remoteMethod')('其他物料')
    await flushPromises()
    expect(wrapper.get('.engineering-kit-group').text()).toContain('GCL08-D12-H10')
    expect(select.props('modelValue')).toBe(realMaterial.id)
    expect(select.findAllComponents({ name: 'ElOption' })).toHaveLength(0)
    expect(select.text()).toContain('01021000185 · T型支座')
    wrapper.unmount()
  })

  it('新查询只显示本次匹配物料，前面已添加行保留料号名称和明细', async () => {
    const previous = { ...realMaterial, id: 'pump', materialCode: '01021000204', name: '罗茨真空泵', specification: 'BSJ150L', brand: '鲍斯' }
    const current = { ...realMaterial, id: 'iso', materialCode: '01021000071', name: 'CQ-90-ISO63', specification: 'ISO63' }
    api.listMaterials.mockImplementation(async (_token: string, query: string) => query === previous.materialCode ? [previous] : query === 'ISO63' ? [current] : [])
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建套件')!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '添加物料')!.trigger('click')
    const first = wrapper.get('.engineering-kit-group').getComponent({ name: 'ElSelect' })
    await first.props('remoteMethod')(previous.materialCode)
    first.vm.$emit('update:modelValue', previous.id)
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '添加物料')!.trigger('click')
    const second = wrapper.get('.engineering-kit-group').findAllComponents({ name: 'ElSelect' })[1]!
    await second.props('remoteMethod')('ISO63')
    await flushPromises()
    expect(second.findAllComponents({ name: 'ElOption' })).toHaveLength(1)
    expect(second.findComponent({ name: 'ElOption' }).props('label')).toBe('01021000071 · CQ-90-ISO63')
    expect(first.text()).toContain('01021000204 · 罗茨真空泵')
    expect(wrapper.get('.engineering-kit-group').text()).toContain('BSJ150L')
    expect(wrapper.get('.engineering-kit-group').text()).toContain('鲍斯')
    wrapper.unmount()
  })

  it('does not replace newer search results with a late response or offer unapproved and archived material', async () => {
    let completeOld!: (rows: typeof realMaterial[]) => void
    api.listMaterials.mockImplementation((_token: string, query: string) => query === 'old'
      ? new Promise(resolve => { completeOld = resolve })
      : Promise.resolve(query === '01021000185' ? [realMaterial, { ...realMaterial, id: 'pending', approvalStatus: 'Draft' }, { ...realMaterial, id: 'archived', isArchived: true }] : []))
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建套件')!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '添加物料')!.trigger('click')
    const select = wrapper.get('.engineering-kit-group').getComponent({ name: 'ElSelect' })
    const old = select.props('remoteMethod')('old')
    await select.props('remoteMethod')('01021000185')
    completeOld([{ ...realMaterial, id: 'old-material', materialCode: 'OLD' }])
    await old
    await flushPromises()
    expect(select.findAllComponents({ name: 'ElOption' }).map(option => option.props('value'))).toEqual([realMaterial.id])
    expect(select.props('loading')).toBe(false)
    wrapper.unmount()
  })

  it('loads existing kit component details even when they are outside the initial material list', async () => {
    api.listMaterials.mockImplementation(async (_token: string, query: string) => query === '0102000001'
      ? [{ ...realMaterial, id: 'material-1', materialCode: '0102000001', name: '螺栓' }] : [])
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click')
    await flushPromises()
    expect(api.listMaterials).toHaveBeenCalledWith('token', '0102000001', false, 20)
    expect(wrapper.get('.engineering-kit-group').text()).toContain('GCL08-D12-H10')
    expect(wrapper.get('.engineering-kit-group').text()).toContain('YHDA')
    wrapper.unmount()
  })
})
