import { flushPromises, mount } from '@vue/test-utils'
import { ElMessage, ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import BomManager from '../src/components/BomManager.vue'
import type { BomItem } from '../src/types'

const materialApi = vi.hoisted(() => ({
  listMaterials: vi.fn(),
  linkBomMaterial: vi.fn(),
}))

vi.mock('../src/api', () => materialApi)

describe('BomManager', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    materialApi.listMaterials.mockReset()
    materialApi.linkBomMaterial.mockReset()
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
    expect(toolbar.get('.pdm-manager-actions').text()).not.toContain('重新对账')
    expect(wrapper.find('.pdm-bom-selection-summary').exists()).toBe(false)
    expect(wrapper.get('.pdm-bom-selection-actions').classes()).toContain('pdm-bom-selection-actions')
    expect(wrapper.findAll('.pdm-bom-selection-actions button').map(button => button.text())).toEqual(['归入标准件BOM', '归入非标件BOM', '回收站（0）'])

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.find('.pdm-bom-empty-declaration').exists()).toBe(false)
    expect(wrapper.get('.pdm-empty-info').text()).toContain('系统自动按无此类物料处理')
  })

  it('shows the complete material property columns in the required order', async () => {
    const wrapper = mount(BomManager, {
      props: { standard: [], nonStandard: [], electrical: [], declarations: [], pending: false, editable: true },
    })

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')

    expect(wrapper.findAll('thead th').map(header => header.text())).toEqual([
      '', '', '序号', '物料分类', '单位', '物料编码', '物料名称', '型号', '备注信息', '品牌', '材质', '表面处理', '重量', '数量', '版本', '对账状态/说明', '资料状态',
    ])
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

  it('confirms and emits one batch-delete operation for selected categorized rows', async () => {
    const prompt = vi.spyOn(ElMessageBox, 'prompt').mockResolvedValue({ value: '误删测试', action: 'confirm' } as never)
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
    expect(wrapper.emitted('batchDelete')).toEqual([[['bom-1', 'bom-2'], '误删测试']])
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
    expect(wrapper.find('.pdm-bom-reconcile-alert').exists()).toBe(false)
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
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '归入标准件BOM')!.trigger('click')

    expect(wrapper.emitted('batchUpdate')).toEqual([[{
      itemIds: ['bom-pending'], fields: ['kind'], targetKind: 'Standard',
    }]])
    expect(wrapper.get('.pdm-source-data-tab').text()).toBe('源数据（1）')
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
    expect(wrapper.findAll('thead th').map(header => header.text())).toEqual(expect.arrayContaining(['对账状态/说明', '资料状态']))
    expect(wrapper.get('.pdm-bom-source').text()).toBe('与源数据不一致')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('已完善')
    expect(wrapper.get('.pdm-bom-classification-indicator').classes()).toContain('is-classified')
    expect(wrapper.get('.pdm-bom-classification-indicator').attributes('aria-label')).toBe('已归类')

    await wrapper.findAll('button[role="tab"]')[1].trigger('click')
    expect(wrapper.text()).toContain('BOM-001')
    expect(wrapper.text()).toContain('维护后名称')
    expect(wrapper.find('.pdm-bom-classification-indicator').exists()).toBe(false)
    expect(wrapper.find('tbody .pdm-bom-row-actions').exists()).toBe(true)
    expect(wrapper.get('.pdm-bom-source').text()).toBe('与源数据不一致')
    expect(wrapper.get('.pdm-bom-reconciliation').attributes('title')).toContain('物料编码、物料名称、型号')
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
    expect(wrapper.get('.pdm-bom-source').text()).toBe('分类已变更')
    expect(wrapper.get('.pdm-bom-reconciliation').attributes('title')).toBe('图档源数据分类由非标件变更为标准件，已自动迁移。')
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
    await wrapper.findAll('input[aria-label="选择物料"]')[0].setValue(true)
    const restore = wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '恢复源数据')!
    expect(restore.attributes('disabled')).toBeUndefined()
    await restore.trigger('click')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('分类与当前排序不会改变'), '恢复源数据', expect.any(Object))
    expect(wrapper.emitted('restoreSource')).toEqual([[['bom-source']]])
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
    expect(rows[0].findAll('td').filter(cell => cell.classes().some(name => name.endsWith('-cell'))).map(cell => cell.classes())).toEqual([
      ['pdm-bom-reconciliation-cell'],
      ['pdm-bom-data-status-cell', 'is-complete'],
    ])
    expect(rows[0].get('.pdm-bom-data-status').classes()).toContain('is-complete')
    expect(rows[1].classes()).not.toContain('is-bom-classification-mismatch')
    expect(rows[1].get('.pdm-bom-source').text()).toBe('已人工分类')
  })

  it('keeps source rows read-only and exposes only standard or non-standard classification', async () => {
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
    expect(wrapper.findAll('.pdm-bom-selection-actions button').map(button => button.text())).toEqual(['归入标准件BOM', '归入非标件BOM', '回收站（0）'])
    expect(wrapper.emitted('batchDelete')).toBeUndefined()
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
      '编辑物料分类', '编辑物料编码', '编辑物料名称', '编辑型号', '编辑备注信息', '编辑品牌', '编辑材质', '编辑表面处理', '编辑数量',
    ])

    await wrapper.get('button[aria-label="编辑物料名称"]').trigger('click')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    const nameField = wrapper.get('input[aria-label="内联编辑物料名称"]')
    expect((nameField.element as HTMLInputElement).value).toBe('轴承')
    await nameField.setValue('新轴承')
    await nameField.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('batchUpdate')?.[0]).toEqual([{
      itemIds: ['bom-edit'], fields: ['name'], name: '新轴承',
    }])

    await wrapper.get('button[aria-label="编辑物料分类"]').trigger('click')
    await wrapper.get('select[aria-label="内联编辑物料分类"]').setValue('NonStandard')
    expect(wrapper.emitted('batchUpdate')?.[1]).toEqual([{
      itemIds: ['bom-edit'], fields: ['kind'], targetKind: 'NonStandard',
    }])

    await wrapper.get('input[aria-label="选择物料"]').setValue(true)
    expect((wrapper.get('input[aria-label="选择物料"]').element as HTMLInputElement).checked).toBe(true)
    await wrapper.findAll('.pdm-bom-selection-toolbar button').find(button => button.text() === '编辑')!.trigger('click')
    expect(wrapper.get('[role="dialog"]').isVisible()).toBe(true)
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
    await wrapper.findAll('.pdm-bom-selection-actions button').find(button => button.text() === '归入非标件BOM')!.trigger('click')

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
      id: 'material-1', materialCode: '01020000001', name: '标准轴承', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
      specification: '6204', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-19T00:00:00Z',
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
    await wrapper.findAll('button').find(button => button.text() === '引用')!.trigger('click')
    await flushPromises()

    expect(materialApi.linkBomMaterial).toHaveBeenCalledWith('project-1', 'bom-1', 'material-1', 'token')
    expect(wrapper.emitted('save')?.[0]?.[1]).toEqual(expect.arrayContaining([expect.objectContaining({ drawingNumber: '01020000001', name: '标准轴承' })]))
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
    expect(wrapper.emitted('batchUpdate')?.[0]?.[0]).toEqual({
      itemIds: ['bom-1'],
      fields: ['drawingNumber', 'kind', 'unit', 'name', 'specification', 'remark', 'brand', 'material', 'surfaceTreatment', 'weight'],
      targetKind: 'Standard', unit: '001', drawingNumber: '01020000001', name: '标准轴承', specification: '6204',
      remark: '深沟球轴承', brand: 'SKF', material: 'GCr15', surfaceTreatment: '防锈', weight: '0.12',
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

    expect(wrapper.emitted('batchUpdate')?.[0]?.[0]).toEqual(expect.objectContaining({
      itemIds: ['bom-1'], drawingNumber: '01010000009', name: '接近开关', specification: 'BES-M12', brand: 'BALLUFF', unit: '001',
      fields: expect.arrayContaining(['drawingNumber', 'name', 'specification', 'brand', 'unit']),
    }))
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

    expect(wrapper.emitted('batchUpdate')?.[0]?.[0]).toEqual({ itemIds: ['bom-1'], fields: ['specification'], specification: 'BES-M12' })
    expect(ElMessage.warning).toHaveBeenCalledWith('型号“BES-M12”匹配到 2 个料品，未自动回填其他信息')
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

    expect(wrapper.emitted('batchUpdate')?.[0]?.[0]).toEqual({ itemIds: ['bom-1'], fields: ['drawingNumber'], drawingNumber: 'MANUAL-001' })
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
    await wrapper.get('select[aria-label="选择BOM版本"]').setValue('standard-released')
    expect(wrapper.get('.pdm-bom-data-status').text()).toBe('缺少品牌')
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
    expect(wrapper.get('select[aria-label="选择BOM版本"]').text()).toContain('S-B02 · 工作中')
    expect(wrapper.get('.pdm-bom-baseline-picker').text()).toContain('S S-B01 · N N-B01 · E E-B01 · 序列号 70000001起')
    expect(wrapper.get('.pdm-bom-selection-toolbar').element.lastElementChild?.classList).toContain('pdm-bom-version-picker')

    await wrapper.get('select[aria-label="选择BOM版本"]').setValue('standard-draft')
    expect(wrapper.get('.pdm-bom-version-state').text()).toBe('工作中 · 只读')
    expect(wrapper.find('.pdm-bom-selection-toolbar').exists()).toBe(true)
    expect(wrapper.find('.pdm-bom-selection-toolbar > button').exists()).toBe(false)
    expect(wrapper.find('.pdm-bom-version-picker').exists()).toBe(true)
    expect(wrapper.find('button[aria-label="编辑物料名称"]').exists()).toBe(false)
    await wrapper.findAll('.pdm-bom-version-picker button').at(-1)!.trigger('click')
    expect(wrapper.get('.pdm-bom-comparison-summary').text()).toContain('修改 1')
  })
})
