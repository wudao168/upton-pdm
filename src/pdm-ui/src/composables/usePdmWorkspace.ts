import { computed, onMounted, ref } from 'vue'
import { checkHealth, compareDocumentVersions, createProject as createProjectRequest, createSubproject as createSubprojectRequest, createReleasePackage, decideApproval, deleteProject as deleteProjectRequest, exportBom, forceReleaseEditLock as forceReleaseEditLockRequest, getCrmIntegrationSettings, getOrganizationDirectory, getProjectNumberingOptions, getRolePermissionDirectory, getStorageStatus, getSystemSettings, importBom, listAudit, listCustomers, listDocumentVersions, listDocumentWhereUsed, listEditLocks, listEquipmentTypes, listFolderTemplate, listMyApprovalTasks, listProjectAudit, listProjects, listProjectVersions, loadProjectWorkspace, login as apiLogin, obsoleteDocument as obsoleteDocumentRequest, PdmApiError, postDesktopMessage, readDocumentVersionFile, requestEditLockRelease as requestEditLockReleaseRequest, restoreDocumentVersion, saveBom, saveEquipmentType as saveEquipmentTypeRequest, saveFolderTemplate as saveFolderTemplateRequest, saveOrganizationUnit as saveOrganizationUnitRequest, saveProjectOrganization as saveProjectOrganizationRequest, submitReleasePackage, syncCrmCustomers as syncCrmCustomersRequest, testCrmIntegration as testCrmIntegrationRequest, updateChildProjectDesigners as updateChildProjectDesignersRequest, updateCrmIntegrationSettings as updateCrmIntegrationSettingsRequest, updateMainProjectStaffing as updateMainProjectStaffingRequest, updateOrganizationCounters as updateOrganizationCountersRequest, updateOrganizationMemberships as updateOrganizationMembershipsRequest, updateOrganizationUnitManagers as updateOrganizationUnitManagersRequest, updateProjectExecutionUnit as updateProjectExecutionUnitRequest, updateProjectFolderPermissions as updateProjectFolderPermissionsRequest, updateRolePermissions as updateRolePermissionsRequest, updateSystemSettings as updateSystemSettingsRequest, uploadReleaseFile, withdrawReleasePackage } from '../api'
import type { AuthSession } from '../api'
import type { AuditEntry, BomItem, CreateProjectInput, CreateSubprojectInput, CrmConnectionTestResult, CrmCustomerSyncResult, CrmIntegrationSettings, DocumentFilter, DocumentModelDrawingRelation, DocumentNode, DocumentVersionComparison, DocumentVersionSummary, DocumentWhereUsed, EditLockSummary, EquipmentTypeDefinition, FolderPermissionRule, MainProjectStaffingInput, ManagedDocument, MyApprovalTask, OrganizationDirectory, PdmCustomer, PdmSystemSettings, PdmUser, ProjectFolder, ProjectFolderTemplateNode, ProjectNumberingOptions, ProjectSummary, ProjectVersionItem, ReleasePackageSummary, RolePermissionDirectory, SaveOrganizationUnitInput, SaveProjectOrganizationInput, SolidWorksOpenMode, UpdateCrmIntegrationInput } from '../types'

const sessionKey = 'upton-pdm-session'
const defaultSystemSettings: PdmSystemSettings = { vaultRoot: '', releaseRoot: '', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48 }
const defaultCrmIntegrationSettings: CrmIntegrationSettings = { baseUrl: '', username: '', passwordConfigured: false, autoSyncEnabled: false, autoSyncIntervalMinutes: 60, lastSyncAt: null, lastSyncCount: 0, lastAutoSyncAttemptAt: null, lastAutoSyncError: null }

const emptyProject: ProjectSummary = {
  id: '',
  code: '',
  name: '',
  owner: '',
  stage: '',
  vaultName: '',
  vaultLocation: '',
  releaseLocation: '',
  quantity: 1,
  serialNumbers: [],
  responsibleUsers: [],
  collaborativeProjectManagers: [],
  designers: [],
  canAssignExecutionUnit: false,
  canManageMainStaffing: false,
  canAssignDesigners: false,
  canReadContent: false,
}

const emptyRoot: DocumentNode = {
  id: '',
  drawingNumber: '',
  name: '',
  fileName: '',
  kind: 'Assembly',
  configuration: '',
  quantity: 1,
  version: '—',
  status: 'Normal',
  children: [],
}

function findNode(root: DocumentNode, id: string): DocumentNode | undefined {
  if (root.id === id) return root
  for (const child of root.children) {
    const match = findNode(child, id)
    if (match) return match
  }
  return undefined
}

function findNodeByDocumentId(root: DocumentNode, documentId: string): DocumentNode | undefined {
  if (root.documentId === documentId) return root
  for (const child of root.children) {
    const match = findNodeByDocumentId(child, documentId)
    if (match) return match
  }
  return undefined
}

function isIssue(node: DocumentNode) {
  return node.status === 'Missing'
    || node.status === 'Unregistered'
    || (node.versionAlignment !== undefined && node.versionAlignment !== 'Synced')
}

function matchesQuery(node: DocumentNode, query: string) {
  const normalized = query.trim().toLocaleLowerCase('zh-CN')
  if (!normalized) return true
  const ownText = `${node.drawingNumber} ${node.name} ${node.fileName}`.toLocaleLowerCase('zh-CN')
  return ownText.includes(normalized)
}

function filterNode(node: DocumentNode, query: string, filter: Exclude<DocumentFilter, 'drawing'>): DocumentNode | undefined {
  const children = node.children.map((child) => filterNode(child, query, filter)).filter((child): child is DocumentNode => Boolean(child))
  const matchesKind = filter === 'all'
    || (filter === 'model' && node.kind !== 'Drawing')
    || (filter === 'issue' && isIssue(node))
  if ((matchesKind && matchesQuery(node, query)) || children.length > 0) return { ...node, children }
  return undefined
}

function managedDocumentNode(document: ManagedDocument): DocumentNode {
  return {
    id: `document-${document.id}`,
    documentId: document.id,
    drawingNumber: document.drawingNumber,
    name: document.name,
    fileName: document.fileName,
    kind: document.kind,
    configuration: document.kind === 'Drawing' ? '工程图' : '默认',
    quantity: 1,
    version: document.revision,
    checkedOutBy: document.checkedOutBy,
    lifecycleState: document.state,
    status: 'Normal',
    children: [],
  }
}

function firstMatchingNode(root: DocumentNode | undefined, predicate: (node: DocumentNode) => boolean): DocumentNode | undefined {
  if (!root) return undefined
  if (predicate(root)) return root
  for (const child of root.children) {
    const match = firstMatchingNode(child, predicate)
    if (match) return match
  }
  return undefined
}

function countUniqueIssues(root: DocumentNode) {
  const keys = new Set<string>()
  const visit = (node: DocumentNode) => {
    if (isIssue(node)) keys.add(node.documentId ?? node.id)
    node.children.forEach(visit)
  }
  visit(root)
  return keys.size
}

function messageFrom(error: unknown): string {
  return error instanceof Error ? error.message : 'PDM数据加载失败。'
}

export function usePdmWorkspace() {
  const projects = ref<ProjectSummary[]>([])
  const projectNumberingOptions = ref<ProjectNumberingOptions>({ organizations: [], projectTypes: [], equipmentTypes: [] })
  const customers = ref<PdmCustomer[]>([])
  const crmIntegrationSettings = ref<CrmIntegrationSettings>({ ...defaultCrmIntegrationSettings })
  const users = ref<PdmUser[]>([])
  const organizationDirectory = ref<OrganizationDirectory>({ organizations: [], units: [], memberships: [], managers: [], users: [] })
  const rolePermissionDirectory = ref<RolePermissionDirectory>({ permissions: [], roles: [] })
  const systemSettings = ref<PdmSystemSettings>({ ...defaultSystemSettings })
  const equipmentTypes = ref<EquipmentTypeDefinition[]>([])
  const project = ref<ProjectSummary>(emptyProject)
  const projectFolders = ref<ProjectFolder[]>([])
  const managedDocuments = ref<ManagedDocument[]>([])
  const documentRelations = ref<DocumentModelDrawingRelation[]>([])
  const folderTemplate = ref<ProjectFolderTemplateNode[]>([])
  const root = ref<DocumentNode>(emptyRoot)
  const mechanicalBom = ref<BomItem[]>([])
  const electricalBom = ref<BomItem[]>([])
  const releasePackage = ref<ReleasePackageSummary | null>(null)
  const selectedId = ref('')
  const searchQuery = ref('')
  const documentFilter = ref<DocumentFilter>('all')
  const serviceOnline = ref(false)
  const authenticated = ref(false)
  const currentUser = ref('')
  const currentUsername = ref('')
  const currentRole = ref('')
  const currentPermissions = ref<string[]>([])
  const loginPending = ref(false)
  const loginError = ref('')
  const loading = ref(false)
  const loadError = ref('')
  const ready = ref(false)
  const hasDocuments = ref(false)
  const versionDrawerOpen = ref(false)
  const whereUsedDrawerOpen = ref(false)
  const whereUsed = ref<DocumentWhereUsed[]>([])
  const whereUsedLoading = ref(false)
  const whereUsedError = ref('')
  const versions = ref<DocumentVersionSummary[]>([])
  const leftVersionId = ref('')
  const rightVersionId = ref('')
  const versionComparison = ref<DocumentVersionComparison | null>(null)
  const versionLoading = ref(false)
  const versionError = ref('')
  const approvalDialogOpen = ref(false)
  const operationPending = ref(false)
  const operationError = ref('')
  const uploadProgress = ref(0)
  const auditEntries = ref<AuditEntry[]>([])
  const myApprovalTasks = ref<MyApprovalTask[]>([])
  const editLocks = ref<EditLockSummary[]>([])
  const projectVersions = ref<ProjectVersionItem[]>([])
  const projectAuditEntries = ref<AuditEntry[]>([])
  const storageStatus = ref<{ vaultAvailable: boolean; releaseAvailable: boolean } | null>(null)
  let accessToken = ''
  let pendingVersionComparison: { documentId: string; leftVersionId?: string; rightVersionId?: string } | null = null

  const managedDocumentNodes = computed(() => managedDocuments.value.map(managedDocumentNode))
  const drawingNodes = computed(() => managedDocumentNodes.value.filter(node => node.kind === 'Drawing'))
  const selectedNode = computed(() => findNode(root.value, selectedId.value)
    ?? managedDocumentNodes.value.find(node => node.id === selectedId.value)
    ?? root.value)
  const selectedDocumentId = computed(() => selectedNode.value.documentId)
  const filteredTree = computed<DocumentNode | undefined>(() => documentFilter.value === 'drawing'
    ? undefined
    : filterNode(root.value, searchQuery.value, documentFilter.value))
  const filteredDrawings = computed(() => drawingNodes.value.filter(node => matchesQuery(node, searchQuery.value)))
  const documentFilterCounts = computed(() => {
    const unresolvedIssues = countUniqueIssues(root.value)
    const model = managedDocuments.value.filter(document => document.kind !== 'Drawing').length
    const drawing = managedDocuments.value.filter(document => document.kind === 'Drawing').length
    const unregistered = (() => {
      const keys = new Set<string>()
      const visit = (node: DocumentNode) => {
        if (isIssue(node) && !node.documentId) keys.add(node.id)
        node.children.forEach(visit)
      }
      visit(root.value)
      return keys.size
    })()
    return { all: model + drawing + unregistered, model, drawing, issue: unresolvedIssues }
  })
  const relatedNodes = computed(() => {
    const documentId = selectedNode.value.documentId
    if (!documentId) return []
    const relatedIds = new Set<string>()
    for (const relation of documentRelations.value) {
      if (relation.modelDocumentId === documentId) relatedIds.add(relation.drawingDocumentId)
      if (relation.drawingDocumentId === documentId) relatedIds.add(relation.modelDocumentId)
    }
    return [...relatedIds].map(id => findNodeByDocumentId(root.value, id)
      ?? managedDocumentNodes.value.find(node => node.documentId === id))
      .filter((node): node is DocumentNode => Boolean(node))
  })
  const warningCount = computed(() => ready.value && hasDocuments.value ? documentFilterCounts.value.issue : 0)
  const normalCount = computed(() => ready.value && hasDocuments.value
    ? Math.max(0, documentFilterCounts.value.all - warningCount.value)
    : 0)
  const hasPermission = (code: string) => currentPermissions.value.includes(code)

  function selectNode(node: DocumentNode) {
    selectedId.value = node.id
    if (node.documentId) postDesktopMessage('document-selected', { documentId: node.documentId, fileName: node.fileName })
  }

  function setDocumentFilter(filter: DocumentFilter) {
    documentFilter.value = filter
    const current = selectedNode.value
    const compatible = filter === 'all'
      || (filter === 'model' && current.kind !== 'Drawing')
      || (filter === 'drawing' && current.kind === 'Drawing')
      || (filter === 'issue' && isIssue(current))
    if (compatible) return

    const resolveCandidate = () => {
      const related = relatedNodes.value.find(node => filter === 'drawing' ? node.kind === 'Drawing' : filter === 'model' ? node.kind !== 'Drawing' : false)
      return filter === 'drawing'
        ? (related && matchesQuery(related, searchQuery.value) ? related : filteredDrawings.value[0])
        : filter === 'issue'
          ? firstMatchingNode(filteredTree.value, isIssue)
          : filter === 'model'
            ? (related && matchesQuery(related, searchQuery.value) ? related : firstMatchingNode(filteredTree.value, node => node.kind !== 'Drawing'))
            : current
    }
    let candidate = resolveCandidate()
    if (!candidate && searchQuery.value.trim()) {
      searchQuery.value = ''
      candidate = resolveCandidate()
    }
    if (candidate) selectNode(candidate)
  }

  function selectRelatedNode(node: DocumentNode) {
    documentFilter.value = node.kind === 'Drawing' ? 'drawing' : 'model'
    selectNode(node)
  }

  function clearProjectWorkspace() {
    project.value = emptyProject
    projectFolders.value = []
    managedDocuments.value = []
    documentRelations.value = []
    root.value = emptyRoot
    hasDocuments.value = false
    mechanicalBom.value = []
    electricalBom.value = []
    releasePackage.value = null
    selectedId.value = ''
    searchQuery.value = ''
    documentFilter.value = 'all'
    projectVersions.value = []
    projectAuditEntries.value = []
  }

  function selectDocument(documentId: string) {
    const node = findNodeByDocumentId(root.value, documentId)
      ?? managedDocumentNodes.value.find(candidate => candidate.documentId === documentId)
    if (node) selectNode(node)
    return Boolean(node)
  }

  async function openVersionDrawer(left?: string, right?: string) {
    versionDrawerOpen.value = true
    versionLoading.value = true
    versionError.value = ''
    versionComparison.value = null
    const documentId = selectedDocumentId.value
    if (!documentId) {
      versions.value = []
      versionError.value = '该引用尚未登记到PDM，暂无版本记录。'
      versionLoading.value = false
      return
    }
    try {
      versions.value = await listDocumentVersions(documentId, accessToken)
      leftVersionId.value = left && versions.value.some(version => version.id === left) ? left : versions.value.at(-1)?.id ?? ''
      rightVersionId.value = right && versions.value.some(version => version.id === right) ? right : versions.value[0]?.id ?? ''
      await compareVersions()
    } catch (error) {
      versionError.value = messageFrom(error)
    } finally {
      versionLoading.value = false
    }
  }

  async function compareVersions() {
    if (!leftVersionId.value || !rightVersionId.value || leftVersionId.value === rightVersionId.value) {
      versionComparison.value = null
      return
    }
    versionLoading.value = true
    versionError.value = ''
    try {
      versionComparison.value = await compareDocumentVersions(selectedDocumentId.value!, leftVersionId.value, rightVersionId.value, accessToken)
    } catch (error) {
      versionError.value = messageFrom(error)
    } finally {
      versionLoading.value = false
    }
  }

  async function openVersionFile(versionId: string, download: boolean) {
    const documentId = selectedDocumentId.value
    if (!documentId) throw new Error('该引用尚未登记到PDM。')
    const blob = await readDocumentVersionFile(documentId, versionId, accessToken, download)
    const url = URL.createObjectURL(blob)
    if (download) {
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `${selectedNode.value.drawingNumber}-${versions.value.find(version => version.id === versionId)?.revision.display ?? 'history'}-${selectedNode.value.fileName}`
      anchor.click()
    } else {
      window.open(url, '_blank', 'noopener')
    }
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
  }

  async function restoreVersion(versionId: string, changeNote: string) {
    const documentId = selectedDocumentId.value
    if (!documentId) throw new Error('该引用尚未登记到PDM。')
    await restoreDocumentVersion(documentId, versionId, changeNote, accessToken)
    await Promise.all([reload(), openVersionDrawer(undefined, undefined)])
  }

  function openDocument(node = selectedNode.value, mode: SolidWorksOpenMode = 'LatestReadOnly', versionId?: string) {
    if (!node.documentId) return
    postDesktopMessage('open-document', { projectId: project.value.id, documentId: node.documentId, fileName: node.fileName, mode, versionId })
  }

  function previewDocument(node = selectedNode.value) {
    if (!node.documentId) return
    postDesktopMessage('preview-document', { documentId: node.documentId, fileName: node.fileName, revision: node.version })
  }

  function submitApproval() {
    if (releasePackage.value) approvalDialogOpen.value = true
  }

  async function saveBomItems(kind: 'Mechanical' | 'Electrical', items: BomItem[]) {
    operationPending.value = true
    operationError.value = ''
    try {
      const saved = await saveBom(project.value.id, kind, items, accessToken)
      if (kind === 'Mechanical') mechanicalBom.value = saved
      else electricalBom.value = saved
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function importBomFile(kind: 'Mechanical' | 'Electrical', file: File) {
    operationPending.value = true
    operationError.value = ''
    try {
      const imported = await importBom(project.value.id, kind, file, accessToken)
      if (kind === 'Mechanical') mechanicalBom.value = imported
      else electricalBom.value = imported
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function exportBomFile(kind: 'Mechanical' | 'Electrical') {
    const blob = await exportBom(project.value.id, kind, accessToken)
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `${kind.toLocaleLowerCase()}-bom.xlsx`
    anchor.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
  }

  async function createPackage(number: string, processReviewer: string, approver: string) {
    operationPending.value = true
    operationError.value = ''
    try {
      await createReleasePackage(project.value.id, number, processReviewer, approver, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function uploadPackageFile(file: File) {
    if (!releasePackage.value) throw new Error('请先创建发布包。')
    operationPending.value = true
    operationError.value = ''
    uploadProgress.value = 0
    try {
      await uploadReleaseFile(project.value.id, releasePackage.value.number, file, accessToken, value => { uploadProgress.value = value })
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function submitPackage() {
    if (!releasePackage.value) throw new Error('当前没有发布包。')
    operationPending.value = true
    operationError.value = ''
    try {
      await submitReleasePackage(releasePackage.value.id, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function withdrawPackage(comment: string) {
    if (!releasePackage.value) throw new Error('当前没有发布包。')
    operationPending.value = true
    operationError.value = ''
    try {
      await withdrawReleasePackage(releasePackage.value.id, comment, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function openWhereUsed() {
    whereUsedDrawerOpen.value = true
    whereUsedLoading.value = true
    whereUsedError.value = ''
    whereUsed.value = []
    try {
      const documentId = selectedDocumentId.value
      if (!documentId) throw new Error('该引用尚未登记到PDM。')
      whereUsed.value = await listDocumentWhereUsed(documentId, accessToken)
    } catch (error) {
      whereUsedError.value = messageFrom(error)
    } finally {
      whereUsedLoading.value = false
    }
  }

  async function obsoleteSelectedDocument(comment: string) {
    const documentId = selectedDocumentId.value
    if (!documentId) throw new Error('该引用尚未登记到PDM。')
    operationPending.value = true
    try {
      await obsoleteDocumentRequest(documentId, comment, accessToken)
      await reload()
    } finally {
      operationPending.value = false
    }
  }

  async function decideApprovalTask(taskId: string, decision: 'Approved' | 'Rejected', comment: string) {
    operationPending.value = true
    operationError.value = ''
    try {
      await decideApproval(taskId, decision, comment, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function loadAuditEntries() {
    auditEntries.value = await listAudit(accessToken)
  }

  async function requestMyApprovalTasks() {
    try {
      return await listMyApprovalTasks(accessToken)
    } catch (error) {
      if (error instanceof PdmApiError && error.status === 404) return []
      throw error
    }
  }

  async function requestEditLocks() {
    try {
      return await listEditLocks(accessToken)
    } catch (error) {
      if (error instanceof PdmApiError && error.status === 404) return []
      throw error
    }
  }

  async function loadMyApprovalTasks() {
    [myApprovalTasks.value, editLocks.value] = await Promise.all([requestMyApprovalTasks(), requestEditLocks()])
  }

  async function requestEditLockRelease(documentId: string, reason: string) {
    operationPending.value = true
    try { await requestEditLockReleaseRequest(documentId, reason, accessToken); editLocks.value = await requestEditLocks() }
    finally { operationPending.value = false }
  }

  async function forceReleaseEditLock(documentId: string, reason: string) {
    operationPending.value = true
    try { await forceReleaseEditLockRequest(documentId, reason, accessToken); editLocks.value = await requestEditLocks() }
    finally { operationPending.value = false }
  }

  async function loadProjectVersions() {
    if (!project.value.id) return
    try {
      projectVersions.value = await listProjectVersions(project.value.id, accessToken)
    } catch (error) {
      if (error instanceof PdmApiError && error.status === 404) {
        projectVersions.value = []
        return
      }
      throw error
    }
  }

  async function loadProjectAuditEntries() {
    if (!project.value.id) return
    try {
      projectAuditEntries.value = await listProjectAudit(project.value.id, accessToken)
    } catch (error) {
      if (error instanceof PdmApiError && error.status === 404) {
        projectAuditEntries.value = []
        return
      }
      throw error
    }
  }

  async function loadStorageStatus() {
    storageStatus.value = await getStorageStatus(project.value.id, accessToken)
  }

  function applySession(session: AuthSession) {
    accessToken = session.accessToken
    postDesktopMessage('session-ready', { accessToken: session.accessToken })
    authenticated.value = true
    currentUser.value = session.displayName || session.username
    currentUsername.value = session.username
    currentRole.value = session.role
    currentPermissions.value = session.permissions ?? []
    window.sessionStorage.setItem(sessionKey, JSON.stringify(session))
  }

  function clearSession() {
    accessToken = ''
    postDesktopMessage('session-clear')
    authenticated.value = false
    currentUser.value = ''
    currentUsername.value = ''
    currentRole.value = ''
    currentPermissions.value = []
    ready.value = false
    projects.value = []
    projectNumberingOptions.value = { organizations: [], projectTypes: [], equipmentTypes: [] }
    customers.value = []
    crmIntegrationSettings.value = { ...defaultCrmIntegrationSettings }
    users.value = []
    organizationDirectory.value = { organizations: [], units: [], memberships: [], managers: [], users: [] }
    rolePermissionDirectory.value = { permissions: [], roles: [] }
    systemSettings.value = { ...defaultSystemSettings }
    equipmentTypes.value = []
    folderTemplate.value = []
    clearProjectWorkspace()
    myApprovalTasks.value = []
    editLocks.value = []
    window.sessionStorage.removeItem(sessionKey)
  }

  async function reload(projectId?: string) {
    if (!accessToken) return
    loading.value = true
    loadError.value = ''
    try {
      const [loadedProjects, loadedOptions, loadedCustomers, loadedTasks, loadedEditLocks, loadedDirectory] = await Promise.all([
        listProjects(accessToken),
        getProjectNumberingOptions(accessToken),
        listCustomers(accessToken),
        requestMyApprovalTasks(),
        requestEditLocks(),
        getOrganizationDirectory(accessToken),
      ])
      projects.value = loadedProjects
      projectNumberingOptions.value = loadedOptions
      customers.value = loadedCustomers
      myApprovalTasks.value = loadedTasks
      editLocks.value = loadedEditLocks
      organizationDirectory.value = loadedDirectory
      users.value = loadedDirectory.users
      if (hasPermission('settings.storage.manage')) {
        [systemSettings.value, equipmentTypes.value] = await Promise.all([getSystemSettings(accessToken), listEquipmentTypes(accessToken)])
      } else equipmentTypes.value = []
      crmIntegrationSettings.value = hasPermission('settings.customer.manage')
        ? await getCrmIntegrationSettings(accessToken)
        : { ...defaultCrmIntegrationSettings }
      folderTemplate.value = hasPermission('settings.folder.manage') ? await listFolderTemplate(accessToken) : []
      rolePermissionDirectory.value = hasPermission('system.role.view') ? await getRolePermissionDirectory(accessToken) : { permissions: [], roles: [] }
      const selectedProject = projects.value.find(candidate => candidate.id === projectId)
        ?? (project.value.id ? projects.value.find(candidate => candidate.id === project.value.id) : undefined)
      if (!selectedProject) {
        clearProjectWorkspace()
        ready.value = true
        serviceOnline.value = true
        return
      }

      const data = await loadProjectWorkspace(selectedProject.id, accessToken)
      project.value = data.project
      projectFolders.value = data.folders
      managedDocuments.value = data.documents
      documentRelations.value = data.documentRelations
      root.value = data.root
      hasDocuments.value = data.hasDocuments
      mechanicalBom.value = data.mechanicalBom
      electricalBom.value = data.electricalBom
      releasePackage.value = data.releasePackage
      selectedId.value = data.root.id
      ready.value = true
      serviceOnline.value = true
      if (pendingVersionComparison) {
        const request = pendingVersionComparison
        pendingVersionComparison = null
        selectDocument(request.documentId)
        await openVersionDrawer(request.leftVersionId, request.rightVersionId)
      }
    } catch (error) {
      ready.value = false
      if (error instanceof PdmApiError && error.status === 401) {
        clearSession()
        loginError.value = '登录已失效，请重新登录。'
      } else {
        loadError.value = messageFrom(error)
      }
    } finally {
      loading.value = false
    }
  }

  async function createProject(input: CreateProjectInput) {
    operationPending.value = true
    operationError.value = ''
    try {
      const created = await createProjectRequest(input, accessToken)
      await reload()
      return created
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function createSubproject(parentProjectId: string, input: CreateSubprojectInput) {
    operationPending.value = true
    operationError.value = ''
    try {
      const created = await createSubprojectRequest(parentProjectId, input, accessToken)
      await reload()
      return created
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function deleteProject(projectId: string) {
    operationPending.value = true
    operationError.value = ''
    try {
      await deleteProjectRequest(projectId, accessToken)
      if (project.value.id === projectId) clearProjectWorkspace()
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function updateOrganizationCounters(organizationId: string, currentProjectSequence: number, currentSerialSequence: number) {
    operationPending.value = true
    operationError.value = ''
    try {
      projectNumberingOptions.value = await updateOrganizationCountersRequest(organizationId, currentProjectSequence, currentSerialSequence, accessToken)
      return projectNumberingOptions.value
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function saveCrmIntegrationSettings(input: UpdateCrmIntegrationInput) {
    operationPending.value = true
    try {
      crmIntegrationSettings.value = await updateCrmIntegrationSettingsRequest(input, accessToken)
      return crmIntegrationSettings.value
    } finally {
      operationPending.value = false
    }
  }

  async function testCrmIntegration(): Promise<CrmConnectionTestResult> {
    operationPending.value = true
    try {
      return await testCrmIntegrationRequest(accessToken)
    } finally {
      operationPending.value = false
    }
  }

  async function syncCrmCustomers(): Promise<CrmCustomerSyncResult> {
    operationPending.value = true
    try {
      const result = await syncCrmCustomersRequest(accessToken)
      customers.value = result.customers
      crmIntegrationSettings.value = result.settings
      return result
    } finally {
      operationPending.value = false
    }
  }

  async function saveProjectOrganization(input: SaveProjectOrganizationInput) {
    operationPending.value = true
    try {
      const saved = await saveProjectOrganizationRequest(input, accessToken)
      await reload(project.value.id)
      return saved
    } finally { operationPending.value = false }
  }

  async function saveOrganizationUnit(input: SaveOrganizationUnitInput) {
    operationPending.value = true
    try {
      const saved = await saveOrganizationUnitRequest(input, accessToken)
      organizationDirectory.value = await getOrganizationDirectory(accessToken)
      users.value = organizationDirectory.value.users
      return saved
    } finally { operationPending.value = false }
  }

  async function updateOrganizationMemberships(username: string, unitIds: string[], primaryUnitId: string) {
    operationPending.value = true
    try {
      organizationDirectory.value = await updateOrganizationMembershipsRequest(username, unitIds, primaryUnitId, accessToken)
      users.value = organizationDirectory.value.users
      return organizationDirectory.value
    } finally { operationPending.value = false }
  }

  async function updateOrganizationUnitManagers(unitId: string, primaryManager: string, collaborativeManagers: string[]) {
    operationPending.value = true
    try {
      organizationDirectory.value = await updateOrganizationUnitManagersRequest(unitId, primaryManager, collaborativeManagers, accessToken)
      return organizationDirectory.value
    } finally { operationPending.value = false }
  }

  async function updateProjectExecutionUnit(projectId: string, executionUnitId: string) {
    operationPending.value = true
    try {
      const saved = await updateProjectExecutionUnitRequest(projectId, executionUnitId, accessToken)
      await reload(project.value.id)
      return saved
    } finally { operationPending.value = false }
  }

  async function updateMainProjectStaffing(projectId: string, input: MainProjectStaffingInput) {
    operationPending.value = true
    try {
      const saved = await updateMainProjectStaffingRequest(projectId, input, accessToken)
      await reload(project.value.id)
      return saved
    } finally { operationPending.value = false }
  }

  async function updateChildProjectDesigners(projectId: string, designers: string[]) {
    operationPending.value = true
    try {
      const saved = await updateChildProjectDesignersRequest(projectId, designers, accessToken)
      await reload(project.value.id)
      return saved
    } finally { operationPending.value = false }
  }

  async function saveSystemSettings(settings: PdmSystemSettings) {
    operationPending.value = true
    try {
      systemSettings.value = await updateSystemSettingsRequest(settings, accessToken)
      return systemSettings.value
    } finally {
      operationPending.value = false
    }
  }

  async function saveEquipmentType(input: EquipmentTypeDefinition) {
    operationPending.value = true
    try {
      const saved = await saveEquipmentTypeRequest(input, accessToken)
      const [loadedOptions, loadedEquipmentTypes] = await Promise.all([getProjectNumberingOptions(accessToken), listEquipmentTypes(accessToken)])
      projectNumberingOptions.value = loadedOptions
      equipmentTypes.value = loadedEquipmentTypes
      return saved
    } finally {
      operationPending.value = false
    }
  }

  async function updateProjectFolderPermissions(folderId: string, permissions: FolderPermissionRule[]) {
    if (!project.value.id) throw new Error('请先进入项目。')
    operationPending.value = true
    try {
      projectFolders.value = await updateProjectFolderPermissionsRequest(project.value.id, folderId, permissions, accessToken)
      return projectFolders.value
    } finally { operationPending.value = false }
  }

  async function saveFolderTemplate(nodes: ProjectFolderTemplateNode[]) {
    operationPending.value = true
    try {
      folderTemplate.value = await saveFolderTemplateRequest(nodes, accessToken)
      return folderTemplate.value
    } finally { operationPending.value = false }
  }

  async function updateRolePermissions(role: string, permissions: string[]) {
    operationPending.value = true
    try {
      rolePermissionDirectory.value = await updateRolePermissionsRequest(role, permissions, accessToken)
      if (role === currentRole.value) {
        currentPermissions.value = rolePermissionDirectory.value.roles.find(item => item.role === role)?.permissions ?? []
        const stored = restoreSession()
        if (stored) applySession({ ...stored, permissions: currentPermissions.value })
      }
      return rolePermissionDirectory.value
    } finally { operationPending.value = false }
  }

  async function selectProject(projectId: string) {
    await reload(projectId)
  }

  function closeProject() {
    clearProjectWorkspace()
  }

  async function login(username: string, password: string, rememberUsername = false) {
    loginPending.value = true
    loginError.value = ''
    try {
      const session = await apiLogin(username, password)
      postDesktopMessage(rememberUsername ? 'credentials-save' : 'credentials-clear', { username: username.trim() })
      applySession(session)
      await reload()
    } catch (error) {
      clearSession()
      loginError.value = error instanceof PdmApiError && error.status === 401
        ? '用户名或密码错误。'
        : messageFrom(error)
    } finally {
      loginPending.value = false
    }
  }

  function logout() {
    clearSession()
    loginError.value = ''
    loadError.value = ''
  }

  function restoreSession(): AuthSession | null {
    try {
      const value = window.sessionStorage.getItem(sessionKey)
      if (!value) return null
      const session = JSON.parse(value) as AuthSession
      if (!session.accessToken || new Date(session.expiresAt).getTime() <= Date.now()) return null
      return session
    } catch {
      return null
    }
  }

  onMounted(async () => {
    window.addEventListener('pdm-open-version-compare', async (event) => {
      const detail = (event as CustomEvent<{ documentId?: string; leftVersionId?: string; rightVersionId?: string }>).detail
      if (!detail?.documentId) return
      pendingVersionComparison = { documentId: detail.documentId, leftVersionId: detail.leftVersionId, rightVersionId: detail.rightVersionId }
      if (accessToken && ready.value) {
        const request = pendingVersionComparison
        pendingVersionComparison = null
        selectedId.value = request.documentId
        await openVersionDrawer(request.leftVersionId, request.rightVersionId)
      }
    })

    const controller = new AbortController()
    const timer = window.setTimeout(() => controller.abort(), 1600)
    serviceOnline.value = await checkHealth(controller.signal)
    window.clearTimeout(timer)

    window.chrome?.webview?.addEventListener('message', (event) => {
      const message = event.data as { type?: string; payload?: { online?: boolean } }
      if (message.type === 'service-status') serviceOnline.value = Boolean(message.payload?.online)
    })

    const session = restoreSession()
    if (session) {
      applySession(session)
      await reload()
    }
  })

  return {
    projects,
    projectNumberingOptions,
    customers,
    crmIntegrationSettings,
    users,
    organizationDirectory,
    rolePermissionDirectory,
    systemSettings,
    equipmentTypes,
    project,
    projectFolders,
    managedDocuments,
    documentRelations,
    folderTemplate,
    root,
    releasePackage,
    mechanicalBom,
    electricalBom,
    selectedNode,
    filteredTree,
    filteredDrawings,
    documentFilter,
    documentFilterCounts,
    relatedNodes,
    searchQuery,
    serviceOnline,
    authenticated,
    currentUser,
    currentUsername,
    currentRole,
    currentPermissions,
    hasPermission,
    loginPending,
    loginError,
    loading,
    loadError,
    ready,
    hasDocuments,
    normalCount,
    warningCount,
    versionDrawerOpen,
    whereUsedDrawerOpen,
    whereUsed,
    whereUsedLoading,
    whereUsedError,
    versions,
    leftVersionId,
    rightVersionId,
    versionComparison,
    versionLoading,
    versionError,
    approvalDialogOpen,
    operationPending,
    operationError,
    uploadProgress,
    auditEntries,
    myApprovalTasks,
    editLocks,
    projectVersions,
    projectAuditEntries,
    storageStatus,
    createProject,
    createSubproject,
    deleteProject,
    updateOrganizationCounters,
    saveCrmIntegrationSettings,
    testCrmIntegration,
    syncCrmCustomers,
    saveProjectOrganization,
    saveOrganizationUnit,
    updateOrganizationMemberships,
    updateOrganizationUnitManagers,
    updateProjectExecutionUnit,
    updateMainProjectStaffing,
    updateChildProjectDesigners,
    saveSystemSettings,
    saveEquipmentType,
    updateProjectFolderPermissions,
    saveFolderTemplate,
    updateRolePermissions,
    selectProject,
    closeProject,
    selectNode,
    setDocumentFilter,
    selectRelatedNode,
    selectDocument,
    openDocument,
    previewDocument,
    openVersionDrawer,
    compareVersions,
    openVersionFile,
    restoreVersion,
    submitApproval,
    saveBomItems,
    importBomFile,
    exportBomFile,
    createPackage,
    uploadPackageFile,
    submitPackage,
    withdrawPackage,
    openWhereUsed,
    obsoleteSelectedDocument,
    decideApprovalTask,
    loadAuditEntries,
    loadMyApprovalTasks,
    requestEditLockRelease,
    forceReleaseEditLock,
    loadProjectVersions,
    loadProjectAuditEntries,
    loadStorageStatus,
    login,
    logout,
    reload,
  }
}
