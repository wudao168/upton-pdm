import { requestJson } from './api'
import type { PermissionDefinition, ProjectPositionDefinition } from './types'
export interface AuthorizationGrant { permission: string; scopes: string[] }
export interface AuthorizationTemplate { key: string; name: string; description: string; grants: AuthorizationGrant[]; roleCode: string | null; isActive: boolean; projectAssignable?: boolean }
export interface AuthorizationAssignment { id: string; username: string; templateKey: string; companyId: string; scope: 'company' | 'department' | 'project' | 'subtree'; targetId: string | null }
export interface AuthorizationSettings { revision: number; templates: AuthorizationTemplate[]; assignments: AuthorizationAssignment[]; positions: ProjectPositionDefinition[]; migratedAt: string }
export interface AuthorizationDirectory {
  settings: AuthorizationSettings; permissions: PermissionDefinition[]; scopes: Record<string, string>; canEdit: boolean
  defaultRoleGrants?: AuthorizationGrant[]
  users: { username: string; displayName: string; companyId: string; roles?: string[]; role: string; isActive: boolean }[]
  projects: { id: string; code: string; name: string; parentProjectId?: string; organizationId: string }[]
  departments: { id: string; name: string; organizationId: string }[]
}
export interface AuthorizationDecision { permission: string; allowed: boolean; reason: string; sources: string[] }
export const getAuthorization = (token: string) => requestJson<AuthorizationDirectory>('/api/authorization-center', {}, token)
export const saveAuthorization = (settings: AuthorizationSettings, token: string) => requestJson<AuthorizationSettings>('/api/authorization-center', { method: 'PUT', body: JSON.stringify(settings) }, token)
export const previewAuthorization = (username: string, projectId: string, token: string) => requestJson<AuthorizationDecision[]>(`/api/authorization-center/preview?${new URLSearchParams({ username, ...(projectId ? { projectId } : {}) })}`, {}, token)
