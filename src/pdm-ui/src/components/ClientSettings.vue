<script setup lang="ts">
import { ElMessage } from 'element-plus'
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

const available = ref(false)
const startWithWindows = ref(false)
const workspaceRoot = ref('')
const savedWorkspaceRoot = ref('')
const defaultWorkspaceRoot = ref('')
const pending = ref(false)

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

function browseWorkspaceRoot() {
  postDesktopMessage('workspace-folder-browse')
}

function saveWorkspaceRoot() {
  if (!workspaceRoot.value.trim()) {
    ElMessage.warning('请选择本地缓存工作区')
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

onMounted(() => {
  window.addEventListener('pdm-desktop-settings', receiveDesktopSettings)
  window.addEventListener('pdm-workspace-folder-selected', receiveSelectedFolder)
  postDesktopMessage('desktop-settings-request')
})

onBeforeUnmount(() => {
  window.removeEventListener('pdm-desktop-settings', receiveDesktopSettings)
  window.removeEventListener('pdm-workspace-folder-selected', receiveSelectedFolder)
})
</script>

<template>
  <section class="pdm-project-manager" aria-label="客户端设置">
    <header class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">客户端设置 <span>/</span> 本地工作区</div><h1>客户端设置</h1><p>设置当前Windows用户的PDM本地缓存位置和启动方式。</p></div>
    </header>

    <section class="pdm-panel pdm-manager-panel">
      <header class="pdm-manager-heading"><div><h2>本地缓存工作区</h2><p>获取并编辑、只读打开和本地工作副本将保存在此目录。</p></div></header>
      <label class="pdm-client-workspace-label">
        工作区根目录
        <span class="pdm-client-workspace-row"><input v-model="workspaceRoot" :disabled="pending || !available" aria-label="本地缓存工作区"><button type="button" class="pdm-secondary-action" :disabled="pending || !available" @click="browseWorkspaceRoot">浏览…</button></span>
        <small>修改后仅影响后续获取；不会移动或删除旧缓存，也不会改变当前已打开图档的路径。</small>
      </label>
      <div class="pdm-settings-actions pdm-client-settings-actions">
        <button type="button" class="pdm-secondary-action" :disabled="pending || !available || workspaceRoot === defaultWorkspaceRoot" @click="restoreDefaultWorkspaceRoot">恢复默认</button>
        <button type="button" class="pdm-primary-action" :disabled="pending || !available || !workspaceRoot.trim() || workspaceRoot === savedWorkspaceRoot" @click="saveWorkspaceRoot">{{ pending ? '正在保存…' : '保存工作区' }}</button>
      </div>
    </section>

    <section class="pdm-panel pdm-manager-panel">
      <header class="pdm-manager-heading"><div><h2>客户端常驻</h2><p>关闭窗口后，客户端继续在Windows右下角通知区域运行。</p></div></header>
      <div class="pdm-setting-list">
        <article><div><small>启动方式</small><strong>随电脑启动</strong><small>启动后直接进入通知区域；双击PDM图标恢复窗口，右键图标可退出。</small></div><button type="button" class="pdm-secondary-action" :disabled="!available" @click="toggleStartWithWindows">{{ startWithWindows ? '已开启' : '已关闭' }}</button></article>
      </div>
    </section>
  </section>
</template>
