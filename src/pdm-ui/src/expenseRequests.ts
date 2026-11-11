import type { ApprovalParticipants } from './approvalParticipants'
import type { ApprovalWorkflowTemplate } from './types'

export interface ExpenseLine {
  kind: 'Material' | 'Service'
  materialCode: string
  name: string
  specification: string
  brand: string
  unit: string
  quantity: number | null
  unitPrice: number | null
  note: string
}
export interface ExpenseStep {
  id: string
  name: string
  participants: ApprovalParticipants
  decision: 'Approved' | 'Rejected' | null
  actor: string | null
  comment: string | null
  decidedAt: string | null
}
export interface ExpenseRequest {
  id: string
  projectId: string
  projectCode: string
  projectName: string
  applicant: string
  purpose: 'SitePurchase' | 'RepairSupplement'
  department: 'Procurement' | 'SupplyChain'
  requiredOn: string | null
  reason: string
  lines: ExpenseLine[]
  status: 'Draft' | 'Pending' | 'Approved' | 'Rejected' | 'Withdrawn'
  workflow: ApprovalWorkflowTemplate | null
  steps: ExpenseStep[]
  currentStep: ExpenseStep | null
  totalAmount: number | null
  rowVersion: number
  updatedAt: string
}
export interface ExpenseView { request: ExpenseRequest; canEdit: boolean; canApprove: boolean; canViewAmount?: boolean; canWithdraw?: boolean }
export interface ExpenseDirectory {
  projectId: string
  projectCode: string
  projectName: string
  canCreate: boolean
  requests: ExpenseView[]
  departments: { code: string; name: string; configured: boolean }[]
}
export interface SaveExpense {
  purpose: ExpenseRequest['purpose']
  department: ExpenseRequest['department']
  requiredOn: string | null
  reason: string
  lines: ExpenseLine[]
  expectedRowVersion: number
  submit: boolean
}
export const expensePurpose = (purpose: string) => purpose === 'SitePurchase' ? '现场采购' : '维修补订'
export const expenseStatus = (status: string) => ({ Draft: '草稿', Pending: '审批中', Approved: '已通过', Rejected: '已退回', Withdrawn: '已撤回' }[status] ?? '待确认')
export const expenseLineAmount = (line: ExpenseLine) => line.quantity == null || line.unitPrice == null ? null : Math.round((line.quantity * line.unitPrice + Number.EPSILON) * 100) / 100
