import type { StandardLibraryCategory } from './types'

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
