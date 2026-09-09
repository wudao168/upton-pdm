<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { postDesktopMessage } from '../api'

interface DesktopSettingsDetail {
  available?: boolean
  startWithWindows?: boolean
  workspaceRoot?: string
  defaultWorkspaceRoot?: string
  error?: string
  message?: string
}

interface WorkspaceMaintenanceDetail {
  available?: boolean
  workingFiles?: number
  workingBytes?: number
  snapshotFiles?: number
  snapshotBytes?: number
  recoveryFiles?: number
  recoveryBytes?: number
  error?: string
  message?: string
}

const available = ref(false)
const startWithWindows = ref(true)
const workspaceRoot = ref('')
const savedWorkspaceRoot = ref('')
const defaultWorkspaceRoot = ref('')
const pending = ref(false)
const maintenancePending = ref(false)
const usage = ref<WorkspaceMaintenanceDetail>({})

function formatBytes(bytes = 0) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`
  return `${(bytes / 1024 ** 3).toFixed(1)} GB`
}

function receiveDesktopSettings(event: Event) {
  const detail = (event as CustomEvent<DesktopSettingsDetail>).detail ?? {}
  available.value = detail.available === true
  startWithWindows.value = detail.startWithWindows === true
  savedWorkspaceRoot.value = detail.workspaceRoot ?? ''
  workspaceRoot.value = savedWorkspaceRoot.value
  defaultWorkspaceRoot.value = detail.defaultWorkspaceRoot ?? ''
  pending.value = false
  if (detail.error) ElMessage.error(detail.error)
  else if (detail.message) ElMessage.success(detail.message)
}

function receiveSelectedFolder(event: Event) {
  const detail = (event as CustomEvent<{ workspaceRoot?: string }>).detail
  if (detail?.workspaceRoot) workspaceRoot.value = detail.workspaceRoot
}

function receiveWorkspaceMaintenance(event: Event) {
  const detail = (event as CustomEvent<WorkspaceMaintenanceDetail>).detail ?? {}
  usage.value = detail
  maintenancePending.value = false
  if (detail.error) ElMessage.error(detail.error)
  else if (detail.message) ElMessage.success(detail.message)
}

function browseWorkspaceRoot() {
  postDesktopMessage('workspace-folder-browse')
}

function saveWorkspaceRoot() {
  if (!workspaceRoot.value.trim()) {
    ElMessage.warning('请选择PLM受控工作区')
    return
  }
  pending.value = true
  postDesktopMessage('desktop-settings-save', { workspaceRoot: workspaceRoot.value.trim() })
}

function restoreDefaultWorkspaceRoot() {
  workspaceRoot.value = defaultWorkspaceRoot.value
}

function toggleStartWithWindows() {
  postDesktopMessage('desktop-settings-save', { startWithWindows: !startWithWindows.value })
}

function refreshWorkspaceUsage() {
  maintenancePending.value = true
  postDesktopMessage('workspace-maintenance-request')
}

async function clearReusableCache() {
  try {
    await ElMessageBox.confirm('仅删除未被占用、可从PLM重新获取的只读快照和下载暂存。项目工作文件与恢复副本不会删除。', '清理只读缓存', {
      confirmButtonText: '确认清理',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch { return }
  maintenancePending.value = true
  postDesktopMessage('workspace-cache-clean')
}

onMounted(() => {
  window.addEventListener('pdm-desktop-settings', receiveDesktopSettings)
  window.addEventListener('pdm-workspace-folder-selected', receiveSelectedFolder)
  window.addEventListener('pdm-workspace-maintenance', receiveWorkspaceMaintenance)
  postDesktopMessage('desktop-settings-request')
  postDesktopMessage('workspace-maintenance-request')
})

onBeforeUnmount(() => {
  window.removeEventListener('pdm-desktop-settings', receiveDesktopSettings)
  window.removeEventListener('pdm-workspace-folder-selected', receiveSelectedFolder)
  window.removeEventListener('pdm-workspace-maintenance', receiveWorkspaceMaintenance)
})
</script>

<template>
  <section class="pdm-project-manager" aria-label="客户端设置">
    <section class="pdm-panel pdm-manager-panel">
      <header class="pdm-manager-heading"><div><h2>PLM受控工作区</h2><p>客户端和SolidWorks插件共用此位置；日常打开、编辑和刷新都在工作区页面完成。</p></div></header>
      <label class="pdm-client-workspace-label">
        存储位置（通常无需修改）
        <span class="pdm-client-workspace-row"><input v-model="workspaceRoot" :disabled="pending || !available" aria-label="PLM受控工作区"><button type="button" class="pdm-secondary-action" :disabled="pending || !available" @click="browseWorkspaceRoot">选择位置…</button></span>
        <small>该目录由PLM管理，请勿在资源管理器中改名、移动或删除内部文件。修改只影响后续获取，不会移动或删除旧工作区。</small>
      </label>
      <div class="pdm-settings-actions pdm-client-settings-actions">
        <button type="button" class="pdm-secondary-action" :disabled="pending || !available || workspaceRoot === defaultWorkspaceRoot" @click="restoreDefaultWorkspaceRoot">恢复默认</button>
        <button type="button" class="pdm-primary-action" :disabled="pending || !available || !workspaceRoot.trim() || workspaceRoot === savedWorkspaceRoot" @click="saveWorkspaceRoot">{{ pending ? '正在保存…' : '保存工作区' }}</button>
      </div>
      <div class="pdm-workspace-usage" aria-label="工作区用量">
        <article><small>项目工作文件</small><strong>{{ usage.workingFiles ?? 0 }} 个 · {{ formatBytes(usage.workingBytes) }}</strong><span>不自动清理</span></article>
        <article><small>可重建只读缓存</small><strong>{{ usage.snapshotFiles ?? 0 }} 个 · {{ formatBytes(usage.snapshotBytes) }}</strong><span>可安全清理</span></article>
        <article :class="{ 'has-recovery': (usage.recoveryFiles ?? 0) > 0 }"><small>异常恢复副本</small><strong>{{ usage.recoveryFiles ?? 0 }} 个 · {{ formatBytes(usage.recoveryBytes) }}</strong><span>{{ (usage.recoveryFiles ?? 0) > 0 ? '需管理员核对' : '无待处理' }}</span></article>
      </div>
      <div class="pdm-settings-actions pdm-client-settings-actions">
        <button type="button" class="pdm-secondary-action" :disabled="maintenancePending || !available" @click="refreshWorkspaceUsage">刷新用量</button>
        <button type="button" class="pdm-secondary-action" :disabled="maintenancePending || !available || (usage.snapshotFiles ?? 0) === 0" @click="clearReusableCache">清理只读缓存</button>
      </div>
    </section>

    <section class="pdm-panel pdm-manager-panel">
      <header class="pdm-manager-heading"><div><h2>客户端常驻</h2><p>关闭窗口后，客户端继续在Windows右下角通知区域运行。</p></div></header>
      <div class="pdm-setting-list">
        <article><div><small>启动方式</small><strong>随电脑启动</strong><small>默认开启。启动后直接进入通知区域；双击PLM图标恢复窗口，右键图标可退出。</small></div><button type="button" class="pdm-secondary-action" :disabled="!available" @click="toggleStartWithWindows">{{ startWithWindows ? '已开启' : '已关闭' }}</button></article>
      </div>
    </section>
  </section>
</template>
