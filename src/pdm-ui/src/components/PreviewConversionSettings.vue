<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { getPreviewServerStatuses, listPreviewServers, removePreviewServer, savePreviewServer, testPreviewServer } from '../api'
import { ElMessage } from '../statusMessage'
import type { PreviewAgentProbeResult, PreviewServer, PreviewServerInput, PreviewServerStatus } from '../types'

const props = defineProps<{ token: string; active: boolean }>()
const servers = ref<PreviewServer[]>([])
const loading = ref(false)
const saving = ref(false)
const testing = ref('')
const loadError = ref('')
const actionError = ref('')
const probes = reactive(new Map<string, PreviewAgentProbeResult>())
const statuses = ref(new Map<string, PreviewServerStatus>())
const statusSettled = ref(false)
const statusRefresh = ref(0)
const visible = ref(!document.hidden)
const statusIntervalMs = 5_000
const connectionLabels = { online: '在线', offline: '离线', unready: '未就绪', missing: '暂不可用', checking: '检测中' }
const editOpen = ref(false)
const editing = ref<PreviewServer>()
const deleting = ref<PreviewServer>()
const clearToken = ref(false)
const draft = reactive({ name: '', agentUrl: '', agentToken: '', timeoutMinutes: 30, disabled: false })
const busy = computed(() => loading.value || saving.value || Boolean(testing.value))
const allDisabled = computed(() => servers.value.length > 0 && servers.value.every(server => server.disabled))

function syncVisibility() { visible.value = !document.hidden }
onMounted(() => document.addEventListener('visibilitychange', syncVisibility))
onUnmounted(() => document.removeEventListener('visibilitychange', syncVisibility))

watch([() => props.active, () => props.token, visible, servers, statusRefresh], ([active, token, isVisible, rows], _, onCleanup) => {
  if (!active || !token || !isVisible || !rows.length) return
  const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout> | undefined
  onCleanup(() => {
    clearTimeout(timer)
    controller.abort()
  })
  async function poll() {
    try {
      const result = await getPreviewServerStatuses(token, controller.signal)
      if (!controller.signal.aborted) statuses.value = new Map(result.map(row => [row.agentUrl, row]))
    } catch {
      if (!controller.signal.aborted) statuses.value.clear()
    } finally {
      if (!controller.signal.aborted) {
        statusSettled.value = true
        timer = setTimeout(poll, statusIntervalMs)
      }
    }
  }
  void poll()
}, { immediate: true })

const displayedServers = computed(() => servers.value.map(server => {
  const status = statuses.value.get(server.agentUrl)
  const connection = status
    ? (status.connected ? (status.workerReady ? 'online' : 'unready') : 'offline')
    : (statusSettled.value ? 'missing' : 'checking')
  const description = status
    ? `${status.message} · 检测于 ${new Date(status.checkedAt).toLocaleTimeString('zh-CN', { hour12: false })}`
    : connectionLabels[connection]
  return { ...server, connection, description, connectionLabel: connectionLabels[connection] }
}))

async function refreshServers() {
  if (!props.token || busy.value) return
  loading.value = true
  loadError.value = ''
  const token = props.token
  try {
    const result = await listPreviewServers(token)
    if (token !== props.token) return
    servers.value = result
    probes.clear()
  } catch (error) {
    if (token === props.token) loadError.value = error instanceof Error ? error.message : '无法读取转图服务器列表，请刷新后重试。'
  } finally {
    loading.value = false
    if (token !== props.token) void refreshServers()
  }
}

watch(() => props.token, () => {
  servers.value = []
  probes.clear()
  statuses.value.clear()
  statusSettled.value = false
  editOpen.value = false
  deleting.value = undefined
  draft.agentToken = ''
  void refreshServers()
}, { immediate: true })

function openEditor(server?: PreviewServer) {
  editing.value = server
  Object.assign(draft, {
    name: server?.name ?? '',
    agentUrl: server?.agentUrl ?? '',
    agentToken: '',
    timeoutMinutes: server?.timeoutMinutes ?? 30,
    disabled: server?.disabled ?? false,
  })
  clearToken.value = false
  actionError.value = ''
  editOpen.value = true
}

function openDelete(server: PreviewServer) {
  actionError.value = ''
  deleting.value = server
}

const deleteOpen = computed({
  get: () => Boolean(deleting.value),
  set: open => { if (!open) deleting.value = undefined },
})

async function saveServer() {
  if (busy.value) return
  actionError.value = ''
  const name = draft.name.trim()
  const agentUrl = draft.agentUrl.trim().replace(/\/+$/, '')
  const { timeoutMinutes, disabled } = draft
  if (!name) { actionError.value = '请填写服务器名称。'; return }
  if (!/^https?:\/\/[^\s]+$/i.test(agentUrl)) {
    actionError.value = '请输入有效的 HTTP 或 HTTPS 服务器地址。'
    return
  }
  if (!Number.isInteger(timeoutMinutes) || timeoutMinutes < 1 || timeoutMinutes > 120) {
    actionError.value = '转换超时请输入 1 至 120 分钟之间的整数。'
    return
  }
  const originalUrl = editing.value?.agentUrl
  const token = props.token
  const input: PreviewServerInput = { name, agentUrl, timeoutMinutes, disabled }
  if (originalUrl) input.originalUrl = originalUrl
  if (clearToken.value || draft.agentToken.trim()) input.agentToken = clearToken.value ? '' : draft.agentToken.trim()
  saving.value = true
  try {
    const saved = await savePreviewServer(input, token)
    if (token !== props.token) return
    servers.value = originalUrl
      ? servers.value.map(server => server.agentUrl === originalUrl ? saved : server)
      : [...servers.value, saved]
    if (originalUrl) {
      probes.delete(originalUrl)
      statuses.value.delete(originalUrl)
    }
    draft.agentToken = ''
    editOpen.value = false
    ElMessage.success(originalUrl ? '转图服务器已更新' : '转图服务器已添加')
  } catch (error) {
    if (token === props.token) actionError.value = error instanceof Error ? error.message : '保存服务器失败，请重试。'
  } finally {
    saving.value = false
    if (token !== props.token) void refreshServers()
  }
}

async function deleteServer() {
  const server = deleting.value
  if (!server || busy.value) return
  const token = props.token
  actionError.value = ''
  saving.value = true
  try {
    await removePreviewServer(server.agentUrl, token)
    if (token !== props.token) return
    servers.value = servers.value.filter(row => row.agentUrl !== server.agentUrl)
    probes.delete(server.agentUrl)
    statuses.value.delete(server.agentUrl)
    deleting.value = undefined
    ElMessage.success('转图服务器已删除')
  } catch (error) {
    if (token === props.token) actionError.value = error instanceof Error ? error.message : '删除服务器失败，请重试。'
  } finally {
    saving.value = false
    if (token !== props.token) void refreshServers()
  }
}

async function probeServer(server: PreviewServer) {
  if (busy.value) return
  const token = props.token
  testing.value = server.agentUrl
  probes.delete(server.agentUrl)
  try {
    const result = await testPreviewServer(server.agentUrl, token)
    if (token === props.token) probes.set(server.agentUrl, result)
  } catch (error) {
    if (token === props.token) probes.set(server.agentUrl, {
      ok: false,
      message: error instanceof Error ? error.message : '连接测试失败，请检查服务器地址和代理状态。',
    })
  } finally {
    testing.value = ''
    if (token !== props.token) void refreshServers()
    else statusRefresh.value++
  }
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel">
    <header class="pdm-manager-heading">
      <div>
        <h2>PDF / STEP 转图服务器</h2>
        <p>正式发布审批通过后，系统调度空闲的已启用服务器转换 PDF / STEP。</p>
      </div>
      <div class="pdm-settings-actions preview-server-tools">
        <button type="button" class="pdm-secondary-action" :disabled="busy" @click="refreshServers">刷新</button>
        <button type="button" class="pdm-primary-action" :disabled="busy || !!loadError" @click="openEditor()">新增服务器</button>
      </div>
    </header>
    <p v-if="loadError" class="pdm-form-error" role="alert">{{ loadError }}</p>
    <p v-else-if="allDisabled" class="pdm-inline-info">所有服务器已停用，转图任务将等待可用服务器。</p>
    <div class="pdm-table-scroll preview-server-list" tabindex="0" aria-label="转图服务器列表区域">
      <table class="pdm-project-table pdm-fit-columns" data-pdm-min-width="620" aria-label="转图服务器列表" :aria-busy="loading">
        <thead><tr><th>服务器名称</th><th>地址</th><th>访问令牌</th><th>超时（分钟）</th><th>启用状态</th><th>连接状态</th><th class="preview-server-action-heading">操作</th></tr></thead>
        <tbody>
          <tr v-for="server in displayedServers" :key="server.agentUrl">
            <td :title="server.name">{{ server.name }}</td>
            <td class="preview-server-address">{{ server.agentUrl }}</td>
            <td>{{ server.hasToken ? '已设置' : '未设置' }}</td>
            <td>{{ server.timeoutMinutes }}</td>
            <td>
              <span class="preview-server-status" :class="{ 'is-enabled': !server.disabled }">{{ server.disabled ? '已停用' : '已启用' }}</span>
            </td>
            <td>
              <span class="preview-server-status" :class="'is-' + server.connection" :title="server.description">{{ server.connectionLabel }}</span>
              <small v-if="testing === server.agentUrl" class="preview-server-probe" role="status">正在测试…</small>
              <small v-else-if="probes.has(server.agentUrl)" class="preview-server-probe" :class="{ 'is-error': !probes.get(server.agentUrl)?.ok }" role="status">
                {{ probes.get(server.agentUrl)?.message }}
              </small>
            </td>
            <td>
              <div class="preview-server-actions">
                <button type="button" class="pdm-text-action" :disabled="busy || !!loadError" @click="openEditor(server)">编辑</button>
                <button type="button" class="pdm-text-action" :disabled="busy || !!loadError" @click="probeServer(server)">测试连接</button>
                <button type="button" class="pdm-text-action is-danger" :disabled="busy || !!loadError" @click="openDelete(server)">删除</button>
              </div>
            </td>
          </tr>
          <tr v-if="!servers.length">
            <td colspan="7">{{ loading ? '正在读取服务器列表…' : loadError ? '暂时无法读取服务器列表' : '暂无转图服务器，请添加已运行转图代理的服务器。' }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <el-dialog v-model="editOpen" :title="editing ? '编辑转图服务器' : '新增转图服务器'"
      width="min(440px, calc(100vw - 32px))" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving"
      @closed="draft.agentToken = ''">
      <form id="preview-server-form" class="pdm-project-form" @submit.prevent="saveServer">
        <label>服务器名称<input v-model="draft.name" maxlength="100" placeholder="例如 PC-08" :disabled="saving"></label>
        <label>服务器地址<input v-model="draft.agentUrl" placeholder="例如 http://192.168.2.50:5199" :disabled="saving"></label>
        <label>访问令牌<input v-model="draft.agentToken" type="password" autocomplete="new-password" maxlength="200"
          :placeholder="editing?.hasToken ? '已设置，留空保留原令牌' : '与转图代理配置一致（选填）'" :disabled="saving || clearToken"></label>
        <label v-if="editing?.hasToken" class="pdm-checkbox-field"><input v-model="clearToken" type="checkbox" :disabled="saving">清除原令牌</label>
        <label>转换超时（分钟）<input v-model.number="draft.timeoutMinutes" type="number" min="1" max="120" :disabled="saving"></label>
        <label class="pdm-checkbox-field"><input v-model="draft.disabled" :true-value="false" :false-value="true" type="checkbox" :disabled="saving">参与转图</label>
        <p v-if="actionError" class="pdm-form-error" role="alert">{{ actionError }}</p>
      </form>
      <template #footer>
        <button type="button" class="pdm-secondary-action" :disabled="saving" @click="editOpen = false">取消</button>
        <button type="submit" form="preview-server-form" class="pdm-primary-action" :disabled="saving">保存服务器</button>
      </template>
    </el-dialog>
    <el-dialog v-model="deleteOpen" title="删除转图服务器" width="min(440px, calc(100vw - 32px))"
      :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <p>确认删除「{{ deleting?.name }}」？删除后，新的转图任务将不再分配给这台服务器。</p>
      <p v-if="actionError" class="pdm-form-error" role="alert">{{ actionError }}</p>
      <template #footer>
        <button type="button" class="pdm-secondary-action" :disabled="saving" @click="deleteOpen = false">取消</button>
        <button type="button" class="pdm-danger-action" :disabled="saving" @click="deleteServer">确认删除</button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.preview-server-tools { margin: 0; flex-wrap: wrap; gap: 6px; }
.preview-server-list { min-width: 0; overflow-x: auto; }
.preview-server-list table { min-width: 620px; table-layout: fixed; }
.preview-server-list :is(th, td) { text-align: center; vertical-align: middle; }
.preview-server-list th { white-space: normal; }
.preview-server-list td.preview-server-address { white-space: normal; overflow-wrap: anywhere; }
.preview-server-status { display: inline-block; padding: 2px 7px; border-radius: 999px; color: var(--pdm-muted); background: var(--pdm-surface-muted); }
.preview-server-status.is-enabled, .preview-server-status.is-online { color: var(--pdm-green); background: var(--pdm-green-soft); }
.preview-server-status.is-offline { color: var(--pdm-danger); background: var(--pdm-danger-soft); }
.preview-server-status.is-unready { color: var(--pdm-orange); background: var(--pdm-orange-soft); }
.preview-server-list td:has(.preview-server-probe) { white-space: normal; }
.preview-server-probe { display: block; color: var(--pdm-muted); }
.preview-server-probe.is-error { color: var(--pdm-danger); }
.preview-server-action-heading { width: 164px; }
.preview-server-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: center; gap: 4px; }
</style>
