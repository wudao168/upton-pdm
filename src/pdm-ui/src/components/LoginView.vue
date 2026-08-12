<script setup lang="ts">
import { LockKeyhole, UserRound } from '@lucide/vue'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { postDesktopMessage } from '../api'

defineProps<{ pending: boolean; error: string; online: boolean }>()
const emit = defineEmits<{ submit: [username: string, password: string, rememberCredentials: boolean] }>()

const username = ref('')
const password = ref('')
const rememberCredentials = ref(false)
const desktopCredentialStorageAvailable = Boolean(window.chrome?.webview)

function restoreCredentials(event: Event) {
  const detail = (event as CustomEvent<{ username?: string; password?: string; remember?: boolean }>).detail
  if (!detail?.remember) return
  username.value = detail.username ?? ''
  password.value = detail.password ?? ''
  rememberCredentials.value = true
}

onMounted(() => {
  window.addEventListener('pdm-remembered-credentials', restoreCredentials)
  if (desktopCredentialStorageAvailable) postDesktopMessage('credentials-request')
})

onBeforeUnmount(() => window.removeEventListener('pdm-remembered-credentials', restoreCredentials))

function submit() {
  if (username.value.trim() && password.value) emit('submit', username.value.trim(), password.value, rememberCredentials.value)
}
</script>

<template>
  <main class="pdm-login-shell">
    <form class="pdm-panel pdm-login-card" aria-label="登录PDM" @submit.prevent="submit">
      <div class="pdm-login-heading">
        <span class="pdm-brand__mark" aria-hidden="true">P</span>
        <div><h1>登录 UPTON PDM</h1><p>使用PDM账号读取项目、图档、BOM与发布包</p></div>
      </div>
      <label class="pdm-login-field">
        <span>用户名</span>
        <span class="pdm-login-input"><UserRound :size="16" /><input v-model="username" name="username" autocomplete="username" autofocus></span>
      </label>
      <label class="pdm-login-field">
        <span>密码</span>
        <span class="pdm-login-input"><LockKeyhole :size="16" /><input v-model="password" name="password" type="password" autocomplete="current-password"></span>
      </label>
      <label class="pdm-login-remember" :class="{ 'is-disabled': !desktopCredentialStorageAvailable }">
        <input v-model="rememberCredentials" name="rememberCredentials" type="checkbox" :disabled="!desktopCredentialStorageAvailable">
        <span>保存账号和密码</span>
      </label>
      <p v-if="error" class="pdm-login-error" role="alert">{{ error }}</p>
      <p v-else-if="!online" class="pdm-login-warning">API当前未连接，登录可能失败。</p>
      <button type="submit" class="pdm-primary-action pdm-login-submit" :disabled="pending || !username.trim() || !password">
        {{ pending ? '正在登录…' : '登录并加载数据' }}
      </button>
      <small v-if="rememberCredentials">账号和密码使用Windows当前用户加密保存，仅本机当前用户可读取。</small>
      <small v-else-if="desktopCredentialStorageAvailable">登录令牌仅保存在当前客户端会话，关闭客户端后自动清除。</small>
      <small v-else>账号和密码保存仅适用于Windows客户端。</small>
    </form>
  </main>
</template>
