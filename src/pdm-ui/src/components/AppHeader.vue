<script setup lang="ts">
import { Bell, LogOut } from '@lucide/vue'
import { computed, onMounted, onUnmounted, ref } from 'vue'

type PdmTheme = 'a' | 'c' | 'o'

const props = withDefaults(defineProps<{
  online: boolean
  userName?: string
  username?: string
  role?: string
  companyName?: string
  notificationCount?: number
  theme?: PdmTheme
}>(), {
  userName: '',
  username: '',
  role: '',
  companyName: '昆山阿普顿自动化系统有限公司',
  notificationCount: 0,
  theme: 'a',
})

const emit = defineEmits<{ logout: []; notifications: []; theme: [theme: PdmTheme] }>()
const headerNow = ref(new Date())
const personalInfoVisible = ref(false)
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
    <div v-if="props.userName" class="pdm-titlebar__actions">
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
      <button type="button" class="pdm-user-profile-trigger" @click="personalInfoVisible = true">{{ props.userName }}</button>
      <button type="button" class="pdm-logout-button" @click="emit('logout')"><LogOut :size="16" aria-hidden="true" /><span>退出</span></button>
    </div>

    <el-dialog v-model="personalInfoVisible" title="个人信息" width="520px" append-to-body>
      <el-descriptions :column="1" border>
        <el-descriptions-item label="公司">{{ props.companyName }}</el-descriptions-item>
        <el-descriptions-item label="姓名">{{ props.userName }}</el-descriptions-item>
        <el-descriptions-item label="账号">{{ props.username || props.userName }}</el-descriptions-item>
        <el-descriptions-item label="角色">{{ roleName }}</el-descriptions-item>
        <el-descriptions-item label="PDM服务">{{ props.online ? '服务正常' : '服务未连接' }}</el-descriptions-item>
      </el-descriptions>
      <el-alert class="pdm-personal-info-note" title="账号资料与密码由PDM系统管理员统一维护。" type="info" :closable="false" show-icon />
    </el-dialog>
  </header>
</template>
