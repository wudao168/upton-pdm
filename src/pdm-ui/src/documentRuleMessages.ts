import definitions from '../../Pdm.Domain/DocumentOperationRules.json'
// 定位用的现有提示原文；{…} 表示文件名、路径等动态内容，不改变规则执行。
export type DocumentRuleMessageKind = '报错' | '提示' | '日志'
export interface DocumentRuleMessage { kind: DocumentRuleMessageKind; text: string }
export interface DocumentRuleMessageInfo { messages: readonly DocumentRuleMessage[]; messageNote: string }

export const documentRuleMessageCatalog: Record<string, DocumentRuleMessageInfo> = Object.fromEntries(definitions.map(rule => [rule.id, { messages: rule.messages as DocumentRuleMessage[], messageNote: rule.messageNote }]))
