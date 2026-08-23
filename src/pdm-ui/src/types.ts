export type DocumentKind = 'Assembly' | 'Part' | 'Drawing'
export type ReferenceStatus = 'Normal' | 'Suppressed' | 'Hidden' | 'Lightweight' | 'Virtual' | 'Missing' | 'Unregistered' | 'Unarchived'
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
  rootProjectId?: string
  childSequence?: number
  bomItemCategoryCode?: '0301' | '0302'
  serialNumbers: string[]
  responsibleUsers: string[]
  executionUnitId?: string
  executionUnitName?: string
  primaryProjectManager?: string
  collaborativeProjectManagers: string[]
  designLead?: string
  designers: string[]
  documentCount?: number
  modelDocumentCount?: number
  drawingDocumentCount?: number
  businessStatus?: string
  rootDocumentCheckedOutBy?: string
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
  bomItemCategoryCode: '0301' | '0302'
}

export interface CreateSubprojectInput {
  name: string
  projectAlias?: string
  quantity: number
  equipmentTypeCode?: number
}

export interface UpdateProjectInput {
  organizationId?: string
  projectTypeCode?: string
  equipmentTypeCode?: number
  customerId?: string
  name: string
  projectAlias?: string
  signedDate: string
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

export interface ProjectTypeDefinition { code: string; name: string; isActive?: boolean }
export interface EquipmentTypeDefinition { code: number; name: string; isActive?: boolean }
export interface ProjectNumberingOptions {
  organizations: ProjectOrganization[]
  projectTypes: ProjectTypeDefinition[]
  equipmentTypes: EquipmentTypeDefinition[]
}
export interface PdmCustomer { id: string; code: string; name: string; isActive: boolean; sourceSystem?: string; lastSyncedAt?: string | null }
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
export interface PdmUser {
  username: string
  displayName: string
  role: string
  isActive: boolean
  companyId?: string | null
  crossCompanyView?: boolean
  accessibleCompanyIds?: string[]
}
export interface SavePdmUserInput {
  username: string
  displayName: string
  role: string
  isActive: boolean
  companyId: string
  crossCompanyView: boolean
  accessibleCompanyIds: string[]
  password?: string
}
export interface PdmUserProfile {
  username: string
  displayName: string
  nickname?: string | null
  gender: 'male' | 'female' | 'unspecified'
  landline?: string | null
  mobilePhone?: string | null
  email?: string | null
}
export interface PasswordResetTask { id: string; username: string; displayName: string; requestedAt: string }
export interface BomPropertyMapping {
  pdmPropertyKey: string
  pdmPropertyName: string
  solidWorksProperty: string
  source: 'SolidWorks' | 'Assembly' | 'Pdm'
  mappingEditable: boolean
}
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
  bomDrawingNumberProperty: string
  bomNameProperty: string
  bomDescriptionProperty: string
  bomMaterialProperty: string
  bomSpecificationProperty: string
  bomUnitProperty: string
  bomBrandProperty: string
  bomSurfaceTreatmentProperty: string
  bomWeightProperty: string
  bomPropertyMappings: BomPropertyMapping[]
  validationRules: BomValidationRules
  approvalWorkflows?: ReleaseApprovalSettings
  materialCodeApproval?: MaterialCodeApprovalSettings
  releaseChangeReasonTypes?: string[]
}
export interface MaterialCodeApprovalSettings { version: number; approverRoleCodes: string[] }
export type ApprovalStage = 'ProcessReview' | 'Approval' | 'MechanicalEngineer' | 'MainDesigner' | 'MechanicalSupervisor' | 'HardwareEngineer' | 'HardwareSupervisor' | 'StandardizationSupervisor'
export type ApprovalAssigneeSource = 'Submitter' | 'ProjectDesignLead' | 'FixedUser' | 'PrimaryUnitManager' | 'ParentUnitManager'
export interface ApprovalWorkflowStepTemplate { stage: ApprovalStage; name: string; assigneeSource: ApprovalAssigneeSource; fixedAssignee?: string | null }
export interface ApprovalWorkflowTemplate { code: string; name: string; version: number; steps: ApprovalWorkflowStepTemplate[] }
export interface ReleaseApprovalSettings { mechanical: ApprovalWorkflowTemplate; electrical: ApprovalWorkflowTemplate; emergencySubstituteRoleCode: string }
export type BomValidationField = 'drawingNumber' | 'name' | 'unit' | 'specification' | 'brand' | 'material' | 'surfaceTreatment' | 'weight' | 'quantity' | 'revision' | 'remark'
export interface BomValidationRules {
  standard: BomValidationField[]
  nonStandard: BomValidationField[]
  electrical: BomValidationField[]
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
  baseRole: string
  isSystem: boolean
  isSystemAdministrator: boolean
  permissions: string[]
  userCount: number
}

export interface RolePermissionDirectory {
  permissions: PermissionDefinition[]
  roles: RolePermissionSettings[]
}
export interface CreateRoleInput { name: string; description: string; sourceRoleCode: string }
export interface SaveProjectOrganizationInput { id?: string; name: string; projectCompanyCode: string; modelCompanyCode: string; isActive: boolean }
export interface SaveOrganizationUnitInput { id?: string; organizationId: string; parentUnitId?: string; code: string; name: string; kind: OrganizationUnitKind; isActive: boolean; sortOrder: number }
export interface MainProjectStaffingInput { primaryProjectManager: string; collaborativeProjectManagers: string[]; designLead: string }
export interface DocumentNode {
  /** Unique assembly occurrence. Tree selection and rendering must use this value. */
  id: string
  /** PLM document identity. Different occurrences of one part intentionally share this value. */
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
  storedVersionCount?: number
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
  id?: string
  kind?: BomKind
  sequence: number
  drawingNumber: string
  name: string
  quantity: number
  unit: string
  material?: string
  specification?: string
  remark?: string
  brand?: string
  surfaceTreatment?: string
  weight?: string
  revision: string
  complete: boolean
  sourceDocumentId?: string
  sourceConfiguration?: string
  sourceInstancePath?: string
  parentDrawingNumber?: string
  source?: 'Auto' | 'Manual'
  manuallyOverridden?: boolean
  pendingRemoval?: boolean
  pendingClassification?: boolean
  manualUnmatched?: boolean
  manuallyRetained?: boolean
  manuallyExcluded?: boolean
  reconciliationStatus?: string
  reconciliationNote?: string
  reconciliationUpdatedBy?: string
  reconciliationUpdatedAt?: string
  deletedAt?: string
  deletedBy?: string
  deleteReason?: string
  propertyWritebackStatus?: 'PendingSave' | 'Pending' | 'InProgress' | 'Succeeded' | 'Conflict' | 'Failed' | 'Superseded'
}

export type BomKind = 'Standard' | 'NonStandard' | 'Unclassified' | 'Electrical'
export interface BatchUpdateBomItemsInput {
  itemIds: string[]
  fields: string[]
  targetKind?: BomKind
  unit?: string
  drawingNumber?: string
  name?: string
  specification?: string
  remark?: string
  brand?: string
  material?: string
  surfaceTreatment?: string
  weight?: string
  quantity?: number
  revision?: string
  parentDrawingNumber?: string
  complete?: boolean
}
export interface BomEmptyDeclaration { kind: BomKind; declaredEmpty: boolean; updatedBy?: string; updatedAt?: string }
export interface BomGenerationResult {
  standardItems: BomItem[]
  nonStandardItems: BomItem[]
  electricalItems: BomItem[]
  unclassifiedItems: BomItem[]
  virtualCount: number
  unclassifiedCount: number
  pendingRemovalCount: number
  manualUnmatchedCount: number
  applied: boolean
}

export type BomVersionState = 'Draft' | 'InReview' | 'Released' | 'Obsolete'
export interface BomVersion {
  id: string
  projectId: string
  kind: Exclude<BomKind, 'Unclassified'>
  versionNumber: number
  label: string
  state: BomVersionState
  baseVersionId?: string
  changeNumber?: string
  changeReason?: string
  effectiveSerialFrom?: string
  effectiveSerialTo?: string
  items: BomItem[]
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  releasedAt?: string
  validationRequiredFields?: BomValidationField[]
}

export interface ManufacturingBomBaseline {
  id: string
  projectId: string
  sequence: number
  label: string
  standardBomVersionId: string
  nonStandardBomVersionId: string
  electricalBomVersionId: string
  changeNumber: string
  changeReason: string
  effectiveSerialFrom: string
  effectiveSerialTo?: string
  releasePackageId: string
  createdBy: string
  createdAt: string
}

export interface ApprovalStep {
  id: string
  stage: string
  assignee: string
  status: 'done' | 'current' | 'waiting'
  detail: string
  decision?: string | number
  comment?: string
  stepOrder?: number
  emergencySubstitute?: boolean
  emergencyReason?: string
}

export type ReleaseScope = 'LegacyCombined' | 'StandardLongLead' | 'StandardFormal' | 'StandardSupplement' | 'ElectricalFormal' | 'ElectricalSupplement' | 'NonStandardWithDrawing'

export interface CreateReleasePackageInput {
  changeReason: string
  scope: Exclude<ReleaseScope, 'LegacyCombined'>
  selectedBomItemIds: string[]
}

export interface ReleasePackageSummary {
  id: string
  number: string
  state: string
  steps: ApprovalStep[]
  publishedPath?: string
  publishError?: string
  changeNumber?: string
  changeReason?: string
  effectiveSerialFrom?: string
  effectiveSerialTo?: string
  standardBomRevision?: string
  nonStandardBomRevision?: string
  electricalBomRevision?: string
  standardBomVersionId?: string
  nonStandardBomVersionId?: string
  electricalBomVersionId?: string
  standardBomSnapshot: BomItem[]
  nonStandardBomSnapshot: BomItem[]
  electricalBomSnapshot: BomItem[]
  createdAt?: string
  publishedAt?: string
  scope: ReleaseScope
  workflowCode?: string
  workflowVersion: number
  selectedBomItemIds: string[]
  createsManufacturingBaseline: boolean
  locksDocuments: boolean
}

export type DrawingReviewPackageState = 'InReview' | 'ChangesRequested' | 'WritingProperties' | 'Approved' | 'Stale'
export type DrawingReviewTarget = 'Model3D' | 'Drawing2D'
export type DrawingReviewTargetState = 'Pending' | 'ChangesRequested' | 'Approved' | 'Marked'
export type DrawingReviewDecision = 'Approve' | 'RequestChanges'
export type DrawingReviewMarkupSeverity = 'Note' | 'Blocking'
export type DrawingReviewMarkupState = 'Open' | 'Resolved'

export type DrawingReviewBadgeTone = 'neutral' | 'pending' | 'warning' | 'success' | 'danger'

export interface DrawingReviewBadge {
  label: string
  tone: DrawingReviewBadgeTone
}

export interface DrawingReviewMarkup {
  id: string
  packageId: string
  itemId: string
  target: DrawingReviewTarget
  viewName?: string | null
  normalizedX?: number | null
  normalizedY?: number | null
  text: string
  severity: DrawingReviewMarkupSeverity
  state: DrawingReviewMarkupState
  createdBy: string
  createdAt: string
  resolvedBy?: string | null
  resolvedAt?: string | null
}

export interface DrawingReviewItem {
  id: string
  packageId: string
  bomItemId: string
  drawingNumber: string
  name: string
  configuration?: string | null
  modelDocumentId: string
  modelVersionId: string
  modelRevision: string
  modelSha256: string
  modelCreatedBy: string
  drawingDocumentId: string
  drawingVersionId: string
  drawingRevision: string
  drawingSha256: string
  drawingCreatedBy: string
  modelState: DrawingReviewTargetState
  modelReviewer?: string | null
  modelReviewerName?: string | null
  modelReviewedAt?: string | null
  modelComment?: string | null
  drawingState: DrawingReviewTargetState
  drawingReviewer?: string | null
  drawingReviewerName?: string | null
  drawingReviewedAt?: string | null
  drawingComment?: string | null
  modelWritebackId?: string | null
  drawingWritebackId?: string | null
  modelResultVersionId?: string | null
  drawingResultVersionId?: string | null
  effectiveModelVersionId: string
  effectiveDrawingVersionId: string
}

export interface DrawingReviewPackage {
  id: string
  projectId: string
  number: string
  state: DrawingReviewPackageState
  createdBy: string
  createdAt: string
  approvedAt?: string | null
  items: DrawingReviewItem[]
  markups: DrawingReviewMarkup[]
}

export interface AddDrawingReviewMarkupInput {
  itemId: string
  target: DrawingReviewTarget
  viewName?: string
  normalizedX?: number
  normalizedY?: number
  text: string
  severity: DrawingReviewMarkupSeverity
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
  preview?: {
    format: 'Step' | 'Pdf' | 0 | 1
    storageRelativePath: string
    fileLength: number
    sha256: string
    sourceSha256: string
  } | null
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

export type MaterialKind = 'Electrical' | 'Standard' | 'NonStandard' | 'Product'
export type MaterialSupplyMode = 'Purchase' | 'Manufacture' | 'Outsource'
export type MaterialApprovalStatus = 'Draft' | 'Approved'
export type MaterialSyncStatus = 'NotQueued' | 'PreviewReady' | 'Pending' | 'Succeeded' | 'Failed' | 'NeedsReview' | 'Superseded'
export type MaterialDataSource = 'Pdm' | 'U9C'
export type MaterialMasterOwner = 'Pdm' | 'U9C'

export interface PdmMaterial {
  id: string
  materialCode: string
  name: string
  kind: MaterialKind
  supplyMode: MaterialSupplyMode
  unitCode: string
  specification?: string | null
  material?: string | null
  remark?: string | null
  brand?: string | null
  surfaceTreatment?: string | null
  purchaseLink?: string | null
  weight?: number | null
  weightUnit?: string | null
  sourceBomItemId?: string | null
  approvalStatus: MaterialApprovalStatus
  approvedBy?: string | null
  approvedAt?: string | null
  u9CategoryCode?: string | null
  u9ItemId?: string | null
  u9ItemCode?: string | null
  syncStatus: MaterialSyncStatus
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion: number
  categoryCode?: string | null
  isArchived: boolean
  archivedBy?: string | null
  archivedAt?: string | null
  u9SyncConfirmed: boolean
  sourceSystem: MaterialDataSource
  masterOwner: MaterialMasterOwner
  lastU9SyncedAt?: string | null
  referenceCount: number
}

export type BomHeaderKind = 'Master' | 'Standard' | 'NonStandard' | 'Electrical'

export interface ProjectBomHeader {
  projectId: string
  kind: BomHeaderKind
  parentKind?: BomHeaderKind | null
  materialId?: string | null
  materialCode?: string | null
  materialName?: string | null
  categoryCode?: string | null
  approvalStatus?: MaterialApprovalStatus | null
  rowVersion: number
}

export type MaterialCodeApplicationStatus = 'Pending' | 'Approved' | 'Rejected'
export interface MaterialCodeApplication {
  id: string
  projectId: string
  bomItemId: string
  status: MaterialCodeApplicationStatus
  requestedBy: string
  requestedAt: string
  decidedBy?: string | null
  decidedAt?: string | null
  decisionComment?: string | null
  materialId?: string | null
  materialCode?: string | null
  rowVersion: number
  bomItemName?: string | null
  specification?: string | null
  brand?: string | null
  remark?: string | null
}
export type MaterialCodeResolutionStatus = 'Matched' | 'NoMatch' | 'Ambiguous' | 'ApplicationPending' | 'ApplicationApproved'
export interface MaterialCodeResolution {
  bomItemId: string
  status: MaterialCodeResolutionStatus
  material?: PdmMaterial | null
  candidates: PdmMaterial[]
  application?: MaterialCodeApplication | null
}

export interface SaveMaterialInput {
  materialCode: string
  name: string
  kind: MaterialKind
  supplyMode: MaterialSupplyMode
  unitCode: string
  specification?: string | null
  material?: string | null
  remark?: string | null
  brand?: string | null
  surfaceTreatment?: string | null
  purchaseLink?: string | null
  weight?: number | null
  weightUnit?: string | null
  expectedRowVersion?: number | null
  categoryCode?: string | null
}

export interface MaterialCategory {
  code: string
  name: string
  parentCode?: string | null
  u9CategoryId?: string | null
  pdmKind?: MaterialKind | null
  defaultSupplyMode: MaterialSupplyMode
  allowCreate: boolean
  isVisible: boolean
  isActive: boolean
  numberPrefix: string
  sequenceLength: number
  counterScope: string
  sortOrder: number
  updatedBy: string
  updatedAt: string
  rowVersion: number
  currentSequence: number
}

export interface MaterialRemovalResult {
  material: PdmMaterial
  deleted: boolean
  archived: boolean
}

export interface MaterialRemovalReadiness {
  materialId: string
  materialCode: string
  pdmReferenceCount: number
  isPdmMaster: boolean
  localDeletePreconditionsPassed: boolean
  u9ReferenceCheckAvailable: boolean
  synchronizedDeleteAvailable: boolean
  decision: string
}

export interface MaterialCategoryRule {
  pdmKind: MaterialKind
  u9CategoryCode: string
  u9CategoryName: string
  defaultSupplyMode: MaterialSupplyMode
  isEnabled: boolean
  updatedBy: string
  updatedAt: string
}

export interface MaterialSyncTask {
  id: string
  materialId: string
  operation: 'Create' | 'Update'
  status: MaterialSyncStatus
  correlationId: string
  payloadJson: string
  payloadSha256: string
  attemptCount: number
  nextAttemptAt?: string | null
  lastError?: string | null
  responsePreview?: string | null
  u9ItemId?: string | null
  u9ItemCode?: string | null
  createdAt: string
  updatedAt: string
}

export interface MaterialSyncExecutionResult {
  material: PdmMaterial
  task: MaterialSyncTask
  created: boolean
  alreadyExisted: boolean
  updated: boolean
}

export interface U9MaterialIntegrationSettings {
  baseUrl: string
  enterpriseCode: string
  organizationCode: string
  userCode: string
  clientId: string
  clientSecretConfigured: boolean
  itemCreatePath: string
  itemQueryPath: string
  itemModifyPath: string
  itemDeletePath: string
  customerQueryPath: string
  bomCreatePath: string
  bomQueryPath: string
  bomModifyPath: string
  bomDeletePath: string
  bomBatchUnapprovePath: string
  bomBipQueryPagePath: string
  unitCodeMappings: Record<string, string>
  writeEnabled: boolean
  updatedBy?: string | null
  updatedAt?: string | null
}

export interface U9ConnectionTestResult {
  baseUrl: string
  enterpriseCode: string
  organizationCode: string
  userCode: string
  clientId: string
  testedAt: string
}

export interface U9ItemQueryResult {
  responseCode: number
  responseMessage?: string | null
  items: Array<{
    u9ItemId?: string | null
    u9ItemCode?: string | null
    u9ItemName?: string | null
    u9Specification?: string | null
    u9CategoryCode?: string | null
    u9CategoryName?: string | null
    u9UnitCode?: string | null
    u9ItemFormAttribute?: number | null
    u9Brand?: string | null
    u9Description?: string | null
    u9Material?: string | null
    u9SurfaceTreatment?: string | null
    u9Weight?: number | null
    u9WeightUnitCode?: string | null
    u9PurchaseLink?: string | null
  }>
}

export interface U9BomComponentReference {
  sequence?: number | null
  itemId?: string | null
  itemCode?: string | null
  itemName?: string | null
  itemVersionCode?: string | null
  usageQty?: number | null
  issueUomCode?: string | null
  issueUomName?: string | null
  parentQty?: number | null
  componentType?: number | null
  isEffective?: boolean | null
  effectiveDate?: string | null
  disableDate?: string | null
  remark?: string | null
  projectMapNum?: string | null
  issueStyle?: number | null
  supplyStyle?: number | null
  isPhantomPart?: boolean | null
  isDelete?: boolean | null
}

export interface U9BomReference {
  itemId?: string | null
  itemCode?: string | null
  itemName?: string | null
  bomVersionCode?: string | null
  organizationCode?: string | null
  organizationName?: string | null
  alternateType?: number | null
  lot?: number | null
  productUomCode?: string | null
  productUomName?: string | null
  effectiveDate?: string | null
  disableDate?: string | null
  status?: number | null
  bomSort?: number | null
  bomType?: number | null
  projectMapNum?: string | null
  explain?: string | null
  ecoCode?: string | null
  isCostRoll?: boolean | null
  itemSource?: number | null
  sysState?: number | null
  components: U9BomComponentReference[]
  otherId?: string | null
}

export interface U9BomQueryInput {
  itemCode: string
  bomVersionCode?: string | null
  lot?: number | null
  productUomCode?: string | null
}

export interface U9BomQueryExecution {
  queryPath: string
  requestPreview: string
  queriedAt: string
  result: {
    responseCode: number
    responseMessage?: string | null
    boms: U9BomReference[]
  }
}

export type U9BomWriteOperation = 0 | 1 | 2

export interface U9BomComponentInput {
  sequence: number
  itemCode: string
  usageQty: number
  issueUomCode: string
  parentQty: number
  itemVersionCode?: string | null
  isEffective?: boolean
  effectiveDate?: string | null
  disableDate?: string | null
  remark?: string | null
  componentType?: number
  issueStyle?: number
  supplyStyle?: number
  isPhantomPart?: boolean
  isDelete?: boolean
}

export interface U9BomWriteInput {
  operation: U9BomWriteOperation
  itemCode: string
  bomVersionCode: string
  productUomCode: string
  lot: number
  components: U9BomComponentInput[]
  effectiveDate?: string | null
  disableDate?: string | null
  bomSort?: number
  bomType?: number
  projectMapNum?: string | null
  explain?: string | null
}

export interface U9BomWritePreview {
  operation: U9BomWriteOperation
  path: string
  requestPreview: string
  requestSha256: string
  baselineSha256: string
  requiredConfirmation: string
  addedComponentCount: number
  retainedHistoricalComponentCount: number
  generatedAt: string
}

export interface U9BomWriteExecution {
  preview: U9BomWritePreview
  writeResult: { responseCode: number; responseMessage?: string | null; rows: Array<{ isSuccess: boolean; errorMessage?: string | null }> }
  verification: { responseCode: number; responseMessage?: string | null; boms: U9BomReference[] }
  executedAt: string
}

export interface U9MaterialSampleItem {
  u9ItemId: string
  materialCode: string
  name: string
  categoryCode: string
  categoryName: string
  kind: MaterialKind
  supplyMode: MaterialSupplyMode
  unitCode: string
  specification?: string | null
  brand?: string | null
  material?: string | null
  surfaceTreatment?: string | null
  remark?: string | null
  weight?: number | null
  weightUnit?: string | null
  purchaseLink?: string | null
  existsInPdm: boolean
  canImport: boolean
  decision: string
}

export interface U9MaterialSamplePreview {
  categoryCodes: string[]
  limitPerCategory: number
  items: U9MaterialSampleItem[]
  queriedAt: string
}

export interface U9MaterialSampleImportResult {
  preview: U9MaterialSamplePreview
  createdCount: number
  refreshedCount: number
  skippedCount: number
  materials: PdmMaterial[]
  importedAt: string
}

export interface UpdateU9MaterialIntegrationInput {
  baseUrl: string
  enterpriseCode: string
  organizationCode: string
  userCode: string
  clientId: string
  clientSecret?: string | null
  itemCreatePath: string
  itemQueryPath: string
  itemModifyPath: string
  itemDeletePath: string
  customerQueryPath: string
  bomCreatePath: string
  bomQueryPath: string
  bomModifyPath: string
  bomDeletePath: string
  bomBatchUnapprovePath: string
  bomBipQueryPagePath: string
  unitCodeMappings: Record<string, string>
  writeEnabled: boolean
}
