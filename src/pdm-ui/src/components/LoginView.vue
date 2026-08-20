<script setup lang="ts">
import { ArrowRight, Eye, EyeOff } from '@lucide/vue'
import { ElMessage } from 'element-plus'
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { postDesktopMessage, requestPasswordReset } from '../api'
import companyLogo from '../assets/company-logo-white.png'
import PdmLoginCharacters from './PdmLoginCharacters.vue'

const props = withDefaults(defineProps<{ pending: boolean; error: string; online: boolean; compact?: boolean }>(), { compact: false })
const emit = defineEmits<{ submit: [username: string, password: string, rememberCredentials: boolean] }>()

const desktopCredentialStorageAvailable = Boolean(window.chrome?.webview)
const username = ref('')
const password = ref('')
const rememberCredentials = ref(desktopCredentialStorageAvailable)
const showPassword = ref(false)
const isTypingUsername = ref(false)
const passwordHelpVisible = ref(false)
const passwordResetPending = ref(false)
const passwordResetForm = reactive({ username: '', displayName: '' })

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
  if (!username.value.trim() || !password.value) return
  emit('submit', username.value.trim(), password.value, rememberCredentials.value)
}

function openPasswordReset() {
  passwordResetForm.username = username.value.trim()
  passwordResetForm.displayName = ''
  passwordHelpVisible.value = true
}

async function submitPasswordReset() {
  const resetUsername = passwordResetForm.username.trim()
  const displayName = passwordResetForm.displayName.trim()
  if (!resetUsername || !displayName) return ElMessage.warning('请输入账号和姓名')
  passwordResetPending.value = true
  try {
    await requestPasswordReset(resetUsername, displayName)
    passwordHelpVisible.value = false
    ElMessage.success('申请已提交；如信息匹配，管理员将收到处理提醒')
  } finally {
    passwordResetPending.value = false
  }
}
</script>

<template>
  <main class="pdm-login-shell" :class="{ 'is-compact': props.compact }">
    <section class="pdm-login-layout">
      <aside v-if="!props.compact" class="pdm-login-visual" aria-label="PDM图档管理系统登录插画">
        <div class="pdm-login-brand">
          <img class="pdm-login-brand__logo" :src="companyLogo" alt="UPTON 阿普顿">
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
          <div v-if="!props.compact" class="pdm-login-mobile-brand" aria-hidden="true">
            <img class="pdm-login-brand__logo" :src="companyLogo" alt="">
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

          <div class="pdm-login-options">
            <button type="button" class="pdm-login-forgot" @click="openPasswordReset">忘记密码？</button>
            <label class="pdm-login-remember" :class="{ 'is-disabled': !desktopCredentialStorageAvailable }">
              <input v-model="rememberCredentials" name="rememberCredentials" type="checkbox" :disabled="!desktopCredentialStorageAvailable">
              <span>保存账号和密码</span>
            </label>
          </div>

          <p v-if="error" class="pdm-login-error" role="alert">{{ error }}</p>
          <p v-else-if="!online" class="pdm-login-warning">PDM服务当前未连接，登录可能失败。</p>

          <button type="submit" class="pdm-login-submit" :disabled="pending || !username.trim() || !password">
            <span class="pdm-login-submit__label">{{ pending ? '登录中...' : '登录' }}</span>
            <span class="pdm-login-submit__hover" aria-hidden="true">
              <span>{{ pending ? '登录中...' : '登录' }}</span>
              <ArrowRight :size="16" />
            </span>
          </button>

          <small v-if="desktopCredentialStorageAvailable">账号和密码仅加密保存在当前Windows用户下。</small>
          <small v-else>保存账号和密码仅适用于Windows客户端。</small>
        </form>
      </section>
    </section>

    <el-dialog v-model="passwordHelpVisible" title="申请重置密码" width="420px" :close-on-click-modal="false" append-to-body>
      <el-form :model="passwordResetForm" label-position="top" @submit.prevent="submitPasswordReset">
        <el-form-item label="账号" required>
          <el-input v-model="passwordResetForm.username" autocomplete="username" placeholder="请输入账号" maxlength="80" />
        </el-form-item>
        <el-form-item label="姓名" required>
          <el-input v-model="passwordResetForm.displayName" autocomplete="name" placeholder="请输入姓名" maxlength="80" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="passwordResetPending" @click="passwordHelpVisible = false">取消</el-button>
        <el-button type="primary" :loading="passwordResetPending" @click="submitPasswordReset">发送申请</el-button>
      </template>
    </el-dialog>
  </main>
</template>
