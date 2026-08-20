<script setup lang="ts">
import { Boxes, ClipboardList, FileClock, FolderOpen, FolderTree, LayoutDashboard, PackageCheck } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { computed, ref } from 'vue'
import type { ProjectSummary } from '../types'

export type ProjectTab = 'overview' | 'files' | 'documents' | 'bom' | 'versions' | 'release' | 'records'

const props = defineProps<{ project: ProjectSummary; projects: ProjectSummary[]; activeTab: ProjectTab; activeProjectDocumentStatus?: string; currentUsername?: string }>()
const emit = defineEmits<{ back: []; switch: [projectId: string]; tab: [tab: ProjectTab] }>()

const tabs = [
  { key: 'overview', label: '概览', icon: LayoutDashboard },
  { key: 'files', label: '文件库', icon: FolderOpen },
  { key: 'documents', label: '图档', icon: FolderTree },
  { key: 'bom', label: 'BOM', icon: Boxes },
  { key: 'versions', label: '版本', icon: FileClock },
  { key: 'release', label: '审批发布', icon: PackageCheck },
  { key: 'records', label: '项目记录', icon: ClipboardList },
] satisfies Array<{ key: ProjectTab; label: string; icon: typeof LayoutDashboard }>

const rootProject = computed(() => {
  if (!props.project.parentProjectId) return props.project
  return props.projects.find(item => item.id === props.project.parentProjectId) ?? props.project
})
const rootProjects = computed(() => props.projects.filter(item => !item.parentProjectId))
const familyProjects = computed(() => {
  const root = props.projects.find(item => item.id === rootProject.value.id) ?? rootProject.value
  const children = props.projects
    .filter(item => item.parentProjectId === root.id)
    .sort((left, right) => (left.childSequence ?? 0) - (right.childSequence ?? 0))
  return [root, ...children.filter(item => item.id !== root.id)]
})
const childCount = computed(() => familyProjects.value.filter(item => item.parentProjectId === rootProject.value.id).length)
const currentProjectDocumentCount = computed(() => familyProjects.value.find(item => item.id === props.project.id)?.documentCount ?? props.project.documentCount ?? 0)
const sidebarProject = computed(() => props.project)
const childProjectSelected = computed(() => !!sidebarProject.value.parentProjectId)
const visibleTabs = computed(() => tabs.filter(tab => props.project.canReadContent || tab.key === 'overview' || tab.key === 'records'))
const switchConfirmationPending = ref(false)
const projectBrowserOpen = ref(false)
const projectSearchQuery = ref('')
const projectCustomerFilter = ref('')
const projectExecutionUnitFilter = ref('')
const projectPersonFilter = ref('')
const minimumProjectSearchLength = 4
const normalizedProjectSearchQuery = computed(() => projectSearchQuery.value.trim().toLocaleLowerCase())
const projectTextSearchReady = computed(() => [...normalizedProjectSearchQuery.value].length >= minimumProjectSearchLength)
const projectFiltersActive = computed(() => Boolean(projectCustomerFilter.value || projectExecutionUnitFilter.value || projectPersonFilter.value))
const projectSearchReady = computed(() => projectTextSearchReady.value || (!normalizedProjectSearchQuery.value && projectFiltersActive.value))
const projectCustomerOptions = computed(() => [...new Set(rootProjects.value.map(item => item.customerName).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const projectExecutionUnitOptions = computed(() => [...new Set(rootProjects.value.map(item => item.executionUnitName).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const projectPersonOptions = computed(() => [...new Set(rootProjects.value.flatMap(item => [item.primaryProjectManager, ...item.collaborativeProjectManagers, item.designLead, ...item.designers]).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const filteredRootProjects = computed(() => {
  const query = normalizedProjectSearchQuery.value
  if (!projectSearchReady.value) return []
  return rootProjects.value.filter(item => {
    if (query && ![item.code, item.name, item.projectAlias, item.deviceModel].some(value => value?.toLocaleLowerCase().includes(query))) return false
    if (projectCustomerFilter.value && item.customerName !== projectCustomerFilter.value) return false
    if (projectExecutionUnitFilter.value && item.executionUnitName !== projectExecutionUnitFilter.value) return false
    if (projectPersonFilter.value && ![item.primaryProjectManager, ...item.collaborativeProjectManagers, item.designLead, ...item.designers].includes(projectPersonFilter.value)) return false
    return true
  })
})

function projectBrowserDescription(project: ProjectSummary) {
  return [
    project.customerName && `客户：${project.customerName}`,
    project.executionUnitName && `事业部：${project.executionUnitName}`,
    project.primaryProjectManager && `项目经理：${project.primaryProjectManager}`,
    project.designLead && `主设：${project.designLead}`,
  ].filter(Boolean).join(' · ') || '暂无客户及人员信息'
}

async function confirmProjectSwitch(projectId: string) {
  if (switchConfirmationPending.value || projectId === rootProject.value.id) return false
  const target = props.projects.find(item => item.id === projectId)
  if (!target) return false

  switchConfirmationPending.value = true
  try {
    await ElMessageBox.confirm(
      `确定切换到项目“${target.code} · ${target.name}”吗？`,
      '确认切换项目',
      { confirmButtonText: '确认切换', cancelButtonText: '取消', type: 'warning' },
    )
    emit('switch', projectId)
    return true
  } catch {
    // 用户取消时保持当前项目。
    return false
  } finally {
    switchConfirmationPending.value = false
  }
}

function closeProjectBrowser() {
  if (switchConfirmationPending.value) return
  projectBrowserOpen.value = false
  projectSearchQuery.value = ''
  projectCustomerFilter.value = ''
  projectExecutionUnitFilter.value = ''
  projectPersonFilter.value = ''
}

async function selectBrowsedProject(projectId: string) {
  if (await confirmProjectSwitch(projectId)) closeProjectBrowser()
}

function statusTone(status?: string) {
  if (status?.includes('失败') || status?.includes('退回')) return 'is-alert'
  if (status?.includes('待') || status?.includes('编辑中') || status?.includes('检出') || status?.includes('发布中')) return 'is-remind'
  return 'is-ok'
}

function documentStatus(project: ProjectSummary, activeProject = false) {
  if (activeProject && props.activeProjectDocumentStatus) return props.activeProjectDocumentStatus
  const owner = project.rootDocumentCheckedOutBy?.trim()
  if (!owner) return '正常'
  const currentUsername = props.currentUsername?.trim()
  return currentUsername && owner.localeCompare(currentUsername, undefined, { sensitivity: 'accent' }) === 0
    ? '可编辑'
    : `${owner}编辑中`
}
</script>

<template>
  <div class="pdm-project-layout">
    <aside class="pdm-project-sidebar-stack" aria-label="项目基本信息与全部项目号">
      <section class="pdm-project-sidebar__context" aria-label="当前项目">
        <div class="pdm-project-switcher"><span class="pdm-visually-hidden">切换项目</span>
          <input :value="`${rootProject.code} · ${rootProject.name}`" type="text" readonly aria-label="当前项目显示" :title="`${rootProject.code} · ${rootProject.name}`">
          <button type="button" class="pdm-secondary-action" aria-label="浏览项目" :disabled="switchConfirmationPending" @click="projectBrowserOpen = true">浏览</button>
        </div>
      </section>

      <div class="pdm-project-sidebar">
      <section class="pdm-project-sidebar__summary">
        <small>{{ childProjectSelected ? '子项目' : '主项目' }}</small>
        <strong :title="`${sidebarProject.code} · ${sidebarProject.name}`">{{ sidebarProject.code }} · {{ sidebarProject.name }}</strong>
        <span v-if="sidebarProject.projectAlias">{{ sidebarProject.projectAlias }}</span>
        <dl>
          <div><dt>状态</dt><dd><span class="pdm-status" :class="statusTone(documentStatus(sidebarProject, true))">{{ documentStatus(sidebarProject, true) }}</span></dd></div>
          <div><dt>型号</dt><dd :title="sidebarProject.deviceModel">{{ sidebarProject.deviceModel || '—' }}</dd></div>
          <div><dt>序列号</dt><dd :title="sidebarProject.serialNumbers.join('、')">{{ sidebarProject.serialNumbers.join('、') || '—' }}</dd></div>
          <div><dt>事业部</dt><dd :title="rootProject.executionUnitName">{{ rootProject.executionUnitName || '待分配' }}</dd></div>
          <div><dt>项目经理</dt><dd :title="rootProject.primaryProjectManager">{{ rootProject.primaryProjectManager || '待分配' }}</dd></div>
          <div><dt>主设</dt><dd :title="rootProject.designLead">{{ rootProject.designLead || '待分配' }}</dd></div>
        </dl>
      </section>

      <section class="pdm-project-sidebar__overview" aria-label="项目概览">
        <div><strong>{{ childCount }}</strong><span>子项目</span></div>
        <div><strong>{{ currentProjectDocumentCount }}</strong><span>图档</span></div>
      </section>

      <section class="pdm-project-family">
        <header><strong>全部项目号</strong><span>{{ familyProjects.length }}</span></header>
        <div class="pdm-project-family__list">
          <button
            v-for="item in familyProjects"
            :key="item.id"
            type="button"
            :class="{ 'is-active': item.id === project.id }"
            :aria-current="item.id === project.id ? 'page' : undefined"
            :aria-label="`选择项目号 ${item.code}`"
            :disabled="switchConfirmationPending"
            @click="item.id !== project.id && emit('switch', item.id)"
          >
            <span class="pdm-project-family__identity"><strong>{{ item.code }}</strong><small :title="item.name">{{ item.name }}</small></span>
            <span class="pdm-project-family__meta">
              <span class="pdm-project-family__state">{{ documentStatus(item, item.id === project.id) }}</span>
              <span v-if="(item.documentCount ?? 0) > 0" class="pdm-project-family__document-tag" :title="`该项目号包含 ${item.documentCount} 个图档`">{{ item.documentCount }}</span>
            </span>
          </button>
        </div>
      </section>
      </div>
    </aside>

    <section class="pdm-project-detail">
      <header class="pdm-project-detail__header">
        <nav class="pdm-project-tabs" aria-label="项目功能">
          <button v-for="tab in visibleTabs" :key="tab.key" type="button" :class="{ 'is-active': activeTab === tab.key }" @click="emit('tab', tab.key)"><component :is="tab.icon" :size="15" /><span>{{ tab.label }}</span></button>
        </nav>
      </header>
      <slot />
    </section>

    <div v-if="projectBrowserOpen" class="pdm-dialog-backdrop" @click.self="closeProjectBrowser" @keydown.esc="closeProjectBrowser">
      <section class="pdm-project-browser-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-project-browser-title">
        <header>
          <div><h3 id="pdm-project-browser-title">浏览项目</h3><p>搜索并选择需要切换的主项目。</p></div>
          <button type="button" class="pdm-icon-button" aria-label="关闭项目浏览" :disabled="switchConfirmationPending" @click="closeProjectBrowser">×</button>
        </header>
        <div class="pdm-project-browser-search">
          <input v-model="projectSearchQuery" type="search" aria-label="搜索项目" placeholder="输入至少4个字符，搜索项目号、名称或型号" autofocus>
          <span>{{ projectSearchReady ? `找到 ${filteredRootProjects.length} 个项目` : '输入4个字符或选择筛选条件' }}</span>
        </div>
        <div class="pdm-project-browser-filters" aria-label="项目筛选">
          <select v-model="projectCustomerFilter" aria-label="客户筛选"><option value="">全部客户</option><option v-for="customer in projectCustomerOptions" :key="customer" :value="customer">{{ customer }}</option></select>
          <select v-model="projectExecutionUnitFilter" aria-label="事业部筛选"><option value="">全部事业部</option><option v-for="unit in projectExecutionUnitOptions" :key="unit" :value="unit">{{ unit }}</option></select>
          <select v-model="projectPersonFilter" aria-label="人员筛选"><option value="">全部人员</option><option v-for="person in projectPersonOptions" :key="person" :value="person">{{ person }}</option></select>
        </div>
        <div class="pdm-project-browser-list" role="listbox" aria-label="项目搜索结果">
          <button
            v-for="item in filteredRootProjects"
            :key="item.id"
            type="button"
            role="option"
            :aria-label="`选择浏览项目 ${item.code}`"
            :aria-selected="item.id === rootProject.id"
            :disabled="switchConfirmationPending || item.id === rootProject.id"
            @click="selectBrowsedProject(item.id)"
          >
            <span class="pdm-project-browser-identity"><strong>{{ item.code }}</strong><span :title="item.name">{{ item.name }}</span></span>
            <span class="pdm-project-browser-description" :title="projectBrowserDescription(item)">{{ projectBrowserDescription(item) }}</span>
            <em>{{ item.id === rootProject.id ? '当前项目' : '选择' }}</em>
          </button>
          <p v-if="!projectSearchReady" class="pdm-empty-info">请输入至少 4 个字符搜索项目，或选择客户、事业部、人员进行筛选。</p>
          <p v-else-if="filteredRootProjects.length === 0" class="pdm-empty-info">没有符合搜索条件的项目。</p>
        </div>
        <footer><button type="button" class="pdm-secondary-action" :disabled="switchConfirmationPending" @click="closeProjectBrowser">取消</button></footer>
      </section>
    </div>
  </div>
</template>
