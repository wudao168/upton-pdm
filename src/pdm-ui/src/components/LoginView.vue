<script setup lang="ts">
import { Eye, EyeOff, LockKeyhole, UserRound } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { postDesktopMessage, requestPasswordReset } from '../api'
import companyLogo from '../assets/company-logo-white.png'
import melonJellyUrl from '../assets/melon-jelly.html?url'

const props = withDefaults(defineProps<{ pending: boolean; error: string; online: boolean; compact?: boolean }>(), { compact: false })
const emit = defineEmits<{ submit: [username: string, password: string, rememberCredentials: boolean] }>()

const desktopCredentialStorageAvailable = Boolean(window.chrome?.webview)
const username = ref('')
const password = ref('')
const rememberCredentials = ref(desktopCredentialStorageAvailable)
const showPassword = ref(false)
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
  <main class="pdm-login-shell has-melon" :class="{ 'is-compact': props.compact }">
    <section class="pdm-login-layout">
      <aside class="pdm-login-melon" aria-label="西瓜软糖交互区">
        <iframe
          class="pdm-login-melon-frame"
          :src="`${melonJellyUrl}?embed=login`"
          sandbox="allow-scripts"
          title="西瓜软糖：拖动拉伸，松手回弹"
        />
      </aside>

      <section class="pdm-login-content">
        <div class="pdm-login-compact-brand">
          <img class="pdm-login-brand__logo" :src="companyLogo" alt="UPTON 阿普顿">
        </div>
        <form class="pdm-login-form" aria-label="登录PLM" autocomplete="off" @submit.prevent="submit">
          <header class="pdm-login-heading">
            <h1>产品生命周期管理系统</h1>
            <p>请输入账号与密码</p>
          </header>

          <label class="pdm-login-field" for="pdm-login-username">
            <span>账号</span>
            <span class="pdm-login-input-frame">
              <UserRound class="pdm-login-input-icon" :size="19" aria-hidden="true" />
              <input
                id="pdm-login-username"
                v-model="username"
                class="pdm-login-input"
                name="username"
                autocomplete="username"
                placeholder="请输入账号"
                autofocus
              >
            </span>
          </label>

          <label class="pdm-login-field" for="pdm-login-password">
            <span>密码</span>
            <span class="pdm-login-input-frame pdm-login-password">
              <LockKeyhole class="pdm-login-input-icon" :size="19" aria-hidden="true" />
              <input
                id="pdm-login-password"
                v-model="password"
                class="pdm-login-input"
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
          <p v-else-if="!online" class="pdm-login-warning">PLM服务当前未连接，登录可能失败。</p>

          <button type="submit" class="pdm-login-submit" :disabled="pending || !username.trim() || !password">
            {{ pending ? '登录中...' : '登录' }}
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

<style scoped>
.pdm-login-shell.has-melon { height: 100vh; height: 100dvh; min-height: 0; display: block; padding: 0; overflow: hidden; background: #f6f5f0; }
.pdm-login-shell.has-melon .pdm-login-layout { position: relative; width: 100%; max-width: 1920px; height: 100%; min-height: 0; margin: 0 auto; display: grid; grid-template-columns: minmax(0, 1fr) 380px; }
.pdm-login-melon { min-width: 0; min-height: 0; height: 100%; overflow: hidden; }
.pdm-login-melon-frame { display: block; width: 100%; height: 100%; border: 0; background: #f6f5f0; }
.pdm-login-shell.has-melon .pdm-login-content { min-width: 0; min-height: 0; height: 100%; display: grid; grid-template-rows: minmax(0, 1fr) auto minmax(0, 1fr); justify-items: center; padding: 24px 28px; background: transparent; }
.pdm-login-shell.has-melon .pdm-login-compact-brand { grid-row: 1; align-self: end; display: flex; margin-bottom: 28px; }
.pdm-login-shell.has-melon .pdm-login-brand__logo { width: 180px; filter: brightness(0) saturate(100%) invert(16%) sepia(12%) saturate(1212%) hue-rotate(39deg) brightness(91%) contrast(88%); }
.pdm-login-shell.has-melon .pdm-login-form { grid-row: 2; width: min(100%, 400px); margin: 0; padding: 27px 24px; transform: none; border: 1px solid #deded2; border-radius: 10px; background: rgba(255, 254, 250, .7); backdrop-filter: blur(10px); box-shadow: none; }
.pdm-login-shell.has-melon .pdm-login-heading { display: block; }
.pdm-login-shell.has-melon .pdm-login-heading h1 { font-size: 20px; }
.pdm-login-shell.has-melon .pdm-login-options { align-items: flex-start; flex-direction: column; gap: 12px; }
.pdm-login-shell.has-melon .pdm-login-remember { margin-left: 0; }
@media (max-width: 900px) {
  .pdm-login-shell.has-melon .pdm-login-layout { grid-template-columns: minmax(0, 1fr); }
  .pdm-login-melon { position: absolute; inset: 0; width: 100%; }
  .pdm-login-shell.has-melon .pdm-login-content { position: relative; z-index: 1; padding: 20px; pointer-events: none; }
  .pdm-login-shell.has-melon .pdm-login-form { pointer-events: auto; }
}
@media (max-height: 760px) {
  .pdm-login-shell.has-melon .pdm-login-content { padding-block: 12px; }
  .pdm-login-shell.has-melon .pdm-login-compact-brand { margin-bottom: 16px; }
  .pdm-login-shell.has-melon .pdm-login-brand__logo { width: 160px; }
  .pdm-login-shell.has-melon .pdm-login-form { gap: 7px; padding: 20px; }
  .pdm-login-shell.has-melon .pdm-login-heading { margin-bottom: 8px; }
  .pdm-login-shell.has-melon .pdm-login-heading h1 { font-size: 18px; margin-bottom: 6px; }
  .pdm-login-shell.has-melon .pdm-login-field { gap: 5px; }
  .pdm-login-shell.has-melon .pdm-login-input-frame { height: 40px; min-height: 40px; }
  .pdm-login-shell.has-melon .pdm-login-options { gap: 8px; }
  .pdm-login-shell.has-melon .pdm-login-submit { height: 40px; line-height: 40px; margin: 10px 0 4px; }
}
@media (max-height: 560px) {
  .pdm-login-shell.has-melon .pdm-login-content { padding-block: 10px; }
  .pdm-login-shell.has-melon .pdm-login-compact-brand { display: none; }
  .pdm-login-shell.has-melon .pdm-login-form { gap: 5px; padding: 16px 20px; }
  .pdm-login-shell.has-melon .pdm-login-heading { margin-bottom: 4px; }
  .pdm-login-shell.has-melon .pdm-login-heading h1 { font-size: 17px; margin-bottom: 4px; }
  .pdm-login-shell.has-melon .pdm-login-field { font-size: 13px; line-height: 18px; gap: 4px; }
  .pdm-login-shell.has-melon .pdm-login-input-frame { height: 34px; min-height: 34px; }
  .pdm-login-shell.has-melon .pdm-login-options { flex-direction: row; align-items: center; gap: 6px; }
  .pdm-login-shell.has-melon .pdm-login-forgot, .pdm-login-shell.has-melon .pdm-login-remember { font-size: 12px; }
  .pdm-login-shell.has-melon .pdm-login-submit { height: 34px; line-height: 34px; margin: 6px 0 2px; }
}
@media (max-height: 440px) {
  .pdm-login-shell.has-melon .pdm-login-form { gap: 4px; padding-block: 8px; }
  .pdm-login-shell.has-melon .pdm-login-heading p, .pdm-login-shell.has-melon .pdm-login-form > small { display: none; }
  .pdm-login-shell.has-melon .pdm-login-input-frame { height: 32px; min-height: 32px; }
  .pdm-login-shell.has-melon .pdm-login-submit { margin-top: 4px; }
  .pdm-login-shell.has-melon .pdm-login-error, .pdm-login-shell.has-melon .pdm-login-warning { padding: 5px 8px; font-size: 11px; line-height: 14px; }
}
</style>
