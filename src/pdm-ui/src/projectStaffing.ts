import type { OrganizationDirectory, ProjectSummary } from './types'

export function divisionStaffingUsers(directory: OrganizationDirectory, divisionId?: string) {
  if (!divisionId) return []
  const activeUnits = new Map(directory.units.filter(unit => unit.isActive).map(unit => [unit.id, unit]))
  const membershipScope = new Map<string, boolean>()
  function inDivision(id: string) {
    const seen = new Set<string>()
    let current = activeUnits.get(id)
    while (current && !seen.has(current.id)) {
      if (current.id === divisionId) return true
      if (membershipScope.has(current.id)) return membershipScope.get(current.id)!
      seen.add(current.id)
      current = current.parentUnitId ? activeUnits.get(current.parentUnitId) : undefined
    }
    return false
  }
  const members = new Set<string>()
  for (const item of directory.memberships) {
    if (!membershipScope.has(item.unitId)) membershipScope.set(item.unitId, inDivision(item.unitId))
    if (membershipScope.get(item.unitId)) members.add(item.username.toLowerCase())
  }
  return directory.users.filter(user => user.isActive && members.has(user.username.toLowerCase()))
}

export function canChangeProjectManager(currentUsername: string, project: ProjectSummary | null) {
  return !project?.primaryProjectManager
    || currentUsername.toLowerCase() !== project.primaryProjectManager.toLowerCase()
    || project.effectiveProjectPermissions?.includes('project.staffing.replace-self') === true
}
