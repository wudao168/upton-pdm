import type { BomItem, U9InventoryRow } from './types'

export type InventoryBomRow = Pick<BomItem, 'drawingNumber' | 'name' | 'specification' | 'brand' | 'quantity' | 'unit' | 'sequence'> & {
  key: string
  signature: string
  disabledReason?: string
}
export interface InventoryCandidate {
  materialCode: string
  name: string
  specification: string
  brand: string
  similarity: number
  reason: string
  stockQuantity: number
  locations: U9InventoryRow[]
}
export function normalizeInventoryModel(value = '') {
  return value.normalize('NFKC').replace(/[^\p{L}\p{N}]/gu, '').toUpperCase()
}
export function inventoryBomSignature(row: Pick<BomItem, 'drawingNumber' | 'specification' | 'brand' | 'unit' | 'quantity'>) {
  return JSON.stringify([row.drawingNumber, row.specification, row.brand, row.unit, row.quantity])
}
export function hasOriginalInventory(item: InventoryBomRow, stock: U9InventoryRow[]) {
  const code = item.drawingNumber.trim().toUpperCase()
  const model = normalizeInventoryModel(item.specification)
  const brand = normalizeInventoryModel(item.brand)
  return stock.some(row => row.stockQuantity > 0 && (code
    ? row.materialCode.trim().toUpperCase() === code
    : !!model && normalizeInventoryModel(row.specification ?? '') === model
      && (!brand || normalizeInventoryModel(row.brand ?? '') === brand)))
}
export function groupInventoryCandidates(rows: U9InventoryRow[]): InventoryCandidate[] {
  const candidates = new Map<string, InventoryCandidate>()
  for (const row of rows) {
    if (row.stockQuantity <= 0 || (row.similarityPercent ?? 0) < 80) continue
    const key = row.materialCode.trim().toUpperCase()
    let candidate = candidates.get(key)
    if (!candidate) {
      candidate = { materialCode: row.materialCode, name: row.itemName, specification: row.specification ?? '',
        brand: row.brand ?? '', similarity: row.similarityPercent!, reason: row.similarityReason ?? '型号近似',
        stockQuantity: 0, locations: [] }
      candidates.set(key, candidate)
    }
    candidate.stockQuantity += row.stockQuantity
    candidate.similarity = Math.max(candidate.similarity, row.similarityPercent!)
    candidate.locations.push(row)
  }
  return [...candidates.values()].sort((a, b) => b.similarity - a.similarity || a.materialCode.localeCompare(b.materialCode))
}

// Align normalized characters, preserving punctuation and the original display text.
// Missing characters are highlighted in the opposite string when its arguments are reversed.
export function inventoryModelDifference(original: string, candidate: string): Array<{ text: string; changed: boolean }> {
  const left = [...normalizeInventoryModel(original)]
  const display = [...candidate]
  const right = display.flatMap((character, index) => [...normalizeInventoryModel(character)].map(value => ({ value, index })))
  // The inventory service accepts models up to 256 characters; bound alignment for malformed master data.
  if (left.length > 256 || right.length > 256) return [{ text: candidate, changed: normalizeInventoryModel(original) !== normalizeInventoryModel(candidate) }]
  const costs = Array.from({ length: left.length + 1 }, () => new Uint16Array(right.length + 1))
  for (let i = 0; i <= left.length; i++) costs[i]![0] = i
  for (let j = 0; j <= right.length; j++) costs[0]![j] = j
  for (let i = 1; i <= left.length; i++) {
    for (let j = 1; j <= right.length; j++) {
      costs[i]![j] = Math.min(costs[i - 1]![j]! + 1, costs[i]![j - 1]! + 1,
        costs[i - 1]![j - 1]! + (left[i - 1] === right[j - 1]!.value ? 0 : 1))
    }
  }
  const changed = new Set<number>()
  let i = left.length, j = right.length
  while (i || j) {
    if (i && j && costs[i]![j] === costs[i - 1]![j - 1]! + (left[i - 1] === right[j - 1]!.value ? 0 : 1)) {
      if (left[i - 1] !== right[j - 1]!.value) changed.add(right[j - 1]!.index)
      i--; j--
    } else if (j && costs[i]![j] === costs[i]![j - 1]! + 1) {
      changed.add(right[j - 1]!.index); j--
    } else i--
  }
  const segments: Array<{ text: string; changed: boolean }> = []
  display.forEach((character, index) => {
    const isChanged = changed.has(index)
    const last = segments.at(-1)
    if (last && last.changed === isChanged) last.text += character
    else segments.push({ text: character, changed: isChanged })
  })
  return segments
}
