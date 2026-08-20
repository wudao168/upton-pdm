<script setup lang="ts">
import { Bell, LogIn, LogOut } from '@lucide/vue'
import { ElMessage } from 'element-plus'
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import type { PdmUserProfile } from '../types'

type PdmTheme = 'a' | 'c' | 'o'

const props = withDefaults(defineProps<{
  online: boolean
  userName?: string
  username?: string
  role?: string
  companyName?: string
  notificationCount?: number
  theme?: PdmTheme
  profile?: PdmUserProfile | null
  onSaveProfile?: (profile: Pick<PdmUserProfile, 'landline' | 'mobilePhone' | 'email' | 'gender' | 'nickname'>) => Promise<PdmUserProfile>
  onChangePassword?: (currentPassword: string, password: string) => Promise<void>
}>(), {
  userName: '',
  username: '',
  role: '',
  companyName: '昆山阿普顿自动化系统有限公司',
  notificationCount: 0,
  theme: 'a',
})

const emit = defineEmits<{ login: []; logout: []; notifications: []; theme: [theme: PdmTheme] }>()
const headerNow = ref(new Date())
const personalInfoVisible = ref(false)
const personalSettingsTab = ref('profile')
const personalSettingsPending = ref(false)
const profileForm = reactive({ nickname: '', gender: 'unspecified' as PdmUserProfile['gender'], landline: '', mobilePhone: '', email: '' })
const passwordForm = reactive({ currentPassword: '', password: '', confirmPassword: '' })
let headerClockTimer: number | undefined

const roleName = computed(() => ({
  Administrator: '系统管理员',
  Engineer: '工程师',
  Reviewer: '审核人',
  Approver: '批准人',
  Production: '生产人员',
}[props.role] || props.role || '未分配角色'))

const headerDateTime = computed(() => {
  const value = headerNow.value
  const pad = (part: number) => String(part).padStart(2, '0')
  const weekday = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'][value.getDay()]
  const date = new Date(Date.UTC(value.getFullYear(), value.getMonth(), value.getDate()))
  const day = date.getUTCDay() || 7
  date.setUTCDate(date.getUTCDate() + 4 - day)
  const yearStart = new Date(Date.UTC(date.getUTCFullYear(), 0, 1))
  const week = Math.ceil(((date.getTime() - yearStart.getTime()) / 86_400_000 + 1) / 7)
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())} ${weekday} ${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())} 第${week}周`
})

function selectTheme(command: string) {
  if (command === 'a' || command === 'c' || command === 'o') emit('theme', command)
}

function openPersonalSettings() {
  Object.assign(profileForm, {
    nickname: props.profile?.nickname || '',
    gender: props.profile?.gender || 'unspecified',
    landline: props.profile?.landline || '',
    mobilePhone: props.profile?.mobilePhone || '',
    email: props.profile?.email || '',
  })
  Object.assign(passwordForm, { currentPassword: '', password: '', confirmPassword: '' })
  personalSettingsTab.value = 'profile'
  personalInfoVisible.value = true
}

async function submitProfile() {
  if (!props.onSaveProfile) return
  personalSettingsPending.value = true
  try {
    await props.onSaveProfile({ ...profileForm })
    ElMessage.success('个人资料已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '个人资料保存失败')
  } finally {
    personalSettingsPending.value = false
  }
}

async function submitPassword() {
  if (!passwordForm.currentPassword) return ElMessage.error('请输入当前密码')
  if (passwordForm.password !== passwordForm.confirmPassword) return ElMessage.error('两次密码不一致')
  if (!props.onChangePassword) return
  personalSettingsPending.value = true
  try {
    await props.onChangePassword(passwordForm.currentPassword, passwordForm.password)
    personalInfoVisible.value = false
    ElMessage.success('密码修改成功')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '密码修改失败')
  } finally {
    personalSettingsPending.value = false
  }
}

onMounted(() => {
  headerClockTimer = window.setInterval(() => { headerNow.value = new Date() }, 1_000)
})

onUnmounted(() => {
  if (headerClockTimer !== undefined) window.clearInterval(headerClockTimer)
})
</script>

<template>
  <header class="pdm-titlebar" role="banner">
    <div class="pdm-titlebar__left">
      <span class="pdm-tenant-company" :title="props.companyName">{{ props.companyName }}</span>
    </div>
    <div class="pdm-titlebar__actions">
      <template v-if="props.userName">
        <time class="pdm-header-clock" :datetime="headerNow.toISOString()">{{ headerDateTime }}</time>
        <button type="button" class="pdm-header-control pdm-message-button" @click="emit('notifications')">
          <Bell :size="17" aria-hidden="true" />
          <span>消息</span>
          <em>{{ props.notificationCount }}</em>
        </button>
        <el-dropdown trigger="click" @command="selectTheme">
          <button type="button" class="pdm-header-control pdm-theme-trigger">
            <svg class="pdm-theme-icon" viewBox="0 0 24 24" aria-hidden="true">
              <path d="M12 3a9 9 0 0 0 0 18h1.3a1.7 1.7 0 0 0 1.2-2.9 1.7 1.7 0 0 1 1.2-2.9H17a4 4 0 0 0 4-4A8.2 8.2 0 0 0 12 3Z" />
              <circle cx="7.5" cy="10.5" r="1" />
              <circle cx="10" cy="7" r="1" />
              <circle cx="14" cy="7" r="1" />
              <circle cx="16.5" cy="10.5" r="1" />
            </svg>
            <span>主题</span>
          </button>
          <template #dropdown>
            <el-dropdown-menu class="pdm-theme-menu">
              <el-dropdown-item command="a"><i class="pdm-theme-swatch is-a" /><span>青蓝平衡</span><small v-if="props.theme === 'a'">当前</small></el-dropdown-item>
              <el-dropdown-item command="c"><i class="pdm-theme-swatch is-c" /><span>石墨青绿</span><small v-if="props.theme === 'c'">当前</small></el-dropdown-item>
              <el-dropdown-item command="o"><i class="pdm-theme-swatch is-o" /><span>暖橙活力</span><small v-if="props.theme === 'o'">当前</small></el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <span class="pdm-user-role">{{ roleName }}</span>
        <button type="button" class="pdm-user-profile-trigger" @click="openPersonalSettings">{{ props.userName }}</button>
        <button type="button" class="pdm-logout-button" @click="emit('logout')"><LogOut :size="16" aria-hidden="true" /><span>退出</span></button>
      </template>
      <button v-else type="button" class="pdm-header-control pdm-login-button" @click="emit('login')">
        <LogIn :size="16" aria-hidden="true" />
        <span>登录</span>
      </button>
    </div>

    <el-dialog v-model="personalInfoVisible" class="personal-settings-dialog" title="个人设置" width="560px" append-to-body>
      <el-tabs v-model="personalSettingsTab">
        <el-tab-pane label="个人资料" name="profile">
          <el-form label-width="82px" class="personal-settings-form">
            <el-form-item label="姓名"><el-input :model-value="props.profile?.displayName || props.userName || props.username" disabled /></el-form-item>
            <el-form-item label="昵称"><el-input v-model="profileForm.nickname" maxlength="80" show-word-limit /></el-form-item>
            <el-form-item label="性别">
              <el-radio-group v-model="profileForm.gender">
                <el-radio value="unspecified">未设置</el-radio>
                <el-radio value="male">男</el-radio>
                <el-radio value="female">女</el-radio>
              </el-radio-group>
            </el-form-item>
            <el-form-item label="固定电话"><el-input v-model="profileForm.landline" maxlength="40" /></el-form-item>
            <el-form-item label="移动电话"><el-input v-model="profileForm.mobilePhone" maxlength="40" /></el-form-item>
            <el-form-item label="邮箱"><el-input v-model="profileForm.email" maxlength="120" /></el-form-item>
          </el-form>
          <div class="personal-settings-actions"><el-button type="primary" :loading="personalSettingsPending" @click="submitProfile">保存资料</el-button></div>
        </el-tab-pane>
        <el-tab-pane label="修改密码" name="password">
          <el-form label-width="100px" class="personal-settings-form password-settings-form">
            <el-form-item label="当前密码" required><el-input v-model="passwordForm.currentPassword" type="password" show-password autocomplete="current-password" /></el-form-item>
            <el-form-item label="新密码" required><el-input v-model="passwordForm.password" type="password" show-password autocomplete="new-password" placeholder="至少 8 位，包含字母和数字" /></el-form-item>
            <el-form-item label="确认新密码" required><el-input v-model="passwordForm.confirmPassword" type="password" show-password autocomplete="new-password" /></el-form-item>
          </el-form>
          <div class="personal-settings-actions"><el-button type="primary" :loading="personalSettingsPending" @click="submitPassword">修改密码</el-button></div>
        </el-tab-pane>
      </el-tabs>
    </el-dialog>
  </header>
</template>
