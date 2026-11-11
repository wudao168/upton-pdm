import { inject, type InjectionKey } from 'vue'
import type { PdmUser } from './types'

export type UserDisplayNameResolver = (username?: string | null, emptyText?: string) => string

export const userDisplayNameKey: InjectionKey<UserDisplayNameResolver> = Symbol('userDisplayName')

export function resolveUserDisplayName(users: readonly PdmUser[], username?: string | null, emptyText = '—') {
  const normalized = username?.trim()
  if (!normalized) return emptyText
  const name = users.find(user => user.username.localeCompare(normalized, undefined, { sensitivity: 'accent' }) === 0)?.displayName?.trim()
  return name && name.toLowerCase() !== normalized.toLowerCase() ? name : '姓名待补充'
}

export function useUserDisplayName() {
  return inject(userDisplayNameKey, (username, emptyText = '—') => resolveUserDisplayName([], username, emptyText))
}
