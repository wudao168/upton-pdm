export type CompanyBusinessDetailsKind = 'invoice' | 'receiving'
export type CompanyBusinessFields = Record<string, string | null | undefined>
export interface CompanyBusinessDetails {
  rowVersion: number
  invoice?: CompanyBusinessFields | null
  receiving?: CompanyBusinessFields | null
}
export interface CompanyBusinessDetailsStatus {
  organizationId: string
  invoiceConfigured: boolean
  receivingConfigured: boolean
}
