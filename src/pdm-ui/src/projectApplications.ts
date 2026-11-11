import type { PdmCustomer, ProjectNumberingOptions } from './types'
import type { SettlementLine } from './projectBudget'

export const pendingApplicationContract = '待补合同'
export const maximumContractLines = 100
export const maximumContractQuantity = 1_000_000
export interface ProjectApplicationContractLine { name: string; specification: string; quantity: number | null; remarks: string }
export interface ProjectApplicationContractGroup { main: ProjectApplicationContractLine; children: ProjectApplicationContractLine[] }
export interface ProjectApplicationContract {
  number: string
  details: string
  groups?: ProjectApplicationContractGroup[] | null
}
export interface ProjectApplicationContractDraft extends ProjectApplicationContract {
  groups: ProjectApplicationContractGroup[] | null
}

export function blankContractLine(): ProjectApplicationContractLine {
  return { name: '', specification: '', quantity: null, remarks: '' }
}

export function copyContractDraft(value?: ProjectApplicationContract | null): ProjectApplicationContractDraft {
  return {
    number: value?.number ?? '', details: value?.details ?? '',
    groups: value?.groups?.map(group => ({ main: { ...group.main }, children: group.children.map(line => ({ ...line })) }))
      ?? (value?.details?.trim() ? null : [{ main: blankContractLine(), children: [] }]),
  }
}

export function contractDraftError(value: ProjectApplicationContractDraft, requireComplete: boolean) {
  if (value.groups === null) {
    return requireComplete && (!value.number.trim() || !value.details.trim()) ? '请填写合同编号和合同明细。' : ''
  }
  const lines = value.groups.flatMap(group => [group.main, ...group.children])
  if (lines.some(line => line.quantity != null && (!Number.isInteger(line.quantity) || line.quantity < 1 || line.quantity > maximumContractQuantity))) {
    return '合同明细的数量应为1至100万的整数。'
  }
  if (requireComplete && (!value.number.trim() || !lines.length || lines.some(line => !line.name.trim() || line.quantity == null))) {
    return '请填写合同编号，并补齐合同明细每行的名称和数量。'
  }
  return ''
}

export interface ProjectApplicationInput {
  customerId: string | null; customerName: string; projectName: string; orderDate: string | null; deliveryDate: string | null
  salesUsername: string; presalesUsernames: string[]; remarks: string; isSpecial: boolean
  budgetAmounts?: Record<string, number | null> | null
  settlementLines?: SettlementLine[] | null
  contract?: ProjectApplicationContract | null
}
export interface ProjectApplicationPerson { username: string; displayName: string }
export interface ProjectApplicationData { input: ProjectApplicationInput; salesName: string; presalesPeople: ProjectApplicationPerson[]; canViewBudget?: boolean }
export interface ProjectApplication {
  id: string; number: string; companyId: string; applicant: string; applicantName: string; status: string; revision: number
  data: ProjectApplicationData; rowVersion: number; createdAt: string; updatedAt: string; projectId: string | null; projectCode: string | null
  contract?: ProjectApplicationContract | null
}
export interface ProjectApplicationApprovalInput {
  expectedRowVersion: number; customerId: string; projectTypeCode: string; equipmentTypeCode: number; quantity: number
  bomItemCategoryCode: '0301' | '0302'; projectAlias: string; comment: string
  projectData?: ProjectApplicationInput
}
export interface ProjectApplicationEvent {
  id: string; revision: number; action: string; actor: string; actorName: string; at: string; data: ProjectApplicationData
  comment: string | null; approval: ProjectApplicationApprovalInput | null; projectId: string | null; projectCode: string | null
  approvedCustomerName?: string | null
  approvedData?: ProjectApplicationData | null
  supplementedContract?: ProjectApplicationContract | null
}
export interface ProjectApplicationDetail { application: ProjectApplication; history: ProjectApplicationEvent[]; canEdit: boolean; canWithdraw: boolean; canApprove: boolean; canSupplementContract?: boolean }
export interface ProjectApplicationView {
  canViewBudget?: boolean
  applications: ProjectApplication[]; canApply: boolean; canApprove: boolean; people: ProjectApplicationPerson[]
  customers: PdmCustomer[]; numberingOptions: ProjectNumberingOptions
}
