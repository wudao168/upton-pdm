export interface ProjectContactInformation {
  projectId: string
  customerContact: string
  customerPhone: string
  shippingAddress: string
  shippingContact: string
  shippingPhone: string
  rowVersion: number
}
export interface ProjectContactInformationView {
  information: ProjectContactInformation
  customerName: string
  canEdit: boolean
}
