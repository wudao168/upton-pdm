<script setup lang="ts">
import { Bell, LogOut } from '@lucide/vue'

defineProps<{ online: boolean; userName?: string }>()
const emit = defineEmits<{ logout: []; notifications: [] }>()
</script>

<template>
  <header class="pdm-titlebar">
    <div class="pdm-brand">
      <span class="pdm-brand__mark" aria-hidden="true">P</span>
      <span class="pdm-brand__copy">
        <strong>UPTON PDM</strong>
        <small>产品数据管理客户端</small>
      </span>
    </div>
    <div class="pdm-titlebar__actions">
      <span class="pdm-service-state" :class="{ 'is-offline': !online }">
        <i aria-hidden="true" />{{ online ? '服务正常' : '服务未连接' }}
      </span>
      <button type="button" class="pdm-icon-button" aria-label="通知" @click="emit('notifications')">
        <Bell :size="17" />
        <i class="pdm-notice-dot" aria-hidden="true" />
      </button>
      <span v-if="userName" class="pdm-current-user">{{ userName }}</span>
      <button v-if="userName" type="button" class="pdm-icon-button" aria-label="退出登录" @click="emit('logout')"><LogOut :size="16" /></button>
    </div>
  </header>
</template>
