import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MaterialRelationEditor from '../src/components/MaterialRelationEditor.vue'

const api = vi.hoisted(() => ({
  listMaterialRelationTemplates: vi.fn(),
  listMaterials: vi.fn(),
  saveMaterialRelationTemplate: vi.fn(),
  publishMaterialRelationTemplate: vi.fn(),
}))
vi.mock('../src/api', () => api)

const mainMaterial = {
  id: 'main-1', materialCode: 'M-001', name: '伺服电机', specification: '750W', remark: '带抱闸',
  kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', approvalStatus: 'Approved', syncStatus: 'Succeeded',
  createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1, isArchived: false,
  u9SyncConfirmed: true, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 0, model3DAttachmentCount: 0, documentAttachmentCount: 0,
} as const
const accessory = { ...mainMaterial, id: 'accessory-1', materialCode: 'C-001', name: '伺服控制器', specification: '1kW', remark: 'EtherCAT' }
const relation = {
  id: 'relation-1', mainMaterialId: mainMaterial.id, mainMaterialCode: mainMaterial.materialCode, mainMaterialName: mainMaterial.name,
  name: 'M-001关联物料', isArchived: false, updatedBy: 'standard', updatedAt: '2026-09-05T08:00:00Z', rowVersion: 1,
  publishedRevision: null,
  draftRevision: {
    id: 'revision-1', version: 1, state: 'Draft', changeNote: '首次配置', createdBy: 'standard', createdAt: '', rowVersion: 1,
    groups: [{
      id: 'group-1', name: '控制器', isRequired: true, selectionMode: 0, minSelection: 1, maxSelection: 1, autoSelectUnique: true, sortOrder: 1,
      options: [{ id: 'option-1', materialId: accessory.id, materialCode: accessory.materialCode, materialName: accessory.name, materialKind: 'Standard', unitCode: '001', quantityMode: 0, quantityPerSet: 1, isDefault: true, sortOrder: 1 }],
    }],
  },
}

describe('MaterialRelationEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.listMaterialRelationTemplates.mockResolvedValue([relation])
    api.listMaterials.mockResolvedValue([mainMaterial, accessory])
    api.saveMaterialRelationTemplate.mockResolvedValue(relation)
  })

  afterEach(() => { document.body.innerHTML = '' })

  it('maintains association data directly for the selected material without template wording', async () => {
    const wrapper = mount(MaterialRelationEditor, {
      props: { token: 'token', mainMaterial, canManage: true, canPublish: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('M-001')
    expect(wrapper.text()).toContain('伺服电机')
    expect(wrapper.text()).toContain('待发布生效 · V1')
    expect(wrapper.text()).toContain('只能选 1 项')
    expect(wrapper.text()).toContain('可同时选多项')
    expect(wrapper.text()).not.toContain('模板名称')
    expect(wrapper.text()).not.toContain('新建模板')
    expect(wrapper.find('.material-relation-group').findComponent({ name: 'ElSelect' }).props('modelValue')).toBe('Single')
    expect(wrapper.find('.material-relation-option-row').findAllComponents({ name: 'ElSelect' })[1].props('modelValue')).toBe('PerMainQuantity')
    const headers = wrapper.findAll('.material-relation-option-head span').map(item => item.text())
    expect(headers).toEqual(['料号', '名称', '型号', '备注', '数量计算', '每套数量', '默认', '操作'])
    expect(wrapper.find('.material-relation-option-row').text()).toContain('伺服控制器')
    expect(wrapper.find('.material-relation-option-row').text()).toContain('1kW')
    expect(wrapper.find('.material-relation-option-row').text()).toContain('EtherCAT')

    await wrapper.findAll('button').find(button => button.text() === '保存修改')!.trigger('click')
    await flushPromises()
    expect(api.saveMaterialRelationTemplate).toHaveBeenCalledWith(expect.objectContaining({ mainMaterialId: 'main-1', name: 'M-001关联物料' }), 'token', 'relation-1')
  })

  it('shows the effective association read-only to engineers', async () => {
    api.listMaterialRelationTemplates.mockResolvedValue([{ ...relation, draftRevision: null, publishedRevision: { ...relation.draftRevision, state: 'Published' } }])
    const wrapper = mount(MaterialRelationEditor, {
      props: { token: 'token', mainMaterial, canManage: false, canPublish: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(api.listMaterialRelationTemplates).toHaveBeenCalledWith('token', false)
    expect(wrapper.text()).toContain('已生效 · V1')
    expect(wrapper.findAll('button').some(button => button.text() === '保存修改')).toBe(false)
    expect(wrapper.findAll('button').some(button => button.text() === '发布生效')).toBe(false)
  })
})
