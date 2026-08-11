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
