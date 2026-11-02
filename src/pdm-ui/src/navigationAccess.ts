export type NavigationKey = 'work-hours' | 'project-applications' | 'project-calendar' | 'project-center' | 'project-workbench' | 'projects' | 'production-drawings' | 'materials' | 'standard-library' | 'stocked-materials' | 'standard-structure' | 'program-templates' | 'tasks' | 'admin'
type PermissionCheck = (permission: string) => boolean

const productionOperations = ['production.assign', 'production.schedule', 'production.execute', 'production.inspect', 'production.issue', 'production.capacity.manage', 'production.drawing.manage', 'production.scan.manage']

export function canViewProductionTab(tab: string, can: PermissionCheck) {
  const content = can('project.content.view')
  switch (tab) {
    case 'tracking': case 'external': case 'schedule':
      return content || productionOperations.some(can)
    case 'capacity': case 'drawings':
      return content
    case 'execution':
      return content || productionOperations.some(can)
    case 'suppliers':
      return can('production.supplier.view') || can('production.supplier.manage')
    default: return false
  }
}

export function canViewNavigation(key: NavigationKey | 'workspace', can: PermissionCheck, canManageSystem: boolean) {
  switch (key) {
    case 'project-workbench': case 'project-calendar': case 'projects':
      return can('project.view')
    case 'project-center': case 'workspace':
      return can('project.view') && (!can('system.project-center-permissions.v1') ||
        ['overview', 'information', 'files', 'project-plan', 'opl', 'budget', 'validation-plan', 'documents', 'bom', 'release', 'procurement', 'records'].some(tab => can(`project.tab.${tab}.view`)))
    case 'project-applications':
      return can('project.application.submit') || can('project.application.approve')
    case 'standard-library': case 'standard-structure': case 'stocked-materials':
      return can('standard-library.view')
    case 'program-templates': return can('program-template.view')
    case 'materials': return can('material.view')
    case 'production-drawings':
      return ['tracking', 'capacity', 'drawings', 'execution', 'suppliers'].some(tab => canViewProductionTab(tab, can))
    case 'admin': return canManageSystem
    // Personal work-hour records and personal tasks are available to authenticated users.
    case 'work-hours': case 'tasks': return true
    default: return false
  }
}
