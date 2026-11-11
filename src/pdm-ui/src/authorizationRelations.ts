import type { ProjectPositionDefinition } from './types'

export interface RelationOption {
  key: string
  label: string
  description: string
  summary?: string
}

interface RelationGroup {
  title: string
  options: RelationOption[]
  advanced?: RelationOption[]
  secondary?: boolean
}

const groups: RelationGroup[] = [
  { title: '授权范围', options: [
    { key: 'company', label: '授权范围内的所有项目', description: '在角色获授权的范围内，无需项目分工即可匹配。' },
  ] },
  { title: '项目经理', options: [
    { key: 'MainManager', label: '本人负责的主项目及其子项目', description: '本人是主项目的项目经理或协同项目经理时，适用于该主项目及其子项目。', summary: '项目经理（主项目及子项目）' },
    { key: 'ChildManager', label: '仅本人负责的子项目', description: '本人是该子项目的项目经理或协同项目经理时，只适用于该子项目。', summary: '项目经理（仅负责的子项目）' },
  ], advanced: [
    { key: 'project-manager', label: '仅当前项目，含协同项目经理', description: '只看当前打开的主项目或子项目的「项目经理」和「协同项目经理」。', summary: '项目经理（仅当前项目，含协同项目经理）' },
    { key: 'primary-project-manager', label: '仅当前项目，不含协同项目经理', description: '只看当前打开的主项目或子项目的「项目经理」。', summary: '项目经理（仅当前项目，不含协同项目经理）' },
    { key: 'root-primary-manager', label: '主项目及子项目，不含协同项目经理', description: '只看所属主项目的「项目经理」，适用于该主项目及其子项目。', summary: '项目经理（主项目及子项目，不含协同项目经理）' },
  ] },
  { title: '主设计', options: [
    { key: 'MainDesigner', label: '本人负责的项目', description: '本人是主项目的主设计时，适用于主项目及其子项目。本人是子项目的主设计时，只适用于该子项目。', summary: '主设计（负责的项目）' },
  ], advanced: [
    { key: 'project-lead', label: '仅当前项目', description: '只看当前打开的主项目或子项目的「主设计」。', summary: '主设计（仅当前项目）' },
    { key: 'root-design-lead', label: '仅主项目', description: '只在操作主项目时检查该主项目的「主设计」。', summary: '主设计（仅主项目）' },
  ] },
  { title: '设计与电气', options: [
    { key: 'Engineer', label: '设计人员、电气工程师或硬件工程师', description: '匹配当前项目的这三类人员。主项目的电气工程师和硬件工程师也适用于子项目。' },
  ], advanced: [
    { key: 'current-designer', label: '仅本项目设计人员', description: '只看正在操作的项目的设计人员名单。' },
    { key: 'current-hardware', label: '仅本项目硬件工程师', description: '只看当前打开项目的「硬件工程师」。' },
    { key: 'project-designer', label: '本项目的主设计或设计人员', description: '匹配正在操作的项目中这两类分工。' },
  ] },
  { title: '事业部范围', secondary: true, options: [
    { key: 'division-manager', label: '我管理的事业部项目', description: '项目由本人管理的事业部或其下级单位执行。' },
    { key: 'division-member', label: '我所在的事业部项目', description: '本人是项目执行单位或其下级单位的成员。' },
  ] },
  { title: '销售与售前', secondary: true, options: [
    { key: 'sales-contact', label: '销售经理', description: '以所属主项目的「销售经理」为准。' },
    { key: 'presales-contact', label: '售前工程师', description: '以所属主项目的「售前工程师」为准。' },
  ] },
  { title: '文件目录', secondary: true, options: [
    { key: 'commercial-folder', label: '仅售前资料或客户资料目录', description: '只在操作项目的售前资料或客户资料目录时匹配。' },
    { key: 'release-folder', label: '仅发布资料目录', description: '只在操作项目的发布资料目录时匹配。' },
  ] },
  { title: '历史规则', secondary: true, options: [
    { key: 'Other', label: '未分配项目岗位的人员', description: '本人在当前项目和主项目均不是项目经理、协同项目经理、主设计、设计人员、电气工程师或硬件工程师，也未匹配已启用的自定义岗位。' },
  ] },
]
const labels = new Map(groups.flatMap(group => [...group.options, ...(group.advanced ?? [])])
  .map(option => [option.key, option.summary ?? option.label] as const))
const folderPermissions = new Set(['folder.view', 'folder.download', 'folder.upload', 'folder.edit', 'folder.manage'])

export function authorizationRelationLabel(key: string, permissionScope: string | undefined, available: Record<string, string>) {
  if (key === 'company' && permissionScope !== 'project') return '授权公司内'
  return labels.get(key) ?? available[key] ?? key
}

export function authorizationRelationGroups(permission: { code: string; scope?: string }, available: Record<string, string>,
  positions: ProjectPositionDefinition[], saved: string[]): RelationGroup[] {
  if (permission.scope !== 'project') {
    return [{ title: '授权范围', options: available.company ? [
      { key: 'company', label: '授权公司内', description: '在已授予该角色的公司范围内适用。' },
    ] : [] }]
  }

  const hasFolder = folderPermissions.has(permission.code)
  const availableOptions = (options: RelationOption[]) => options.filter(option => Object.hasOwn(available, option.key))
  const result = groups.map(group => {
    const options = availableOptions(group.options)
    return {
      ...group,
      options: group.title === '文件目录' && !hasFolder
        ? options.filter(option => saved.includes(option.key))
          .map(option => ({ ...option, description: '此操作不涉及文件目录，保留显示以便核对已有配置。' }))
        : options,
      advanced: availableOptions(group.advanced ?? []),
    }
  }).filter(group => group.options.length > 0 || group.advanced.length > 0)
  const custom = positions.filter(position => !position.isSystem && Object.hasOwn(available, position.key))
  const extra: RelationGroup[] = []
  if (custom.length) {
    extra.push({
      title: '自定义项目岗位',
      secondary: true,
      options: custom.map(position => ({
        key: position.key,
        label: position.name + (position.isActive ? '' : '（已停用）'),
        description: position.isActive
          ? position.scope === 'subtree' ? '按项目岗位分工匹配，主项目的分工也覆盖子项目。' : '仅按当前项目的岗位分工匹配。'
          : '该项目岗位已停用，目前不匹配。',
      })),
    })
  }
  const known = new Set([...labels.keys(), ...custom.map(position => position.key)])
  const additional = Object.entries(available).filter(([key]) => !known.has(key))
  if (additional.length) {
    extra.push({ title: '其他关系', secondary: true,
      options: additional.map(([key, label]) => ({ key, label, description: '' })) })
  }
  return [...result, ...extra]
}
