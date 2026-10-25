import type { ProductionDrawingItem } from './types'
import type { ProductionLine, ProductionReleaseState } from './productionTracking'

export function drawingsForProductionLine(items: ProductionDrawingItem[], state: ProductionReleaseState, line: ProductionLine) {
  const packageId = state.formalReleasePackageId || state.releasePackageId
  return items.filter(item => item.projectId === state.projectId && item.releasePackageId === packageId && (
    item.bomQuantities?.some(quantity => quantity.materialKey === line.materialKey)
    || item.bomItems?.some(bom => bom.id && line.sourceBomItemIds?.includes(bom.id))
    || item.documentId === line.sourceDocumentId && (!line.sourceVersionId || item.versionId === line.sourceVersionId)))
}
