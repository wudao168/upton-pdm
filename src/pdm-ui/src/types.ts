export type DocumentKind = 'Assembly' | 'Part' | 'Drawing'
export type ReferenceStatus = 'Normal' | 'Suppressed' | 'Hidden' | 'Lightweight' | 'Virtual' | 'Missing' | 'Unregistered' | 'Unarchived'
export type DocumentFilter = 'all' | 'model' | 'drawing' | 'issue'
export type VersionAlignmentStatus = 'Synced' | 'StructureStale' | 'VersionConflict' | 'NotSnapshotted'
export type PreviewMode = 'model' | 'drawing'
export type SolidWorksOpenMode = 'LatestReadOnly' | 'LatestReleased' | 'LatestEdit' | 'SpecificReadOnly' | 'PropertyWriteback'

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
  designLeads?: string[]
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

export interface ProjectCopyOptionsInput {
  sourceProjectId: string
  copyModels: boolean
  copyDrawings: boolean
  copyBom: boolean
  copyValidationItems: boolean
  folderIds: string[] | null
}

export interface ProjectCopyFolderOption {
  id: string
  name: string
  path: string
  templateKey: string
  fileCount: number
  totalBytes: number
  defaultSelected: boolean
}

export interface ProjectCopyPreview {
  sourceProjectId: string
  targetProjectId: string
  modelCount: number
  drawingCount: number
  bomItemCount: number
  validationItemCount: number
  projectFileCount: number
  totalBytes: number
  folders: ProjectCopyFolderOption[]
  blockingReasons: string[]
  warnings: string[]
  canExecute: boolean
}

export interface ProjectCopyResult {
  sourceProjectId: string
  targetProjectId: string
  documentCount: number
  bomItemCount: number
  validationItemCount: number
  projectFileCount: number
  totalBytes: number
}

export type ProjectPlanStage = string
export interface ProjectPlanStageDefinition {
  code: string; name: string
  participatesInDelivery?: boolean | null
  durationRatio?: number
  progressRatio?: number
  independentDurationDays?: number
}
export type ProjectPlanTaskStatus = 'NotStarted' | 'InProgress' | 'Completed'

export interface ProjectPlanTemplateTask {
  startOffsetDays?: number
  fixedDurationDays?: number | null
  id: string
  name: string
  stage: ProjectPlanStage
  durationRatio: number
  predecessorSortOrders: number[]
  defaultAssigneeRole?: string
  weight: number
  isMilestone: boolean
  isRequired: boolean
  sortOrder: number
}

export interface ProjectPlanTemplate {
  stages?: ProjectPlanStageDefinition[]
  id: string
  name: string
  projectTypeCode?: string
  isActive: boolean
  tasks: ProjectPlanTemplateTask[]
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion?: number
}

export interface ProjectPlanTask {
  templateTaskId?: string | null
  sourceTaskId?: string | null
  id: string
  name: string
  stage: ProjectPlanStage
  assignee?: string
  durationDays: number
  plannedStart: string
  plannedFinish: string
  baselineStart?: string
  baselineFinish?: string
  actualStart?: string
  actualFinish?: string
  completionPercent: number
  status: ProjectPlanTaskStatus
  predecessorTaskIds: string[]
  weight: number
  isMilestone: boolean
  isRequired: boolean
  sortOrder: number
}

export interface ProjectPlan {
  changeRequest?: {
    id: string; tasks: Array<{ taskId: string; plannedStart: string; plannedFinish: string; assignee?: string | null }>
    reason: string; submittedBy: string; submittedAt: string; approvalAssignee: string
    status: 'Pending' | 'Approved' | 'Rejected'; comment?: string; decidedBy?: string; decidedAt?: string
  } | null
  changeDraftSource?: ProjectPlan | null
  followsParentPlan?: boolean
  parentPlanId?: string | null
  parentPlanRowVersion?: number | null
  childSyncResults?: Array<{ projectId: string; projectCode: string; result: string; differences: string[] }>
  stageSchedules?: Array<{ stage: string; startDate: string; durationDays: number }>
  stages?: ProjectPlanStageDefinition[]
  approvalStatus?: 'Draft' | 'Pending' | 'Rejected' | 'Approved'
  approvalAssignee?: string
  submittedBy?: string
  submittedAt?: string
  approvedBy?: string
  approvedAt?: string
  approvalComment?: string
  id: string
  projectId: string
  templateId: string
  templateName: string
  currentStage: ProjectPlanStage
  manualStage?: ProjectPlanStage
  manualStageReason?: string
  plannedStart: string
  plannedFinish: string
  forecastFinish: string
  baselineVersion: number
  tasks: ProjectPlanTask[]
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion: number
}

export interface ProjectPlanVersion {
  id: string
  planId: string
  versionNumber: number
  changeReason: string
  snapshot: ProjectPlan
  createdBy: string
  createdAt: string
}

export interface ProjectPlanPortfolioItem {
  projectId: string
  projectCode: string
  projectName: string
  isRoot: boolean
  hasPlan: boolean
  currentStage?: ProjectPlanStage
  completionPercent: number
  plannedStart?: string
  plannedFinish?: string
  forecastFinish?: string
  isLagging: boolean
  isAtRisk: boolean
  plan?: ProjectPlan
}

export interface ProjectPlanPortfolio {
  rootProjectId: string
  currentStage: ProjectPlanStage
  completionPercent: number
  laggingProjectCount: number
  riskProjectCount: number
  plannedStart?: string
  plannedFinish?: string
  projects: ProjectPlanPortfolioItem[]
}

export interface SaveProjectPlanTemplateInput {
  stages?: ProjectPlanStageDefinition[]
  name: string
  projectTypeCode?: string
  isActive: boolean
  tasks: ProjectPlanTemplateTask[]
  expectedRowVersion?: number
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
  roles?: string[]
  isActive: boolean
  companyId?: string | null
  crossCompanyView?: boolean
  accessibleCompanyIds?: string[]
}
export interface SavePdmUserInput {
  username: string
  displayName: string
  role: string
  roles: string[]
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
  materialAttachmentRoot: string
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
  formalSupplementPolicies?: FormalSupplementPolicies
}
export interface MaterialCodeApprovalSettings { version: number; approverRoleCodes: string[] }
export interface FormalSupplementPolicy { maximumCount?: number | null; validDays?: number | null }
export interface FormalSupplementPolicies { standard: FormalSupplementPolicy; electrical: FormalSupplementPolicy }
export type ApprovalStage = 'ProcessReview' | 'Approval' | 'MechanicalEngineer' | 'MainDesigner' | 'MechanicalSupervisor' | 'HardwareEngineer' | 'HardwareSupervisor' | 'StandardizationSupervisor'
export type ApprovalAssigneeSource = 'Submitter' | 'ProjectDesignLead' | 'FixedUser' | 'PrimaryUnitManager' | 'ParentUnitManager'
export interface ApprovalWorkflowStepTemplate { stage: ApprovalStage; name: string; assigneeSource: ApprovalAssigneeSource; fixedAssignee?: string | null }
export interface ApprovalWorkflowTemplate { code: string; name: string; version: number; steps: ApprovalWorkflowStepTemplate[] }
export interface ReleaseApprovalSettings { mechanical: ApprovalWorkflowTemplate; electrical: ApprovalWorkflowTemplate; validationPlan?: ApprovalWorkflowTemplate; emergencySubstituteRoleCode: string }
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
  canManufacture: boolean
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
export interface SaveOrganizationUnitInput { id?: string; organizationId: string; parentUnitId?: string; code: string; name: string; kind: OrganizationUnitKind; canManufacture: boolean; isActive: boolean; sortOrder: number }
export interface MainProjectStaffingInput { primaryProjectManager: string; collaborativeProjectManagers: string[]; designLeads: string[] }
export interface DocumentNode {
  /** Unique assembly occurrence. Tree selection and rendering must use this value. */
  id: string
  /** Stable occurrence path from the reference snapshot. */
  instancePath?: string
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

export type WorkspaceLocalStateCode = 'NotDownloaded' | 'ReadOnlyCache' | 'Editable' | 'Modified' | 'NeedsUpdate' | 'IntegrityMismatch' | 'PermissionMismatch' | 'UnexpectedWritable' | 'IdentityConflict'
export interface WorkspaceLocalFileState {
  documentId: string
  fileName: string
  fullPath: string
  localState: WorkspaceLocalStateCode
  localStateLabel: string
  localRevision: string
  latestRevision: string
  message: string
  isReadOnly: boolean
  lastWriteTimeUtc?: string
}
export interface WorkspaceLocalStateSnapshot {
  projectId: string
  projectCode: string
  projectDirectory?: string
  projectDirectoryExists?: boolean
  error?: string
  items: WorkspaceLocalFileState[]
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
  rowVersion?: number
  storedVersionCount?: number
  checkedOutBy?: string
  checkedOutAt?: string
  checkoutMachine?: string
  checkoutLastHeartbeatAt?: string
  checkoutLeaseExpiresAt?: string
  checkoutReleaseRequestedBy?: string
  checkoutReleaseRequestedAt?: string
  updatedAt?: string
  deletedAt?: string
  deletedBy?: string
  deleteReason?: string
  purgedAt?: string
}

export interface ControlledDocumentRecycleReadiness {
  document: ManagedDocument
  canRecycle: boolean
  blockers: string[]
  storedVersionCount: number
  whereUsedCount: number
  restoreDeadline?: string
}

export interface ProjectFileVersion {
  id: string
  projectFileId: string
  versionNumber: number
  fileName: string
  fileLength: number
  sha256: string
  uploadedBy: string
  uploadedAt: string
  comment?: string
}

export interface ProjectFile {
  id: string
  rootProjectId: string
  folderId: string
  fileName: string
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  deletedAt?: string
  deletedBy?: string
  currentVersion?: ProjectFileVersion
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
  kind?: BomClassification
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
  heatTreatment?: string
  weight?: string
  revision: string
  complete: boolean
  sourceDocumentId?: string
  sourceConfiguration?: string
  sourceInstancePath?: string
  parentDrawingNumber?: string
  source?: 'Auto' | 'Manual' | 'MaterialRelation' | 'EngineeringKit'
  engineeringKitReferenceId?: string
  engineeringKitId?: string
  engineeringKitRevisionId?: string
  engineeringKitCode?: string
  engineeringKitVersionNumber?: number
  engineeringKitComponentId?: string
  engineeringKitComponentOptional?: boolean
  manuallyOverridden?: boolean
  pendingRemoval?: boolean
  pendingClassification?: boolean
  manualUnmatched?: boolean
  manuallyRetained?: boolean
  manuallyExcluded?: boolean
  releaseExcluded?: boolean
  releaseExclusionReason?: string
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
export type BomExportMode = 'Summary' | 'Structure'
export type BomClassification = BomKind | 'Virtual'

export type EngineeringKitRevisionState = 'Draft' | 'Released'

export interface EngineeringKitComponent {
  id: string
  revisionId: string
  materialId: string
  materialCode: string
  materialName: string
  quantity: number
  unit: string
  isOptional: boolean
  sortOrder: number
}

export interface EngineeringKitRevision {
  id: string
  kitId: string
  versionNumber: number
  state: EngineeringKitRevisionState
  changeNote?: string
  components: EngineeringKitComponent[]
  createdBy: string
  createdAt: string
  publishedBy?: string
  publishedAt?: string
}

export interface EngineeringKit {
  id: string
  code?: string
  name: string
  description?: string
  currentReleasedRevisionId?: string
  revisions: EngineeringKitRevision[]
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion: number
}

export interface EngineeringKitExpansionLine {
  kitComponentId: string
  materialId: string
  materialCode: string
  materialName: string
  quantity: number
  unit: string
  material?: string
  specification?: string
  remark?: string
  brand?: string
  surfaceTreatment?: string
  weight?: string
  isOptional: boolean
}

export interface EngineeringKitExpansion {
  referenceId: string
  kitId: string
  revisionId: string
  kitCode: string
  kitName: string
  versionNumber: number
  kitQuantity: number
  lines: EngineeringKitExpansionLine[]
}
export interface BatchUpdateBomItemsInput {
  itemIds: string[]
  fields: string[]
  targetKind?: BomClassification
  unit?: string
  drawingNumber?: string
  name?: string
  specification?: string
  remark?: string
  brand?: string
  material?: string
  surfaceTreatment?: string
  heatTreatment?: string
  weight?: string
  quantity?: number
  revision?: string
  parentDrawingNumber?: string
  complete?: boolean
}

export interface BomSourceReclassificationItemPreview {
  itemId: string
  currentKind: BomClassification
  targetKind: 'Standard' | 'NonStandard'
  currentDrawingNumber: string
  sourceDrawingNumber: string
  resultDrawingNumber: string
  currentName: string
  sourceName: string
  changedFields: string[]
  officialMaterialCodeProtected: boolean
}

export interface BomSourceReclassificationPreview {
  targetKind: 'Standard' | 'NonStandard'
  itemCount: number
  changedItemCount: number
  items: BomSourceReclassificationItemPreview[]
}

export interface BomEmptyDeclaration { kind: BomKind; declaredEmpty: boolean; updatedBy?: string; updatedAt?: string }
export interface BomGenerationResult {
  standardItems: BomItem[]
  nonStandardItems: BomItem[]
  electricalItems: BomItem[]
  unclassifiedItems: BomItem[]
  virtualItems: BomItem[]
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
  status: 'approved' | 'rejected' | 'current' | 'waiting' | 'skipped' | 'done'
  detail: string
  decision?: string | number
  decisionBy?: string
  comment?: string
  stepOrder?: number
  emergencySubstitute?: boolean
  emergencyReason?: string
}

export interface UserNotification {
  id: string
  recipient: string
  category: string
  title: string
  content: string
  projectId?: string
  releasePackageId?: string
  sourceKey: string
  createdAt: string
  readAt?: string
}

export interface ApprovalTransferCandidate {
  username: string
  displayName: string
}

export interface ReleaseItemComment {
  id: string
  releasePackageId: string
  bomItemId: string
  materialKey: string
  materialCode: string
  materialName: string
  specification?: string
  sourceInstancePath?: string
  comment: string
  createdBy: string
  createdAt: string
}

export type ReleaseScope = 'LegacyCombined' | 'StandardLongLead' | 'StandardFormal' | 'StandardSupplement' | 'ElectricalFormal' | 'ElectricalSupplement' | 'NonStandardWithDrawing'

export interface CreateReleasePackageInput {
  changeReason: string
  scope: Exclude<ReleaseScope, 'LegacyCombined'>
  selectedBomItemIds: string[]
  selectedBomItemQuantities?: Record<string, number>
  wholeSetMultiplier: number
}

export interface UpdateReleasePackageDraftInput {
  changeReason: string
  selectedBomItemIds: string[]
  selectedBomItemQuantities?: Record<string, number>
  wholeSetMultiplier?: number
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
  wholeSetMultiplier?: number
  changeReasonSelections?: ReleaseChangeReasonSelection[]
  formalSupplementPolicySnapshotted?: boolean
  formalSupplementMaximumCount?: number | null
  formalSupplementValidDays?: number | null
}

export interface ReleaseChangeReasonSelection {
  categoryCode: string
  reasonCode: string
  category: string
  reason: string
  detail?: string | null
}

export type DrawingReviewPackageState = 'InReview' | 'ChangesRequested' | 'WritingProperties' | 'Approved' | 'Stale' | 'Withdrawn'
export type DrawingReviewCandidateState = 'Ready' | 'InReview' | 'ApprovedCurrent' | 'Unavailable'
export type DrawingReviewTarget = 'Model3D' | 'Drawing2D'
export type DrawingReviewTargetState = 'Pending' | 'ChangesRequested' | 'Approved' | 'Marked' | 'NotRequired'
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
  drawingDocumentId?: string | null
  drawingVersionId?: string | null
  drawingRevision?: string | null
  drawingSha256?: string | null
  drawingCreatedBy?: string | null
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
  effectiveDrawingVersionId?: string | null
}

export interface DrawingReviewPackage {
  id: string
  projectId: string
  number: string
  state: DrawingReviewPackageState
  createdBy: string
  createdAt: string
  approvedAt?: string | null
  withdrawnBy?: string | null
  withdrawnAt?: string | null
  withdrawalReason?: string | null
  items: DrawingReviewItem[]
  markups: DrawingReviewMarkup[]
}

export interface DrawingReviewCandidate {
  candidateId: string
  bomItemId?: string | null
  modelDocumentId?: string | null
  drawingDocumentId?: string | null
  drawingNumber: string
  name: string
  configuration?: string | null
  bomKinds: BomKind[]
  modelRevision: string
  drawingRevision?: string | null
  state: DrawingReviewCandidateState
  reason?: string | null
  selectable: boolean
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
  kind?: 'release' | 'validationPlan'
  releasePackageId?: string
  releasePackageNumber?: string
  validationPlanId?: string
  validationPlanRevision?: number
  stepName?: string
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
export type MaterialAttachmentKind = 'Model3D' | 'Document' | 'CoverImage'

export interface MaterialAttachment {
  id: string
  materialId: string
  kind: MaterialAttachmentKind
  originalFileName: string
  fileLength: number
  sha256: string
  uploadedBy: string
  uploadedAt: string
}

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
  selectionAdvice?: string | null
  referencePrice?: number | null
  model3DLink?: string | null
  documentLink?: string | null
  isRecommended?: boolean
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
  model3DAttachmentCount: number
  documentAttachmentCount: number
  coverImageAttachmentId?: string | null
}

export interface MaterialPage {
  items: PdmMaterial[]
  total: number
  page: number
  pageSize: number
}

export interface BomHeaderMaterialDirectoryItem {
  materialId: string
  projectId: string
  projectCode: string
  subprojectCode: string
  projectName: string
  kind: BomHeaderKind
  materialCode: string
  materialName: string
  automaticStatus: string
  automaticMessage: string
  isArchived: boolean
}

export type MaterialDuplicateField = 'Name' | 'Specification' | 'Brand'

export interface MaterialDuplicateRule {
  categoryCode: string
  fields: MaterialDuplicateField[]
}

export interface StandardLibraryCategory {
  id: string
  name: string
  parentId?: string | null
  sortOrder: number
  isActive: boolean
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion: number
}

export type MaterialRelationSelectionMode = 'Single' | 'Multiple'
export type MaterialRelationQuantityMode = 'PerMainQuantity' | 'Fixed'
export type MaterialRelationRevisionState = 'Draft' | 'Published' | 'Superseded'
export type MaterialRelationReviewDecision = 'Selected' | 'NoAccessory' | 0 | 1

export interface MaterialRelationOption {
  id: string
  materialId: string
  materialCode: string
  materialName: string
  materialKind: MaterialKind
  unitCode: string
  quantityMode: MaterialRelationQuantityMode
  quantityPerSet: number
  isDefault: boolean
  sortOrder: number
}

export interface MaterialRelationGroup {
  id: string
  name: string
  isRequired: boolean
  selectionMode: MaterialRelationSelectionMode
  minSelection: number
  maxSelection?: number | null
  autoSelectUnique: boolean
  sortOrder: number
  options: MaterialRelationOption[]
}

export interface MaterialRelationRevision {
  id: string
  version: number
  state: MaterialRelationRevisionState
  changeNote?: string | null
  createdBy: string
  createdAt: string
  publishedBy?: string | null
  publishedAt?: string | null
  rowVersion: number
  groups: MaterialRelationGroup[]
}

export interface MaterialRelationTemplate {
  id: string
  mainMaterialId: string
  mainMaterialCode: string
  mainMaterialName: string
  name: string
  isArchived: boolean
  publishedRevision?: MaterialRelationRevision | null
  draftRevision?: MaterialRelationRevision | null
  updatedBy: string
  updatedAt: string
  rowVersion: number
}

export interface SaveMaterialRelationTemplateInput {
  mainMaterialId: string
  name: string
  changeNote?: string | null
  expectedRevisionRowVersion?: number | null
  groups: Array<{
    name: string
    isRequired: boolean
    selectionMode: MaterialRelationSelectionMode
    minSelection: number
    maxSelection?: number | null
    autoSelectUnique: boolean
    sortOrder: number
    options: Array<{
      materialId: string
      quantityMode: MaterialRelationQuantityMode
      quantityPerSet: number
      isDefault: boolean
      sortOrder: number
    }>
  }>
}

export interface MaterialRelationGroupCheck {
  groupId: string
  groupName: string
  isRequired: boolean
  selectionMode: MaterialRelationSelectionMode
  maxSelection?: number | null
  isComplete: boolean
  status: string
  expectedQuantity: number
  actualQuantity: number
  selectedOptionIds: string[]
  reviewDecision?: MaterialRelationReviewDecision | null
  reviewReason?: string | null
  reviewedBy?: string | null
  reviewedAt?: string | null
  options: MaterialRelationOption[]
}

export interface MaterialRelationMainCheck {
  mainBomItemId: string
  mainMaterialCode: string
  mainMaterialName: string
  mainQuantity: number
  templateId: string
  revisionId: string
  revisionVersion: number
  isComplete: boolean
  groups: MaterialRelationGroupCheck[]
}

export interface MaterialRelationCompleteness {
  projectId: string
  isComplete: boolean
  mainMaterialCount: number
  incompleteGroupCount: number
  mainMaterials: MaterialRelationMainCheck[]
}

export interface StandardLibraryMaterial {
  material: PdmMaterial
  categories: StandardLibraryCategory[]
  coverImage?: MaterialAttachment | null
}

export interface StandardLibraryMaterialPage {
  items: StandardLibraryMaterial[]
  total: number
  page: number
  pageSize: number
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
  applicationStatus?: MaterialCodeApplicationStatus | null
  applicationId?: string | null
  requestedBy?: string | null
  requestedAt?: string | null
  automaticStatus?: 'NotRequested' | 'ApprovalQueued' | 'Running' | 'Rejected' | 'Completed' | 'Failed' | 'WaitingRetry' | 'Queued'
  automaticMessage?: string | null
  canRetryAutomatic?: boolean
  applicationRowVersion?: number
}

export type MaterialCodeApplicationStatus = 'Pending' | 'Approved' | 'Rejected'
export type MaterialCodeWorkflowState = 'PendingApproval' | 'PendingMaterialSync' | 'MaterialSyncFailed' | 'PendingBomSync' | 'BomSyncFailed' | 'Completed' | 'Rejected'
export interface MaterialCodeApplication {
  id: string
  projectId: string
  bomItemId?: string | null
  bomHeaderKind?: BomHeaderKind | null
  applicationType: 'StandardBomItem' | 'BomHeader'
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
  applicationName?: string | null
  projectCode?: string | null
  projectName?: string | null
  categoryCode?: string | null
  requestedMaterialCode?: string | null
  specification?: string | null
  brand?: string | null
  remark?: string | null
  workflowState: MaterialCodeWorkflowState
  syncTaskId?: string | null
  syncStatus?: MaterialSyncStatus | null
  syncError?: string | null
  workflowMessage?: string | null
}
export type MaterialCodeResolutionStatus = 'Matched' | 'NoMatch' | 'Ambiguous' | 'ApplicationPending' | 'ApplicationApproved' | 'Verified' | 'ValidationFailed' | 'CodeNotFound'
export interface MaterialCodeResolution {
  bomItemId: string
  status: MaterialCodeResolutionStatus
  material?: PdmMaterial | null
  candidates: PdmMaterial[]
  application?: MaterialCodeApplication | null
  issues: string[]
}

export type ApprovalU9AutomationStage = 'NotRequested' | 'ItemSyncFailed' | 'WaitingForDependencies' | 'BomSyncFailed' | 'Completed'
export type ProjectBomU9AutomaticState = 'WaitingForDependencies' | 'AwaitingApproval' | 'Empty' | 'UpToDate' | 'Created' | 'Modified'
export interface ApprovalU9BomOutcome {
  projectId: string
  projectCode: string
  kind: BomHeaderKind
  state?: ProjectBomU9AutomaticState | null
  message: string
  failed: boolean
}
export interface ApprovalU9AutomationResult {
  stage: ApprovalU9AutomationStage
  message: string
  itemSync?: MaterialSyncExecutionResult | null
  boms: ApprovalU9BomOutcome[]
}

export interface MaterialCodeDecisionResult {
  application: MaterialCodeApplication
  material?: PdmMaterial | null
  task?: MaterialSyncTask | null
  automation?: ApprovalU9AutomationResult | null
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
  selectionAdvice?: string | null
  referencePrice?: number | null
  model3DLink?: string | null
  documentLink?: string | null
  isRecommended?: boolean
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

export interface MaterialNumberingSettings {
  startSequence: number
  sequenceLength: number
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
  materialCode?: string | null
  materialName?: string | null
  specification?: string | null
  brand?: string | null
  remark?: string | null
  categoryCode?: string | null
  projectId?: string | null
  projectCode?: string | null
  projectName?: string | null
  bomHeaderKind?: BomHeaderKind | null
  requestedBy?: string | null
  requestedAt?: string | null
  createdAt: string
  updatedAt: string
}

export interface MaterialSyncExecutionResult {
  material: PdmMaterial
  task: MaterialSyncTask
  created: boolean
  alreadyExisted: boolean
  updated: boolean
  automation?: ApprovalU9AutomationResult | null
  applications?: MaterialCodeApplication[]
  completed?: boolean
  message?: string
}

export type MaterialSyncBatchStatus = 'Queued' | 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed'
export type MaterialSyncBatchItemStatus = 'Queued' | 'Running' | 'Succeeded' | 'Waiting' | 'Failed'

export interface MaterialSyncBatchItem {
  id: string
  batchId: string
  taskId: string
  ordinal: number
  status: MaterialSyncBatchItemStatus
  message?: string | null
  startedAt?: string | null
  completedAt?: string | null
}

export interface MaterialSyncBatch {
  id: string
  status: MaterialSyncBatchStatus
  requestedBy: string
  requestedRole: string
  totalCount: number
  completedCount: number
  succeededCount: number
  waitingCount: number
  failedCount: number
  currentTaskId?: string | null
  currentMaterialCode?: string | null
  lastError?: string | null
  createdAt: string
  startedAt?: string | null
  completedAt?: string | null
  items: MaterialSyncBatchItem[]
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
  componentChanges?: Array<{ sequence: number; itemCode: string; change: string; previousQuantity: number; quantity: number }>
  modifiedComponentCount?: number
  deletedComponentCount?: number
  operation: U9BomWriteOperation
  path: string
  requestPreview: string
  requestSha256: string
  baselineSha256: string
  requiredConfirmation: string
  addedComponentCount: number
  retainedHistoricalComponentCount: number
  quantityReconciliations: Array<{
    itemCode: string
    issueUomCode: string
    parentQty: number
    plmApprovedTotal: number
    u9ExistingTotal: number
    uploadDelta: number
  }>
  generatedAt: string
}

export interface U9BomWriteExecution {
  preview: U9BomWritePreview
  writeResult: { responseCode: number; responseMessage?: string | null; rows: Array<{ isSuccess: boolean; errorMessage?: string | null }> }
  verification: { responseCode: number; responseMessage?: string | null; boms: U9BomReference[] }
  executedAt: string
}

export type ProjectBomU9SyncState = 'Empty' | 'CreateRequired' | 'AwaitingApproval' | 'ModifyRequired' | 'UpToDate'

export interface ProjectBomU9SyncPreview {
  projectId: string
  kind: BomHeaderKind
  itemCode: string
  componentCount: number
  state: ProjectBomU9SyncState
  writePreview?: U9BomWritePreview | null
}

export interface ProjectBomU9SyncExecution {
  preview: ProjectBomU9SyncPreview
  execution: U9BomWriteExecution
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

export interface U9MaterialFullSyncCategory {
  code: string
  name: string
}

export interface U9MaterialFullSyncCategoryResult {
  categoryCode: string
  categoryName: string
  discoveredCount: number
  createdCount: number
  refreshedCount: number
  skippedCount: number
  maximumSequence: number
  succeeded: boolean
  error?: string | null
  inactivatedCount: number
  conflictCount: number
}

export interface U9MaterialFullSyncRun {
  id: string
  triggerKind: string
  status: 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed'
  categoryCodes: string[]
  categoryResults: U9MaterialFullSyncCategoryResult[]
  categoryCount: number
  completedCategoryCount: number
  discoveredCount: number
  createdCount: number
  refreshedCount: number
  skippedCount: number
  failedCategoryCount: number
  lastError?: string | null
  startedAt: string
  completedAt?: string | null
}

export interface U9MaterialFullSyncStatusResponse {
  scheduleTime: string
  checkIntervalMinutes: number
  categories: U9MaterialFullSyncCategory[]
  latestRun?: U9MaterialFullSyncRun | null
}

export interface U9InventoryRow {
  similarityPercent?: number | null
  organizationCode: string
  warehouseCode: string
  warehouseName: string
  materialCode: string
  itemName: string
  brand?: string | null
  specification?: string | null
  projectCode?: string | null
  projectName?: string | null
  subproject?: string | null
  stockQuantity: number
  availableQuantity: number
  reservedQuantity: number
  unavailableQuantity: number
  binCode?: string | null
  binName?: string | null
  storageType?: string | null
  refreshedAt: string
}

export interface U9InventoryPage {
  items: U9InventoryRow[]
  total: number
  page: number
  pageSize: number
  warehouseNames: string[]
  brandNames: string[]
  projectCodes: string[]
  subprojectOptions: Array<{ projectCode: string; subproject: string }>
  lastSuccessfulRefreshAt?: string | null
}

export interface U9InventorySyncSettings {
  autoSyncEnabled: boolean
  syncIntervalMinutes: number
  queryPath: string
  updatedBy?: string | null
  updatedAt?: string | null
}

export interface U9InventorySyncRun {
  id: string
  triggerKind: string
  status: 'Running' | 'Succeeded' | 'Failed'
  sourceRowCount: number
  storedRowCount: number
  materialCount: number
  lastError?: string | null
  startedAt: string
  completedAt?: string | null
}

export interface U9InventorySyncStatusResponse {
  settings: U9InventorySyncSettings
  latestRun?: U9InventorySyncRun | null
}

export interface U9InventoryFilters {
  similarSpecification?: string
  materialCode?: string
  itemName?: string
  specification?: string
  brand?: string
  warehouse?: string
  projectCode?: string
  subproject?: string
  positiveStockOnly?: boolean
  page?: number
  pageSize?: number
}

export interface ProcurementDocumentDetail {
  kind: '请购' | '采购'
  documentNumber: string
  lineNumber: number
  lineStatus: string
  rawLineStatus: number
  isCanceled: boolean
  quantity: number
  arrivedQuantity: number
  remark?: string | null
  businessDate?: string | null
  deliveryDate?: string | null
  latestDeliveryDate?: string | null
  matchKind: string
}

export interface WarehouseMovementDetail {
  kind: 'RCV' | 'ISSUE' | 'MISC' | 'TRANSFER' | 'STOCKIN'
  lineId?: string | null
  documentNumber: string
  lineNumber: number
  date: string
  quantity: number
  unit?: string | null
}

export interface ProjectProcurementTrackingItem {
  sequence: number
  projectCode: string
  subprojectCode?: string | null
  materialCode: string
  materialName: string
  specification?: string | null
  remark?: string | null
  brand?: string | null
  quantity: number | null
  bomKind: string
  releasePackageNumber?: string | null
  purchaseRequisitionNumbers: string[]
  purchaseRequisitionStatus: string
  purchaseRequisitionCreatedAt?: string | null
  purchaseRequisitionDeliveryDate?: string | null
  purchaseOrderNumbers: string[]
  purchaseOrderStatus: string
  buyerName?: string | null
  purchaseQuantity: number
  arrivedQuantity: number
  purchaseRemark?: string | null
  purchaseDeliveryDate?: string | null
  latestDeliveryDate?: string | null
  requestedQuantity: number | null
  approvedQuantity: number | null
  hasDeliveryDelay?: boolean
  warehouseMovements?: WarehouseMovementDetail[]
  isWarehouseMovementRow?: boolean
  isFullyReceived?: boolean
  details: ProcurementDocumentDetail[]
}

export interface ProjectProcurementTrackingResult {
  projectId: string
  projectCode: string
  subprojectCode?: string | null
  items: ProjectProcurementTrackingItem[]
  lastSuccessfulRefreshAt?: string | null
  lastRefreshError?: string | null
  hasPublishedBom: boolean
}

export interface U9ProcurementSyncSettings {
  autoSyncEnabled: boolean
  syncIntervalMinutes: number
  queryPath: string
  updatedBy?: string | null
  updatedAt?: string | null
}

export interface U9ProcurementSyncRun {
  id: string
  triggerKind: string
  status: 'Running' | 'Succeeded' | 'Failed'
  sourceRowCount: number
  storedRowCount: number
  projectCount: number
  lastError?: string | null
  startedAt: string
  completedAt?: string | null
}

export interface U9ProcurementSyncStatusResponse {
  settings: U9ProcurementSyncSettings
  latestRun?: U9ProcurementSyncRun | null
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

export type ProgramTemplateAssetType = 'PlcFunctionBlock' | 'PlcProgram' | 'HmiTemplate'
export type ProgramTemplateRevisionState = 'Draft' | 'PendingReview' | 'PendingApproval' | 'Rejected' | 'Published' | 'Superseded' | 'Archived'
export type ProgramTemplateParameterDirection = 'Input' | 'Output' | 'InOut'
export type ProgramTemplateAttachmentKind = 'Package' | 'TestEvidence'
export type ProgramTemplateApprovalStage = 'Review' | 'Approval'
export type ProgramTemplateApprovalDecision = 'Approved' | 'Rejected'
export type ProgramTemplateVersionBump = 'Major' | 'Minor' | 'Patch'

export interface ProgramTemplateParameter {
  id: string
  direction: ProgramTemplateParameterDirection
  sortOrder: number
  name: string
  dataType: string
  defaultValue?: string | null
  unit?: string | null
  description?: string | null
}

export interface ProgramTemplateRevision {
  id: string
  version: string
  attemptNumber: number
  state: ProgramTemplateRevisionState
  name: string
  category: string
  description: string
  vendor: string
  platform: string
  softwareVersion: string
  applicableSeries: string
  tags: string[]
  changeNote: string
  packageFileName?: string | null
  packageFileLength?: number | null
  packageSha256?: string | null
  evidenceFileName?: string | null
  evidenceFileLength?: number | null
  evidenceSha256?: string | null
  createdBy: string
  createdAt: string
  submittedAt?: string | null
  publishedAt?: string | null
  rowVersion: number
  parameters: ProgramTemplateParameter[]
}

export interface ProgramTemplate {
  id: string
  code: string
  assetType: ProgramTemplateAssetType
  originCompanyId?: string | null
  originCompanyName?: string | null
  currentPublishedRevisionId?: string | null
  isArchived: boolean
  createdBy: string
  createdAt: string
  revisions: ProgramTemplateRevision[]
}

export interface ProgramTemplateTask {
  id: string
  templateId: string
  templateCode: string
  revisionId: string
  templateName: string
  version: string
  stage: ProgramTemplateApprovalStage
  state: ProgramTemplateRevisionState
  requiredChecklist: string[]
  createdAt: string
  rowVersion: number
}

export interface ProgramTemplateParameterInput {
  direction: ProgramTemplateParameterDirection
  sortOrder: number
  name: string
  dataType: string
  defaultValue?: string | null
  unit?: string | null
  description?: string | null
}

export interface ProgramTemplateDraftInput {
  assetType: ProgramTemplateAssetType
  name: string
  category: string
  description: string
  vendor: string
  platform: string
  softwareVersion: string
  applicableSeries: string
  tags: string[]
  changeNote: string
  parameters: ProgramTemplateParameterInput[]
}

export interface ValidationCheckCategory {
  id: string
  name: string
  sortOrder: number
  isActive: boolean
  note?: string | null
  itemCount: number
  referenceCount: number
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion: number
}

export interface ValidationCheckItem {
  id: string
  categoryId: string
  content: string
  defaultInformationSource: string
  sortOrder: number
  isActive: boolean
  note?: string | null
  referenceCount: number
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion: number
}

export interface ValidationCheckCatalog {
  categories: ValidationCheckCategory[]
  items: ValidationCheckItem[]
}

export interface ProjectValidationPlanItem {
  id: string
  catalogCategoryId?: string | null
  catalogItemId?: string | null
  categoryName: string
  validationContent: string
  informationSource?: string | null
  validationDate?: string | null
  result?: string | null
  responsiblePerson?: string | null
  remark?: string | null
  sortOrder: number
}

export interface ProjectValidationPlan {
  id: string
  projectId: string
  revisionNumber: number
  state: 'Draft' | 'PendingApproval' | 'Effective' | 'Rejected' | 'Superseded' | number
  preparedBy?: string | null
  validationDate?: string | null
  items: ProjectValidationPlanItem[]
  approvalTasks: ValidationPlanApprovalTask[]
  attachments: ValidationPlanAttachment[]
  workflowCode?: string | null
  workflowVersion?: number | null
  submittedBy?: string | null
  submittedAt?: string | null
  effectiveBy?: string | null
  effectiveAt?: string | null
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  rowVersion: number
}

export interface ValidationPlanApprovalTask {
  id: string
  planId: string
  stepOrder: number
  stage: ApprovalStage | number
  stepName: string
  assignee: string
  decision?: 'Approved' | 'Rejected' | number | null
  decisionBy?: string | null
  decisionComment?: string | null
  createdAt: string
  decidedAt?: string | null
}

export interface ValidationPlanAttachment {
  id: string
  planId: string
  kind: 'PlanDocument' | 'Evidence' | number
  originalFileName: string
  fileVersion: number
  storageRelativePath: string
  fileLength: number
  sha256: string
  uploadedBy: string
  uploadedAt: string
}

export interface ValidationPlanRecognitionCandidate {
  planItemId: string
  categoryName: string
  validationContent: string
  matchConfidence: number
  matchStatus: 'Matched' | 'Review' | 'Unmatched'
  recognizedResult?: string | null
  recognizedValidationDate?: string | null
  recognizedResponsiblePerson?: string | null
  recognizedRemark?: string | null
  sourceText: string
}

export interface ValidationPlanRecognitionDraft {
  planId: string
  attachmentId: string
  originalFileName: string
  recognizedAt: string
  ocrText: string
  candidates: ValidationPlanRecognitionCandidate[]
}

export interface ValidationPlanExecutionItem {
  id: string
  executionRecordId: string
  planItemId: string
  matchConfidence: number
  sourceText: string
  recognizedResult?: string | null
  recognizedValidationDate?: string | null
  recognizedResponsiblePerson?: string | null
  recognizedRemark?: string | null
  result?: string | null
  validationDate?: string | null
  responsiblePerson?: string | null
  remark?: string | null
}

export interface ValidationPlanExecutionRecord {
  id: string
  planId: string
  sourceAttachmentId: string
  sourceFileName: string
  ocrText: string
  items: ValidationPlanExecutionItem[]
  confirmedBy: string
  confirmedAt: string
}

export interface ConfirmValidationPlanExecutionInput {
  sourceAttachmentId: string
  ocrText: string
  items: Array<{
    planItemId: string
    matchConfidence: number
    sourceText: string
    recognizedResult?: string | null
    recognizedValidationDate?: string | null
    recognizedResponsiblePerson?: string | null
    recognizedRemark?: string | null
    result?: string | null
    validationDate?: string | null
    responsiblePerson?: string | null
    remark?: string | null
  }>
}

export interface ValidationPlanApprovalTaskSummary {
  id: string
  planId: string
  projectId: string
  projectCode: string
  projectName: string
  revisionNumber: number
  stage: ApprovalStage | number
  stepName: string
  createdAt: string
}

export interface SaveValidationCheckCategoryInput {
  name: string
  sortOrder: number
  isActive: boolean
  note?: string | null
  expectedRowVersion?: number | null
}

export interface SaveValidationCheckItemInput {
  categoryId: string
  content: string
  defaultInformationSource?: string | null
  sortOrder: number
  isActive: boolean
  note?: string | null
  expectedRowVersion?: number | null
}

export interface SaveProjectValidationPlanInput {
  preparedBy?: string | null
  validationDate?: string | null
  items: Array<{
    catalogItemId?: string | null
    validationContent?: string | null
    informationSource?: string | null
    validationDate?: string | null
    result?: string | null
    responsiblePerson?: string | null
    remark?: string | null
    sortOrder: number
  }>
  expectedRowVersion?: number | null
}
