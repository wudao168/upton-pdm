<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import AppHeader from './components/AppHeader.vue'
import AuditLog from './components/AuditLog.vue'
import BomManager from './components/BomManager.vue'
import ClientSettings from './components/ClientSettings.vue'
import DocumentTree from './components/DocumentTree.vue'
import ProjectFileLibrary from './components/ProjectFileLibrary.vue'
import LoginView from './components/LoginView.vue'
import MaterialManagement from './components/MaterialManagement.vue'
import MyTasks from './components/MyTasks.vue'
import PreviewWorkspace from './components/PreviewWorkspace.vue'
import ProjectManager from './components/ProjectManager.vue'
import ProjectVersions from './components/ProjectVersions.vue'
import ProjectWorkspaceHeader from './components/ProjectWorkspaceHeader.vue'
import type { ProjectTab } from './components/ProjectWorkspaceHeader.vue'
import ReleaseCenter from './components/ReleaseCenter.vue'
import SideNav from './components/SideNav.vue'
import SystemManagement from './components/SystemManagement.vue'
import WorkbenchHome from './components/WorkbenchHome.vue'
import { usePdmWorkspace } from './composables/usePdmWorkspace'

const workspace = usePdmWorkspace()
const loginVisible = ref(false)
type PdmTheme = 'a' | 'c' | 'o'
type NavKey = 'project-center' | 'projects' | 'materials' | 'tasks' | 'client-settings' | 'admin'
type ActiveView = NavKey | 'workspace'
const activeView = ref<ActiveView>('projects')
const activeNav = computed<NavKey>(() => activeView.value === 'workspace' ? 'project-center' : activeView.value)
const canManageSystem = computed(() => ['settings.customer.manage', 'settings.organization.manage', 'settings.folder.manage', 'settings.storage.manage', 'system.role.view', 'audit.view'].some(workspace.hasPermission))
const desktopAvailable = Boolean(window.chrome?.webview)
const projectTab = ref<ProjectTab>('overview')
const restoreNote = ref('从历史版本恢复生成新的工作版本')
const projectTabMemoryKey = 'upton-pdm-project-tabs'
const projectCenterMemoryKey = 'upton-pdm-project-center'
const activeNavigationMemoryKey = 'upton-pdm-active-navigation'
const savedTheme = window.localStorage.getItem('pdm_theme')
const theme = ref<PdmTheme>(savedTheme === 'c' || savedTheme === 'o' ? savedTheme : 'a')
const notificationCount = computed(() => workspace.myApprovalTasks.value.length + workspace.passwordResetTasks.value.length + workspace.editLocks.value.filter(lock => lock.ownedByCurrentUser || lock.releaseRequestedBy || lock.canForceRelease).length)
const companyName = computed(() => workspace.project.value.organizationName || workspace.projectNumberingOptions.value.organizations[0]?.name || '昆山阿普顿自动化系统有限公司')
const activeProjectDocumentStatus = computed(() => {
  const owner = workspace.root.value.checkedOutBy?.trim()
  if (!owner) return '正常'
  const currentUsername = workspace.currentUsername.value.trim()
  if (owner.localeCompare(currentUsername, undefined, { sensitivity: 'accent' }) === 0) return '可编辑'
  return `${owner}编辑中`
})

function selectTheme(value: PdmTheme) {
  theme.value = value
  window.localStorage.setItem('pdm_theme', value)
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
    return tabs[projectId] ?? 'overview'
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
    window.localStorage.setItem(projectCenterMemoryKey, JSON.stringify({ projectId, tab }))
  } catch {
    // Local storage may be unavailable in restricted WebView environments.
  }
}

function readRememberedProjectCenterPage() {
  try {
    const page = JSON.parse(window.localStorage.getItem(projectCenterMemoryKey) ?? 'null') as { projectId?: string; tab?: ProjectTab } | null
    if (page?.projectId && page.tab && supportedProjectTabs.includes(page.tab)) return { projectId: page.projectId, tab: page.tab }
  } catch {
    // Ignore stale or unavailable local storage and use the first accessible project.
  }
  return null
}

function readRememberedNavigation(): NavKey | null {
  try {
    const remembered = window.localStorage.getItem(activeNavigationMemoryKey)
    return ['project-center', 'projects', 'materials', 'tasks', 'client-settings', 'admin'].includes(remembered ?? '')
      ? remembered as NavKey
      : null
  } catch {
    return null
  }
}

function rememberNavigation(key: NavKey) {
  try {
    window.localStorage.setItem(activeNavigationMemoryKey, key)
  } catch {
    // Local storage may be unavailable in restricted WebView environments.
  }
}

async function openProjectTab(tab: ProjectTab) {
  if (!workspace.project.value.canReadContent && tab !== 'overview' && tab !== 'records') tab = 'overview'
  projectTab.value = tab
  if (workspace.project.value.id) rememberProjectTab(workspace.project.value.id, tab)
  if (tab === 'versions') await workspace.loadProjectVersions()
  if (tab === 'records') await workspace.loadProjectAuditEntries()
}

function openProjectList() {
  activeView.value = 'projects'
  rememberNavigation('projects')
}

async function handleNavigation(key: NavKey) {
  if (key === 'project-center') return openProjectCenter(true)
  if (key === 'projects') openProjectList()
  if (key === 'materials') {
    activeView.value = 'materials'
    rememberNavigation(key)
  }
  if (key === 'tasks') {
    activeView.value = 'tasks'
    rememberNavigation(key)
    await workspace.loadMyApprovalTasks()
  }
  if (key === 'client-settings') {
    activeView.value = 'client-settings'
    rememberNavigation(key)
  }
  if (key === 'admin') {
    activeView.value = 'admin'
    rememberNavigation(key)
    if (workspace.hasPermission('audit.view')) await workspace.loadAuditEntries()
  }
}

async function openManagedProject(projectId: string, requestedTab?: ProjectTab) {
  try {
    await workspace.selectProject(projectId)
    activeView.value = 'workspace'
    rememberNavigation('project-center')
    await openProjectTab(requestedTab ?? readRememberedProjectTab(projectId))
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '项目加载失败')
  }
}

type ProjectNavigationRequest = { projectId: string; tab: ProjectTab }
const supportedProjectTabs: ProjectTab[] = ['overview', 'files', 'documents', 'bom', 'versions', 'release', 'records']
let pendingProjectNavigation: ProjectNavigationRequest | null = null
let projectNavigationInProgress = false
let initialPageRestored = false

async function openProjectCenter(resetToOverview = false) {
  rememberNavigation('project-center')
  if (workspace.project.value.id) {
    activeView.value = 'workspace'
    if (resetToOverview) await openProjectTab('overview')
    return
  }

  const remembered = readRememberedProjectCenterPage()
  const target = workspace.projects.value.find(project => project.id === remembered?.projectId)
    ?? workspace.projects.value[0]
  if (!target) {
    activeView.value = 'projects'
    return
  }
  await openManagedProject(target.id, resetToOverview ? 'overview' : target.id === remembered?.projectId ? remembered.tab : 'overview')
}

async function restoreLastPage() {
  if (initialPageRestored
    || pendingProjectNavigation
    || projectNavigationInProgress
    || !workspace.authenticated.value
    || !workspace.ready.value) return
  initialPageRestored = true
  const rememberedNavigation = readRememberedNavigation()
  if (rememberedNavigation === 'projects') {
    openProjectList()
    return
  }
  if (rememberedNavigation === 'materials' || rememberedNavigation === 'tasks') {
    await handleNavigation(rememberedNavigation)
    return
  }
  if (rememberedNavigation === 'client-settings' && desktopAvailable) {
    await handleNavigation(rememberedNavigation)
    return
  }
  if (rememberedNavigation === 'admin' && canManageSystem.value) {
    await handleNavigation(rememberedNavigation)
    return
  }
  await openProjectCenter()
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
    activeView.value = 'projects'
    projectTab.value = 'overview'
    return
  }
  if (!ready) return
  void (async () => {
    await applyPendingProjectNavigation()
    await restoreLastPage()
  })()
})
watch(workspace.loginError, (error) => {
  if (error && !workspace.authenticated.value) loginVisible.value = true
})
onMounted(() => window.addEventListener('pdm-open-project', handleProjectNavigation))
onBeforeUnmount(() => window.removeEventListener('pdm-open-project', handleProjectNavigation))

async function login(username: string, password: string, rememberCredentials: boolean) {
  await workspace.login(username, password, rememberCredentials)
  if (workspace.authenticated.value) loginVisible.value = false
}

async function switchProject(projectId: string) {
  await openManagedProject(projectId, projectTab.value)
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

async function generateBom() {
  try {
    const result = await workspace.generateBomFromDrawings()
    if (result.applied) ElMessage.success('BOM已按最新设计树更新')
    else ElMessage.info('已取消更新，BOM未发生变化')
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

async function withdrawCurrentPackage() {
  try {
    const { value } = await ElMessageBox.prompt('请填写撤回原因。撤回后发布包回到草稿，相关图档恢复为工作中。', '撤回审批', {
      confirmButtonText: '确认撤回', cancelButtonText: '取消', inputPattern: /\S+/, inputErrorMessage: '撤回原因不能为空', type: 'warning',
    })
    await workspace.withdrawPackage(value)
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

async function openWhereUsedParent(projectId: string, parentDocumentId: string) {
  workspace.whereUsedDrawerOpen.value = false
  await openManagedProject(projectId, 'documents')
  if (!workspace.selectDocument(parentDocumentId)) ElMessage.warning('父装配体不在当前设计树中')
}
</script>

<template>
  <div class="pdm-app-shell" :class="`theme-${theme}`">
    <div class="pdm-app-body" :class="{ 'is-guest': !workspace.authenticated.value }">
      <SideNav
        v-if="workspace.authenticated.value"
        :active="activeNav"
        :approval-count="notificationCount"
        :can-manage-system="canManageSystem"
        :desktop-available="desktopAvailable"
        @navigate="handleNavigation"
      />
      <section class="pdm-shell-content">
        <AppHeader
          :online="workspace.serviceOnline.value"
          :user-name="workspace.currentUser.value"
          :username="workspace.currentUsername.value"
          :role="workspace.currentRole.value"
          :company-name="companyName"
          :notification-count="notificationCount"
          :theme="theme"
          :profile="workspace.currentProfile.value"
          :on-save-profile="workspace.saveMyProfile"
          :on-change-password="workspace.changeMyPassword"
          @login="loginVisible = true"
          @logout="workspace.logout"
          @notifications="handleNavigation('tasks')"
          @theme="selectTheme"
        />
        <main v-if="!workspace.authenticated.value" class="pdm-main pdm-guest-home" aria-label="未登录主页" />
        <main v-else class="pdm-main" :class="{ 'is-project-workspace': activeView === 'workspace' }">
        <section v-if="workspace.loading.value && !workspace.ready.value" class="pdm-panel pdm-workspace-state" aria-live="polite">
          <span class="pdm-state-spinner" aria-hidden="true" />
          <h1>正在读取PDM数据</h1>
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
        <ProjectManager
          v-if="activeView === 'projects'"
          :projects="workspace.projects.value"
          :numbering-options="workspace.projectNumberingOptions.value"
          :customers="workspace.customers.value"
          :users="workspace.users.value"
          :organization-directory="workspace.organizationDirectory.value"
          :current-username="workspace.currentUsername.value"
          :administrator="workspace.currentRole.value === 'Administrator'"
          :can-create="workspace.hasPermission('project.create')"
          :can-edit="workspace.currentRole.value === 'Administrator' || workspace.hasPermission('project.edit')"
          :can-delete="workspace.currentRole.value === 'Administrator' || workspace.hasPermission('project.delete')"
          :can-create-subproject="workspace.hasPermission('project.child.create')"
          :pending="workspace.operationPending.value"
          :on-create="workspace.createProject"
          :on-create-subproject="workspace.createSubproject"
          :on-update-project="workspace.updateProject"
          :on-delete-project="workspace.deleteProject"
          :on-update-execution-unit="workspace.updateProjectExecutionUnit"
          :on-update-main-staffing="workspace.updateMainProjectStaffing"
          :on-update-designers="workspace.updateChildProjectDesigners"
          @open="openManagedProject"
        />
        <MyTasks v-else-if="activeView === 'tasks'" :tasks="workspace.myApprovalTasks.value" :locks="workspace.editLocks.value" :password-reset-tasks="workspace.passwordResetTasks.value" :pending="workspace.operationPending.value" :on-request-release="workspace.requestEditLockRelease" :on-force-release="workspace.forceReleaseEditLock" :on-reset-password="workspace.resetRequestedPassword" @refresh="runOperation(workspace.loadMyApprovalTasks, '待办任务已刷新')" @open="(projectId) => openManagedProject(projectId, 'release')" />
        <MaterialManagement
          v-else-if="activeView === 'materials'"
          :token="workspace.getAccessToken()"
          :can-edit="workspace.hasPermission('bom.edit')"
          :can-approve="workspace.hasPermission('release.manage')"
          :can-manage-integration="workspace.hasPermission('settings.storage.manage')"
        />
        <ClientSettings v-else-if="activeView === 'client-settings'" />
        <SystemManagement
          v-else-if="activeView === 'admin'"
          :token="workspace.getAccessToken()"
          :customers="workspace.customers.value"
          :crm-integration-settings="workspace.crmIntegrationSettings.value"
          :settings="workspace.systemSettings.value"
          :equipment-types="workspace.equipmentTypes.value"
          :numbering-options="workspace.projectNumberingOptions.value"
          :organization-directory="workspace.organizationDirectory.value"
          :role-permission-directory="workspace.rolePermissionDirectory.value"
          :permissions="workspace.currentPermissions.value"
          :current-username="workspace.currentUsername.value"
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
          @refresh-audit="runOperation(workspace.loadAuditEntries, '全局审计已刷新')"
        />
        <section v-else-if="activeView === 'workspace'" class="pdm-project-workspace">
          <ProjectWorkspaceHeader :project="workspace.project.value" :projects="workspace.projects.value" :active-tab="projectTab" :active-project-document-status="activeProjectDocumentStatus" :current-username="workspace.currentUsername.value" @back="openProjectList" @switch="switchProject" @tab="openProjectTab">
            <div class="pdm-project-tab-content">
            <WorkbenchHome
              v-if="projectTab === 'overview'"
              :project="workspace.project.value"
              :selected="workspace.selectedNode.value"
              :current-username="workspace.currentUsername.value"
              :has-documents="workspace.hasDocuments.value"
              :document-count="workspace.normalCount.value + workspace.warningCount.value"
              :warning-count="workspace.warningCount.value"
              :standard-count="workspace.standardBom.value.filter(item => !item.manuallyExcluded && !item.pendingClassification).length"
              :non-standard-count="workspace.nonStandardBom.value.filter(item => !item.manuallyExcluded && !item.pendingClassification).length"
              :electrical-count="workspace.electricalBom.value.length"
              :release-package="workspace.releasePackage.value"
              @documents="openProjectTab('documents')"
              @bom="openProjectTab('bom')"
            />
            <ProjectFileLibrary v-else-if="projectTab === 'files'" :folders="workspace.projectFolders.value" :documents="workspace.managedDocuments.value" :users="workspace.users.value" :administrator="workspace.hasPermission('settings.folder.manage')" :pending="workspace.operationPending.value" :on-update-permissions="workspace.updateProjectFolderPermissions" />
            <section v-else-if="projectTab === 'documents'" class="pdm-document-workspace">
              <section v-if="!workspace.hasDocuments.value" class="pdm-panel pdm-workspace-state">
                <h1>项目尚未关联CAD图纸</h1><p>请在SolidWorks插件中选择“{{ workspace.project.value.code }} · {{ workspace.project.value.name }}”，再提交整套装配存档。</p>
              </section>
              <div v-else class="pdm-workspace">
                <DocumentTree v-model:query="workspace.searchQuery.value" :filter="workspace.documentFilter.value" :root="workspace.filteredTree.value" :drawings="workspace.filteredDrawings.value" :selected-id="workspace.selectedNode.value.id" :all-count="workspace.documentFilterCounts.value.all" :model-count="workspace.documentFilterCounts.value.model" :drawing-count="workspace.documentFilterCounts.value.drawing" :warning-count="workspace.warningCount.value" @update:filter="workspace.setDocumentFilter" @select="workspace.selectNode" @refresh="workspace.reload" @open="workspace.openDocument" />
                <section class="pdm-stage"><div class="pdm-preview-layout"><PreviewWorkspace :selected="workspace.selectedNode.value" :related="workspace.relatedNodes.value" :bom-item="workspace.selectedBomItem.value" :current-username="workspace.currentUsername.value" :can-manage-lifecycle="workspace.hasPermission('release.manage')" :desktop-available="desktopAvailable" :obscured="workspace.versionDrawerOpen.value || workspace.whereUsedDrawerOpen.value" @open="workspace.openDocument" @preview="workspace.previewDocument" @related="workspace.selectRelatedNode" @more="workspace.openVersionDrawer()" @where-used="workspace.openWhereUsed" @obsolete="obsoleteSelectedDocument" /></div></section>
              </div>
            </section>
            <BomManager v-else-if="projectTab === 'bom'" :source-data="workspace.bomSourceData.value" :standard="workspace.standardBom.value" :non-standard="workspace.nonStandardBom.value" :unclassified="workspace.unclassifiedBom.value" :electrical="workspace.electricalBom.value" :validation-rules="workspace.systemSettings.value.validationRules" :declarations="workspace.bomEmptyDeclarations.value" :versions="workspace.bomVersions.value" :baselines="workspace.bomBaselines.value" :pending="workspace.operationPending.value" :editable="workspace.hasPermission('bom.edit')" :token="workspace.getAccessToken()" :project-id="workspace.project.value.id" @save="(kind, items) => runOperation(() => workspace.saveBomItems(kind, items), 'BOM已保存；CAD来源物料的变更已进入SolidWorks待写回队列')" @import="(kind, file) => runOperation(() => workspace.importBomFile(kind, file), 'BOM已导入并保存')" @export="(kind) => runOperation(() => workspace.exportBomFile(kind), 'BOM已导出')" @generate="generateBom" @resolve="(itemId, action, targetKind) => runOperation(() => workspace.resolveBomItem(itemId, action, targetKind), '待处理项已更新，保存BOM后再写回SolidWorks')" @batch-update="(input) => runOperation(() => workspace.batchUpdateBomItems(input), 'BOM属性已更新，保存BOM后再写回SolidWorks')" @batch-delete="(itemIds, reason) => runOperation(() => workspace.batchDeleteBomItems(itemIds, reason), '所选BOM物料已移入回收站')" @batch-restore="(itemIds, mode) => runOperation(() => workspace.batchRestoreBomItems(itemIds, mode), mode === 'AsManual' ? '所选物料已转为人工物料并恢复' : '所选BOM物料已恢复')" @restore-source="(itemIds) => runOperation(() => workspace.restoreBomItemsFromSource(itemIds), '所选BOM属性已恢复为最新图档源数据；分类与排序保持不变')" />
            <ProjectVersions v-else-if="projectTab === 'versions'" :versions="workspace.projectVersions.value" :pending="workspace.operationPending.value" @refresh="runOperation(workspace.loadProjectVersions, '项目版本已刷新')" @open="openVersionDocument" />
            <ReleaseCenter v-else-if="projectTab === 'release'" :release-package="workspace.releasePackage.value" :serial-numbers="workspace.project.value.serialNumbers" :username="workspace.currentUsername.value" :pending="workspace.operationPending.value" :progress="workspace.uploadProgress.value" :error="workspace.operationError.value" :can-manage="workspace.hasPermission('release.manage')" :can-decide="workspace.hasPermission('approval.decide')" @create="(number, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, reviewer, approver) => runOperation(() => workspace.createPackage(number, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, reviewer, approver), 'ECN发布草稿已创建，三套BOM版本已固化')" @upload="(file) => runOperation(() => workspace.uploadPackageFile(file), '发包文件已上传并通过SHA-256校验')" @submit="runOperation(workspace.submitPackage, '发布包已提交工艺审核')" @withdraw="withdrawCurrentPackage" @decide="(taskId, decision, comment) => runOperation(() => workspace.decideApprovalTask(taskId, decision, comment), decision === 'Approved' ? '审批已流转' : '发布包已驳回')" />
            <AuditLog v-else :entries="workspace.projectAuditEntries.value" hide-heading @refresh="runOperation(workspace.loadProjectAuditEntries, '项目记录已刷新')" />
            </div>
          </ProjectWorkspaceHeader>
        </section>
        </template>
        </main>
      </section>
    </div>

    <el-dialog v-model="loginVisible" class="pdm-login-dialog" title="登录 PDM" width="460px" :close-on-click-modal="false" destroy-on-close>
      <LoginView
        compact
        :pending="workspace.loginPending.value"
        :error="workspace.loginError.value"
        :online="workspace.serviceOnline.value"
        @submit="login"
      />
    </el-dialog>

    <el-drawer v-model="workspace.versionDrawerOpen.value" title="图档历史版本对比" size="680px">
      <div class="pdm-version-summary">
        <strong>{{ workspace.selectedNode.value.drawingNumber }} · {{ workspace.selectedNode.value.version }}</strong>
        <span>{{ workspace.selectedNode.value.fileName }} · 历史版本永久不可变</span>
      </div>
      <div v-if="workspace.versionLoading.value" class="pdm-empty-info">正在读取版本与快照差异…</div>
      <p v-else-if="workspace.versionError.value" class="pdm-empty-info">{{ workspace.versionError.value }}</p>
      <p v-else-if="workspace.versions.value.length === 0" class="pdm-empty-info">该图档尚无版本记录。</p>
      <template v-else>
        <div class="pdm-version-selectors">
          <label>左侧版本<el-select v-model="workspace.leftVersionId.value" @change="workspace.compareVersions"><el-option v-for="version in workspace.versions.value" :key="version.id" :label="`${version.revision.display} · ${version.createdBy} · ${new Date(version.createdAt).toLocaleString()}`" :value="version.id" /></el-select></label>
          <label>右侧版本<el-select v-model="workspace.rightVersionId.value" @change="workspace.compareVersions"><el-option v-for="version in workspace.versions.value" :key="version.id" :label="`${version.revision.display} · ${version.createdBy} · ${new Date(version.createdAt).toLocaleString()}`" :value="version.id" /></el-select></label>
        </div>
        <div class="pdm-version-actions"><button v-if="desktopAvailable" type="button" class="pdm-secondary-action" :disabled="!workspace.leftVersionId.value" @click="workspace.openDocument(workspace.selectedNode.value, 'SpecificReadOnly', workspace.leftVersionId.value)">SolidWorks只读打开左侧</button><button v-if="desktopAvailable" type="button" class="pdm-secondary-action" @click="workspace.openVersionFile(workspace.leftVersionId.value, false)">只读预览左侧</button><button type="button" class="pdm-secondary-action" @click="workspace.openVersionFile(workspace.leftVersionId.value, true)">下载左侧</button></div>
        <div v-if="workspace.versionComparison.value" class="pdm-diff-sections">
          <section><h3>版本信息</h3><p>左：{{ workspace.versionComparison.value.left.revision.display }} · {{ versionStatus(workspace.versionComparison.value.left.status) }} · {{ workspace.versionComparison.value.left.createdBy }} · {{ new Date(workspace.versionComparison.value.left.createdAt).toLocaleString() }} · {{ workspace.versionComparison.value.left.changeNote }}</p><p>右：{{ workspace.versionComparison.value.right.revision.display }} · {{ versionStatus(workspace.versionComparison.value.right.status) }} · {{ workspace.versionComparison.value.right.createdBy }} · {{ new Date(workspace.versionComparison.value.right.createdAt).toLocaleString() }} · {{ workspace.versionComparison.value.right.changeNote }}</p></section>
          <section><h3>属性差异（{{ workspace.versionComparison.value.propertyChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.propertyChanges" :key="`p-${index}`">【{{ propertyChangeKind(change.kind) }}】{{ change.name }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.propertyChanges.length">无变化</p></section>
          <section><h3>引用树差异（{{ workspace.versionComparison.value.referenceChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.referenceChanges" :key="`r-${index}`">【{{ referenceChangeKind(change.kind) }}】{{ change.instancePath }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.referenceChanges.length">无变化</p></section>
          <section><h3>BOM差异（{{ workspace.versionComparison.value.bomChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.bomChanges" :key="`b-${index}`">【{{ bomChangeKind(change.kind) }}】{{ change.drawingNumber }} · {{ change.field }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.bomChanges.length">无变化</p></section>
        </div>
        <div class="pdm-version-restore"><el-input v-model="restoreNote" maxlength="500" placeholder="填写恢复说明" /><button type="button" class="pdm-primary-action" @click="restoreSelectedVersion">从左侧版本创建新工作版本</button><small>不会覆盖当前文件，也不会修改历史版本。</small></div>
      </template>
    </el-drawer>

    <el-drawer v-model="workspace.whereUsedDrawerOpen.value" title="使用位置" size="680px">
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
