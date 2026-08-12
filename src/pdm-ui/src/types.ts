export type DocumentKind = 'Assembly' | 'Part' | 'Drawing'
export type ReferenceStatus = 'Normal' | 'Suppressed' | 'Hidden' | 'Lightweight' | 'Virtual' | 'Missing'
export type PreviewMode = 'model' | 'drawing' | 'bom'

export interface ProjectSummary {
  id: string
  code: string
  name: string
  owner: string
  stage: string
  vaultName: string
  vaultLocation: string
  releaseLocation: string
}

export interface DocumentNode {
  id: string
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
  stage: string
  assignee: string
  status: 'done' | 'current' | 'waiting'
  detail: string
}

export interface ReleasePackageSummary {
  id: string
  number: string
  state: string
  steps: ApprovalStep[]
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
