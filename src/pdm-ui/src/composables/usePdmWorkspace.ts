import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { batchDeleteBomItems as batchDeleteBomItemsRequest, batchRestoreBomItems as batchRestoreBomItemsRequest, restoreBomItemsFromSource as restoreBomItemsFromSourceRequest } from '../api'
import { getBomSourceData } from '../api'
import { batchUpdateBomItems as batchUpdateBomItemsRequest, changeMyPassword as changeMyPasswordRequest, checkHealth, compareDocumentVersions, createProject as createProjectRequest, createRole as createRoleRequest, createSubproject as createSubprojectRequest, createReleasePackage, createUser as createUserRequest, decideApproval, deleteProject as deleteProjectRequest, deleteRole as deleteRoleRequest, exportBom, forceReleaseEditLock as forceReleaseEditLockRequest, generateMechanicalBom, getBomValidationRules, getCrmIntegrationSettings, getMyProfile, getOrganizationDirectory, getProjectNumberingOptions, getRolePermissionDirectory, getStorageStatus, getSystemSettings, importBom, listAudit, listBomBaselines, listBomVersions, listCustomers, listDocumentVersions, listDocumentWhereUsed, listEditLocks, listEquipmentTypes, listFolderTemplate, listMyApprovalTasks, listPasswordResetTasks, listProjectAudit, listProjects, listProjectVersions, loadProjectDocumentWorkspace, loadProjectWorkspace, login as apiLogin, obsoleteDocument as obsoleteDocumentRequest, PdmApiError, postDesktopMessage, readDocumentVersionFile, requestEditLockRelease as requestEditLockReleaseRequest, resetRequestedPassword as resetRequestedPasswordRequest, resetUserPassword as resetUserPasswordRequest, resolveBomItem as resolveBomItemRequest, restoreDocumentVersion, resumeSession as apiResumeSession, saveBom, saveEquipmentType as saveEquipmentTypeRequest, saveFolderTemplate as saveFolderTemplateRequest, saveOrganizationUnit as saveOrganizationUnitRequest, saveProjectOrganization as saveProjectOrganizationRequest, setBomEmptyDeclaration as setBomEmptyDeclarationRequest, submitReleasePackage, syncCrmCustomers as syncCrmCustomersRequest, testCrmIntegration as testCrmIntegrationRequest, updateChildProjectDesigners as updateChildProjectDesignersRequest, updateCrmIntegrationSettings as updateCrmIntegrationSettingsRequest, updateMainProjectStaffing as updateMainProjectStaffingRequest, updateMyProfile as updateMyProfileRequest, updateOrganizationCounters as updateOrganizationCountersRequest, updateOrganizationMemberships as updateOrganizationMembershipsRequest, updateOrganizationUnitManagers as updateOrganizationUnitManagersRequest, updateProject as updateProjectRequest, updateProjectExecutionUnit as updateProjectExecutionUnitRequest, updateProjectFolderPermissions as updateProjectFolderPermissionsRequest, updateRolePermissions as updateRolePermissionsRequest, updateSystemSettings as updateSystemSettingsRequest, updateUser as updateUserRequest, uploadReleaseFile, withdrawReleasePackage } from '../api'
import type { AuthSession } from '../api'
import type { AuditEntry, BatchUpdateBomItemsInput, BomEmptyDeclaration, BomGenerationResult, BomItem, BomKind, BomVersion, CreateProjectInput, CreateRoleInput, CreateSubprojectInput, CrmConnectionTestResult, CrmCustomerSyncResult, CrmIntegrationSettings, DocumentFilter, DocumentModelDrawingRelation, DocumentNode, DocumentVersionComparison, DocumentVersionSummary, DocumentWhereUsed, EditLockSummary, EquipmentTypeDefinition, FolderPermissionRule, MainProjectStaffingInput, ManagedDocument, ManufacturingBomBaseline, MyApprovalTask, OrganizationDirectory, PasswordResetTask, PdmCustomer, PdmSystemSettings, PdmUser, PdmUserProfile, ProjectFolder, ProjectFolderTemplateNode, ProjectNumberingOptions, ProjectSummary, ProjectVersionItem, ReleasePackageSummary, RolePermissionDirectory, SaveOrganizationUnitInput, SavePdmUserInput, SaveProjectOrganizationInput, SolidWorksOpenMode, UpdateCrmIntegrationInput, UpdateProjectInput } from '../types'

const sessionKey = 'upton-pdm-session'
const fallbackBomPropertyMappings: PdmSystemSettings['bomPropertyMappings'] = [
  { pdmPropertyKey: 'kind', pdmPropertyName: '物料分类', solidWorksProperty: '物料分类', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'wearPart', pdmPropertyName: '易损件', solidWorksProperty: '易损件', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'unit', pdmPropertyName: '单位', solidWorksProperty: '单位', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'drawingNumber', pdmPropertyName: '物料编码', solidWorksProperty: '物料编码', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'name', pdmPropertyName: '物料名称', solidWorksProperty: '物料名称', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'specification', pdmPropertyName: '型号', solidWorksProperty: '型号', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'remark', pdmPropertyName: '备注信息', solidWorksProperty: '备注信息', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'brand', pdmPropertyName: '品牌', solidWorksProperty: '品牌', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'material', pdmPropertyName: '材质', solidWorksProperty: '材质', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'surfaceTreatment', pdmPropertyName: '表面处理', solidWorksProperty: '表面处理', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'weight', pdmPropertyName: '重量', solidWorksProperty: '重量', source: 'SolidWorks', mappingEditable: true },
  { pdmPropertyKey: 'quantity', pdmPropertyName: '数量', solidWorksProperty: '', source: 'Assembly', mappingEditable: false },
  { pdmPropertyKey: 'revision', pdmPropertyName: '版本', solidWorksProperty: '', source: 'Pdm', mappingEditable: false },
]
function bomPropertyMappingsFromLegacy(settings: PdmSystemSettings) {
  const values: Record<string, string> = {
    kind: '物料分类',
    wearPart: '易损件',
    unit: settings.bomUnitProperty,
    drawingNumber: settings.bomDrawingNumberProperty,
    name: settings.bomNameProperty,
    specification: settings.bomSpecificationProperty,
    remark: settings.bomDescriptionProperty,
    brand: settings.bomBrandProperty,
    material: settings.bomMaterialProperty,
    surfaceTreatment: settings.bomSurfaceTreatmentProperty,
    weight: settings.bomWeightProperty,
  }
  return fallbackBomPropertyMappings.map(mapping => ({
    ...mapping,
    solidWorksProperty: mapping.mappingEditable ? values[mapping.pdmPropertyKey] ?? mapping.solidWorksProperty : mapping.solidWorksProperty,
  }))
}
const defaultSystemSettings: PdmSystemSettings = { vaultRoot: '', releaseRoot: '', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48, bomDrawingNumberProperty: '物料编码', bomNameProperty: '物料名称', bomDescriptionProperty: '备注信息', bomMaterialProperty: '材质', bomSpecificationProperty: '型号', bomUnitProperty: '单位', bomBrandProperty: '品牌', bomSurfaceTreatmentProperty: '表面处理', bomWeightProperty: '重量', bomPropertyMappings: fallbackBomPropertyMappings.map(mapping => ({ ...mapping })), validationRules: { standard: ['drawingNumber', 'name', 'unit', 'specification', 'quantity', 'revision'], nonStandard: ['drawingNumber', 'name', 'unit', 'material', 'quantity', 'revision'], electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'], } }
const defaultCrmIntegrationSettings: CrmIntegrationSettings = { baseUrl: '', username: '', passwordConfigured: false, autoSyncEnabled: false, autoSyncIntervalMinutes: 60, lastSyncAt: null, lastSyncCount: 0, lastAutoSyncAttemptAt: null, lastAutoSyncError: null }

export function formatBomGenerationConfirmation(preview: BomGenerationResult): string {
  return [
    '将按最新设计树更新机械BOM：',
    `• 标准件：${preview.standardItems.length} 条`,
    `• 非标件：${preview.nonStandardItems.length} 条`,
    `• 待分类：${preview.unclassifiedCount} 条`,
    `• 待移除：${preview.pendingRemovalCount} 条`,
    `• 人工待确认：${preview.manualUnmatchedCount} 条`,
    `• 虚拟件排除：${preview.virtualCount} 条`,
    '',
    '待处理项不会静默删除，并会阻止发布。是否应用本次更新？',
  ].join('\n')
}

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
    || node.status === 'Unarchived'
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
    version: document.storedVersionCount === 0 ? '—' : document.revision,
    checkedOutBy: document.checkedOutBy,
    lifecycleState: document.state,
    status: document.storedVersionCount === 0 ? 'Unarchived' : 'Normal',
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
  const standardBom = ref<BomItem[]>([])
  const nonStandardBom = ref<BomItem[]>([])
  const unclassifiedBom = ref<BomItem[]>([])
  const bomSourceData = ref<BomItem[]>([])
  const mechanicalBom = computed(() => [...standardBom.value, ...nonStandardBom.value].filter(item => !item.manuallyExcluded))
  const electricalBom = ref<BomItem[]>([])
  const bomEmptyDeclarations = ref<BomEmptyDeclaration[]>([])
  const bomVersions = ref<BomVersion[]>([])
  const bomBaselines = ref<ManufacturingBomBaseline[]>([])
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
  const currentProfile = ref<PdmUserProfile | null>(null)
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
  const passwordResetTasks = ref<PasswordResetTask[]>([])
  const projectVersions = ref<ProjectVersionItem[]>([])
  const projectAuditEntries = ref<AuditEntry[]>([])
  const storageStatus = ref<{ vaultAvailable: boolean; releaseAvailable: boolean } | null>(null)
  let accessToken = ''
  let pendingVersionComparison: { documentId: string; leftVersionId?: string; rightVersionId?: string } | null = null
  let projectSummaryTimer: number | undefined
  let projectDocumentTimer: number | undefined
  let sessionRenewTimer: number | undefined
  let persistentSession: AuthSession | null = null
  let sessionRenewal: Promise<boolean> | null = null
  let projectDocumentRefreshPending = false

  const managedDocumentNodes = computed(() => managedDocuments.value.map(managedDocumentNode))
  const currentProjectManagedDocuments = computed(() => managedDocuments.value.filter(document => document.projectId === project.value.id))
  const drawingNodes = computed(() => currentProjectManagedDocuments.value.map(managedDocumentNode).filter(node => node.kind === 'Drawing'))
  const selectedNode = computed(() => findNode(root.value, selectedId.value)
    ?? managedDocumentNodes.value.find(node => node.id === selectedId.value)
    ?? root.value)
  const allBomItems = computed(() => [...standardBom.value, ...nonStandardBom.value, ...unclassifiedBom.value, ...electricalBom.value])
  function bomItemForNode(node: DocumentNode) {
    const items = allBomItems.value.filter(item => !item.manuallyExcluded)
    const documentId = node.documentId
    const configuration = node.configuration?.trim()
    return items.find(item => documentId && item.sourceDocumentId === documentId && (!item.sourceConfiguration || item.sourceConfiguration === configuration))
      ?? items.find(item => documentId && item.sourceDocumentId === documentId)
      ?? items.find(item => item.drawingNumber.localeCompare(node.drawingNumber, undefined, { sensitivity: 'accent' }) === 0)
  }
  const selectedBomItem = computed(() => bomItemForNode(selectedNode.value))
  const selectedDocumentId = computed(() => selectedNode.value.documentId)
  const filteredTree = computed<DocumentNode | undefined>(() => documentFilter.value === 'drawing'
    ? undefined
    : filterNode(root.value, searchQuery.value, documentFilter.value))
  const filteredDrawings = computed(() => drawingNodes.value.filter(node => matchesQuery(node, searchQuery.value)))
  const documentFilterCounts = computed(() => {
    const unresolvedIssues = countUniqueIssues(root.value)
    const model = currentProjectManagedDocuments.value.filter(document => document.kind !== 'Drawing').length
    const drawing = currentProjectManagedDocuments.value.filter(document => document.kind === 'Drawing').length
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
    standardBom.value = []
    nonStandardBom.value = []
    unclassifiedBom.value = []
    bomSourceData.value = []
    electricalBom.value = []
    bomEmptyDeclarations.value = []
    bomVersions.value = []
    bomBaselines.value = []
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
    const bomItem = bomItemForNode(node)
    const lifecycleState = typeof node.lifecycleState === 'number'
      ? ['工作中', '审批中', '已发布', '已作废'][node.lifecycleState] ?? String(node.lifecycleState)
      : ({ Work: '工作中', InReview: '审批中', Released: '已发布', Obsolete: '已作废' } as Record<string, string>)[node.lifecycleState ?? ''] ?? node.lifecycleState ?? '工作中'
    postDesktopMessage('preview-document', {
      documentId: node.documentId,
      fileName: node.fileName,
      revision: node.version,
      drawingNumber: node.drawingNumber,
      name: node.name,
      specification: bomItem?.specification ?? '',
      material: bomItem?.material ?? '',
      brand: bomItem?.brand ?? '',
      surfaceTreatment: bomItem?.surfaceTreatment ?? '',
      status: lifecycleState,
    })
  }

  function submitApproval() {
    if (releasePackage.value) approvalDialogOpen.value = true
  }

  async function saveBomItems(kind: BomKind, items: BomItem[]) {
    operationPending.value = true
    operationError.value = ''
    try {
      const saved = await saveBom(project.value.id, kind, items, accessToken)
      if (kind === 'Standard') standardBom.value = saved
      else if (kind === 'NonStandard') nonStandardBom.value = saved
      else electricalBom.value = saved
      bomEmptyDeclarations.value = bomEmptyDeclarations.value.map(item => item.kind === kind ? { ...item, declaredEmpty: false } : item)
      await refreshBomVersionData()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function importBomFile(kind: BomKind, file: File) {
    operationPending.value = true
    operationError.value = ''
    try {
      const imported = await importBom(project.value.id, kind, file, accessToken)
      if (kind === 'Standard') standardBom.value = imported
      else if (kind === 'NonStandard') nonStandardBom.value = imported
      else electricalBom.value = imported
      await refreshBomVersionData()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function exportBomFile(kind: BomKind) {
    const blob = await exportBom(project.value.id, kind, accessToken)
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `${kind.toLocaleLowerCase()}-bom.xlsx`
    anchor.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
  }

  async function generateBomFromDrawings(): Promise<BomGenerationResult> {
    operationPending.value = true
    try {
      const preview = await generateMechanicalBom(project.value.id, false, accessToken)
      const confirmed = window.confirm(formatBomGenerationConfirmation(preview))
      if (!confirmed) return preview
      const result = await generateMechanicalBom(project.value.id, true, accessToken)
      standardBom.value = result.standardItems
      nonStandardBom.value = result.nonStandardItems
      electricalBom.value = result.electricalItems
      unclassifiedBom.value = result.unclassifiedItems
      bomSourceData.value = await getBomSourceData(project.value.id, accessToken)
      await refreshBomVersionData()
      return result
    } finally {
      operationPending.value = false
    }
  }

  async function resolveBomItem(itemId: string, action: 'classify' | 'retain' | 'remove', targetKind?: BomKind) {
    operationPending.value = true
    try {
      await resolveBomItemRequest(project.value.id, itemId, action, targetKind, accessToken)
      await reload()
    } finally {
      operationPending.value = false
    }
  }

  async function batchUpdateBomItems(input: BatchUpdateBomItemsInput) {
    operationPending.value = true
    operationError.value = ''
    try {
      await batchUpdateBomItemsRequest(project.value.id, input, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function batchDeleteBomItems(itemIds: string[], reason: string) {
    operationPending.value = true
    operationError.value = ''
    try {
      await batchDeleteBomItemsRequest(project.value.id, itemIds, reason, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function batchRestoreBomItems(itemIds: string[], mode: 'Original' | 'AsManual') {
    operationPending.value = true
    operationError.value = ''
    try {
      await batchRestoreBomItemsRequest(project.value.id, itemIds, mode, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function restoreBomItemsFromSource(itemIds: string[]) {
    operationPending.value = true
    operationError.value = ''
    try {
      await restoreBomItemsFromSourceRequest(project.value.id, itemIds, accessToken)
      await reload()
    } catch (error) {
      operationError.value = messageFrom(error)
      throw error
    } finally {
      operationPending.value = false
    }
  }

  async function setBomCategoryEmpty(kind: BomKind, declaredEmpty: boolean) {
    operationPending.value = true
    try {
      const saved = await setBomEmptyDeclarationRequest(project.value.id, kind, declaredEmpty, accessToken)
      bomEmptyDeclarations.value = [...bomEmptyDeclarations.value.filter(item => item.kind !== kind), saved]
    } finally {
      operationPending.value = false
    }
  }

  async function refreshBomVersionData() {
    if (!project.value.id) return
    const [loadedVersions, loadedBaselines] = await Promise.all([
      listBomVersions(project.value.id, accessToken),
      listBomBaselines(project.value.id, accessToken),
    ])
    bomVersions.value = loadedVersions
    bomBaselines.value = loadedBaselines
  }

  async function createPackage(
    number: string,
    changeNumber: string,
    changeReason: string,
    effectiveSerialFrom: string,
    effectiveSerialTo: string,
    processReviewer: string,
    approver: string,
  ) {
    operationPending.value = true
    operationError.value = ''
    try {
      await createReleasePackage(
        project.value.id,
        number,
        changeNumber,
        changeReason,
        effectiveSerialFrom,
        effectiveSerialTo,
        processReviewer,
        approver,
        accessToken,
      )
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
    [myApprovalTasks.value, editLocks.value, passwordResetTasks.value] = await Promise.all([requestMyApprovalTasks(), requestEditLocks(), requestPasswordResetTasks()])
  }

  async function requestPasswordResetTasks() {
    return currentRole.value === 'Administrator' ? listPasswordResetTasks(accessToken) : []
  }

  async function resetRequestedPassword(taskId: string) {
    operationPending.value = true
    try {
      await resetRequestedPasswordRequest(taskId, accessToken)
      passwordResetTasks.value = await requestPasswordResetTasks()
    } finally { operationPending.value = false }
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
    persistentSession = session
    accessToken = session.accessToken
    postDesktopMessage('session-ready', { accessToken: session.accessToken })
    authenticated.value = true
    currentUser.value = session.displayName || session.username
    currentUsername.value = session.username
    currentRole.value = session.role
    currentPermissions.value = session.permissions ?? []
    if (currentProfile.value?.username !== session.username) {
      currentProfile.value = { username: session.username, displayName: session.displayName || session.username, gender: 'unspecified' }
    }
    window.localStorage.setItem(sessionKey, JSON.stringify(session))
    window.sessionStorage.removeItem(sessionKey)
    scheduleSessionRenewal(session)
  }

  function clearSession() {
    accessToken = ''
    postDesktopMessage('session-clear')
    authenticated.value = false
    currentUser.value = ''
    currentUsername.value = ''
    currentRole.value = ''
    currentPermissions.value = []
    currentProfile.value = null
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
    passwordResetTasks.value = []
    persistentSession = null
    if (sessionRenewTimer !== undefined) window.clearTimeout(sessionRenewTimer)
    sessionRenewTimer = undefined
    window.localStorage.removeItem(sessionKey)
    window.sessionStorage.removeItem(sessionKey)
  }

  function scheduleSessionRenewal(session: AuthSession) {
    if (sessionRenewTimer !== undefined) window.clearTimeout(sessionRenewTimer)
    if (!session.resumeToken) return
    const delay = Math.max(5_000, new Date(session.expiresAt).getTime() - Date.now() - 5 * 60_000)
    sessionRenewTimer = window.setTimeout(() => void renewPersistentSession(), Math.min(delay, 2_147_000_000))
  }

  async function renewPersistentSession(): Promise<boolean> {
    if (sessionRenewal) return sessionRenewal
    if (!persistentSession?.resumeToken) return false
    const wasAuthenticated = authenticated.value
    sessionRenewal = (async () => {
      try {
        const session = await apiResumeSession(persistentSession!.resumeToken)
        applySession(session)
        if (!wasAuthenticated) await reload()
        return true
      } catch (error) {
        if (error instanceof PdmApiError && error.status === 401) {
          clearSession()
          loginError.value = '登录已失效，请重新登录。'
        } else if (persistentSession) {
          sessionRenewTimer = window.setTimeout(() => void renewPersistentSession(), 60_000)
        }
        return false
      } finally {
        sessionRenewal = null
      }
    })()
    return sessionRenewal
  }

  async function reload(projectId?: string) {
    if (!accessToken) return
    loading.value = true
    loadError.value = ''
    try {
      const [loadedProjects, loadedOptions, loadedCustomers, loadedTasks, loadedEditLocks, loadedDirectory, loadedProfile, loadedPasswordResetTasks, loadedValidationRules] = await Promise.all([
        listProjects(accessToken),
        getProjectNumberingOptions(accessToken),
        listCustomers(accessToken),
        requestMyApprovalTasks(),
        requestEditLocks(),
        getOrganizationDirectory(accessToken),
        getMyProfile(accessToken),
        requestPasswordResetTasks(),
        getBomValidationRules(accessToken).catch(error => {
          if (error instanceof PdmApiError && error.status !== 404) throw error
          return defaultSystemSettings.validationRules
        }),
      ])
      projects.value = loadedProjects
      projectNumberingOptions.value = loadedOptions
      customers.value = loadedCustomers
      myApprovalTasks.value = loadedTasks
      editLocks.value = loadedEditLocks
      organizationDirectory.value = loadedDirectory
      users.value = loadedDirectory.users
      currentProfile.value = loadedProfile
      passwordResetTasks.value = loadedPasswordResetTasks
      systemSettings.value = { ...systemSettings.value, validationRules: loadedValidationRules }
      if (hasPermission('settings.storage.manage')) {
        const [loadedSettings, loadedEquipmentTypes] = await Promise.all([getSystemSettings(accessToken), listEquipmentTypes(accessToken)])
        const mergedSettings = { ...systemSettings.value, ...loadedSettings }
        systemSettings.value = {
          ...systemSettings.value,
          ...loadedSettings,
          bomPropertyMappings: loadedSettings.bomPropertyMappings?.length ? loadedSettings.bomPropertyMappings : bomPropertyMappingsFromLegacy(mergedSettings),
          validationRules: loadedSettings.validationRules ?? loadedValidationRules,
        }
        equipmentTypes.value = loadedEquipmentTypes
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

      const previousSelectedId = selectedProject.id === project.value.id ? selectedId.value : ''
      const data = await loadProjectWorkspace(selectedProject.id, accessToken)
      project.value = data.project
      projectFolders.value = data.folders
      managedDocuments.value = data.documents
      documentRelations.value = data.documentRelations
      root.value = data.root
      hasDocuments.value = data.hasDocuments
      standardBom.value = data.standardBom
      nonStandardBom.value = data.nonStandardBom
      unclassifiedBom.value = data.unclassifiedBom
      electricalBom.value = data.electricalBom
      bomSourceData.value = data.bomSourceData
      bomEmptyDeclarations.value = data.bomEmptyDeclarations
      bomVersions.value = data.bomVersions
      bomBaselines.value = data.bomBaselines
      releasePackage.value = data.releasePackage
      const selectedStillExists = previousSelectedId
        && (findNode(data.root, previousSelectedId)
          || data.documents.some(document => `document-${document.id}` === previousSelectedId))
      selectedId.value = selectedStillExists ? previousSelectedId : data.root.id
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

  async function refreshProjectSummaries() {
    if (!accessToken || loading.value) return
    try {
      const loadedProjects = await listProjects(accessToken)
      projects.value = loadedProjects
      const current = loadedProjects.find(candidate => candidate.id === project.value.id)
      if (current) {
        project.value = {
          ...project.value,
          documentCount: current.documentCount,
          businessStatus: current.businessStatus,
          rootDocumentCheckedOutBy: current.rootDocumentCheckedOutBy,
        }
      }
    } catch (error) {
      if (error instanceof PdmApiError && error.status === 401) handleSessionExpired()
    }
  }

  async function refreshOpenProjectDocuments() {
    if (!accessToken || !ready.value || !project.value.id || loading.value || operationPending.value || projectDocumentRefreshPending) return
    const projectId = project.value.id
    projectDocumentRefreshPending = true
    try {
      const data = await loadProjectDocumentWorkspace(projectId, accessToken)
      if (project.value.id !== projectId) return
      const previousSelectedId = selectedId.value
      managedDocuments.value = data.documents
      documentRelations.value = data.documentRelations
      root.value = data.root
      hasDocuments.value = data.hasDocuments
      const selectedStillExists = previousSelectedId
        && (findNode(data.root, previousSelectedId)
          || data.documents.some(document => `document-${document.id}` === previousSelectedId))
      if (!selectedStillExists) selectedId.value = data.root.id
    } catch (error) {
      if (error instanceof PdmApiError && error.status === 401) handleSessionExpired()
    } finally {
      projectDocumentRefreshPending = false
    }
  }

  function refreshOpenProjectOnFocus() {
    if (!authenticated.value || !project.value.id || loading.value) return
    void reload(project.value.id)
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

  async function saveUser(input: SavePdmUserInput, creating: boolean) {
    operationPending.value = true
    try {
      const saved = creating ? await createUserRequest(input, accessToken) : await updateUserRequest(input, accessToken)
      organizationDirectory.value = await getOrganizationDirectory(accessToken)
      users.value = organizationDirectory.value.users
      return saved
    } finally { operationPending.value = false }
  }

  async function resetUserPassword(username: string) {
    operationPending.value = true
    try { return await resetUserPasswordRequest(username, accessToken) }
    finally { operationPending.value = false }
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

  async function updateProject(projectId: string, input: UpdateProjectInput) {
    operationPending.value = true
    try {
      const saved = await updateProjectRequest(projectId, input, accessToken)
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
      const saved = await updateSystemSettingsRequest(settings, accessToken)
      systemSettings.value = {
        ...settings,
        ...saved,
        bomPropertyMappings: saved.bomPropertyMappings ?? settings.bomPropertyMappings,
        validationRules: saved.validationRules ?? settings.validationRules,
      }
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

  async function createRole(input: CreateRoleInput) {
    operationPending.value = true
    try {
      rolePermissionDirectory.value = await createRoleRequest(input, accessToken)
      return rolePermissionDirectory.value
    } finally { operationPending.value = false }
  }

  async function deleteRole(role: string) {
    operationPending.value = true
    try {
      rolePermissionDirectory.value = await deleteRoleRequest(role, accessToken)
      return rolePermissionDirectory.value
    } finally { operationPending.value = false }
  }

  async function selectProject(projectId: string) {
    await reload(projectId)
  }

  function closeProject() {
    clearProjectWorkspace()
  }

  async function login(username: string, password: string, rememberCredentials = false) {
    loginPending.value = true
    loginError.value = ''
    try {
      const session = await apiLogin(username, password)
      if (rememberCredentials) {
        postDesktopMessage('credentials-save', { username: username.trim(), password })
      } else {
        postDesktopMessage('credentials-clear')
      }
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

  async function saveMyProfile(profile: Pick<PdmUserProfile, 'landline' | 'mobilePhone' | 'email' | 'gender' | 'nickname'>) {
    currentProfile.value = await updateMyProfileRequest(profile, accessToken)
    return currentProfile.value
  }

  async function changeMyPassword(currentPassword: string, password: string) {
    const session = await changeMyPasswordRequest(currentPassword, password, accessToken)
    const profile = currentProfile.value
    applySession(session)
    currentProfile.value = profile
  }

  function logout() {
    clearSession()
    loginError.value = ''
    loadError.value = ''
  }

  function handleSessionExpired() {
    if (persistentSession?.resumeToken) {
      void renewPersistentSession()
      return
    }
    if (authenticated.value) clearSession()
    loadError.value = ''
    loginError.value = '登录已过期，请重新登录。'
  }

  function restoreSession(): AuthSession | null {
    try {
      const value = window.localStorage.getItem(sessionKey) ?? window.sessionStorage.getItem(sessionKey)
      if (!value) return null
      const session = JSON.parse(value) as AuthSession
      if (!session.accessToken || (!session.resumeToken && new Date(session.expiresAt).getTime() <= Date.now())) return null
      return session
    } catch {
      return null
    }
  }

  onMounted(async () => {
    window.addEventListener('pdm-session-expired', handleSessionExpired)
    window.addEventListener('focus', refreshOpenProjectOnFocus)
    projectSummaryTimer = window.setInterval(() => void refreshProjectSummaries(), 15_000)
    projectDocumentTimer = window.setInterval(() => void refreshOpenProjectDocuments(), 5_000)
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
      persistentSession = session
      if (new Date(session.expiresAt).getTime() > Date.now()) {
        applySession(session)
        await reload()
      } else {
        await renewPersistentSession()
      }
    }
  })

  onBeforeUnmount(() => {
    window.removeEventListener('pdm-session-expired', handleSessionExpired)
    window.removeEventListener('focus', refreshOpenProjectOnFocus)
    if (projectSummaryTimer !== undefined) window.clearInterval(projectSummaryTimer)
    if (projectDocumentTimer !== undefined) window.clearInterval(projectDocumentTimer)
    if (sessionRenewTimer !== undefined) window.clearTimeout(sessionRenewTimer)
  })

  return {
    getAccessToken: () => accessToken,
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
    standardBom,
    nonStandardBom,
    unclassifiedBom,
    bomSourceData,
    mechanicalBom,
    electricalBom,
    bomEmptyDeclarations,
    bomVersions,
    bomBaselines,
    selectedNode,
    selectedBomItem,
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
    currentProfile,
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
    passwordResetTasks,
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
    saveUser,
    resetUserPassword,
    updateOrganizationUnitManagers,
    updateProject,
    updateProjectExecutionUnit,
    updateMainProjectStaffing,
    updateChildProjectDesigners,
    saveSystemSettings,
    saveEquipmentType,
    updateProjectFolderPermissions,
    saveFolderTemplate,
    updateRolePermissions,
    createRole,
    deleteRole,
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
    generateBomFromDrawings,
    resolveBomItem,
    batchUpdateBomItems,
    batchDeleteBomItems,
    batchRestoreBomItems,
    restoreBomItemsFromSource,
    setBomCategoryEmpty,
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
    resetRequestedPassword,
    loadProjectVersions,
    loadProjectAuditEntries,
    loadStorageStatus,
    login,
    saveMyProfile,
    changeMyPassword,
    logout,
    reload,
  }
}
