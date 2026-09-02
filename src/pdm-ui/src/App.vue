<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onBeforeUnmount, onMounted, provide, ref, watch } from 'vue'
import AppHeader from './components/AppHeader.vue'
import AuditLog from './components/AuditLog.vue'
import BomManager from './components/BomManager.vue'
import DocumentTree from './components/DocumentTree.vue'
import DrawingReviewPanel from './components/DrawingReviewPanel.vue'
import ProjectFileLibrary from './components/ProjectFileLibrary.vue'
import LoginView from './components/LoginView.vue'
import MaterialManagement from './components/MaterialManagement.vue'
import StandardLibrary from './components/StandardLibrary.vue'
import MyTasks from './components/MyTasks.vue'
import PreviewWorkspace from './components/PreviewWorkspace.vue'
import PlmCubeIcon from './components/PlmCubeIcon.vue'
import ProgramTemplateLibrary from './components/ProgramTemplateLibrary.vue'
import ProjectManager from './components/ProjectManager.vue'
import ProjectVersions from './components/ProjectVersions.vue'
import ProjectWorkspaceHeader from './components/ProjectWorkspaceHeader.vue'
import type { ProjectTab } from './components/ProjectWorkspaceHeader.vue'
import ReleaseOverview from './components/ReleaseOverview.vue'
import SideNav from './components/SideNav.vue'
import SquareLoader from './components/SquareLoader.vue'
import SystemManagement from './components/SystemManagement.vue'
import WorkbenchHome from './components/WorkbenchHome.vue'
import WorkspaceExplorerBar from './components/WorkspaceExplorerBar.vue'
import { postDesktopMessage } from './api'
import { usePdmWorkspace } from './composables/usePdmWorkspace'
import type { AddDrawingReviewMarkupInput, DocumentNode, DrawingReviewBadge, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget, DrawingReviewTargetState, WorkspaceLocalFileState, WorkspaceLocalStateSnapshot } from './types'
import { resolveUserDisplayName, userDisplayNameKey } from './userDisplay'

const workspace = usePdmWorkspace()
const displayUserName = (username?: string | null, emptyText = '—') => resolveUserDisplayName(workspace.users.value, username, emptyText)
provide(userDisplayNameKey, displayUserName)
type PdmTheme = 'a' | 'c' | 'o'
type NavKey = 'project-center' | 'projects' | 'materials' | 'standard-library' | 'standard-structure' | 'program-templates' | 'tasks' | 'admin'
type ActiveView = NavKey | 'workspace'
const activeView = ref<ActiveView>('project-center')
const activeNav = computed<NavKey>(() => activeView.value === 'workspace' ? 'project-center' : activeView.value)
const desktopAvailable = Boolean(window.chrome?.webview)
const workspaceLocalSnapshot = ref<WorkspaceLocalStateSnapshot>()
const workspaceLocalRefreshing = ref(false)
const workspaceLocalStates = computed<Record<string, WorkspaceLocalFileState>>(() => Object.fromEntries((workspaceLocalSnapshot.value?.items ?? []).map(item => [item.documentId, item])))
const selectedWorkspaceLocalState = computed(() => workspace.selectedNode.value.documentId ? workspaceLocalStates.value[workspace.selectedNode.value.documentId] : undefined)
const canManageSystem = computed(() => desktopAvailable || ['settings.customer.manage', 'settings.organization.manage', 'settings.folder.manage', 'settings.storage.manage', 'system.role.view', 'audit.view'].some(workspace.hasPermission))
const projectTab = ref<ProjectTab>('overview')
const mountedBomProjectId = ref('')
const materialRequestedTab = ref('materials')
const requestedProgramTemplateId = ref('')
const drawingReviewPanelOpen = ref(false)
const drawingReviewPackageId = ref('')
const requestedReleasePackageId = ref('')
const restoreNote = ref('从历史版本恢复生成新的工作版本')
const bomHasUnsavedChanges = ref(false)
const projectCenterOpening = ref(false)
const switchingProjectId = ref('')
const projectTabMemoryKey = 'upton-pdm-project-tabs'
const projectCenterMemoryKey = 'upton-pdm-project-center'
const activeNavigationMemoryKey = 'upton-pdm-active-navigation'
const sidebarCollapsedMemoryKey = 'upton-pdm-sidebar-collapsed'
const sidebarCollapsed = ref(window.localStorage.getItem(sidebarCollapsedMemoryKey) === 'true')
const savedTheme = window.localStorage.getItem('pdm_theme')
const theme = ref<PdmTheme>(savedTheme === 'c' || savedTheme === 'o' ? savedTheme : 'a')
const notificationCount = computed(() => workspace.myApprovalTasks.value.length + workspace.materialCodeApprovalTasks.value.length + workspace.programTemplateTasks.value.length + workspace.passwordResetTasks.value.length + new Set(workspace.editLocks.value.filter(lock => lock.ownedByCurrentUser || lock.releaseRequestedBy || lock.canForceRelease).map(lock => lock.projectId)).size)
const materialNoticeCount = ref(0)
function updateMaterialNoticeCount(counts: { syncTasks: number; codeApprovals: number }) {
  materialNoticeCount.value = counts.syncTasks + counts.codeApprovals
}
watch([workspace.materialSyncTasks, workspace.materialCodeApprovalTasks], ([syncTasks, codeApprovals]) => {
  updateMaterialNoticeCount({
    syncTasks: syncTasks.filter(task => !['Succeeded', 'Superseded'].includes(task.status)).length,
    codeApprovals: codeApprovals.length,
  })
}, { immediate: true })
const systemOrganizationId = computed(() => workspace.activeCompanyId.value)
const companyName = computed(() => workspace.activeCompanyName.value || workspace.accessibleCompanies.value.find(item => item.id === workspace.activeCompanyId.value)?.name || '昆山阿普顿自动化系统有限公司')
const activeProjectDocumentStatus = computed(() => {
  const owner = workspace.root.value.checkedOutBy?.trim()
  if (!owner) return '正常'
  const currentUsername = workspace.currentUsername.value.trim()
  if (owner.localeCompare(currentUsername, undefined, { sensitivity: 'accent' }) === 0) return '可编辑'
  return `${displayUserName(owner)}编辑中`
})

function workspaceDocuments(root: DocumentNode | undefined, drawings: DocumentNode[]) {
  const byDocumentId = new Map<string, { documentId: string; fileName: string; latestRevision: string; checkedOutBy: string }>()
  const visit = (node: DocumentNode | undefined) => {
    if (!node) return
    if (node.documentId && !byDocumentId.has(node.documentId)) byDocumentId.set(node.documentId, {
      documentId: node.documentId,
      fileName: node.fileName,
      latestRevision: node.version,
      checkedOutBy: node.checkedOutBy ?? '',
    })
    node.children.forEach(visit)
  }
  visit(root)
  drawings.forEach(visit)
  return [...byDocumentId.values()]
}

function requestWorkspaceLocalState() {
  if (!desktopAvailable || !workspace.project.value.id || !workspace.project.value.code) return
  workspaceLocalRefreshing.value = true
  postDesktopMessage('workspace-local-state-request', {
    projectId: workspace.project.value.id,
    projectCode: workspace.project.value.code,
    currentUsername: workspace.currentUsername.value,
    documents: workspaceDocuments(workspace.root.value, workspace.filteredDrawings.value),
  })
}

function handleWorkspaceLocalState(event: Event) {
  const snapshot = (event as CustomEvent<WorkspaceLocalStateSnapshot>).detail
  if (!snapshot || snapshot.projectId !== workspace.project.value.id) return
  workspaceLocalSnapshot.value = snapshot
  workspaceLocalRefreshing.value = false
}

function openWorkspaceFolder(node = workspace.selectedNode.value) {
  if (!desktopAvailable) return
  postDesktopMessage('workspace-open-folder', {
    projectId: workspace.project.value.id,
    projectCode: workspace.project.value.code,
    documentId: node.documentId,
  })
}

function handleWorkspaceSolidWorksStatus(event: Event) {
  const state = (event as CustomEvent<{ state?: string }>).detail?.state
  if (state === 'ready' || state === 'error') window.setTimeout(requestWorkspaceLocalState, 500)
}

function packageContainsDocument(review: DrawingReviewPackage, documentId: string | undefined) {
  return Boolean(documentId && review.items.some(item => item.modelDocumentId === documentId || item.drawingDocumentId === documentId))
}

const selectedDrawingReviewPackage = computed(() => workspace.drawingReviews.value.find(item => item.id === drawingReviewPackageId.value)
  ?? workspace.drawingReviews.value.find(item => packageContainsDocument(item, workspace.selectedNode.value.documentId))
  ?? workspace.drawingReviews.value[0])
const selectedDrawingReviewItem = computed(() => selectedDrawingReviewPackage.value?.items.find(item => item.modelDocumentId === workspace.selectedNode.value.documentId || item.drawingDocumentId === workspace.selectedNode.value.documentId))
const selectedDrawingReviewTarget = computed(() => selectedDrawingReviewItem.value?.drawingDocumentId === workspace.selectedNode.value.documentId ? 'Drawing2D' : 'Model3D')
const selectedDrawingReviewVersionId = computed(() => {
  if (!selectedDrawingReviewItem.value) return ''
  return selectedDrawingReviewTarget.value === 'Drawing2D'
    ? selectedDrawingReviewItem.value.effectiveDrawingVersionId ?? ''
    : selectedDrawingReviewItem.value.effectiveModelVersionId
})
const selectedDrawingReviewRevision = computed(() => {
  if (!selectedDrawingReviewVersionId.value || !selectedDrawingReviewItem.value) return ''
  return selectedDrawingReviewTarget.value === 'Drawing2D'
    ? selectedDrawingReviewItem.value.drawingRevision ?? ''
    : selectedDrawingReviewItem.value.modelRevision
})

function reviewBadge(review: DrawingReviewPackage, state: DrawingReviewTargetState): DrawingReviewBadge {
  if (review.state === 'Stale') return { label: '版本冲突', tone: 'danger' }
  if (review.state === 'Withdrawn') return { label: '已撤销', tone: 'neutral' }
  if (review.state === 'WritingProperties') return { label: '写入标记', tone: 'pending' }
  if (state === 'ChangesRequested') return { label: '已退改', tone: 'danger' }
  if (state === 'Marked') return { label: '已审核', tone: 'success' }
  if (state === 'Approved') return { label: '待写标记', tone: 'pending' }
  return { label: '待审核', tone: 'warning' }
}

const drawingReviewStates = computed<Record<string, DrawingReviewBadge>>(() => {
  const states: Record<string, DrawingReviewBadge> = {}
  const nonStandardModelIds = new Set(workspace.nonStandardBom.value
    .filter(item => !item.manuallyExcluded && !item.pendingClassification && item.sourceDocumentId)
    .map(item => item.sourceDocumentId!))
  for (const documentId of nonStandardModelIds) states[documentId] = { label: '未发起', tone: 'neutral' }
  for (const relation of workspace.documentRelations.value) {
    if (nonStandardModelIds.has(relation.modelDocumentId)) states[relation.drawingDocumentId] = { label: '未发起', tone: 'neutral' }
  }
  const reviews = [...workspace.drawingReviews.value].sort((left, right) => left.createdAt.localeCompare(right.createdAt))
  for (const review of reviews) {
    for (const item of review.items) {
      states[item.modelDocumentId] = reviewBadge(review, item.modelState)
      if (item.drawingDocumentId) states[item.drawingDocumentId] = reviewBadge(review, item.drawingState)
    }
  }
  return states
})
const selectedDrawingReviewStatus = computed(() => {
  const documentId = workspace.selectedNode.value.documentId
  if (!documentId) return { label: '未纳入审核', tone: 'neutral' as const }
  return drawingReviewStates.value[documentId] ?? { label: '未纳入审核', tone: 'neutral' as const }
})
const canWritebackSelectedDrawingReview = computed(() => {
  if (selectedDrawingReviewPackage.value?.state !== 'WritingProperties' || !selectedDrawingReviewItem.value) return false
  return selectedDrawingReviewTarget.value === 'Drawing2D'
    ? selectedDrawingReviewItem.value.drawingState === 'Approved'
    : selectedDrawingReviewItem.value.modelState === 'Approved'
})
const canManageDrawingReviewWithdrawal = computed(() => workspace.hasRole('Administrator')
  || workspace.project.value.primaryProjectManager === workspace.currentUsername.value
  || (workspace.project.value.collaborativeProjectManagers ?? []).includes(workspace.currentUsername.value))
const drawingReviewOverlayState = computed(() => ({
  visible: desktopAvailable && activeView.value === 'workspace' && projectTab.value === 'documents' && drawingReviewPanelOpen.value,
  packageId: drawingReviewPackageId.value,
  packages: workspace.drawingReviews.value,
  candidates: workspace.drawingReviewCandidates.value,
  selectedDocumentId: workspace.selectedNode.value.documentId,
  currentUsername: workspace.currentUsername.value,
  pending: workspace.operationPending.value,
  canSubmit: workspace.hasPermission('drawing-review.submit'),
  canManageWithdraw: canManageDrawingReviewWithdrawal.value,
  canAnnotate: workspace.hasPermission('drawing-review.annotate'),
  canDecide: workspace.hasPermission('drawing-review.decide'),
  allowSelfReview: workspace.hasRole('developer'),
  theme: theme.value,
}))

watch([() => workspace.drawingReviews.value, () => workspace.selectedNode.value.documentId], ([reviews, documentId]) => {
  if (!reviews.length) {
    drawingReviewPackageId.value = ''
    return
  }
  const current = reviews.find(item => item.id === drawingReviewPackageId.value)
  const matching = reviews.find(item => packageContainsDocument(item, documentId))
  if (!current || (documentId && !packageContainsDocument(current, documentId) && matching)) drawingReviewPackageId.value = matching?.id ?? reviews[0].id
}, { immediate: true, deep: true })
watch(drawingReviewOverlayState, state => {
  if (desktopAvailable) postDesktopMessage('review-overlay-state', state)
}, { immediate: true, deep: true, flush: 'post' })

function setSystemOrganizationId(organizationId: string) {
  workspace.switchCompany(organizationId)
}

function selectTheme(value: PdmTheme) {
  theme.value = value
  window.localStorage.setItem('pdm_theme', value)
}

watch(theme, value => {
  document.documentElement.dataset.pdmTheme = value
  if (desktopAvailable) postDesktopMessage('theme-change', { theme: value })
}, { immediate: true })

function toggleSidebar() {
  sidebarCollapsed.value = !sidebarCollapsed.value
  window.localStorage.setItem(sidebarCollapsedMemoryKey, String(sidebarCollapsed.value))
}

function readRememberedProjectTab(projectId: string): ProjectTab {
  try {
    const page = JSON.parse(window.localStorage.getItem(projectCenterMemoryKey) ?? 'null') as { projectId?: string; tab?: ProjectTab } | null
    if (page?.projectId === projectId && page.tab && supportedProjectTabs.includes(page.tab)) return page.tab
  } catch {
    // Local storage may be unavailable or contain a stale value.
  }
  try {
    const tabs = JSON.parse(window.sessionStorage.getItem(projectTabMemoryKey) ?? '{}') as Record<string, ProjectTab>
    return supportedProjectTabs.includes(tabs[projectId]) ? tabs[projectId] : 'overview'
  } catch {
    return 'overview'
  }
}

function rememberProjectTab(projectId: string, tab: ProjectTab) {
  try {
    const tabs = JSON.parse(window.sessionStorage.getItem(projectTabMemoryKey) ?? '{}') as Record<string, ProjectTab>
    window.sessionStorage.setItem(projectTabMemoryKey, JSON.stringify({ ...tabs, [projectId]: tab }))
  } catch {
    // Session storage may be unavailable in restricted WebView environments.
  }
  try {
    window.localStorage.setItem(projectCenterMemoryKey, JSON.stringify({
      projectId,
      tab,
      username: workspace.currentUsername.value,
      companyId: workspace.activeCompanyId.value,
    }))
  } catch {
    // Local storage may be unavailable in restricted WebView environments.
  }
}

function readRememberedProjectCenterPage() {
  try {
    const page = JSON.parse(window.localStorage.getItem(projectCenterMemoryKey) ?? 'null') as { projectId?: string; tab?: ProjectTab; username?: string; companyId?: string } | null
    if (page?.projectId
      && page.tab
      && page.username === workspace.currentUsername.value
      && page.companyId === workspace.activeCompanyId.value
      && supportedProjectTabs.includes(page.tab)) return { projectId: page.projectId, tab: page.tab }
  } catch {
    // Ignore stale or unavailable local storage and use the first accessible project.
  }
  return null
}

function rememberNavigation(key: NavKey) {
  try {
    window.localStorage.setItem(activeNavigationMemoryKey, key)
  } catch {
    // Local storage may be unavailable in restricted WebView environments.
  }
}

async function openProjectTab(tab: ProjectTab) {
  if (projectTab.value === 'bom' && tab !== 'bom' && bomHasUnsavedChanges.value) {
    ElMessage.warning('当前BOM有未保存修改，请先保存或撤销修改后再离开。')
    return
  }
  if (!workspace.project.value.canReadContent && tab !== 'overview' && tab !== 'records') tab = 'overview'
  if (tab === 'bom') mountedBomProjectId.value = workspace.project.value.id
  projectTab.value = tab
  if (workspace.project.value.id) rememberProjectTab(workspace.project.value.id, tab)
  if (tab === 'versions') await workspace.loadProjectVersions()
  if (tab === 'documents') await workspace.refreshDrawingReviews()
  if (tab === 'records') await workspace.loadProjectAuditEntries()
}

function openProjectList() {
  if (projectTab.value === 'bom' && bomHasUnsavedChanges.value) {
    ElMessage.warning('当前BOM有未保存修改，请先保存或撤销修改后再离开。')
    return
  }
  activeView.value = 'projects'
  rememberNavigation('projects')
}

async function handleNavigation(key: NavKey) {
  if (activeView.value === 'workspace' && projectTab.value === 'bom' && bomHasUnsavedChanges.value && key !== 'project-center') {
    ElMessage.warning('当前BOM有未保存修改，请先保存或撤销修改后再离开。')
    return
  }
  if (key === 'project-center') return openProjectCenter(true)
  if (key === 'projects') openProjectList()
  if (key === 'materials') {
    materialRequestedTab.value = 'materials'
    activeView.value = 'materials'
    rememberNavigation(key)
  }
  if (key === 'standard-library') {
    activeView.value = 'standard-library'
    rememberNavigation(key)
  }
  if (key === 'standard-structure') {
    activeView.value = 'standard-structure'
    rememberNavigation(key)
  }
  if (key === 'program-templates') {
    requestedProgramTemplateId.value = ''
    activeView.value = 'program-templates'
    rememberNavigation(key)
  }
  if (key === 'tasks') {
    activeView.value = 'tasks'
    rememberNavigation(key)
    await workspace.loadMyApprovalTasks()
  }
  if (key === 'admin') {
    activeView.value = 'admin'
    rememberNavigation(key)
    if (workspace.hasPermission('audit.view')) await workspace.loadAuditEntries()
  }
}

function openMaterialApprovals() {
  materialRequestedTab.value = 'code-approvals'
  activeView.value = 'materials'
  rememberNavigation('materials')
}

async function openManagedProject(projectId: string, requestedTab?: ProjectTab) {
  if (workspace.project.value.id && workspace.project.value.id !== projectId && projectTab.value === 'bom' && bomHasUnsavedChanges.value) {
    ElMessage.warning('当前BOM有未保存修改，请先保存或撤销修改后再切换项目。')
    return false
  }
  try {
    await workspace.selectProject(projectId)
    if (!workspace.ready.value || workspace.loadError.value || workspace.project.value.id !== projectId) {
      throw new Error(workspace.loadError.value || '项目数据未能加载，请重试')
    }
    activeView.value = 'workspace'
    rememberNavigation('project-center')
    await openProjectTab(requestedTab ?? readRememberedProjectTab(projectId))
    return true
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '项目加载失败')
    return false
  }
}

async function openReleasePackage(projectId: string, releasePackageId: string) {
  requestedReleasePackageId.value = releasePackageId
  await openManagedProject(projectId, 'bom')
}

function openProgramTemplate(templateId: string) {
  requestedProgramTemplateId.value = templateId
  activeView.value = 'program-templates'
  rememberNavigation('program-templates')
}

type ProjectNavigationRequest = { projectId: string; tab: ProjectTab }
const supportedProjectTabs: ProjectTab[] = ['overview', 'files', 'documents', 'bom', 'versions', 'release', 'records']
let pendingProjectNavigation: ProjectNavigationRequest | null = null
let projectNavigationInProgress = false
let initialPageRestored = false

async function openProjectCenter(resetToOverview = false) {
  if (projectCenterOpening.value) return
  rememberNavigation('project-center')
  if (workspace.project.value.id) {
    activeView.value = 'workspace'
    if (resetToOverview) await openProjectTab('overview')
    return
  }

  projectCenterOpening.value = true
  activeView.value = 'project-center'
  try {
    const remembered = readRememberedProjectCenterPage()
    const username = workspace.currentUsername.value.trim().toLocaleLowerCase()
    const isResponsible = (project: (typeof workspace.projects.value)[number]) => [
      project.owner,
      project.primaryProjectManager,
      project.designLead,
      ...(project.responsibleUsers ?? []),
      ...(project.collaborativeProjectManagers ?? []),
      ...(project.designLeads ?? []),
      ...(project.designers ?? []),
    ].some(value => value?.trim().toLocaleLowerCase() === username)
    const byRecency = (left: (typeof workspace.projects.value)[number], right: (typeof workspace.projects.value)[number]) =>
      (right.signedDate ?? '').localeCompare(left.signedDate ?? '')
      || right.code.localeCompare(left.code, 'zh-CN', { numeric: true, sensitivity: 'base' })
    const target = workspace.projects.value.find(project => project.id === remembered?.projectId)
      ?? [...workspace.projects.value].filter(isResponsible).sort(byRecency)[0]
      ?? [...workspace.projects.value].sort(byRecency)[0]
    if (!target) {
      activeView.value = 'projects'
      return
    }
    const opened = await openManagedProject(target.id, resetToOverview ? 'overview' : target.id === remembered?.projectId ? remembered.tab : 'overview')
    if (!opened) activeView.value = 'projects'
  } finally {
    projectCenterOpening.value = false
  }
}

async function restoreLastPage() {
  if (initialPageRestored
    || pendingProjectNavigation
    || projectNavigationInProgress
    || !workspace.authenticated.value
    || !workspace.ready.value) return
  initialPageRestored = true
  if (desktopAvailable) {
    await openProjectCenter(true)
    return
  }
  await openProjectCenter(true)
}

async function applyPendingProjectNavigation() {
  if (!pendingProjectNavigation
    || projectNavigationInProgress
    || !workspace.authenticated.value
    || !workspace.ready.value) return

  const request = pendingProjectNavigation
  pendingProjectNavigation = null
  projectNavigationInProgress = true
  initialPageRestored = true
  try {
    await openManagedProject(request.projectId, request.tab)
  } finally {
    projectNavigationInProgress = false
    if (pendingProjectNavigation) void applyPendingProjectNavigation()
  }
}

function handleProjectNavigation(event: Event) {
  const detail = (event as CustomEvent<{ projectId?: string; tab?: string }>).detail
  if (!detail?.projectId) return
  const tab = supportedProjectTabs.includes(detail.tab as ProjectTab) ? detail.tab as ProjectTab : 'documents'
  pendingProjectNavigation = { projectId: detail.projectId, tab }
  void applyPendingProjectNavigation()
}

watch([workspace.authenticated, workspace.ready], ([authenticated, ready]) => {
  if (!authenticated) {
    initialPageRestored = false
    activeView.value = 'project-center'
    projectTab.value = 'overview'
    return
  }
  if (!ready) return
  void (async () => {
    await applyPendingProjectNavigation()
    await restoreLastPage()
  })()
}, { immediate: true })

async function refreshDocumentTree() {
  if (workspace.loading.value || !workspace.project.value.id) return
  await workspace.reload(workspace.project.value.id)
  if (workspace.ready.value && !workspace.loadError.value) {
    requestWorkspaceLocalState()
    ElMessage.success('设计树和本地工作区已刷新')
  }
}
watch([() => workspace.project.value.id, () => workspace.root.value], () => requestWorkspaceLocalState(), { flush: 'post' })
onMounted(() => {
  window.addEventListener('pdm-open-project', handleProjectNavigation)
  window.addEventListener('pdm-workspace-local-state', handleWorkspaceLocalState)
  window.addEventListener('pdm-solidworks-status', handleWorkspaceSolidWorksStatus)
  window.chrome?.webview?.addEventListener('message', handleReviewOverlayAction)
  requestWorkspaceLocalState()
})
onBeforeUnmount(() => {
  window.removeEventListener('pdm-open-project', handleProjectNavigation)
  window.removeEventListener('pdm-workspace-local-state', handleWorkspaceLocalState)
  window.removeEventListener('pdm-solidworks-status', handleWorkspaceSolidWorksStatus)
  window.chrome?.webview?.removeEventListener?.('message', handleReviewOverlayAction)
  if (document.documentElement.dataset.pdmTheme === theme.value) delete document.documentElement.dataset.pdmTheme
})

async function login(username: string, password: string, rememberCredentials: boolean) {
  await workspace.login(username, password, rememberCredentials)
}

async function switchProject(projectId: string) {
  if (switchingProjectId.value || projectId === workspace.project.value.id) return
  switchingProjectId.value = projectId
  try {
    await openManagedProject(projectId, projectTab.value)
  } finally {
    switchingProjectId.value = ''
  }
}

async function openVersionDocument(documentId: string) {
  if (!workspace.selectDocument(documentId)) {
    ElMessage.warning('该图档不在当前引用树中，暂不能发起版本对比')
    return
  }
  await workspace.openVersionDrawer()
}

async function runOperation(action: () => Promise<unknown>, success: string) {
  try { await action(); ElMessage.success(success) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '操作失败') }
}

async function generateBom(discardUnsavedChanges = false) {
  try {
    const result = await workspace.generateBomFromDrawings(discardUnsavedChanges)
    if (result === null) {
      ElMessage.info('已取消重新对账，未修改BOM数据')
      return
    }
    ElMessage.success('BOM已按最新设计树完成重新对账')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'BOM更新失败')
  }
}

function versionStatus(status: 'Work' | 'Released' | 0 | 1) {
  return status === 'Released' || status === 1 ? '正式版' : '工作版'
}

function propertyChangeKind(kind: string | number) {
  return typeof kind === 'string' ? kind : ['新增', '删除', '修改'][kind] ?? String(kind)
}

function referenceChangeKind(kind: string | number) {
  return typeof kind === 'string' ? kind : ['新增零件', '删除零件', '替换文件', '移动', '配置变化', '数量变化', '状态变化'][kind] ?? String(kind)
}

function bomChangeKind(kind: string | number) {
  return typeof kind === 'string' ? kind : ['物料新增', '物料删除', '数量变化', '材料变化', '规格变化', '版本变化'][kind] ?? String(kind)
}

async function restoreSelectedVersion() {
  if (!workspace.leftVersionId.value) return
  try {
    await workspace.restoreVersion(workspace.leftVersionId.value, restoreNote.value)
    ElMessage.success('已从历史版本创建新的工作版本，当前文件未被覆盖')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '历史版本恢复失败')
  }
}

async function withdrawCurrentPackage(releasePackageId: string) {
  try {
    const { value } = await ElMessageBox.prompt('请填写撤回原因。撤回后发布包回到草稿，相关图档恢复为工作中。', '撤回审批', {
      confirmButtonText: '确认撤回', cancelButtonText: '取消', inputPattern: /\S+/, inputErrorMessage: '撤回原因不能为空', type: 'warning',
    })
    await workspace.withdrawPackage(releasePackageId, value)
    ElMessage.success('审批已撤回，相关图档已恢复为工作中')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '撤回审批失败')
  }
}

async function obsoleteSelectedDocument() {
  try {
    const { value } = await ElMessageBox.prompt('作废后该图档不能再获取编辑权限。请填写可追溯的作废原因。', '作废图档', {
      confirmButtonText: '确认作废', cancelButtonText: '取消', inputPattern: /\S+/, inputErrorMessage: '作废原因不能为空', type: 'warning',
    })
    await workspace.obsoleteSelectedDocument(value)
    ElMessage.success('图档已受控作废')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '作废图档失败')
  }
}

function toggleDrawingReviewPanel() {
  drawingReviewPanelOpen.value = !drawingReviewPanelOpen.value
}

function selectDrawingReviewDocument(documentId: string) {
  const document = workspace.managedDocuments.value.find(item => item.id === documentId)
  if (document) workspace.setDocumentFilter(document.kind === 'Drawing' ? 'drawing' : 'model')
  if (!workspace.selectDocument(documentId)) ElMessage.warning('审核图档不在当前项目设计树中')
}

async function createDrawingReviewFromDocuments(modelDocumentIds: string[]) {
  await workspace.createDrawingReview(modelDocumentIds)
  drawingReviewPackageId.value = workspace.drawingReviews.value[0]?.id ?? ''
}

function handleReviewOverlayAction(event: MessageEvent) {
  const message = event.data as {
    type?: string
    payload?: {
      action?: string
      packageId?: string
      documentId?: string
      markupId?: string
      itemId?: string
      input?: AddDrawingReviewMarkupInput
      target?: DrawingReviewTarget
      decision?: DrawingReviewDecision
      comment?: string
      reason?: string
      modelDocumentIds?: string[]
    }
  } | undefined
  if (message?.type !== 'review-overlay-action' || !message.payload?.action) return
  const payload = message.payload
  switch (payload.action) {
    case 'update-package':
      drawingReviewPackageId.value = payload.packageId ?? ''
      break
    case 'close':
      drawingReviewPanelOpen.value = false
      break
    case 'create':
      if (payload.modelDocumentIds?.length) void runOperation(() => createDrawingReviewFromDocuments(payload.modelDocumentIds!), '图纸审核单已创建，所选3D和2D版本已冻结')
      break
    case 'refresh':
      void runOperation(workspace.refreshDrawingReviews, '图纸审核状态已刷新')
      break
    case 'refresh-candidates':
      void runOperation(workspace.refreshDrawingReviews, '审核范围已刷新')
      break
    case 'withdraw':
      if (payload.packageId && payload.reason) void runOperation(() => workspace.withdrawDrawingReview(payload.packageId!, payload.reason!), '图纸审核已撤销，编辑锁已释放')
      break
    case 'select-document':
      if (payload.documentId) selectDrawingReviewDocument(payload.documentId)
      break
    case 'add-markup':
      if (payload.packageId && payload.input) void runOperation(() => workspace.addDrawingReviewMarkup(payload.packageId!, payload.input!), '图纸批注已保存')
      break
    case 'resolve-markup':
      if (payload.packageId && payload.markupId) void runOperation(() => workspace.resolveDrawingReviewMarkup(payload.packageId!, payload.markupId!), '图纸批注已关闭')
      break
    case 'decide':
      if (payload.packageId && payload.itemId && payload.target && payload.decision) {
        void runOperation(
          () => workspace.decideDrawingReviewTarget(payload.packageId!, payload.itemId!, payload.target!, payload.decision!, payload.comment ?? ''),
          payload.decision === 'Approve' ? '审核结果已记录' : '图纸已退回修改',
        )
      }
      break
  }
}

watch(projectTab, (tab, previousTab) => {
  if (previousTab === 'documents' && tab !== 'documents') postDesktopMessage('preview-host-hide')
  if (tab !== 'documents') drawingReviewPanelOpen.value = false
}, { flush: 'sync' })

watch(activeView, (view, previousView) => {
  if (previousView === 'workspace' && view !== 'workspace' && projectTab.value === 'documents') {
    postDesktopMessage('preview-host-hide')
  }
}, { flush: 'sync' })

async function openWhereUsedParent(projectId: string, parentDocumentId: string) {
  workspace.whereUsedDrawerOpen.value = false
  await openManagedProject(projectId, 'documents')
  if (!workspace.selectDocument(parentDocumentId)) ElMessage.warning('父装配体不在当前设计树中')
}
</script>

<template>
  <div class="pdm-app-shell" :class="`theme-${theme}`">
    <div v-if="!workspace.authInitialized.value" class="pdm-session-restoring" role="status" aria-label="正在恢复登录状态">
      <span class="pdm-session-restoring__spinner" aria-hidden="true" />
      <span>正在恢复登录状态…</span>
    </div>
    <LoginView
      v-else-if="!workspace.authenticated.value"
      compact
      class="pdm-login-page"
      aria-label="未登录主页"
      :pending="workspace.loginPending.value"
      :error="workspace.loginError.value"
      :online="workspace.serviceOnline.value"
      @submit="login"
    />
    <div v-else class="pdm-app-body" :class="{ 'is-sidebar-collapsed': sidebarCollapsed }">
      <SideNav
        :active="activeNav"
        :approval-count="notificationCount"
        :material-count="materialNoticeCount"
        :can-manage-system="canManageSystem"
        :can-view-standard-library="workspace.hasPermission('standard-library.view')"
        :can-view-materials="workspace.hasPermission('material.view')"
        :collapsed="sidebarCollapsed"
        @navigate="handleNavigation"
      />
      <section class="pdm-shell-content">
        <AppHeader
          :online="workspace.serviceOnline.value"
          :user-name="workspace.currentUser.value"
          :username="workspace.currentUsername.value"
          :role="workspace.currentRole.value"
          :company-name="companyName"
          :active-company-id="workspace.activeCompanyId.value"
          :accessible-companies="workspace.accessibleCompanies.value"
          :notification-count="notificationCount"
        :theme="theme"
        :sidebar-collapsed="sidebarCollapsed"
          :profile="workspace.currentProfile.value"
          :on-save-profile="workspace.saveMyProfile"
          :on-change-password="workspace.changeMyPassword"
          @logout="workspace.logout"
          @notifications="handleNavigation('tasks')"
        @company="workspace.switchCompany"
        @theme="selectTheme"
        @toggle-sidebar="toggleSidebar"
        />
        <main class="pdm-main" :class="{ 'is-project-workspace': activeView === 'workspace' }">
        <section v-if="workspace.loading.value && !workspace.ready.value" class="pdm-panel pdm-workspace-state" aria-live="polite">
          <SquareLoader label="正在加载权限内项目和待办任务" />
          <h1>正在读取PLM数据</h1>
          <p>正在加载权限内项目和待办任务…</p>
        </section>
        <section v-else-if="workspace.loadError.value" class="pdm-panel pdm-workspace-state is-error" role="alert">
          <h1>数据加载失败</h1>
          <p>{{ workspace.loadError.value }}</p>
          <div class="pdm-state-actions">
            <button type="button" class="pdm-primary-action" @click="workspace.reload()">重新加载</button>
            <button type="button" class="pdm-secondary-action" @click="workspace.logout">重新登录</button>
          </div>
        </section>
        <template v-else-if="workspace.ready.value">
        <section v-if="activeView === 'project-center'" class="pdm-panel pdm-workspace-state" aria-live="polite" aria-label="正在打开项目中心">
          <SquareLoader label="正在打开最近项目" />
          <h1>正在打开项目中心</h1>
          <p>{{ projectCenterOpening ? '正在读取最近打开或负责的项目…' : '正在准备项目数据…' }}</p>
        </section>
        <ProjectManager
          v-else-if="activeView === 'projects'"
          :projects="workspace.projects.value"
          :numbering-options="workspace.projectNumberingOptions.value"
          :customers="workspace.customers.value"
          :users="workspace.users.value"
          :organization-directory="workspace.organizationDirectory.value"
          :current-username="workspace.currentUsername.value"
          :administrator="workspace.hasRole('Administrator')"
          :can-create="workspace.hasPermission('project.create')"
          :can-edit="workspace.hasRole('Administrator') || workspace.hasPermission('project.edit')"
          :can-delete="workspace.hasRole('Administrator') || workspace.hasPermission('project.delete')"
          :can-create-subproject="workspace.hasPermission('project.child.create')"
          :pending="workspace.operationPending.value"
          :on-create="workspace.createProject"
          :on-create-subproject="workspace.createSubproject"
          :on-update-project="workspace.updateProject"
          :on-delete-project="workspace.deleteProject"
          :on-update-execution-unit="workspace.updateProjectExecutionUnit"
          :on-update-main-staffing="workspace.updateMainProjectStaffing"
          :on-update-designers="workspace.updateChildProjectDesigners"
          :on-update-child-manager="workspace.updateChildProjectManager"
          @open="openManagedProject"
        />
        <MyTasks v-else-if="activeView === 'tasks'" :tasks="workspace.myApprovalTasks.value" :material-code-tasks="workspace.materialCodeApprovalTasks.value" :program-template-tasks="workspace.programTemplateTasks.value" :locks="workspace.editLocks.value" :password-reset-tasks="workspace.passwordResetTasks.value" :pending="workspace.operationPending.value" :on-request-release="workspace.requestEditLockRelease" :on-force-release="workspace.forceReleaseEditLock" :on-reset-password="workspace.resetRequestedPassword" @refresh="runOperation(workspace.loadMyApprovalTasks, '待办任务已刷新')" @open="openReleasePackage" @open-material-approvals="openMaterialApprovals" @open-program-template="openProgramTemplate" />
        <ProgramTemplateLibrary
          v-else-if="activeView === 'program-templates'"
          :token="workspace.getAccessToken()"
          :username="workspace.currentUsername.value"
          :permissions="workspace.currentPermissions.value"
          :requested-template-id="requestedProgramTemplateId"
          @tasks-changed="workspace.loadMyApprovalTasks"
        />
        <MaterialManagement
          v-else-if="activeView === 'materials'"
          :token="workspace.getAccessToken()"
          :can-edit="workspace.hasPermission('material.manage')"
          :can-approve="workspace.hasPermission('material.manage')"
          :can-decide-material-code="workspace.hasPermission('approval.decide')"
          :can-manage-integration="workspace.hasPermission('settings.storage.manage')"
          :requested-tab="materialRequestedTab"
          @notice-counts-change="updateMaterialNoticeCount"
        />
        <StandardLibrary
          v-else-if="activeView === 'standard-library'"
          :token="workspace.getAccessToken()"
          :can-manage="workspace.hasPermission('standard-library.manage')"
          :can-edit="workspace.hasPermission('material.manage')"
        />
        <section v-else-if="activeView === 'standard-structure'" class="pdm-panel pdm-workspace-state pdm-standard-structure-state" aria-label="标准结构">
          <PlmCubeIcon
            class="pdm-standard-structure-cube"
            motion="axial"
            style="--plm-cube-box-size: 132px; --plm-cube-size: 76px; --plm-cube-perspective: 380px"
          />
          <h1>标准结构</h1>
          <p>开发中</p>
        </section>
        <SystemManagement
          v-else-if="activeView === 'admin'"
          :desktop-available="desktopAvailable"
          :token="workspace.getAccessToken()"
          :customers="workspace.customers.value"
          :crm-integration-settings="workspace.crmIntegrationSettings.value"
          :settings="workspace.systemSettings.value"
          :equipment-types="workspace.equipmentTypes.value"
          :numbering-options="workspace.projectNumberingOptions.value"
          :organization-directory="workspace.organizationDirectory.value"
          :role-permission-directory="workspace.rolePermissionDirectory.value"
          :active-organization-id="systemOrganizationId"
          :permissions="workspace.currentPermissions.value"
          :current-username="workspace.currentUsername.value"
          :platform-administrator="workspace.hasRole('platform_admin') || workspace.hasRole('developer')"
          :audit-entries="workspace.auditEntries.value"
          :folder-template="workspace.folderTemplate.value"
          :pending="workspace.operationPending.value"
          :on-save-crm-integration-settings="workspace.saveCrmIntegrationSettings"
          :on-test-crm-integration="workspace.testCrmIntegration"
          :on-sync-crm-customers="workspace.syncCrmCustomers"
          :on-save-settings="workspace.saveSystemSettings"
          :on-save-equipment-type="workspace.saveEquipmentType"
          :on-update-counters="workspace.updateOrganizationCounters"
          :on-save-organization="workspace.saveProjectOrganization"
          :on-save-unit="workspace.saveOrganizationUnit"
          :on-update-memberships="workspace.updateOrganizationMemberships"
          :on-update-managers="workspace.updateOrganizationUnitManagers"
          :on-save-user="workspace.saveUser"
          :on-reset-user-password="workspace.resetUserPassword"
          :on-save-folder-template="workspace.saveFolderTemplate"
          :on-update-role-permissions="workspace.updateRolePermissions"
          :on-create-role="workspace.createRole"
          :on-delete-role="workspace.deleteRole"
          @update-active-organization-id="setSystemOrganizationId"
          @refresh-audit="runOperation(workspace.loadAuditEntries, '全局审计已刷新')"
        />
        <section v-else-if="activeView === 'workspace'" class="pdm-project-workspace">
          <ProjectWorkspaceHeader :project="workspace.project.value" :projects="workspace.projects.value" :active-tab="projectTab" :active-project-document-status="activeProjectDocumentStatus" :active-document-counts="workspace.documentFilterCounts.value" :current-username="workspace.currentUsername.value" :switching-project-id="switchingProjectId" @back="openProjectList" @switch="switchProject" @tab="openProjectTab">
            <div class="pdm-project-tab-content">
            <BomManager v-if="mountedBomProjectId === workspace.project.value.id" v-show="projectTab === 'bom'" :source-data="workspace.bomSourceData.value" :standard="workspace.standardBom.value" :non-standard="workspace.nonStandardBom.value" :unclassified="workspace.unclassifiedBom.value" :electrical="workspace.electricalBom.value" :documents="workspace.managedDocuments.value" :document-relations="workspace.documentRelations.value" :validation-rules="workspace.systemSettings.value.validationRules" :release-change-reason-types="workspace.systemSettings.value.releaseChangeReasonTypes" :declarations="workspace.bomEmptyDeclarations.value" :versions="workspace.bomVersions.value" :baselines="workspace.bomBaselines.value" :release-packages="workspace.releasePackages.value" :username="workspace.currentUsername.value" :upload-progress="workspace.uploadProgress.value" :operation-error="workspace.operationError.value" :can-manage-release="workspace.hasPermission('release.manage')" :can-decide-approval="workspace.hasPermission('approval.decide')" :can-emergency-decide="workspace.hasPermission('approval.emergency-substitute')" :requested-release-package-id="requestedReleasePackageId" :pending="workspace.operationPending.value" :editable="workspace.hasPermission('bom.edit')" :token="workspace.getAccessToken()" :project-id="workspace.project.value.id" :project="workspace.project.value" :projects="workspace.projects.value" @dirty-change="bomHasUnsavedChanges = $event" @save="(kind, items) => runOperation(() => workspace.saveBomItems(kind, items), 'BOM已保存；CAD来源物料的变更已进入SolidWorks待写回队列')" @import="(kind, file) => runOperation(() => workspace.importBomFile(kind, file), 'BOM已导入并保存')" @export="(kind) => runOperation(() => workspace.exportBomFile(kind), 'BOM已导出')" @generate="generateBom" @resolve="(itemId, action, targetKind) => runOperation(() => workspace.resolveBomItem(itemId, action, targetKind), '待处理项已更新，保存BOM后再写回SolidWorks')" @batch-retain="itemIds => runOperation(() => workspace.retainBomItems(itemIds), '所选待处理BOM项已确认保留')" @batch-update="(input) => runOperation(() => workspace.batchUpdateBomItems(input), 'BOM属性已更新，保存BOM后再写回SolidWorks')" @batch-delete="(itemIds, reason) => runOperation(() => workspace.batchDeleteBomItems(itemIds, reason), '所选BOM物料已移入回收站')" @batch-restore="(itemIds, mode) => runOperation(() => workspace.batchRestoreBomItems(itemIds, mode), mode === 'AsManual' ? '所选物料已转为人工物料并恢复' : '所选BOM物料已恢复')" @restore-source="(itemIds) => runOperation(() => workspace.restoreBomItemsFromSource(itemIds), '所选BOM属性已恢复为最新图档源数据；分类与排序保持不变')" @release-create="(input) => runOperation(() => workspace.createPackage(input), '发布草稿已创建，范围与审批模板已固化')" @release-upload="(releasePackageId, file) => runOperation(() => workspace.uploadPackageFile(releasePackageId, file), '发包文件已上传并通过SHA-256校验')" @release-submit="releasePackageId => runOperation(() => workspace.submitPackage(releasePackageId), '发布包已提交审批')" @release-withdraw="withdrawCurrentPackage" @release-decide="(taskId, decision, comment) => runOperation(() => workspace.decideApprovalTask(taskId, decision, comment), decision === 'Approved' ? '审批已流转' : '发布包已驳回')" @release-transfer="(taskId, targetUsername, comment) => runOperation(() => workspace.transferApprovalTask(taskId, targetUsername, comment), '审批已转交')" @release-emergency-decide="(taskId, decision, reason) => runOperation(() => workspace.emergencyDecideApprovalTask(taskId, decision, reason), decision === 'Approved' ? '当前节点已紧急代批并继续流转' : '当前节点已紧急代驳回')" @release-request-handled="requestedReleasePackageId = ''" @material-code-changed="workspace.reload(workspace.project.value.id)" />
            <WorkbenchHome
              v-if="projectTab === 'overview'"
              :project="workspace.project.value"
              :projects="workspace.projects.value"
              :users="workspace.users.value"
              :selected="workspace.selectedNode.value"
              :current-username="workspace.currentUsername.value"
              :has-documents="workspace.hasDocuments.value"
              :document-count="workspace.normalCount.value + workspace.warningCount.value"
              :model-count="workspace.documentFilterCounts.value.model"
              :drawing-count="workspace.documentFilterCounts.value.drawing"
              :warning-count="workspace.warningCount.value"
              :standard-count="workspace.standardBom.value.filter(item => !item.manuallyExcluded && !item.pendingClassification).length"
              :non-standard-count="workspace.nonStandardBom.value.filter(item => !item.manuallyExcluded && !item.pendingClassification).length"
              :electrical-count="workspace.electricalBom.value.length"
              :standard-item-ids="workspace.standardBom.value.flatMap(item => item.id && !item.manuallyExcluded && !item.pendingClassification ? [item.id] : [])"
              :non-standard-item-ids="workspace.nonStandardBom.value.flatMap(item => item.id && !item.manuallyExcluded && !item.pendingClassification ? [item.id] : [])"
              :electrical-item-ids="workspace.electricalBom.value.flatMap(item => item.id && !item.manuallyExcluded ? [item.id] : [])"
              :drawing-reviews="workspace.drawingReviews.value"
              :material-applications="workspace.materialCodeApplications.value"
              :release-packages="workspace.releasePackages.value"
              :release-package="workspace.releasePackage.value"
              :organization-directory="workspace.organizationDirectory.value"
              :pending="workspace.operationPending.value"
              :on-update-main-staffing="workspace.updateMainProjectStaffing"
              :on-update-designers="workspace.updateChildProjectDesigners"
              @documents="openProjectTab('documents')"
              @bom="openProjectTab('bom')"
            />
            <ProjectFileLibrary v-else-if="projectTab === 'files'" :project-id="workspace.project.value.id" :token="workspace.getAccessToken()" :folders="workspace.projectFolders.value" :documents="workspace.managedDocuments.value" :users="workspace.users.value" :roles="workspace.rolePermissionDirectory.value.roles" :administrator="workspace.hasPermission('settings.folder.manage')" :pending="workspace.operationPending.value" :on-update-permissions="workspace.updateProjectFolderPermissions" :on-reload="() => workspace.reload(workspace.project.value.id)" />
            <section v-else-if="projectTab === 'documents'" class="pdm-document-workspace">
              <section v-if="!workspace.hasDocuments.value" class="pdm-panel pdm-workspace-state">
                <h1>项目尚未关联CAD图纸</h1><p>请在SolidWorks插件中选择“{{ workspace.project.value.code }} · {{ workspace.project.value.name }}”，再提交整套装配存档。</p>
              </section>
              <template v-else>
              <WorkspaceExplorerBar :project="workspace.project.value" :selected="workspace.selectedNode.value" :local-state="selectedWorkspaceLocalState" :desktop-available="desktopAvailable" @open-folder="openWorkspaceFolder" />
              <div class="pdm-workspace" :class="{ 'has-drawing-review': drawingReviewPanelOpen }">
                <DocumentTree v-model:query="workspace.searchQuery.value" :filter="workspace.documentFilter.value" :root="workspace.filteredTree.value" :drawings="workspace.filteredDrawings.value" :selected-id="workspace.selectedNode.value.id" :all-count="workspace.documentFilterCounts.value.all" :model-count="workspace.documentFilterCounts.value.model" :drawing-count="workspace.documentFilterCounts.value.drawing" :warning-count="workspace.warningCount.value" :can-edit="workspace.hasPermission('document.edit')" :review-states="drawingReviewStates" :local-states="workspaceLocalStates" :refreshing="workspace.loading.value || workspaceLocalRefreshing" @update:filter="workspace.setDocumentFilter" @select="workspace.selectNode" @refresh="refreshDocumentTree" @open="workspace.openDocument" @open-folder="openWorkspaceFolder" />
                <section class="pdm-stage">
                  <div class="pdm-preview-layout">
                    <PreviewWorkspace :selected="workspace.selectedNode.value" :related="workspace.relatedNodes.value" :bom-item="workspace.selectedBomItem.value" :current-username="workspace.currentUsername.value" :can-manage-lifecycle="workspace.hasPermission('release.manage')" :can-edit-documents="workspace.hasPermission('document.edit')" :desktop-available="desktopAvailable" :access-token="workspace.getAccessToken()" :project-id="workspace.project.value.id" :obscured="workspace.versionDrawerOpen.value || workspace.whereUsedDrawerOpen.value" :review-panel-open="drawingReviewPanelOpen" :review-status="selectedDrawingReviewStatus.label" :review-status-tone="selectedDrawingReviewStatus.tone" :review-version-id="selectedDrawingReviewVersionId" :review-revision="selectedDrawingReviewRevision" :can-writeback-review-properties="canWritebackSelectedDrawingReview" @open="workspace.openDocument" @preview="workspace.previewDocument" @related="workspace.selectRelatedNode" @review="toggleDrawingReviewPanel" @more="workspace.openVersionDrawer()" @where-used="workspace.openWhereUsed" @obsolete="obsoleteSelectedDocument">
                      <DrawingReviewPanel
                        v-if="drawingReviewPanelOpen && !desktopAvailable"
                        v-model:package-id="drawingReviewPackageId"
                        :packages="workspace.drawingReviews.value"
                        :candidates="workspace.drawingReviewCandidates.value"
                        :selected-document-id="workspace.selectedNode.value.documentId"
                        :current-username="workspace.currentUsername.value"
                        :pending="workspace.operationPending.value"
                        :can-submit="workspace.hasPermission('drawing-review.submit')"
                        :can-manage-withdraw="canManageDrawingReviewWithdrawal"
                        :can-annotate="workspace.hasPermission('drawing-review.annotate')"
                        :can-decide="workspace.hasPermission('drawing-review.decide')"
                        :allow-self-review="workspace.hasRole('developer')"
                        :desktop-available="desktopAvailable"
                        @close="drawingReviewPanelOpen = false"
                        @create="modelDocumentIds => runOperation(() => createDrawingReviewFromDocuments(modelDocumentIds), '图纸审核单已创建，所选3D和2D版本已冻结')"
                        @refresh="runOperation(workspace.refreshDrawingReviews, '图纸审核状态已刷新')"
                        @refresh-candidates="runOperation(workspace.refreshDrawingReviews, '审核范围已刷新')"
                        @withdraw="(packageId, reason) => runOperation(() => workspace.withdrawDrawingReview(packageId, reason), '图纸审核已撤销，编辑锁已释放')"
                        @select-document="selectDrawingReviewDocument"
                        @add-markup="(packageId, input) => runOperation(() => workspace.addDrawingReviewMarkup(packageId, input), '图纸批注已保存')"
                        @resolve-markup="(packageId, markupId) => runOperation(() => workspace.resolveDrawingReviewMarkup(packageId, markupId), '图纸批注已关闭')"
                        @decide="(packageId, itemId, target, decision, comment) => runOperation(() => workspace.decideDrawingReviewTarget(packageId, itemId, target, decision, comment), decision === 'Approve' ? '审核结果已记录' : '图纸已退回修改')"
                      />
                    </PreviewWorkspace>
                  </div>
                </section>
              </div>
              </template>
            </section>
            <ProjectVersions v-else-if="projectTab === 'versions'" :versions="workspace.projectVersions.value" :pending="workspace.operationPending.value" @refresh="runOperation(workspace.loadProjectVersions, '项目版本已刷新')" @open="openVersionDocument" />
            <ReleaseOverview v-else-if="projectTab === 'release'" :release-packages="workspace.releasePackages.value" :versions="workspace.bomVersions.value" :baselines="workspace.bomBaselines.value" @open="releasePackageId => openReleasePackage(workspace.project.value.id, releasePackageId)" />
            <AuditLog v-else-if="projectTab === 'records'" :entries="workspace.projectAuditEntries.value" hide-heading @refresh="runOperation(workspace.loadProjectAuditEntries, '项目记录已刷新')" />
            </div>
          </ProjectWorkspaceHeader>
        </section>
        </template>
        </main>
      </section>
    </div>

    <el-drawer v-if="workspace.authenticated.value" v-model="workspace.versionDrawerOpen.value" title="图档历史版本对比" size="680px">
      <div class="pdm-version-summary">
        <strong>{{ workspace.selectedNode.value.drawingNumber }} · {{ workspace.selectedNode.value.version }}</strong>
        <span>{{ workspace.selectedNode.value.fileName }} · 历史版本永久不可变</span>
      </div>
      <div v-if="workspace.versionLoading.value" class="pdm-empty-info">正在读取版本与快照差异…</div>
      <p v-else-if="workspace.versionError.value" class="pdm-empty-info">{{ workspace.versionError.value }}</p>
      <p v-else-if="workspace.versions.value.length === 0" class="pdm-empty-info">该图档尚无版本记录。</p>
      <template v-else>
        <div class="pdm-version-selectors">
          <label>左侧版本<el-select v-model="workspace.leftVersionId.value" @change="workspace.compareVersions"><el-option v-for="version in workspace.versions.value" :key="version.id" :label="`${version.revision.display} · ${displayUserName(version.createdBy)} · ${new Date(version.createdAt).toLocaleString()}`" :value="version.id" /></el-select></label>
          <label>右侧版本<el-select v-model="workspace.rightVersionId.value" @change="workspace.compareVersions"><el-option v-for="version in workspace.versions.value" :key="version.id" :label="`${version.revision.display} · ${displayUserName(version.createdBy)} · ${new Date(version.createdAt).toLocaleString()}`" :value="version.id" /></el-select></label>
        </div>
        <div class="pdm-version-actions"><button v-if="desktopAvailable" type="button" class="pdm-secondary-action" :disabled="!workspace.leftVersionId.value" @click="workspace.openDocument(workspace.selectedNode.value, 'SpecificReadOnly', workspace.leftVersionId.value)">SolidWorks只读打开左侧</button><button v-if="desktopAvailable" type="button" class="pdm-secondary-action" @click="workspace.openVersionFile(workspace.leftVersionId.value, false)">只读预览左侧</button><button type="button" class="pdm-secondary-action" @click="workspace.openVersionFile(workspace.leftVersionId.value, true)">下载左侧</button></div>
        <div v-if="workspace.versionComparison.value" class="pdm-diff-sections">
          <section><h3>版本信息</h3><p>左：{{ workspace.versionComparison.value.left.revision.display }} · {{ versionStatus(workspace.versionComparison.value.left.status) }} · {{ displayUserName(workspace.versionComparison.value.left.createdBy) }} · {{ new Date(workspace.versionComparison.value.left.createdAt).toLocaleString() }} · {{ workspace.versionComparison.value.left.changeNote }}</p><p>右：{{ workspace.versionComparison.value.right.revision.display }} · {{ versionStatus(workspace.versionComparison.value.right.status) }} · {{ displayUserName(workspace.versionComparison.value.right.createdBy) }} · {{ new Date(workspace.versionComparison.value.right.createdAt).toLocaleString() }} · {{ workspace.versionComparison.value.right.changeNote }}</p></section>
          <section><h3>属性差异（{{ workspace.versionComparison.value.propertyChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.propertyChanges" :key="`p-${index}`">【{{ propertyChangeKind(change.kind) }}】{{ change.name }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.propertyChanges.length">无变化</p></section>
          <section><h3>引用树差异（{{ workspace.versionComparison.value.referenceChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.referenceChanges" :key="`r-${index}`">【{{ referenceChangeKind(change.kind) }}】{{ change.instancePath }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.referenceChanges.length">无变化</p></section>
          <section><h3>BOM差异（{{ workspace.versionComparison.value.bomChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.bomChanges" :key="`b-${index}`">【{{ bomChangeKind(change.kind) }}】{{ change.drawingNumber }} · {{ change.field }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.bomChanges.length">无变化</p></section>
        </div>
        <div class="pdm-version-restore"><el-input v-model="restoreNote" maxlength="500" placeholder="填写恢复说明" /><button type="button" class="pdm-primary-action" @click="restoreSelectedVersion">从左侧版本创建新工作版本</button><small>不会覆盖当前文件，也不会修改历史版本。</small></div>
      </template>
    </el-drawer>

    <el-drawer v-if="workspace.authenticated.value" v-model="workspace.whereUsedDrawerOpen.value" title="使用位置" size="680px">
      <div class="pdm-version-summary">
        <strong>{{ workspace.selectedNode.value.drawingNumber }} · {{ workspace.selectedNode.value.name }}</strong>
        <span>按当前各项目最新引用快照计算，不读取用户本地临时装配。</span>
      </div>
      <div v-if="workspace.whereUsedLoading.value" class="pdm-empty-info">正在计算反向引用…</div>
      <p v-else-if="workspace.whereUsedError.value" class="pdm-empty-info">{{ workspace.whereUsedError.value }}</p>
      <p v-else-if="workspace.whereUsed.value.length === 0" class="pdm-empty-info">当前受控结构中没有装配体引用该图档。</p>
      <table v-else class="pdm-data-table pdm-where-used-table">
        <thead><tr><th>项目</th><th>父装配体</th><th>版本/状态</th><th>配置/数量</th><th></th></tr></thead>
        <tbody><tr v-for="usage in workspace.whereUsed.value" :key="`${usage.projectId}:${usage.parentDocumentId}:${usage.instancePath}`"><td>{{ usage.projectCode }}<small>{{ usage.projectName }}</small></td><td>{{ usage.parentDrawingNumber }}<small>{{ usage.parentName }}</small></td><td>{{ usage.parentRevision.display }} / {{ usage.parentState }}</td><td>{{ usage.configuration || '默认' }} / {{ usage.quantity }}</td><td><button type="button" class="pdm-link-button" @click="openWhereUsedParent(usage.projectId, usage.parentDocumentId)">定位</button></td></tr></tbody>
      </table>
    </el-drawer>

  </div>
</template>
