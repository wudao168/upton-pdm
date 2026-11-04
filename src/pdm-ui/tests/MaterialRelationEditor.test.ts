import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MaterialRelationEditor from '../src/components/MaterialRelationEditor.vue'

const api = vi.hoisted(() => ({
  listMaterialRelationTemplates: vi.fn(),
  listMaterials: vi.fn(),
  listMaterialRelationMainOptions: vi.fn(),
  saveMaterialRelationTemplate: vi.fn(),
  publishMaterialRelationTemplate: vi.fn(),
  listMaterialRelationCategories: vi.fn(),
  getMaterialRelationCategories: vi.fn(),
  assignMaterialRelationCategory: vi.fn(),
}))
vi.mock('../src/api', () => api)

const mainMaterial = {
  id: 'main-1', materialCode: 'M-001', name: '伺服电机', specification: '750W', brand: 'MITSUBISHI', remark: '带抱闸',
  kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', approvalStatus: 'Approved', syncStatus: 'Succeeded',
  createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1, isArchived: false,
  u9SyncConfirmed: true, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 0, model3DAttachmentCount: 0, documentAttachmentCount: 0,
} as const
const accessory = { ...mainMaterial, id: 'accessory-1', materialCode: 'C-001', name: '伺服控制器', specification: '1kW', brand: 'YASKAWA', remark: 'EtherCAT' }
const modelMatch = { ...mainMaterial, id: 'accessory-2', materialCode: 'C-750', name: '伺服驱动器', specification: 'SV-750W', remark: '脉冲型' }
const relation = {
  id: 'relation-1', mainMaterialId: mainMaterial.id, mainMaterialCode: mainMaterial.materialCode, mainMaterialName: mainMaterial.name,
  name: 'M-001关联物料', isArchived: false, updatedBy: 'standard', updatedAt: '2026-09-05T08:00:00Z', rowVersion: 1,
  publishedRevision: null,
  draftRevision: {
    id: 'revision-1', version: 1, state: 'Draft', changeNote: '首次配置', createdBy: 'standard', createdAt: '', rowVersion: 1,
    groups: [{
      id: 'group-1', name: '控制器', isRequired: true, selectionMode: 0, minSelection: 1, maxSelection: 1, autoSelectUnique: true, sortOrder: 1,
      options: [{ id: 'option-1', materialId: accessory.id, materialCode: accessory.materialCode, materialName: accessory.name, materialKind: 'Standard', unitCode: '001', quantityMode: 0, quantityPerSet: 1, isDefault: true, sortOrder: 1, selectionAdvice: '适配 750W 电机' }],
    }],
  },
}

async function selectMain(wrapper: ReturnType<typeof mount>, id: string) {
  const select = wrapper.get('.material-relation-main-add-row').findComponent({ name: 'ElSelect' })
  select.vm.$emit('update:modelValue', id)
  select.vm.$emit('change', id)
  await flushPromises()
}

async function selectAccessory(wrapper: ReturnType<typeof mount>, id: string) {
  const select = wrapper.get('.material-relation-option-row.is-quick-entry').findComponent({ name: 'ElSelect' })
  select.vm.$emit('update:modelValue', id)
  select.vm.$emit('change', id)
  await flushPromises()
}

describe('MaterialRelationEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.listMaterialRelationMainOptions.mockImplementation(async (token, query) => (await api.listMaterials(token, query ?? '', false, 100)).filter((item: any) => item.approvalStatus === 'Approved' && !item.isArchived).map((item: any) => ({ materialId: item.id, materialCode: item.materialCode, materialName: item.name, specification: item.specification, brand: item.brand })))
    api.listMaterialRelationTemplates.mockResolvedValue([relation])
    api.listMaterials.mockResolvedValue([mainMaterial, accessory])
    api.listMaterialRelationCategories.mockResolvedValue([])
    api.getMaterialRelationCategories.mockResolvedValue([])
    api.saveMaterialRelationTemplate.mockResolvedValue(relation)
  })

  afterEach(() => { document.body.innerHTML = '' })

  it('精确料号搜索只显示命中项，已选料号仍保留正确显示', async () => {
    api.listMaterials.mockImplementation(async (_token, query) => query === modelMatch.materialCode ? [accessory, modelMatch] : [mainMaterial, accessory])
    const wrapper=mount(MaterialRelationEditor,{props:{token:'token',mainMaterial,canManage:true,canPublish:false},global:{plugins:[ElementPlus]}})
    await flushPromises()
    const selected=wrapper.get('.material-relation-option-row:not(.is-quick-entry)').findComponent({name:'ElSelect'})
    const quick=wrapper.get('.material-relation-option-row.is-quick-entry').findComponent({name:'ElSelect'})
    await quick.props('remoteMethod')(modelMatch.materialCode);await flushPromises()
    expect(quick.findAllComponents({name:'ElOption'}).map(option=>option.props('value'))).toEqual([modelMatch.id])
    expect(selected.props('modelValue')).toBe(accessory.id)
    expect(selected.text()).toContain(accessory.materialCode)
    expect(wrapper.get('.material-relation-option-row:not(.is-quick-entry)').text()).toContain(accessory.name)
    expect(api.saveMaterialRelationTemplate).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('无匹配结果时不显示历史已选项，并丢弃迟到的旧查询', async () => {
    const wrapper=mount(MaterialRelationEditor,{props:{token:'token',mainMaterial,canManage:true,canPublish:false},global:{plugins:[ElementPlus]}})
    await flushPromises()
    let finishOld!:(rows:typeof accessory[])=>void
    api.listMaterials.mockImplementation(async (_token, query) => query === 'OLD' ? new Promise(resolve=>{finishOld=resolve}) : [])
    const quick=wrapper.get('.material-relation-option-row.is-quick-entry').findComponent({name:'ElSelect'})
    const old=quick.props('remoteMethod')('OLD')
    await flushPromises()
    expect(quick.findAllComponents({name:'ElOption'})).toHaveLength(0)
    await quick.props('remoteMethod')('NO-MATCH');await flushPromises()
    finishOld([accessory]);await old;await flushPromises()
    expect(quick.findAllComponents({name:'ElOption'})).toHaveLength(0)
    expect(wrapper.get('.material-relation-option-row:not(.is-quick-entry)').text()).toContain(accessory.materialCode)
    wrapper.unmount()
  })

  it('料品套件可按型号搜索并作为共享主料保存', async () => {
    api.listMaterialRelationTemplates.mockResolvedValue([])
    api.listMaterialRelationMainOptions.mockResolvedValue([{ materialId: 'kit-id', materialCode: 'UKIT-000004', materialName: 'HMI组件', specification: 'UTJ-12-0001', brand: 'UPTON', isEngineeringKit: true }])
    const wrapper = mount(MaterialRelationEditor, { props: { token: 'token', canManage: true, canPublish: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const select = wrapper.get('.material-relation-main-add-row').findComponent({ name: 'ElSelect' })
    await select.props('remoteMethod')('UTJ-12-0001'); await flushPromises()
    expect(api.listMaterialRelationMainOptions).toHaveBeenCalledWith('token', 'UTJ-12-0001')
    await selectMain(wrapper, 'kit-id')
    expect(wrapper.get('[aria-label="共享配置主物料"]').text()).toContain('UKIT-000004')
    expect(wrapper.get('[aria-label="共享配置主物料"]').text()).toContain('UTJ-12-0001')
    await wrapper.get('[aria-label="关联配置名称"]').setValue('套件配套')
    await wrapper.findAll('button').find(button => button.text() === '添加配件组')!.trigger('click')
    await wrapper.get('[aria-label="分类名称（必填）"]').setValue('配件')
    await selectAccessory(wrapper, accessory.id)
    await wrapper.findAll('button').find(button => button.text() === '保存修改')!.trigger('click'); await flushPromises()
    expect(api.saveMaterialRelationTemplate.mock.calls.at(-1)?.[0].mainMaterialIds).toEqual(['kit-id'])
    wrapper.unmount()
  })

  it('新增配置直接维护多主料，统一保存同一份配件规则', async () => {
    const second = { ...mainMaterial, id: 'main-2', materialCode: 'M-002', name: '同系列电机' }
    api.listMaterialRelationTemplates.mockResolvedValue([])
    api.listMaterials.mockResolvedValue([mainMaterial, second, accessory])
    const category = { id: 'category', name: '机电标准件', isActive: true, sortOrder: 1 }
    api.listMaterialRelationCategories.mockResolvedValue([category])
    api.getMaterialRelationCategories.mockResolvedValue([category])
    const wrapper = mount(MaterialRelationEditor, { props: { token: 'token', canManage: true, canPublish: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.text()).toContain('主物料 0 个')
    expect(wrapper.findAll('button').some(button => button.text() === '开始配置关联物料')).toBe(false)
    expect(wrapper.find('.material-relation-main-toolbar .el-select').exists()).toBe(false)
    await selectMain(wrapper, mainMaterial.id)
    await selectMain(wrapper, second.id)
    expect(wrapper.findAll('.material-relation-main-table tbody tr:not(.is-quick-entry)')).toHaveLength(2)
    expect(wrapper.findAll('.material-relation-main-add-row')).toHaveLength(1)
    await wrapper.get('[aria-label="关联配置名称"]').setValue('同系列电机共享配套')
    await wrapper.findAll('button').find(button => button.text() === '添加配件组')!.trigger('click')
    await wrapper.get('[aria-label="分类名称（必填）"]').setValue('控制器')
    await selectAccessory(wrapper, accessory.id)
    expect(wrapper.findAll('.material-relation-option-row.is-quick-entry')).toHaveLength(1)
    expect(wrapper.get('.material-relation-option-row:not(.is-quick-entry)').get('input[role="spinbutton"]').attributes('aria-disabled')).not.toBe('true')
    await wrapper.findAll('button').find(button => button.text() === '保存修改')!.trigger('click'); await flushPromises()
    expect(api.saveMaterialRelationTemplate).toHaveBeenCalledWith(expect.objectContaining({ mainMaterialIds: ['main-1', 'main-2'], name: '同系列电机共享配套', groups: [expect.objectContaining({ options: [expect.objectContaining({ materialId: accessory.id })] })] }), 'token', undefined)
    wrapper.unmount()
  })

  it('主料已归属其他配置时不可直接覆盖，未保存的范围修改不可发布', async () => {
    const second = { ...mainMaterial, id: 'main-2', materialCode: 'M-002', name: '同系列电机' }
    api.listMaterials.mockResolvedValue([mainMaterial, second, accessory])
    api.getMaterialRelationCategories.mockResolvedValue([{ id: 'category', name: '机电件', isActive: true }])
    api.listMaterialRelationTemplates.mockResolvedValue([relation, { ...relation, id: 'other', mainMaterialId: second.id, mainMaterialCode: second.materialCode, name: '其他配置' }])
    const wrapper = mount(MaterialRelationEditor, { props: { token: 'token', templateId: relation.id, canManage: true, canPublish: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get('.material-relation-main-add-row .el-select__wrapper').trigger('click'); await flushPromises()
    const blocked = [...document.querySelectorAll('.el-select-dropdown__item')].find(option => option.textContent?.includes(second.materialCode) && option.textContent.includes('已归属'))!
    expect(blocked.classList.contains('is-disabled')).toBe(true)
    await wrapper.get('[aria-label="关联配置名称"]').setValue('修改共享名称')
    expect(wrapper.findAll('button').find(button => button.text() === '发布生效')!.attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })

  it('maintains association data directly for the selected material without template wording', async () => {
    const wrapper = mount(MaterialRelationEditor, {
      props: { token: 'token', mainMaterial, canManage: true, canPublish: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('M-001')
    expect(wrapper.text()).toContain('伺服电机')
    expect(wrapper.get('.material-relation-main-table tbody').text()).toContain('MITSUBISHI')
    expect(wrapper.text()).toContain('待发布生效 · V1')
    expect(wrapper.text()).toContain('只能选 1 项')
    expect(wrapper.text()).not.toContain('模板名称')
    expect(wrapper.text()).not.toContain('新建模板')
    expect(wrapper.find('.material-relation-group').findComponent({ name: 'ElSelect' }).props('modelValue')).toBe('Single')
    expect(wrapper.find('.material-relation-option-row').findAllComponents({ name: 'ElSelect' })[1].props('modelValue')).toBe('PerMainQuantity')
    const headers = wrapper.findAll('.material-relation-option-head span').map(item => item.text())
    expect(headers).toEqual(['料号', '名称', '型号', '品牌', '备注', '选型建议', '数量计算', '每套数量', '优先推荐', '操作'])
    expect(wrapper.find('[aria-label="分类名称（必填）"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('需要工程师核对')
    expect(wrapper.text()).not.toContain('不会自动加入 BOM')
    expect(wrapper.text()).not.toContain('只有一个选项时自动带出')
    expect(wrapper.find('.material-relation-option-row').text()).toContain('伺服控制器')
    expect(wrapper.find('.material-relation-option-row').text()).toContain('1kW')
    expect(wrapper.find('.material-relation-option-row').text()).toContain('YASKAWA')
    expect(wrapper.find('.material-relation-option-row').text()).toContain('EtherCAT')
    const adviceInput = wrapper.get('[aria-label="选型建议"]')
    expect(adviceInput.attributes('maxlength')).toBe('500')
    await adviceInput.setValue('优先选用 EtherCAT 版本')
    const quantityInput = wrapper.find('.material-relation-option-row').findComponent({ name: 'ElInputNumber' })
    expect(quantityInput.props('min')).toBe(1)
    expect(quantityInput.props('step')).toBe(1)
    expect(quantityInput.props('precision')).toBe(0)
    expect(api.listMaterials).toHaveBeenCalledWith('token', '', false, 100)
    expect(api.listMaterials).toHaveBeenCalledWith('token', 'C-001', false, 20)

    const materialSelect = wrapper.find('.material-relation-option-row').findAllComponents({ name: 'ElSelect' })[0]
    expect(materialSelect.props('remote')).toBe(true)
    expect(materialSelect.props('placeholder')).toBe('输入料号或型号搜索')
    api.listMaterials.mockResolvedValueOnce([modelMatch])
    await materialSelect.props('remoteMethod')('SV-750W')
    await flushPromises()
    expect(api.listMaterials).toHaveBeenCalledWith('token', 'SV-750W', false, 100)
    expect(document.body.textContent).toContain('C-750')
    expect(document.body.textContent).toContain('SV-750W')

    await wrapper.findAll('button').find(button => button.text() === '保存修改')!.trigger('click')
    await flushPromises()
    expect(api.saveMaterialRelationTemplate).toHaveBeenCalledWith(expect.objectContaining({
      mainMaterialId: 'main-1', name: 'M-001关联物料', groups: [expect.objectContaining({ isRequired: true, autoSelectUnique: false, options: [expect.objectContaining({ selectionAdvice: '优先选用 EtherCAT 版本' })] })],
    }), 'token', 'relation-1')
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
