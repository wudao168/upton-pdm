import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import EngineeringKitLibrary from '../src/components/EngineeringKitLibrary.vue'

const api = vi.hoisted(() => ({
  listEngineeringKits: vi.fn(),
  listMaterials: vi.fn(),
  saveEngineeringKit: vi.fn(),
  publishEngineeringKit: vi.fn(),
}))
vi.mock('../src/api', () => api)

const kit = {
  id: 'kit-1', code: 'UKIT-000001', model: 'UKIT-000001', name: '安装附件套件', brand: 'UPTON', currentReleasedRevisionId: 'revision-1',
  revisions: [
    { id: 'revision-1', kitId: 'kit-1', versionNumber: 1, state: 'Released', changeNote: '首次发布', createdBy: 'admin', createdAt: '2026-09-08T00:00:00Z', publishedBy: 'admin', publishedAt: '2026-09-08T00:00:00Z', components: [{ id: 'component-1', revisionId: 'revision-1', materialId: 'material-1', materialCode: '0102000001', materialName: '螺栓', quantity: 5, unit: '001', isOptional: false, sortOrder: 1 }] },
    { id: 'revision-2', kitId: 'kit-1', versionNumber: 2, state: 'Draft', changeNote: '增加垫圈', createdBy: 'admin', createdAt: '2026-09-08T01:00:00Z', components: [
      { id: 'component-2', revisionId: 'revision-2', materialId: 'material-1', materialCode: '0102000001', materialName: '螺栓', quantity: 5, unit: '001', isOptional: false, sortOrder: 1 },
      { id: 'component-3', revisionId: 'revision-2', materialId: 'material-2', materialCode: '0102000002', materialName: '垫圈', quantity: 2, unit: '001', isOptional: false, sortOrder: 2 },
    ] },
  ],
  createdBy: 'admin', createdAt: '2026-09-08T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-08T01:00:00Z', rowVersion: 3,
}

describe('EngineeringKitLibrary', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    api.listEngineeringKits.mockReset().mockResolvedValue([kit])
    api.listMaterials.mockReset().mockResolvedValue([])
    api.saveEngineeringKit.mockReset()
    api.publishEngineeringKit.mockReset().mockResolvedValue({ ...kit, rowVersion: 4 })
  })

  it('shows the virtual kit fields and component count without a category column', async () => {
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    expect(api.listEngineeringKits).toHaveBeenCalledWith('token', false)
    expect(wrapper.text()).toContain('套件料号')
    expect(wrapper.text()).toContain('套件内物料数量')
    expect(wrapper.text()).toContain('UKIT-000001')
    expect(wrapper.text()).toContain('UPTON')
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
})
