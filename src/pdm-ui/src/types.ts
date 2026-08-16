export type DocumentKind = 'Assembly' | 'Part' | 'Drawing'
export type ReferenceStatus = 'Normal' | 'Suppressed' | 'Hidden' | 'Lightweight' | 'Virtual' | 'Missing' | 'Unregistered'
export type DocumentFilter = 'all' | 'model' | 'drawing' | 'issue'
export type VersionAlignmentStatus = 'Synced' | 'StructureStale' | 'VersionConflict' | 'NotSnapshotted'
export type PreviewMode = 'model' | 'drawing'
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
  executionUnitId?: string
  executionUnitName?: string
  primaryProjectManager?: string
  collaborativeProjectManagers: string[]
  designLead?: string
  designers: string[]
  documentCount?: number
  businessStatus?: string
  canAssignExecutionUnit: boolean
  canManageMainStaffing: boolean
  canAssignDesigners: boolean
  canReadContent: boolean
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
  isActive?: boolean
}

export interface ProjectTypeDefinition { code: string; name: string }
export interface EquipmentTypeDefinition { code: number; name: string; isActive?: boolean }
export interface ProjectNumberingOptions {
  organizations: ProjectOrganization[]
  projectTypes: ProjectTypeDefinition[]
  equipmentTypes: EquipmentTypeDefinition[]
}
export interface PdmCustomer { id: string; code: string; name: string; isActive: boolean }
export interface CrmIntegrationSettings {
  baseUrl: string
  username: string
  passwordConfigured: boolean
  autoSyncEnabled: boolean
  autoSyncIntervalMinutes: number
  lastSyncAt?: string | null
  lastSyncCount: number
  lastAutoSyncAttemptAt?: string | null
  lastAutoSyncError?: string | null
}
export interface UpdateCrmIntegrationInput { baseUrl: string; username: string; password?: string; autoSyncEnabled: boolean; autoSyncIntervalMinutes: number }
export interface CrmConnectionTestResult { customerCount: number; skippedCount: number; testedAt: string }
export interface CrmCustomerSyncResult {
  customerCount: number
  skippedCount: number
  syncedAt: string
  settings: CrmIntegrationSettings
  customers: PdmCustomer[]
}
export interface PdmUser { username: string; displayName: string; role: string; isActive: boolean }
export interface PdmSystemSettings {
  vaultRoot: string
  releaseRoot: string
  checkoutHeartbeatSeconds: number
  checkoutLeaseMinutes: number
  checkoutOfflineGraceMinutes: number
  checkoutReminderHours: number
  checkoutStrongReminderHours: number
  checkoutOverdueHours: number
  checkoutForceReleaseHours: number
}
export type OrganizationUnitKind = 'BusinessDivision' | 'Department' | 'Team'
export interface OrganizationUnit {
  id: string
  organizationId: string
  parentUnitId?: string
  code: string
  name: string
  kind: OrganizationUnitKind
  isActive: boolean
  sortOrder: number
}
export interface OrganizationMembership { unitId: string; username: string; isPrimary: boolean }
export interface OrganizationUnitManagers { unitId: string; primaryManager: string; collaborativeManagers: string[] }
export interface OrganizationDirectory {
  organizations: ProjectOrganization[]
  units: OrganizationUnit[]
  memberships: OrganizationMembership[]
  managers: OrganizationUnitManagers[]
  users: PdmUser[]
}

export interface PermissionDefinition {
  code: string
  name: string
  module: string
  description?: string | null
  sensitive: boolean
}

export interface RolePermissionSettings {
  role: string
  name: string
  description: string
  isSystemAdministrator: boolean
  permissions: string[]
}

export interface RolePermissionDirectory {
  permissions: PermissionDefinition[]
  roles: RolePermissionSettings[]
}
export interface SaveProjectOrganizationInput { id?: string; name: string; projectCompanyCode: string; modelCompanyCode: string; isActive: boolean }
export interface SaveOrganizationUnitInput { id?: string; organizationId: string; parentUnitId?: string; code: string; name: string; kind: OrganizationUnitKind; isActive: boolean; sortOrder: number }
export interface MainProjectStaffingInput { primaryProjectManager: string; collaborativeProjectManagers: string[]; designLead: string }
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
  /** Latest immutable document revision from the document record. */
  version: string
  /** Revision actually used by the structure; for the root this is the selected root version. */
  snapshotVersion?: string
  versionAlignment?: VersionAlignmentStatus
  checkedOutBy?: string
  lifecycleState?: string | number
  status: ReferenceStatus
  children: DocumentNode[]
}

export interface DocumentWhereUsed {
  documentId: string
  parentDocumentId: string
  projectId: string
  projectCode: string
  projectName: string
  parentDrawingNumber: string
  parentName: string
  parentFileName: string
  parentKind: string | number
  parentState: string | number
  parentRevision: { display: string }
  instancePath: string
  configuration: string
  quantity: number
}

export interface DocumentModelDrawingRelation {
  modelDocumentId: string
  drawingDocumentId: string
}

export type ProjectFolderPurpose = 'Root' | 'MechanicalRoot' | 'ElectricalRoot' | 'ProjectContainer' | 'Release' | 'Standard'
export type FolderPrincipalType = 'Role' | 'User'
export interface FolderPermissionRule { id?: string; principalType: FolderPrincipalType; principalKey: string; access: number }
export interface ProjectFolder {
  id: string
  rootProjectId: string
  parentFolderId?: string
  targetProjectId?: string
  folderKey: string
  templateKey: string
  name: string
  purpose: ProjectFolderPurpose
  sortOrder: number
  isSystem: boolean
  inheritPermissions: boolean
  effectiveAccess: number
  permissions: FolderPermissionRule[]
}
export interface ProjectFolderTemplateNode {
  folderKey: string
  parentKey?: string
  name: string
  purpose: ProjectFolderPurpose
  sortOrder: number
  isSystem: boolean
  inheritPermissions: boolean
  permissions: FolderPermissionRule[]
}
export interface ManagedDocument {
  id: string
  projectId: string
  folderId?: string
  drawingNumber: string
  name: string
  fileName: string
  kind: DocumentKind
  state: string | number
  revision: string
  checkedOutBy?: string
  checkedOutAt?: string
  checkoutMachine?: string
  checkoutLastHeartbeatAt?: string
  checkoutLeaseExpiresAt?: string
  checkoutReleaseRequestedBy?: string
  checkoutReleaseRequestedAt?: string
  updatedAt?: string
}

export interface EditLockSummary {
  documentId: string
  projectId: string
  projectCode: string
  projectName: string
  drawingNumber: string
  documentName: string
  fileName: string
  checkedOutBy: string
  checkedOutAt: string
  checkoutMachine?: string
  lastHeartbeatAt: string
  leaseExpiresAt: string
  connectionState: 'Active' | 'OfflineGrace' | 'Offline' | 0 | 1 | 2
  attentionLevel: 'Normal' | 'Reminder' | 'StrongReminder' | 'Overdue' | 'Reclaimable' | 0 | 1 | 2 | 3 | 4
  releaseRequestedBy?: string
  releaseRequestedAt?: string
  releaseRequestReason?: string
  ownedByCurrentUser: boolean
  canRequestRelease: boolean
  canForceRelease: boolean
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

export interface MyApprovalTask {
  id: string
  projectId: string
  projectCode: string
  projectName: string
  releasePackageId: string
  releasePackageNumber: string
  stage: string | number
  packageState: string | number
  createdAt: string
}

export interface ProjectVersionItem {
  id: string
  documentId: string
  drawingNumber: string
  documentName: string
  fileName: string
  revision: { display: string }
  status: 'Work' | 'Released' | 0 | 1
  createdBy: string
  createdAt: string
  changeNote: string
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
