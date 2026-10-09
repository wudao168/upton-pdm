export interface ProjectContactEntry { contact: string; phone: string; address: string; isShippingAddress: boolean; position?: string; remarks?: string }
export interface ProjectContactInformation {
  projectId: string
  customerContact: string
  customerPhone: string
  shippingAddress: string
  shippingContact: string
  shippingPhone: string
  rowVersion: number
  salesPeople?: string
  presalesPeople?: string
  entries?: ProjectContactEntry[]
}
export interface ProjectContactInformationView {
  information: ProjectContactInformation
  customerName: string
  canEdit: boolean
}
