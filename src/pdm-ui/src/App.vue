<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { GitCompareArrows, Send } from '@lucide/vue'
import { ref } from 'vue'
import AppHeader from './components/AppHeader.vue'
import ApprovalPanel from './components/ApprovalPanel.vue'
import BomSummary from './components/BomSummary.vue'
import DocumentTree from './components/DocumentTree.vue'
import LoginView from './components/LoginView.vue'
import PreviewWorkspace from './components/PreviewWorkspace.vue'
import SideNav from './components/SideNav.vue'
import WorkbenchHome from './components/WorkbenchHome.vue'
import { usePdmWorkspace } from './composables/usePdmWorkspace'
import type { PreviewMode } from './types'

const workspace = usePdmWorkspace()
type NavKey = 'workbench' | 'documents' | 'bom' | 'approvals' | 'release' | 'changes' | 'audit' | 'settings'
const activeView = ref<'workbench' | 'documents'>('documents')
const activeNav = ref<NavKey>('documents')

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
  workspace.submitApproval()
}

function handleNavigation(key: NavKey, label: string) {
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
    openDocuments('bom')
    return
  }
  if (key === 'approvals') {
    activeNav.value = key
    submitApproval()
    return
  }
  ElMessage.info(`${label}将在后续阶段开放`)
}

function confirmApproval() {
  workspace.approvalDialogOpen.value = false
  ElMessage.info('当前客户端已读取真实发布包；审批写入接口接入后开放提交操作')
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
            <button type="button" class="pdm-primary-action" @click="workspace.reload">重新加载</button>
            <button type="button" class="pdm-secondary-action" @click="workspace.logout">重新登录</button>
          </div>
        </section>
        <template v-else-if="workspace.ready.value">
        <WorkbenchHome
          v-if="activeView === 'workbench'"
          :project="workspace.project.value"
          :selected="workspace.selectedNode.value"
          :document-count="workspace.normalCount.value + workspace.warningCount.value"
          :warning-count="workspace.warningCount.value"
          :mechanical-count="workspace.mechanicalBom.value.length"
          :electrical-count="workspace.electricalBom.value.length"
          :release-package="workspace.releasePackage.value"
          @documents="openDocuments('model')"
          @bom="openDocuments('bom')"
        />
        <template v-else>
        <header class="pdm-pagebar">
          <div>
            <div class="pdm-breadcrumb">项目图档 <span>/</span> 设计资料中心</div>
            <h1>{{ workspace.project.value.code }} · {{ workspace.project.value.name }}</h1>
            <p :title="workspace.project.value.vaultLocation">项目负责人：{{ workspace.project.value.owner }}　{{ workspace.project.value.stage }}　图档库：{{ workspace.project.value.vaultName }}</p>
          </div>
          <div class="pdm-page-actions">
            <button type="button" class="pdm-secondary-action" @click="workspace.versionDrawerOpen.value = true"><GitCompareArrows :size="16" />版本对比</button>
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
          />
          <section class="pdm-stage">
            <div class="pdm-preview-layout">
              <PreviewWorkspace
                v-model:mode="workspace.previewMode.value"
                :selected="workspace.selectedNode.value"
                :bom="workspace.mechanicalBom.value"
                @open="workspace.openDocument"
                @fit="ElMessage.success('预览已适合当前窗口')"
                @more="ElMessage.info('更多图档操作将在获取权限和下载接口接入后开放')"
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

    <el-drawer v-model="workspace.versionDrawerOpen.value" title="版本结构对比" size="420px">
      <div class="pdm-version-summary">
        <strong>{{ workspace.selectedNode.value.drawingNumber }} · {{ workspace.selectedNode.value.version }}</strong>
        <span>{{ workspace.selectedNode.value.fileName }} · 服务端当前版本</span>
      </div>
      <p class="pdm-empty-info">历史版本差异接口尚未提供，当前未使用演示差异数据。</p>
    </el-drawer>

    <el-dialog v-model="workspace.approvalDialogOpen.value" title="提交发布包审批" width="520px">
      <div v-if="workspace.releasePackage.value" class="pdm-submit-dialog">
        <p><strong>{{ workspace.releasePackage.value.number }}</strong> 当前关联数据：</p>
        <ul>
          <li>图档结构：{{ workspace.root.value.drawingNumber }} · {{ workspace.root.value.version }}</li>
          <li>机械BOM：{{ workspace.mechanicalBom.value.length }} 项</li>
          <li>电气BOM：{{ workspace.electricalBom.value.length }} 项</li>
          <li>发布状态：{{ workspace.releasePackage.value.state }}</li>
        </ul>
        <p class="pdm-dialog-note">当前阶段为真实数据只读展示，尚未调用审批写入接口。</p>
      </div>
      <template #footer>
        <button type="button" class="pdm-secondary-action" @click="workspace.approvalDialogOpen.value = false">取消</button>
        <button type="button" class="pdm-primary-action" @click="confirmApproval">知道了</button>
      </template>
    </el-dialog>
  </div>
</template>
