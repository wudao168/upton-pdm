<script setup lang="ts">
import { Boxes, ClipboardCheck, FolderKanban, HardDrive, ListTree, Settings } from '@lucide/vue'
import pdmClientIconUrl from '../../../Pdm.Desktop/Assets/PdmClient.ico'

type NavKey = 'project-center' | 'projects' | 'materials' | 'tasks' | 'client-settings' | 'admin'

const props = withDefaults(defineProps<{ active: NavKey; approvalCount?: number; canManageSystem?: boolean; desktopAvailable?: boolean }>(), {
  approvalCount: 0,
  canManageSystem: false,
  desktopAvailable: false,
})
const emit = defineEmits<{ navigate: [key: NavKey, label: string] }>()

const items = [
  { key: 'project-center', label: '项目中心', icon: FolderKanban },
  { key: 'projects', label: '项目列表', icon: ListTree },
  { key: 'materials', label: '料品管理', icon: Boxes },
  { key: 'tasks', label: '我的待办', icon: ClipboardCheck },
] satisfies Array<{ key: NavKey; label: string; icon: typeof FolderKanban }>
</script>

<template>
  <aside class="pdm-sidebar" aria-label="主导航">
    <div class="pdm-sidebar__brand">
      <img
        class="pdm-sidebar__brand-mark"
        :src="pdmClientIconUrl"
        alt=""
        width="38"
        height="38"
        draggable="false"
        aria-hidden="true"
      >
      <div class="pdm-sidebar__brand-copy">
        <strong>UPTON</strong>
        <span>产品数据管理</span>
      </div>
    </div>
    <nav class="pdm-sidebar__nav">
      <button
        v-for="item in items"
        :key="item.label"
        type="button"
        class="pdm-nav-item"
        :class="{ 'is-active': props.active === item.key }"
        :aria-current="props.active === item.key ? 'page' : undefined"
        @click="emit('navigate', item.key, item.label)"
      >
        <component :is="item.icon" :size="18" aria-hidden="true" />
        <span>{{ item.label }}</span>
        <em v-if="item.key === 'tasks' && props.approvalCount">{{ props.approvalCount }}</em>
      </button>
    </nav>
    <div class="pdm-sidebar__footer">
      <button
        v-if="props.desktopAvailable"
        type="button"
        class="pdm-nav-item pdm-sidebar__settings"
        :class="{ 'is-active': props.active === 'client-settings' }"
        :aria-current="props.active === 'client-settings' ? 'page' : undefined"
        @click="emit('navigate', 'client-settings', '客户端设置')"
      >
        <HardDrive :size="18" aria-hidden="true" /><span>客户端设置</span>
      </button>
      <button
        v-if="props.canManageSystem"
        type="button"
        class="pdm-nav-item pdm-sidebar__settings"
        :class="{ 'is-active': props.active === 'admin' }"
        :aria-current="props.active === 'admin' ? 'page' : undefined"
        @click="emit('navigate', 'admin', '系统管理')"
      >
        <Settings :size="18" aria-hidden="true" /><span>系统管理</span>
      </button>
    </div>
  </aside>
</template>
