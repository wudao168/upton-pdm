import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import StorageSettings from '../src/components/StorageSettings.vue'
import type { PdmSystemSettings } from '../src/types'

const mappings: PdmSystemSettings['bomPropertyMappings'] = [
  { pdmPropertyKey: 'kind', pdmPropertyName: '物料分类', solidWorksProperty: '物料分类', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'wearPart', pdmPropertyName: '易损件', solidWorksProperty: '易损件', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'drawingNumber', pdmPropertyName: '物料编码', solidWorksProperty: '物料编码', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'heatTreatment', pdmPropertyName: '热处理', solidWorksProperty: '热处理', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'quantity', pdmPropertyName: '数量', solidWorksProperty: '', source: 'Assembly', mappingEditable: false },
  { pdmPropertyKey: 'revision', pdmPropertyName: '版本', solidWorksProperty: '', source: 'Pdm', mappingEditable: false },
]

const settings: PdmSystemSettings = {
  vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release',
  checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60,
  checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48,
  bomDrawingNumberProperty: '物料编码', bomNameProperty: '物料名称', bomDescriptionProperty: '备注信息',
  bomMaterialProperty: '材质', bomSpecificationProperty: '型号', bomUnitProperty: '单位', bomBrandProperty: '品牌',
  bomSurfaceTreatmentProperty: '表面处理', bomWeightProperty: '重量', bomPropertyMappings: mappings,
  validationRules: { standard: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'], nonStandard: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'], electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'] },
}

describe('StorageSettings BOM property mappings', () => {
  it('renders the server mapping directory and saves a manually selected SolidWorks property', async () => {
    const onSaveSettings = vi.fn().mockImplementation(async input => input)
    const wrapper = mount(StorageSettings, {
      attachTo: document.body,
      props: {
        settings, equipmentTypes: [], numberingOptions: { organizations: [], projectTypes: [], equipmentTypes: [] }, pending: false,
        onSaveSettings, onSaveEquipmentType: vi.fn(), onUpdateCounters: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })

    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    const tab = wrapper.findAll('.el-tabs__item').find(item => item.text() === 'BOM属性映射')
    expect(tab).toBeDefined()
    await tab!.trigger('click')
    await flushPromises()

    expect(wrapper.get('table[aria-label="SolidWorks属性映射列表"]').findAll('tbody tr')).toHaveLength(mappings.length)
    expect(wrapper.text()).toContain('热处理')
    expect((wrapper.get('[aria-label="物料分类对应SolidWorks属性"]').element as HTMLInputElement).value).toBe('物料分类')
    expect((wrapper.get('[aria-label="易损件对应SolidWorks属性"]').element as HTMLInputElement).value).toBe('易损件')
    expect(wrapper.text()).toContain('装配结构自动计算')
    expect(wrapper.text()).toContain('PDM受控版本')

    await wrapper.get('[aria-label="热处理对应SolidWorks属性"]').setValue('热处理方式')
    await wrapper.get('[aria-label="物料分类对应SolidWorks属性"]').setValue('分类')
    await wrapper.get('[aria-label="易损件对应SolidWorks属性"]').setValue('易损件标识')
    await wrapper.get('[aria-label="物料编码对应SolidWorks属性"]').setValue('图号')
    const saveButton = wrapper.findAll('button.pdm-primary-action').find(button => button.text() === '保存属性映射')
    expect(saveButton).toBeDefined()
    await saveButton!.trigger('click')
    await flushPromises()

    expect(onSaveSettings).toHaveBeenCalledWith(expect.objectContaining({
      bomDrawingNumberProperty: '图号',
      bomPropertyMappings: expect.arrayContaining([
        expect.objectContaining({ pdmPropertyKey: 'kind', solidWorksProperty: '分类' }),
        expect.objectContaining({ pdmPropertyKey: 'wearPart', solidWorksProperty: '易损件标识' }),
        expect.objectContaining({ pdmPropertyKey: 'heatTreatment', solidWorksProperty: '热处理方式' }),
      ]),
    }))
    wrapper.unmount()
  })
})
