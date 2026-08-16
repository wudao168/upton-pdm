<script setup lang="ts">
import { ArrowRight, Eye, EyeOff } from '@lucide/vue'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { postDesktopMessage } from '../api'
import PdmLoginCharacters from './PdmLoginCharacters.vue'

defineProps<{ pending: boolean; error: string; online: boolean }>()
const emit = defineEmits<{ submit: [username: string, password: string, rememberUsername: boolean] }>()

const desktopCredentialStorageAvailable = Boolean(window.chrome?.webview)
const username = ref('')
const password = ref('')
const rememberUsername = ref(desktopCredentialStorageAvailable)
const showPassword = ref(false)
const isTypingUsername = ref(false)

function restoreCredentials(event: Event) {
  const detail = (event as CustomEvent<{ username?: string; remember?: boolean }>).detail
  if (!detail?.remember) return
  username.value = detail.username ?? ''
  password.value = ''
  rememberUsername.value = true
}

onMounted(() => {
  window.addEventListener('pdm-remembered-credentials', restoreCredentials)
  if (desktopCredentialStorageAvailable) postDesktopMessage('credentials-request')
})

onBeforeUnmount(() => window.removeEventListener('pdm-remembered-credentials', restoreCredentials))

function submit() {
  if (username.value.trim() && password.value) emit('submit', username.value.trim(), password.value, rememberUsername.value)
}
</script>

<template>
  <main class="pdm-login-shell">
    <section class="pdm-login-layout">
      <aside class="pdm-login-visual" aria-label="PDM图档管理系统登录插画">
        <div class="pdm-login-brand">
          <span class="pdm-login-brand__mark" aria-hidden="true">P</span>
          <span><strong>UPTON PDM</strong><small>产品数据管理系统</small></span>
        </div>
        <div class="pdm-login-character-stage">
          <PdmLoginCharacters
            :is-typing="isTypingUsername"
            :show-password="showPassword"
            :password-length="password.length"
          />
        </div>
      </aside>

      <section class="pdm-login-content">
        <form class="pdm-login-form" aria-label="登录PDM" autocomplete="off" @submit.prevent="submit">
          <div class="pdm-login-mobile-brand" aria-hidden="true">
            <span class="pdm-login-brand__mark">P</span>
            <strong>UPTON PDM</strong>
          </div>
          <header class="pdm-login-heading">
            <h1>PDM图档管理系统</h1>
            <p>请输入账号与密码</p>
          </header>

          <label class="pdm-login-field" for="pdm-login-username">
            <span>账号</span>
            <input
              id="pdm-login-username"
              v-model="username"
              name="username"
              autocomplete="username"
              placeholder="请输入账号"
              autofocus
              @focus="isTypingUsername = true"
              @blur="isTypingUsername = false"
            >
          </label>

          <label class="pdm-login-field" for="pdm-login-password">
            <span>密码</span>
            <span class="pdm-login-password">
              <input
                id="pdm-login-password"
                v-model="password"
                name="password"
                :type="showPassword ? 'text' : 'password'"
                autocomplete="current-password"
                placeholder="请输入密码"
              >
              <button
                type="button"
                class="pdm-login-password-toggle"
                :aria-label="showPassword ? '隐藏密码' : '显示密码'"
                @mousedown.prevent
                @click="showPassword = !showPassword"
              >
                <EyeOff v-if="showPassword" :size="18" />
                <Eye v-else :size="18" />
              </button>
            </span>
          </label>

          <label class="pdm-login-remember" :class="{ 'is-disabled': !desktopCredentialStorageAvailable }">
            <input v-model="rememberUsername" name="rememberUsername" type="checkbox" :disabled="!desktopCredentialStorageAvailable">
            <span>保存账号</span>
          </label>

          <p v-if="error" class="pdm-login-error" role="alert">{{ error }}</p>
          <p v-else-if="!online" class="pdm-login-warning">PDM服务当前未连接，登录可能失败。</p>

          <button type="submit" class="pdm-login-submit" :disabled="pending || !username.trim() || !password">
            <span class="pdm-login-submit__label">{{ pending ? '登录中...' : '登录' }}</span>
            <span class="pdm-login-submit__hover" aria-hidden="true">
              <span>{{ pending ? '登录中...' : '登录' }}</span>
              <ArrowRight :size="16" />
            </span>
          </button>

          <small v-if="desktopCredentialStorageAvailable">仅保存账号，不保存密码。</small>
          <small v-else>保存账号仅适用于Windows客户端。</small>
        </form>
      </section>
    </section>
  </main>
</template>
