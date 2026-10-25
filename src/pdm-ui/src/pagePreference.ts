import { inject, ref, watch, type InjectionKey, type Ref } from 'vue'

export const pagePreferenceScopeKey: InjectionKey<Readonly<Ref<string>>> = Symbol('page-preference-scope')

export function readPagePreference(key: string): string | null {
  for (const storage of [() => window.sessionStorage, () => window.localStorage]) {
    try { const value = storage().getItem(key); if (value !== null) return value } catch { /* 尝试下一种可用存储。 */ }
  }
  return null
}
export function writePagePreference(key: string, value: string) {
  for (const storage of [() => window.sessionStorage, () => window.localStorage]) {
    try { storage().setItem(key, value) } catch { /* 不阻断页面切换。 */ }
  }
}

/** 只保存页面选择，不保存表单草稿；不同账号和公司分别记忆。 */
export function rememberedTab<T extends string>(name: string, initial: T, allowed: readonly T[]): Ref<T> {
  const value = ref(initial) as Ref<T>
  const scope = inject(pagePreferenceScopeKey, undefined)
  if (!scope) return value
  const key = () => `upton-pdm-page-tab:v1:${scope.value}:${name}`
  watch(scope, () => {
    let saved: string | null = null
    saved = readPagePreference(key())
    value.value = allowed.includes(saved as T) ? saved as T : initial
  }, { immediate: true, flush: 'sync' })
  watch(value, selected => {
    try { if (JSON.parse(scope.value)[0]) writePagePreference(key(), selected) } catch { /* 无效范围仍使用默认页签。 */ }
  }, { flush: 'sync' })
  return value
}
