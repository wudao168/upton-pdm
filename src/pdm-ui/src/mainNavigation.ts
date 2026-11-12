import { Bot, Boxes, ClipboardCheck, Clock, FileCode, FileText, FolderKanban, LayoutDashboard, Library, Settings } from './solidMenuIcons'
import type { NavigationKey } from './navigationAccess'

export const footerNavigationEntries = [
  { key: 'ai-assistant', label: 'AI助手', icon: Bot, iconColor: '#7165bd' },
  { key: 'admin', label: '系统设置', icon: Settings, iconColor: '#8298b2' },
] as const

export const mainNavigationGroups = [
  { key: 'primary', label: '项目', items: [
    { key: 'project-workbench', label: '项目总览', icon: LayoutDashboard, iconColor: '#3e63dd' },
    { key: 'project-center', label: '项目中心', icon: FolderKanban, iconColor: '#12a594' },
    { key: 'work-hours', label: '工时管理', icon: Clock, iconColor: '#f5a524' },
    { key: 'tasks', label: '我的待办', icon: ClipboardCheck, iconColor: '#8e4ec6' },
  ] },
  { key: 'production', label: '生产', items: [
    { key: 'production-drawings', label: '制造中心', icon: FileText, iconColor: '#f76b15' },
    { key: 'spare-parts', label: '备品备件', icon: Boxes, iconColor: '#0d9469' },
  ] },
  { key: 'standardization', label: '标准化', items: [
    { key: 'standard-library', label: '标准物料', icon: Library, iconColor: '#30a46c' },
    { key: 'program-templates', label: '标准程序', icon: FileCode, iconColor: '#0090ff' },
    { key: 'materials', label: '料品管理', icon: Boxes, iconColor: '#d6409f' },
  ] },
  { key: 'purchase', label: '总结', items: [
    { key: 'purchase-analysis', label: '采购分析', icon: LayoutDashboard, iconColor: '#009688' },
    { key: 'quality-review', label: '经验总结', icon: ClipboardCheck, iconColor: '#009688' },
  ] },
] satisfies Array<{ key: string; label: string; items: Array<{ key: NavigationKey; label: string; icon: typeof FolderKanban; iconColor: string }> }>
