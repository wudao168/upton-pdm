<script setup lang="ts">
import { computed } from 'vue'
import { Home, ClipboardCheck, FolderOpen, Clock, UserRound } from '@lucide/vue'
import { mobileOfficeNavigation, type MobileOfficeLayout, type MobileOfficePage } from '../mobileOfficeLayout'
const props = withDefaults(defineProps<{ layout: MobileOfficeLayout; page: MobileOfficePage; canViewProjects: boolean; taskCount?: number; reminderCount?: number }>(), { taskCount: 0, reminderCount: 0 })
defineEmits<{ navigate: [page: MobileOfficePage] }>()
const icons = { home: Home, tasks: ClipboardCheck, projects: FolderOpen, 'work-hours': Clock, mine: UserRound }
const items = computed(() => mobileOfficeNavigation(props.layout, props.canViewProjects))
</script>
<template>
  <nav class="mobile-bottom-nav" aria-label="移动办公导航" :style="{ gridTemplateColumns: `repeat(${items.length}, minmax(0, 1fr))` }">
    <button v-for="item in items" :key="item.key" :aria-current="page === item.key ? 'page' : undefined" @click="$emit('navigate', item.key)"><component :is="icons[item.icon]" :size="20" /><span>{{ item.label }}</span><small v-if="item.key === 'tasks' && taskCount">{{ taskCount }}</small><small v-if="item.key === 'work-hours' && reminderCount">{{ reminderCount }}天</small></button>
  </nav>
</template>
