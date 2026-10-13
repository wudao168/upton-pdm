<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { postDesktopMessage } from '../api'

type State = 'checking' | 'connected' | 'disconnected' | 'warning' | 'unconfigured'
type Indicator = { state: State; message: string }
const props = defineProps<{ token: string }>()
const sw = ref<Indicator>({ state: 'unconfigured', message: 'SW：网页端未建立本机SolidWorks插件连接，请使用客户端联动。' })
const pdf = ref<Indicator>({ state: 'checking', message: 'PDF：正在检查转图服务器。' })
const u9c = ref<Indicator>({ state: 'checking', message: 'U9C：正在检查连接。' })
let serverTimer: ReturnType<typeof setInterval> | undefined
let swTimer: ReturnType<typeof setInterval> | undefined
let request: AbortController | undefined
let disposed = false
let swCheckedAt = 0

function onCapability(event: Event) {
  const detail = (event as CustomEvent<{ available?: boolean; connected?: boolean }>).detail
  swCheckedAt = Date.now()
  sw.value = detail?.connected
    ? { state: 'connected', message: 'SW：已连接当前客户端的SolidWorks插件。' }
    : detail?.available
      ? { state: 'warning', message: 'SW：插件已安装，但尚未响应；请启动SolidWorks并加载UPLM插件。' }
      : { state: 'disconnected', message: 'SW：当前客户端未找到可用插件，请检查SolidWorks及UPLM插件安装。' }
}

function refreshSw() {
  if (!window.chrome?.webview || document.hidden) return
  if (Date.now() - swCheckedAt > 10000) sw.value = { state: 'checking', message: 'SW：正在检查当前客户端的插件连接。' }
  postDesktopMessage('solidworks-capability-request')
}

async function refreshServer() {
  if (request || !props.token || document.hidden) return
  const controller = new AbortController()
  request = controller
  const timeout = setTimeout(() => controller.abort(), 15000)
  try {
    const response = await fetch('/api/system-connections', { headers: { Authorization: `Bearer ${props.token}` }, cache: 'no-store', signal: controller.signal })
    if (!response.ok) throw new Error(`HTTP ${response.status}`)
    const result = await response.json() as { pdf: Indicator; u9c: Indicator }
    if (!disposed && request === controller) { pdf.value = result.pdf; u9c.value = result.u9c }
  } catch (error) {
    if (!disposed && request === controller) {
      pdf.value = { state: 'warning', message: 'PDF：暂时无法读取连接状态，请检查PLM服务器连接。' }
      u9c.value = { state: 'warning', message: 'U9C：暂时无法读取连接状态，请检查PLM服务器连接。' }
      console.warn('系统连接状态检查失败', error)
    }
  } finally { clearTimeout(timeout); if (request === controller) request = undefined }
}

function refreshVisible() { if (!document.hidden) { refreshSw(); void refreshServer() } }
watch(() => props.token, () => { request?.abort(); request = undefined; pdf.value = { state: 'checking', message: 'PDF：正在检查转图服务器。' }; u9c.value = { state: 'checking', message: 'U9C：正在检查连接。' }; void refreshServer() })
onMounted(() => {
  window.addEventListener('pdm-solidworks-capability', onCapability)
  document.addEventListener('visibilitychange', refreshVisible)
  refreshVisible()
  serverTimer = setInterval(() => void refreshServer(), 30000)
  swTimer = setInterval(refreshSw, 5000)
})
onBeforeUnmount(() => { disposed = true; request?.abort(); clearInterval(serverTimer); clearInterval(swTimer); window.removeEventListener('pdm-solidworks-capability', onCapability); document.removeEventListener('visibilitychange', refreshVisible) })
</script>

<template>
  <div class="pdm-connection-lights" role="group" aria-label="系统连接状态">
    <span v-for="item in [{ label: 'SW', value: sw }, { label: 'PDF', value: pdf }, { label: 'U9C', value: u9c }]" :key="item.label"
      class="pdm-connection-light" :data-state="item.value.state" :title="item.value.message" :aria-label="item.value.message" role="status">{{ item.label }}</span>
  </div>
</template>

<style scoped>
.pdm-connection-lights { display:flex; gap:3px; padding:0 20px 7px; }
.pdm-connection-light { display:flex; flex:1; align-items:center; justify-content:center; min-width:0; height:22px; border:1px solid rgba(255,255,255,.25); border-bottom:3px solid #94a3b8; border-radius:3px; background:rgba(255,255,255,.06); color:#fff; font-size:11px; font-weight:600; cursor:help; }
.pdm-connection-light[data-state="connected"] { border-bottom-color:#22c55e; }
.pdm-connection-light[data-state="disconnected"] { border-bottom-color:#ef4444; }
.pdm-connection-light[data-state="warning"] { border-bottom-color:#f59e0b; }
:global(.pdm-sidebar.is-collapsed .pdm-connection-lights) { flex-direction:column; align-items:center; padding-inline:3px; }
:global(.pdm-sidebar.is-collapsed .pdm-connection-light) { flex:none; width:32px; height:18px; font-size:10px; }
</style>
