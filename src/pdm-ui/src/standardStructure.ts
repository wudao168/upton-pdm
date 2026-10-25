export interface StructureCategory { id: string; name: string; parentId: string | null; sortOrder: number; isActive: boolean }
export interface StructureNode {
  id: string; drawingNumber: string; name: string; fileName: string; kind: string
  fileKey: string | null; previewFileKey: string | null; embedded: boolean; quantity: number; children: StructureNode[]
}
export interface StructureBomLine {
  id: string; kind: 'Standard' | 'NonStandard' | 'Unclassified'; drawingNumber: string; name: string; quantity: number
  unit: string; revision: string; material?: string; specification?: string; brand?: string
  surfaceTreatment?: string; heatTreatment?: string; remark?: string
}
export interface StructureRevision {
  id: string; version: number; archivedAt: string; archivedBy: string; root: StructureNode
  files: Array<{ key: string; fileName: string; length: number; sha256: string }>; bom: StructureBomLine[]
}
export interface StandardStructure { id: string; categoryId: string; code: string; name: string; revisions: StructureRevision[]; drawingType?: number; isActive?: boolean; deletedAt?: string | null }
export interface StructureReference { projectId: string; projectCode?: string; revisionId: string; version: number; kind: 'Standard' | 'NonStandard'; quantity: number; requestId: string; referencedAt: string; actor: string; pending: boolean }
export function structureCategoryActive(categories: StructureCategory[], id: string): boolean {
  const visited = new Set<string>()
  let current = categories.find(category => category.id === id)
  if (!current) return false
  while (current) {
    if (!current.isActive || visited.has(current.id)) return false
    visited.add(current.id)
    if (!current.parentId) return true
    current = categories.find(category => category.id === current!.parentId)
    if (!current) return false
  }
  return true
}
export const unclassifiedStructureCategoryId = '55505354-0000-0000-0000-000000000002'
export const structureVersion = (version: number) => `V${String(version).padStart(2, '0')}`
export const structureTypeName = (type?: number) => ({ 1: '标准组件', 2: '标准零件', 3: '组件模板', 4: '零件模板', 9: '其他图档' })[type as 1 | 2 | 3 | 4 | 9] ?? '其他图档'
export interface StructureCatalog { companyId: string; categories: StructureCategory[]; structures: StandardStructure[]; storageRoot: string; rowVersion: number }
export interface StructureView { catalog: StructureCatalog; canManage: boolean; canArchive: boolean; canConfigureStorage: boolean }
export type StructureCategoryNode = StructureCategory & { children: StructureCategoryNode[] }

export function structureCategoryTree(categories: StructureCategory[]): StructureCategoryNode[] {
  const nodes = new Map(categories.map(c => [c.id, { ...c, children: [] } as StructureCategoryNode]))
  const roots: StructureCategoryNode[] = []
  for (const node of nodes.values()) {
    const parent = node.parentId ? nodes.get(node.parentId) : undefined
    if (parent) parent.children.push(node)
    else roots.push(node)
  }
  const sort = (items: StructureCategoryNode[]) => { items.sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'zh-CN')); items.forEach(item => sort(item.children)) }
  sort(roots)
  return roots
}
export function structureCategoryScope(categories: StructureCategory[], id: string) {
  const result = new Set([id])
  let changed = true
  while (changed) {
    changed = false
    for (const c of categories) if (c.parentId && result.has(c.parentId) && !result.has(c.id)) { result.add(c.id); changed = true }
  }
  return result
}
