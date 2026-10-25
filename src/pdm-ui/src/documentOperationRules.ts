import definitions from '../../Pdm.Domain/DocumentOperationRules.json'
// 每条正式规则只登记一次；分类、搜索和列表均由此目录生成。
import { documentRuleMessageCatalog, type DocumentRuleMessageInfo } from './documentRuleMessages'
export const documentRuleCategories = {
  A: '登录与权限', B: '结构与文件', C: '工程图', D: '身份与版本', E: '保存与上传',
  F: '获取与打开', G: '编辑与切换', H: '改名与属性', I: '草稿与恢复',
} as const
export type DocumentRuleCategory = keyof typeof documentRuleCategories
export interface DocumentOperationRule extends DocumentRuleMessageInfo { id: string; category: DocumentRuleCategory; name: string; behavior: string; canBlock: boolean }

export const documentOperationRules: readonly DocumentOperationRule[] = definitions.map(rule => ({
  ...rule, category: rule.id[0] as DocumentRuleCategory, ...documentRuleMessageCatalog[rule.id]!,
}))

export function matchesDocumentOperationRule(rule: DocumentOperationRule, query: string): boolean {
  const text = query.trim().toLowerCase().replace(/\s+/g, '')
  if (!text) return true
  const normalize = (value: string) => value.toLowerCase().replace(/\s+/g, '')
  if ([rule.id, rule.name, rule.behavior, rule.messageNote, documentRuleCategories[rule.category], ...rule.messages.map(message => message.text)]
    .some(value => normalize(value).includes(text))) return true
  // Pasting a whole dialog includes dynamic filenames and may include a wrapper prefix.
  return rule.messages.some(message => {
    const fragments = normalize(message.text).split(/\{[^}]+\}/).filter(Boolean)
    if (fragments.join('').length < 4) return false
    let offset = 0
    return fragments.every(fragment => {
      const index = text.indexOf(fragment, offset)
      if (index < 0) return false
      offset = index + fragment.length
      return true
    })
  })
}
