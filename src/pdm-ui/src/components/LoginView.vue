<script setup lang="ts">
import { Eye, EyeOff, LockKeyhole, UserRound } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { postDesktopMessage, requestPasswordReset } from '../api'
import companyLogo from '../assets/company-logo-white.png'
import PdmGalaxyScene from './PdmGalaxyScene.vue'


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
  <main class="pdm-login-shell has-upton" :class="{ 'is-compact': props.compact }">
    <section class="pdm-login-layout">
      <aside class="pdm-login-artwork" aria-label="UPTON M106 星系主题画面">
        <PdmGalaxyScene />
        <div class="pdm-login-artwork__brand">
          <img :src="companyLogo" alt="UPTON 阿普顿" class="pdm-login-artwork__logo">
        </div>
      </aside>

      <section class="pdm-login-content">
        <div class="pdm-login-compact-brand">
          <img class="pdm-login-brand__logo" :src="companyLogo" alt="UPTON 阿普顿">
        </div>
        <form class="pdm-login-form" aria-label="登录PLM" autocomplete="off" @submit.prevent="submit">
          <header class="pdm-login-heading">
            <h1>产品生命周期管理系统</h1>
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
        </form>
      </section>
    </section>

    <el-dialog v-model="passwordHelpVisible" title="申请重置密码" width="420px" :close-on-click-modal="!passwordResetPending" append-to-body>
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
.pdm-login-shell.has-upton {
  height: 100vh; height: 100dvh; min-height: 0; display: block; padding: 0; overflow: auto;
  background: var(--pdm-bg);
}
.pdm-login-shell.has-upton .pdm-login-layout {
  position: relative; width: 100%; height: 100%; min-height: 620px; margin: 0;
  display: grid; grid-template-columns: minmax(0, 1fr) clamp(380px, 31vw, 480px);
}
.pdm-login-artwork { position: relative; min-width: 0; overflow: hidden; color: #fff; background: #0a203b; }
.pdm-login-artwork__image { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; object-position: 50% 65%; }
.pdm-login-artwork__shade { position: absolute; inset: 0; background: linear-gradient(180deg, rgba(4, 18, 39, .18), transparent 55%, rgba(4, 18, 39, .5)); }
.pdm-login-artwork__brand { position: relative; padding: clamp(28px, 4vw, 72px); text-shadow: 0 2px 18px rgba(1, 14, 31, .2); }
.pdm-login-artwork__footer { position: absolute; bottom: 30px; left: clamp(28px, 4vw, 72px); display: flex; align-items: center; gap: 18px; font-size: 12px; letter-spacing: .18em; color: #e5f2ff; }
.pdm-login-artwork__footer i { width: 3px; height: 3px; border-radius: 50%; background: #8fe4fa; }
.pdm-login-shell.has-upton .pdm-login-content { min-width: 0; min-height: 0; height: 100%; display: flex; flex-direction: column; justify-content: center; align-items: center; gap: 32px; padding: 32px 28px; background: var(--pdm-bg); }
.pdm-login-shell.has-upton .pdm-login-compact-brand { display: flex; margin: 0; }
.pdm-login-shell.has-upton .pdm-login-brand__logo { width: 210px; filter: brightness(0); }
.pdm-login-shell.has-upton .pdm-login-form { width: min(100%, 400px); margin: 0; padding: 28px 24px; transform: none; border: 0; border-radius: var(--pdm-stat-card-radius, 20px); background: var(--pdm-surface); box-shadow: var(--pdm-stat-card-shadow, 0 8px 32px rgba(19, 51, 77, .08)); }
.pdm-login-shell.has-upton .pdm-login-heading { display: block; }
.pdm-login-shell.has-upton .pdm-login-heading h1 { font-size: 20px; }
.pdm-login-shell.has-upton .pdm-login-input-frame, .pdm-login-shell.has-upton .pdm-login-submit { border-radius: 999px; }
.pdm-login-shell.has-upton .pdm-login-options { align-items: center; flex-direction: row; justify-content: space-between; gap: 8px; }
.pdm-login-shell.has-upton .pdm-login-remember { margin-left: auto; white-space: nowrap; font-size: 12px; }
@media (max-height: 760px) and (min-width: 761px) {
  .pdm-login-shell.has-upton .pdm-login-layout { min-height: 540px; }
  .pdm-login-shell.has-upton .pdm-login-content { gap: 20px; padding-block: 20px; }
  .pdm-login-shell.has-upton .pdm-login-form { gap: 7px; padding: 22px; }
  .pdm-login-shell.has-upton .pdm-login-heading { margin-bottom: 8px; }
  .pdm-login-shell.has-upton .pdm-login-input-frame { height: 50px; min-height: 50px; }
  .pdm-login-shell.has-upton .pdm-login-submit { height: 50px; line-height: 50px; margin: 10px 0 4px; }
}
@media (max-width: 760px) {
  .pdm-login-shell.has-upton .pdm-login-layout { height: auto; min-height: 100%; grid-template-columns: minmax(0, 1fr); grid-template-rows: 440px auto; }
  .pdm-login-artwork__image { object-position: 65% 63%; }
  .pdm-login-artwork__shade { background: linear-gradient(90deg, rgba(4, 18, 39, .82), rgba(4, 18, 39, .1)); }
  .pdm-login-artwork__brand { padding: 22px; }
  .pdm-login-artwork__footer { display: none; }
  .pdm-login-shell.has-upton .pdm-login-content { height: auto; padding: 24px 16px; gap: 0; }
  .pdm-login-shell.has-upton .pdm-login-compact-brand { display: none; }
  .pdm-login-shell.has-upton .pdm-login-form { padding: 26px 22px; }
  .pdm-login-shell.has-upton .pdm-login-heading h1 { font-size: 19px; }
}
</style>






<style>
.pdm-login-shell.has-upton .pdm-login-artwork__logo {
  display: block; width: auto; max-width: 100%; height: 36px;
  object-fit: contain; object-position: left center;
}
.pdm-login-shell.has-upton .pdm-login-input:is(:autofill, :-webkit-autofill) {
  -webkit-text-fill-color: var(--pdm-text);
  caret-color: var(--pdm-text);
  box-shadow: inset 0 0 0 100px var(--pdm-surface) !important;
}

@layer pdm-button-sizing {
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-input-frame,
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-submit {
    box-sizing: border-box !important;
    height: 50px !important; min-height: 50px !important; max-height: 50px !important;
    border-radius: 999px !important;
  }
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-submit {
    width: 100% !important; line-height: 50px !important;
    background: #000 !important; color: #fff !important; font-size: 14px !important;
  }
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-submit:hover:not(:disabled) {
    background: #222 !important;
  }
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-submit:not(:focus-visible) {
    outline: none !important;
  }
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-input-frame:not(:focus-within) {
    border-color: color-mix(in srgb, var(--pdm-container-border, var(--pdm-border)) 75%, var(--pdm-text)) !important;
  }
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-input-frame:hover:not(:focus-within) {
    border-color: color-mix(in srgb, var(--pdm-container-border, var(--pdm-border)) 60%, var(--pdm-text)) !important;
  }
  :root body .pdm-login-shell.has-upton .pdm-login-form .pdm-login-input-frame:focus-within {
    border-color: var(--shell-accent) !important;
  }
}
</style>
