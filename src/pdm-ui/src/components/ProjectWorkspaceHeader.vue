<script setup lang="ts">
import { Boxes, ClipboardList, FileClock, FolderOpen, FolderTree, LayoutDashboard, PackageCheck, Search } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { computed, ref, watch } from 'vue'
import type { ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'

export type ProjectTab = 'overview' | 'files' | 'documents' | 'bom' | 'versions' | 'release' | 'records'

const props = defineProps<{ project: ProjectSummary; projects: ProjectSummary[]; activeTab: ProjectTab; activeProjectDocumentStatus?: string; activeDocumentCounts?: { all: number; model: number; drawing: number }; currentUsername?: string; switchingProjectId?: string }>()
const emit = defineEmits<{ back: []; switch: [projectId: string]; tab: [tab: ProjectTab] }>()
const displayUserName = useUserDisplayName()

function projectDesignLeads(project: ProjectSummary) {
  return project.designLeads?.length ? project.designLeads : project.designLead ? [project.designLead] : []
}

const tabs = [
  { key: 'overview', label: '概览', icon: LayoutDashboard },
  { key: 'files', label: '文件', icon: FolderOpen },
  { key: 'documents', label: '图档', icon: FolderTree },
  { key: 'bom', label: 'BOM', icon: Boxes },
  { key: 'release', label: '发布', icon: PackageCheck },
  { key: 'versions', label: '版本', icon: FileClock },
  { key: 'records', label: '记录', icon: ClipboardList },
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
const currentProjectDocumentCount = computed(() => props.activeDocumentCounts?.all
  ?? familyProjects.value.find(item => item.id === props.project.id)?.documentCount
  ?? props.project.documentCount
  ?? 0)
const sidebarProject = computed(() => props.project)
const childProjectSelected = computed(() => !!sidebarProject.value.parentProjectId)
const visibleTabs = computed(() => tabs.filter(tab => props.project.canReadContent || tab.key === 'overview' || tab.key === 'records'))
const switchConfirmationPending = ref(false)
const projectBrowserOpen = ref(false)
const projectSearchQuery = ref('')
const projectCustomerFilter = ref('')
const projectExecutionUnitFilter = ref('')
const projectPersonFilter = ref('')
const projectPage = ref(1)
const projectPageSize = ref(20)
const normalizedProjectSearchQuery = computed(() => projectSearchQuery.value.trim().toLocaleLowerCase())
const projectCustomerOptions = computed(() => [...new Set(rootProjects.value.map(item => item.customerName).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const projectExecutionUnitOptions = computed(() => [...new Set(rootProjects.value.map(item => item.executionUnitName).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const projectPersonOptions = computed(() => [...new Set(rootProjects.value.flatMap(item => [item.primaryProjectManager, ...item.collaborativeProjectManagers, ...projectDesignLeads(item), ...item.designers]).filter((item): item is string => Boolean(item)))].sort((left, right) => displayUserName(left).localeCompare(displayUserName(right), 'zh-CN')))
const filteredRootProjects = computed(() => {
  const query = normalizedProjectSearchQuery.value
  return rootProjects.value.filter(item => {
    if (query && ![item.code, item.name, item.projectAlias, item.customerName, item.deviceModel, ...item.serialNumbers].some(value => value?.toLocaleLowerCase().includes(query))) return false
    if (projectCustomerFilter.value && item.customerName !== projectCustomerFilter.value) return false
    if (projectExecutionUnitFilter.value && item.executionUnitName !== projectExecutionUnitFilter.value) return false
    if (projectPersonFilter.value && ![item.primaryProjectManager, ...item.collaborativeProjectManagers, ...projectDesignLeads(item), ...item.designers].includes(projectPersonFilter.value)) return false
    return true
  }).sort((left, right) => right.code.localeCompare(left.code, 'zh-CN', { numeric: true, sensitivity: 'base' }))
})
const projectPageCount = computed(() => Math.max(1, Math.ceil(filteredRootProjects.value.length / projectPageSize.value)))
const pagedRootProjects = computed(() => {
  const start = (projectPage.value - 1) * projectPageSize.value
  return filteredRootProjects.value.slice(start, start + projectPageSize.value)
})

watch([projectSearchQuery, projectCustomerFilter, projectExecutionUnitFilter, projectPersonFilter, projectPageSize], () => { projectPage.value = 1 })
watch(projectPageCount, pageCount => { if (projectPage.value > pageCount) projectPage.value = pageCount })

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
  projectPage.value = 1
  projectPageSize.value = 20
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

function modelDocumentCount(project: ProjectSummary) {
  if (project.id === props.project.id && props.activeDocumentCounts) return props.activeDocumentCounts.model
  return project.modelDocumentCount ?? Math.max((project.documentCount ?? 0) - (project.drawingDocumentCount ?? 0), 0)
}

function drawingDocumentCount(project: ProjectSummary) {
  if (project.id === props.project.id && props.activeDocumentCounts) return props.activeDocumentCounts.drawing
  return project.drawingDocumentCount ?? 0
}
</script>

<template>
  <div class="pdm-project-layout">
    <aside class="pdm-project-sidebar-stack" aria-label="项目基本信息与全部项目号">
      <section class="pdm-project-sidebar__context" aria-label="当前项目">
        <div class="pdm-project-switcher">
          <button type="button" class="pdm-project-switcher__display" aria-label="浏览项目" aria-haspopup="dialog" :aria-expanded="projectBrowserOpen" :disabled="switchConfirmationPending || Boolean(switchingProjectId)" :title="`${rootProject.code} · ${rootProject.name}`" @click="projectBrowserOpen = true">
            <span>{{ rootProject.code }} · {{ rootProject.name }}</span>
            <span class="pdm-project-switcher__search-icon" aria-hidden="true"><Search :size="12" /></span>
          </button>
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
          <div><dt>项目经理</dt><dd :title="displayUserName(rootProject.primaryProjectManager, '待分配')">{{ displayUserName(rootProject.primaryProjectManager, '待分配') }}</dd></div>
          <div><dt>主设</dt><dd :title="projectDesignLeads(rootProject).map(item => displayUserName(item)).join('、') || '待分配'">{{ projectDesignLeads(rootProject).map(item => displayUserName(item)).join('、') || '待分配' }}</dd></div>
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
            :aria-busy="item.id === switchingProjectId"
            :disabled="switchConfirmationPending || Boolean(switchingProjectId)"
            @click="item.id !== project.id && emit('switch', item.id)"
          >
            <span class="pdm-project-family__identity"><strong>{{ item.code }}</strong><small :title="item.name">{{ item.name }}</small></span>
            <span class="pdm-project-family__meta">
              <span class="pdm-project-family__state">{{ item.id === switchingProjectId ? '切换中…' : documentStatus(item, item.id === project.id) }}</span>
              <span class="pdm-project-family__document-counts" :aria-label="`3D图档 ${modelDocumentCount(item)}，2D图档 ${drawingDocumentCount(item)}`">
                <span class="is-model" :title="`3D图档 ${modelDocumentCount(item)}`">{{ modelDocumentCount(item) }}</span>
                <span class="is-drawing" :title="`2D图档 ${drawingDocumentCount(item)}`">{{ drawingDocumentCount(item) }}</span>
              </span>
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
          <input v-model="projectSearchQuery" type="search" aria-label="搜索项目" placeholder="搜索项目号、名称、客户、型号或序列号" autofocus>
          <span>找到 {{ filteredRootProjects.length }} 个项目</span>
        </div>
        <div class="pdm-project-browser-filters" aria-label="项目筛选">
          <select v-model="projectCustomerFilter" aria-label="客户筛选"><option value="">全部客户</option><option v-for="customer in projectCustomerOptions" :key="customer" :value="customer">{{ customer }}</option></select>
          <select v-model="projectExecutionUnitFilter" aria-label="事业部筛选"><option value="">全部事业部</option><option v-for="unit in projectExecutionUnitOptions" :key="unit" :value="unit">{{ unit }}</option></select>
          <select v-model="projectPersonFilter" aria-label="人员筛选"><option value="">全部人员</option><option v-for="person in projectPersonOptions" :key="person" :value="person">{{ displayUserName(person) }}</option></select>
        </div>
        <div class="pdm-project-browser-list" role="listbox" aria-label="项目搜索结果">
          <div class="pdm-project-browser-table-head" aria-hidden="true"><span>项目号</span><span>名称</span><span>客户名称</span><span>型号</span><span>序列号</span><span>操作</span></div>
          <button
            v-for="item in pagedRootProjects"
            :key="item.id"
            type="button"
            role="option"
            :aria-label="`选择浏览项目 ${item.code}`"
            :aria-selected="item.id === rootProject.id"
            :disabled="switchConfirmationPending || item.id === rootProject.id"
            @click="selectBrowsedProject(item.id)"
          >
            <strong :title="item.code">{{ item.code }}</strong>
            <span :title="item.name">{{ item.name }}</span>
            <span :title="item.customerName">{{ item.customerName || '—' }}</span>
            <span :title="item.deviceModel">{{ item.deviceModel || '—' }}</span>
            <span :title="item.serialNumbers.join('、')">{{ item.serialNumbers.join('、') || '—' }}</span>
            <em>{{ item.id === rootProject.id ? '当前项目' : '选择' }}</em>
          </button>
          <p v-if="filteredRootProjects.length === 0" class="pdm-empty-info">没有符合搜索条件的项目。</p>
        </div>
        <div class="pdm-project-browser-pagination" aria-label="项目列表分页">
          <span>共 {{ filteredRootProjects.length }} 条</span>
          <label>每页<select v-model.number="projectPageSize" aria-label="每页行数"><option :value="20">20</option><option :value="50">50</option><option :value="100">100</option></select>行</label>
          <button type="button" class="pdm-secondary-action" aria-label="上一页" :disabled="projectPage <= 1" @click="projectPage--">上一页</button>
          <span>第 {{ projectPage }} / {{ projectPageCount }} 页</span>
          <button type="button" class="pdm-secondary-action" aria-label="下一页" :disabled="projectPage >= projectPageCount" @click="projectPage++">下一页</button>
        </div>
        <footer><button type="button" class="pdm-secondary-action" :disabled="switchConfirmationPending" @click="closeProjectBrowser">取消</button></footer>
      </section>
    </div>
  </div>
</template>
