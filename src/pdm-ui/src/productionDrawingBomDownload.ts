import { getProjectExportMetadata, listProductionDrawings, PdmApiError } from './api'
import { createProductionBomWorkbook } from './productionBomWorkbook'
import type { BomItem, DrawingPriority, ProductionDrawingBomQuantity, ProductionDrawingItem } from './types'
import { u9UnitName } from './u9Units'

export type ColumnKey = 'kind' | 'wearPart' | 'impact' | 'unit' | 'drawingNumber' | 'name' | 'parentDrawingNumber' | 'specification' | 'revision' | 'remark' | 'brand' | 'material' | 'surfaceTreatment' | 'heatTreatment' | 'weight' | 'quantity' | 'quantityReference' | 'priorReleasedQuantity' | 'netIssueQuantity' | 'drawing' | 'issue' | 'dataStatus' | 'priority' | 'requiredOn' | 'deliveryStatus'
export const columnDefinitions: { key: ColumnKey; label: string }[] = [
  { key: 'kind', label: '物料分类' }, { key: 'wearPart', label: '易损件' }, { key: 'impact', label: '关键' }, { key: 'unit', label: '单位' },
  { key: 'drawingNumber', label: '物料编码' }, { key: 'name', label: '名称' }, { key: 'parentDrawingNumber', label: '上级物料编码' }, { key: 'specification', label: '型号' }, { key: 'revision', label: '版本' },
  { key: 'remark', label: '备注' }, { key: 'brand', label: '品牌' }, { key: 'material', label: '材质' }, { key: 'surfaceTreatment', label: '表面处理' }, { key: 'heatTreatment', label: '热处理' },
  { key: 'weight', label: '重量' }, { key: 'quantityReference', label: '单套数量 × 套数' }, { key: 'quantity', label: '发布总量' }, { key: 'priorReleasedQuantity', label: '已提前发布' }, { key: 'netIssueQuantity', label: '本次新增下发' }, { key: 'drawing', label: '图纸' },
  { key: 'issue', label: '问题' }, { key: 'dataStatus', label: '资料状态' }, { key: 'priority', label: '紧急程度' }, { key: 'requiredOn', label: '需求货期' }, { key: 'deliveryStatus', label: '交付状态' },
]

const bomItems = (item: ProductionDrawingItem) => item.bomItems ?? []
function joinedBomValue(item: ProductionDrawingItem, key: keyof BomItem) {
  const values = bomItems(item).map(bom => bom[key]).filter(value => value !== null && value !== undefined && String(value).trim() !== '')
  return [...new Set(values.map(String))].join('、')
}
function quantityValue(item: ProductionDrawingItem, key: keyof Pick<ProductionDrawingBomQuantity, 'totalQuantity' | 'priorReleasedQuantity' | 'netIssueQuantity'> | 'reference' = 'totalQuantity') {
  const quantities = item.bomQuantities ?? []
  return quantities.map(quantity => {
    const bom = bomItems(item).find(value => value.id && quantity.sourceBomItemIds.includes(value.id))
    const value = key === 'reference' ? `${quantity.singleSetQuantity} × ${quantity.wholeSetMultiplier}` : String(quantity[key])
    return `${quantities.length > 1 ? `${bom?.drawingNumber ?? quantity.materialKey}：` : ''}${value} ${u9UnitName(bom?.unit ?? quantity.materialKey.split('|').at(-1) ?? '')}`
  }).join('；') || '—'
}
export function columnValue(item: ProductionDrawingItem, key: ColumnKey): string {
  switch (key) {
    case 'kind': return bomItems(item).length ? '非标件' : '—'
    case 'wearPart': return bomItems(item).length ? [...new Set(bomItems(item).map(bom => bom.isWearPart ? '是' : '否'))].join('、') : '—'
    case 'impact': return joinedBomValue(item, 'impactStage').replaceAll('Assembly', '装配').replaceAll('Commissioning', '调试') || '—'
    case 'unit': return bomItems(item).length ? [...new Set(bomItems(item).map(bom => u9UnitName(bom.unit)))].join('、') : '—'
    case 'drawingNumber': return joinedBomValue(item, 'drawingNumber') || item.drawingNumber
    case 'name': return joinedBomValue(item, 'name') || item.name
    case 'specification': return joinedBomValue(item, 'specification') || item.model
    case 'revision': return joinedBomValue(item, 'revision') || item.revision
    case 'quantity': return quantityValue(item)
    case 'quantityReference': return quantityValue(item, 'reference')
    case 'priorReleasedQuantity': return quantityValue(item, 'priorReleasedQuantity')
    case 'netIssueQuantity': return quantityValue(item, 'netIssueQuantity')
    case 'issue': return bomItems(item).some(bom => bom.manualUnmatched || bom.pendingRemoval || bom.releaseExcluded) ? '需核对' : '—'
    case 'dataStatus': return bomItems(item).length ? bomItems(item).every(bom => bom.complete) ? '齐全' : '待完善' : '—'
    case 'priority': return priorityLabel(item.priority)
    case 'requiredOn': return item.requiredOn || '—'
    case 'deliveryStatus': return item.pdfReady ? '可拿图' : '待转换'
    case 'drawing': return ''
    default: return joinedBomValue(item, key) || '—'
  }
}
export const priorityLabel = (value: DrawingPriority) => value === 'Urgent' ? '紧急' : value === 'Priority' ? '优先' : '普通'

export async function createProductionDrawingBomDownload(items: ProductionDrawingItem[], token: string, exportProjectId: string, frozenPackage: { id: string; number: string }, selectedOnly: boolean) {
  const metadata = await getProjectExportMetadata(exportProjectId, token, frozenPackage.id)
  // BOM 也属于生产发放资料；不能从长期停留页面导出已经作废的图纸配套数据。
  const available = await listProductionDrawings(token, true, exportProjectId)
  items = items.map(item => {
    const latest = available.find(value => value.versionId === item.versionId && value.releasePackageId === frozenPackage.id)
    if (!latest) throw new PdmApiError('所选图纸已作废、已删除或无法访问，不能导出生产BOM，请刷新列表。', 409)
    if (!latest.bomQuantities?.length) throw new PdmApiError('发布包缺少冻结数量，不能导出生产BOM，请刷新列表。', 409)
    return latest
  })
  const columns = columnDefinitions.filter(column => !['drawing', 'quantityReference', 'issue', 'dataStatus'].includes(column.key))
  const headers = ['发布包', '关联图号', ...columns.map(column => column.label), '单套数量', '整套数量']
  const materials = new Map<string, { item: ProductionDrawingItem; quantity: ProductionDrawingBomQuantity; drawingNumbers: Set<string> }>()
  for (const item of items) for (const quantity of item.bomQuantities ?? []) {
    const key = `${item.releasePackageId}|${quantity.materialKey}`
    const existing = materials.get(key)
    if (existing) { existing.drawingNumbers.add(item.drawingNumber); continue }
    const bom = bomItems(item).find(value => value.id && quantity.sourceBomItemIds.includes(value.id))
    if (!bom) throw new Error('发布包数量与物料数据不完整，请刷新后重试。')
    materials.set(key, { item: { ...item, bomItems: [{ ...bom, parentDrawingNumber: undefined }], bomQuantities: [quantity] }, quantity, drawingNumbers: new Set([item.drawingNumber]) })
  }
  const values = [...materials.values()].map(({ item, quantity, drawingNumbers }) => [item.releasePackageNumber, [...drawingNumbers].join('、'), ...columns.map(column =>
    column.key === 'quantity' ? quantity.totalQuantity
      : column.key === 'priorReleasedQuantity' ? quantity.priorReleasedQuantity
        : column.key === 'netIssueQuantity' ? quantity.netIssueQuantity : columnValue(item, column.key)), quantity.singleSetQuantity, quantity.wholeSetMultiplier])
  const fileName = `${frozenPackage.number}-${selectedOnly ? '选中' : '本包'}BOM.xlsx`.replace(/[<>:"/\\|?*]/g, '_')
  return { blob: createProductionBomWorkbook(headers, values, { ...metadata, title: '发布包BOM清单', scope: selectedOnly ? `选中条目 · ${items.length}条` : '完整发布包', versionLabel: '发布包版本', version: frozenPackage.number }), fileName }
}
