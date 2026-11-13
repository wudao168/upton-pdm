export type NavigationKey = 'work-hours' | 'project-applications' | 'project-calendar' | 'project-center' | 'project-workbench' | 'project-reports' | 'projects' | 'production-drawings' | 'spare-parts' | 'purchase-analysis' | 'quality-review' | 'ai-assistant' | 'materials' | 'standard-library' | 'stocked-materials' | 'standard-structure' | 'program-templates' | 'tasks' | 'admin'
type PermissionCheck = (permission: string) => boolean

const productionOperations = ['production.assign', 'production.schedule', 'production.execute', 'production.inspect', 'production.issue', 'production.capacity.manage', 'production.drawing.manage', 'production.scan.manage']

const systemTabs = {
  ai: ['ai.settings.manage'],
  u9: ['settings.customer.manage', 'settings.storage.manage', 'stock.settings.manage'],
  users: ['settings.organization.manage', 'system.role.view'],
  settings: ['settings.storage.manage', 'settings.folder.manage'],
  approval: ['settings.storage.manage'],
  'production-scan': ['production.scan.manage'],
  audit: ['audit.view'],
  'client-logs': ['system.client-logs.view'],
  'login-security': ['system.login-policy.manage', 'system.client-logs.view'],
}
export type SystemTab = keyof typeof systemTabs

export function systemManagementTabs(can: PermissionCheck): SystemTab[] {
  return (Object.keys(systemTabs) as SystemTab[]).filter(tab => systemTabs[tab].some(can))
}

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

export function canViewNavigation(key: NavigationKey | 'workspace', can: PermissionCheck) {
  switch (key) {
    case 'project-workbench': case 'project-reports': case 'project-calendar': case 'projects':
      return can('project.view')
    case 'project-center': case 'workspace':
      return can('project.view') && (!can('system.project-center-permissions.v1') ||
        can('project.summary.view') || ['overview', 'information', 'files', 'project-plan', 'opl', 'budget', 'validation-plan', 'documents', 'bom', 'release', 'procurement', 'records'].some(tab => can(`project.tab.${tab}.view`)))
    case 'project-applications':
      return can('project.application.submit') || can('project.application.approve')
    case 'standard-library': case 'standard-structure':
      return can('standard-library.view')
    case 'stocked-materials': return can('stock.view')
    case 'program-templates': return can('program-template.view')
    case 'materials': return can('material.view')
    case 'production-drawings':
      return ['tracking', 'capacity', 'drawings', 'execution', 'suppliers'].some(tab => canViewProductionTab(tab, can))
    case 'spare-parts': return can('spare-parts.view')
    case 'purchase-analysis': return can('purchase-analysis.view')
    case 'quality-review': return can('quality.review.view')
    case 'ai-assistant': return can('ai.chat.use')
    case 'admin': return systemManagementTabs(can).length > 0
    // Personal work-hour records and personal tasks are available to authenticated users.
    case 'work-hours': case 'tasks': return true
    default: return false
  }
}
