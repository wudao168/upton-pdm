<script setup lang="ts">
import { ArrowLeft, Boxes, ClipboardList, FileClock, FolderOpen, FolderTree, LayoutDashboard, PackageCheck } from '@lucide/vue'
import { computed } from 'vue'
import type { ProjectSummary } from '../types'

export type ProjectTab = 'overview' | 'files' | 'documents' | 'bom' | 'versions' | 'release' | 'records'

const props = defineProps<{ project: ProjectSummary; projects: ProjectSummary[]; activeTab: ProjectTab }>()
const emit = defineEmits<{ back: []; switch: [projectId: string]; tab: [tab: ProjectTab] }>()

const tabs = [
  { key: 'overview', label: '概览', icon: LayoutDashboard },
  { key: 'files', label: '文件库', icon: FolderOpen },
  { key: 'documents', label: '图档', icon: FolderTree },
  { key: 'bom', label: 'BOM', icon: Boxes },
  { key: 'versions', label: '版本', icon: FileClock },
  { key: 'release', label: '审批与发布', icon: PackageCheck },
  { key: 'records', label: '项目记录', icon: ClipboardList },
] satisfies Array<{ key: ProjectTab; label: string; icon: typeof LayoutDashboard }>

const rootProject = computed(() => {
  if (!props.project.parentProjectId) return props.project
  return props.projects.find(item => item.id === props.project.parentProjectId) ?? props.project
})
const rootProjects = computed(() => props.projects.filter(item => !item.parentProjectId))
const familyProjects = computed(() => {
  const root = rootProject.value
  const children = props.projects
    .filter(item => item.parentProjectId === root.id)
    .sort((left, right) => (left.childSequence ?? 0) - (right.childSequence ?? 0))
  return [root, ...children.filter(item => item.id !== root.id)]
})
const childCount = computed(() => familyProjects.value.filter(item => item.parentProjectId === rootProject.value.id).length)
const drawingProjectCount = computed(() => familyProjects.value.filter(item => (item.documentCount ?? 0) > 0).length)
const visibleTabs = computed(() => tabs.filter(tab => props.project.canReadContent || tab.key === 'overview' || tab.key === 'records'))

function statusTone(status?: string) {
  if (status?.includes('失败') || status?.includes('退回')) return 'is-alert'
  if (status?.includes('待') || status?.includes('检出') || status?.includes('发布中')) return 'is-remind'
  return 'is-ok'
}
</script>

<template>
  <section class="pdm-project-context" aria-label="当前项目">
    <div class="pdm-project-context__bar">
      <div class="pdm-project-context__breadcrumb">
        <button type="button" @click="emit('back')">项目中心</button><span>/</span>
        <strong>{{ rootProject.code }} · {{ rootProject.name }}</strong>
        <template v-if="project.id !== rootProject.id"><span>/</span><strong>{{ project.code }} · {{ project.name }}</strong></template>
      </div>
      <label class="pdm-project-switcher">切换项目
        <select :value="rootProject.id" aria-label="切换项目" @change="emit('switch', ($event.target as HTMLSelectElement).value)">
          <option v-for="item in rootProjects" :key="item.id" :value="item.id">{{ item.code }} · {{ item.name }}</option>
        </select>
      </label>
      <div class="pdm-project-context__actions">
        <button type="button" class="pdm-context-back" @click="emit('back')"><ArrowLeft :size="15" />返回项目中心</button>
      </div>
    </div>
  </section>

  <div class="pdm-project-layout">
    <aside class="pdm-project-sidebar" aria-label="项目基本信息与全部项目号">
      <section class="pdm-project-sidebar__summary">
        <small>主项目</small>
        <strong :title="`${rootProject.code} · ${rootProject.name}`">{{ rootProject.code }} · {{ rootProject.name }}</strong>
        <span v-if="rootProject.projectAlias">{{ rootProject.projectAlias }}</span>
        <dl>
          <div><dt>状态</dt><dd><span class="pdm-status" :class="statusTone(rootProject.businessStatus)">{{ rootProject.businessStatus || rootProject.stage }}</span></dd></div>
          <div><dt>客户</dt><dd :title="rootProject.customerName">{{ rootProject.customerName || '—' }}</dd></div>
          <div><dt>事业部</dt><dd :title="rootProject.executionUnitName">{{ rootProject.executionUnitName || '待分配' }}</dd></div>
          <div><dt>项目经理</dt><dd :title="rootProject.primaryProjectManager">{{ rootProject.primaryProjectManager || '待分配' }}</dd></div>
          <div><dt>设计负责人</dt><dd :title="rootProject.designLead">{{ rootProject.designLead || '待分配' }}</dd></div>
        </dl>
      </section>

      <section class="pdm-project-sidebar__overview" aria-label="项目概览">
        <div><strong>{{ childCount }}</strong><span>子项目</span></div>
        <div><strong>{{ drawingProjectCount }}</strong><span>有图档</span></div>
      </section>

      <section class="pdm-project-family">
        <header><strong>全部项目号</strong><span>{{ familyProjects.length }}</span></header>
        <div class="pdm-project-family__list">
          <button
            v-for="item in familyProjects"
            :key="item.id"
            type="button"
            :class="{ 'is-active': item.id === project.id, 'has-drawings': (item.documentCount ?? 0) > 0 }"
            :aria-current="item.id === project.id ? 'page' : undefined"
            :aria-label="`选择项目号 ${item.code}`"
            @click="item.id !== project.id && emit('switch', item.id)"
          >
            <span class="pdm-project-family__identity"><strong>{{ item.code }}</strong><small :title="item.name">{{ item.name }}</small></span>
            <span class="pdm-project-family__state"><i aria-hidden="true" />{{ item.businessStatus || item.stage }}</span>
          </button>
        </div>
      </section>

      <section v-if="activeTab === 'documents' && $slots.sidebar" class="pdm-project-sidebar__aux" aria-label="项目图档概览">
        <slot name="sidebar" />
      </section>
    </aside>

    <section class="pdm-project-detail">
      <header class="pdm-project-detail__header">
        <div class="pdm-project-selected-summary">
          <div class="pdm-project-selected-summary__identity">
            <small>当前项目号</small>
            <strong :title="`${project.code} · ${project.name}`">{{ project.code }} · {{ project.name }}</strong>
          </div>
          <dl>
            <div><dt>设备型号</dt><dd :title="project.deviceModel">{{ project.deviceModel || '—' }}</dd></div>
            <div><dt>序列号</dt><dd :title="project.serialNumbers.join('、')">{{ project.serialNumbers.join('、') || '—' }}</dd></div>
            <div><dt>状态</dt><dd><span class="pdm-status" :class="statusTone(project.businessStatus)">{{ project.businessStatus || project.stage }}</span></dd></div>
            <div><dt>客户</dt><dd :title="project.customerName">{{ project.customerName || '—' }}</dd></div>
          </dl>
        </div>
        <nav class="pdm-project-tabs" aria-label="项目功能">
          <button v-for="tab in visibleTabs" :key="tab.key" type="button" :class="{ 'is-active': activeTab === tab.key }" @click="emit('tab', tab.key)"><component :is="tab.icon" :size="15" /><span>{{ tab.label }}</span></button>
        </nav>
      </header>
      <slot />
    </section>
  </div>
</template>
