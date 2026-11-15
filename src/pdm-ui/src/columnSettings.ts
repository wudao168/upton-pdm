import type { Ref } from 'vue'
import { ref } from 'vue'

export type ColumnOption<Key extends string> = { key: Key; label: string }

export function orderColumns<Key extends string, Column extends ColumnOption<Key>>(columns: readonly Column[], order: readonly Key[]) {
  const remaining = new Map(columns.map(column => [column.key, column]))
  const ordered: Column[] = []
  for (const key of order) {
    const column = remaining.get(key)
    if (!column) continue
    ordered.push(column)
    remaining.delete(key)
  }
  return [...ordered, ...remaining.values()]
}

export function useColumnSettings<Key extends string>(visible: Ref<Key[]>, order: Ref<Key[]>) {
  const draftVisible = ref<Key[]>([]) as Ref<Key[]>
  const draftOrder = ref<Key[]>([]) as Ref<Key[]>
  function setDraft(keys: readonly Key[], sequence: readonly Key[] = []) {
    draftVisible.value = [...keys]
    draftOrder.value = [...sequence]
  }
  function openDraft() {
    setDraft(visible.value, order.value)
  }
  function applyDraft() {
    visible.value = [...draftVisible.value]
    order.value = [...draftOrder.value]
  }
  return { draftVisible, draftOrder, setDraft, openDraft, applyDraft }
}
