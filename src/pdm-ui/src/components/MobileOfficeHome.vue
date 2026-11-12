<script setup lang="ts">
import CompanyName from './CompanyName.vue'
import { computed } from 'vue'
import type { MyApprovalTask, ProjectSummary } from '../types'
import { mobileOfficeNavigation, type MobileOfficeLayout, type MobileOfficePage, type MobileOfficeBlockKey } from '../mobileOfficeLayout'
const props = withDefaults(defineProps<{
  layout: MobileOfficeLayout; displayName: string; today: string; tasks: MyApprovalTask[]; projects: ProjectSummary[]
  unread: number; todayHours: number | null; todayHoursError?: string; refreshing?: boolean; canViewProjects: boolean; reminderCount?: number; selectedBlock?: string
}>(), { todayHoursError: '', refreshing: false, reminderCount: 0, selectedBlock: '' })
defineEmits<{ navigate: [page: MobileOfficePage]; openTask: [id: string]; openProject: [id: string]; select: [key: MobileOfficeBlockKey] }>()
const blocks = computed(() => props.layout.blocks.filter(block => block.enabled && (block.key !== 'projects' || props.canViewProjects)))
const enabled = (page: MobileOfficePage) => mobileOfficeNavigation(props.layout, props.canViewProjects).some(item => item.key === page)
</script>
<template>
  <section aria-label="移动办公首页" class="mobile-home mobile-home-layout" :data-spacing="layout.spacing">
    <section v-for="block in blocks" :key="block.key" :data-block="block.key" :data-selected="selectedBlock === block.key || undefined" class="mobile-home-block" :style="{ gridColumn: `span ${block.width}`, '--mobile-block-columns': block.columns }" @click="$emit('select', block.key)">
      <template v-if="block.key === 'welcome'"><h1>{{ block.title }}</h1><p>{{ displayName }}，{{ today }}</p><p>项目与业务数据和电脑端同步。</p></template>
      <template v-else-if="block.key === 'metrics'"><h2>{{ block.title }}</h2><div class="mobile-layout-metrics mobile-layout-grid"><button @click="$emit('navigate', 'tasks')"><strong>{{ tasks.length + unread }}</strong><span>待处理事项</span></button><button @click="$emit('navigate', 'work-hours')"><strong>{{ todayHours === null ? '—' : todayHours }}</strong><span>今日已填工时（小时）</span></button></div><p v-if="todayHoursError" class="mobile-error" role="status">{{ todayHoursError }}</p></template>
      <template v-else-if="block.key === 'shortcuts'"><h2>{{ block.title }}</h2><div class="mobile-shortcuts mobile-layout-grid"><button v-if="enabled('tasks')" @click="$emit('navigate', 'tasks')">处理审批</button><button v-if="enabled('work-hours')" @click="$emit('navigate', 'work-hours')">填写工时<span v-if="reminderCount"> · 未填{{ reminderCount }}天</span></button><button v-if="enabled('projects')" @click="$emit('navigate', 'projects')">查看项目 / 提交OPL</button></div><p v-if="!enabled('tasks') && !enabled('work-hours') && !enabled('projects')">业务入口已隐藏，可联系管理员调整导航。</p></template>
      <template v-else-if="block.key === 'approvals'"><h2>{{ block.title }}</h2><p v-if="refreshing" role="status">正在读取待办…</p><p v-else-if="!tasks.length">暂无待处理审批。</p><div class="mobile-layout-grid"><button v-for="task in tasks.slice(0, block.limit)" :key="task.id" class="mobile-record mobile-record-button" @click="$emit('openTask', task.id)"><strong>{{ task.projectCode }} · {{ task.projectName }}</strong><span>{{ task.releasePackageNumber || '验证 / 验收计划' }} · {{ task.stepName || task.stage }}</span></button></div></template>
      <template v-else-if="block.key === 'projects'"><h2>{{ block.title }}</h2><div class="mobile-layout-grid"><button v-for="project in projects.slice(0, block.limit)" :key="project.id" class="mobile-record mobile-record-button" @click="$emit('openProject', project.id)"><strong>{{ project.code }} · {{ project.name }}</strong><span>{{ project.stage }} · <CompanyName :name="project.customerName || '未设置客户'" /></span></button></div><p v-if="!refreshing && !projects.length">当前账号暂无可查看的项目。</p></template>
    </section>
  </section>
</template>
