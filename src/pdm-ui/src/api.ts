import type { AddDrawingReviewMarkupInput, ApprovalStep, ApprovalU9AutomationResult, AuditEntry, BatchUpdateBomItemsInput, BomClassification, BomEmptyDeclaration, BomExportMode, BomGenerationResult, BomHeaderKind, BomItem, BomKind, BomValidationRules, BomVersion, BomVersionState, CreateProjectInput, CreateReleasePackageInput, CreateRoleInput, CreateSubprojectInput, CrmConnectionTestResult, CrmCustomerSyncResult, CrmIntegrationSettings, DocumentKind, DocumentModelDrawingRelation, DocumentNode, DocumentVersionComparison, DocumentVersionSummary, DocumentWhereUsed, DrawingReviewCandidate, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget, EditLockSummary, EngineeringKit, EngineeringKitExpansion, EquipmentTypeDefinition, FolderPermissionRule, MainProjectStaffingInput, ManagedDocument, ManufacturingBomBaseline, MaterialAttachment, MaterialAttachmentKind, MaterialCategory, MaterialCategoryRule, MaterialCodeApplication, MaterialCodeApplicationStatus, MaterialCodeDecisionResult, MaterialCodeResolution, MaterialDuplicateRule, MaterialKind, MaterialNumberingSettings, MaterialPage, MaterialRemovalReadiness, MaterialRemovalResult, MaterialSyncExecutionResult, MaterialSyncTask, MyApprovalTask, OrganizationDirectory, OrganizationUnit, PasswordResetTask, PdmCustomer, PdmMaterial, PdmSystemSettings, PdmUser, PdmUserProfile, ProgramTemplate, ProgramTemplateApprovalDecision, ProgramTemplateAttachmentKind, ProgramTemplateDraftInput, ProgramTemplateRevision, ProgramTemplateTask, ProgramTemplateVersionBump, ProjectBomHeader, ProjectBomU9SyncExecution, ProjectBomU9SyncPreview, ProjectFile, ProjectFileVersion, ProjectFolder, ProjectFolderTemplateNode, ProjectNumberingOptions, ProjectOrganization, ProjectProcurementTrackingResult, ProjectSummary, ProjectVersionItem, ReferenceStatus, ReleaseItemComment, ReleasePackageSummary, ReleaseScope, RolePermissionDirectory, SaveMaterialInput, SaveOrganizationUnitInput, SavePdmUserInput, SaveProjectOrganizationInput, StandardLibraryCategory, StandardLibraryMaterialPage, U9BomQueryExecution, U9BomQueryInput, U9BomWriteExecution, U9BomWriteInput, U9BomWritePreview, U9ConnectionTestResult, U9InventoryFilters, U9InventoryPage, U9InventorySyncSettings, U9InventorySyncStatusResponse, U9ItemQueryResult, U9MaterialFullSyncStatusResponse, U9MaterialIntegrationSettings, U9MaterialSampleImportResult, U9MaterialSamplePreview, U9ProcurementSyncSettings, U9ProcurementSyncStatusResponse, UpdateCrmIntegrationInput, UpdateProjectInput, UpdateReleasePackageDraftInput, UpdateU9MaterialIntegrationInput } from './types'
import type { MaterialSyncBatch } from './types'
import type { ApprovalTransferCandidate, UserNotification } from './types'
import type { ProjectCopyOptionsInput, ProjectCopyPreview, ProjectCopyResult } from './types'

import type { BomSourceReclassificationPreview } from './types'
import type { MaterialRelationCompleteness, MaterialRelationTemplate, SaveMaterialRelationTemplateInput } from './types'
import type { ConfirmValidationPlanExecutionInput, ProjectValidationPlan, SaveProjectValidationPlanInput, SaveValidationCheckCategoryInput, SaveValidationCheckItemInput, ValidationCheckCatalog, ValidationCheckCategory, ValidationCheckItem, ValidationPlanAttachment, ValidationPlanApprovalTaskSummary, ValidationPlanExecutionRecord, ValidationPlanRecognitionDraft } from './types'

const localDesktopOrigin = window.location.hostname === 'appassets.pdm.local'
const needsLocalApiFallback = localDesktopOrigin || import.meta.env.MODE === 'test'
const apiBase = (import.meta.env.VITE_PDM_API_BASE ?? (needsLocalApiFallback ? 'http://127.0.0.1:5080' : '')).replace(/\/$/, '')

export class PdmApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message)
  }
}

function withApiContext<T>(label: string, request: Promise<T>): Promise<T> {
  return request.catch(error => {
    if (error instanceof PdmApiError) throw new PdmApiError(`${label}：${error.message}`, error.status)
    throw error
  })
}

export interface AuthSession {
  accessToken: string
  expiresAt: string
  resumeToken: string
  username: string
  displayName: string
  role: string
  roles?: string[]
  permissions: string[]
  primaryCompanyId: string
  activeCompanyId: string
  activeCompanyName: string
  crossCompanyView: boolean
  accessibleCompanies: Array<{ id: string; name: string; code: string }>
}

export function listProgramTemplates(token: string, mine = false): Promise<ProgramTemplate[]> {
  return requestJson<ProgramTemplate[]>(`/api/program-templates${mine ? '?mine=true' : ''}`, {}, token)
}

export function getProgramTemplate(templateId: string, token: string): Promise<ProgramTemplate> {
  return requestJson<ProgramTemplate>(`/api/program-templates/${templateId}`, {}, token)
}

export function createProgramTemplate(input: ProgramTemplateDraftInput, token: string): Promise<ProgramTemplate> {
  return requestJson<ProgramTemplate>('/api/program-templates', { method: 'POST', body: JSON.stringify(input) }, token)
}

export function updateProgramTemplateDraft(revisionId: string, input: ProgramTemplateDraftInput, expectedRowVersion: number, token: string): Promise<ProgramTemplateRevision> {
  const { assetType: _, ...draft } = input
  return requestJson<ProgramTemplateRevision>(`/api/program-templates/revisions/${revisionId}`, {
    method: 'PUT', body: JSON.stringify({ ...draft, expectedRowVersion }),
  }, token)
}

export function createProgramTemplateRevision(templateId: string, bump: ProgramTemplateVersionBump, token: string): Promise<ProgramTemplateRevision> {
  return requestJson<ProgramTemplateRevision>(`/api/program-templates/${templateId}/revisions`, {
    method: 'POST', body: JSON.stringify({ bump }),
  }, token)
}

export async function uploadProgramTemplateFile(
  revisionId: string,
  kind: ProgramTemplateAttachmentKind,
  file: File,
  expectedRowVersion: number,
  token: string,
  onProgress?: (percent: number) => void,
): Promise<ProgramTemplateRevision> {
  onProgress?.(0)
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  const sha256 = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('').toUpperCase()
  const session = await requestJson<{ id: string; chunkSize: number }>(`/api/program-templates/revisions/${revisionId}/uploads`, {
    method: 'POST', body: JSON.stringify({ kind, fileName: file.name, totalLength: file.size, sha256 }),
  }, token)
  const chunks = Math.ceil(file.size / session.chunkSize)
  for (let index = 0; index < chunks; index++) {
    const body = file.slice(index * session.chunkSize, Math.min(file.size, (index + 1) * session.chunkSize))
    const response = await fetch(`${apiBase}/api/program-templates/uploads/${session.id}/chunks/${index}`, {
      method: 'PUT', headers: authenticatedHeaders(token), body,
    })
    if (!response.ok) throw new PdmApiError(`程序模板分块${index + 1}上传失败（${response.status}）`, response.status)
    onProgress?.(Math.round(((index + 1) / chunks) * 100))
  }
  return requestJson<ProgramTemplateRevision>(`/api/program-templates/uploads/${session.id}/complete`, {
    method: 'POST', body: JSON.stringify({ expectedRowVersion }),
  }, token)
}

export function submitProgramTemplateRevision(revisionId: string, expectedRowVersion: number, token: string): Promise<ProgramTemplateRevision> {
  return requestJson<ProgramTemplateRevision>(`/api/program-templates/revisions/${revisionId}/submit`, {
    method: 'POST', body: JSON.stringify({ expectedRowVersion }),
  }, token)
}

export function listProgramTemplateTasks(token: string): Promise<ProgramTemplateTask[]> {
  return requestJson<ProgramTemplateTask[]>('/api/program-templates/tasks/mine', {}, token)
}

export function decideProgramTemplateTask(
  taskId: string,
  decision: ProgramTemplateApprovalDecision,
  comment: string,
  checklistItems: string[],
  expectedRowVersion: number,
  token: string,
): Promise<{ revision: ProgramTemplateRevision }> {
  return requestJson(`/api/program-templates/tasks/${taskId}/decision`, {
    method: 'POST', body: JSON.stringify({ decision, comment, checklistItems, expectedRowVersion }),
  }, token)
}

export async function downloadProgramTemplate(templateId: string, fileName: string, token: string): Promise<void> {
  const response = await fetch(`${apiBase}/api/program-templates/${templateId}/download`, { headers: authenticatedHeaders(token) })
  if (!response.ok) throw new PdmApiError(`程序模板下载失败（${response.status}）`, response.status)
  const url = URL.createObjectURL(await response.blob())
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  anchor.click()
  URL.revokeObjectURL(url)
}

export function setProgramTemplateArchived(templateId: string, archived: boolean, reason: string, token: string): Promise<ProgramTemplate> {
  return requestJson<ProgramTemplate>(`/api/program-templates/${templateId}/archive`, {
    method: 'POST', body: JSON.stringify({ archived, reason }),
  }, token)
}

export interface ProjectWorkspaceData {
  project: ProjectSummary
  root: DocumentNode
  hasDocuments: boolean
  documents: ManagedDocument[]
  documentRelations: DocumentModelDrawingRelation[]
  folders: ProjectFolder[]
  standardBom: BomItem[]
  nonStandardBom: BomItem[]
  unclassifiedBom: BomItem[]
  electricalBom: BomItem[]
  bomSourceData: BomItem[]
  bomEmptyDeclarations: BomEmptyDeclaration[]
  bomVersions: BomVersion[]
  bomBaselines: ManufacturingBomBaseline[]
  drawingReviews: DrawingReviewPackage[]
  materialCodeApplications: MaterialCodeApplication[]
  releasePackages: ReleasePackageSummary[]
  releasePackage: ReleasePackageSummary | null
}

export interface ProjectDocumentWorkspaceData {
  root: DocumentNode
  hasDocuments: boolean
  documents: ManagedDocument[]
  documentRelations: DocumentModelDrawingRelation[]
}

interface ApiProject {
  id: string
  code: string
  name: string
  owner: string
  vaultLocation: string
  releaseLocation: string
  isActive: boolean
  projectAlias?: string | null
  organizationId?: string | null
  organizationName?: string | null
  projectTypeCode?: string | null
  equipmentTypeCode?: number | null
  customerCode?: string | null
  customerName?: string | null
  customerProjectSequence?: number | null
  deviceModel?: string | null
  signedDate?: string | null
  quantity?: number
  parentProjectId?: string | null
  rootProjectId?: string | null
  childSequence?: number | null
  bomItemCategoryCode?: '0301' | '0302' | null
  serialNumbers?: string[]
  responsibleUsers?: string[]
  executionUnitId?: string | null
  executionUnitName?: string | null
  primaryProjectManager?: string | null
  collaborativeProjectManagers?: string[]
  designLead?: string | null
  designLeads?: string[]
  designers?: string[]
  documentCount?: number | null
  modelDocumentCount?: number | null
  drawingDocumentCount?: number | null
  businessStatus?: string | null
  rootDocumentCheckedOutBy?: string | null
  canAssignExecutionUnit?: boolean
  canManageMainStaffing?: boolean
  canAssignDesigners?: boolean
  canReadContent?: boolean
}

interface ApiRevision {
  baseRevision?: string | null
  workIteration?: number
  isReleased?: boolean
  display?: string
}

interface ApiDocument {
  id: string
  projectId: string
  folderId?: string | null
  drawingNumber: string
  name: string
  fileName: string
  kind: number | string
  revision?: ApiRevision | null
  checkedOutBy?: string | null
  checkedOutAt?: string | null
  checkoutMachine?: string | null
  checkoutLastHeartbeatAt?: string | null
  checkoutLeaseExpiresAt?: string | null
  checkoutReleaseRequestedBy?: string | null
  checkoutReleaseRequestedAt?: string | null
  lifecycleState?: number | string
  state?: number | string
  storedVersionCount?: number | null
  updatedAt?: string
}

interface ApiReferenceNode {
  nodeId: string
  documentId?: string | null
  instancePath: string
  fileName: string
  displayName: string
  kind: number | string
  configuration: string
  quantity: number
  status: number | string
  revision?: ApiRevision | null
  checkedOutBy?: string | null
  children?: ApiReferenceNode[]
}

interface ApiBomItem {
  id?: string
  kind?: string | number
  sequence: number
  drawingNumber: string
  name: string
  quantity: number
  unit: string
  material?: string | null
  specification?: string | null
  remark?: string | null
  brand?: string | null
  surfaceTreatment?: string | null
  heatTreatment?: string | null
  weight?: string | null
  revision: string
  isComplete: boolean
  sourceDocumentId?: string | null
  sourceConfiguration?: string | null
  source?: string | null
  isManuallyOverridden?: boolean
  isPendingRemoval?: boolean
  isPendingClassification?: boolean
  isManualUnmatched?: boolean
  isManuallyRetained?: boolean
  isManuallyExcluded?: boolean
  isReleaseExcluded?: boolean
  releaseExclusionReason?: string | null
  reconciliationStatus?: string | null
  reconciliationNote?: string | null
  reconciliationUpdatedBy?: string | null
  reconciliationUpdatedAt?: string | null
  deletedAt?: string | null
  deletedBy?: string | null
  deleteReason?: string | null
  propertyWritebackStatus?: string | number | null
  engineeringKitReferenceId?: string | null
  engineeringKitId?: string | null
  engineeringKitRevisionId?: string | null
  engineeringKitCode?: string | null
  engineeringKitVersionNumber?: number | null
  engineeringKitComponentId?: string | null
  engineeringKitComponentOptional?: boolean
}

interface ApiApprovalTask {
  id: string
  stage: number | string
  assignee: string
  decisionBy?: string | null
  decision?: number | string | null
  decidedAt?: string | null
  comment?: string | null
  stepOrder?: number
  stepName?: string | null
  isEmergencySubstitute?: boolean
  emergencyReason?: string | null
}

interface ApiReleasePackage {
  id: string
  number: string
  state: number | string
  approvalTasks?: ApiApprovalTask[]
  publishedAt?: string | null
  publishedPath?: string | null
  publishError?: string | null
  changeNumber?: string | null
  changeReason?: string | null
  effectiveSerialFrom?: string | null
  effectiveSerialTo?: string | null
  standardBomRevision?: string | null
  nonStandardBomRevision?: string | null
  electricalBomRevision?: string | null
  scope?: string | number
  workflowCode?: string | null
  workflowVersion?: number
  selectedBomItemIds?: string[]
  createsManufacturingBaseline?: boolean
  locksDocuments?: boolean
  wholeSetMultiplier?: number
  changeReasonSelections?: ReleasePackageSummary['changeReasonSelections']
  formalSupplementPolicySnapshotted?: boolean
  formalSupplementMaximumCount?: number | null
  formalSupplementValidDays?: number | null
  standardBomVersionId?: string | null
  nonStandardBomVersionId?: string | null
  electricalBomVersionId?: string | null
  standardBomSnapshot?: ApiBomItem[]
  nonStandardBomSnapshot?: ApiBomItem[]
  electricalBomSnapshot?: ApiBomItem[]
  createdAt?: string | null
}

interface ApiBomVersion extends Omit<BomVersion, 'kind' | 'state' | 'items'> {
  kind: string | number
  state: string | number
  items: ApiBomItem[]
}

interface ApiDrawingReviewPackage extends Omit<DrawingReviewPackage, 'state' | 'items' | 'markups'> {
  state: string | number
  items: Array<Omit<DrawingReviewPackage['items'][number], 'modelState' | 'drawingState'> & { modelState: string | number; drawingState: string | number }>
  markups: Array<Omit<DrawingReviewPackage['markups'][number], 'target' | 'severity' | 'state'> & { target: string | number; severity: string | number; state: string | number }>
}

interface ApiDrawingReviewCandidate extends Omit<DrawingReviewCandidate, 'state' | 'bomKinds'> {
  state: string | number
  bomKinds: Array<string | number>
}

async function requestJson<T>(path: string, init: RequestInit = {}, token?: string): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  if (token) headers.set('Authorization', `Bearer ${token}`)
  const activeCompanyId = window.localStorage.getItem('pdm_active_organization')
  if (activeCompanyId && (token || path === '/api/auth/resume')) headers.set('X-Company-Id', activeCompanyId)

  const response = await fetch(`${apiBase}${path}`, { ...init, headers, cache: 'no-store' })
  if (!response.ok) {
    if (response.status === 401 && path !== '/api/auth/login' && path !== '/api/auth/resume') {
      window.dispatchEvent(new CustomEvent('pdm-session-expired'))
      throw new PdmApiError('登录已过期，请重新登录。', response.status)
    }
    let message = `PLM API请求失败（${response.status}）`
    try {
      const problem = await response.json() as { title?: string; detail?: string; message?: string; errors?: Record<string, string[]> }
      const validation = problem.errors ? Object.values(problem.errors).flat().join('；') : ''
      message = problem.detail || problem.message || validation || problem.title || message
    } catch {
      // Keep the status-based message when the response has no JSON body.
    }
    throw new PdmApiError(message, response.status)
  }

  if (response.status === 204) return undefined as T
  const responseBody = await response.text()
  return (responseBody ? JSON.parse(responseBody) : null) as T
}

function authenticatedHeaders(token: string): Headers {
  const headers = new Headers({ Authorization: `Bearer ${token}` })
  const activeCompanyId = window.localStorage.getItem('pdm_active_organization')
  if (activeCompanyId) headers.set('X-Company-Id', activeCompanyId)
  return headers
}

export function readValidationCheckCatalog(token: string, includeInactive = false): Promise<ValidationCheckCatalog> {
  return requestJson<ValidationCheckCatalog>(`/api/validation-check-catalog${includeInactive ? '?includeInactive=true' : ''}`, {}, token)
}

export function saveValidationCheckCategory(categoryId: string | null, input: SaveValidationCheckCategoryInput, token: string): Promise<ValidationCheckCategory> {
  return requestJson<ValidationCheckCategory>(categoryId ? `/api/validation-check-catalog/categories/${categoryId}` : '/api/validation-check-catalog/categories', {
    method: categoryId ? 'PUT' : 'POST',
    body: JSON.stringify(input),
  }, token)
}

export function deleteValidationCheckCategory(categoryId: string, expectedRowVersion: number, token: string): Promise<void> {
  return requestJson<void>(`/api/validation-check-catalog/categories/${categoryId}?expectedRowVersion=${expectedRowVersion}`, { method: 'DELETE' }, token)
}

export function saveValidationCheckItem(itemId: string | null, input: SaveValidationCheckItemInput, token: string): Promise<ValidationCheckItem> {
  return requestJson<ValidationCheckItem>(itemId ? `/api/validation-check-catalog/items/${itemId}` : '/api/validation-check-catalog/items', {
    method: itemId ? 'PUT' : 'POST',
    body: JSON.stringify(input),
  }, token)
}

export function deleteValidationCheckItem(itemId: string, expectedRowVersion: number, token: string): Promise<void> {
  return requestJson<void>(`/api/validation-check-catalog/items/${itemId}?expectedRowVersion=${expectedRowVersion}`, { method: 'DELETE' }, token)
}

export function readProjectValidationPlan(projectId: string, token: string): Promise<ProjectValidationPlan | null> {
  return requestJson<ProjectValidationPlan | null>(`/api/projects/${projectId}/validation-plan`, {}, token)
}

export function saveProjectValidationPlan(projectId: string, input: SaveProjectValidationPlanInput, token: string): Promise<ProjectValidationPlan> {
  return requestJson<ProjectValidationPlan>(`/api/projects/${projectId}/validation-plan`, { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function createProjectValidationPlanRevision(projectId: string, expectedRowVersion: number, token: string): Promise<ProjectValidationPlan> {
  return requestJson<ProjectValidationPlan>(`/api/projects/${projectId}/validation-plan/revisions?expectedRowVersion=${expectedRowVersion}`, { method: 'POST' }, token)
}

export function submitProjectValidationPlan(projectId: string, expectedRowVersion: number, token: string): Promise<ProjectValidationPlan> {
  return requestJson<ProjectValidationPlan>(`/api/projects/${projectId}/validation-plan/submit?expectedRowVersion=${expectedRowVersion}`, { method: 'POST' }, token)
}

export function decideValidationPlanApproval(taskId: string, decision: 'Approved' | 'Rejected', comment: string, token: string): Promise<ProjectValidationPlan> {
  return requestJson<ProjectValidationPlan>(`/api/validation-plan-approval-tasks/${taskId}/decision`, { method: 'POST', body: JSON.stringify({ decision: decision === 'Approved' ? 0 : 1, comment }) }, token)
}

export function listMyValidationPlanApprovalTasks(token: string): Promise<ValidationPlanApprovalTaskSummary[]> {
  return requestJson<ValidationPlanApprovalTaskSummary[]>('/api/validation-plan-approval-tasks/mine', {}, token)
}

export async function uploadValidationPlanAttachment(planId: string, kind: 'PlanDocument' | 'Evidence', file: File, token: string): Promise<ValidationPlanAttachment> {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  const sha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, '0')).join('').toUpperCase()
  const session = await requestJson<{ id: string; chunkSize: number }>(`/api/validation-plans/${planId}/attachment-uploads`, {
    method: 'POST', body: JSON.stringify({ kind, fileName: file.name, totalLength: file.size, sha256 }),
  }, token)
  const chunks = Math.ceil(file.size / session.chunkSize)
  for (let index = 0; index < chunks; index++) {
    const body = file.slice(index * session.chunkSize, Math.min(file.size, (index + 1) * session.chunkSize))
    const response = await fetch(`${apiBase}/api/uploads/sessions/${session.id}/chunks/${index}`, { method: 'PUT', headers: authenticatedHeaders(token), body })
    if (!response.ok) throw new PdmApiError(`验证计划文件上传失败（${response.status}）`, response.status)
  }
  return requestJson<ValidationPlanAttachment>(`/api/validation-plans/${planId}/attachment-uploads/${session.id}/complete`, { method: 'POST', body: JSON.stringify({ kind }) }, token)
}

export async function downloadValidationPlanAttachment(attachmentId: string, fileName: string, token: string): Promise<void> {
  const response = await fetch(`${apiBase}/api/validation-plan-attachments/${attachmentId}/download`, { headers: authenticatedHeaders(token) })
  if (!response.ok) throw new PdmApiError(`验证计划附件下载失败（${response.status}）`, response.status)
  const url = URL.createObjectURL(await response.blob())
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  anchor.click()
  URL.revokeObjectURL(url)
}

export function recognizeValidationPlanAttachment(attachmentId: string, token: string): Promise<ValidationPlanRecognitionDraft> {
  return requestJson<ValidationPlanRecognitionDraft>(`/api/validation-plan-attachments/${attachmentId}/recognize`, { method: 'POST' }, token)
}

export function readValidationPlanExecutionRecords(planId: string, token: string): Promise<ValidationPlanExecutionRecord[]> {
  return requestJson<ValidationPlanExecutionRecord[]>(`/api/validation-plans/${planId}/execution-records`, {}, token)
}

export function confirmValidationPlanExecution(planId: string, input: ConfirmValidationPlanExecutionInput, token: string): Promise<ValidationPlanExecutionRecord> {
  return requestJson<ValidationPlanExecutionRecord>(`/api/validation-plans/${planId}/execution-records`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export async function exportProjectValidationPlan(projectId: string, projectCode: string, token: string): Promise<void> {
  const response = await fetch(`${apiBase}/api/projects/${projectId}/validation-plan/export`, { headers: authenticatedHeaders(token) })
  if (!response.ok) {
    let message = `验证计划导出失败（${response.status}）`
    try {
      const problem = await response.json() as { title?: string; detail?: string }
      message = problem.detail || problem.title || message
    } catch { /* 保留状态码信息。 */ }
    throw new PdmApiError(message, response.status)
  }
  const url = URL.createObjectURL(await response.blob())
  const disposition = response.headers.get('content-disposition') ?? ''
  const encodedName = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1]
  const fileName = encodedName ? decodeURIComponent(encodedName) : `${projectCode}_验证计划.xlsx`
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

export function listMaterials(token: string, query = '', includeArchived = false, limit = 100, categoryCode = ''): Promise<PdmMaterial[]> {
  const parameters = new URLSearchParams()
  if (query.trim()) parameters.set('query', query.trim())
  if (includeArchived) parameters.set('includeArchived', 'true')
  if (limit !== 100) parameters.set('limit', String(limit))
  if (categoryCode.trim()) parameters.set('categoryCode', categoryCode.trim())
  const suffix = parameters.size ? `?${parameters}` : ''
  return requestJson<PdmMaterial[]>(`/api/materials${suffix}`, {}, token)
}

export function listPendingMasterMaterials(token: string): Promise<PdmMaterial[]> {
  return requestJson<PdmMaterial[]>('/api/materials/pending-approval', {}, token)
}

export function listBomHeaderMaterialDirectory(token: string): Promise<import('./types').BomHeaderMaterialDirectoryItem[]> {
  return requestJson('/api/materials/bom-headers', {}, token)
}

export function listMaterialPage(token: string, input: { query?: string; categoryCode?: string; brand?: string; includeArchived?: boolean; page?: number; pageSize?: number; createdAtOrder?: 'asc' | 'desc'; ordinaryOnly?: boolean }): Promise<MaterialPage> {
  const parameters = new URLSearchParams()
  if (input.query?.trim()) parameters.set('query', input.query.trim())
  if (input.categoryCode?.trim()) parameters.set('categoryCode', input.categoryCode.trim())
  if (input.brand?.trim()) parameters.set('brand', input.brand.trim())
  if (input.includeArchived) parameters.set('includeArchived', 'true')
  if (input.page && input.page !== 1) parameters.set('page', String(input.page))
  if (input.pageSize && input.pageSize !== 50) parameters.set('pageSize', String(input.pageSize))
  if (input.createdAtOrder) parameters.set('createdAtOrder', input.createdAtOrder)
  if (input.ordinaryOnly) parameters.set('ordinaryOnly', 'true')
  const suffix = parameters.size ? `?${parameters}` : ''
  return requestJson<MaterialPage>(`/api/materials/page${suffix}`, {}, token)
}

export function getMaterialDuplicateRules(token: string): Promise<MaterialDuplicateRule[]> {
  return requestJson<MaterialDuplicateRule[]>('/api/material-duplicate-rules', {}, token)
}

export function updateMaterialDuplicateRules(rules: MaterialDuplicateRule[], token: string): Promise<MaterialDuplicateRule[]> {
  return requestJson<MaterialDuplicateRule[]>('/api/material-duplicate-rules', { method: 'PUT', body: JSON.stringify({ rules }) }, token)
}

export function listProjectBomHeaders(projectId: string, token: string): Promise<ProjectBomHeader[]> {
  return requestJson<ProjectBomHeader[]>(`/api/projects/${projectId}/bom-headers`, {}, token)
}

export function retryProjectBomHeaderAutomatic(projectId: string, kind: BomHeaderKind, applicationId: string, expectedRowVersion: number, token: string): Promise<ProjectBomHeader> {
  return requestJson<ProjectBomHeader>(`/api/projects/${projectId}/bom-headers/${kind}/retry-automatic`, {
    method: 'POST', body: JSON.stringify({ applicationId, expectedRowVersion, confirmation: '确认重试料号自动处理' }),
  }, token)
}

export function bindProjectBomHeaderMaterial(projectId: string, kind: BomHeaderKind, materialId: string, expectedRowVersion: number, token: string): Promise<ProjectBomHeader> {
  return requestJson<ProjectBomHeader>(`/api/projects/${projectId}/bom-headers/${kind}/material`, {
    method: 'PUT',
    body: JSON.stringify({ materialId, expectedRowVersion }),
  }, token)
}

export function generateProjectBomHeaderHierarchy(projectId: string, token: string): Promise<{ rootProjectId: string; expectedCount: number; generatedCount: number; existingCount: number; headers: ProjectBomHeader[]; autoApprovedCount: number; queuedSyncCount: number; automaticBatchId?: string | null }> {
  return requestJson(`/api/projects/${projectId}/bom-headers/generate-hierarchy`, { method: 'POST' }, token)
}

export function createMaterial(input: SaveMaterialInput, token: string): Promise<PdmMaterial> {
  return requestJson<PdmMaterial>('/api/materials', { method: 'POST', body: JSON.stringify(input) }, token)
}

export function updateMaterial(materialId: string, input: SaveMaterialInput, token: string): Promise<PdmMaterial> {
  return requestJson<PdmMaterial>(`/api/materials/${materialId}`, { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function changeApprovedMaterial(materialId: string, input: SaveMaterialInput, token: string): Promise<{ material: PdmMaterial; task?: MaterialSyncTask | null }> {
  return requestJson(`/api/materials/${materialId}/change`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export function deleteMaterial(materialId: string, expectedRowVersion: number, token: string): Promise<MaterialRemovalResult> {
  return requestJson<MaterialRemovalResult>(`/api/materials/${materialId}?expectedRowVersion=${expectedRowVersion}`, { method: 'DELETE' }, token)
}

export function getMaterialRemovalReadiness(materialId: string, token: string): Promise<MaterialRemovalReadiness> {
  return requestJson<MaterialRemovalReadiness>(`/api/materials/${materialId}/removal-readiness`, {}, token)
}

export function archiveMaterial(materialId: string, expectedRowVersion: number, token: string): Promise<PdmMaterial> {
  return requestJson<PdmMaterial>(`/api/materials/${materialId}/archive?expectedRowVersion=${expectedRowVersion}`, { method: 'POST' }, token)
}

export function reactivateMaterial(materialId: string, expectedRowVersion: number, token: string): Promise<PdmMaterial> {
  return requestJson<PdmMaterial>(`/api/materials/${materialId}/reactivate?expectedRowVersion=${expectedRowVersion}`, { method: 'POST' }, token)
}

export function listMaterialAttachments(materialId: string, token: string, kind?: MaterialAttachmentKind): Promise<MaterialAttachment[]> {
  const query = kind ? `?kind=${encodeURIComponent(kind)}` : ''
  return requestJson<MaterialAttachment[]>(`/api/materials/${materialId}/attachments${query}`, {}, token).catch(error => {
    if (error instanceof PdmApiError && error.status === 404) return []
    throw error
  })
}

export async function uploadMaterialAttachment(materialId: string, kind: MaterialAttachmentKind, file: File, token: string, onProgress?: (percent: number) => void): Promise<MaterialAttachment> {
  onProgress?.(0)
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  const sha256 = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('').toUpperCase()
  const session = await requestJson<{ id: string; chunkSize: number }>(`/api/materials/${materialId}/attachments/uploads`, {
    method: 'POST', body: JSON.stringify({ kind, fileName: file.name, totalLength: file.size, sha256 }),
  }, token)
  const chunks = Math.ceil(file.size / session.chunkSize)
  for (let index = 0; index < chunks; index++) {
    const body = file.slice(index * session.chunkSize, Math.min(file.size, (index + 1) * session.chunkSize))
    const response = await fetch(`${apiBase}/api/material-attachment-uploads/${session.id}/chunks/${index}`, { method: 'PUT', headers: authenticatedHeaders(token), body })
    if (!response.ok) throw new PdmApiError(`附件分块${index + 1}上传失败（${response.status}）`, response.status)
    onProgress?.(Math.round(((index + 1) / chunks) * 100))
  }
  return requestJson<MaterialAttachment>(`/api/material-attachment-uploads/${session.id}/complete`, { method: 'POST' }, token)
}

export async function downloadMaterialAttachment(materialId: string, attachment: MaterialAttachment, token: string): Promise<void> {
  const response = await fetch(`${apiBase}/api/materials/${materialId}/attachments/${attachment.id}/file`, { headers: authenticatedHeaders(token) })
  if (!response.ok) throw new PdmApiError(`附件下载失败（${response.status}）`, response.status)
  const url = URL.createObjectURL(await response.blob())
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = attachment.originalFileName
  anchor.click()
  URL.revokeObjectURL(url)
}

export async function materialAttachmentObjectUrl(materialId: string, attachmentId: string, token: string, thumbnailMaxSize?: number): Promise<string> {
  const response = await fetch(`${apiBase}/api/materials/${materialId}/attachments/${attachmentId}/file`, { headers: authenticatedHeaders(token) })
  if (!response.ok) throw new PdmApiError(`图片加载失败（${response.status}）`, response.status)
  let imageBlob = await response.blob()
  if (thumbnailMaxSize && typeof createImageBitmap === 'function') {
    try {
      const bitmap = await createImageBitmap(imageBlob)
      const scale = Math.min(1, thumbnailMaxSize / Math.max(bitmap.width, bitmap.height))
      const canvas = document.createElement('canvas')
      canvas.width = Math.max(1, Math.round(bitmap.width * scale))
      canvas.height = Math.max(1, Math.round(bitmap.height * scale))
      canvas.getContext('2d')?.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
      const thumbnail = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/webp', 0.82))
      bitmap.close()
      if (thumbnail) imageBlob = thumbnail
    } catch {
      // Older browsers can still display the original authenticated image.
    }
  }
  return URL.createObjectURL(imageBlob)
}

export function setMaterialCover(materialId: string, attachmentId: string | null, expectedRowVersion: number, token: string): Promise<PdmMaterial> {
  return requestJson<PdmMaterial>(`/api/materials/${materialId}/cover`, {
    method: 'PUT', body: JSON.stringify({ attachmentId, expectedRowVersion }),
  }, token)
}

export function listStandardLibraryCategories(token: string, includeInactive = false): Promise<StandardLibraryCategory[]> {
  return requestJson<StandardLibraryCategory[]>(`/api/standard-library/categories${includeInactive ? '?includeInactive=true' : ''}`, {}, token)
}

export function saveStandardLibraryCategory(input: { name: string; parentId?: string | null; sortOrder: number; isActive: boolean; expectedRowVersion?: number | null }, token: string, categoryId?: string): Promise<StandardLibraryCategory> {
  return requestJson<StandardLibraryCategory>(categoryId ? `/api/standard-library/categories/${categoryId}` : '/api/standard-library/categories', {
    method: categoryId ? 'PUT' : 'POST', body: JSON.stringify(input),
  }, token)
}

export function deleteStandardLibraryCategory(categoryId: string, expectedRowVersion: number, token: string): Promise<void> {
  return requestJson<void>(`/api/standard-library/categories/${categoryId}?expectedRowVersion=${expectedRowVersion}`, { method: 'DELETE' }, token)
}

export function listStandardLibraryMaterials(token: string, input: { categoryId?: string; query?: string; brand?: string; recommendedOnly?: boolean; page?: number; pageSize?: number }): Promise<StandardLibraryMaterialPage> {
  const parameters = new URLSearchParams()
  if (input.categoryId) parameters.set('categoryId', input.categoryId)
  if (input.query?.trim()) parameters.set('query', input.query.trim())
  if (input.brand?.trim()) parameters.set('brand', input.brand.trim())
  if (input.recommendedOnly) parameters.set('recommendedOnly', 'true')
  if (input.page) parameters.set('page', String(input.page))
  if (input.pageSize) parameters.set('pageSize', String(input.pageSize))
  return requestJson<StandardLibraryMaterialPage>(`/api/standard-library/materials?${parameters}`, {}, token)
}

export function addStandardLibraryMaterials(categoryIds: string[], materialIds: string[], token: string): Promise<void> {
  return requestJson<void>('/api/standard-library/memberships', { method: 'POST', body: JSON.stringify({ categoryIds, materialIds }) }, token)
}

export function removeStandardLibraryMaterial(categoryId: string, materialId: string, token: string): Promise<void> {
  return requestJson<void>(`/api/standard-library/categories/${categoryId}/materials/${materialId}`, { method: 'DELETE' }, token)
}

export function setStandardLibraryRecommendation(materialId: string, isRecommended: boolean, expectedRowVersion: number, token: string): Promise<PdmMaterial> {
  return requestJson<PdmMaterial>(`/api/standard-library/materials/${materialId}/recommended`, {
    method: 'PUT', body: JSON.stringify({ isRecommended, expectedRowVersion }),
  }, token)
}

export function listEngineeringKits(token: string, releasedOnly = true): Promise<EngineeringKit[]> {
  return requestJson<EngineeringKit[]>(`/api/engineering-kits?releasedOnly=${releasedOnly}`, {}, token)
}

export function saveEngineeringKit(input: {
  name: string
  description?: string
  changeNote?: string
  components: Array<{ materialId: string; quantity: number; isOptional: boolean; sortOrder: number }>
  expectedRowVersion?: number
}, token: string, kitId?: string): Promise<EngineeringKit> {
  return requestJson<EngineeringKit>(kitId ? `/api/engineering-kits/${kitId}` : '/api/engineering-kits', {
    method: kitId ? 'PUT' : 'POST', body: JSON.stringify(input),
  }, token)
}

export function publishEngineeringKit(kitId: string, expectedRowVersion: number, token: string): Promise<EngineeringKit> {
  return requestJson<EngineeringKit>(`/api/engineering-kits/${kitId}/publish`, {
    method: 'POST', body: JSON.stringify({ expectedRowVersion }),
  }, token)
}

export function expandEngineeringKit(kitId: string, input: { revisionId?: string; quantity: number; selectedOptionalComponentIds: string[] }, token: string): Promise<EngineeringKitExpansion> {
  return requestJson<EngineeringKitExpansion>(`/api/engineering-kits/${kitId}/expand`, {
    method: 'POST', body: JSON.stringify(input),
  }, token)
}

export function listMaterialRelationTemplates(token: string, includeDraft = false): Promise<MaterialRelationTemplate[]> {
  return requestJson<MaterialRelationTemplate[]>(`/api/material-relations/templates${includeDraft ? '?includeDraft=true' : ''}`, {}, token)
}

export function saveMaterialRelationTemplate(input: SaveMaterialRelationTemplateInput, token: string, templateId?: string): Promise<MaterialRelationTemplate> {
  return requestJson<MaterialRelationTemplate>(templateId ? `/api/material-relations/templates/${templateId}/draft` : '/api/material-relations/templates', {
    method: templateId ? 'PUT' : 'POST', body: JSON.stringify(input),
  }, token)
}

export function publishMaterialRelationTemplate(templateId: string, revisionId: string, expectedRowVersion: number, token: string): Promise<MaterialRelationTemplate> {
  return requestJson<MaterialRelationTemplate>(`/api/material-relations/templates/${templateId}/publish`, {
    method: 'POST', body: JSON.stringify({ revisionId, expectedRowVersion }),
  }, token)
}

export function getMaterialRelationCompleteness(projectId: string, token: string): Promise<MaterialRelationCompleteness> {
  return requestJson<MaterialRelationCompleteness>(`/api/material-relations/projects/${projectId}/completeness`, {}, token)
}

export function applyMaterialRelations(projectId: string, mainMaterials: Array<{ mainBomItemId: string; choices: Array<{ groupId: string; optionIds: string[]; confirmNoAccessory?: boolean; noAccessoryReason?: string | null }> }>, token: string): Promise<MaterialRelationCompleteness> {
  return requestJson<MaterialRelationCompleteness>(`/api/material-relations/projects/${projectId}/apply`, {
    method: 'POST', body: JSON.stringify({ mainMaterials }),
  }, token)
}

export function linkBomMaterial(projectId: string, bomItemId: string, materialId: string, token: string): Promise<PdmMaterial> {
  return requestJson<PdmMaterial>('/api/materials/link-bom', { method: 'POST', body: JSON.stringify({ projectId, bomItemId, materialId }) }, token)
}

export function resolveBomMaterialCodes(projectId: string, bomItemIds: string[], token: string): Promise<MaterialCodeResolution[]> {
  return requestJson('/api/material-code/resolve', { method: 'POST', body: JSON.stringify({ projectId, bomItemIds }) }, token)
}

export function applyForBomMaterialCodes(projectId: string, bomItemIds: string[], token: string): Promise<MaterialCodeResolution[]> {
  return requestJson('/api/material-code/applications', { method: 'POST', body: JSON.stringify({ projectId, bomItemIds }) }, token)
}

export async function listMaterialCodeApplications(token: string, projectId?: string, status?: MaterialCodeApplicationStatus): Promise<MaterialCodeApplication[]> {
  const parameters = new URLSearchParams()
  if (projectId) parameters.set('projectId', projectId)
  if (status) parameters.set('status', status)
  return requestJson<MaterialCodeApplication[]>(`/api/material-code/applications${parameters.size ? `?${parameters}` : ''}`, {}, token).catch(error => {
    if (error instanceof PdmApiError && error.status === 404) return []
    throw error
  })
}

export function decideMaterialCodeApplication(applicationId: string, expectedRowVersion: number, approved: boolean, comment: string, token: string): Promise<MaterialCodeDecisionResult> {
  return requestJson(`/api/material-code/applications/${applicationId}/decision`, { method: 'POST', body: JSON.stringify({ expectedRowVersion, approved, comment }) }, token)
}

export function approveMaterial(materialId: string, expectedRowVersion: number, token: string): Promise<{ material: PdmMaterial; task: MaterialSyncTask }> {
  return requestJson(`/api/materials/${materialId}/approve?expectedRowVersion=${expectedRowVersion}`, { method: 'POST' }, token)
}

export function listMaterialCategoryRules(token: string): Promise<MaterialCategoryRule[]> {
  return requestJson<MaterialCategoryRule[]>('/api/material-category-rules', {}, token)
}

export function saveMaterialCategoryRule(rule: MaterialCategoryRule, token: string): Promise<MaterialCategoryRule> {
  return requestJson<MaterialCategoryRule>(`/api/material-category-rules/${rule.pdmKind}`, { method: 'PUT', body: JSON.stringify(rule) }, token)
}

export function listMaterialCategories(token: string, includeHidden = false): Promise<MaterialCategory[]> {
  return requestJson<MaterialCategory[]>(`/api/material-categories?includeHidden=${includeHidden}`, {}, token)
}

export function getMaterialNumberingSettings(token: string): Promise<MaterialNumberingSettings> {
  return requestJson<MaterialNumberingSettings>('/api/material-numbering-settings', {}, token)
}

export function updateMaterialNumberingSettings(startSequence: number, token: string): Promise<MaterialNumberingSettings> {
  return requestJson<MaterialNumberingSettings>('/api/material-numbering-settings', {
    method: 'PUT', body: JSON.stringify({ startSequence }),
  }, token)
}

export function saveMaterialCategory(category: MaterialCategory, token: string, creating = false): Promise<MaterialCategory> {
  const method = creating ? 'POST' : 'PUT'
  const path = creating ? '/api/material-categories' : `/api/material-categories/${encodeURIComponent(category.code)}`
  return requestJson<MaterialCategory>(path, { method, body: JSON.stringify({ ...category, expectedRowVersion: creating ? null : category.rowVersion }) }, token)
}

export function calibrateMaterialCategoryCounter(code: string, lastMaterialCode: string, token: string): Promise<MaterialCategory> {
  return requestJson<MaterialCategory>(`/api/material-categories/${encodeURIComponent(code)}/counter`, {
    method: 'PUT', body: JSON.stringify({ lastMaterialCode }),
  }, token)
}

export function listMaterialSyncTasks(token: string, ordinaryOnly = false): Promise<MaterialSyncTask[]> {
  return requestJson<MaterialSyncTask[]>(`/api/material-sync-tasks${ordinaryOnly ? '?ordinaryOnly=true' : ''}`, {}, token)
}

export function retryMaterialSyncTask(taskId: string, token: string): Promise<MaterialSyncTask> {
  return requestJson<MaterialSyncTask>(`/api/material-sync-tasks/${taskId}/retry`, { method: 'POST' }, token)
}

export function executeMaterialSyncTask(taskId: string, token: string): Promise<MaterialSyncExecutionResult> {
  return requestJson<MaterialSyncExecutionResult>(`/api/material-sync-tasks/${taskId}/execute`, { method: 'POST' }, token)
}

export function createMaterialSyncBatch(taskIds: string[], token: string): Promise<MaterialSyncBatch> {
  return requestJson<MaterialSyncBatch>('/api/material-sync-batches', { method: 'POST', body: JSON.stringify({ taskIds }) }, token)
}

export function getMaterialSyncBatch(batchId: string, token: string): Promise<MaterialSyncBatch> {
  return requestJson<MaterialSyncBatch>(`/api/material-sync-batches/${batchId}`, {}, token)
}

export function listMaterialSyncBatches(token: string): Promise<MaterialSyncBatch[]> {
  return requestJson<MaterialSyncBatch[]>('/api/material-sync-batches', {}, token)
}

export function getU9MaterialIntegration(token: string): Promise<U9MaterialIntegrationSettings> {
  return requestJson<U9MaterialIntegrationSettings>('/api/u9-material-integration', {}, token)
}

export function getU9MaterialFullSyncStatus(token: string): Promise<U9MaterialFullSyncStatusResponse> {
  return requestJson<U9MaterialFullSyncStatusResponse>('/api/u9-material-full-sync/status', {}, token)
}

export function listMaterialInventory(filters: U9InventoryFilters, token: string): Promise<U9InventoryPage> {
  const params = new URLSearchParams()
  if (filters.similarSpecification !== undefined) params.set('similarSpecification', filters.similarSpecification.trim())
  if (filters.materialCode?.trim()) params.set('materialCode', filters.materialCode.trim())
  if (filters.itemName?.trim()) params.set('itemName', filters.itemName.trim())
  if (filters.specification?.trim()) params.set('specification', filters.specification.trim())
  if (filters.brand?.trim()) params.set('brand', filters.brand.trim())
  if (filters.warehouse?.trim()) params.set('warehouse', filters.warehouse.trim())
  if (filters.projectCode?.trim()) params.set('projectCode', filters.projectCode.trim())
  if (filters.subproject?.trim()) params.set('subproject', filters.subproject.trim())
  params.set('positiveStockOnly', String(filters.positiveStockOnly ?? true))
  params.set('page', String(filters.page ?? 1))
  params.set('pageSize', String(filters.pageSize ?? 50))
  return requestJson<U9InventoryPage>(`/api/material-inventory?${params.toString()}`, {}, token)
}

export function refreshMaterialInventory(materialCode: string, token: string): Promise<U9InventoryPage> {
  return requestJson<U9InventoryPage>(`/api/material-inventory/${encodeURIComponent(materialCode)}/refresh`, { method: 'POST' }, token)
}

export function getU9InventorySyncStatus(token: string): Promise<U9InventorySyncStatusResponse> {
  return requestJson<U9InventorySyncStatusResponse>('/api/u9-inventory-sync/status', {}, token)
}

export function updateU9InventorySyncSettings(settings: Pick<U9InventorySyncSettings, 'autoSyncEnabled' | 'syncIntervalMinutes' | 'queryPath'>, token: string): Promise<U9InventorySyncSettings> {
  return requestJson<U9InventorySyncSettings>('/api/u9-inventory-sync/settings', { method: 'PUT', body: JSON.stringify(settings) }, token)
}

export function startU9InventoryFullSync(token: string): Promise<{ message: string }> {
  return requestJson<{ message: string }>('/api/u9-inventory-sync/run', { method: 'POST' }, token)
}

export function getProjectProcurementTracking(projectId: string, token: string): Promise<ProjectProcurementTrackingResult> {
  return requestJson<ProjectProcurementTrackingResult>(`/api/projects/${projectId}/procurement-tracking`, {}, token)
}

export function refreshProjectProcurementTracking(projectId: string, token: string): Promise<{ message: string }> {
  return requestJson<{ message: string }>(`/api/projects/${projectId}/procurement-tracking/refresh`, { method: 'POST' }, token)
}

export function getU9ProcurementSyncStatus(token: string): Promise<U9ProcurementSyncStatusResponse> {
  return requestJson<U9ProcurementSyncStatusResponse>('/api/u9-procurement-sync/status', {}, token)
}

export function updateU9ProcurementSyncSettings(settings: Pick<U9ProcurementSyncSettings, 'autoSyncEnabled' | 'syncIntervalMinutes' | 'queryPath'>, token: string): Promise<U9ProcurementSyncSettings> {
  return requestJson<U9ProcurementSyncSettings>('/api/u9-procurement-sync/settings', { method: 'PUT', body: JSON.stringify(settings) }, token)
}

export function startU9ProcurementFullSync(token: string): Promise<{ message: string }> {
  return requestJson<{ message: string }>('/api/u9-procurement-sync/run', { method: 'POST' }, token)
}

export function startU9MaterialFullSync(token: string): Promise<{ message: string }> {
  return requestJson<{ message: string }>('/api/u9-material-full-sync/run', { method: 'POST' }, token)
}

export function updateU9MaterialIntegration(input: UpdateU9MaterialIntegrationInput, token: string): Promise<U9MaterialIntegrationSettings> {
  return requestJson<U9MaterialIntegrationSettings>('/api/u9-material-integration', { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function testU9MaterialIntegration(token: string): Promise<U9ConnectionTestResult> {
  return requestJson<U9ConnectionTestResult>('/api/u9-material-integration/test', { method: 'POST' }, token)
}

export function queryU9Material(materialCode: string, token: string): Promise<U9ItemQueryResult> {
  return requestJson<U9ItemQueryResult>(`/api/u9-material-query/${encodeURIComponent(materialCode)}`, {}, token)
}

export function queryU9Bom(input: U9BomQueryInput, token: string): Promise<U9BomQueryExecution> {
  return requestJson<U9BomQueryExecution>('/api/u9-boms/query', {
    method: 'POST', body: JSON.stringify(input),
  }, token)
}

export function previewU9BomWrite(input: U9BomWriteInput, token: string): Promise<U9BomWritePreview> {
  return requestJson<U9BomWritePreview>('/api/u9-boms/write-preview', {
    method: 'POST', body: JSON.stringify(input),
  }, token)
}

export function executeU9BomWrite(input: U9BomWriteInput, requestSha256: string, confirmation: string, token: string): Promise<U9BomWriteExecution> {
  return requestJson<U9BomWriteExecution>('/api/u9-boms/write-execute', {
    method: 'POST', body: JSON.stringify({ command: input, requestSha256, confirmation }),
  }, token)
}

export function previewProjectBomU9Sync(projectId: string, kind: BomHeaderKind, token: string): Promise<ProjectBomU9SyncPreview> {
  return requestJson<ProjectBomU9SyncPreview>(`/api/projects/${projectId}/bom-headers/${kind}/u9-preview`, { method: 'POST' }, token)
}

export function executeProjectBomU9Sync(projectId: string, kind: BomHeaderKind, requestSha256: string, confirmation: string, token: string): Promise<ProjectBomU9SyncExecution> {
  return requestJson<ProjectBomU9SyncExecution>(`/api/projects/${projectId}/bom-headers/${kind}/u9-execute`, {
    method: 'POST', body: JSON.stringify({ requestSha256, confirmation }),
  }, token)
}

export function continueProjectBomU9Automation(projectId: string, token: string): Promise<ApprovalU9AutomationResult> {
  return requestJson<ApprovalU9AutomationResult>(`/api/material-code/projects/${projectId}/u9-continue`, { method: 'POST' }, token)
}

export function previewU9MaterialSample(categoryCodes: string[], limitPerCategory: number, token: string): Promise<U9MaterialSamplePreview> {
  return requestJson<U9MaterialSamplePreview>('/api/u9-material-sample/preview', {
    method: 'POST', body: JSON.stringify({ categoryCodes, limitPerCategory }),
  }, token)
}

export function importU9MaterialSample(categoryCodes: string[], limitPerCategory: number, token: string): Promise<U9MaterialSampleImportResult> {
  return requestJson<U9MaterialSampleImportResult>('/api/u9-material-sample/import', {
    method: 'POST', body: JSON.stringify({ categoryCodes, limitPerCategory }),
  }, token)
}

export function listDocumentVersions(documentId: string, token: string): Promise<DocumentVersionSummary[]> {
  return requestJson<DocumentVersionSummary[]>(`/api/documents/${documentId}/versions`, {}, token)
}

export async function readDocumentPreviewFile(documentId: string, versionId: string, token: string): Promise<Blob> {
  const response = await fetch(`${apiBase}/api/documents/${documentId}/versions/${versionId}/preview`, {
    headers: authenticatedHeaders(token),
    cache: 'no-store',
  })
  if (!response.ok) throw new PdmApiError(response.status === 404 ? '该版本尚未生成网页预览文件。' : `预览文件读取失败（${response.status}）`, response.status)
  return response.blob()
}

export function compareDocumentVersions(documentId: string, left: string, right: string, token: string): Promise<DocumentVersionComparison> {
  return requestJson<DocumentVersionComparison>(`/api/documents/${documentId}/versions/compare?left=${encodeURIComponent(left)}&right=${encodeURIComponent(right)}`, {}, token)
}

export function restoreDocumentVersion(documentId: string, versionId: string, changeNote: string, token: string): Promise<unknown> {
  return requestJson(`/api/documents/${documentId}/versions/${versionId}/restore`, { method: 'POST', body: JSON.stringify({ changeNote }) }, token)
}

export async function readDocumentVersionFile(documentId: string, versionId: string, token: string, download: boolean): Promise<Blob> {
  const response = await fetch(`${apiBase}/api/documents/${documentId}/versions/${versionId}/file?download=${download}`, { headers: authenticatedHeaders(token), cache: 'no-store' })
  if (!response.ok) throw new PdmApiError(`历史版本文件读取失败（${response.status}）`, response.status)
  return response.blob()
}

export async function checkHealth(signal?: AbortSignal): Promise<boolean> {
  try {
    const response = await fetch(`${apiBase}/health`, { signal })
    return response.ok
  } catch {
    return false
  }
}

export function login(username: string, password: string): Promise<AuthSession> {
  return requestJson<AuthSession>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ username: username.trim(), password }),
  })
}

export function resumeSession(resumeToken: string): Promise<AuthSession> {
  return requestJson<AuthSession>('/api/auth/resume', {
    method: 'POST',
    body: JSON.stringify({ resumeToken }),
  })
}

export function requestPasswordReset(username: string, displayName: string): Promise<boolean> {
  return requestJson('/api/auth/password-reset-request', { method: 'POST', body: JSON.stringify({ username: username.trim(), displayName: displayName.trim() }) })
}

export function getMyProfile(token: string): Promise<PdmUserProfile> {
  return requestJson('/api/auth/me', {}, token)
}

export function updateMyProfile(profile: Pick<PdmUserProfile, 'landline' | 'mobilePhone' | 'email' | 'gender' | 'nickname'>, token: string): Promise<PdmUserProfile> {
  return requestJson('/api/auth/profile', { method: 'PUT', body: JSON.stringify(profile) }, token)
}

export function changeMyPassword(currentPassword: string, password: string, token: string): Promise<AuthSession> {
  return requestJson('/api/auth/password', { method: 'PUT', body: JSON.stringify({ currentPassword, password }) }, token)
}

export function listPasswordResetTasks(token: string): Promise<PasswordResetTask[]> {
  return requestJson('/api/password-reset-requests', {}, token)
}

export function resetRequestedPassword(taskId: string, token: string): Promise<boolean> {
  return requestJson(`/api/password-reset-requests/${taskId}/reset`, { method: 'PUT' }, token)
}

export async function listProjects(token: string): Promise<ProjectSummary[]> {
  const projects = await requestJson<ApiProject[]>('/api/projects', {}, token)
  return projects.map(mapProject)
}

export function getProjectNumberingOptions(token: string): Promise<ProjectNumberingOptions> {
  return requestJson('/api/project-numbering/options', {}, token)
}

export function listCustomers(token: string): Promise<PdmCustomer[]> {
  return requestJson('/api/customers', {}, token)
}

export function getCrmIntegrationSettings(token: string): Promise<CrmIntegrationSettings> {
  return requestJson('/api/crm-integration', {}, token)
}

export function updateCrmIntegrationSettings(input: UpdateCrmIntegrationInput, token: string): Promise<CrmIntegrationSettings> {
  return requestJson('/api/crm-integration', { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function testCrmIntegration(token: string): Promise<CrmConnectionTestResult> {
  return requestJson('/api/crm-integration/test', { method: 'POST' }, token)
}

export function syncCrmCustomers(token: string): Promise<CrmCustomerSyncResult> {
  return requestJson('/api/crm-integration/sync', { method: 'POST' }, token)
}

export function listUsers(token: string): Promise<PdmUser[]> {
  return requestJson('/api/users', {}, token)
}

export function createUser(input: SavePdmUserInput, token: string): Promise<PdmUser> {
  return requestJson('/api/users', {
    method: 'POST', body: JSON.stringify(input),
  }, token)
}

export function updateUser(input: SavePdmUserInput, token: string): Promise<PdmUser> {
  return requestJson(`/api/users/${encodeURIComponent(input.username)}`, {
    method: 'PUT', body: JSON.stringify({ displayName: input.displayName, role: input.role, roles: input.roles, isActive: input.isActive, companyId: input.companyId, crossCompanyView: input.crossCompanyView, accessibleCompanyIds: input.accessibleCompanyIds }),
  }, token)
}

export function resetUserPassword(username: string, token: string): Promise<PdmUser> {
  return requestJson(`/api/users/${encodeURIComponent(username)}/reset-password`, { method: 'PUT' }, token)
}

export async function getOrganizationDirectory(token: string): Promise<OrganizationDirectory> {
  const directory = await requestJson<Omit<OrganizationDirectory, 'units'> & { units: Array<Omit<OrganizationUnit, 'kind'> & { kind: string | number }> }>('/api/organization-directory', {}, token)
  return { ...directory, units: directory.units.map(mapOrganizationUnit) }
}

export function getRolePermissionDirectory(token: string): Promise<RolePermissionDirectory> {
  return requestJson('/api/role-permissions', {}, token)
}

export function updateRolePermissions(role: string, permissions: string[], token: string): Promise<RolePermissionDirectory> {
  return requestJson(`/api/role-permissions/${encodeURIComponent(role)}`, {
    method: 'PUT', body: JSON.stringify({ permissions }),
  }, token)
}

export function createRole(input: CreateRoleInput, token: string): Promise<RolePermissionDirectory> {
  return requestJson('/api/role-permissions', { method: 'POST', body: JSON.stringify(input) }, token)
}

export function deleteRole(role: string, token: string): Promise<RolePermissionDirectory> {
  return requestJson(`/api/role-permissions/${encodeURIComponent(role)}`, { method: 'DELETE' }, token)
}

export function saveProjectOrganization(input: SaveProjectOrganizationInput, token: string): Promise<ProjectOrganization> {
  return requestJson(input.id ? `/api/organizations/${input.id}` : '/api/organizations', {
    method: input.id ? 'PUT' : 'POST', body: JSON.stringify(input),
  }, token)
}

export async function saveOrganizationUnit(input: SaveOrganizationUnitInput, token: string): Promise<OrganizationUnit> {
  const kind = ({ BusinessDivision: 0, Department: 1, Team: 2 } as const)[input.kind]
  const saved = await requestJson<Omit<OrganizationUnit, 'kind'> & { kind: string | number }>(input.id ? `/api/organization-units/${input.id}` : '/api/organization-units', {
    method: input.id ? 'PUT' : 'POST', body: JSON.stringify({ ...input, parentUnitId: input.parentUnitId || null, kind }),
  }, token)
  return mapOrganizationUnit(saved)
}

function mapOrganizationUnit(unit: Omit<OrganizationUnit, 'kind'> & { kind: string | number }): OrganizationUnit {
  return { ...unit, kind: typeof unit.kind === 'string' ? unit.kind as OrganizationUnit['kind'] : ['BusinessDivision', 'Department', 'Team'][unit.kind] as OrganizationUnit['kind'] }
}

export function updateOrganizationMemberships(username: string, unitIds: string[], primaryUnitId: string, token: string): Promise<OrganizationDirectory> {
  return requestJson(`/api/organization-users/${encodeURIComponent(username)}/memberships`, {
    method: 'PUT', body: JSON.stringify({ unitIds, primaryUnitId }),
  }, token)
}

export function updateOrganizationUnitManagers(unitId: string, primaryManager: string, collaborativeManagers: string[], token: string): Promise<OrganizationDirectory> {
  return requestJson(`/api/organization-units/${unitId}/managers`, {
    method: 'PUT', body: JSON.stringify({ primaryManager, collaborativeManagers }),
  }, token)
}

export async function updateProjectExecutionUnit(projectId: string, executionUnitId: string, token: string): Promise<ProjectSummary> {
  return mapProject(await requestJson<ApiProject>(`/api/projects/${projectId}/execution-unit`, { method: 'PUT', body: JSON.stringify({ executionUnitId }) }, token))
}

export async function updateProject(projectId: string, input: UpdateProjectInput, token: string): Promise<ProjectSummary> {
  return mapProject(await requestJson<ApiProject>(`/api/projects/${projectId}/details`, { method: 'PUT', body: JSON.stringify(input) }, token))
}

export async function updateMainProjectStaffing(projectId: string, input: MainProjectStaffingInput, token: string): Promise<ProjectSummary> {
  return mapProject(await requestJson<ApiProject>(`/api/projects/${projectId}/staffing`, { method: 'PUT', body: JSON.stringify(input) }, token))
}

export async function updateChildProjectDesigners(projectId: string, designers: string[], token: string): Promise<ProjectSummary> {
  return mapProject(await requestJson<ApiProject>(`/api/projects/${projectId}/designers`, { method: 'PUT', body: JSON.stringify({ designers }) }, token))
}

export async function updateChildProjectManager(projectId: string, projectManager: string, token: string): Promise<ProjectSummary> {
  return mapProject(await requestJson<ApiProject>(`/api/projects/${projectId}/manager`, { method: 'PUT', body: JSON.stringify({ projectManager }) }, token))
}

export function getSystemSettings(token: string): Promise<PdmSystemSettings> {
  return requestJson('/api/system-settings', {}, token)
}

export function getBomValidationRules(token: string): Promise<BomValidationRules> {
  return requestJson('/api/bom-validation-rules', {}, token)
}

export function updateSystemSettings(settings: PdmSystemSettings, token: string): Promise<PdmSystemSettings> {
  return requestJson('/api/system-settings', { method: 'PUT', body: JSON.stringify(settings) }, token)
}

export function listEquipmentTypes(token: string): Promise<EquipmentTypeDefinition[]> {
  return requestJson('/api/system-settings/equipment-types', {}, token)
}

export function saveEquipmentType(input: EquipmentTypeDefinition, token: string): Promise<EquipmentTypeDefinition> {
  return requestJson(`/api/system-settings/equipment-types/${input.code}`, {
    method: 'PUT',
    body: JSON.stringify({ name: input.name, isActive: input.isActive !== false }),
  }, token)
}

export function updateOrganizationCounters(organizationId: string, currentProjectSequence: number, currentSerialSequence: number, token: string): Promise<ProjectNumberingOptions> {
  return requestJson(`/api/project-numbering/organizations/${organizationId}/counters`, {
    method: 'PUT',
    body: JSON.stringify({ currentProjectSequence, currentSerialSequence }),
  }, token)
}

export async function createProject(input: CreateProjectInput, token: string): Promise<ProjectSummary> {
  const project = await requestJson<ApiProject>('/api/projects', {
    method: 'POST',
    body: JSON.stringify(input),
  }, token)
  return mapProject(project)
}

export async function createSubproject(parentProjectId: string, input: CreateSubprojectInput, token: string): Promise<ProjectSummary> {
  const project = await requestJson<ApiProject>(`/api/projects/${parentProjectId}/children`, {
    method: 'POST',
    body: JSON.stringify(input),
  }, token)
  return mapProject(project)
}

export async function deleteProject(projectId: string, token: string): Promise<void> {
  await requestJson(`/api/projects/${projectId}`, { method: 'DELETE' }, token)
}

export function previewProjectCopy(targetProjectId: string, input: ProjectCopyOptionsInput, token: string): Promise<ProjectCopyPreview> {
  return requestJson(`/api/projects/${targetProjectId}/copy-preview`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export function copyProjectContent(targetProjectId: string, input: ProjectCopyOptionsInput, token: string): Promise<ProjectCopyResult> {
  return requestJson(`/api/projects/${targetProjectId}/copy`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export async function saveBom(projectId: string, kind: BomKind, items: BomItem[], token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/${kind}`, {
    method: 'PUT',
    body: JSON.stringify({ items: items.map(item => ({ ...item, isComplete: item.complete })) }),
  }, token)
  return saved.map(mapBomItem)
}

export async function listBom(projectId: string, kind: BomKind, token: string): Promise<BomItem[]> {
  const items = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/${kind}`, {}, token)
  return items.map(mapBomItem)
}

export async function importBom(projectId: string, kind: BomKind, file: File, token: string): Promise<BomItem[]> {
  const form = new FormData()
  form.append('file', file, file.name)
  const imported = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/${kind}/import`, { method: 'POST', body: form }, token)
  return imported.map(mapBomItem)
}

export async function exportBom(projectId: string, kind: BomKind, mode: BomExportMode, token: string): Promise<{ blob: Blob; fileName: string }> {
  const response = await fetch(`${apiBase}/api/projects/${projectId}/boms/${kind}/export?mode=${mode}`, { headers: authenticatedHeaders(token), cache: 'no-store' })
  if (!response.ok) throw new PdmApiError(`BOM导出失败（${response.status}）`, response.status)
  const disposition = response.headers.get('content-disposition') ?? ''
  const encodedName = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
  const plainName = disposition.match(/filename="?([^";]+)"?/i)?.[1]
  let fileName = `${kind.toLocaleLowerCase()}-bom.xlsx`
  try {
    fileName = encodedName ? decodeURIComponent(encodedName) : plainName ?? fileName
  } catch {
    fileName = plainName ?? fileName
  }
  return { blob: await response.blob(), fileName }
}

export async function generateMechanicalBom(projectId: string, token: string): Promise<BomGenerationResult> {
  const result = await requestJson<{ standardItems: ApiBomItem[]; nonStandardItems: ApiBomItem[]; electricalItems: ApiBomItem[]; unclassifiedItems: ApiBomItem[]; virtualItems: ApiBomItem[]; virtualCount: number; unclassifiedCount: number; pendingRemovalCount: number; manualUnmatchedCount: number; applied: boolean }>(`/api/projects/${projectId}/boms/generate?apply=false`, { method: 'POST' }, token)
  return { ...result, standardItems: result.standardItems.map(mapBomItem), nonStandardItems: result.nonStandardItems.map(mapBomItem), electricalItems: result.electricalItems.map(mapBomItem), unclassifiedItems: result.unclassifiedItems.map(mapBomItem), virtualItems: result.virtualItems.map(mapBomItem) }
}

export async function getBomSourceData(projectId: string, token: string): Promise<BomItem[]> {
  const items = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/bom-source-data`, {}, token)
  return items.map(mapBomItem)
}

export function resolveBomItem(projectId: string, itemId: string, action: 'classify' | 'retain' | 'remove', targetKind: BomKind | undefined, token: string): Promise<BomItem[]> {
  return requestJson(`/api/projects/${projectId}/boms/items/${itemId}/resolve`, {
    method: 'POST', body: JSON.stringify({ action, targetKind }),
  }, token)
}

export async function batchUpdateBomItems(projectId: string, input: BatchUpdateBomItemsInput, token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/items/batch`, {
    method: 'PATCH', body: JSON.stringify(input),
  }, token)
  return saved.map(mapBomItem)
}

export async function batchDeleteBomItems(projectId: string, itemIds: string[], reason: string, token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/items/batch-delete`, {
    method: 'POST', body: JSON.stringify({ itemIds, reason }),
  }, token)
  return saved.map(mapBomItem)
}

export async function batchRestoreBomItems(projectId: string, itemIds: string[], mode: 'Original' | 'AsManual', token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/items/batch-restore`, {
    method: 'POST', body: JSON.stringify({ itemIds, mode }),
  }, token)
  return saved.map(mapBomItem)
}

export async function setBomReleaseExclusion(projectId: string, itemIds: string[], excluded: boolean, reason: string, token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/items/release-exclusion`, {
    method: 'POST', body: JSON.stringify({ itemIds, excluded, reason }),
  }, token)
  return saved.map(mapBomItem)
}

export async function restoreBomItemsFromSource(projectId: string, itemIds: string[], token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/items/restore-source`, {
    method: 'POST', body: JSON.stringify({ itemIds }),
  }, token)
  return saved.map(mapBomItem)
}

export function previewBomSourceReclassification(projectId: string, itemIds: string[], targetKind: 'Standard' | 'NonStandard', token: string): Promise<BomSourceReclassificationPreview> {
  return requestJson(`/api/projects/${projectId}/boms/items/reclassify-source/preview`, {
    method: 'POST', body: JSON.stringify({ itemIds, targetKind }),
  }, token)
}

export async function reclassifyBomItemsFromSource(projectId: string, itemIds: string[], targetKind: 'Standard' | 'NonStandard', token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/items/reclassify-source`, {
    method: 'POST', body: JSON.stringify({ itemIds, targetKind }),
  }, token)
  return saved.map(mapBomItem)
}

export function setBomEmptyDeclaration(projectId: string, kind: BomKind, declaredEmpty: boolean, token: string): Promise<BomEmptyDeclaration> {
  return requestJson(`/api/projects/${projectId}/boms/${kind}/empty-declaration`, { method: 'PUT', body: JSON.stringify({ declaredEmpty }) }, token)
}

export async function listDrawingReviews(projectId: string, token: string): Promise<DrawingReviewPackage[]> {
  const packages = await requestJson<ApiDrawingReviewPackage[]>(`/api/projects/${projectId}/drawing-reviews`, {}, token).catch(error => {
    if (error instanceof PdmApiError && error.status === 404) return []
    throw error
  })
  return packages.map(mapDrawingReviewPackage)
}

export async function listDrawingReviewCandidates(projectId: string, token: string): Promise<DrawingReviewCandidate[]> {
  const states = ['Ready', 'InReview', 'ApprovedCurrent', 'Unavailable'] as const
  const bomKinds = ['Unclassified', 'Electrical', 'Standard', 'NonStandard', 'Unclassified'] as const
  const candidates = await requestJson<ApiDrawingReviewCandidate[]>(`/api/projects/${projectId}/drawing-review-candidates`, {}, token)
  return candidates.map(candidate => ({
    ...candidate,
    state: drawingReviewEnum(candidate.state, states),
    bomKinds: candidate.bomKinds.map(kind => drawingReviewEnum(kind, bomKinds)),
  }))
}

export async function createDrawingReview(projectId: string, modelDocumentIds: string[], token: string): Promise<DrawingReviewPackage> {
  return mapDrawingReviewPackage(await requestJson<ApiDrawingReviewPackage>(`/api/projects/${projectId}/drawing-reviews`, {
    method: 'POST', body: JSON.stringify({ modelDocumentIds }),
  }, token))
}

export async function withdrawDrawingReview(packageId: string, reason: string, token: string): Promise<DrawingReviewPackage> {
  return mapDrawingReviewPackage(await requestJson<ApiDrawingReviewPackage>(`/api/drawing-reviews/${packageId}/withdraw`, {
    method: 'POST', body: JSON.stringify({ reason }),
  }, token))
}

export async function addDrawingReviewMarkup(packageId: string, input: AddDrawingReviewMarkupInput, token: string): Promise<DrawingReviewPackage> {
  return mapDrawingReviewPackage(await requestJson<ApiDrawingReviewPackage>(`/api/drawing-reviews/${packageId}/markups`, {
    method: 'POST', body: JSON.stringify(input),
  }, token))
}

export async function resolveDrawingReviewMarkup(packageId: string, markupId: string, token: string): Promise<DrawingReviewPackage> {
  return mapDrawingReviewPackage(await requestJson<ApiDrawingReviewPackage>(`/api/drawing-reviews/${packageId}/markups/${markupId}/resolve`, { method: 'POST' }, token))
}

export async function decideDrawingReviewTarget(packageId: string, itemId: string, target: DrawingReviewTarget, decision: DrawingReviewDecision, comment: string, token: string): Promise<DrawingReviewPackage> {
  return mapDrawingReviewPackage(await requestJson<ApiDrawingReviewPackage>(`/api/drawing-reviews/${packageId}/items/${itemId}/decision`, {
    method: 'POST', body: JSON.stringify({ target, decision, comment }),
  }, token))
}

export async function listReleasePackages(projectId: string, token: string): Promise<ReleasePackageSummary[]> {
  return (await requestJson<ApiReleasePackage[]>(`/api/projects/${projectId}/release-packages`, {}, token)).map(mapReleasePackage)
}

export function createReleasePackage(projectId: string, input: CreateReleasePackageInput, token: string): Promise<ApiReleasePackage> {
  return requestJson('/api/release-packages', { method: 'POST', body: JSON.stringify({ projectId, referenceSnapshotId: null, ...input }) }, token)
}

export function updateReleasePackageDraft(releasePackageId: string, input: UpdateReleasePackageDraftInput, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/release-packages/${releasePackageId}/draft`, { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function deleteReleasePackageDraft(releasePackageId: string, token: string): Promise<void> {
  return requestJson(`/api/release-packages/${releasePackageId}/draft`, { method: 'DELETE' }, token)
}

export async function listBomVersions(projectId: string, token: string): Promise<BomVersion[]> {
  const versions = await requestJson<ApiBomVersion[]>(`/api/projects/${projectId}/bom-versions`, {}, token).catch(error => {
    if (error instanceof PdmApiError && error.status === 404) return []
    throw error
  })
  return versions.map(mapBomVersion)
}

export async function listBomBaselines(projectId: string, token: string): Promise<ManufacturingBomBaseline[]> {
  return requestJson<ManufacturingBomBaseline[]>(`/api/projects/${projectId}/bom-baselines`, {}, token).catch(error => {
    if (error instanceof PdmApiError && error.status === 404) return []
    throw error
  })
}

export function submitReleasePackage(releasePackageId: string, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/release-packages/${releasePackageId}/submit`, { method: 'POST' }, token)
}

export function withdrawReleasePackage(releasePackageId: string, comment: string, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/release-packages/${releasePackageId}/withdraw`, { method: 'POST', body: JSON.stringify({ comment }) }, token)
}

export function retryLongLeadU9(releasePackageId: string, token: string): Promise<{ releasePackageId: string; releasePackageNumber: string; headerApplications: { generatedCount: number; existingCount: number }; automation: ApprovalU9AutomationResult }> {
  return requestJson(`/api/release-packages/${releasePackageId}/u9-retry`, { method: 'POST' }, token)
}

export function listReleaseItemComments(releasePackageId: string, token: string): Promise<ReleaseItemComment[]> {
  return requestJson(`/api/release-packages/${releasePackageId}/item-comments`, {}, token)
}

export function addReleaseItemComment(releasePackageId: string, bomItemId: string, comment: string, token: string): Promise<ReleaseItemComment> {
  return requestJson(`/api/release-packages/${releasePackageId}/item-comments`, {
    method: 'POST', body: JSON.stringify({ bomItemId, comment }),
  }, token)
}

export function listDocumentWhereUsed(documentId: string, token: string): Promise<DocumentWhereUsed[]> {
  return requestJson(`/api/documents/${documentId}/where-used`, {}, token)
}

export function obsoleteDocument(documentId: string, comment: string, token: string): Promise<ManagedDocument> {
  return requestJson(`/api/documents/${documentId}/obsolete`, { method: 'POST', body: JSON.stringify({ comment }) }, token)
}

export function decideApproval(taskId: string, decision: 'Approved' | 'Rejected', comment: string, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/approval-tasks/${taskId}/decision`, { method: 'POST', body: JSON.stringify({ decision: decision === 'Approved' ? 0 : 1, comment }) }, token)
}

export function listApprovalTransferCandidates(taskId: string, token: string): Promise<ApprovalTransferCandidate[]> {
  return requestJson(`/api/approval-tasks/${taskId}/transfer-candidates`, {}, token)
}

export function transferApproval(taskId: string, targetUsername: string, comment: string, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/approval-tasks/${taskId}/transfer`, { method: 'POST', body: JSON.stringify({ targetUsername, comment }) }, token)
}

export function emergencyDecideApproval(taskId: string, decision: 'Approved' | 'Rejected', reason: string, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/approval-tasks/${taskId}/emergency-decision`, { method: 'POST', body: JSON.stringify({ decision, reason }) }, token)
}

export async function uploadReleaseFile(projectId: string, packageNumber: string, file: File, token: string, onProgress?: (percent: number) => void): Promise<void> {
  const extension = file.name.split('.').pop()?.toLocaleLowerCase()
  if (extension !== 'pdf') throw new PdmApiError('生产发包只允许上传PDF。', 400)
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  const sha256 = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('').toUpperCase()
  const session = await requestJson<{ id: string; chunkSize: number }>(`/api/uploads/sessions`, { method: 'POST', body: JSON.stringify({ projectId, fileName: file.name, totalLength: file.size, sha256 }) }, token)
  const chunks = Math.ceil(file.size / session.chunkSize)
  for (let index = 0; index < chunks; index++) {
    const body = file.slice(index * session.chunkSize, Math.min(file.size, (index + 1) * session.chunkSize))
    const response = await fetch(`${apiBase}/api/uploads/sessions/${session.id}/chunks/${index}`, { method: 'PUT', headers: authenticatedHeaders(token), body })
    if (!response.ok) throw new PdmApiError(`发布文件分块${index + 1}上传失败（${response.status}）`, response.status)
    onProgress?.(Math.round(((index + 1) / chunks) * 100))
  }
  const safeName = file.name.replace(/[\\/:*?"<>|]/g, '_')
  await requestJson(`/api/uploads/sessions/${session.id}/complete`, { method: 'POST', body: JSON.stringify({ relativeTargetPath: `.release-staging/${packageNumber}/drawings/${safeName}` }) }, token)
}

export function listAudit(token: string): Promise<AuditEntry[]> {
  return requestJson('/api/audit?take=200', {}, token)
}

export function listMyApprovalTasks(token: string): Promise<MyApprovalTask[]> {
  return Promise.all([
    requestJson<MyApprovalTask[]>('/api/approval-tasks/mine', {}, token),
    listMyValidationPlanApprovalTasks(token),
  ]).then(([releaseTasks, validationTasks]) => [
    ...releaseTasks.map(task => ({ ...task, kind: 'release' as const })),
    ...validationTasks.map(task => ({
      id: task.id,
      kind: 'validationPlan' as const,
      projectId: task.projectId,
      projectCode: task.projectCode,
      projectName: task.projectName,
      validationPlanId: task.planId,
      validationPlanRevision: task.revisionNumber,
      stage: task.stage,
      stepName: task.stepName,
      packageState: 'PendingApproval',
      createdAt: task.createdAt,
    })),
  ])
}

export function listUserNotifications(token: string): Promise<UserNotification[]> {
  return requestJson('/api/notifications/mine?take=200', {}, token)
}

export function markUserNotificationRead(notificationId: string, token: string): Promise<void> {
  return requestJson(`/api/notifications/${notificationId}/read`, { method: 'POST' }, token)
}

export function markAllUserNotificationsRead(token: string): Promise<void> {
  return requestJson('/api/notifications/read-all', { method: 'POST' }, token)
}

export function listProjectPlanTemplates(token: string, includeInactive = false): Promise<import('./types').ProjectPlanTemplate[]> {
  return requestJson(`/api/project-plan-templates${includeInactive ? '?includeInactive=true' : ''}`, {}, token)
}

export function saveProjectPlanTemplate(templateId: string | null, input: import('./types').SaveProjectPlanTemplateInput, token: string): Promise<import('./types').ProjectPlanTemplate> {
  return requestJson(templateId ? `/api/project-plan-templates/${templateId}` : '/api/project-plan-templates', {
    method: templateId ? 'PUT' : 'POST',
    body: JSON.stringify(input),
  }, token)
}

export function readProjectPlan(projectId: string, token: string): Promise<import('./types').ProjectPlan | null> {
  return requestJson(`/api/projects/${projectId}/plan`, {}, token)
}

export function readProjectPlanPortfolio(projectId: string, token: string): Promise<import('./types').ProjectPlanPortfolio> {
  return requestJson(`/api/projects/${projectId}/plan/portfolio`, {}, token)
}

export function listProjectPlanVersions(projectId: string, token: string): Promise<import('./types').ProjectPlanVersion[]> {
  return requestJson(`/api/projects/${projectId}/plan/versions`, {}, token)
}

export function generateProjectPlan(projectId: string, input: { templateId: string; startDate: string; totalDurationDays: number; replaceExisting: boolean; changeReason?: string; independentStages?: Array<{ stage: string; startDate: string; durationDays: number }>; deferredStages?: string[] }, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/generate`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export function saveProjectPlan(projectId: string, input: { tasks: import('./types').ProjectPlanTask[]; changeReason: string; expectedRowVersion: number; stages?: import('./types').ProjectPlanStageDefinition[]; createMissingFollowers?: boolean }, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan`, { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function deleteProjectPlan(projectId: string, expectedRowVersion: number, includeIndependentChildren: boolean, token: string): Promise<void> {
  return requestJson<void>(`/api/projects/${projectId}/plan?expectedRowVersion=${expectedRowVersion}&includeIndependentChildren=${includeIndependentChildren}`, { method: 'DELETE' }, token)
}

export function supplementProjectPlanStageSchedule(projectId: string, input: { startDate: string; totalDurationDays: number; expectedRowVersion: number }, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/stage-schedule`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export function setProjectPlanBaseline(projectId: string, expectedRowVersion: number, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/baseline`, { method: 'POST', body: JSON.stringify({ expectedRowVersion }) }, token)
}

export function submitProjectPlan(projectId: string, expectedRowVersion: number, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/submit`, { method: 'POST', body: JSON.stringify({ expectedRowVersion }) }, token)
}

export function submitProjectPlanChange(projectId: string, input: { tasks: NonNullable<import('./types').ProjectPlan['changeRequest']>['tasks']; reason: string; expectedRowVersion: number }, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/change-request`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export function completeProjectPlanChange(projectId: string, expectedRowVersion: number, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/change-complete`, { method: 'POST', body: JSON.stringify({ expectedRowVersion }) }, token)
}

export function abandonProjectPlanChange(projectId: string, expectedRowVersion: number, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/change-abandon`, { method: 'POST', body: JSON.stringify({ expectedRowVersion }) }, token)
}

export function decideProjectPlan(projectId: string, input: { expectedRowVersion: number; approve: boolean; comment?: string }, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/decision`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export function updateProjectPlanTaskProgress(projectId: string, taskId: string, input: { completionPercent: number; actualStart?: string; actualFinish?: string; expectedRowVersion: number }, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/tasks/${taskId}/progress`, { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function setProjectPlanStage(projectId: string, input: { stage?: import('./types').ProjectPlanStage; reason: string; expectedRowVersion: number }, token: string): Promise<import('./types').ProjectPlan> {
  return requestJson(`/api/projects/${projectId}/plan/stage`, { method: 'PUT', body: JSON.stringify(input) }, token)
}

export function reuseProjectPlan(rootProjectId: string, input: { sourceProjectId: string; targetProjectIds: string[]; replaceExisting: boolean; changeReason: string }, token: string): Promise<import('./types').ProjectPlan[]> {
  return requestJson(`/api/projects/${rootProjectId}/plan/reuse`, { method: 'POST', body: JSON.stringify(input) }, token)
}

export function listEditLocks(token: string): Promise<EditLockSummary[]> {
  return requestJson('/api/edit-locks', {}, token)
}

export function requestEditLockRelease(documentId: string, reason: string, token: string): Promise<EditLockSummary> {
  return requestJson(`/api/documents/${documentId}/request-release`, { method: 'POST', body: JSON.stringify({ reason }) }, token)
}

export function forceReleaseEditLock(documentId: string, reason: string, token: string): Promise<void> {
  return requestJson(`/api/documents/${documentId}/force-release`, { method: 'POST', body: JSON.stringify({ reason }) }, token)
}

export function listProjectVersions(projectId: string, token: string): Promise<ProjectVersionItem[]> {
  return requestJson(`/api/projects/${projectId}/versions`, {}, token)
}

export function listProjectAudit(projectId: string, token: string): Promise<AuditEntry[]> {
  return requestJson(`/api/projects/${projectId}/audit?take=200`, {}, token)
}

export function getStorageStatus(projectId: string, token: string): Promise<{ vaultAvailable: boolean; releaseAvailable: boolean }> {
  return requestJson(`/api/projects/${projectId}/storage-status`, {}, token)
}

export async function listProjectFolders(projectId: string, token: string): Promise<ProjectFolder[]> {
  return (await requestJson<Array<Omit<ProjectFolder, 'purpose' | 'permissions'> & { purpose: string | number; permissions?: Array<Omit<FolderPermissionRule, 'principalType'> & { principalType: string | number }> }>>(`/api/projects/${projectId}/folders`, {}, token)).map(mapProjectFolder)
}

export async function updateProjectFolderPermissions(projectId: string, folderId: string, permissions: FolderPermissionRule[], token: string): Promise<ProjectFolder[]> {
  const result = await requestJson<Array<Omit<ProjectFolder, 'purpose' | 'permissions'> & { purpose: string | number; permissions?: Array<Omit<FolderPermissionRule, 'principalType'> & { principalType: string | number }> }>>(`/api/projects/${projectId}/folders/${folderId}/permissions`, {
    method: 'PUT',
    body: JSON.stringify({ permissions: permissions.map(({ principalType, principalKey, access }) => ({ principalType: principalType === 'Role' ? 0 : 1, principalKey, access })) }),
  }, token)
  return result.map(mapProjectFolder)
}

export function listProjectFiles(projectId: string, folderId: string, includeDeleted: boolean, token: string): Promise<ProjectFile[]> {
  return requestJson(`/api/projects/${projectId}/files?folderId=${encodeURIComponent(folderId)}&includeDeleted=${includeDeleted}`, {}, token)
}

export async function uploadProjectFile(projectId: string, folderId: string, file: File, token: string, comment = '', onProgress?: (percent: number) => void, signal?: AbortSignal): Promise<ProjectFile> {
  onProgress?.(0)
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  const sha256 = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('').toUpperCase()
  const session = await requestJson<{ id: string; chunkSize: number }>(`/api/projects/${projectId}/folders/${folderId}/file-uploads`, {
    method: 'POST', body: JSON.stringify({ fileName: file.name, totalLength: file.size, sha256 }), signal,
  }, token)
  try {
    const chunks = Math.ceil(file.size / session.chunkSize)
    for (let index = 0; index < chunks; index++) {
      const response = await fetch(`${apiBase}/api/project-file-uploads/${session.id}/chunks/${index}`, {
        method: 'PUT', headers: authenticatedHeaders(token), body: file.slice(index * session.chunkSize, Math.min(file.size, (index + 1) * session.chunkSize)), signal,
      })
      if (!response.ok) throw new PdmApiError(`文件分块${index + 1}上传失败（${response.status}）`, response.status)
      onProgress?.(Math.round(((index + 1) / chunks) * 95))
    }
    const saved = await requestJson<ProjectFile>(`/api/project-file-uploads/${session.id}/complete`, { method: 'POST', body: JSON.stringify({ comment }), signal }, token)
    onProgress?.(100)
    return saved
  } catch (error) {
    await requestJson<void>(`/api/project-file-uploads/${session.id}`, { method: 'DELETE' }, token).catch(() => undefined)
    throw error
  }
}

export function createProjectFolder(projectId: string, parentFolderId: string, name: string, token: string): Promise<ProjectFolder> {
  return requestJson(`/api/projects/${projectId}/folders/${parentFolderId}/children`, { method: 'POST', body: JSON.stringify({ name }) }, token)
}
export function renameProjectFolder(projectId: string, folderId: string, name: string, token: string): Promise<ProjectFolder> {
  return requestJson(`/api/projects/${projectId}/folders/${folderId}`, { method: 'PATCH', body: JSON.stringify({ name }) }, token)
}
export function moveProjectFolder(projectId: string, folderId: string, targetFolderId: string, token: string): Promise<ProjectFolder> {
  return requestJson(`/api/projects/${projectId}/folders/${folderId}/move`, { method: 'POST', body: JSON.stringify({ folderId: targetFolderId }) }, token)
}
export function deleteProjectFolder(projectId: string, folderId: string, token: string): Promise<void> {
  return requestJson(`/api/projects/${projectId}/folders/${folderId}`, { method: 'DELETE' }, token)
}
export function renameProjectFile(projectId: string, fileId: string, name: string, token: string): Promise<ProjectFile> {
  return requestJson(`/api/projects/${projectId}/files/${fileId}`, { method: 'PATCH', body: JSON.stringify({ name }) }, token)
}
export function moveProjectFile(projectId: string, fileId: string, folderId: string, token: string): Promise<ProjectFile> {
  return requestJson(`/api/projects/${projectId}/files/${fileId}/move`, { method: 'POST', body: JSON.stringify({ folderId }) }, token)
}
export function deleteProjectFile(projectId: string, fileId: string, token: string): Promise<ProjectFile> {
  return requestJson(`/api/projects/${projectId}/files/${fileId}`, { method: 'DELETE' }, token)
}
export function restoreProjectFile(projectId: string, fileId: string, token: string): Promise<ProjectFile> {
  return requestJson(`/api/projects/${projectId}/files/${fileId}/restore`, { method: 'POST' }, token)
}
export function listProjectFileVersions(projectId: string, fileId: string, token: string): Promise<ProjectFileVersion[]> {
  return requestJson(`/api/projects/${projectId}/files/${fileId}/versions`, {}, token)
}
export async function downloadProjectFile(projectId: string, file: ProjectFile, token: string, versionId?: string, preview = false): Promise<void> {
  const query = new URLSearchParams({ download: preview ? 'false' : 'true' })
  if (versionId) query.set('versionId', versionId)
  const response = await fetch(`${apiBase}/api/projects/${projectId}/files/${file.id}/content?${query}`, { headers: authenticatedHeaders(token) })
  if (!response.ok) throw new PdmApiError(`项目文件读取失败（${response.status}）`, response.status)
  const url = URL.createObjectURL(await response.blob())
  if (preview) window.open(url, '_blank', 'noopener,noreferrer')
  else { const link = document.createElement('a'); link.href = url; link.download = file.fileName; link.click() }
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

export async function listFolderTemplate(token: string): Promise<ProjectFolderTemplateNode[]> {
  const nodes = await requestJson<Array<Omit<ProjectFolderTemplateNode, 'purpose' | 'permissions'> & { purpose: string | number; permissions?: Array<Omit<FolderPermissionRule, 'principalType'> & { principalType: string | number }> }>>('/api/folder-template', {}, token)
  return nodes.map(mapFolderTemplateNode)
}

export async function saveFolderTemplate(nodes: ProjectFolderTemplateNode[], token: string): Promise<ProjectFolderTemplateNode[]> {
  const saved = await requestJson<Array<Omit<ProjectFolderTemplateNode, 'purpose' | 'permissions'> & { purpose: string | number; permissions?: Array<Omit<FolderPermissionRule, 'principalType'> & { principalType: string | number }> }>>('/api/folder-template', {
    method: 'PUT',
    body: JSON.stringify({ nodes: nodes.map(({ folderKey, name, sortOrder, inheritPermissions, permissions }) => ({ folderKey, name, sortOrder, inheritPermissions, permissions: permissions.map(rule => ({ ...rule, principalType: rule.principalType === 'Role' ? 0 : 1 })) })) }),
  }, token)
  return saved.map(mapFolderTemplateNode)
}

function mapFolderPurpose(value: string | number): ProjectFolder['purpose'] {
  return typeof value === 'string' ? value as ProjectFolder['purpose'] : ['Root', 'MechanicalRoot', 'ElectricalRoot', 'ProjectContainer', 'Release', 'Standard'][value] as ProjectFolder['purpose']
}

function mapFolderPermission(rule: Omit<FolderPermissionRule, 'principalType'> & { principalType: string | number }): FolderPermissionRule {
  return { ...rule, principalType: rule.principalType === 0 || rule.principalType === 'Role' ? 'Role' : 'User' }
}

function mapProjectFolder(folder: Omit<ProjectFolder, 'purpose' | 'permissions'> & { purpose: string | number; permissions?: Array<Omit<FolderPermissionRule, 'principalType'> & { principalType: string | number }> }): ProjectFolder {
  return { ...folder, purpose: mapFolderPurpose(folder.purpose), permissions: (folder.permissions ?? []).map(mapFolderPermission) }
}

function mapFolderTemplateNode(node: Omit<ProjectFolderTemplateNode, 'purpose' | 'permissions'> & { purpose: string | number; permissions?: Array<Omit<FolderPermissionRule, 'principalType'> & { principalType: string | number }> }): ProjectFolderTemplateNode {
  return { ...node, purpose: mapFolderPurpose(node.purpose), permissions: (node.permissions ?? []).map(mapFolderPermission) }
}

export async function loadProjectWorkspace(projectId: string, token: string): Promise<ProjectWorkspaceData> {
  const project = await withApiContext('项目详情', requestJson<ApiProject>(`/api/projects/${projectId}`, {}, token))
  const mappedProject = mapProject(project)
  if (!mappedProject.canReadContent) {
    return { project: mappedProject, root: emptyProjectRoot(project), hasDocuments: false, documents: [], documentRelations: [], folders: [], standardBom: [], nonStandardBom: [], unclassifiedBom: [], electricalBom: [], bomSourceData: [], bomEmptyDeclarations: [], bomVersions: [], bomBaselines: [], drawingReviews: [], materialCodeApplications: [], releasePackages: [], releasePackage: null }
  }

  const [documentWorkspace, folders, standard, nonStandard, unclassified, electrical, sourceData, bomEmptyDeclarations, bomVersions, bomBaselines, drawingReviews, materialCodeApplications, releasePackages] = await Promise.all([
    withApiContext('图档工作区', loadProjectDocumentWorkspace(project.id, token)),
    withApiContext('项目文件夹', listProjectFolders(project.id, token)),
    withApiContext('标准件BOM', requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/Standard`, {}, token)),
    withApiContext('非标件BOM', requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/NonStandard`, {}, token)),
    withApiContext('待分类BOM', requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/Unclassified`, {}, token)),
    withApiContext('电气BOM', requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/Electrical`, {}, token)),
    withApiContext('BOM源数据', requestJson<ApiBomItem[]>(`/api/projects/${project.id}/bom-source-data`, {}, token)),
    withApiContext('空BOM声明', requestJson<BomEmptyDeclaration[]>(`/api/projects/${project.id}/boms/empty-declarations`, {}, token)),
    withApiContext('BOM版本', listBomVersions(project.id, token)),
    withApiContext('制造基线', listBomBaselines(project.id, token)),
    withApiContext('图纸审核', listDrawingReviews(project.id, token)),
    withApiContext('料号申请', listMaterialCodeApplications(token, project.id)),
    withApiContext('发布记录', requestJson<ApiReleasePackage[]>(`/api/projects/${project.id}/release-packages`, {}, token)),
  ])

  return {
    project: mappedProject,
    ...documentWorkspace,
    folders,
    standardBom: standard.map(mapBomItem),
    nonStandardBom: nonStandard.map(mapBomItem),
    unclassifiedBom: unclassified.map(mapBomItem),
    electricalBom: electrical.map(mapBomItem),
    bomSourceData: sourceData.map(mapBomItem),
    bomEmptyDeclarations,
    bomVersions,
    bomBaselines,
    drawingReviews,
    materialCodeApplications,
    releasePackages: releasePackages.map(mapReleasePackage),
    releasePackage: releasePackages.length > 0 ? mapReleasePackage(releasePackages[0]) : null,
  }
}

export async function loadProjectDocumentWorkspace(projectId: string, token: string): Promise<ProjectDocumentWorkspaceData> {
  const [documents, libraryDocuments, documentRelations, referenceRoot] = await Promise.all([
    withApiContext('项目图档', requestJson<ApiDocument[]>(`/api/projects/${projectId}/documents`, {}, token)),
    withApiContext('文件夹图档', requestJson<ApiDocument[]>(`/api/projects/${projectId}/folder-documents`, {}, token)),
    withApiContext('图档关系', requestJson<DocumentModelDrawingRelation[]>(`/api/projects/${projectId}/document-relations`, {}, token)),
    withApiContext('引用结构', requestJson<ApiReferenceNode>(`/api/projects/${projectId}/reference-tree`, {}, token).catch(error => {
      if (error instanceof PdmApiError && error.status === 404) return null
      throw error
    })),
  ])
  const documentsById = new Map([...documents, ...libraryDocuments].map((document) => [document.id, document]))
  const documentsByFileName = uniqueDocumentsByFileName(documentsById.values())
  const snapshotVersionsByFileName = referenceRoot ? uniqueSnapshotVersionsByFileName(referenceRoot) : new Map<string, string>()
  const referenceTree = referenceRoot
    ? reconcileCurrentReferenceTree(mapReferenceNode(referenceRoot, documentsById, documentsByFileName, snapshotVersionsByFileName, true), documentsById, documentRelations)
    : emptyProjectRoot({ id: projectId })
  return {
    root: referenceTree,
    hasDocuments: documents.length > 0,
    documents: libraryDocuments.map(mapManagedDocument),
    documentRelations,
  }
}

function mapManagedDocument(document: ApiDocument): ManagedDocument {
  return {
    id: document.id,
    projectId: document.projectId,
    folderId: document.folderId ?? undefined,
    drawingNumber: document.drawingNumber,
    name: document.name,
    fileName: document.fileName,
    kind: mapDocumentKind(document.kind),
    state: document.state ?? document.lifecycleState ?? 'Work',
    revision: document.storedVersionCount === 0 ? '—' : revisionDisplay(document.revision),
    storedVersionCount: document.storedVersionCount ?? undefined,
    checkedOutBy: document.checkedOutBy ?? undefined,
    checkedOutAt: document.checkedOutAt ?? undefined,
    checkoutMachine: document.checkoutMachine ?? undefined,
    checkoutLastHeartbeatAt: document.checkoutLastHeartbeatAt ?? undefined,
    checkoutLeaseExpiresAt: document.checkoutLeaseExpiresAt ?? undefined,
    checkoutReleaseRequestedBy: document.checkoutReleaseRequestedBy ?? undefined,
    checkoutReleaseRequestedAt: document.checkoutReleaseRequestedAt ?? undefined,
    updatedAt: document.updatedAt,
  }
}

function emptyProjectRoot(project: Pick<ApiProject, 'id'>): DocumentNode {
  return {
    id: `project-${project.id}`,
    drawingNumber: '—',
    name: '尚未关联SolidWorks图档',
    fileName: '',
    kind: 'Assembly',
    configuration: '—',
    quantity: 0,
    version: '—',
    status: 'Normal',
    children: [],
  }
}

function mapProject(project: ApiProject): ProjectSummary {
  const locationParts = project.vaultLocation.split(/[\\/]/).filter(Boolean)
  return {
    id: project.id,
    code: project.code,
    name: project.name,
    owner: project.owner,
    stage: project.isActive ? '进行中' : '已停用',
    vaultName: locationParts.at(-1) ?? project.vaultLocation,
    vaultLocation: project.vaultLocation,
    releaseLocation: project.releaseLocation,
    projectAlias: project.projectAlias ?? undefined,
    organizationId: project.organizationId ?? undefined,
    organizationName: project.organizationName ?? undefined,
    projectTypeCode: project.projectTypeCode ?? undefined,
    equipmentTypeCode: project.equipmentTypeCode ?? undefined,
    customerCode: project.customerCode ?? undefined,
    customerName: project.customerName ?? undefined,
    customerProjectSequence: project.customerProjectSequence ?? undefined,
    deviceModel: project.deviceModel ?? undefined,
    signedDate: project.signedDate ?? undefined,
    quantity: project.quantity ?? 1,
    parentProjectId: project.parentProjectId ?? undefined,
    rootProjectId: project.rootProjectId ?? undefined,
    childSequence: project.childSequence ?? undefined,
    bomItemCategoryCode: project.bomItemCategoryCode ?? undefined,
    serialNumbers: project.serialNumbers ?? [],
    responsibleUsers: project.responsibleUsers ?? (project.owner ? [project.owner] : []),
    executionUnitId: project.executionUnitId ?? undefined,
    executionUnitName: project.executionUnitName ?? undefined,
    primaryProjectManager: project.primaryProjectManager ?? undefined,
    collaborativeProjectManagers: project.collaborativeProjectManagers ?? [],
    designLead: project.designLead ?? undefined,
    designLeads: project.designLeads?.length ? project.designLeads : project.designLead ? [project.designLead] : [],
    designers: project.designers ?? [],
    documentCount: project.documentCount ?? undefined,
    modelDocumentCount: project.modelDocumentCount ?? undefined,
    drawingDocumentCount: project.drawingDocumentCount ?? undefined,
    businessStatus: project.businessStatus ?? undefined,
    rootDocumentCheckedOutBy: project.rootDocumentCheckedOutBy ?? undefined,
    canAssignExecutionUnit: project.canAssignExecutionUnit ?? false,
    canManageMainStaffing: project.canManageMainStaffing ?? false,
    canAssignDesigners: project.canAssignDesigners ?? false,
    canReadContent: project.canReadContent ?? true,
  }
}

function mapReferenceNode(
  node: ApiReferenceNode,
  documentsById: Map<string, ApiDocument>,
  documentsByFileName: Map<string, ApiDocument>,
  snapshotVersionsByFileName: Map<string, string>,
  isRoot = false,
): DocumentNode {
  const document = node.documentId
    ? documentsById.get(node.documentId)
    : documentsByFileName.get(normalizedFileName(node.fileName))
  const documentId = node.documentId ?? document?.id
  const hasStoredVersion = document?.storedVersionCount !== 0
  const latestVersion = document && hasStoredVersion ? revisionDisplay(document.revision) : '—'
  const capturedVersion = revisionDisplay(node.revision)
  const structureVersion = isRoot && document
    ? latestVersion
    : capturedVersion === '—'
      ? snapshotVersionsByFileName.get(normalizedFileName(node.fileName)) ?? '—'
      : capturedVersion
  const seenInstancePaths = new Set<string>()
  const children = (node.children ?? [])
    .filter((child) => {
      const instancePath = child.instancePath.trim().toLocaleLowerCase('zh-CN')
      if (!instancePath) return true
      if (seenInstancePaths.has(instancePath)) return false
      seenInstancePaths.add(instancePath)
      return true
    })
    .map((child) => mapReferenceNode(child, documentsById, documentsByFileName, snapshotVersionsByFileName, false))
  return {
    id: node.nodeId || node.instancePath,
    instancePath: node.instancePath,
    documentId: documentId ?? undefined,
    drawingNumber: document?.drawingNumber ?? node.fileName.replace(/\.[^.]+$/, ''),
    name: document?.name || meaningfulReferenceName(node.displayName, node.fileName),
    fileName: node.fileName,
    kind: mapDocumentKind(node.kind),
    configuration: node.configuration || '默认',
    quantity: node.quantity,
    version: document ? latestVersion : structureVersion,
    snapshotVersion: structureVersion,
    versionAlignment: document && hasStoredVersion ? classifyVersionAlignment(latestVersion, structureVersion) : undefined,
    // A reference snapshot records the checkout owner at capture time. It is
    // historical metadata and must not be presented as the current edit state.
    checkedOutBy: document?.checkedOutBy ?? undefined,
    lifecycleState: document?.state ?? document?.lifecycleState ?? 'Work',
    status: documentId ? hasStoredVersion ? mapReferenceStatus(node.status) : 'Unarchived' : 'Unregistered',
    children,
  }
}

function meaningfulReferenceName(displayName: string, fileName: string) {
  const fallback = fileName.replace(/\.[^.]+$/, '')
  const name = displayName?.trim()
  return !name || name.includes('/') || name.includes('\\') ? fallback : name
}

function uniqueDocumentsByFileName(documents: Iterable<ApiDocument>): Map<string, ApiDocument> {
  const unique = new Map<string, ApiDocument>()
  const ambiguous = new Set<string>()
  for (const document of documents) {
    const key = normalizedFileName(document.fileName)
    if (!key || ambiguous.has(key)) continue
    const existing = unique.get(key)
    if (existing && existing.id !== document.id) {
      unique.delete(key)
      ambiguous.add(key)
    } else {
      unique.set(key, document)
    }
  }
  return unique
}

function uniqueSnapshotVersionsByFileName(root: ApiReferenceNode): Map<string, string> {
  const versions = new Map<string, Set<string>>()
  const visit = (node: ApiReferenceNode) => {
    const key = normalizedFileName(node.fileName)
    const version = revisionDisplay(node.revision)
    if (key && version !== '—') {
      const candidates = versions.get(key) ?? new Set<string>()
      candidates.add(version)
      versions.set(key, candidates)
    }
    for (const child of node.children ?? []) visit(child)
  }
  visit(root)
  return new Map([...versions]
    .filter(([, candidates]) => candidates.size === 1)
    .map(([key, candidates]) => [key, [...candidates][0]]))
}

function normalizedFileName(value: string): string {
  return (value ?? '').trim().replace(/\\/g, '/').split('/').at(-1)?.toLocaleLowerCase('zh-CN') ?? ''
}

function reconcileCurrentReferenceTree(
  root: DocumentNode,
  documentsById: Map<string, ApiDocument>,
  relations: DocumentModelDrawingRelation[],
): DocumentNode {
  const drawingsByModelId = new Map<string, ApiDocument[]>()
  for (const relation of relations) {
    const drawing = documentsById.get(relation.drawingDocumentId)
    if (!drawing || mapDocumentKind(drawing.kind) !== 'Drawing') continue
    const drawings = drawingsByModelId.get(relation.modelDocumentId) ?? []
    if (!drawings.some(candidate => candidate.id === drawing.id)) drawings.push(drawing)
    drawingsByModelId.set(relation.modelDocumentId, drawings)
  }

  const visit = (node: DocumentNode): DocumentNode => {
    let children = node.children.map(visit)
    if (node.documentId && node.kind !== 'Drawing') {
      const relatedDrawings = drawingsByModelId.get(node.documentId) ?? []
      const relatedDrawingsById = new Map(relatedDrawings.map(drawing => [drawing.id, drawing]))
      children = children.map((child) => {
        const drawing = child.kind === 'Drawing' && child.documentId
          ? relatedDrawingsById.get(child.documentId)
          : undefined
        if (!drawing) return child

        const drawingVersion = revisionDisplay(drawing.revision)
        return {
          ...child,
          drawingNumber: drawing.drawingNumber,
          name: drawing.name,
          fileName: drawing.fileName,
          version: drawingVersion,
          snapshotVersion: drawingVersion,
          versionAlignment: 'Synced',
          checkedOutBy: drawing.checkedOutBy ?? undefined,
          lifecycleState: drawing.state ?? drawing.lifecycleState ?? 'Work',
          status: 'Normal',
        }
      })
      const existingDrawingIds = new Set(children
        .filter(child => child.kind === 'Drawing' && child.documentId)
        .map(child => child.documentId))
      for (const drawing of relatedDrawings) {
        if (existingDrawingIds.has(drawing.id)) continue
        const drawingVersion = revisionDisplay(drawing.revision)
        children.push({
          id: `drawing:${node.id}:${drawing.id}`,
          documentId: drawing.id,
          drawingNumber: drawing.drawingNumber,
          name: drawing.name,
          fileName: drawing.fileName,
          kind: 'Drawing',
          configuration: '工程图',
          quantity: 1,
          version: drawingVersion,
          snapshotVersion: drawingVersion,
          versionAlignment: 'Synced',
          checkedOutBy: drawing.checkedOutBy ?? undefined,
          lifecycleState: drawing.state ?? drawing.lifecycleState ?? 'Work',
          status: 'Normal',
          children: [],
        })
      }
    }
    return { ...node, children }
  }

  return visit(root)
}

function mapBomItem(item: ApiBomItem): BomItem {
  return {
    id: item.id,
    kind: typeof item.kind === 'number'
      ? ([undefined, 'Electrical', 'Standard', 'NonStandard', 'Unclassified', 'Virtual'] as const)[item.kind]
      : item.kind as BomClassification | undefined,
    sequence: item.sequence,
    drawingNumber: item.drawingNumber,
    name: item.name,
    quantity: item.quantity,
    unit: item.unit,
    material: item.material ?? undefined,
    specification: item.specification ?? undefined,
    remark: item.remark ?? undefined,
    brand: item.brand ?? undefined,
    surfaceTreatment: item.surfaceTreatment ?? undefined,
    heatTreatment: item.heatTreatment ?? undefined,
    weight: item.weight ?? undefined,
    revision: item.revision,
    complete: item.isComplete,
    sourceDocumentId: item.sourceDocumentId ?? undefined,
    sourceConfiguration: item.sourceConfiguration ?? undefined,
    source: ['Auto', 'MaterialRelation', 'EngineeringKit'].includes(item.source ?? '') ? item.source as BomItem['source'] : 'Manual',
    engineeringKitReferenceId: item.engineeringKitReferenceId ?? undefined,
    engineeringKitId: item.engineeringKitId ?? undefined,
    engineeringKitRevisionId: item.engineeringKitRevisionId ?? undefined,
    engineeringKitCode: item.engineeringKitCode ?? undefined,
    engineeringKitVersionNumber: item.engineeringKitVersionNumber ?? undefined,
    engineeringKitComponentId: item.engineeringKitComponentId ?? undefined,
    engineeringKitComponentOptional: item.engineeringKitComponentOptional ?? false,
    manuallyOverridden: item.isManuallyOverridden ?? false,
    pendingRemoval: item.isPendingRemoval ?? false,
    pendingClassification: item.isPendingClassification ?? false,
    manualUnmatched: item.isManualUnmatched ?? false,
    manuallyRetained: item.isManuallyRetained ?? false,
    manuallyExcluded: item.isManuallyExcluded ?? false,
    releaseExcluded: item.isReleaseExcluded ?? false,
    releaseExclusionReason: item.releaseExclusionReason ?? undefined,
    reconciliationStatus: item.reconciliationStatus ?? undefined,
    reconciliationNote: item.reconciliationNote ?? undefined,
    reconciliationUpdatedBy: item.reconciliationUpdatedBy ?? undefined,
    reconciliationUpdatedAt: item.reconciliationUpdatedAt ?? undefined,
    deletedAt: item.deletedAt ?? undefined,
    deletedBy: item.deletedBy ?? undefined,
    deleteReason: item.deleteReason ?? undefined,
    propertyWritebackStatus: typeof item.propertyWritebackStatus === 'string' ? item.propertyWritebackStatus as BomItem['propertyWritebackStatus'] : undefined,
  }
}

function drawingReviewEnum<T extends string>(value: string | number, values: readonly T[]): T {
  return typeof value === 'number' ? values[value] : value as T
}

function mapDrawingReviewPackage(review: ApiDrawingReviewPackage): DrawingReviewPackage {
  const packageStates = ['InReview', 'ChangesRequested', 'WritingProperties', 'Approved', 'Stale', 'Withdrawn'] as const
  const targetStates = ['Pending', 'ChangesRequested', 'Approved', 'Marked', 'NotRequired'] as const
  const targets = ['Model3D', 'Drawing2D'] as const
  const severities = ['Note', 'Blocking'] as const
  const markupStates = ['Open', 'Resolved'] as const
  return {
    ...review,
    state: drawingReviewEnum(review.state, packageStates),
    items: review.items.map(item => ({
      ...item,
      modelState: drawingReviewEnum(item.modelState, targetStates),
      drawingState: drawingReviewEnum(item.drawingState, targetStates),
      effectiveModelVersionId: item.effectiveModelVersionId || item.modelResultVersionId || item.modelVersionId,
      effectiveDrawingVersionId: item.effectiveDrawingVersionId || item.drawingResultVersionId || item.drawingVersionId,
    })),
    markups: review.markups.map(markup => ({
      ...markup,
      target: drawingReviewEnum(markup.target, targets),
      severity: drawingReviewEnum(markup.severity, severities),
      state: drawingReviewEnum(markup.state, markupStates),
    })),
  }
}

function mapReleasePackage(releasePackage: ApiReleasePackage): ReleasePackageSummary {
  const state = releaseState(releasePackage.state)
  const approvalTasks = [...(releasePackage.approvalTasks ?? [])].sort((left, right) => (left.stepOrder ?? 0) - (right.stepOrder ?? 0))
  const currentTaskId = approvalTasks.find(task => !(task.decidedAt || task.decisionBy || task.decision !== null && task.decision !== undefined))?.id
  const decisionOf = (task: ApiApprovalTask) => typeof task.decision === 'number'
    ? (task.decision === 0 ? 'Approved' : task.decision === 1 ? 'Rejected' : undefined)
    : task.decision
  const rejectedOrder = approvalTasks.find(task => decisionOf(task) === 'Rejected')?.stepOrder
  const steps: ApprovalStep[] = approvalTasks.map((task) => {
    const stage = task.stepName || approvalStage(task.stage)
    const done = Boolean(task.decidedAt || task.decisionBy || task.decision !== null && task.decision !== undefined)
    const current = !done && currentTaskId === task.id && ['审批中', '工艺审核', '待批准'].includes(state)
    const decision = decisionOf(task)
    const skipped = !done && rejectedOrder !== undefined && (task.stepOrder ?? 0) > rejectedOrder
    const status = decision === 'Approved' ? 'approved' : decision === 'Rejected' ? 'rejected' : skipped ? 'skipped' : current ? 'current' : 'waiting'
    const decisionTime = task.decidedAt ? formatDate(task.decidedAt) : ''
    return {
      id: task.id,
      stage,
      assignee: task.assignee,
      status,
      detail: decision === 'Approved' ? `已同意${decisionTime ? ` · ${decisionTime}` : ''}`
        : decision === 'Rejected' ? `已退回${decisionTime ? ` · ${decisionTime}` : ''}`
          : skipped ? '本轮未到达' : '待处理',
      decision: task.decision ?? undefined,
      decisionBy: task.decisionBy ?? undefined,
      comment: task.comment ?? undefined,
      stepOrder: task.stepOrder,
      emergencySubstitute: task.isEmergencySubstitute ?? false,
      emergencyReason: task.emergencyReason ?? undefined,
    }
  })

  steps.push({
    id: 'production-release',
    stage: '生产发包',
    assignee: '生产部',
    status: state === '已发布' ? 'done' : state === '发布中' ? 'current' : state === '已驳回' ? 'skipped' : 'waiting',
    detail: state === '已驳回' ? '本轮未到达' : releasePackage.publishedAt ? formatDate(releasePackage.publishedAt) : '审批后自动推送',
  })

  return {
    id: releasePackage.id, number: releasePackage.number, state, steps,
    publishedPath: releasePackage.publishedPath ?? undefined, publishError: releasePackage.publishError ?? undefined,
    changeNumber: releasePackage.changeNumber ?? undefined, changeReason: releasePackage.changeReason ?? undefined,
    effectiveSerialFrom: releasePackage.effectiveSerialFrom ?? undefined, effectiveSerialTo: releasePackage.effectiveSerialTo ?? undefined,
    standardBomRevision: releasePackage.standardBomRevision ?? undefined, nonStandardBomRevision: releasePackage.nonStandardBomRevision ?? undefined,
    electricalBomRevision: releasePackage.electricalBomRevision ?? undefined,
    scope: mapReleaseScope(releasePackage.scope),
    workflowCode: releasePackage.workflowCode ?? undefined,
    workflowVersion: releasePackage.workflowVersion ?? 0,
    selectedBomItemIds: releasePackage.selectedBomItemIds ?? [],
    createsManufacturingBaseline: releasePackage.createsManufacturingBaseline ?? true,
    locksDocuments: releasePackage.locksDocuments ?? true,
    wholeSetMultiplier: releasePackage.wholeSetMultiplier ?? 1,
    changeReasonSelections: releasePackage.changeReasonSelections ?? [],
    formalSupplementPolicySnapshotted: releasePackage.formalSupplementPolicySnapshotted ?? false,
    formalSupplementMaximumCount: releasePackage.formalSupplementMaximumCount ?? null,
    formalSupplementValidDays: releasePackage.formalSupplementValidDays ?? null,
    standardBomVersionId: releasePackage.standardBomVersionId ?? undefined,
    nonStandardBomVersionId: releasePackage.nonStandardBomVersionId ?? undefined,
    electricalBomVersionId: releasePackage.electricalBomVersionId ?? undefined,
    standardBomSnapshot: (releasePackage.standardBomSnapshot ?? []).map(mapBomItem),
    nonStandardBomSnapshot: (releasePackage.nonStandardBomSnapshot ?? []).map(mapBomItem),
    electricalBomSnapshot: (releasePackage.electricalBomSnapshot ?? []).map(mapBomItem),
    createdAt: releasePackage.createdAt ?? undefined,
    publishedAt: releasePackage.publishedAt ?? undefined,
  }
}

function mapReleaseScope(value?: string | number): ReleaseScope {
  if (typeof value === 'string') return value as ReleaseScope
  return ['LegacyCombined', 'StandardLongLead', 'StandardFormal', 'StandardSupplement', 'ElectricalFormal', 'ElectricalSupplement', 'NonStandardWithDrawing'][value ?? 0] as ReleaseScope
}

function mapBomVersionState(value: string | number): BomVersionState {
  if (typeof value === 'string') return value as BomVersionState
  return ['Draft', 'InReview', 'Released', 'Obsolete'][value] as BomVersionState
}

function mapBomVersionKind(value: string | number): Exclude<BomKind, 'Unclassified'> {
  if (typeof value === 'string') return value as Exclude<BomKind, 'Unclassified'>
  return value === 2 ? 'Standard' : value === 3 ? 'NonStandard' : 'Electrical'
}

function mapBomVersion(version: ApiBomVersion): BomVersion {
  return { ...version, kind: mapBomVersionKind(version.kind), state: mapBomVersionState(version.state), items: version.items.map(mapBomItem) }
}

export function mapApiReleasePackage(releasePackage: ApiReleasePackage): ReleasePackageSummary {
  return mapReleasePackage(releasePackage)
}

function revisionDisplay(revision?: ApiRevision | null): string {
  if (!revision) return '—'
  if (revision.display) return revision.display
  if (revision.isReleased && revision.baseRevision) return revision.baseRevision
  const work = `W${revision.workIteration ?? 1}`
  return revision.baseRevision ? `${revision.baseRevision}-${work}` : work
}

function classifyVersionAlignment(latest: string, structure: string): DocumentNode['versionAlignment'] {
  if (latest === '—') return 'VersionConflict'
  if (structure === '—') return 'NotSnapshotted'
  if (latest === structure) return 'Synced'

  const latestOrder = revisionOrder(latest)
  const structureOrder = revisionOrder(structure)
  if (!latestOrder || !structureOrder) return 'VersionConflict'
  if (latestOrder[0] > structureOrder[0] || (latestOrder[0] === structureOrder[0] && latestOrder[1] > structureOrder[1])) {
    return 'StructureStale'
  }
  return 'VersionConflict'
}

function revisionOrder(value: string): [number, number] | null {
  const normalized = value.trim().toUpperCase()
  const workOnly = /^W(\d+)$/.exec(normalized)
  if (workOnly) return [0, Number(workOnly[1])]

  const released = /^([A-Z]+)(?:-W(\d+))?$/.exec(normalized)
  if (!released) return null
  const base = [...released[1]].reduce((total, letter) => total * 26 + letter.charCodeAt(0) - 64, 0)
  return [base, Number(released[2] ?? 0)]
}

function mapDocumentKind(value: number | string): DocumentKind {
  if (typeof value === 'string') return value === 'Drawing' ? 'Drawing' : value === 'Part' ? 'Part' : 'Assembly'
  return value === 1 ? 'Part' : value === 2 ? 'Drawing' : 'Assembly'
}

function mapReferenceStatus(value: number | string): ReferenceStatus {
  if (typeof value === 'string') return value as ReferenceStatus
  return (['Normal', 'Suppressed', 'Hidden', 'Lightweight', 'Virtual', 'Missing'][value] ?? 'Normal') as ReferenceStatus
}

function approvalStage(value: number | string): string {
  const key = typeof value === 'number' ? ({ 1: 'ProcessReview', 2: 'Approval', 10: 'MechanicalEngineer', 20: 'MainDesigner', 30: 'MechanicalSupervisor', 40: 'HardwareEngineer', 50: 'HardwareSupervisor', 60: 'StandardizationSupervisor' } as Record<number, string>)[value] : value
  return ({ ProcessReview: '工艺审核', Approval: '批准', MechanicalEngineer: '机械工程师自检', MainDesigner: '主设审核', MechanicalSupervisor: '机械主管批准', HardwareEngineer: '硬件工程师自检', HardwareSupervisor: '硬件主管审核', StandardizationSupervisor: '标准化主管批准' } as Record<string, string>)[key] ?? String(value)
}

function releaseState(value: number | string): string {
  const name = typeof value === 'number'
    ? ['草稿', '工艺审核', '待批准', '已驳回', '发布中', '已发布', '发布失败'][value]
    : ({ Draft: '草稿', ProcessReview: '审批中', Approval: '待批准', Rejected: '已驳回', Publishing: '发布中', Published: '已发布', PublishFailed: '发布失败' } as Record<string, string>)[value]
  return name ?? String(value)
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

export function postDesktopMessage(type: string, payload?: unknown): void {
  window.chrome?.webview?.postMessage({ type, payload })
}
