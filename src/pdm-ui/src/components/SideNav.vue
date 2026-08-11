<script setup lang="ts">
import { FolderTree, GitPullRequestArrow, History, LayoutDashboard, ListTree, PackageCheck, Settings, Stamp } from '@lucide/vue'

type NavKey = 'workbench' | 'documents' | 'bom' | 'approvals' | 'release' | 'changes' | 'audit' | 'settings'

const props = withDefaults(defineProps<{ active: NavKey; approvalCount?: number }>(), {
  approvalCount: 0,
})
const emit = defineEmits<{ navigate: [key: NavKey, label: string] }>()

const items = [
  { key: 'workbench', label: '工作台', icon: LayoutDashboard },
  { key: 'documents', label: '项目图档', icon: FolderTree },
  { key: 'bom', label: 'BOM管理', icon: ListTree },
  { key: 'approvals', label: '图纸审批', icon: Stamp },
  { key: 'release', label: '生产发包', icon: PackageCheck },
  { key: 'changes', label: '变更管理', icon: GitPullRequestArrow },
  { key: 'audit', label: '审计查询', icon: History },
] satisfies Array<{ key: NavKey; label: string; icon: typeof LayoutDashboard }>
</script>

<template>
  <aside class="pdm-sidebar" aria-label="主导航">
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
        <component :is="item.icon" :size="17" aria-hidden="true" />
        <span>{{ item.label }}</span>
        <em v-if="item.key === 'approvals' && props.approvalCount">{{ props.approvalCount }}</em>
      </button>
    </nav>
    <button
      type="button"
      class="pdm-nav-item pdm-sidebar__settings"
      :class="{ 'is-active': props.active === 'settings' }"
      :aria-current="props.active === 'settings' ? 'page' : undefined"
      @click="emit('navigate', 'settings', '系统设置')"
    >
      <Settings :size="17" aria-hidden="true" /><span>系统设置</span>
    </button>
  </aside>
</template>
