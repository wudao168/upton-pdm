import type { PdmCustomer, ProjectNumberingOptions } from './types'

export interface ProjectApplicationInput {
  customerId: string | null; customerName: string; projectName: string; orderDate: string | null; deliveryDate: string | null
  salesUsername: string; presalesUsernames: string[]; remarks: string; isSpecial: boolean
}
export interface ProjectApplicationPerson { username: string; displayName: string }
export interface ProjectApplicationData { input: ProjectApplicationInput; salesName: string; presalesPeople: ProjectApplicationPerson[] }
export interface ProjectApplication {
  id: string; number: string; companyId: string; applicant: string; applicantName: string; status: string; revision: number
  data: ProjectApplicationData; rowVersion: number; createdAt: string; updatedAt: string; projectId: string | null; projectCode: string | null
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
}
export interface ProjectApplicationDetail { application: ProjectApplication; history: ProjectApplicationEvent[]; canEdit: boolean; canWithdraw: boolean; canApprove: boolean }
export interface ProjectApplicationView {
  applications: ProjectApplication[]; canApply: boolean; canApprove: boolean; people: ProjectApplicationPerson[]
  customers: PdmCustomer[]; numberingOptions: ProjectNumberingOptions
}
