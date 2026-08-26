import { inject, type InjectionKey } from 'vue'
import type { PdmUser } from './types'

export type UserDisplayNameResolver = (username?: string | null, emptyText?: string) => string

export const userDisplayNameKey: InjectionKey<UserDisplayNameResolver> = Symbol('userDisplayName')

export function resolveUserDisplayName(users: readonly PdmUser[], username?: string | null, emptyText = '—') {
  const normalized = username?.trim()
  if (!normalized) return emptyText
  return users.find(user => user.username.localeCompare(normalized, undefined, { sensitivity: 'accent' }) === 0)?.displayName || normalized
}

export function useUserDisplayName() {
  return inject(userDisplayNameKey, (username, emptyText = '—') => username?.trim() || emptyText)
}
