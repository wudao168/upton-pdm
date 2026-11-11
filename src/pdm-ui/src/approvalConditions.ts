import type { ApprovalWorkflowStepTemplate } from './types'
export interface FieldCondition { field: string; operator: string; value: string | null }
export interface ConditionSet { match: 'All' | 'Any'; rules: FieldCondition[] }
export interface ConditionField { key: string; label: string; type: 'text' | 'number' | 'date' | 'set' | 'enum'; options?: [string, string][] }
export function conditionFields(flow: string): ConditionField[] {
  const common: ConditionField[] = [
    { key: 'SubmitterRole', label: '发起人角色', type: 'set' }, { key: 'Submitter', label: '发起人账号', type: 'text' },
    { key: 'SubmitterDepartment', label: '发起人所属部门', type: 'set' }, { key: 'ProjectCode', label: '项目号', type: 'text' }, { key: 'ProjectName', label: '项目名称', type: 'text' },
  ]
  const field = (key: string, label: string, type: ConditionField['type'] = 'text'): ConditionField => ({ key, label, type })
  if (flow.startsWith('expense-')) return [...common, field('TotalAmount', '预计总金额（元）', 'number'),
    { ...field('Purpose', '申请用途', 'enum'), options: [['SitePurchase', '现场采购'], ['RepairSupplement', '维修补订']] },
    { ...field('Department', '审批部门', 'enum'), options: [['Procurement', '采购部'], ['SupplyChain', '供应链']] },
    field('RequiredOn', '需求日期', 'date'), field('Reason', '申请原因'), field('LineCount', '明细行数', 'number'),
    { ...field('ItemKind', '明细类型（任一行）', 'set'), options: [['Material', '物料'], ['Service', '服务']] }]
  if (flow === 'validation-plan') return [...common, field('PreparedBy', '编制人'), field('ValidationDate', '计划日期', 'date'), field('ItemCount', '检查项数', 'number'), field('RevisionNumber', '计划版本', 'number')]
  if (flow === 'material-code-application') return [...common, field('MaterialName', '物料名称'), field('Specification', '规格型号'), field('Brand', '品牌'), field('Remark', '申请说明')]
  return [...common, { ...field('ReleaseScope', '发布类型', 'enum'), options: [['StandardLongLead','标准件长周期'],['StandardFormal','标准件正式'],['StandardSupplement','标准件补充'],['ElectricalFormal','电气件正式'],['ElectricalSupplement','电气件补充'],['NonStandardWithDrawing','非标件与图纸'],['NonStandardLongLead','非标件长周期'],['NonStandardSupplement','非标件补充'],['ElectricalLongLead','电气件长周期'],['Nameplate','铭牌']] }, field('ChangeReason', '变更原因'), field('ItemCount', '发布明细行数', 'number')]
}
export const conditionOperators: Record<string, string> = { Eq: '等于', Ne: '不等于', Gt: '大于', Gte: '大于等于', Lt: '小于', Lte: '小于等于', Contains: '包含', NotContains: '不包含', Empty: '为空', NotEmpty: '不为空' }
export function operators(type?: ConditionField['type']) { return type === 'number' || type === 'date' ? ['Eq','Ne','Gt','Gte','Lt','Lte','Empty','NotEmpty'] : type === 'text' ? ['Eq','Ne','Contains','NotContains','Empty','NotEmpty'] : ['Eq','Ne','Empty','NotEmpty'] }
export function conditionSummary(step: Pick<ApprovalWorkflowStepTemplate, 'conditions'>, flow: string) {
  return step.conditions?.rules.map(rule => {
    const field = conditionFields(flow).find(item => item.key === rule.field)
    return `${field?.label ?? '未选择字段'}${conditionOperators[rule.operator] ?? ''}${['Empty','NotEmpty'].includes(rule.operator) ? '' : field?.options?.find(item => item[0] === rule.value)?.[1] ?? rule.value ?? ''}`
  }).join(step.conditions.match === 'All' ? ' 且 ' : ' 或 ') || ''
}
