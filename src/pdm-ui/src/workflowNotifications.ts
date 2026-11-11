import type { OrganizationDirectory, ApprovalCopyRecipient } from './types'

export interface DepartmentPosition { id: string; name: string; isActive: boolean }
export interface DepartmentPositionAssignment { departmentId: string; positionId: string; usernames: string[] }
export interface WorkflowNotificationRecipient { departmentSource: 'Fixed' | 'SubmitterDepartment' | 'CompanyRole' | 'User' | 'Submitter' | 'Manager' | 'FixedManager' | 'ParentDepartment' | 'SubmitterDivision' | 'ProjectRole' | 'ProjectMembers'; departmentId: string | null; positionId?: string; roleCode?: string | null; target?: string | null; includeSubdepartments?: boolean }
export interface WorkflowNotificationRule { flowCode: string; enabled: boolean; recipients: WorkflowNotificationRecipient[] }
export interface WorkflowNotificationCatalog { companyId: string; rowVersion: number; positions: DepartmentPosition[]; assignments: DepartmentPositionAssignment[]; flows: WorkflowNotificationRule[] }
export interface WorkflowNoticeRecord { id: string; state: string; message: string; createdAt: string; submitter: string; submitterDepartmentId: string | null; messages: Array<{ username: string; title: string; content: string }> }
export const submitterDepartmentOption = '__submitter_department__'
export function noticeToCopy(row:WorkflowNotificationRecipient):ApprovalCopyRecipient {
  return { source:row.departmentSource==='Fixed'?'Department':row.departmentSource==='CompanyRole'?'Role':row.departmentSource,
    target:row.departmentSource==='CompanyRole'?row.roleCode??'':row.departmentSource==='Fixed'||row.departmentSource==='FixedManager'?row.departmentId??'':row.target??'',
    roleCode:row.departmentSource==='CompanyRole'?null:row.positionId&&row.positionId!=='00000000-0000-0000-0000-000000000000'?'__legacy__':row.roleCode,
    includeSubdepartments:row.includeSubdepartments??false }
}
export function copyToNotice(row:ApprovalCopyRecipient):WorkflowNotificationRecipient {
  return {departmentSource:row.source==='Department'?'Fixed':row.source==='Role'?'CompanyRole':row.source,
    departmentId:['Department','FixedManager'].includes(row.source)?row.target:null,
    roleCode:row.source==='Role'?row.target:row.roleCode??null,
    target:['User','ProjectRole'].includes(row.source)?row.target:null,includeSubdepartments:!!row.includeSubdepartments}
}
export function incompleteCopy(row:ApprovalCopyRecipient) { return ['Role','Department','User','FixedManager','ProjectRole'].includes(row.source)&&!row.target.trim() }
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
