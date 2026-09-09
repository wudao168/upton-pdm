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
  id: 'kit-1', code: 'UKIT-000001', name: '安装附件套件', description: '仅供PDM工程引用', currentReleasedRevisionId: 'revision-1',
  revisions: [
    { id: 'revision-1', kitId: 'kit-1', versionNumber: 1, state: 'Released', changeNote: '首次发布', createdBy: 'admin', createdAt: '2026-09-08T00:00:00Z', publishedBy: 'admin', publishedAt: '2026-09-08T00:00:00Z', components: [{ id: 'component-1', revisionId: 'revision-1', materialId: 'material-1', materialCode: '0102000001', materialName: '必选螺栓', quantity: 5, unit: '001', isOptional: false, sortOrder: 1 }] },
    { id: 'revision-2', kitId: 'kit-1', versionNumber: 2, state: 'Draft', changeNote: '增加可选件', createdBy: 'admin', createdAt: '2026-09-08T01:00:00Z', components: [
      { id: 'component-2', revisionId: 'revision-2', materialId: 'material-1', materialCode: '0102000001', materialName: '必选螺栓', quantity: 5, unit: '001', isOptional: false, sortOrder: 1 },
      { id: 'component-3', revisionId: 'revision-2', materialId: 'material-2', materialCode: '0102000002', materialName: '可选垫圈', quantity: 2, unit: '001', isOptional: true, sortOrder: 2 },
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

  it('shows the independent UKIT code, draft version and required/optional counts', async () => {
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    expect(api.listEngineeringKits).toHaveBeenCalledWith('token', false)
    expect(wrapper.text()).toContain('UKIT-000001')
    expect(wrapper.text()).toContain('V02 草稿')
    expect(wrapper.text()).toContain('必选 1 · 可选 1')
    expect(wrapper.text()).toContain('套件本身不进入 U9C')
  })

  it('publishes the current draft without changing the UKIT code', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(EngineeringKitLibrary, { props: { token: 'token', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '发布')!.trigger('click')
    await flushPromises()

    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('V02'), '发布 UKIT-000001 新版本', expect.any(Object))
    expect(api.publishEngineeringKit).toHaveBeenCalledWith('kit-1', 3, 'token')
  })
})
