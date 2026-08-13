import { computed, onMounted, ref } from 'vue'
import { checkHealth, compareDocumentVersions, createProject as createProjectRequest, createSubproject as createSubprojectRequest, createReleasePackage, decideApproval, exportBom, getProjectNumberingOptions, getStorageStatus, getSystemSettings, importBom, listAudit, listCustomers, listDocumentVersions, listEquipmentTypes, listProjects, listUsers, loadProjectWorkspace, login as apiLogin, PdmApiError, postDesktopMessage, readDocumentVersionFile, restoreDocumentVersion, saveBom, saveCustomer as saveCustomerRequest, saveEquipmentType as saveEquipmentTypeRequest, submitReleasePackage, updateOrganizationCounters as updateOrganizationCountersRequest, updateProjectResponsibles as updateProjectResponsiblesRequest, updateSystemSettings as updateSystemSettingsRequest, uploadReleaseFile } from '../api'
import type { AuthSession } from '../api'
import type { AuditEntry, BomItem, CreateProjectInput, CreateSubprojectInput, DocumentNode, DocumentVersionComparison, DocumentVersionSummary, EquipmentTypeDefinition, PdmCustomer, PdmSystemSettings, PdmUser, PreviewMode, ProjectNumberingOptions, ProjectSummary, ReleasePackageSummary, SolidWorksOpenMode } from '../types'

const sessionKey = 'upton-pdm-session'

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

function filterNode(node: DocumentNode, query: string): DocumentNode | undefined {
  const normalized = query.trim().toLocaleLowerCase('zh-CN')
  if (!normalized) return node
  const children = node.children.map((child) => filterNode(child, query)).filter((child): child is DocumentNode => Boolean(child))
  const ownText = `${node.drawingNumber} ${node.name} ${node.fileName}`.toLocaleLowerCase('zh-CN')
  if (ownText.includes(normalized) || children.length > 0) return { ...node, children }
  return undefined
}

function countNodes(root: DocumentNode): { normal: number; warning: number } {
  const hasWarning = root.status === 'Missing' || root.status === 'Unregistered'
  let normal = hasWarning ? 0 : 1
  let warning = hasWarning ? 1 : 0
  for (const child of root.children) {
    const counts = countNodes(child)
    normal += counts.normal
    warning += counts.warning
  }
  return { normal, warning }
}

function messageFrom(error: unknown): string {
  return error instanceof Error ? error.message : 'PDM数据加载失败。'
}

export function usePdmWorkspace() {
  const projects = ref<ProjectSummary[]>([])
  const projectNumberingOptions = ref<ProjectNumberingOptions>({ organizations: [], projectTypes: [], equipmentTypes: [] })
  const customers = ref<PdmCustomer[]>([])
  const users = ref<PdmUser[]>([])
  const systemSettings = ref<PdmSystemSettings>({ vaultRoot: '', releaseRoot: '' })
  const equipmentTypes = ref<EquipmentTypeDefinition[]>([])
  const project = ref<ProjectSummary>(emptyProject)
  const root = ref<DocumentNode>(emptyRoot)
  const mechanicalBom = ref<BomItem[]>([])
  const electricalBom = ref<BomItem[]>([])
  const releasePackage = ref<ReleasePackageSummary | null>(null)
  const selectedId = ref('')
  const searchQuery = ref('')
  const previewMode = ref<PreviewMode>('model')
  const serviceOnline = ref(false)
  const authenticated = ref(false)
  const currentUser = ref('')
  const currentUsername = ref('')
  const currentRole = ref('')
  const loginPending = ref(false)
  const loginError = ref('')
  const loading = ref(false)
  const loadError = ref('')
  const ready = ref(false)
  const hasDocuments = ref(false)
  const versionDrawerOpen = ref(false)
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
  const storageStatus = ref<{ vaultAvailable: boolean; releaseAvailable: boolean } | null>(null)
  let accessToken = ''
  let pendingVersionComparison: { documentId: string; leftVersionId?: string; rightVersionId?: string } | null = null

  const selectedNode = computed(() => findNode(root.value, selectedId.value) ?? root.value)
  const selectedDocumentId = computed(() => selectedNode.value.documentId)
  const filteredTree = computed(() => filterNode(root.value, searchQuery.value) ?? root.value)
  const nodeCounts = computed(() => ready.value && hasDocuments.value ? countNodes(root.value) : { normal: 0, warning: 0 })
  const normalCount = computed(() => nodeCounts.value.normal)
  const warningCount = computed(() => nodeCounts.value.warning)

  function selectNode(node: DocumentNode) {
    selectedId.value = node.id
    if (node.documentId) postDesktopMessage('document-selected', { documentId: node.documentId, fileName: node.fileName })
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
    window.sessionStorage.setItem(sessionKey, JSON.stringify(session))
  }

  function clearSession() {
    accessToken = ''
    postDesktopMessage('session-clear')
    authenticated.value = false
    currentUser.value = ''
    currentUsername.value = ''
    currentRole.value = ''
    ready.value = false
    projects.value = []
    projectNumberingOptions.value = { organizations: [], projectTypes: [], equipmentTypes: [] }
    customers.value = []
    users.value = []
    systemSettings.value = { vaultRoot: '', releaseRoot: '' }
    equipmentTypes.value = []
    project.value = emptyProject
    root.value = emptyRoot
    hasDocuments.value = false
    mechanicalBom.value = []
    electricalBom.value = []
    releasePackage.value = null
    window.sessionStorage.removeItem(sessionKey)
  }

  async function reload(projectId?: string) {
    if (!accessToken) return
    loading.value = true
    loadError.value = ''
    try {
      const [loadedProjects, loadedOptions, loadedCustomers] = await Promise.all([
        listProjects(accessToken),
        getProjectNumberingOptions(accessToken),
        listCustomers(accessToken),
      ])
      projects.value = loadedProjects
      projectNumberingOptions.value = loadedOptions
      customers.value = loadedCustomers
      if (currentRole.value === 'Administrator') {
        const [loadedUsers, loadedSettings, loadedEquipmentTypes] = await Promise.all([
          listUsers(accessToken), getSystemSettings(accessToken), listEquipmentTypes(accessToken),
        ])
        users.value = loadedUsers
        systemSettings.value = loadedSettings
        equipmentTypes.value = loadedEquipmentTypes
      } else {
        users.value = []
        equipmentTypes.value = []
      }
      const selectedProject = projects.value.find(candidate => candidate.id === projectId)
        ?? projects.value.find(candidate => candidate.id === project.value.id)
        ?? projects.value.find(candidate => candidate.stage === '进行中')
        ?? projects.value[0]
      if (!selectedProject) {
        project.value = emptyProject
        root.value = emptyRoot
        hasDocuments.value = false
        mechanicalBom.value = []
        electricalBom.value = []
        releasePackage.value = null
        ready.value = true
        serviceOnline.value = true
        return
      }

      const data = await loadProjectWorkspace(selectedProject.id, accessToken)
      project.value = data.project
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
        selectedId.value = request.documentId
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
      await reload(created.id)
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
      await reload(created.id)
      return created
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

  async function saveCustomer(customer: Partial<PdmCustomer> & Pick<PdmCustomer, 'code' | 'name' | 'isActive'>) {
    operationPending.value = true
    try {
      const saved = await saveCustomerRequest(customer, accessToken)
      await reload(project.value.id)
      return saved
    } finally {
      operationPending.value = false
    }
  }

  async function updateProjectResponsibles(projectId: string, usernames: string[]) {
    operationPending.value = true
    try {
      const saved = await updateProjectResponsiblesRequest(projectId, usernames, accessToken)
      await reload(project.value.id || projectId)
      return saved
    } finally {
      operationPending.value = false
    }
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

  async function selectProject(projectId: string) {
    await reload(projectId)
  }

  async function login(username: string, password: string, rememberCredentials = false) {
    loginPending.value = true
    loginError.value = ''
    try {
      const session = await apiLogin(username, password)
      postDesktopMessage(rememberCredentials ? 'credentials-save' : 'credentials-clear', { username: username.trim(), password })
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
    users,
    systemSettings,
    equipmentTypes,
    project,
    root,
    releasePackage,
    mechanicalBom,
    electricalBom,
    selectedNode,
    filteredTree,
    searchQuery,
    previewMode,
    serviceOnline,
    authenticated,
    currentUser,
    currentUsername,
    currentRole,
    loginPending,
    loginError,
    loading,
    loadError,
    ready,
    hasDocuments,
    normalCount,
    warningCount,
    versionDrawerOpen,
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
    storageStatus,
    createProject,
    createSubproject,
    updateOrganizationCounters,
    saveCustomer,
    updateProjectResponsibles,
    saveSystemSettings,
    saveEquipmentType,
    selectProject,
    selectNode,
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
    decideApprovalTask,
    loadAuditEntries,
    loadStorageStatus,
    login,
    logout,
    reload,
  }
}
