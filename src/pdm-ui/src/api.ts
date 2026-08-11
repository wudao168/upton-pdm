import type { ApprovalStep, BomItem, DocumentKind, DocumentNode, ProjectSummary, ReferenceStatus, ReleasePackageSummary } from './types'

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
  stage: number | string
  assignee: string
  decisionBy?: string | null
  decision?: number | string | null
  decidedAt?: string | null
}

interface ApiReleasePackage {
  id: string
  number: string
  state: number | string
  approvalTasks?: ApiApprovalTask[]
  publishedAt?: string | null
}

async function requestJson<T>(path: string, init: RequestInit = {}, token?: string): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body) headers.set('Content-Type', 'application/json')
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

export async function loadProjectWorkspace(token: string): Promise<ProjectWorkspaceData> {
  const projects = await requestJson<ApiProject[]>('/api/projects', {}, token)
  const project = projects.find((candidate) => candidate.isActive) ?? projects[0]
  if (!project) throw new PdmApiError('当前账号没有可访问的PDM项目。', 404)

  const [documents, referenceRoot, mechanical, electrical, releasePackages] = await Promise.all([
    requestJson<ApiDocument[]>(`/api/projects/${project.id}/documents`, {}, token),
    requestJson<ApiReferenceNode>(`/api/projects/${project.id}/reference-tree`, {}, token),
    requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/Mechanical`, {}, token),
    requestJson<ApiBomItem[]>(`/api/projects/${project.id}/boms/Electrical`, {}, token),
    requestJson<ApiReleasePackage[]>(`/api/projects/${project.id}/release-packages`, {}, token),
  ])

  const documentsById = new Map(documents.map((document) => [document.id, document]))
  const documentsByFileName = new Map(documents.map((document) => [document.fileName.toLocaleLowerCase(), document]))

  return {
    project: mapProject(project),
    root: mapReferenceNode(referenceRoot, documentsById, documentsByFileName),
    mechanicalBom: mechanical.map(mapBomItem),
    electricalBom: electrical.map(mapBomItem),
    releasePackage: releasePackages.length > 0 ? mapReleasePackage(releasePackages[0]) : null,
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
  }
}

function mapReferenceNode(
  node: ApiReferenceNode,
  documentsById: Map<string, ApiDocument>,
  documentsByFileName: Map<string, ApiDocument>,
): DocumentNode {
  const document = (node.documentId ? documentsById.get(node.documentId) : undefined)
    ?? documentsByFileName.get(node.fileName.toLocaleLowerCase())
  return {
    id: node.documentId ?? node.nodeId,
    drawingNumber: document?.drawingNumber ?? node.fileName.replace(/\.[^.]+$/, ''),
    name: document?.name ?? node.displayName,
    fileName: node.fileName,
    kind: mapDocumentKind(node.kind),
    configuration: node.configuration || '默认',
    quantity: node.quantity,
    version: revisionDisplay(node.revision ?? document?.revision),
    checkedOutBy: node.checkedOutBy ?? document?.checkedOutBy ?? undefined,
    status: mapReferenceStatus(node.status),
    children: (node.children ?? []).map((child) => mapReferenceNode(child, documentsById, documentsByFileName)),
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
      stage,
      assignee: task.assignee,
      status: done ? 'done' : current ? 'current' : 'waiting',
      detail: task.decidedAt ? formatDate(task.decidedAt) : done ? '已处理' : '待处理',
    }
  })

  steps.push({
    stage: '生产发包',
    assignee: '生产部',
    status: state === '已发布' ? 'done' : state === '发布中' ? 'current' : 'waiting',
    detail: releasePackage.publishedAt ? formatDate(releasePackage.publishedAt) : '审批后自动推送',
  })

  return { id: releasePackage.id, number: releasePackage.number, state, steps }
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
