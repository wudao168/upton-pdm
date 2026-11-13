import { footerNavigationEntries, mainNavigationGroups } from './mainNavigation'
import type { PermissionDefinition } from './types'

const otherEntry = { key: 'other', label: '其他' }

export const authorizationNavigationGroups = [
  ...mainNavigationGroups,
  { key: 'system', label: '', items: [...footerNavigationEntries, otherEntry] },
]

// Presentation only. The permission catalog and saved grants retain their own codes and scopes.
const sections: Record<string, readonly [label: string, modules: string[], codes?: string[]][]> = {
  'project-workbench': [
    ['项目报表', ['项目报表']],
    ['项目明细', ['项目工作台']],
    ['立项信息', ['项目管理'], ['project.view', 'project.create', 'project.edit', 'project.delete']],
    ['立项申请', ['立项申请']],
  ],
  'project-center': [
    ['概览', [], ['project.tab.overview.view', 'project.risk.']],
    ['信息', ['项目分工', '项目铭牌'], ['project.tab.information.view', 'project.contact.edit', 'project.nameplate.', 'nameplate.template.manage', 'project.child.create']],
    ['文件', ['项目文件', '目录默认权限'], ['project.tab.files.view']],
    ['计划', ['项目计划'], ['project.tab.project-plan.view']],
    ['预算', ['项目预算', '财务数据范围', '费用申请', '项目奖金'], ['project.tab.budget.view']],
    ['质量', ['质量资料'], ['project.tab.validation-plan.view', 'validation-plan.edit', 'validation-catalog.manage']],
    ['OPL', ['项目OPL'], ['project.tab.opl.view']],
    ['质量闭环', [], ['project.quality.']],
    ['项目总结', [], ['project.summary.', 'quality.summary.review']],
    ['图档', ['项目图档', '图纸审核'], ['project.tab.documents.view', 'document.']],
    ['BOM', ['标准件／非标件BOM', '电气件BOM'], ['project.tab.bom.view', 'bom.']],
    ['发布', ['审批发布'], ['project.tab.release.view']],
    ['备料', [], ['project.tab.procurement.view']],
    ['记录', [], ['project.tab.records.view', 'project.record.manage']],
    ['通用操作', ['项目内容', '项目中心操作']],
  ],
  'work-hours': [['', ['工时管理']]],
  tasks: [['', ['消息中心'], ['approval.']]],
  'production-drawings': [
    ['发布跟踪', ['生产跟踪']],
    ['自制追溯', [], ['production.schedule']],
    ['产能分析', [], ['production.capacity.manage']],
    ['生产执行', ['生产执行'], ['production.execute', 'production.inspect', 'production.issue']],
    ['图纸资料', [], ['production.drawing.manage']],
    ['供应商库', ['供应商库']],
  ],
  'spare-parts': [['', ['备品备件']]],
  'standard-library': [
    ['标准物料', ['标准库']],
    ['常备件', ['常备物料']],
  ],
  'program-templates': [['', ['程序模板']]],
  materials: [['', ['料品管理']]],
  'purchase-analysis': [['', ['采购分析']]],
  'ai-assistant': [['', [], ['ai.chat.use', 'ai.business.read', 'ai.cloud.use']]],
  'quality-review': [
    ['公司问题汇总', [], ['quality.review.']],
    ['持续改善', [], ['quality.improvement.']],
    ['经验与落实', [], ['quality.experience.']],
  ],
  admin: [
    ['系统接口', ['常备件设置'], ['settings.customer.manage', 'stock.settings.manage']],
    ['AI服务', [], ['ai.settings.manage']],
    ['用户设置', ['权限中心'], ['settings.organization.manage', 'system.project-permission.view', 'system.platform.manage']],
    ['编号与存储', ['系统设置'], ['settings.storage.manage', 'settings.folder.manage']],
    ['生产扫码设置', [], ['production.scan.manage']],
    ['全局审计', ['系统审计']],
    ['客户端日志', [], ['system.client-logs.view']],
    ['登录安全', [], ['system.login-policy.manage']],
  ],
}

const entries = authorizationNavigationGroups.flatMap(group => group.items.map(item => ({
  key: item.key, label: item.label, groupLabel: group.label,
})))
const rules = entries.flatMap((entry, entryOrder) => (sections[entry.key] ?? []).map(([label, modules, codes], sectionOrder) => ({
  entry, label, modules, codes: codes ?? [], entryOrder, sectionOrder,
})))
const unclassifiedEntry = entries[entries.length - 1]!

export function permissionNavigationLocation(permission: Pick<PermissionDefinition, 'code' | 'module'>) {
  const rule = rules.find(rule => rule.codes.includes(permission.code))
    ?? rules.find(rule => rule.codes.some(code => code.endsWith('.') && permission.code.startsWith(code)))
    ?? rules.find(rule => rule.modules.includes(permission.module))
  const entry = rule?.entry ?? unclassifiedEntry
  const label = rule?.label ?? permission.module
  return {
    entryKey: entry.key,
    sectionKey: label ? `${entry.key}/${label}` : entry.key,
    sectionLabel: label,
    groupLabel: entry.groupLabel,
    path: label ? `${entry.label} → ${label}` : entry.label,
    entryOrder: rule?.entryOrder ?? entries.length - 1,
    sectionOrder: rule?.sectionOrder ?? 0,
  }
}
