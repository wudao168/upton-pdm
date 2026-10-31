import type { StandardLibraryCategory } from './types'

export const isUncategorized = (category: StandardLibraryCategory) => !category.parentId && category.name === '未分类'

export function standardCategoryTree(categories: StandardLibraryCategory[], includeInactive = false) {
  type Node = { id: string; label: string; isActive: boolean; children: Node[] }
  const sorted = [...categories].filter(item => includeInactive || item.isActive).sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'zh-CN'))
  const nodes = new Map(sorted.map(item => [item.id, { id: item.id, label: item.name, isActive: item.isActive, children: [] as Node[] }]))
  const roots: Node[] = []
  for (const item of sorted) {
    const node = nodes.get(item.id)!, parent = item.parentId ? nodes.get(item.parentId) : undefined
    if (parent) parent.children.push(node)
    else roots.push(node)
  }
  return roots
}

export function matchesStandardCategory(categories: StandardLibraryCategory[], selected: string, assigned: string[]) {
  if (!selected) return true
  if (selected === '__unclassified__' || categories.some(item => item.id === selected && isUncategorized(item))) return assigned.length === 0
  const byId = new Map(categories.map(item => [item.id, item]))
  return assigned.some(id => {
    const seen = new Set<string>()
    while (id && !seen.has(id)) {
      if (id === selected) return true
      seen.add(id); id = byId.get(id)?.parentId || ''
    }
    return false
  })
}

export function standardCategoryCode(categories: StandardLibraryCategory[], categoryId?: string | null): string | undefined {
  let category = categories.find(item => item.id === categoryId)
  const seen = new Set<string>()
  while (category?.parentId) {
    if (seen.has(category.id)) return undefined
    seen.add(category.id)
    category = categories.find(item => item.id === category!.parentId)
  }
  return category?.code || undefined
}
