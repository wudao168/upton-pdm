<script setup lang="ts">
import { Blocks, Boxes, ClipboardCheck, FolderKanban, Library, ListTree, Network, Settings } from '@lucide/vue'
import uptonLogo from '../assets/upton-logo-white.png'
import PlmCubeIcon from './PlmCubeIcon.vue'

type NavKey = 'project-center' | 'projects' | 'materials' | 'standard-library' | 'standard-structure' | 'program-templates' | 'tasks' | 'admin'

const props = withDefaults(defineProps<{ active: NavKey; approvalCount?: number; materialCount?: number; canManageSystem?: boolean; canViewStandardLibrary?: boolean; collapsed?: boolean }>(), {
  approvalCount: 0,
  materialCount: 0,
  canManageSystem: false,
  canViewStandardLibrary: false,
  collapsed: false,
})
const emit = defineEmits<{ navigate: [key: NavKey, label: string] }>()

const items = [
  { key: 'project-center', label: '项目中心', icon: FolderKanban },
  { key: 'projects', label: '项目列表', icon: ListTree },
  { key: 'standard-library', label: '标准物料', icon: Library },
  { key: 'standard-structure', label: '标准结构', icon: Network },
  { key: 'materials', label: '料品管理', icon: Boxes },
  { key: 'program-templates', label: '程序模板', icon: Blocks },
  { key: 'tasks', label: '我的待办', icon: ClipboardCheck },
] satisfies Array<{ key: NavKey; label: string; icon: typeof FolderKanban }>
</script>

<template>
  <aside class="pdm-sidebar" :class="{ 'is-collapsed': props.collapsed }" aria-label="主导航">
    <div class="pdm-sidebar__brand">
      <PlmCubeIcon class="pdm-sidebar__brand-mark" />
      <div class="pdm-sidebar__brand-copy">
        <img class="pdm-sidebar__brand-logo" :src="uptonLogo" alt="UPTON">
      </div>
    </div>
    <nav class="pdm-sidebar__nav">
      <button
        v-for="item in items.filter(entry => !['standard-library', 'standard-structure'].includes(entry.key) || props.canViewStandardLibrary)"
        :key="item.label"
        type="button"
        class="pdm-nav-item"
        :class="{ 'is-active': props.active === item.key }"
        :aria-current="props.active === item.key ? 'page' : undefined"
        :aria-label="props.collapsed ? item.label : undefined"
        :title="props.collapsed ? item.label : undefined"
        @click="emit('navigate', item.key, item.label)"
      >
        <component :is="item.icon" :size="18" aria-hidden="true" />
        <span>{{ item.label }}</span>
        <em v-if="item.key === 'materials' && props.materialCount">{{ props.materialCount }}</em>
        <em v-else-if="item.key === 'tasks' && props.approvalCount">{{ props.approvalCount }}</em>
      </button>
    </nav>
    <div class="pdm-sidebar__footer">
      <button
        v-if="props.canManageSystem"
        type="button"
        class="pdm-nav-item pdm-sidebar__settings"
        :class="{ 'is-active': props.active === 'admin' }"
        :aria-current="props.active === 'admin' ? 'page' : undefined"
        :aria-label="props.collapsed ? '系统管理' : undefined"
        :title="props.collapsed ? '系统管理' : undefined"
        @click="emit('navigate', 'admin', '系统管理')"
      >
        <Settings :size="18" aria-hidden="true" /><span>系统管理</span>
      </button>
    </div>
  </aside>
</template>
