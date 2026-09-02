import { flushPromises, mount } from '@vue/test-utils'
import { ElMessage, ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import BomManager from '../src/components/BomManager.vue'
import type { BomItem, ReleasePackageSummary } from '../src/types'

const materialApi = vi.hoisted(() => ({
  listMaterials: vi.fn(),
  linkBomMaterial: vi.fn(),
  resolveBomMaterialCodes: vi.fn(),
  applyForBomMaterialCodes: vi.fn(),
  previewBomSourceReclassification: vi.fn(),
  reclassifyBomItemsFromSource: vi.fn(),
}))

vi.mock('../src/api', () => materialApi)

describe('BomManager', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    window.localStorage.clear()
    materialApi.listMaterials.mockReset()
    materialApi.linkBomMaterial.mockReset()
    materialApi.resolveBomMaterialCodes.mockReset().mockResolvedValue([])
    materialApi.applyForBomMaterialCodes.mockReset().mockResolvedValue([])
    materialApi.previewBomSourceReclassification.mockReset()
    materialApi.reclassifyBomItemsFromSource.mockReset().mockResolvedValue([])
  })

  it('places the orange source-data view before categorized BOM tabs and treats empty BOMs automatically', async () => {
    const wrapper = mount(BomManager, {
      props: { standard: [], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    const toolbar = wrapper.get('.pdm-bom-detail-toolbar')
    expect(wrapper.get('[aria-label="BOM维护"]').classes()).toContain('pdm-bom-manager-panel')
    expect(wrapper.find('h2').exists()).toBe(false)
    expect(toolbar.find('.pdm-segmented').exists()).toBe(true)
    expect(toolbar.get('.pdm-source-data-tab').text()).toBe('源数据（0）')
    expect(toolbar.get('.pdm-source-data-tab').attributes('aria-selected')).toBe('true')
    expect(toolbar.get('.pdm-source-data-tab').classes()).toContain('pdm-source-data-tab')
    expect(toolbar.find('.pdm-bom-empty-declaration').exists()).toBe(false)
    expect(toolbar.get('button[aria-label="重新对账"]').classes()).not.toContain('is-reconcile-needed')
    expect(toolbar.findAll('.pdm-manager-actions button').map(button => button.text())).toEqual(['重新对账'])
    expect(wrapper.findAll('button').some(button => button.text() === '新增物料')).toBe(false)
    expect(wrapper.find('.pdm-bom-selection-summary').exists()).toBe(false)
    expect(wrapper.get('.pdm-bom-selection-actions').classes()).toContain('pdm-bom-selection-actions')
    const sourceActions = wrapper.findAll('.pdm-bom-selection-actions button').map(button => button.text())
    expect(sourceActions[0]).toContain('标准件BOM')
    expect(sourceActions[1]).toContain('非标件BOM')
    expect(sourceActions.slice(2)).toEqual(['设为虚拟件', '回收站（0）'])

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.find('.pdm-bom-empty-declaration').exists()).toBe(false)
    expect(wrapper.get('.pdm-empty-info').text()).toContain('系统自动按无此类物料处理')
  })

  it('aggregates duplicate source instances into one material row and keeps categorized BOM counts', () => {
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [
          { id: 'source-1', sequence: 1, drawingNumber: 'STD-001', name: '重复零件', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1', sourceConfiguration: 'Default', sourceInstancePath: 'root/1' },
          { id: 'source-2', sequence: 2, drawingNumber: 'STD-001', name: '重复零件', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1', sourceConfiguration: 'Alternate', sourceInstancePath: 'root/2' },
          { id: 'source-3', sequence: 3, drawingNumber: 'STD-002', name: '另一零件', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1', sourceConfiguration: 'Default', sourceInstancePath: 'root/3' },
        ],
        standard: [
          { id: 'standard-1', sequence: 1, drawingNumber: 'STD-001', name: '标准件一', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'standard-2', sequence: 2, drawingNumber: 'STD-002', name: '标准件二', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        nonStandard: [{ id: 'non-standard-1', sequence: 1, drawingNumber: 'NST-001', name: '非标件', quantity: 1, unit: '个', revision: 'W1', complete: true }],
        electrical: [{ id: 'electrical-1', sequence: 1, drawingNumber: 'ELE-001', name: '电气件', quantity: 1, unit: '个', revision: 'W1', complete: true }],
        declarations: [], pending: false,
      },
    })

    const tabs = wrapper.findAll('button[role="tab"]')
    expect(tabs.map(tab => tab.text())).toEqual(['源数据（2）', '标准件BOM（2）', '非标件BOM（1）', '电气BOM（1）'])
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('.pdm-bom-quantity-audit')[0].text()).toBe('2 / 2')
  })

  it.each([
    ['standard', 1], ['nonStandard', 2], ['electrical', 3],
  ] as const)('compares %s summary quantities with source totals while preserving per-instance differences', async (category, tabIndex) => {
    const sourceData: BomItem[] = [
      { id: 'source-1', sequence: 1, drawingNumber: 'PART-001', name: '重复零件', quantity: 4, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'doc-1', sourceInstancePath: 'root/1' },
      { id: 'source-2', sequence: 2, drawingNumber: 'PART-001', name: '重复零件', quantity: 6, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'doc-1', sourceInstancePath: 'root/2' },
      { id: 'other-unit', sequence: 3, drawingNumber: 'PART-001', name: '不同单位', quantity: 20, unit: '米', revision: 'W1', complete: true },
      { id: 'other-part', sequence: 4, drawingNumber: 'PART-002', name: '其他零件', quantity: 30, unit: '个', revision: 'W1', complete: true },
    ]
    const categorized = sourceData.slice(0, 2).map(item => ({ ...item, drawingNumber: 'OFFICIAL-001' }))
    const wrapper = mount(BomManager, {
      props: { sourceData, standard: [], nonStandard: [], electrical: [], [category]: categorized, declarations: [], pending: false },
    })

    await wrapper.findAll('button[role="tab"]')[tabIndex].trigger('click')
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('10 / 10')
    expect(wrapper.get('.pdm-bom-quantity-audit').classes()).toContain('is-matched')

    await wrapper.setProps({ [category]: [{ ...categorized[0]! }, { ...categorized[1]!, quantity: 5 }] })
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('9 / 10')
    expect(wrapper.get('.pdm-bom-quantity-audit').classes()).toContain('is-blocking')

    await wrapper.get('[aria-label="BOM显示方式"] button:nth-of-type(2)').trigger('click')
    const cells = wrapper.findAll('.pdm-bom-quantity-audit')
    expect(cells.map(cell => cell.text())).toEqual(['4 / 4', '5 / 6'])
    expect(cells[0]!.classes()).toContain('is-matched')
    expect(cells[1]!.classes()).toContain('is-blocking')
    expect(wrapper.emitted('save')).toBeUndefined()
    expect(sourceData.map(item => item.quantity)).toEqual([4, 6, 20, 30])
  })

  it.each([
    ['standard', 1], ['nonStandard', 2], ['electrical', 3],
  ] as const)('shows the quantity comparison for editable %s summaries without editing their total', async (category, tabIndex) => {
    const sourceData: BomItem[] = Array.from({ length: 10 }, (_, index) => ({
      id: `source-${index}`, sequence: index + 1, drawingNumber: 'PART-001', name: '重复零件', quantity: 1,
      unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'doc-1', sourceInstancePath: `root/${index}`,
    }))
    const wrapper = mount(BomManager, {
      props: { sourceData, standard: [], nonStandard: [], electrical: [], [category]: sourceData, declarations: [], pending: false, editable: true },
    })

    await wrapper.findAll('button[role="tab"]')[tabIndex].trigger('click')
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('10 / 10')
    expect(wrapper.get('.pdm-bom-quantity-audit').classes()).toContain('is-matched')
    expect(wrapper.find('input[aria-label="数量"]').exists()).toBe(false)
    expect(wrapper.get('.pdm-bom-quantity-audit span').attributes('title')).toContain('请切换到结构模式修改')

    await wrapper.get('[aria-label="BOM显示方式"] button:nth-of-type(2)').trigger('click')
    expect(wrapper.findAll('button[aria-label="编辑数量"]')).toHaveLength(10)
    expect(wrapper.findAll('.pdm-bom-quantity-audit').every(cell => cell.text() === '1 / 1')).toBe(true)
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('edits a summarized material without confirmation and preserves each instance quantity when saving', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm')
    const items: BomItem[] = [
      { id: 'instance-1', sequence: 1, drawingNumber: 'PART-001', name: '原名称', quantity: 4, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'doc-1', sourceInstancePath: 'root/1' },
      { id: 'instance-2', sequence: 2, drawingNumber: 'PART-001', name: '原名称', quantity: 6, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'doc-1', sourceInstancePath: 'root/2' },
    ]
    const wrapper = mount(BomManager, {
      props: { sourceData: items, standard: items, nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('10 / 10')
    await wrapper.get('button[aria-label="编辑物料名称"]').trigger('click')
    const editor = wrapper.get('input[aria-label="内联编辑物料名称"]')
    await editor.setValue('统一名称')
    await editor.trigger('keydown', { key: 'Enter' })

    expect(confirm).not.toHaveBeenCalled()
    await wrapper.get('.pdm-bom-save-action').trigger('click')
    const savedItems = wrapper.emitted('save')?.[0]?.[1] as BomItem[]
    expect(savedItems.map(item => item.name)).toEqual(['统一名称', '统一名称'])
    expect(savedItems.map(item => item.quantity)).toEqual([4, 6])
  })

  it('offers a safe reconciliation for legacy multiplied quantities and clears it after repair', async () => {
    const sourceData: BomItem[] = [
      { id: 'instance-1', sequence: 1, drawingNumber: 'PART-001', name: '零件', quantity: 4, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'doc-1', sourceInstancePath: 'root/1' },
      { id: 'instance-2', sequence: 2, drawingNumber: 'PART-001', name: '零件', quantity: 6, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'doc-1', sourceInstancePath: 'root/2' },
    ]
    const corrupted = [
      { ...sourceData[0]!, quantity: 16 },
      { ...sourceData[1]!, quantity: 36 },
    ]
    const wrapper = mount(BomManager, {
      props: { sourceData, standard: corrupted, nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    expect(wrapper.get('button[aria-label*="按最新设计树重新计算"]').text()).toBe('修正数量（1）')
    await wrapper.get('button[aria-label*="按最新设计树重新计算"]').trigger('click')
    expect(wrapper.emitted('generate')).toHaveLength(1)

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('52 / 10')
    await wrapper.setProps({ standard: sourceData })
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('10 / 10')
    expect(wrapper.find('button[aria-label*="按最新设计树重新计算"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some(button => button.text() === '重新对账')).toBe(true)
  })

  it('safely aggregates uncoded rows only when they come from the same 3D document and configuration', async () => {
    const rows: BomItem[] = [
      { id: 'same-1', kind: 'Standard', sequence: 1, drawingNumber: '', name: '同一零件', specification: 'M1', brand: 'FESTO', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'doc-same', sourceConfiguration: 'Default' },
      { id: 'same-2', kind: 'Standard', sequence: 2, drawingNumber: '', name: '同一零件', specification: 'M1', brand: 'FESTO', quantity: 2, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'doc-same', sourceConfiguration: 'Default' },
      { id: 'other', kind: 'Standard', sequence: 3, drawingNumber: '', name: '同一零件', specification: 'M1', brand: 'FESTO', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'doc-other', sourceConfiguration: 'Default' },
    ]
    const wrapper = mount(BomManager, {
      props: { sourceData: rows, standard: rows, nonStandard: [], electrical: [], declarations: [], pending: false },
    })

    expect(wrapper.get('.pdm-source-data-tab').text()).toBe('源数据（2）')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('.pdm-bom-quantity-audit').map(cell => cell.text())).toContain('3 / 3')
    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.findAll('.pdm-bom-quantity-audit').map(cell => cell.text())).toEqual(['3 / 3', '1 / 1'])
    expect(wrapper.findAll('.pdm-bom-quantity-audit').every(cell => cell.classes().includes('is-matched'))).toBe(true)
  })

  it('classifies every source instance represented by an aggregated material row', async () => {
    const duplicateSource = [
      { id: 'source-1', sequence: 1, drawingNumber: 'STD-001', name: '重复零件', quantity: 1, unit: '个', revision: 'W1', complete: false, source: 'Auto' as const, pendingClassification: true },
      { id: 'source-2', sequence: 2, drawingNumber: 'STD-001', name: '重复零件', quantity: 2, unit: '个', revision: 'W1', complete: false, source: 'Auto' as const, pendingClassification: true },
    ]
    const wrapper = mount(BomManager, {
      props: { sourceData: duplicateSource, standard: [], nonStandard: [], unclassified: duplicateSource, electrical: [], declarations: [], pending: false, editable: true },
    })

    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(1)
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('3 / 3')
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text().includes('标准件BOM'))!.trigger('click')
    await flushPromises()

    expect(wrapper.emitted('batchUpdate')).toEqual([[{
      itemIds: ['source-1', 'source-2'], fields: ['kind'], targetKind: 'Standard',
    }]])
  })

  it('previews and confirms source-backed reclassification before writing', async () => {
    const sourceItem = {
      id: 'source-classified', kind: 'Standard' as const, sequence: 1, drawingNumber: 'CAD-001', name: '旧名称',
      quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Auto' as const,
      sourceDocumentId: 'document-1', sourceInstancePath: 'root/1',
    }
    materialApi.previewBomSourceReclassification.mockResolvedValue({
      targetKind: 'NonStandard', itemCount: 1, changedItemCount: 1,
      items: [{
        itemId: sourceItem.id, currentKind: 'Standard', targetKind: 'NonStandard',
        currentDrawingNumber: 'OFFICIAL-001', sourceDrawingNumber: 'CAD-001', resultDrawingNumber: 'OFFICIAL-001',
        currentName: '旧名称', sourceName: '源名称', changedFields: ['物料分类', '物料名称'], officialMaterialCodeProtected: true,
      }],
    })
    const wrapper = mount(BomManager, {
      props: {
        projectId: 'project-1', token: 'token', sourceData: [sourceItem], standard: [sourceItem],
        nonStandard: [], unclassified: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '归入非标件BOM')!.trigger('click')
    await flushPromises()

    expect(materialApi.previewBomSourceReclassification).toHaveBeenCalledWith('project-1', ['source-classified'], 'NonStandard', 'token')
    expect(wrapper.get('[aria-labelledby="pdm-bom-reclassify-title"]').text()).toContain('正式料号已保护')
    await wrapper.get('[aria-labelledby="pdm-bom-reclassify-title"] .pdm-primary-action').trigger('click')
    await flushPromises()
    expect(materialApi.reclassifyBomItemsFromSource).toHaveBeenCalledWith('project-1', ['source-classified'], 'NonStandard', 'token')
    expect(wrapper.emitted('materialCodeChanged')).toHaveLength(1)
  })

  it('switches between aggregated and assembly-structure views, expands on demand and keeps the mode across tabs', async () => {
    const structure: BomItem[] = [
      { id: 'assembly-a', kind: 'Standard', sequence: 1, drawingNumber: 'ASM-A', name: '子装配A', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceInstancePath: 'ROOT/ASM-A-1' },
      { id: 'part-a', kind: 'Standard', sequence: 2, drawingNumber: 'PART-A', name: '零件A', quantity: 2, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceInstancePath: 'ROOT/ASM-A-1/PART-A-1', parentDrawingNumber: 'ASM-A' },
      { id: 'assembly-b', kind: 'Standard', sequence: 3, drawingNumber: 'ASM-A', name: '子装配A', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceInstancePath: 'ROOT/ASM-A-2' },
    ]
    const wrapper = mount(BomManager, {
      props: { projectId: 'project-structure', sourceData: structure, standard: structure, nonStandard: structure, electrical: structure, declarations: [], pending: false },
    })

    expect(wrapper.get('[aria-label="BOM显示方式"] button.is-active').text()).toBe('按汇总')
    expect(wrapper.get('[aria-label="BOM显示方式"] small').text()).toBe('实例 3 · 汇总 2')
    expect(wrapper.get('[aria-label="BOM显示方式"]').element.firstElementChild?.tagName).toBe('SMALL')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(2)

    await wrapper.get('[aria-label="BOM显示方式"] button:nth-of-type(2)').trigger('click')
    expect(wrapper.get('[aria-label="BOM显示方式"] button.is-active').text()).toBe('按结构')
    expect(wrapper.get('[aria-label="BOM显示方式"] small').text()).toBe('实例 3 · 汇总 2')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(2)
    expect((wrapper.get('select[aria-label="BOM明细每页条数"]').element as HTMLSelectElement).value).toBe('50')
    expect(wrapper.get('.pdm-bom-pagination').text()).toContain('结构实例 3 条 · 当前层级 2 条')

    await wrapper.get('button[aria-label="展开 ASM-A"]').trigger('click')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(3)
    expect(wrapper.get('button[aria-label="折叠 ASM-A"]').attributes('aria-expanded')).toBe('true')

    for (const tabIndex of [1, 2, 3]) {
      await wrapper.findAll('button[role="tab"]')[tabIndex].trigger('click')
      expect(wrapper.get('[aria-label="BOM显示方式"] button.is-active').text()).toBe('按结构')
      expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(2)
    }
    expect(window.localStorage.getItem('pdm:bom-display:project-structure')).toBe('Structure')
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('keeps release actions out of source data and shows per-BOM release state', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-standard-1', number: 'RP-S-001', state: '审批中', steps: [], scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-08-21T08:00:00Z',
    }
    const wrapper = mount(BomManager, {
      props: {
        standard: [], nonStandard: [], electrical: [], declarations: [], releasePackages: [releasePackage], pending: false, editable: true, canManageRelease: true,
        versions: [{ id: 'standard-draft', projectId: 'project', kind: 'Standard', versionNumber: 1, label: 'S-B01', state: 'Draft', items: [], createdBy: 'admin', createdAt: '2026-08-21', updatedBy: 'admin', updatedAt: '2026-08-21' }],
      },
      global: { stubs: { ElDrawer: { template: '<div><slot /></div>' } } },
    })

    expect(wrapper.find('.pdm-bom-release-strip').exists()).toBe(false)
    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    const releaseStrip = wrapper.get('.pdm-bom-release-strip')
    expect(releaseStrip.text()).toContain('在途发布包1 个')
    expect(releaseStrip.text()).toContain('处理审批 1')
    expect(releaseStrip.text()).toContain('发起发布')
    expect(releaseStrip.text()).toContain('当前工作版S-V01')
    expect(releaseStrip.text()).not.toContain('S-B01')
    expect(wrapper.get('.pdm-bom-save-action').text()).toBe('保存BOM')
    expect(releaseStrip.findAll('.pdm-bom-release-strip-actions')).toHaveLength(1)
    expect(releaseStrip.find('.pdm-bom-release-maintenance-actions').exists()).toBe(false)
    expect(releaseStrip.findAll('.pdm-bom-release-strip-actions button').map(button => button.text())).toEqual(['导入XLSX', '导出XLSX', '重新对账', '处理审批 1', '保存BOM', '发起发布'])
    expect(releaseStrip.findAll('button').some(button => button.text() === '新增物料')).toBe(false)

    await wrapper.setProps({ releasePackages: [] })
    expect(wrapper.findAll('.pdm-bom-release-strip-actions button').map(button => button.text())).toEqual(['导入XLSX', '导出XLSX', '重新对账', '发布记录', '保存BOM', '发起发布'])
  })

  it('defaults to formal after long-lead publication and keeps repeated long-lead available after formal publication', async () => {
    const longLead: ReleasePackageSummary = {
      id: 'release-long-lead', number: 'RP-LL-001', state: '已发布', steps: [], scope: 'StandardLongLead',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-08-20T08:00:00Z',
    }
    const formal: ReleasePackageSummary = {
      ...longLead, id: 'release-formal', number: 'RP-S-001', scope: 'StandardFormal', createdAt: '2026-08-21T08:00:00Z',
    }
    const wrapper = mount(BomManager, {
      props: {
        standard: [], nonStandard: [], electrical: [], declarations: [], releasePackages: [longLead], pending: false, editable: true, canManageRelease: true,
      },
      global: { stubs: { ElDrawer: { template: '<div><slot /></div>' } } },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect((wrapper.get('.pdm-release-type-row select').element as HTMLSelectElement).value).toBe('StandardFormal')

    await wrapper.setProps({ releasePackages: [longLead, formal] })
    expect(wrapper.findAll('.pdm-release-type-row option').map(option => option.text())).toEqual(['标准件 · 长交期提前发布', '标准件 · 增补/变更'])
    expect((wrapper.get('.pdm-release-type-row select').element as HTMLSelectElement).value).toBe('StandardSupplement')
  })

  it('shows the complete material property columns in the required order', async () => {
    const wrapper = mount(BomManager, {
      props: { standard: [], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')

    expect(wrapper.findAll('thead th').map(header => header.text())).toEqual([
      '', '', '序号', '物料分类', '单位', '物料编码', '物料名称', '上级物料编码', '型号', '备注信息', '品牌', '材质', '表面处理', '重量', '数量(BOM/源)', '图纸核对', '版本', '问题', '资料状态',
    ])
  })

  it('keeps one quick-entry row at the bottom and excludes an untouched row from save', async () => {
    const wrapper = mount(BomManager, {
      props: { projectId: 'project-1', standard: [], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(1)
    const quickEntry = wrapper.get('.pdm-bom-table tbody tr')
    expect(quickEntry.classes()).toContain('is-quick-entry')
    expect(quickEntry.text()).toContain('标准件')
    expect(quickEntry.text()).toContain('个')
    expect(quickEntry.findAll('td')[16].text()).toBe('W1')
    expect(quickEntry.text()).toContain('待录入')
    expect(quickEntry.text()).not.toContain('缺3D图')
    expect(quickEntry.text()).not.toContain('—')
    expect(quickEntry.find('input[aria-label="选择物料"]').exists()).toBe(false)
    expect(quickEntry.find('input[aria-label="数量"]').exists()).toBe(false)
    expect(quickEntry.get('input[aria-label="上级物料编码"]').attributes('placeholder')).toBe('')

    await wrapper.get('.pdm-bom-save-action').trigger('click')
    expect(wrapper.emitted('save')?.[0]?.[1]).toEqual([])

    await wrapper.get('input[aria-label="物料名称"]').setValue('快捷新增物料')
    await flushPromises()
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('.pdm-bom-table tbody tr').at(-1)!.classes()).toContain('is-quick-entry')

    await wrapper.get('.pdm-bom-save-action').trigger('click')
    const saved = wrapper.emitted('save')?.[1]?.[1] as BomItem[]
    expect(saved).toHaveLength(1)
    expect(saved[0].name).toBe('快捷新增物料')
  })

  it('counts pending materials with the same distinct-material rule as source data', () => {
    const duplicatePending = [
      { id: 'pending-1', kind: 'Unclassified' as const, sequence: 1, drawingNumber: 'P-001', name: '待分类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, pendingClassification: true },
      { id: 'pending-2', kind: 'Unclassified' as const, sequence: 2, drawingNumber: 'P-001', name: '待分类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, pendingClassification: true },
    ]
    const wrapper = mount(BomManager, {
      props: { sourceData: duplicatePending, standard: [], nonStandard: [], unclassified: duplicatePending, electrical: [], declarations: [], pending: false },
    })

    expect(wrapper.get('.pdm-source-data-tab').text()).toBe('源数据（1）')
    expect(wrapper.get('.pdm-bom-unresolved-count').text()).toBe('待处理 1')
  })

  it('warns beside the model when it differs from the linked design-tree drawing name', () => {
    const row: BomItem = {
      id: 'bom-drawing-name', kind: 'Standard', sequence: 1, drawingNumber: 'P-002', name: '物料名称', specification: 'MODEL-002', quantity: 1, unit: '件', revision: 'W1', complete: true,
      source: 'Auto', sourceDocumentId: 'document-2', sourceInstancePath: 'R70000050.02-1/CQ-WS-ISO63-PT2-20250520113101861-1', manuallyOverridden: true, reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与图档源数据不一致：物料分类。',
    }
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [row], standard: [row], nonStandard: [], electrical: [], declarations: [], pending: false,
        documents: [{ id: 'document-2', projectId: 'project-1', drawingNumber: 'P-002', name: 'R70000050.02-1/CQ-WS-ISO63-PT2-20250520113101861-1', fileName: 'CQ-WS-ISO63-PT2-20250520113101861.SLDPRT', kind: 'Part', state: 'Working', revision: 'W1' }],
      },
    })

    expect(wrapper.find('.pdm-bom-drawing-name-cell').exists()).toBe(false)
    expect(wrapper.findAll('thead th').map(header => header.text())).not.toContain('图纸名称')
    const warning = wrapper.get('.pdm-bom-drawing-name-warning')
    expect(warning.attributes('aria-label')).toBe('图纸名称与BOM型号不一致')
    expect(warning.attributes('title')).toBe('设计树图纸名称：CQ-WS-ISO63-PT2-20250520113101861；BOM型号：MODEL-002')
    expect(wrapper.get('.pdm-bom-reconciliation-cell').text()).toBe('—')
    expect(wrapper.find('.pdm-bom-reconciliation').exists()).toBe(false)
  })

  it('hides benign auto-added reconciliation messages but keeps exception details in the cell', () => {
    const autoAdded: BomItem = {
      id: 'bom-auto-added', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '正常物料', quantity: 1, unit: '件', revision: 'W1', complete: true,
      source: 'Auto', sourceDocumentId: 'document-1', reconciliationStatus: 'AutoAdded', reconciliationNote: '图档源数据新增，已根据物料分类自动进入标准件BOM。',
    }
    const mismatch: BomItem = {
      ...autoAdded, id: 'bom-mismatch', sequence: 2, drawingNumber: 'S-002', reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与最新图档源数据不一致：物料编码、型号、品牌。',
    }
    const writebackFailed: BomItem = {
      ...autoAdded, id: 'bom-writeback-failed', sequence: 3, drawingNumber: 'S-003', reconciliationStatus: 'SourceMatched', reconciliationNote: '属性写回SolidWorks失败，请检查本地文件。', propertyWritebackStatus: 'Failed',
    }
    const legacyNameMismatch: BomItem = {
      ...autoAdded, id: 'bom-legacy-name', sequence: 4, drawingNumber: 'S-004', reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与最新图档源数据不一致：物料名称。',
    }
    const legacyCodeAndNameMismatch: BomItem = {
      ...autoAdded, id: 'bom-legacy-code-name', sequence: 5, drawingNumber: 'S-005', reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与最新图档源数据不一致：物料编码、物料名称。',
    }
    const wrapper = mount(BomManager, {
      props: { sourceData: [autoAdded, mismatch, writebackFailed, legacyNameMismatch, legacyCodeAndNameMismatch], standard: [autoAdded, mismatch, writebackFailed, legacyNameMismatch, legacyCodeAndNameMismatch], nonStandard: [], electrical: [], declarations: [], pending: false },
    })

    const rows = wrapper.findAll('.pdm-bom-table tbody tr')
    expect(rows[0].get('.pdm-bom-reconciliation-cell').text()).toBe('—')
    expect(rows[0].classes()).not.toContain('is-reconciliation-issue')
    expect(rows[1].get('.pdm-bom-reconciliation-cell').text()).toBe('物料编码等3项不一致')
    expect(rows[1].get('.pdm-bom-reconciliation').attributes('title')).toBe('BOM维护值与最新图档源数据不一致：物料编码、型号、品牌。')
    expect(rows[1].classes()).toContain('is-reconciliation-issue')
    expect(rows[2].get('.pdm-bom-reconciliation-cell').text()).toBe('写回失败')
    expect(rows[2].get('.pdm-bom-source').classes()).toContain('is-critical')
    expect(rows[2].get('.pdm-bom-reconciliation').attributes('title')).toBe('属性写回SolidWorks失败，请检查本地文件。')
    expect(rows[3].get('.pdm-bom-reconciliation-cell').text()).toBe('—')
    expect(rows[3].classes()).not.toContain('is-reconciliation-issue')
    expect(rows[4].get('.pdm-bom-reconciliation-cell').text()).toBe('物料编码不一致')
    expect(rows[4].get('.pdm-bom-reconciliation').attributes('title')).toBe('BOM维护值与最新图档源数据不一致：物料编码。')
  })

  it('paginates source and categorized BOM rows at 50 by default with 30, 100 and 200 choices', async () => {
    const items: BomItem[] = Array.from({ length: 55 }, (_, index) => ({
      id: `bom-page-${index + 1}`, kind: 'Standard', sequence: index + 1, drawingNumber: `P-${String(index + 1).padStart(3, '0')}`,
      name: `零件${index + 1}`, quantity: 1, unit: '件', revision: 'W1', complete: true,
    }))
    const wrapper = mount(BomManager, {
      props: { sourceData: items, standard: items, nonStandard: [], electrical: [], declarations: [], pending: false },
    })

    const pageSize = wrapper.get('select[aria-label="BOM明细每页条数"]')
    expect((pageSize.element as HTMLSelectElement).value).toBe('50')
    expect(pageSize.findAll('option').map(option => option.text())).toEqual(['30条/页', '50条/页', '100条/页', '200条/页'])
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(50)

    await wrapper.get('button[aria-label="BOM明细下一页"]').trigger('click')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(5)
    await pageSize.setValue('100')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(55)

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await pageSize.setValue('50')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(50)

    await wrapper.get('[aria-label="BOM显示方式"] button:nth-of-type(2)').trigger('click')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(50)
    expect((wrapper.get('select[aria-label="BOM明细每页条数"]').element as HTMLSelectElement).value).toBe('50')
    await wrapper.get('button[aria-label="BOM明细下一页"]').trigger('click')
    expect(wrapper.findAll('.pdm-bom-table tbody tr')).toHaveLength(5)
  })

  it('selects and clears only the current source-data page from the header checkbox', async () => {
    const items: BomItem[] = Array.from({ length: 55 }, (_, index) => ({
      id: `bom-select-page-${index + 1}`, kind: 'Standard', sequence: index + 1, drawingNumber: `S-${String(index + 1).padStart(3, '0')}`,
      name: `分页物料${index + 1}`, quantity: 1, unit: '件', revision: 'W1', complete: true,
    }))
    const wrapper = mount(BomManager, {
      props: { sourceData: items, standard: items, nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })
    const selectPage = wrapper.get('input[aria-label="选择全部源数据物料"]')

    await selectPage.setValue(true)
    expect(wrapper.get('.pdm-bom-selection-summary').text()).toBe('已选择 50 项')
    expect(wrapper.findAll('input[aria-label="选择物料"]').filter(input => (input.element as HTMLInputElement).checked)).toHaveLength(50)

    await wrapper.get('button[aria-label="BOM明细下一页"]').trigger('click')
    expect((wrapper.get('input[aria-label="选择全部源数据物料"]').element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.findAll('input[aria-label="选择物料"]').filter(input => (input.element as HTMLInputElement).checked)).toHaveLength(0)

    await wrapper.get('input[aria-label="选择全部源数据物料"]').setValue(true)
    expect(wrapper.get('.pdm-bom-selection-summary').text()).toBe('已选择 55 项')
    expect(wrapper.findAll('input[aria-label="选择物料"]').filter(input => (input.element as HTMLInputElement).checked)).toHaveLength(5)

    await wrapper.get('input[aria-label="选择全部源数据物料"]').setValue(false)
    expect(wrapper.get('.pdm-bom-selection-summary').text()).toBe('已选择 50 项')
    await wrapper.get('button[aria-label="BOM明细上一页"]').trigger('click')
    expect((wrapper.get('input[aria-label="选择全部源数据物料"]').element as HTMLInputElement).checked).toBe(true)
    expect(wrapper.findAll('input[aria-label="选择物料"]').filter(input => (input.element as HTMLInputElement).checked)).toHaveLength(50)
  })

  it('keeps the full material name as a tooltip while the column is visually truncated', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-name', sequence: 1, drawingNumber: 'STD-001', name: '这是一个超过八个字的物料名称', quantity: 1, unit: '件', revision: 'W1', complete: true }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')

    const nameValue = wrapper.get('.pdm-bom-name-value')
    expect(nameValue.attributes('title')).toBe('这是一个超过八个字的物料名称')
    expect(nameValue.text()).toBe('这是一个超过八个字的物料名称')
  })

  it('shows BOM and source quantities while retaining the same-code total in the tooltip', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [
          { id: 'bom-1', sequence: 1, drawingNumber: 'STD-001', name: '配套物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
          { id: 'bom-2', sequence: 2, drawingNumber: 'STD-001', name: '配套物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
          { id: 'bom-3', sequence: 3, drawingNumber: 'STD-001', name: '配套物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
          { id: 'bom-4', sequence: 4, drawingNumber: 'STD-002', name: '唯一物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
        ],
        sourceData: [
          { id: 'bom-1', sequence: 1, drawingNumber: 'STD-001', name: '配套物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
          { id: 'bom-2', sequence: 2, drawingNumber: 'STD-001', name: '配套物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
          { id: 'bom-3', sequence: 3, drawingNumber: 'STD-001', name: '配套物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
          { id: 'bom-4', sequence: 4, drawingNumber: 'STD-002', name: '唯一物料', quantity: 1, unit: '001', revision: 'W1', complete: true },
        ],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.get('[aria-label="BOM显示方式"] button:nth-of-type(2)').trigger('click')
    await wrapper.findAll('button[role="tab"]')[1].trigger('click')

    const quantityButtons = wrapper.findAll('button[aria-label="编辑数量"]')
    expect(quantityButtons.map(button => button.text())).toEqual(['1 / 1', '1 / 1', '1 / 1', '1 / 1'])
    expect(quantityButtons[0].attributes('title')).toContain('同料号合计 3')

    await quantityButtons[0].trigger('click')
    expect((wrapper.get('input[aria-label="内联编辑数量"]').element as HTMLInputElement).value).toBe('1')
  })

  it('shows strict 3D and unique 2D checks for non-standard BOM rows', async () => {
    const linked = { id: 'bom-linked', kind: 'NonStandard' as const, sequence: 1, drawingNumber: 'N-001', name: '非标零件', quantity: 2, unit: '件', revision: 'W1', complete: true, source: 'Auto' as const, sourceDocumentId: 'model-1' }
    const missingDrawing = { id: 'bom-missing-drawing', kind: 'NonStandard' as const, sequence: 2, drawingNumber: 'N-002', name: '缺工程图零件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto' as const, sourceDocumentId: 'model-2' }
    const manual = { id: 'bom-manual', kind: 'NonStandard' as const, sequence: 3, drawingNumber: 'N-003', name: '人工非标件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' as const }
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [linked, missingDrawing], standard: [], nonStandard: [linked, missingDrawing, manual], electrical: [], declarations: [], pending: false, editable: true,
        documents: [
          { id: 'model-1', projectId: 'project-1', drawingNumber: 'N-001', name: '非标零件', fileName: 'N-001.SLDPRT', kind: 'Part', state: 'Working', revision: 'W1' },
          { id: 'model-2', projectId: 'project-1', drawingNumber: 'N-002', name: '缺工程图零件', fileName: 'N-002.SLDPRT', kind: 'Part', state: 'Working', revision: 'W1' },
          { id: 'drawing-1', projectId: 'project-1', drawingNumber: 'N-001', name: '非标工程图', fileName: 'N-001.SLDDRW', kind: 'Drawing', state: 'Working', revision: 'W2' },
        ],
        documentRelations: [{ modelDocumentId: 'model-1', drawingDocumentId: 'drawing-1' }],
      },
    })

    await wrapper.findAll('button[role="tab"]')[2].trigger('click')

    expect(wrapper.get('.pdm-bom-drawing-audit-header').text()).toBe('图纸核对')
    expect(wrapper.findAll('.pdm-bom-audit strong').map(item => item.text())).toEqual(['3D+2D已对应', '缺2D图', '缺3D图'])
    expect(wrapper.findAll('.pdm-bom-audit small').map(item => item.text())).toEqual(['N-001 · W2'])
    expect(wrapper.findAll('.pdm-bom-audit')[2].attributes('title')).toBe('必须补齐3D及唯一2D工程图')
    expect(wrapper.findAll('.pdm-bom-quantity-audit').map(item => item.text())).toEqual(['2 / 2', '1 / 1', '1 / —'])
  })

  it('allows an empty reason when deleting selected categorized rows', async () => {
    const prompt = vi.spyOn(ElMessageBox, 'prompt').mockResolvedValue({ value: '', action: 'confirm' } as never)
    const wrapper = mount(BomManager, {
      props: {
        standard: [],
        nonStandard: [
          { id: 'bom-1', sequence: 1, drawingNumber: '1', name: '1111', quantity: 1, unit: '件', material: '11', specification: '1', revision: 'W1', complete: true, source: 'Manual' },
          { id: 'bom-2', sequence: 2, drawingNumber: '2', name: '1', quantity: 1, unit: '件', material: '1', specification: '1', revision: 'W1', complete: true, source: 'Manual' },
        ],
        electrical: [],
        declarations: [],
        pending: false,
        editable: true,
      },
    })

    await wrapper.get('button[role="tab"]:nth-child(3)').trigger('click')
    await wrapper.get('input[aria-label="选择当前分类全部物料"]').setValue(true)
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '批量删除')!.trigger('click')
    await flushPromises()

    expect(prompt).toHaveBeenCalledOnce()
    expect(prompt).toHaveBeenCalledWith(
      expect.stringContaining('删除后统一移入回收站'),
      '移入BOM回收站',
      expect.objectContaining({ customClass: 'pdm-bom-recycle-prompt', inputPlaceholder: '请输入删除原因（选填）' }),
    )
    expect(wrapper.emitted('batchDelete')).toEqual([[['bom-1', 'bom-2'], '']])
  })

  it('keeps drawing items in source data while classification is edited in the batch editor', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [],
        nonStandard: [],
        sourceData: [{ id: 'bom-pending', kind: 'Unclassified', sequence: 1, drawingNumber: 'P-001', name: '待分类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1', pendingClassification: true }],
        unclassified: [{ id: 'bom-pending', kind: 'Unclassified', sequence: 1, drawingNumber: 'P-001', name: '待分类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1', pendingClassification: true }],
        electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.get('.pdm-source-data-tab').text()).toBe('源数据（1）')
    expect(wrapper.text()).toContain('待处理 1')
    expect(wrapper.find('.pdm-bom-reconcile-hint').exists()).toBe(false)
    expect(wrapper.find('.pdm-source-data-hint').exists()).toBe(false)
    expect(wrapper.text()).toContain('待分类')
    expect(wrapper.findAll('button[role="tab"]')[2].text()).toBe('非标件BOM（0）')
    expect(wrapper.findAll('thead th').map(header => header.text())).not.toContain('操作')
    expect(wrapper.find('tbody .pdm-table-action').exists()).toBe(false)
    expect(wrapper.get('.pdm-bom-classification-indicator').classes()).toContain('is-unclassified')
    expect(wrapper.get('.pdm-bom-classification-indicator').attributes('aria-label')).toBe('未归类')
    expect(wrapper.findAll('tbody td')[1].find('.pdm-bom-classification-indicator').exists()).toBe(true)
    expect(wrapper.findAll('tbody td')[2].find('.pdm-bom-classification-indicator').exists()).toBe(false)
    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text().includes('标准件BOM'))!.trigger('click')

    expect(wrapper.emitted('batchUpdate')).toEqual([[{
      itemIds: ['bom-pending'], fields: ['kind'], targetKind: 'Standard',
    }]])
    expect(wrapper.get('.pdm-source-data-tab').text()).toBe('源数据（1）')
  })

  it('filters source data to pending items only', async () => {
    const classified = { id: 'bom-classified', kind: 'Standard' as const, sequence: 1, drawingNumber: 'S-001', name: '已归类零件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto' as const, sourceDocumentId: 'document-1' }
    const pending = { id: 'bom-pending', kind: 'Unclassified' as const, sequence: 2, drawingNumber: 'P-001', name: '待分类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto' as const, sourceDocumentId: 'document-2', pendingClassification: true }
    const wrapper = mount(BomManager, {
      props: {
        standard: [classified], nonStandard: [], sourceData: [classified, pending], unclassified: [pending], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    const pendingOnly = wrapper.get('input[aria-label="仅显示待处理"]')
    await pendingOnly.setValue(true)

    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('待分类零件')
    expect(wrapper.text()).not.toContain('已归类零件')
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '清空筛选')!.trigger('click')
    expect((pendingOnly.element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
  })

  it('routes quantity changes to quantity repair without reporting excluded reconciliation fields', async () => {
    const source: BomItem = {
      id: 'bom-updated', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '更新后的零件', quantity: 2, unit: '件', revision: 'W2', complete: true,
      source: 'Auto', sourceDocumentId: 'document-1', reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与图档源数据不一致：数量、版本。',
    }
    const maintained: BomItem = {
      ...source, name: '原零件', quantity: 1, revision: 'W1', reconciliationStatus: 'SourceMatched', reconciliationNote: 'BOM维护值已与图档源数据一致。',
    }
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [source], standard: [maintained], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    const reconcileButton = wrapper.get('button[aria-label*="按最新设计树重新计算"]')
    expect(wrapper.find('.pdm-bom-reconcile-hint').exists()).toBe(false)
    expect(reconcileButton.text()).toBe('修正数量（1）')
    expect(reconcileButton.classes()).toContain('is-reconcile-needed')
    await reconcileButton.trigger('click')
    expect(wrapper.emitted('generate')).toEqual([[false]])

    await wrapper.setProps({
      sourceData: [{ ...source, reconciliationStatus: 'SourceMatched', reconciliationNote: 'BOM维护值已与图档源数据一致。' }],
      standard: [{ ...source, reconciliationStatus: 'SourceMatched', reconciliationNote: 'BOM维护值已与图档源数据一致。' }],
    })
    expect(wrapper.find('.pdm-bom-reconcile-hint').exists()).toBe(false)
    expect(wrapper.get('button[aria-label="重新对账"]').classes()).not.toContain('is-reconcile-needed')
  })

  it('warns about unsaved edits in reconciliation and discards them only after refreshed data arrives', async () => {
    const original = { id: 'bom-1', kind: 'Standard' as const, sequence: 1, drawingNumber: 'S-001', name: '原名称', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' as const }
    const wrapper = mount(BomManager, {
      props: { sourceData: [], standard: [original], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="编辑物料名称"]').trigger('click')
    await wrapper.get('input[aria-label="内联编辑物料名称"]').setValue('未保存名称')
    await wrapper.get('input[aria-label="内联编辑物料名称"]').trigger('keydown', { key: 'Enter' })

    await wrapper.get('button[aria-label="重新对账"]').trigger('click')
    expect(wrapper.emitted('generate')).toEqual([[true]])
    expect(wrapper.get('.pdm-bom-unsaved-count').text()).toBe('未保存 1 项')

    await wrapper.setProps({ standard: [{ ...original, name: '重新对账名称' }] })
    await flushPromises()
    expect(wrapper.find('.pdm-bom-unsaved-count').exists()).toBe(false)
    expect(wrapper.get('button[aria-label="编辑物料名称"]').text()).toBe('重新对账名称')
  })

  it('asks before discarding unsaved edits when switching BOM categories', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm')
      .mockRejectedValueOnce('cancel')
      .mockResolvedValueOnce('confirm' as never)
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'standard-1', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '标准件', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' }],
        nonStandard: [{ id: 'custom-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'N-001', name: '非标件', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' }],
        electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    const tabs = wrapper.findAll('button[role="tab"]')
    await tabs[1].trigger('click')
    await wrapper.get('button[aria-label="编辑物料名称"]').trigger('click')
    await wrapper.get('input[aria-label="内联编辑物料名称"]').setValue('未保存名称')
    await wrapper.get('input[aria-label="内联编辑物料名称"]').trigger('keydown', { key: 'Enter' })

    await tabs[2].trigger('click')
    await flushPromises()
    expect(tabs[1].attributes('aria-selected')).toBe('true')
    expect(wrapper.get('.pdm-bom-unsaved-count').text()).toBe('未保存 1 项')

    await tabs[2].trigger('click')
    await flushPromises()
    expect(confirm).toHaveBeenLastCalledWith(
      expect.stringContaining('切换分类将放弃这些修改'),
      '放弃未保存修改？',
      expect.objectContaining({ confirmButtonText: '放弃修改并切换', cancelButtonText: '取消' }),
    )
    expect(tabs[2].attributes('aria-selected')).toBe('true')
    expect(wrapper.find('.pdm-bom-unsaved-count').exists()).toBe(false)
  })

  it('does not request reconciliation for a legacy standard-item name-only mismatch', () => {
    const maintained: BomItem = {
      id: 'bom-manual', kind: 'Standard', sequence: 1, drawingNumber: 'S-002', name: '人工维护名称', quantity: 1, unit: '件', revision: 'W2', complete: true,
      source: 'Auto', sourceDocumentId: 'document-2', manuallyOverridden: true, reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与最新图档源数据不一致：物料名称。',
    }
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [{ ...maintained, name: '图档名称', reconciliationNote: 'BOM维护值与图档源数据不一致：物料名称。' }],
        standard: [maintained], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.find('.pdm-bom-reconcile-hint').exists()).toBe(false)
  })

  it('shows raw drawing values and reconciliation columns in the read-only source view', async () => {
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [{ id: 'bom-raw', kind: 'Standard', sequence: 1, drawingNumber: 'RAW-001', name: '图档原始名称', specification: 'RAW-M1', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1', reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与图档源数据不一致：物料编码、物料名称、型号。' }],
        standard: [{ id: 'bom-raw', kind: 'Standard', sequence: 1, drawingNumber: 'BOM-001', name: '维护后名称', specification: 'BOM-M1', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1', manuallyOverridden: true, reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与最新图档源数据不一致：物料编码、物料名称、型号。' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.text()).toContain('RAW-001')
    expect(wrapper.text()).toContain('图档原始名称')
    expect(wrapper.text()).not.toContain('BOM-001')
    expect(wrapper.findAll('thead th').map(header => header.text())).toEqual(expect.arrayContaining(['问题', '资料状态']))
    expect(wrapper.get('.pdm-bom-source').text()).toBe('物料编码、型号不一致')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('已完善')
    expect(wrapper.get('.pdm-bom-classification-indicator').classes()).toContain('is-classified')
    expect(wrapper.get('.pdm-bom-classification-indicator').attributes('aria-label')).toBe('已归类')

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.text()).toContain('BOM-001')
    expect(wrapper.text()).toContain('维护后名称')
    expect(wrapper.find('.pdm-bom-classification-indicator').exists()).toBe(false)
    expect(wrapper.find('tbody .pdm-bom-row-actions').exists()).toBe(true)
    expect(wrapper.get('.pdm-bom-source').text()).toBe('物料编码、型号不一致')
    expect(wrapper.get('.pdm-bom-reconciliation').attributes('title')).toContain('物料编码、型号')
    expect(wrapper.get('.pdm-bom-reconciliation').attributes('title')).not.toContain('物料名称')
    expect(wrapper.find('tbody tr').classes()).not.toContain('is-bom-classification-mismatch')
  })

  it('shows the persisted reconciliation status and concrete explanation', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [{
          id: 'bom-status', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '标准件', quantity: 1, unit: '件', revision: 'W1', complete: true,
          source: 'Auto', sourceDocumentId: 'document-1', reconciliationStatus: 'ClassificationChanged', reconciliationNote: '图档源数据分类由非标件变更为标准件，已自动迁移。',
        }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('.pdm-bom-reconciliation-cell').text()).toBe('—')
    expect(wrapper.find('.pdm-bom-reconciliation').exists()).toBe(false)
  })

  it('confirms manual unmatched rows directly from the reconciliation cell', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const prompt = vi.spyOn(ElMessageBox, 'prompt').mockResolvedValue({ value: '确认删除无来源人工项' } as never)
    const wrapper = mount(BomManager, {
      props: {
        standard: [{
          id: 'manual-unmatched', kind: 'Standard', sequence: 1, drawingNumber: 'S-MANUAL', name: '人工待确认项', quantity: 1, unit: '件', revision: 'W1', complete: true,
          source: 'Manual', manualUnmatched: true, reconciliationStatus: 'ManualUnmatched', reconciliationNote: 'BOM中存在，但最新图档源数据中无对应项，等待确认删除或人工保留。',
        }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    const actions = wrapper.get('.pdm-bom-reconciliation-actions')
    expect(actions.findAll('button').map(button => button.text())).toEqual(['保留', '删除'])
    expect(actions.attributes('title')).toBeUndefined()

    await actions.get('button[aria-label="确认保留人工BOM项 S-MANUAL"]').trigger('click')
    expect(confirm).toHaveBeenCalledOnce()
    expect(wrapper.emitted('resolve')).toEqual([['manual-unmatched', 'retain']])

    await actions.get('button[aria-label="确认删除人工BOM项 S-MANUAL"]').trigger('click')
    expect(prompt).toHaveBeenCalledOnce()
    expect(wrapper.emitted('batchDelete')).toEqual([[['manual-unmatched'], '确认删除无来源人工项']])
  })

  it('batch confirms retainable rows while rejecting a mixed ordinary selection', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(BomManager, {
      props: {
        standard: [
          { id: 'manual-1', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '人工待确认一', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual', manualUnmatched: true },
          { id: 'manual-2', kind: 'Standard', sequence: 2, drawingNumber: 'S-002', name: '人工待确认二', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual', manualUnmatched: true },
          { id: 'ordinary', kind: 'Standard', sequence: 3, drawingNumber: 'S-003', name: '普通物料', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto' },
        ],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    const checkboxes = wrapper.findAll('input[aria-label="选择物料"]')
    await checkboxes[0].setValue(true)
    await checkboxes[2].setValue(true)
    let retainButton = wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '批量确认保留')!
    expect((retainButton.element as HTMLButtonElement).disabled).toBe(true)
    expect(retainButton.attributes('title')).toBe('仅支持同时保留人工待确认或待确认删除的物料')

    await checkboxes[2].setValue(false)
    await checkboxes[1].setValue(true)
    retainButton = wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '批量确认保留')!
    expect((retainButton.element as HTMLButtonElement).disabled).toBe(false)
    await retainButton.trigger('click')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(
      '确认保留所选 2 条待处理BOM项吗？确认后这些物料不再显示为待处理。',
      '批量确认保留',
      expect.objectContaining({ confirmButtonText: '批量确认保留' }),
    )
    expect(wrapper.emitted('batchRetain')).toEqual([[['manual-1', 'manual-2']]])
    expect(wrapper.find('.pdm-bom-selection-summary').exists()).toBe(false)
  })

  it('treats a restored classification-only difference as reconciled', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [{
          id: 'bom-classified', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '已归类零件', quantity: 1, unit: '件', revision: 'W1', complete: true,
          source: 'Auto', sourceDocumentId: 'document-1', manuallyOverridden: true, reconciliationStatus: 'ManualOverrideMismatch',
          reconciliationNote: '已由admin恢复图档属性；BOM分类与排序保持不变，仍与图档源数据不一致：物料分类。',
        }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('.pdm-bom-reconciliation-cell').text()).toBe('—')
    expect(wrapper.find('.pdm-bom-reconciliation').exists()).toBe(false)
  })

  it('restores selected maintained properties from source while keeping classification and order server-side', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [],
        standard: [
          { id: 'bom-source', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '维护名称', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1' },
          { id: 'bom-manual', kind: 'Standard', sequence: 2, drawingNumber: 'S-002', name: '人工名称', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
        ],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="编辑物料名称"]').trigger('click')
    const editor = wrapper.get('input[aria-label="内联编辑物料名称"]')
    await editor.setValue('待恢复名称')
    await editor.trigger('keydown', { key: 'Enter' })
    expect(wrapper.get('.pdm-bom-unsaved-count').text()).toBe('未保存 1 项')
    await wrapper.findAll('input[aria-label="选择物料"]')[0].setValue(true)
    const restore = wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '恢复源数据')!
    expect(restore.attributes('disabled')).toBeUndefined()
    await restore.trigger('click')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('分类与当前排序不会改变'), '恢复源数据', expect.any(Object))
    expect(wrapper.emitted('restoreSource')).toEqual([[['bom-source']]])
    await wrapper.setProps({
      standard: [
        { id: 'bom-source', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '图档源名称', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1' },
        { id: 'bom-manual', kind: 'Standard', sequence: 2, drawingNumber: 'S-002', name: '人工名称', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
      ],
    })
    await flushPromises()
    expect(wrapper.findAll('button[aria-label="编辑物料名称"]')[0].text()).toBe('图档源名称')
    expect(wrapper.find('.pdm-bom-unsaved-count').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('重置')
    expect(wrapper.text()).toContain('清空筛选')
  })

  it('shows source differences only in the reconciliation status', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [
          {
            id: 'bom-mismatch', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '待写回分类', specification: 'M1', brand: '测试品牌', quantity: 1, unit: '件', revision: 'W1', complete: true,
            source: 'Auto', sourceDocumentId: 'document-1', manuallyOverridden: true, reconciliationStatus: 'ManuallyClassified', reconciliationNote: '已由admin人工归入标准件BOM。', propertyWritebackStatus: 'Pending',
          },
          {
            id: 'bom-synced', kind: 'Standard', sequence: 2, drawingNumber: 'S-002', name: '已同步分类', specification: 'M2', brand: '测试品牌', quantity: 1, unit: '件', revision: 'W1', complete: true,
            source: 'Auto', sourceDocumentId: 'document-2', manuallyOverridden: true, reconciliationStatus: 'ManuallyClassified', reconciliationNote: '已由admin人工归入标准件BOM。', propertyWritebackStatus: 'Succeeded',
          },
        ],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    const rows = wrapper.findAll('tbody tr')
    expect(rows[0].classes()).not.toContain('is-bom-classification-mismatch')
    expect(rows[0].get('.pdm-bom-source').classes()).not.toContain('is-classification-mismatch')
    expect(rows[0].get('.pdm-bom-source').classes()).toContain('is-warning')
    expect(rows[0].get('.pdm-bom-source').text()).toBe('分类不一致')
    expect(rows[0].classes()).toContain('is-reconciliation-issue')
    expect(rows[0].findAll('td').filter(cell => cell.classes().some(name => name.endsWith('-cell'))).map(cell => cell.classes())).toEqual([
      ['pdm-bom-model-cell'],
      ['pdm-bom-drawing-audit-cell'],
      ['pdm-bom-reconciliation-cell'],
      ['pdm-bom-data-status-cell', 'is-complete'],
    ])
    expect(rows[0].get('.pdm-bom-data-status').classes()).toContain('is-complete')
    expect(rows[1].classes()).not.toContain('is-bom-classification-mismatch')
    expect(rows[1].get('.pdm-bom-reconciliation-cell').text()).toBe('—')
    expect(rows[1].find('.pdm-bom-source').exists()).toBe(false)
  })

  it('keeps source rows read-only and exposes maintained and virtual classifications', async () => {
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [
          { id: 'missing-1', sequence: 1, drawingNumber: 'S-001', name: '已删除一', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1', pendingRemoval: true },
          { id: 'missing-2', sequence: 2, drawingNumber: 'S-002', name: '已删除二', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-2', pendingRemoval: true },
        ],
        standard: [], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.get('input[aria-label="选择全部源数据物料"]').setValue(true)
    expect(wrapper.get('.pdm-bom-selection-summary').text()).toBe('已选择 2 项')
    const sourceActions = wrapper.findAll('.pdm-bom-selection-actions button').map(button => button.text())
    expect(sourceActions[0]).toContain('标准件BOM')
    expect(sourceActions[1]).toContain('非标件BOM')
    expect(sourceActions.slice(2)).toEqual(['设为虚拟件', '回收站（0）'])
    expect(wrapper.emitted('batchDelete')).toBeUndefined()
  })

  it('preserves virtual source classifications in summary, structure and filters without saving', async () => {
    const virtual: BomItem = { id: 'virtual-1', kind: 'Virtual', sequence: 1, drawingNumber: 'BALL', name: '钢球', quantity: 1, unit: '个', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'ball', sourceInstancePath: 'root/1' }
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [virtual, { ...virtual, id: 'virtual-2', sequence: 2, quantity: 2, sourceInstancePath: 'root/2' }],
        standard: [], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.get('.pdm-bom-kind').text()).toBe('虚拟件')
    expect(wrapper.get('.pdm-bom-kind').attributes('data-kind')).toBe('Virtual')
    expect(wrapper.find('.is-bom-unresolved').exists()).toBe(false)
    expect(wrapper.get('.pdm-bom-classification-indicator').attributes('aria-label')).toBe('已归类')
    expect(wrapper.get('.pdm-bom-quantity-audit').text()).toBe('3 / 3')
    await wrapper.get('select[aria-label="筛选物料分类"]').setValue('Virtual')
    expect(wrapper.findAll('.pdm-bom-kind')).toHaveLength(1)
    await wrapper.get('input[aria-label="仅显示待处理"]').setValue(true)
    expect(wrapper.findAll('.pdm-bom-kind')).toHaveLength(0)
    await wrapper.get('input[aria-label="仅显示待处理"]').setValue(false)
    await wrapper.get('[aria-label="BOM显示方式"] button:nth-of-type(2)').trigger('click')
    expect(wrapper.findAll('.pdm-bom-kind').map(cell => cell.text())).toEqual(['虚拟件', '虚拟件'])
    expect(wrapper.findAll('.pdm-bom-kind').map(cell => cell.attributes('data-kind'))).toEqual(['Virtual', 'Virtual'])
    await wrapper.get('select[aria-label="筛选物料分类"]').setValue('Unclassified')
    expect(wrapper.findAll('.pdm-bom-kind')).toHaveLength(0)
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it.each([
    ['Standard', 'standard', '标准件', 1],
    ['NonStandard', 'nonStandard', '非标件', 2],
    ['Electrical', 'electrical', '电气件', 3],
  ] as const)('marks %s text consistently for category colors in source and editable BOMs', async (kind, category, label, tabIndex) => {
    const item: BomItem = { id: 'item', kind, sequence: 1, drawingNumber: 'PART', name: '零件', quantity: 1, unit: '个', revision: 'W1', complete: true }
    const wrapper = mount(BomManager, {
      props: { sourceData: [item], standard: [], nonStandard: [], electrical: [], [category]: [item], declarations: [], pending: false, editable: true },
    })
    expect(wrapper.get('.pdm-bom-kind').text()).toBe(label)
    expect(wrapper.get('.pdm-bom-kind').attributes('data-kind')).toBe(kind)
    await wrapper.findAll('button[role="tab"]')[tabIndex].trigger('click')
    expect(wrapper.get('button.pdm-bom-kind').text()).toBe(label)
    expect(wrapper.get('button.pdm-bom-kind').attributes('data-kind')).toBe(kind)
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('does not hide pending or mixed classifications when virtual source rows are summarized', () => {
    const item: BomItem = { id: 'virtual', kind: 'Virtual', sequence: 1, drawingNumber: 'PART', name: '零件', quantity: 1, unit: '个', revision: 'W1', complete: false }
    const standard: BomItem = { ...item, id: 'standard', kind: 'Standard', sequence: 2 }
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [item, standard, { ...item, id: 'pending', drawingNumber: 'PENDING', sequence: 3, pendingClassification: true }],
        standard: [standard], nonStandard: [], electrical: [], declarations: [], pending: false,
      },
    })
    expect(wrapper.findAll('.pdm-bom-kind').map(cell => cell.text())).toEqual(['待分类', '待分类'])
    expect(wrapper.findAll('.is-bom-unresolved')).toHaveLength(2)
  })

  it('uses maintained BOM classification for source-row status', () => {
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [
          { id: 'classified', kind: 'Unclassified', sequence: 1, drawingNumber: 'S-001', name: '已归类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1', pendingClassification: true },
          { id: 'pending', kind: 'Unclassified', sequence: 2, drawingNumber: 'P-001', name: '待分类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-2', pendingClassification: true },
        ],
        standard: [{ id: 'classified', kind: 'Standard', sequence: 1, drawingNumber: 'S-001', name: '已归类零件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1' }],
        nonStandard: [],
        unclassified: [{ id: 'pending', kind: 'Unclassified', sequence: 1, drawingNumber: 'P-001', name: '待分类零件', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-2', pendingClassification: true }],
        electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0].get('.pdm-bom-classification-indicator').attributes('aria-label')).toBe('已归类')
    expect(rows[0].get('.pdm-bom-kind').text()).toBe('标准件')
    expect(rows[0].classes()).not.toContain('is-bom-unresolved')
    expect(rows[1].get('.pdm-bom-classification-indicator').attributes('aria-label')).toBe('未归类')
    expect(rows[1].get('.pdm-bom-kind').text()).toBe('待分类')
    expect(rows[1].classes()).toContain('is-bom-unresolved')
  })

  it('keeps excluded drawing rows in source data but hides them from categorized BOM counts', async () => {
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [
          { id: 'source-standard', sequence: 1, drawingNumber: 'S-001', name: '图纸标准件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-s' },
          { id: 'source-custom', sequence: 2, drawingNumber: 'N-001', name: '图纸非标件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-n' },
        ],
        standard: [
          { id: 'source-standard', sequence: 1, drawingNumber: 'S-001', name: '图纸标准件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-s', manuallyExcluded: true },
          { id: 'manual-standard', sequence: 2, drawingNumber: 'S-002', name: '人工标准件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' },
        ],
        nonStandard: [{ id: 'source-custom', sequence: 1, drawingNumber: 'N-001', name: '图纸非标件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-n' }],
        electrical: [{ id: 'electrical', sequence: 1, drawingNumber: 'E-001', name: '电气件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' }],
        declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.get('.pdm-source-data-tab').text()).toBe('源数据（2）')
    expect(wrapper.findAll('.pdm-bom-name-value').map(value => value.text())).toEqual(['图纸标准件', '图纸非标件'])
    expect(wrapper.text()).not.toContain('已人工排除')
    expect(wrapper.find('tbody .pdm-table-action').exists()).toBe(false)

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.findAll('button[role="tab"]')[1].text()).toBe('标准件BOM（1）')
    expect(wrapper.findAll('.pdm-bom-name-value').map(value => value.text())).toEqual(['人工标准件'])
    expect(wrapper.findAll('thead th').map(header => header.text())).not.toContain('操作')
    expect(wrapper.findAll('.pdm-bom-selection-toolbar button').map(button => button.text())).toContain('编辑')
  })

  it('lists deleted source and manual rows in the recycle bin and restores both by their original source', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'deleted-source', kind: 'Standard', sequence: 1, drawingNumber: 'S-DEL', name: '有源删除项', quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-s', manuallyExcluded: true, deletedBy: 'admin', deletedAt: '2026-08-20T10:00:00Z', deleteReason: '误删' }],
        nonStandard: [],
        electrical: [{ id: 'deleted-manual', kind: 'Electrical', sequence: 1, drawingNumber: 'E-DEL', name: '人工删除项', quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Manual', manuallyExcluded: true, deleteReason: '停用前确认' }],
        declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '回收站（2）')!.trigger('click')
    expect(wrapper.get('#pdm-bom-recycle-title').text()).toBe('BOM回收站')
    expect(wrapper.findAll('.pdm-bom-recycle-table tbody tr')).toHaveLength(2)

    await wrapper.get('input[aria-label="选择回收站全部物料"]').setValue(true)
    expect(wrapper.text()).toContain('恢复后保持删除前的数据来源')
    expect(wrapper.text()).toContain('有源数据仍关联原图档')
    expect(wrapper.text()).toContain('人工添加数据仍保持人工')
    expect(wrapper.findAll('.pdm-bom-recycle-dialog footer button')).toHaveLength(1)
    await wrapper.findAll('.pdm-bom-recycle-dialog footer button').find(button => button.text() === '恢复选中')!.trigger('click')
    expect(wrapper.emitted('batchRestore')).toEqual([[['deleted-source', 'deleted-manual'], 'Original']])
  })

  it('drags whole rows from the left handle and removes arrow ordering controls', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [
          { id: 'bom-1', sequence: 1, drawingNumber: 'S-001', name: '第一项', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' },
          { id: 'bom-2', sequence: 2, drawingNumber: 'S-002', name: '第二项', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' },
        ],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.findAll('.pdm-bom-selection-toolbar button').map(button => button.text())).not.toEqual(expect.arrayContaining(['上移', '下移', '插入物料']))
    expect(wrapper.find('button[aria-label^="上移第"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label^="下移第"]').exists()).toBe(false)
    expect(wrapper.findAll('.pdm-bom-row-drag-handle')).toHaveLength(2)

    const targetRow = wrapper.findAll('tbody tr')[1]
    Object.defineProperty(document, 'elementFromPoint', {
      configurable: true,
      value: () => null,
    })
    const elementFromPoint = vi.spyOn(document, 'elementFromPoint').mockReturnValue(targetRow.element)
    await wrapper.get('[aria-label="拖动第 1 行排序"]').trigger('pointerdown', { button: 0, pointerId: 7, clientX: 20, clientY: 1 })
    const move = new MouseEvent('pointermove', { bubbles: true, clientX: 20, clientY: 1 })
    Object.defineProperty(move, 'pointerId', { value: 7 })
    document.dispatchEvent(move)
    await flushPromises()
    expect(wrapper.findAll('tbody tr')[1].classes()).toContain('is-drag-over-after')
    const up = new MouseEvent('pointerup', { bubbles: true, clientX: 20, clientY: 1 })
    Object.defineProperty(up, 'pointerId', { value: 7 })
    document.dispatchEvent(up)
    await flushPromises()
    elementFromPoint.mockRestore()
    expect(wrapper.findAll('tbody tr').some(row => row.classes().includes('is-drag-over-after'))).toBe(false)
    expect(wrapper.findAll('tbody tr').slice(0, 2).map(row => row.find('td:nth-child(6)').text())).toEqual(['S-002', 'S-001'])
    expect(wrapper.findAll('tbody tr').slice(0, 2).map(row => row.get('.pdm-bom-sequence-value').text())).toEqual(['1', '2'])
    expect(wrapper.find('td:nth-child(3) input').exists()).toBe(false)
    expect(wrapper.findAll('thead th').map(header => header.text())).not.toContain('操作')
  })

  it('inserts a new material directly below the clicked row from the plus column', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [
          { id: 'bom-1', sequence: 1, drawingNumber: 'S-001', name: '第一项', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
          { id: 'bom-2', sequence: 2, drawingNumber: 'S-002', name: '第二项', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
        ],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.find('.pdm-bom-insert-button').exists()).toBe(false)
    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.findAll('.pdm-bom-insert-button')).toHaveLength(2)

    await wrapper.get('button[aria-label="在第 1 行下方插入物料"]').trigger('click')

    const rows = wrapper.findAll('tbody tr')
    expect(rows).toHaveLength(3)
    expect(rows.map(row => row.get('.pdm-bom-sequence-value').text())).toEqual(['1', '2', '3'])
    expect(rows[0].find('td:nth-child(6)').text()).toBe('S-001')
    expect((rows[1].get('input[aria-label="物料编码"]').element as HTMLInputElement).value).toBe('')
    expect(rows[1].find('td:nth-child(5)').text()).toBe('个')
    expect(rows[2].find('td:nth-child(6)').text()).toBe('S-002')
    expect(wrapper.findAll('.pdm-bom-insert-button')).toHaveLength(3)
    expect(wrapper.findAll('.pdm-bom-delete-draft-button')).toHaveLength(1)

    await wrapper.get('button[aria-label="删除未保存的第 2 行"]').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('tbody tr').map(row => row.get('.pdm-bom-sequence-value').text())).toEqual(['1', '2'])
    expect(wrapper.findAll('.pdm-bom-delete-draft-button')).toHaveLength(0)
  })

  it('keeps duplicate material codes as separate relationship rows', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [
          { id: 'bom-1', sequence: 1, drawingNumber: 'SAME-001', name: '配套物料', parentDrawingNumber: 'PARENT-A', quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Manual' },
        ],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="在第 1 行下方插入物料"]').trigger('click')
    const draft = wrapper.findAll('tbody tr')[1]
    await draft.get('input[aria-label="物料编码"]').setValue('SAME-001')
    await draft.get('input[aria-label="物料名称"]').setValue('配套物料')
    await draft.get('input[aria-label="上级物料编码"]').setValue('PARENT-B')
    await wrapper.get('button.pdm-primary-action').trigger('click')

    const savedItems = wrapper.emitted('save')?.[0]?.[1] as BomItem[]
    expect(savedItems).toHaveLength(2)
    expect(savedItems.map(item => item.drawingNumber)).toEqual(['SAME-001', 'SAME-001'])
    expect(savedItems.map(item => item.parentDrawingNumber)).toEqual(['PARENT-A', 'PARENT-B'])
  })

  it('keeps an inserted material while its fields are edited and parent BOM data refreshes', async () => {
    const standard = [
      { id: 'bom-1', sequence: 1, drawingNumber: 'S-001', name: '第一项', specification: 'M1', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' as const },
      { id: 'bom-2', sequence: 2, drawingNumber: 'S-002', name: '第二项', specification: 'M2', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' as const },
    ]
    const wrapper = mount(BomManager, {
      props: { standard, nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="在第 1 行下方插入物料"]').trigger('click')

    const draft = wrapper.findAll('tbody tr')[1]
    await draft.get('input[aria-label="物料编码"]').setValue('S-NEW')
    await draft.get('input[aria-label="物料名称"]').setValue('新增物料')
    await draft.get('input[aria-label="型号"]').setValue('M-NEW')
    await draft.get('input[aria-label="数量"]').setValue('2')
    expect(draft.get('input[aria-label="数量"]').classes()).toContain('pdm-bom-quantity-editor')

    await wrapper.setProps({ standard: standard.map(item => ({ ...item })) })
    await flushPromises()

    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    const preserved = wrapper.findAll('tbody tr')[1]
    expect((preserved.get('input[aria-label="物料编码"]').element as HTMLInputElement).value).toBe('S-NEW')
    expect((preserved.get('input[aria-label="数量"]').element as HTMLInputElement).value).toBe('2')

    await wrapper.get('button.pdm-primary-action').trigger('click')
    const savedItems = wrapper.emitted('save')?.[0]?.[1] as BomItem[]
    expect(savedItems[1]).toMatchObject({ drawingNumber: 'S-NEW', name: '新增物料', specification: 'M-NEW', quantity: 2 })
    expect(savedItems[1] as unknown as Record<string, unknown>).not.toHaveProperty('_clientKey')

    await wrapper.setProps({
      pending: true,
      standard: [standard[0], { ...savedItems[1], id: 'bom-new' }, standard[1]],
    })
    await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    expect(wrapper.find('input[aria-label="物料编码"]').exists()).toBe(false)
  })

  it('edits clicked cells inline and reserves the dialog for checkbox-driven editing', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [{
          id: 'bom-edit', sequence: 1, drawingNumber: 'STD-001', name: '轴承', specification: '6204', remark: '备注', brand: '品牌', material: 'GCr15', surfaceTreatment: '发黑', quantity: 2, unit: '个', revision: 'W1', complete: true, source: 'Manual',
        }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.findAll('tbody button.pdm-bom-cell-edit').map(button => button.attributes('aria-label'))).toEqual([
      '编辑物料分类', '编辑物料编码', '编辑物料名称', '编辑上级物料编码', '编辑型号', '编辑备注信息', '编辑品牌', '编辑材质', '编辑表面处理', '编辑数量',
    ])

    await wrapper.get('button[aria-label="编辑物料名称"]').trigger('click')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    const nameField = wrapper.get('input[aria-label="内联编辑物料名称"]')
    expect((nameField.element as HTMLInputElement).value).toBe('轴承')
    await nameField.setValue('新轴承')
    await nameField.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('batchUpdate')).toBeUndefined()
    expect(wrapper.get('.pdm-bom-unsaved-count').text()).toBe('未保存 1 项')
    expect(wrapper.get('button[aria-label="编辑物料名称"]').text()).toBe('新轴承')

    await wrapper.get('button[aria-label="编辑物料分类"]').trigger('click')
    await wrapper.get('select[aria-label="内联编辑物料分类"]').setValue('NonStandard')
    expect(wrapper.emitted('batchUpdate')?.[0]).toEqual([{
      itemIds: ['bom-edit'], fields: ['kind'], targetKind: 'NonStandard',
    }])

    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    expect((wrapper.get('input[aria-label="选择物料"]').element as HTMLInputElement).checked).toBe(true)
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '编辑')!.trigger('click')
    expect(wrapper.get('[role="dialog"]').isVisible()).toBe(true)
    const classificationField = wrapper.get('.pdm-bom-batch-field')
    expect(classificationField.findAll('option').map(option => option.text())).toEqual(['标准件', '非标件', '虚拟件'])
    await classificationField.get('input[type="checkbox"]').setValue(true)
    await classificationField.get('select').setValue('Virtual')
    await wrapper.findAll('[role="dialog"] button').find(button => button.text() === '确认批量修改')!.trigger('click')
    expect(wrapper.emitted('batchUpdate')?.[1]).toEqual([{
      itemIds: ['bom-edit'], fields: ['kind'], targetKind: 'Virtual',
    }])
  })

  it('does not rematch or stage an unchanged inline material-code edit', async () => {
    materialApi.listMaterials.mockResolvedValue([])
    const wrapper = mount(BomManager, {
      props: {
        projectId: 'project-1', token: 'token',
        standard: [{
          id: 'bom-unchanged', kind: 'Standard', sequence: 1, drawingNumber: '01020014733', name: '导向轴支座', specification: 'SHF20', quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1',
        }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await flushPromises()
    materialApi.listMaterials.mockClear()

    await wrapper.get('button[aria-label="编辑物料编码"]').trigger('click')
    const editor = wrapper.get('input[aria-label="内联编辑物料编码"]')
    expect((editor.element as HTMLInputElement).value).toBe('01020014733')
    await editor.trigger('blur')
    await flushPromises()

    expect(materialApi.listMaterials).not.toHaveBeenCalled()
    expect(wrapper.find('.pdm-bom-unsaved-count').exists()).toBe(false)
    expect(wrapper.get('button[aria-label="编辑物料编码"]').text()).toBe('01020014733')
  })

  it('allows an unclassified drawing item to select non-standard on the first attempt', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [],
        nonStandard: [],
        sourceData: [{
          id: 'bom-first-classification', kind: 'Unclassified', sequence: 1, drawingNumber: 'N-001', name: '首次分类零件', quantity: 1, unit: '个', revision: 'W1', complete: false,
          source: 'Auto', sourceDocumentId: 'document-1', pendingClassification: true,
        }],
        unclassified: [{
          id: 'bom-first-classification', kind: 'Unclassified', sequence: 1, drawingNumber: 'N-001', name: '首次分类零件', quantity: 1, unit: '个', revision: 'W1', complete: false,
          source: 'Auto', sourceDocumentId: 'document-1', pendingClassification: true,
        }],
        electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    await wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text().includes('非标件BOM'))!.trigger('click')

    expect(wrapper.emitted('batchUpdate')).toEqual([[{
      itemIds: ['bom-first-classification'], fields: ['kind'], targetKind: 'NonStandard',
    }]])
  })

  it('filters materials by searchable fields, classification, brand and material', async () => {
    const wrapper = mount(BomManager, {
      props: {
        sourceData: [
          { id: 'standard-bearing', kind: 'Standard', sequence: 1, drawingNumber: 'STD-6204', name: '深沟球轴承', specification: '6204', brand: 'SKF', material: '轴承钢', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1' },
          { id: 'custom-base', kind: 'NonStandard', sequence: 2, drawingNumber: 'N-001', name: '安装底座', specification: 'BASE-01', brand: 'UPTON', material: '6061', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-2' },
          { id: 'pending-part', kind: 'Unclassified', sequence: 3, drawingNumber: 'P-001', name: '待确认零件', specification: 'PENDING', quantity: 1, unit: '个', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-3', pendingClassification: true },
        ],
        standard: [{
          id: 'standard-bearing', kind: 'Standard', sequence: 1, drawingNumber: 'STD-6204', name: '深沟球轴承', specification: '6204', brand: 'SKF', material: '轴承钢', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1',
        }],
        nonStandard: [{
          id: 'custom-base', kind: 'NonStandard', sequence: 2, drawingNumber: 'N-001', name: '安装底座', specification: 'BASE-01', brand: 'UPTON', material: '6061', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-2',
        }],
        unclassified: [{
          id: 'pending-part', kind: 'Unclassified', sequence: 3, drawingNumber: 'P-001', name: '待确认零件', specification: 'PENDING', quantity: 1, unit: '个', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-3', pendingClassification: true,
        }],
        electrical: [], declarations: [], pending: false, editable: true,
      },
    })

    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    await wrapper.get('input[aria-label="搜索BOM物料"]').setValue('6204')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.get('.pdm-bom-name-value').text()).toBe('深沟球轴承')
    expect(wrapper.find('.pdm-bom-selection-summary').exists()).toBe(false)

    await wrapper.get('input[aria-label="搜索BOM物料"]').setValue('')
    await wrapper.get('select[aria-label="筛选物料分类"]').setValue('NonStandard')
    await wrapper.get('select[aria-label="筛选品牌"]').setValue('UPTON')
    await wrapper.get('select[aria-label="筛选材质"]').setValue('6061')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.get('.pdm-bom-name-value').text()).toBe('安装底座')

    await wrapper.get('input[aria-label="选择全部源数据物料"]').setValue(true)
    expect(wrapper.findAll('input[aria-label="选择物料"]').filter(input => (input.element as HTMLInputElement).checked)).toHaveLength(1)

    await wrapper.get('select[aria-label="筛选物料分类"]').setValue('All')
    await wrapper.get('select[aria-label="筛选品牌"]').setValue('')
    await wrapper.get('select[aria-label="筛选材质"]').setValue('')
    expect((wrapper.get('select[aria-label="筛选物料分类"]').element as HTMLSelectElement).value).toBe('All')
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
  })

  it('searches approved material masters by code and links the selected BOM row', async () => {
    materialApi.listMaterials.mockResolvedValue([{
      id: 'material-2', materialCode: '01020000002', name: '备用轴承', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
      specification: '6205', brand: 'FAG', remark: '备用', referenceCount: 2, approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19T00:00:00Z',
      updatedBy: 'admin', updatedAt: '2026-08-19T00:00:00Z', rowVersion: 2, categoryCode: '0102', isArchived: false,
    }, {
      id: 'material-1', materialCode: '01020000001', name: '标准轴承', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
      specification: '6204', brand: 'SKF', remark: '优先选用', referenceCount: 5, approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19T00:00:00Z',
      updatedBy: 'admin', updatedAt: '2026-08-19T00:00:00Z', rowVersion: 2, categoryCode: '0102', isArchived: false,
    }])
    materialApi.linkBomMaterial.mockResolvedValue({ id: 'material-1' })
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: 'OLD-001', name: '旧名称', quantity: 1, unit: '件', revision: 'W1', complete: true }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text().includes('按编码引用料品'))!.trigger('click')
    await flushPromises()

    expect(materialApi.listMaterials).toHaveBeenCalledWith('token', 'OLD-001')
    expect(wrapper.text()).toContain('01020000001')
    const dialog = wrapper.get('.pdm-material-reference-dialog')
    expect(dialog.findAll('th').map(cell => cell.text())).toEqual(['物料编码', '名称', '分类', '引用次数', '规格', '品牌', '备注', '同步', ''])
    expect(dialog.findAll('tbody tr').map(row => row.findAll('td')[0].text())).toEqual(['01020000001', '01020000002'])
    expect(dialog.findAll('tbody tr')[0].text()).toContain('SKF')
    expect(dialog.findAll('tbody tr')[0].text()).toContain('优先选用')
    expect(dialog.findAll('tbody tr')[0].text()).toContain('5')
    await dialog.get('input[aria-label="筛选引用料品品牌"]').setValue('sk')
    expect(dialog.findAll('tbody tr')).toHaveLength(2)
    await dialog.findAll('button').find(button => button.text() === '查询')!.trigger('click')
    await flushPromises()
    expect(dialog.findAll('tbody tr')).toHaveLength(1)
    await dialog.findAll('button').find(button => button.text() === '引用')!.trigger('click')
    await flushPromises()

    expect(materialApi.linkBomMaterial).toHaveBeenCalledWith('project-1', 'bom-1', 'material-1', 'token')
    expect(wrapper.emitted('save')?.[0]?.[1]).toEqual(expect.arrayContaining([expect.objectContaining({ drawingNumber: '01020000001', name: '标准轴承' })]))
  })

  it('selects a newly inserted row and links its material after the save assigns a BOM id', async () => {
    vi.spyOn(ElMessage, 'success').mockImplementation(() => undefined as never)
    materialApi.listMaterials.mockResolvedValue([{
      id: 'material-new', materialCode: '01020000999', name: '新增引用料品', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
      specification: 'NEW-999', brand: 'UPTON', remark: '新增行引用', referenceCount: 0, approvalStatus: 'Approved', syncStatus: 'Succeeded',
      createdBy: 'admin', createdAt: '2026-08-21T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-21T00:00:00Z', rowVersion: 1,
      categoryCode: '0102', isArchived: false,
    }])
    materialApi.linkBomMaterial.mockResolvedValue({ id: 'material-new' })
    const existing = { id: 'bom-1', sequence: 1, drawingNumber: 'S-001', name: '原物料', quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Manual' as const }
    const wrapper = mount(BomManager, {
      props: {
        standard: [existing], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="在第 1 行下方插入物料"]').trigger('click')
    const draft = wrapper.findAll('tbody tr')[1]
    const draftCheckbox = draft.get('input[aria-label="选择物料"]')
    expect((draftCheckbox.element as HTMLInputElement).disabled).toBe(false)

    await draftCheckbox.setValue(true)
    const referenceButton = wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '按编码引用料品')!
    const editButton = wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '编辑')!
    const deleteButton = wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '删除')!
    expect((referenceButton.element as HTMLButtonElement).disabled).toBe(false)
    expect((editButton.element as HTMLButtonElement).disabled).toBe(true)
    expect((deleteButton.element as HTMLButtonElement).disabled).toBe(true)

    await referenceButton.trigger('click')
    await flushPromises()
    await wrapper.get('.pdm-material-reference-dialog tbody button').trigger('click')
    await flushPromises()

    expect(materialApi.linkBomMaterial).not.toHaveBeenCalled()
    const savedItems = wrapper.emitted('save')?.[0]?.[1] as BomItem[]
    expect(savedItems[1]).toMatchObject({ drawingNumber: '01020000999', name: '新增引用料品', specification: 'NEW-999' })

    await wrapper.setProps({ pending: true, standard: [existing, { ...savedItems[1], id: 'bom-new' }] })
    await flushPromises()

    expect(materialApi.linkBomMaterial).toHaveBeenCalledWith('project-1', 'bom-new', 'material-new', 'token')
    expect(ElMessage.success).toHaveBeenCalledWith('已引用料品 01020000999')
  })

  it('opens material references without a selected row and appends the chosen material at the bottom', async () => {
    materialApi.listMaterials.mockResolvedValue([{
      id: 'material-new', materialCode: '01020000999', name: '新增引用料品', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
      specification: 'NEW-999', brand: 'UPTON', remark: '新增行引用', referenceCount: 0, approvalStatus: 'Approved', syncStatus: 'Succeeded',
      createdBy: 'admin', createdAt: '2026-08-21T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-21T00:00:00Z', rowVersion: 1,
      categoryCode: '0102', isArchived: false,
    }])
    const existing = { id: 'bom-1', sequence: 1, drawingNumber: 'S-001', name: '原物料', quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Manual' as const }
    const wrapper = mount(BomManager, {
      props: {
        standard: [existing], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    const referenceButton = wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '按编码引用料品')!
    expect((referenceButton.element as HTMLButtonElement).disabled).toBe(false)
    expect(referenceButton.classes()).toContain('pdm-primary-action')

    await referenceButton.trigger('click')
    await flushPromises()
    expect(materialApi.listMaterials).toHaveBeenCalledWith('token', '')

    await wrapper.get('.pdm-material-reference-dialog tbody button').trigger('click')
    await flushPromises()

    expect(materialApi.linkBomMaterial).not.toHaveBeenCalled()
    const savedItems = wrapper.emitted('save')?.[0]?.[1] as BomItem[]
    expect(savedItems).toHaveLength(2)
    expect(savedItems[0]).toMatchObject({ id: 'bom-1', drawingNumber: 'S-001' })
    expect(savedItems[1]).toMatchObject({ sequence: 2, drawingNumber: '01020000999', name: '新增引用料品', specification: 'NEW-999' })
  })

  it('opens an ambiguous material review with the current row filters and only its candidates', async () => {
    const candidates = [
      { id: 'material-1', materialCode: '01020000001', name: '气缸A', kind: 'Standard' as const, supplyMode: 'Purchase' as const, unitCode: '001', specification: 'CP96', brand: 'SMC', approvalStatus: 'Approved' as const, syncStatus: 'Succeeded' as const, createdBy: 'admin', createdAt: '2026-09-02', updatedBy: 'admin', updatedAt: '2026-09-02', rowVersion: 1, isArchived: false },
      { id: 'material-2', materialCode: '01020000002', name: '气缸B', kind: 'Standard' as const, supplyMode: 'Purchase' as const, unitCode: '001', specification: 'CP96', brand: 'SMC', approvalStatus: 'Approved' as const, syncStatus: 'Succeeded' as const, createdBy: 'admin', createdAt: '2026-09-02', updatedBy: 'admin', updatedAt: '2026-09-02', rowVersion: 1, isArchived: false },
    ]
    materialApi.resolveBomMaterialCodes.mockResolvedValue([{ bomItemId: 'bom-1', status: 'Ambiguous', material: null, candidates, application: null, issues: [] }])
    materialApi.listMaterials.mockResolvedValue([{ ...candidates[0], id: 'unrelated', materialCode: '01020000999', name: '无关料品', specification: 'OTHER' }])
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: '', name: '待核对气缸', specification: 'CP96', brand: 'SMC', quantity: 1, unit: '001', revision: 'W1', complete: false }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().includes('匹配到 2 个'))!.trigger('click')

    const dialog = wrapper.get('.pdm-material-reference-dialog')
    expect((dialog.get('input[aria-label="筛选引用料品品牌"]').element as HTMLInputElement).value).toBe('SMC')
    expect((dialog.get('input[aria-label="搜索料品主档"]').element as HTMLInputElement).value).toBe('CP96')
    expect(dialog.text()).toContain('01020000001')
    expect(dialog.text()).toContain('01020000002')
    expect(dialog.text()).not.toContain('无关料品')
    expect(materialApi.listMaterials).not.toHaveBeenCalled()
  })

  it('places brand first and only applies its filter after querying', async () => {
    materialApi.listMaterials.mockResolvedValue([
      { id: 'material-smc', materialCode: '01020000001', name: 'SMC气缸', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'CP96', brand: 'SMC', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19', updatedBy: 'admin', updatedAt: '2026-08-19', rowVersion: 1, isArchived: false },
      { id: 'material-festo', materialCode: '01020000002', name: 'FESTO气缸', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'CP96', brand: 'FESTO', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19', updatedBy: 'admin', updatedAt: '2026-08-19', rowVersion: 1, isArchived: false },
    ])
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: 'OLD-001', name: '旧物料', quantity: 1, unit: '001', revision: 'W1', complete: true }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text().includes('按编码引用料品'))!.trigger('click')
    await flushPromises()

    const dialog = wrapper.get('.pdm-material-reference-dialog')
    expect(dialog.findAll('.pdm-material-reference-search input').map(input => input.attributes('aria-label'))).toEqual(['筛选引用料品品牌', '搜索料品主档'])
    expect(dialog.findAll('.pdm-material-reference-table tbody tr')).toHaveLength(2)
    expect(materialApi.listMaterials).toHaveBeenCalledTimes(1)

    await dialog.get('input[aria-label="筛选引用料品品牌"]').setValue('SMC')
    expect(dialog.findAll('.pdm-material-reference-table tbody tr')).toHaveLength(2)
    expect(materialApi.listMaterials).toHaveBeenCalledTimes(1)

    await dialog.findAll('button').find(button => button.text() === '查询')!.trigger('click')
    await flushPromises()
    expect(dialog.findAll('.pdm-material-reference-table tbody tr')).toHaveLength(1)
    expect(dialog.text()).toContain('SMC气缸')
    expect(dialog.text()).not.toContain('FESTO气缸')
    expect(materialApi.listMaterials).toHaveBeenCalledTimes(2)
  })

  it('paginates material references by 20 items by default and supports 50 or 100 items per page', async () => {
    materialApi.listMaterials.mockResolvedValue(Array.from({ length: 60 }, (_, index) => ({
      id: `material-${index + 1}`,
      materialCode: `0102${String(index + 1).padStart(7, '0')}`,
      name: `料品${index + 1}`,
      kind: 'Standard' as const,
      supplyMode: 'Purchase' as const,
      unitCode: '001',
      specification: `SPEC-${index + 1}`,
      brand: 'FESTO',
      remark: '测试料品',
      referenceCount: index,
      approvalStatus: 'Approved' as const,
      syncStatus: 'Succeeded' as const,
      createdBy: 'admin',
      createdAt: '2026-08-21T00:00:00Z',
      updatedBy: 'admin',
      updatedAt: '2026-08-21T00:00:00Z',
      rowVersion: 1,
      categoryCode: '0102',
      isArchived: false,
    })))
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: 'OLD-001', name: '旧名称', quantity: 1, unit: '件', revision: 'W1', complete: true }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text().includes('按编码引用料品'))!.trigger('click')
    await flushPromises()

    const dialog = wrapper.get('.pdm-material-reference-dialog')
    const pageSize = dialog.get('select[aria-label="料品引用每页条数"]')
    expect((pageSize.element as HTMLSelectElement).value).toBe('20')
    expect(pageSize.findAll('option').map(option => option.text())).toEqual(['20条/页', '50条/页', '100条/页'])
    expect(dialog.findAll('.pdm-material-reference-table tbody tr')).toHaveLength(20)
    expect(dialog.get('.pdm-material-reference-pagination').text()).toContain('1 / 3')

    await pageSize.setValue('50')
    expect(dialog.findAll('.pdm-material-reference-table tbody tr')).toHaveLength(50)
    expect(dialog.get('.pdm-material-reference-pagination').text()).toContain('1 / 2')
    await dialog.get('button[aria-label="料品引用下一页"]').trigger('click')
    expect(dialog.findAll('.pdm-material-reference-table tbody tr')).toHaveLength(10)
    expect(dialog.get('.pdm-material-reference-pagination').text()).toContain('2 / 2')

    await pageSize.setValue('100')
    expect(dialog.findAll('.pdm-material-reference-table tbody tr')).toHaveLength(60)
    expect(dialog.get('.pdm-material-reference-pagination').text()).toContain('1 / 1')
  })

  it('autofills material-master properties after an exact material-code edit', async () => {
    vi.spyOn(ElMessage, 'success').mockImplementation(() => undefined as never)
    materialApi.listMaterials.mockResolvedValue([{
      id: 'material-1', materialCode: '01020000001', name: '标准轴承', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
      specification: '6204', remark: '深沟球轴承', brand: 'SKF', material: 'GCr15', surfaceTreatment: '防锈', weight: 0.12,
      approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19T00:00:00Z',
      updatedBy: 'admin', updatedAt: '2026-08-19T00:00:00Z', rowVersion: 2, categoryCode: '0102', isArchived: false,
    }])
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: 'OLD-001', name: '旧名称', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="编辑物料编码"]').trigger('click')
    await wrapper.get('input[aria-label="内联编辑物料编码"]').setValue('01020000001')
    await wrapper.get('input[aria-label="内联编辑物料编码"]').trigger('keydown.enter')
    await flushPromises()

    expect(materialApi.listMaterials).toHaveBeenCalledWith('token', '01020000001', false, 500)
    expect(wrapper.emitted('batchUpdate')).toBeUndefined()
    expect(wrapper.get('.pdm-bom-unsaved-count').text()).toContain('未保存')
    await wrapper.get('.pdm-bom-save-action').trigger('click')
    const saved = (wrapper.emitted('save')?.[0]?.[1] as BomItem[])[0]
    expect(saved).toMatchObject({
      drawingNumber: '01020000001', name: '标准轴承', specification: '6204', remark: '深沟球轴承',
      brand: 'SKF', material: 'GCr15', surfaceTreatment: '防锈', weight: '0.12', unit: '001',
    })
  })

  it('autofills by model only when the approved material-master match is unique', async () => {
    vi.spyOn(ElMessage, 'success').mockImplementation(() => undefined as never)
    materialApi.listMaterials.mockResolvedValue([{
      id: 'material-1', materialCode: '01010000009', name: '接近开关', kind: 'Electrical', supplyMode: 'Purchase', unitCode: '001',
      specification: 'BES-M12', brand: 'BALLUFF', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19T00:00:00Z',
      updatedBy: 'admin', updatedAt: '2026-08-19T00:00:00Z', rowVersion: 2, categoryCode: '0101', isArchived: false,
    }])
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: 'OLD-001', name: '旧名称', specification: 'OLD-MODEL', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="编辑型号"]').trigger('click')
    await wrapper.get('input[aria-label="内联编辑型号"]').setValue('BES-M12')
    await wrapper.get('input[aria-label="内联编辑型号"]').trigger('keydown.enter')
    await flushPromises()

    expect(wrapper.emitted('batchUpdate')).toBeUndefined()
    await wrapper.get('.pdm-bom-save-action').trigger('click')
    expect((wrapper.emitted('save')?.[0]?.[1] as BomItem[])[0]).toMatchObject({
      drawingNumber: '01010000009', name: '接近开关', specification: 'BES-M12', brand: 'BALLUFF', unit: '001',
    })
  })

  it('keeps the manual model when multiple material masters match exactly', async () => {
    vi.spyOn(ElMessage, 'warning').mockImplementation(() => undefined as never)
    materialApi.listMaterials.mockResolvedValue([
      { id: 'material-1', materialCode: '01010000009', name: '接近开关A', kind: 'Electrical', supplyMode: 'Purchase', unitCode: '001', specification: 'BES-M12', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19', updatedBy: 'admin', updatedAt: '2026-08-19', rowVersion: 1, isArchived: false },
      { id: 'material-2', materialCode: '01010000010', name: '接近开关B', kind: 'Electrical', supplyMode: 'Purchase', unitCode: '001', specification: 'BES-M12', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19', updatedBy: 'admin', updatedAt: '2026-08-19', rowVersion: 1, isArchived: false },
    ])
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: 'OLD-001', name: '旧名称', specification: '', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="编辑型号"]').trigger('click')
    await wrapper.get('input[aria-label="内联编辑型号"]').setValue('BES-M12')
    await wrapper.get('input[aria-label="内联编辑型号"]').trigger('keydown.enter')
    await flushPromises()

    expect(wrapper.emitted('batchUpdate')).toBeUndefined()
    expect(wrapper.get('button[aria-label="编辑型号"]').text()).toBe('BES-M12')
    expect(ElMessage.warning).toHaveBeenCalledWith('型号“BES-M12”匹配到 2 个料品，请输入品牌后自动核对')
  })

  it('reuses an existing uncoded BOM row when a new row has the same model and brand', async () => {
    vi.spyOn(ElMessage, 'success').mockImplementation(() => undefined as never)
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-existing', sequence: 1, drawingNumber: '', name: '磁性开关', specification: 'D-A93L', brand: 'SMC', quantity: 1, unit: '001', revision: 'W1', complete: false }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    const quickModel = wrapper.get('input[aria-label="型号"]')
    await quickModel.setValue('D-A93L')
    await quickModel.trigger('blur')
    await flushPromises()

    expect(wrapper.findAll('input[aria-label="物料名称"]').at(0)!.element).toHaveProperty('value', '磁性开关')
    expect(wrapper.findAll('input[aria-label="品牌"]').at(0)!.element).toHaveProperty('value', 'SMC')
    expect(materialApi.listMaterials).not.toHaveBeenCalled()
    expect(ElMessage.success).toHaveBeenCalledWith('已自动关联同型号BOM物料，料号申请将共用一次')
  })

  it('autofills a new unsaved standard row when its model uniquely matches', async () => {
    vi.spyOn(ElMessage, 'success').mockImplementation(() => undefined as never)
    materialApi.listMaterials.mockResolvedValue([{
      id: 'material-1', materialCode: '01020000055', name: '阀岛', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
      specification: '10P-10-4A', brand: 'FESTO', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19',
      updatedBy: 'admin', updatedAt: '2026-08-19', rowVersion: 1, isArchived: false,
    }])
    const draft = { sequence: 1, drawingNumber: '', name: '', specification: '', brand: '', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Manual' as const }
    const wrapper = mount(BomManager, {
      props: { standard: [draft], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1' },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('input[aria-label="型号"]').setValue('10P-10-4A')
    await wrapper.get('input[aria-label="型号"]').trigger('blur')
    await flushPromises()

    expect((wrapper.get('input[aria-label="物料编码"]').element as HTMLInputElement).value).toBe('01020000055')
    expect((wrapper.get('input[aria-label="物料名称"]').element as HTMLInputElement).value).toBe('阀岛')
    expect((wrapper.get('input[aria-label="品牌"]').element as HTMLInputElement).value).toBe('FESTO')

    materialApi.listMaterials.mockResolvedValue([])
    await wrapper.get('input[aria-label="型号"]').setValue('OTHER-MODEL')
    await wrapper.get('input[aria-label="型号"]').trigger('blur')
    await flushPromises()
    expect((wrapper.get('input[aria-label="物料编码"]').element as HTMLInputElement).value).toBe('')
  })

  it('uses brand to disambiguate a new row after duplicate model matches', async () => {
    vi.spyOn(ElMessage, 'warning').mockImplementation(() => undefined as never)
    vi.spyOn(ElMessage, 'success').mockImplementation(() => undefined as never)
    const base = { kind: 'Standard' as const, supplyMode: 'Purchase' as const, unitCode: '001', specification: 'CP96', approvalStatus: 'Approved' as const, syncStatus: 'Succeeded' as const, createdBy: 'admin', createdAt: '2026-08-19', updatedBy: 'admin', updatedAt: '2026-08-19', rowVersion: 1, isArchived: false }
    materialApi.listMaterials.mockResolvedValue([
      { ...base, id: 'material-1', materialCode: '01020000001', name: 'SMC气缸', brand: 'SMC' },
      { ...base, id: 'material-2', materialCode: '01020000002', name: 'FESTO气缸', brand: 'FESTO' },
    ])
    const draft = { sequence: 1, drawingNumber: '', name: '', specification: '', brand: '', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Manual' as const }
    const wrapper = mount(BomManager, {
      props: { standard: [draft], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1' },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('input[aria-label="型号"]').setValue('CP96')
    await wrapper.get('input[aria-label="型号"]').trigger('blur')
    await flushPromises()
    expect((wrapper.get('input[aria-label="物料编码"]').element as HTMLInputElement).value).toBe('')

    await wrapper.get('input[aria-label="品牌"]').setValue('FESTO')
    await wrapper.get('input[aria-label="品牌"]').trigger('blur')
    await flushPromises()
    expect((wrapper.get('input[aria-label="物料编码"]').element as HTMLInputElement).value).toBe('01020000002')
    expect((wrapper.get('input[aria-label="物料名称"]').element as HTMLInputElement).value).toBe('FESTO气缸')
  })

  it('keeps the manual code when the material master has no exact match', async () => {
    vi.spyOn(ElMessage, 'warning').mockImplementation(() => undefined as never)
    materialApi.listMaterials.mockResolvedValue([])
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'bom-1', sequence: 1, drawingNumber: 'OLD-001', name: '旧名称', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('button[aria-label="编辑物料编码"]').trigger('click')
    await wrapper.get('input[aria-label="内联编辑物料编码"]').setValue('MANUAL-001')
    await wrapper.get('input[aria-label="内联编辑物料编码"]').trigger('keydown.enter')
    await flushPromises()

    expect(wrapper.emitted('batchUpdate')).toBeUndefined()
    expect(wrapper.get('button[aria-label="编辑物料编码"]').text()).toBe('MANUAL-001')
    expect(ElMessage.warning).toHaveBeenCalledWith('料品主档中未找到物料编码“MANUAL-001”，已保留手工输入')
  })

  it('derives data status automatically and does not require material for electrical items', async () => {
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'standard', sequence: 1, drawingNumber: 'S-001', name: '标准件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' }],
        nonStandard: [{ id: 'custom', sequence: 1, drawingNumber: 'N-001', name: '非标件', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' }],
        electrical: [{ id: 'electrical', sequence: 1, drawingNumber: 'E-001', name: '电气件', quantity: 1, unit: '件', revision: 'W1', complete: false, source: 'Manual' }],
        declarations: [], pending: false, editable: true,
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('缺少型号')
    await wrapper.findAll('button[role="tab"]')[2].trigger('click')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('缺少材质')
    await wrapper.findAll('button[role="tab"]')[3].trigger('click')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('已完善')
    expect(wrapper.find('input[aria-label="物料完整"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('完整状态')
  })

  it('validates drawing material codes against model and brand without rewriting the code', async () => {
    materialApi.resolveBomMaterialCodes.mockResolvedValue([{
      bomItemId: 'standard-drawing', status: 'ValidationFailed', material: null, candidates: [], application: null,
      issues: ['型号与料品主档不一致', '料品主档品牌缺失'],
    }])
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'standard-drawing', sequence: 1, drawingNumber: '01020000512', name: '气缸', specification: 'DFM-10', brand: 'FESTO', quantity: 1, unit: '001', revision: 'W1', complete: true, source: 'Auto', sourceDocumentId: 'document-1' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await flushPromises()

    expect(materialApi.resolveBomMaterialCodes).toHaveBeenCalledWith('project-1', ['standard-drawing'], 'token')
    expect(wrapper.get('button[aria-label="编辑物料编码"]').text()).toBe('01020000512')
    expect(wrapper.get('.pdm-bom-data-status').text()).toContain('待人工维护')
    expect(wrapper.get('.pdm-bom-data-status').attributes('title')).toContain('型号与料品主档不一致')
    expect(wrapper.emitted('materialCodeChanged')).toBeUndefined()
  })

  it('hides material-code application when any summarized instance is already pending approval', async () => {
    const standardRows: BomItem[] = [
      { id: 'standard-a', sequence: 1, drawingNumber: '', name: '接头', specification: 'PTL6', brand: 'AIRTAC', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1' },
      { id: 'standard-b', sequence: 2, drawingNumber: '', name: '接头', specification: 'PTL6', brand: 'AIRTAC', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Auto', sourceDocumentId: 'document-1' },
    ]
    materialApi.resolveBomMaterialCodes
      .mockResolvedValueOnce(standardRows.map(row => ({ bomItemId: row.id!, status: 'NoMatch', material: null, candidates: [], application: null, issues: [] })))
      .mockResolvedValueOnce([
        { bomItemId: 'standard-a', status: 'NoMatch', material: null, candidates: [], application: null, issues: [] },
        { bomItemId: 'standard-b', status: 'ApplicationPending', material: null, candidates: [], application: { id: 'application-1' }, issues: [] },
      ])
    const wrapper = mount(BomManager, {
      props: {
        standard: standardRows, nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
        token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await flushPromises()
    expect(wrapper.get('.pdm-material-code-action').text()).toBe('申请料号')

    await wrapper.setProps({ standard: standardRows.map(row => ({ ...row, brand: 'AIRTAC-UPDATED', reconciliationNote: 'server-refreshed' })) })
    await flushPromises()

    expect(materialApi.resolveBomMaterialCodes).toHaveBeenLastCalledWith('project-1', ['standard-a', 'standard-b'], 'token')
    expect(wrapper.get('.pdm-material-code-state.is-pending').text()).toBe('申请中')
    expect(wrapper.get('.pdm-bom-table tbody tr').classes()).toContain('is-data-exception')
    expect(wrapper.find('.pdm-material-code-action').exists()).toBe(false)
    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    const applyButton = wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '申请料号')
    expect(applyButton?.attributes('disabled')).toBeDefined()
  })

  it('blocks standard material-code applications until name, model, and brand are complete', async () => {
    const error = vi.spyOn(ElMessage, 'error').mockImplementation(() => undefined as never)
    const wrapper = mount(BomManager, {
      props: {
        standard: [{ id: 'standard-incomplete', sequence: 3, drawingNumber: '', name: '', specification: '', brand: '', quantity: 1, unit: '001', revision: 'W1', complete: false, source: 'Manual' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, token: 'token', projectId: 'project-1',
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    const applyButton = wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '申请料号')
    expect(applyButton).toBeDefined()
    await applyButton!.trigger('click')

    expect(materialApi.applyForBomMaterialCodes).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalledWith('标准件料号申请前必须补全物料名称、型号、品牌；第 3 行缺少物料名称、型号、品牌')
  })

  it('uses configured rules for the working BOM and the stored rule snapshot for history', async () => {
    const item: BomItem = { id: 'standard', sequence: 1, drawingNumber: 'S-001', name: '标准件', specification: 'M1', quantity: 1, unit: '件', revision: 'W1', complete: true, source: 'Manual' }
    const wrapper = mount(BomManager, {
      props: {
        standard: [item], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
        validationRules: {
          standard: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'],
          nonStandard: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'],
          electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'],
        },
        versions: [{
          id: 'standard-released', projectId: 'project', kind: 'Standard', versionNumber: 1, label: 'S-B01', state: 'Released',
          validationRequiredFields: ['drawingNumber', 'name', 'unit', 'quantity', 'revision', 'brand'],
          items: [item], createdBy: 'admin', createdAt: '2026-08-18', updatedBy: 'admin', updatedAt: '2026-08-18', releasedAt: '2026-08-18',
        }],
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('已完善')
    expect((wrapper.get('select[aria-label="选择BOM版本"]').element as HTMLSelectElement).value).toBe('current')
    await wrapper.get('select[aria-label="选择BOM版本"]').setValue('standard-released')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('缺少品牌')
    expect(wrapper.text()).not.toContain('默认显示PLM最新正式发布版')
  })

  it('shows independent BOM history as read-only and displays the three-version manufacturing baseline', async () => {
    const releasedItem: BomItem = { id: 'released-item', sequence: 1, drawingNumber: 'S-001', name: '旧标准件', specification: 'M1', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' }
    const currentItem: BomItem = { ...releasedItem, id: 'current-item', name: '新标准件', quantity: 2 }
    const wrapper = mount(BomManager, {
      props: {
        standard: [currentItem], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
        versions: [
          { id: 'standard-draft', projectId: 'project', kind: 'Standard', versionNumber: 2, label: 'S-B02', state: 'Draft', baseVersionId: 'standard-released', items: [currentItem], createdBy: 'admin', createdAt: '2026-08-19', updatedBy: 'admin', updatedAt: '2026-08-19' },
          { id: 'standard-released', projectId: 'project', kind: 'Standard', versionNumber: 1, label: 'S-B01', state: 'Released', changeNumber: 'ECN-001', items: [releasedItem], createdBy: 'admin', createdAt: '2026-08-18', updatedBy: 'admin', updatedAt: '2026-08-18', releasedAt: '2026-08-18' },
          { id: 'nonstandard-released', projectId: 'project', kind: 'NonStandard', versionNumber: 1, label: 'N-B01', state: 'Released', items: [], createdBy: 'admin', createdAt: '2026-08-18', updatedBy: 'admin', updatedAt: '2026-08-18' },
          { id: 'electrical-released', projectId: 'project', kind: 'Electrical', versionNumber: 1, label: 'E-B01', state: 'Released', items: [], createdBy: 'admin', createdAt: '2026-08-18', updatedBy: 'admin', updatedAt: '2026-08-18' },
        ],
        baselines: [{ id: 'baseline', projectId: 'project', sequence: 1, label: 'BL-001', standardBomVersionId: 'standard-released', nonStandardBomVersionId: 'nonstandard-released', electricalBomVersionId: 'electrical-released', changeNumber: 'ECN-001', changeReason: '首次发布', effectiveSerialFrom: '70000001', releasePackageId: 'release', createdBy: 'admin', createdAt: '2026-08-18' }],
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('select[aria-label="选择BOM版本"]').text()).toContain('S-V02 · 工作中')
    expect((wrapper.get('select[aria-label="选择BOM版本"]').element as HTMLSelectElement).value).toBe('current')
    expect(wrapper.get('.pdm-bom-baseline-picker').text()).toContain('S S-V01 · N N-V01 · E E-V01')
    expect(wrapper.get('.pdm-bom-baseline-picker').text()).not.toContain('序列号')
    expect(wrapper.get('.pdm-bom-baseline-picker').text()).not.toContain('70000001')
    expect(wrapper.get('.pdm-bom-selection-toolbar').element.lastElementChild?.classList).toContain('pdm-bom-version-picker')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('修改 1')

    await wrapper.get('select[aria-label="选择BOM版本"]').setValue('standard-draft')
    expect(wrapper.get('.pdm-bom-version-state').text()).toBe('工作中 · 只读')
    expect(wrapper.find('.pdm-bom-selection-toolbar').exists()).toBe(true)
    expect(wrapper.find('.pdm-bom-selection-toolbar > button').exists()).toBe(false)
    expect(wrapper.find('.pdm-bom-version-picker').exists()).toBe(true)
    expect(wrapper.find('button[aria-label="编辑物料名称"]').exists()).toBe(false)
    await wrapper.findAll('.pdm-bom-version-picker button').at(-1)!.trigger('click')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('修改 1')
  })

  it('defaults the workspace to the latest released comparison and identifies quantity changes', async () => {
    const releasedRows: BomItem[] = [
      { id: 'part-a', sequence: 1, drawingNumber: 'STD-A', name: '数量变化件', quantity: 4, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
      { id: 'part-b', sequence: 2, drawingNumber: 'STD-B', name: '已移除件', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
      { id: 'part-d', sequence: 3, drawingNumber: 'STD-D', name: '发布未变化件', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
    ]
    const currentRows: BomItem[] = [
      { ...releasedRows[0], quantity: 6 },
      { id: 'part-c', sequence: 2, drawingNumber: 'STD-C', name: '新增件', quantity: 2, unit: '个', revision: 'W1', complete: true, source: 'Manual' },
      { ...releasedRows[2], sequence: 3 },
    ]
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(BomManager, {
      props: {
        standard: currentRows, nonStandard: [], electrical: [], declarations: [], pending: false, editable: true,
        versions: [{
          id: 'standard-released', projectId: 'project', kind: 'Standard', versionNumber: 1, label: 'S-B01', state: 'Released',
          items: releasedRows, createdBy: 'admin', createdAt: '2026-08-18', updatedBy: 'admin', updatedAt: '2026-08-18', releasedAt: '2026-08-18',
        }],
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect((wrapper.get('select[aria-label="选择BOM版本"]').element as HTMLSelectElement).value).toBe('current')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('当前工作区 对比最近发布版 S-V01')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('已发布 1')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('新增 1')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('修改 1')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('已移除 1')

    const currentTableRows = wrapper.findAll('.pdm-bom-table tbody tr').filter(row => !row.classes().includes('is-quick-entry'))
    const modifiedRow = currentTableRows.find(row => row.text().includes('STD-A'))!
    const addedRow = currentTableRows.find(row => row.text().includes('STD-C'))!
    const releasedRow = currentTableRows.find(row => row.text().includes('STD-D'))!
    expect(modifiedRow.classes()).toContain('is-release-modified')
    expect(modifiedRow.get('.pdm-bom-quantity-audit').text()).toBe('4 → 6')
    expect(modifiedRow.attributes('title')).toContain('数量 4 → 6')
    expect(addedRow.classes()).toContain('is-release-added')
    expect(releasedRow.classes()).toContain('is-release-unchanged')

    await wrapper.findAll('.pdm-bom-comparison-filters button').find(button => button.text() === '修改 1')!.trigger('click')
    expect(wrapper.findAll('.pdm-bom-table tbody tr').filter(row => !row.classes().includes('is-quick-entry'))).toHaveLength(1)
    await wrapper.findAll('.pdm-bom-comparison-filters button').find(button => button.text() === '全部 3')!.trigger('click')
    await wrapper.findAll('.pdm-bom-table tbody tr').find(row => row.text().includes('STD-D'))!.get('button[aria-label="编辑物料名称"]').trigger('click')
    await flushPromises()
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('不会改动已发布版本'), '已发布基线物料', expect.any(Object))
    expect(wrapper.find('input[aria-label="内联编辑物料名称"]').exists()).toBe(true)
  })

  it('uses the latest published package snapshot when a long-lead release has no released BOM version', async () => {
    const publishedItem: BomItem = { id: 'long-lead-item', sequence: 1, drawingNumber: 'STD-L', name: '长交期件', quantity: 2, unit: '个', revision: 'W2', complete: true, source: 'Manual' }
    const releasePackage: ReleasePackageSummary = {
      id: 'long-lead-release', number: 'RP-LONG-001', state: '已发布', steps: [], scope: 'StandardLongLead',
      workflowVersion: 1, selectedBomItemIds: ['long-lead-item'], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [publishedItem], nonStandardBomSnapshot: [], electricalBomSnapshot: [], publishedAt: '2026-09-02T20:00:00Z',
    }
    const wrapper = mount(BomManager, {
      props: {
        standard: [publishedItem, { id: 'new-item', sequence: 2, drawingNumber: 'STD-N', name: '新物料', quantity: 1, unit: '个', revision: 'W1', complete: true, source: 'Manual' }],
        nonStandard: [], electrical: [], declarations: [], pending: false, editable: true, releasePackages: [releasePackage], versions: [],
      },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('当前工作区 对比最近发布版 RP-LONG-001')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('已发布 1')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('新增 1')
    expect(wrapper.findAll('tr.is-release-unchanged')).toHaveLength(1)
    expect(wrapper.findAll('tr.is-release-added')).toHaveLength(1)
  })
})
