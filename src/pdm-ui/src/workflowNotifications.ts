import type { OrganizationDirectory } from './types'

export interface DepartmentPosition { id: string; name: string; isActive: boolean }
export interface DepartmentPositionAssignment { departmentId: string; positionId: string; usernames: string[] }
export interface WorkflowNotificationRecipient { departmentSource: 'Fixed' | 'SubmitterDepartment' | 'CompanyRole'; departmentId: string | null; positionId?: string; roleCode?: string | null }
export interface WorkflowNotificationRule { flowCode: string; enabled: boolean; recipients: WorkflowNotificationRecipient[] }
export interface WorkflowNotificationCatalog { companyId: string; rowVersion: number; positions: DepartmentPosition[]; assignments: DepartmentPositionAssignment[]; flows: WorkflowNotificationRule[] }
export interface WorkflowNoticeRecord { id: string; state: string; message: string; createdAt: string; submitter: string; submitterDepartmentId: string | null; messages: Array<{ username: string; title: string; content: string }> }
export const submitterDepartmentOption = '__submitter_department__'
export function notificationDepartments(directory: OrganizationDirectory | undefined, companyId: string) {
  const ids = new Set<string>()
  const units = directory?.units.filter(unit => unit.organizationId === companyId && unit.isActive) ?? []
  for (let previous = -1; previous !== ids.size;) {
    previous = ids.size
    for (const unit of units) if (!unit.parentUnitId || ids.has(unit.parentUnitId)) ids.add(unit.id)
  }
  return units.filter(unit => ids.has(unit.id))
}
export function notificationDepartmentPath(directory: OrganizationDirectory | undefined, id: string) {
  const names: string[] = [], visited = new Set<string>()
  for (let unit = directory?.units.find(unit => unit.id === id); unit && !visited.has(unit.id); unit = directory?.units.find(parent => parent.id === unit!.parentUnitId)) {
    visited.add(unit.id); names.unshift(unit.name)
  }
  return names.join(' / ')
}
