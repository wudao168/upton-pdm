export type MobileOfficePage = 'home' | 'tasks' | 'projects' | 'work-hours' | 'mine'
export type MobileOfficeBlockKey = 'welcome' | 'metrics' | 'shortcuts' | 'approvals' | 'projects'
export interface MobileOfficeBlock { key: MobileOfficeBlockKey; title: string; enabled: boolean; width: number; columns: number; limit: number }
export interface MobileOfficeNav { key: MobileOfficePage; label: string; icon: MobileOfficePage; enabled: boolean }
export interface MobileOfficeLayout {
  schemaVersion: number; defaultPage: MobileOfficePage; theme: 'green' | 'blue' | 'purple'; spacing: 'compact' | 'comfortable'
  blocks: MobileOfficeBlock[]; navigation: MobileOfficeNav[]
}
export interface MobileOfficeLayoutVersion { version: number; publishedAt: string | null; publishedBy: string; action: string; sourceVersion: number | null; layout: MobileOfficeLayout }
export interface MobileOfficeLayoutView { companyId: string; rowVersion: number; draft: MobileOfficeLayout; publishedVersion: number; history: MobileOfficeLayoutVersion[] }
export interface PublishedMobileOfficeLayout { version: number; layout: MobileOfficeLayout }
export const mobileOfficeBlockLabels: Record<MobileOfficeBlockKey, string> = { welcome: '欢迎信息', metrics: '业务概况', shortcuts: '快捷入口', approvals: '审批列表', projects: '项目列表' }
export const mobileOfficePageLabels: Record<MobileOfficePage, string> = { home: '首页', tasks: '待办', projects: '项目', 'work-hours': '工时', mine: '我的' }
export function defaultMobileOfficeLayout(): MobileOfficeLayout {
  return {
    schemaVersion: 1, defaultPage: 'home', theme: 'green', spacing: 'comfortable',
    blocks: [
      { key: 'welcome', title: '移动办公', enabled: true, width: 2, columns: 1, limit: 5 },
      { key: 'metrics', title: '业务概况', enabled: true, width: 2, columns: 2, limit: 5 },
      { key: 'shortcuts', title: '快捷入口', enabled: true, width: 2, columns: 1, limit: 5 },
      { key: 'approvals', title: '我的审批', enabled: true, width: 2, columns: 1, limit: 5 },
      { key: 'projects', title: '我的项目', enabled: true, width: 2, columns: 1, limit: 5 },
    ],
    navigation: (Object.keys(mobileOfficePageLabels) as MobileOfficePage[]).map(key => ({ key, label: mobileOfficePageLabels[key], icon: key, enabled: true })),
  }
}
export function mobileOfficeNavigation(layout: MobileOfficeLayout, canViewProjects: boolean) {
  return layout.navigation.filter(item => item.enabled && (item.key !== 'projects' || canViewProjects))
}
export function mobileOfficeInitialPage(layout: MobileOfficeLayout, canViewProjects: boolean): MobileOfficePage {
  return mobileOfficeNavigation(layout, canViewProjects).some(item => item.key === layout.defaultPage) ? layout.defaultPage : 'home'
}
