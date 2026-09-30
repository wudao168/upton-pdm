<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { postDesktopMessage } from '../api'
import { ElMessage } from '../statusMessage'

const props = withDefaults(defineProps<{ reportErrors?: boolean }>(), { reportErrors: true })

const available = ref(false)
const serverAddress = ref('')
const savedServerAddress = ref('')
const pending = ref(false)

function receiveSettings(event: Event) {
  const detail = (event as CustomEvent<{ available?: boolean; serverAddress?: string; error?: string }>).detail ?? {}
  available.value = detail.available === true
  savedServerAddress.value = detail.serverAddress ?? ''
  serverAddress.value = savedServerAddress.value
  pending.value = false
  if (detail.error && props.reportErrors) ElMessage.error(detail.error)
}

function saveServerAddress() {
  if (!serverAddress.value.trim()) return
  pending.value = true
  postDesktopMessage('desktop-settings-save', { serverAddress: serverAddress.value.trim() })
}

onMounted(() => {
  window.addEventListener('pdm-desktop-settings', receiveSettings)
  postDesktopMessage('desktop-settings-request')
})
onBeforeUnmount(() => window.removeEventListener('pdm-desktop-settings', receiveSettings))
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="服务器连接设置">
    <header class="pdm-manager-heading"><div><h2>服务器连接</h2><p>客户端和 SolidWorks 插件共用此地址，更新和重启后保持不变。</p></div></header>
    <label class="pdm-client-workspace-label">
      服务器 IP 或域名及端口
      <span class="pdm-client-workspace-row"><input v-model="serverAddress" :disabled="pending || !available" aria-label="服务器地址" placeholder="http://192.168.2.8:5173" @keyup.enter="saveServerAddress"></span>
      <small>保存前会验证连接。保存成功后客户端重新连接，SolidWorks 插件重启后生效。</small>
    </label>
    <div class="pdm-settings-actions pdm-client-settings-actions">
      <button type="button" class="pdm-primary-action" :disabled="pending || !available || !serverAddress.trim()" @click="saveServerAddress">{{ pending ? '正在验证连接…' : '保存并连接' }}</button>
    </div>
    <p v-if="!available">请在 UPLM 客户端中设置服务器地址。</p>
  </section>
</template>
