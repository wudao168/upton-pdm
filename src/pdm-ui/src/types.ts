export type DocumentKind = 'Assembly' | 'Part' | 'Drawing'
export type ReferenceStatus = 'Normal' | 'Suppressed' | 'Hidden' | 'Lightweight' | 'Virtual' | 'Missing' | 'Unregistered'
export type PreviewMode = 'model' | 'drawing' | 'bom'
export type SolidWorksOpenMode = 'LatestReadOnly' | 'LatestReleased' | 'LatestEdit' | 'SpecificReadOnly'

export interface ProjectSummary {
  id: string
  code: string
  name: string
  owner: string
  stage: string
  vaultName: string
  vaultLocation: string
  releaseLocation: string
  projectAlias?: string
  organizationId?: string
  organizationName?: string
  projectTypeCode?: string
  equipmentTypeCode?: number
  customerCode?: string
  customerName?: string
  customerProjectSequence?: number
  deviceModel?: string
  signedDate?: string
  quantity: number
  parentProjectId?: string
  childSequence?: number
  serialNumbers: string[]
  responsibleUsers: string[]
}

export interface CreateProjectInput {
  organizationId: string
  projectTypeCode: string
  equipmentTypeCode: number
  customerId: string
  name: string
  projectAlias?: string
  signedDate: string
  quantity: number
}

export interface CreateSubprojectInput {
  name: string
  projectAlias?: string
  quantity: number
}

export interface ProjectOrganization {
  id: string
  name: string
  projectCompanyCode: string
  modelCompanyCode: string
  crmCompanyName: string
  currentProjectSequence: number
  currentSerialSequence: number
}

export interface ProjectTypeDefinition { code: string; name: string }
export interface EquipmentTypeDefinition { code: number; name: string; isActive?: boolean }
export interface ProjectNumberingOptions {
  organizations: ProjectOrganization[]
  projectTypes: ProjectTypeDefinition[]
  equipmentTypes: EquipmentTypeDefinition[]
}
export interface PdmCustomer { id: string; code: string; name: string; isActive: boolean }
export interface PdmUser { username: string; displayName: string; role: string; isActive: boolean }
export interface PdmSystemSettings { vaultRoot: string; releaseRoot: string }
export interface DocumentNode {
  /** Unique assembly occurrence. Tree selection and rendering must use this value. */
  id: string
  /** PDM document identity. Different occurrences of one part intentionally share this value. */
  documentId?: string
  drawingNumber: string
  name: string
  fileName: string
  kind: DocumentKind
  configuration: string
  quantity: number
  version: string
  checkedOutBy?: string
  status: ReferenceStatus
  children: DocumentNode[]
}

export interface BomItem {
  sequence: number
  drawingNumber: string
  name: string
  quantity: number
  unit: string
  material?: string
  specification?: string
  revision: string
  complete: boolean
}

export interface ApprovalStep {
  id: string
  stage: string
  assignee: string
  status: 'done' | 'current' | 'waiting'
  detail: string
  decision?: string | number
  comment?: string
}

export interface ReleasePackageSummary {
  id: string
  number: string
  state: string
  steps: ApprovalStep[]
  publishedPath?: string
  publishError?: string
}

export interface AuditEntry {
  id: string
  occurredAt: string
  actor: string
  action: string
  entityType: string
  entityId: string
  detail: string
}

export interface DocumentVersionSummary {
  id: string
  documentId: string
  revision: { display: string }
  status: 'Work' | 'Released' | 0 | 1
  fileLength: number
  sha256: string
  createdBy: string
  createdAt: string
  changeNote: string
  sourceDescription?: string
  releasePackageId?: string
}

export interface VersionChange {
  kind: string | number
  name?: string
  instancePath?: string
  drawingNumber?: string
  field?: string
  previousValue?: string | null
  currentValue?: string | null
}

export interface DocumentVersionComparison {
  documentId: string
  left: DocumentVersionSummary
  right: DocumentVersionSummary
  propertyChanges: VersionChange[]
  referenceChanges: VersionChange[]
  bomChanges: VersionChange[]
}
