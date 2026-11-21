import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import * as api from '../src/api'
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
  vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release', materialAttachmentRoot: 'D:\\PDM\\MaterialAttachments',
  checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60,
  checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48,
  bomDrawingNumberProperty: '物料编码', bomNameProperty: '物料名称', bomDescriptionProperty: '备注信息',
  bomMaterialProperty: '材质', bomSpecificationProperty: '型号', bomUnitProperty: '单位', bomBrandProperty: '品牌',
  bomSurfaceTreatmentProperty: '表面处理', bomWeightProperty: '重量', bomPropertyMappings: mappings,
  validationRules: { standard: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'], nonStandard: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'], electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'] },
  drawingQrPolicy: { enabled: true, sourceProperty: '型号', ruleVersion: '1', sizeMillimeters: 20, marginMillimeters: 5, everySheet: true },
}

describe('StorageSettings BOM property mappings', () => {
  it('edits the long-held checkout reminder and grace period independently', async () => {
    const onSaveSettings = vi.fn().mockImplementation(async input => input)
    const wrapper = mount(StorageSettings, {
      props: {
        settings, equipmentTypes: [], numberingOptions: { organizations: [], projectTypes: [], equipmentTypes: [] }, pending: false,
        onSaveSettings, onSaveEquipmentType: vi.fn(), onUpdateCounters: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })
    try {
      await wrapper.findAll('[role="tab"]').find(tab => tab.text() === '编辑权限')!.trigger('click')
      const pane = wrapper.get('#pane-checkout-policy')
      const duration = (caption: string) => pane.findAll('label').find(label => label.text().startsWith(caption))!.get<HTMLInputElement>('input')
      const reminder = duration('占用提醒（小时）')
      const grace = duration('提醒后宽限（小时）')
      expect(reminder.element.value).toBe('24')
      expect(grace.element.value).toBe('12')
      await reminder.setValue('6')
      await grace.setValue('3')
      await pane.get('button.pdm-primary-action').trigger('click')
      await flushPromises()
      expect(onSaveSettings).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
        checkoutOverdueHours: 6, checkoutOverdueReleaseGraceHours: 3,
        checkoutReminderHours: 4, checkoutStrongReminderHours: 8,
      }))
    } finally {
      wrapper.unmount()
    }
  })

  it('loads the conversion server list when its settings tab opens', async () => {
    const listServers = vi.spyOn(api, 'listPreviewServers').mockResolvedValue([
      { name: '一号转图机', agentUrl: 'http://one:5199', hasToken: true, timeoutMinutes: 25, disabled: false },
      { name: '二号转图机', agentUrl: 'http://two:5199', hasToken: false, timeoutMinutes: 45, disabled: true },
    ])
    const readStatuses = vi.spyOn(api, 'getPreviewServerStatuses').mockResolvedValue([
      { agentUrl: 'http://one:5199', connected: true, workerReady: true, checkedAt: '2026-11-21T03:00:00Z', message: '连接正常' },
      { agentUrl: 'http://two:5199', connected: false, workerReady: false, checkedAt: '2026-11-21T03:00:00Z', message: '无法连接' },
    ])
    const wrapper = mount(StorageSettings, { props: {
      settings, token: 'test-token', canManageStorage: true,
      equipmentTypes: [], numberingOptions: { organizations: [], projectTypes: [], equipmentTypes: [] }, pending: false,
      onSaveSettings: vi.fn(), onSaveEquipmentType: vi.fn(), onUpdateCounters: vi.fn(),
    }, global: { plugins: [ElementPlus] } })
    try {
      expect(listServers).not.toHaveBeenCalled()
      expect(readStatuses).not.toHaveBeenCalled()
      const tab = wrapper.findAll('[role="tab"]').find(item => item.text() === '图纸转换')!
      await tab.trigger('click')
      await flushPromises()

      expect(listServers).toHaveBeenCalledExactlyOnceWith('test-token')
      expect(readStatuses).toHaveBeenCalledExactlyOnceWith('test-token', expect.any(AbortSignal))
      const rows = wrapper.get('[aria-label="转图服务器列表"]').findAll('tbody tr')
      expect(rows.map(row => row.findAll('td').slice(0, 6).map(cell => cell.text()))).toEqual([
        ['一号转图机', 'http://one:5199', '已设置', '25', '已启用', '在线'],
        ['二号转图机', 'http://two:5199', '未设置', '45', '已停用', '离线'],
      ])
      const signal = readStatuses.mock.calls[0]![1]!
      await wrapper.findAll('[role="tab"]').find(item => item.text() === '存档位置')!.trigger('click')
      expect(signal.aborted).toBe(true)
    } finally {
      wrapper.unmount()
      listServers.mockRestore()
      readStatuses.mockRestore()
    }
  })
  it('shows the project storage directories without instructional copy', () => {
    const wrapper = mount(StorageSettings, {
      props: {
        settings, equipmentTypes: [], numberingOptions: { organizations: [], projectTypes: [], equipmentTypes: [] }, pending: false,
        onSaveSettings: vi.fn(), onSaveEquipmentType: vi.fn(), onUpdateCounters: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })

    const pane = wrapper.get('.pdm-storage-location-pane')
    expect(pane.text()).toContain('项目文件夹规则')
    const directories = pane.get('table[aria-label="项目存档目录"]')
    expect(directories.findAll('tbody th').map(cell => cell.text()))
      .toEqual(['图档存档根目录', '生产发包根目录', '料品资料存档根目录'])
    expect(directories.findAll<HTMLInputElement>('input').map(input => input.element.value))
      .toEqual([settings.vaultRoot, settings.releaseRoot, settings.materialAttachmentRoot])
    expect(pane.findAll('small, input[placeholder]')).toHaveLength(0)
    expect(pane.text()).not.toContain('主项目号作为容器')
    wrapper.unmount()
  })

  it('saves an independently configurable material attachment root', async () => {
    const onSaveSettings = vi.fn().mockImplementation(async input => input)
    const wrapper = mount(StorageSettings, {
      props: {
        settings, equipmentTypes: [], numberingOptions: { organizations: [], projectTypes: [], equipmentTypes: [] }, pending: false,
        onSaveSettings, onSaveEquipmentType: vi.fn(), onUpdateCounters: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })

    const attachmentRootLabel = wrapper.get('table[aria-label="项目存档目录"]').findAll('label').find(label => label.text() === '料品资料存档根目录')!
    const input = wrapper.get<HTMLInputElement>(`input[id="${attachmentRootLabel.attributes('for')}"]`)
    expect(input.element.value).toBe('D:\\PDM\\MaterialAttachments')
    await input.setValue('  E:\\PLM\\MaterialArchive  ')
    await wrapper.findAll('button').find(button => button.text() === '保存存储设置')!.trigger('click')
    await flushPromises()

    expect(onSaveSettings).toHaveBeenCalledWith(expect.objectContaining({ materialAttachmentRoot: 'E:\\PLM\\MaterialArchive' }))
    wrapper.unmount()
  })

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
    expect(wrapper.text()).toContain('PLM受控版本')

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

  it('places folder templates after storage and hides the retired drawing QR settings', async () => {
    const wrapper = mount(StorageSettings, {
      props: {
        settings, equipmentTypes: [], numberingOptions: { organizations: [], projectTypes: [], equipmentTypes: [] }, pending: false,
        canManageFolders: true, onSaveSettings: vi.fn(), onSaveEquipmentType: vi.fn(), onUpdateCounters: vi.fn(),
      },
      slots: { folders: '<div>文件夹模板配置</div>' },
      global: { plugins: [ElementPlus] },
    })

    const tabs = wrapper.findAll('.el-tabs__item')
    expect(tabs.map(tab => tab.text()).slice(0, 4)).toEqual(['项目编号', '存档位置', '文件夹模板', '图纸转换'])
    expect(tabs.map(tab => tab.text())).not.toContain('工程图二维码')
    await tabs[2]!.trigger('click')
    expect(wrapper.text()).toContain('文件夹模板配置')
    wrapper.unmount()
  })
})
