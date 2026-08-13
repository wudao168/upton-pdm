import type { ApprovalStep, AuditEntry, BomItem, CreateProjectInput, CreateSubprojectInput, DocumentKind, DocumentNode, DocumentVersionComparison, DocumentVersionSummary, EquipmentTypeDefinition, PdmCustomer, PdmSystemSettings, PdmUser, ProjectNumberingOptions, ProjectSummary, ReferenceStatus, ReleasePackageSummary } from './types'

const apiBase = (import.meta.env.VITE_PDM_API_BASE ?? 'http://127.0.0.1:5080').replace(/\/$/, '')

export class PdmApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message)
  }
}

export interface AuthSession {
  accessToken: string
  expiresAt: string
  username: string
  displayName: string
  role: string
}

export interface ProjectWorkspaceData {
  project: ProjectSummary
  root: DocumentNode
  hasDocuments: boolean
  mechanicalBom: BomItem[]
  electricalBom: BomItem[]
  releasePackage: ReleasePackageSummary | null
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
  childSequence?: number | null
  serialNumbers?: string[]
  responsibleUsers?: string[]
}

interface ApiRevision {
  baseRevision?: string | null
  workIteration?: number
  isReleased?: boolean
  display?: string
}

interface ApiDocument {
  id: string
  drawingNumber: string
  name: string
  fileName: string
  kind: number | string
  revision?: ApiRevision | null
  checkedOutBy?: string | null
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
  sequence: number
  drawingNumber: string
  name: string
  quantity: number
  unit: string
  material?: string | null
  specification?: string | null
  revision: string
  isComplete: boolean
}

interface ApiApprovalTask {
  id: string
  stage: number | string
  assignee: string
  decisionBy?: string | null
  decision?: number | string | null
  decidedAt?: string | null
  comment?: string | null
}

interface ApiReleasePackage {
  id: string
  number: string
  state: number | string
  approvalTasks?: ApiApprovalTask[]
  publishedAt?: string | null
  publishedPath?: string | null
  publishError?: string | null
}

async function requestJson<T>(path: string, init: RequestInit = {}, token?: string): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  if (token) headers.set('Authorization', `Bearer ${token}`)

  const response = await fetch(`${apiBase}${path}`, { ...init, headers, cache: 'no-store' })
  if (!response.ok) {
    let message = `PDM API请求失败（${response.status}）`
    try {
      const problem = await response.json() as { title?: string; detail?: string }
      message = problem.detail || problem.title || message
    } catch {
      // Keep the status-based message when the response has no JSON body.
    }
    throw new PdmApiError(message, response.status)
  }

  return response.json() as Promise<T>
}

export function listDocumentVersions(documentId: string, token: string): Promise<DocumentVersionSummary[]> {
  return requestJson<DocumentVersionSummary[]>(`/api/documents/${documentId}/versions`, {}, token)
}

export function compareDocumentVersions(documentId: string, left: string, right: string, token: string): Promise<DocumentVersionComparison> {
  return requestJson<DocumentVersionComparison>(`/api/documents/${documentId}/versions/compare?left=${encodeURIComponent(left)}&right=${encodeURIComponent(right)}`, {}, token)
}

export function restoreDocumentVersion(documentId: string, versionId: string, changeNote: string, token: string): Promise<unknown> {
  return requestJson(`/api/documents/${documentId}/versions/${versionId}/restore`, { method: 'POST', body: JSON.stringify({ changeNote }) }, token)
}

export async function readDocumentVersionFile(documentId: string, versionId: string, token: string, download: boolean): Promise<Blob> {
  const response = await fetch(`${apiBase}/api/documents/${documentId}/versions/${versionId}/file?download=${download}`, { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store' })
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

export function saveCustomer(customer: Partial<PdmCustomer> & Pick<PdmCustomer, 'code' | 'name' | 'isActive'>, token: string): Promise<PdmCustomer> {
  return requestJson(customer.id ? `/api/customers/${customer.id}` : '/api/customers', {
    method: customer.id ? 'PUT' : 'POST',
    body: JSON.stringify({ code: customer.code, name: customer.name, isActive: customer.isActive }),
  }, token)
}

export function listUsers(token: string): Promise<PdmUser[]> {
  return requestJson('/api/users', {}, token)
}

export async function updateProjectResponsibles(projectId: string, usernames: string[], token: string): Promise<ProjectSummary> {
  const project = await requestJson<ApiProject>(`/api/projects/${projectId}/responsibles`, { method: 'PUT', body: JSON.stringify({ usernames }) }, token)
  return mapProject(project)
}

export function getSystemSettings(token: string): Promise<PdmSystemSettings> {
  return requestJson('/api/system-settings', {}, token)
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

export async function saveBom(projectId: string, kind: 'Mechanical' | 'Electrical', items: BomItem[], token: string): Promise<BomItem[]> {
  const saved = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/${kind}`, {
    method: 'PUT',
    body: JSON.stringify({ items: items.map(item => ({ ...item, isComplete: item.complete })) }),
  }, token)
  return saved.map(mapBomItem)
}

export async function importBom(projectId: string, kind: 'Mechanical' | 'Electrical', file: File, token: string): Promise<BomItem[]> {
  const form = new FormData()
  form.append('file', file, file.name)
  const imported = await requestJson<ApiBomItem[]>(`/api/projects/${projectId}/boms/${kind}/import`, { method: 'POST', body: form }, token)
  return imported.map(mapBomItem)
}

export async function exportBom(projectId: string, kind: 'Mechanical' | 'Electrical', token: string): Promise<Blob> {
  const response = await fetch(`${apiBase}/api/projects/${projectId}/boms/${kind}/export`, { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store' })
  if (!response.ok) throw new PdmApiError(`BOM导出失败（${response.status}）`, response.status)
  return response.blob()
}

export function createReleasePackage(projectId: string, number: string, processReviewer: string, approver: string, token: string): Promise<ApiReleasePackage> {
  return requestJson('/api/release-packages', { method: 'POST', body: JSON.stringify({ projectId, referenceSnapshotId: null, number, processReviewer, approver }) }, token)
}

export function submitReleasePackage(releasePackageId: string, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/release-packages/${releasePackageId}/submit`, { method: 'POST' }, token)
}

export function decideApproval(taskId: string, decision: 'Approved' | 'Rejected', comment: string, token: string): Promise<ApiReleasePackage> {
  return requestJson(`/api/approval-tasks/${taskId}/decision`, { method: 'POST', body: JSON.stringify({ decision: decision === 'Approved' ? 0 : 1, comment }) }, token)
}

export async function uploadReleaseFile(projectId: string, packageNumber: string, file: File, token: string, onProgress?: (percent: number) => void): Promise<void> {
  const extension = file.name.split('.').pop()?.toLocaleLowerCase()
  if (extension !== 'pdf' && extension !== 'dwg') throw new PdmApiError('生产发包只允许上传PDF或DWG。', 400)
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  const sha256 = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('').toUpperCase()
  const session = await requestJson<{ id: string; chunkSize: number }>(`/api/uploads/sessions`, { method: 'POST', body: JSON.stringify({ projectId, fileName: file.name, totalLength: file.size, sha256 }) }, token)
  const chunks = Math.ceil(file.size / session.chunkSize)
  for (let index = 0; index < chunks; index++) {
    const body = file.slice(index * session.chunkSize, Math.min(file.size, (index + 1) * session.chunkSize))
    const response = await fetch(`${apiBase}/api/uploads/sessions/${session.id}/chunks/${index}`, { method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body })
    if (!response.ok) throw new PdmApiError(`发布文件分块${index + 1}上传失败（${response.status}）`, response.status)
    onProgress?.(Math.round(((index + 1) / chunks) * 100))
  }
  const safeName = file.name.replace(/[\\/:*?"<>|]/g, '_')
  await requestJson(`/api/uploads/sessions/${session.id}/complete`, { method: 'POST', body: JSON.stringify({ relativeTargetPath: `.release-staging/${packageNumber}/drawings/${safeName}` }) }, token)
}

export function listAudit(token: string): Promise<AuditEntry[]> {
  return requestJson('/api/audit?take=200', {}, token)
}

export function getStorageStatus(projectId: string, token: string): Promise<{ vaultAvailable: boolean; releaseAvailable: boolean }> {
  return requestJson(`/api/projects/${projectId}/storage-status`, {}, token)
}

export async function loadProjectWorkspace(projectId: string, token: string): Promise<ProjectWorkspaceData> {
  const project = await requestJson<ApiProject>(`/api/projects/${projectId}`, {}, token)

  const [documents, referenceRoot, mechanical, electrical, releasePackages] = await Promise.all([
    requestJson<ApiDocument[]>(`/api/projects/${project.id}/documents`, {}, token),
    requestJson<ApiReferenceNode>(`/api/projects/${project.id}/reference-tree`, {}, token).catch(error => {
      if (error instanceof PdmApiError && error.status === 404) return null
      throw error
    }),
    requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/Mechanical`, {}, token),
    requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/Electrical`, {}, token),
    requestJson<ApiReleasePackage[]>(`/api/projects/${project.id}/release-packages`, {}, token),
  ])

  const documentsById = new Map(documents.map((document) => [document.id, document]))
  return {
    project: mapProject(project),
    root: referenceRoot
      ? mapReferenceNode(referenceRoot, documentsById)
      : emptyProjectRoot(project),
    hasDocuments: documents.length > 0,
    mechanicalBom: mechanical.map(mapBomItem),
    electricalBom: electrical.map(mapBomItem),
    releasePackage: releasePackages.length > 0 ? mapReleasePackage(releasePackages[0]) : null,
  }
}

function emptyProjectRoot(project: ApiProject): DocumentNode {
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
    childSequence: project.childSequence ?? undefined,
    serialNumbers: project.serialNumbers ?? [],
    responsibleUsers: project.responsibleUsers ?? (project.owner ? [project.owner] : []),
  }
}

function mapReferenceNode(
  node: ApiReferenceNode,
  documentsById: Map<string, ApiDocument>,
): DocumentNode {
  const document = node.documentId ? documentsById.get(node.documentId) : undefined
  const seenInstancePaths = new Set<string>()
  const children = (node.children ?? [])
    .filter((child) => {
      const instancePath = child.instancePath.trim().toLocaleLowerCase('zh-CN')
      if (!instancePath) return true
      if (seenInstancePaths.has(instancePath)) return false
      seenInstancePaths.add(instancePath)
      return true
    })
    .map((child) => mapReferenceNode(child, documentsById))
  return {
    id: node.nodeId || node.instancePath,
    documentId: node.documentId ?? undefined,
    drawingNumber: document?.drawingNumber ?? node.fileName.replace(/\.[^.]+$/, ''),
    name: node.displayName || document?.name || node.fileName.replace(/\.[^.]+$/, ''),
    fileName: node.fileName,
    kind: mapDocumentKind(node.kind),
    configuration: node.configuration || '默认',
    quantity: node.quantity,
    version: revisionDisplay(node.revision ?? document?.revision),
    checkedOutBy: node.checkedOutBy ?? document?.checkedOutBy ?? undefined,
    status: node.documentId ? mapReferenceStatus(node.status) : 'Unregistered',
    children,
  }
}

function mapBomItem(item: ApiBomItem): BomItem {
  return {
    sequence: item.sequence,
    drawingNumber: item.drawingNumber,
    name: item.name,
    quantity: item.quantity,
    unit: item.unit,
    material: item.material ?? undefined,
    specification: item.specification ?? undefined,
    revision: item.revision,
    complete: item.isComplete,
  }
}

function mapReleasePackage(releasePackage: ApiReleasePackage): ReleasePackageSummary {
  const state = releaseState(releasePackage.state)
  const steps: ApprovalStep[] = (releasePackage.approvalTasks ?? []).map((task) => {
    const stage = approvalStage(task.stage)
    const done = Boolean(task.decidedAt || task.decisionBy || task.decision !== null && task.decision !== undefined)
    const current = !done && ((state === '工艺审核' && stage === '工艺审核') || (state === '待批准' && stage === '批准'))
    return {
      id: task.id,
      stage,
      assignee: task.assignee,
      status: done ? 'done' : current ? 'current' : 'waiting',
      detail: task.decidedAt ? formatDate(task.decidedAt) : done ? '已处理' : '待处理',
      decision: task.decision ?? undefined,
      comment: task.comment ?? undefined,
    }
  })

  steps.push({
    id: 'production-release',
    stage: '生产发包',
    assignee: '生产部',
    status: state === '已发布' ? 'done' : state === '发布中' ? 'current' : 'waiting',
    detail: releasePackage.publishedAt ? formatDate(releasePackage.publishedAt) : '审批后自动推送',
  })

  return { id: releasePackage.id, number: releasePackage.number, state, steps, publishedPath: releasePackage.publishedPath ?? undefined, publishError: releasePackage.publishError ?? undefined }
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

function mapDocumentKind(value: number | string): DocumentKind {
  if (typeof value === 'string') return value === 'Drawing' ? 'Drawing' : value === 'Part' ? 'Part' : 'Assembly'
  return value === 1 ? 'Part' : value === 2 ? 'Drawing' : 'Assembly'
}

function mapReferenceStatus(value: number | string): ReferenceStatus {
  if (typeof value === 'string') return value as ReferenceStatus
  return (['Normal', 'Suppressed', 'Hidden', 'Lightweight', 'Virtual', 'Missing'][value] ?? 'Normal') as ReferenceStatus
}

function approvalStage(value: number | string): string {
  return value === 1 || value === 'ProcessReview' ? '工艺审核' : '批准'
}

function releaseState(value: number | string): string {
  const name = typeof value === 'number'
    ? ['草稿', '工艺审核', '待批准', '已驳回', '发布中', '已发布', '发布失败'][value]
    : ({ Draft: '草稿', ProcessReview: '工艺审核', Approval: '待批准', Rejected: '已驳回', Publishing: '发布中', Published: '已发布', PublishFailed: '发布失败' } as Record<string, string>)[value]
  return name ?? String(value)
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

export function postDesktopMessage(type: string, payload?: unknown): void {
  window.chrome?.webview?.postMessage({ type, payload })
}
