<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { GitCompareArrows, Send } from '@lucide/vue'
import { ref } from 'vue'
import AppHeader from './components/AppHeader.vue'
import ApprovalPanel from './components/ApprovalPanel.vue'
import AuditLog from './components/AuditLog.vue'
import BomManager from './components/BomManager.vue'
import BomSummary from './components/BomSummary.vue'
import DocumentTree from './components/DocumentTree.vue'
import CustomerManagement from './components/CustomerManagement.vue'
import LoginView from './components/LoginView.vue'
import PreviewWorkspace from './components/PreviewWorkspace.vue'
import ProjectManager from './components/ProjectManager.vue'
import ReleaseCenter from './components/ReleaseCenter.vue'
import SideNav from './components/SideNav.vue'
import StorageSettings from './components/StorageSettings.vue'
import WorkbenchHome from './components/WorkbenchHome.vue'
import { usePdmWorkspace } from './composables/usePdmWorkspace'
import type { PreviewMode } from './types'

const workspace = usePdmWorkspace()
type NavKey = 'projects' | 'customers' | 'workbench' | 'documents' | 'bom' | 'approvals' | 'release' | 'changes' | 'audit' | 'settings'
const activeView = ref<'projects' | 'customers' | 'workbench' | 'documents' | 'bom' | 'release' | 'audit' | 'settings'>('projects')
const activeNav = ref<NavKey>('projects')
const restoreNote = ref('从历史版本恢复生成新的工作版本')

function openDocuments(mode: PreviewMode = 'model') {
  activeView.value = 'documents'
  activeNav.value = mode === 'bom' ? 'bom' : 'documents'
  workspace.previewMode.value = mode
}

function submitApproval() {
  if (!workspace.releasePackage.value) {
    ElMessage.warning('当前项目暂无发布包，暂不能提交审批')
    return
  }
  activeView.value = 'release'
  activeNav.value = 'approvals'
}

async function handleNavigation(key: NavKey) {
  if (key === 'projects') {
    activeView.value = 'projects'
    activeNav.value = key
    return
  }
  if (key === 'customers') {
    activeView.value = 'customers'
    activeNav.value = key
    return
  }
  if (key === 'settings') {
    activeView.value = 'settings'
    activeNav.value = key
    return
  }
  if (!workspace.project.value.id) {
    ElMessage.warning('请先创建项目或从项目中心选择一个项目')
    activeView.value = 'projects'
    activeNav.value = 'projects'
    return
  }
  if (key === 'workbench') {
    activeView.value = 'workbench'
    activeNav.value = key
    return
  }
  if (key === 'documents') {
    openDocuments('model')
    return
  }
  if (key === 'bom') {
    activeView.value = 'bom'
    activeNav.value = key
    return
  }
  if (key === 'approvals' || key === 'release') {
    activeView.value = 'release'
    activeNav.value = key
    return
  }
  if (key === 'changes') {
    openDocuments('model')
    activeNav.value = key
    await workspace.openVersionDrawer()
    return
  }
  if (key === 'audit') {
    activeView.value = 'audit'
    activeNav.value = key
    await workspace.loadAuditEntries()
    return
  }
}

async function openManagedProject(projectId: string) {
  try {
    await workspace.selectProject(projectId)
    openDocuments('model')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '项目加载失败')
  }
}

async function runOperation(action: () => Promise<unknown>, success: string) {
  try { await action(); ElMessage.success(success) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '操作失败') }
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
</script>

<template>
  <div class="pdm-app-shell">
    <AppHeader
      :online="workspace.serviceOnline.value"
      :user-name="workspace.currentUser.value"
      @logout="workspace.logout"
      @notifications="ElMessage.info('当前没有新的系统通知')"
    />
    <LoginView
      v-if="!workspace.authenticated.value"
      :pending="workspace.loginPending.value"
      :error="workspace.loginError.value"
      :online="workspace.serviceOnline.value"
      @submit="workspace.login"
    />
    <div v-else class="pdm-app-body">
      <SideNav
        :active="activeNav"
        :approval-count="workspace.releasePackage.value ? 1 : 0"
        :is-administrator="workspace.currentRole.value === 'Administrator'"
        @navigate="handleNavigation"
      />
      <main class="pdm-main">
        <section v-if="workspace.loading.value && !workspace.ready.value" class="pdm-panel pdm-workspace-state" aria-live="polite">
          <span class="pdm-state-spinner" aria-hidden="true" />
          <h1>正在读取PDM数据</h1>
          <p>正在加载项目、图档引用树、BOM和发布包…</p>
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
          :current-project-id="workspace.project.value.id"
          :can-create="workspace.currentRole.value === 'Administrator' || workspace.currentRole.value === 'Engineer'"
          :can-manage="workspace.currentRole.value === 'Administrator'"
          :pending="workspace.operationPending.value"
          :on-create="workspace.createProject"
          :on-create-subproject="workspace.createSubproject"
          :on-update-counters="workspace.updateOrganizationCounters"
          :on-update-responsibles="workspace.updateProjectResponsibles"
          @select="(projectId) => runOperation(() => workspace.selectProject(projectId), '当前项目已切换')"
          @open="openManagedProject"
        />
        <CustomerManagement
          v-else-if="activeView === 'customers'"
          :customers="workspace.customers.value"
          :pending="workspace.operationPending.value"
          :on-save="workspace.saveCustomer"
        />
        <WorkbenchHome
          v-else-if="activeView === 'workbench'"
          :project="workspace.project.value"
          :selected="workspace.selectedNode.value"
          :has-documents="workspace.hasDocuments.value"
          :document-count="workspace.normalCount.value + workspace.warningCount.value"
          :warning-count="workspace.warningCount.value"
          :mechanical-count="workspace.mechanicalBom.value.length"
          :electrical-count="workspace.electricalBom.value.length"
          :release-package="workspace.releasePackage.value"
          @documents="openDocuments('model')"
          @bom="openDocuments('bom')"
        />
        <BomManager
          v-else-if="activeView === 'bom'"
          :mechanical="workspace.mechanicalBom.value"
          :electrical="workspace.electricalBom.value"
          :pending="workspace.operationPending.value"
          @save="(kind, items) => runOperation(() => workspace.saveBomItems(kind, items), 'BOM已保存')"
          @import="(kind, file) => runOperation(() => workspace.importBomFile(kind, file), 'BOM已导入并保存')"
          @export="(kind) => runOperation(() => workspace.exportBomFile(kind), 'BOM已导出')"
        />
        <ReleaseCenter
          v-else-if="activeView === 'release'"
          :release-package="workspace.releasePackage.value"
          :username="workspace.currentUsername.value"
          :pending="workspace.operationPending.value"
          :progress="workspace.uploadProgress.value"
          :error="workspace.operationError.value"
          @create="(number, reviewer, approver) => runOperation(() => workspace.createPackage(number, reviewer, approver), '发布包草稿已创建，BOM快照已固化')"
          @upload="(file) => runOperation(() => workspace.uploadPackageFile(file), '发包文件已上传并通过SHA-256校验')"
          @submit="runOperation(workspace.submitPackage, '发布包已提交工艺审核')"
          @decide="(taskId, decision, comment) => runOperation(() => workspace.decideApprovalTask(taskId, decision, comment), decision === 'Approved' ? '审批已流转' : '发布包已驳回')"
        />
        <AuditLog v-else-if="activeView === 'audit'" :entries="workspace.auditEntries.value" @refresh="runOperation(workspace.loadAuditEntries, '审计记录已刷新')" />
        <StorageSettings
          v-else-if="activeView === 'settings'"
          :settings="workspace.systemSettings.value"
          :equipment-types="workspace.equipmentTypes.value"
          :pending="workspace.operationPending.value"
          :on-save-settings="workspace.saveSystemSettings"
          :on-save-equipment-type="workspace.saveEquipmentType"
        />
        <section v-else-if="!workspace.hasDocuments.value" class="pdm-panel pdm-workspace-state">
          <h1>项目已创建，尚未关联图纸</h1>
          <p>请打开SolidWorks插件，刷新项目列表，选择“{{ workspace.project.value.code }} · {{ workspace.project.value.name }}”，再提交图纸存档。</p>
          <div class="pdm-state-actions"><button type="button" class="pdm-secondary-action" @click="handleNavigation('projects')">返回项目中心</button></div>
        </section>
        <template v-else>
        <header class="pdm-pagebar">
          <div>
            <div class="pdm-breadcrumb">项目图档 <span>/</span> 设计资料中心</div>
            <h1>{{ workspace.project.value.code }} · {{ workspace.project.value.name }}</h1>
            <p :title="workspace.project.value.vaultLocation">项目负责人：{{ workspace.project.value.responsibleUsers.join('、') || '未设置' }}　{{ workspace.project.value.stage }}　图档库：{{ workspace.project.value.vaultName }}</p>
          </div>
          <div class="pdm-page-actions">
            <button type="button" class="pdm-secondary-action" @click="workspace.openVersionDrawer()"><GitCompareArrows :size="16" />版本对比</button>
            <button type="button" class="pdm-primary-action" @click="submitApproval"><Send :size="16" />提交审批</button>
          </div>
        </header>

        <div class="pdm-workspace">
          <DocumentTree
            v-model:query="workspace.searchQuery.value"
            :root="workspace.filteredTree.value"
            :selected-id="workspace.selectedNode.value.id"
            :normal-count="workspace.normalCount.value"
            :warning-count="workspace.warningCount.value"
            @select="workspace.selectNode"
            @refresh="workspace.reload"
            @open="workspace.openDocument"
          />
          <section class="pdm-stage">
            <div class="pdm-preview-layout">
              <PreviewWorkspace
                v-model:mode="workspace.previewMode.value"
                :selected="workspace.selectedNode.value"
                :bom="workspace.mechanicalBom.value"
                @open="workspace.openDocument"
                @preview="workspace.previewDocument"
                @more="workspace.openVersionDrawer()"
              />
              <div class="pdm-side-panels">
                <BomSummary
                  :mechanical="workspace.mechanicalBom.value"
                  :electrical="workspace.electricalBom.value"
                  @open="openDocuments('bom')"
                />
                <ApprovalPanel :release-package="workspace.releasePackage.value" />
              </div>
            </div>
          </section>
        </div>
        </template>
        </template>
      </main>
    </div>

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
        <div class="pdm-version-actions"><button type="button" class="pdm-secondary-action" :disabled="!workspace.leftVersionId.value" @click="workspace.openDocument(workspace.selectedNode.value, 'SpecificReadOnly', workspace.leftVersionId.value)">SolidWorks只读打开左侧</button><button type="button" class="pdm-secondary-action" @click="workspace.openVersionFile(workspace.leftVersionId.value, false)">只读预览左侧</button><button type="button" class="pdm-secondary-action" @click="workspace.openVersionFile(workspace.leftVersionId.value, true)">下载左侧</button></div>
        <div v-if="workspace.versionComparison.value" class="pdm-diff-sections">
          <section><h3>版本信息</h3><p>左：{{ workspace.versionComparison.value.left.revision.display }} · {{ versionStatus(workspace.versionComparison.value.left.status) }} · {{ workspace.versionComparison.value.left.createdBy }} · {{ new Date(workspace.versionComparison.value.left.createdAt).toLocaleString() }} · {{ workspace.versionComparison.value.left.changeNote }}</p><p>右：{{ workspace.versionComparison.value.right.revision.display }} · {{ versionStatus(workspace.versionComparison.value.right.status) }} · {{ workspace.versionComparison.value.right.createdBy }} · {{ new Date(workspace.versionComparison.value.right.createdAt).toLocaleString() }} · {{ workspace.versionComparison.value.right.changeNote }}</p></section>
          <section><h3>属性差异（{{ workspace.versionComparison.value.propertyChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.propertyChanges" :key="`p-${index}`">【{{ propertyChangeKind(change.kind) }}】{{ change.name }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.propertyChanges.length">无变化</p></section>
          <section><h3>引用树差异（{{ workspace.versionComparison.value.referenceChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.referenceChanges" :key="`r-${index}`">【{{ referenceChangeKind(change.kind) }}】{{ change.instancePath }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.referenceChanges.length">无变化</p></section>
          <section><h3>BOM差异（{{ workspace.versionComparison.value.bomChanges.length }}）</h3><ul><li v-for="(change, index) in workspace.versionComparison.value.bomChanges" :key="`b-${index}`">【{{ bomChangeKind(change.kind) }}】{{ change.drawingNumber }} · {{ change.field }}：{{ change.previousValue ?? '无' }} → {{ change.currentValue ?? '无' }}</li></ul><p v-if="!workspace.versionComparison.value.bomChanges.length">无变化</p></section>
        </div>
        <div class="pdm-version-restore"><el-input v-model="restoreNote" maxlength="500" placeholder="填写恢复说明" /><button type="button" class="pdm-primary-action" @click="restoreSelectedVersion">从左侧版本创建新工作版本</button><small>不会覆盖当前文件，也不会修改历史版本。</small></div>
      </template>
    </el-drawer>

  </div>
</template>
