<script setup lang="ts">
import DepartmentSelectField from './DepartmentSelectField.vue'
import DepartmentName from './DepartmentName.vue'
import CompanyName from './CompanyName.vue'
import CompanyNameField from './CompanyNameField.vue'
import PersonName from './PersonName.vue'
import SearchField from './SearchField.vue'
import UserSelect from './UserSelect'
import RawDrawerPanel from './RawDrawerPanel.vue'
import PanelActions from './PanelActions.vue'
import { ElAutocomplete, ElOption, ElSelect } from 'element-plus'

import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { OrganizationUnit, PdmCustomer, ProjectCopyOptionsInput, ProjectCopyPreview, ProjectCopyResult, ProjectSummary, ProjectTypeDefinition, ReleasePackageSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'
import { useProjectTabUpdates } from '../projectTabUpdates'
import { projectReleaseAttention } from '../releasePreviewState'
import { useProjectAttention } from '../composables/useProjectAttention'
import { releaseFailureReasons } from '../projectAttention'
import ReleaseAttentionBorder from './ReleaseAttentionBorder.vue'
import ProjectSettingsDrawer from './ProjectSettingsDrawer.vue'
import { useProjectCenterAccess } from '../projectCenterAccess'
const projectAccess = useProjectCenterAccess()

export type ProjectTab = 'overview' | 'information' | 'budget' | 'project-plan' | 'opl' | 'files' | 'validation-plan' | 'documents' | 'bom' | 'release' | 'procurement' | 'records' | 'summary'

const props = withDefaults(defineProps<{
  active?: boolean
  project: ProjectSummary
  projects: ProjectSummary[]
  projectTypes?: ProjectTypeDefinition[]
  customers?: PdmCustomer[]
  organizationUnits?: OrganizationUnit[]
  companyId?: string
  activeTab: ProjectTab
  releasePackages?: ReleasePackageSummary[]
  releasePreviewIssues?: Record<string, boolean>
  activeProjectDocumentStatus?: string
  activeDocumentCounts?: { all: number; model: number; drawing: number }
  currentUsername?: string
  switchingProjectId?: string
  canCopyContent?: boolean
  token?: string
  canResetContent?: boolean
  pending?: boolean
  onPreviewProjectCopy?: (projectId: string, input: ProjectCopyOptionsInput) => Promise<ProjectCopyPreview>
  onCopyProjectContent?: (projectId: string, input: ProjectCopyOptionsInput) => Promise<ProjectCopyResult>
  onContentResetComplete?: () => Promise<unknown>
}>(), { active: true })
const emit = defineEmits<{ back: []; switch: [projectId: string]; tab: [tab: ProjectTab, hasUpdates?: boolean] }>()
const displayUserName = useUserDisplayName()
const { pending: tabUpdates } = useProjectTabUpdates(props)
const attentionNow = ref(Date.now())
const attentionTimer = window.setInterval(() => { attentionNow.value = Date.now() }, 60000)
onBeforeUnmount(() => window.clearInterval(attentionTimer))
function releaseAttention(item: ProjectSummary) {
  return projectReleaseAttention(item, item.id === props.project.id ? props.releasePackages : undefined, Boolean(props.releasePreviewIssues?.[item.id]), attentionNow.value)
}
const activeReleaseAttention = computed(() => releaseAttention(props.project))
function releaseAttentionTitle(item: ProjectSummary) {
  const tone = releaseAttention(item)
  return tone === 'error' ? '发布或图纸转出存在未完成事项，请到发布页查看处理' : tone === 'new' ? item.hasUnreceivedNonStandardRelease ? '有非标件发布包待供应链接收，接收后停止绿色提示' : '有新的标准件或电气件发布，绿色呼吸边框自发布时间起提示24小时' : undefined
}
function updateTitle(key: ProjectTab, label: string) {
  const warnings = tabWarnings.value.filter(detail => detail.tab === key)
  if (warnings.length) return `${label}异常：${[...new Set(warnings.map(detail => detail.reason))].join('；')}`
  if (key === 'release' && activeReleaseAttention.value) return releaseAttentionTitle(props.project)
  const update = tabUpdates.value[key]
  if (!update) return undefined
  return `${label}有未查看更新，查看后清除提醒${update.updatedAt ? ` · ${new Date(update.updatedAt).toLocaleString('zh-CN')}` : ''}${update.actor ? ` · ${displayUserName(update.actor)}` : ''}`
}

function projectDesignLeads(project: ProjectSummary) {
  return project.designLeads?.length ? project.designLeads : project.designLead ? [project.designLead] : []
}

const tabs = [
  { key: 'overview', label: '概览' },
  { key: 'information', label: '信息' },
  { key: 'files', label: '文件' },
  { key: 'project-plan', label: '计划' },
  { key: 'budget', label: '预算' },
  { key: 'validation-plan', label: '质量' },
  { key: 'opl', label: 'OPL' },
  { key: 'summary', label: '总结' },
  { key: 'documents', label: '图档' },
  { key: 'bom', label: 'BOM' },
  { key: 'release', label: '发布' },
  { key: 'procurement', label: '备料' },
  { key: 'records', label: '记录' },
] satisfies Array<{ key: ProjectTab; label: string }>

const rootProject = computed(() => {
  if (!props.project.parentProjectId) return props.project
  return props.projects.find(item => item.id === props.project.parentProjectId) ?? props.project
})
const rootProjects = computed(() => props.projects.filter(item => !item.parentProjectId))
const listAttention = useProjectAttention(() => props.token, () => [rootProject.value.id], () => props.active)
const tabWarnings = computed(() => [
  ...listAttention.details(props.project.id, undefined, props.project.id === rootProject.value.id),
  ...releaseFailureReasons(props.releasePackages).map(reason => ({ tab: 'release', reason })),
  ...(props.releasePreviewIssues?.[props.project.id] ? [{ tab: 'release', reason: '图纸转出失败' }] : []),
])
function tabAttentionTone(key: ProjectTab) {
  return tabWarnings.value.some(detail => detail.tab === key) ? 'error' : key === 'release' && activeReleaseAttention.value === 'new' ? 'new' : undefined
}
function projectWarnings(item: ProjectSummary) {
  return [...new Set([...listAttention.reasons(item.id, undefined, item.id === rootProject.value.id), ...releaseFailureReasons(item.id === props.project.id ? props.releasePackages : []), ...(props.releasePreviewIssues?.[item.id] ? ['图纸转出失败'] : [])])]
}
function listAttentionTone(item: ProjectSummary) {
  return projectWarnings(item).length ? 'error' : projectReleaseAttention({ ...item, hasReleaseAttention: false }, undefined, false, attentionNow.value)
}
function listAttentionTitle(item: ProjectSummary) {
  return projectWarnings(item).length ? `橙色异常：${projectWarnings(item).join('；')}` : listAttentionTone(item) === 'new' ? '绿色提示：有新发布或待供应链接收的发布包' : listAttention.error.value || undefined
}
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
const executionEngineers = computed(() => {
  const usernames = childProjectSelected.value
    ? sidebarProject.value.designers
    : familyProjects.value.flatMap(item => item.designers)
  const seen = new Set<string>()
  return usernames.filter(username => {
    const normalized = username.trim().toLocaleLowerCase()
    if (!normalized || seen.has(normalized)) return false
    seen.add(normalized)
    return true
  })
})
const executionEngineerNames = computed(() => executionEngineers.value.map(item => displayUserName(item)).join('、') || '—')
const visibleTabs = computed(() => tabs.filter(tab => (tab.key === 'summary' ? props.project.effectiveProjectPermissions?.includes('project.summary.view') : projectAccess.canViewTab(tab.key)) && (props.project.canReadContent || tab.key === 'overview' || tab.key === 'records')))
const switchConfirmationPending = ref(false)
const projectSettingsOpen = ref(false)
const projectBrowserOpen = ref(false)
const quickProjectQuery = ref('')
const projectSearchQuery = ref('')
const projectTypeFilter = ref<string[]>([])
const projectCustomerFilter = ref('')
const projectExecutionUnitFilter = ref('')
const projectPersonFilter = ref('')
const projectPage = ref(1)
const projectPageSize = ref(20)
const normalizedProjectSearchQuery = computed(() => projectSearchQuery.value.trim().toLocaleLowerCase())
function projectCodePrefix(code: string) {
  return code.trim().toUpperCase().match(/^[A-Z]/)?.[0] ?? '#'
}
const projectTypeOptions = computed(() => (props.projectTypes ?? [])
  .filter(type => type.isActive !== false)
  .map(type => ({ code: type.code.trim().toUpperCase(), name: type.name }))
  .sort((left, right) => left.code.localeCompare(right.code)))
const defaultProjectTypes = computed(() => projectTypeOptions.value.some(type => type.code === 'P') ? ['P'] : [])
const projectCustomerOptions = computed(() => [...(props.customers ?? [])].sort((left, right) => left.name.localeCompare(right.name, 'zh-CN')))
const projectExecutionUnitOptions = computed(() => (props.organizationUnits ?? [])
  .filter(unit => unit.kind === 'BusinessDivision' && unit.organizationId === (props.companyId || props.project.organizationId))
  .sort((left, right) => left.name.localeCompare(right.name, 'zh-CN', { numeric: true })))
const selectedCustomer = computed(() => projectCustomerOptions.value.find(customer => customer.code === projectCustomerFilter.value))
const selectedExecutionUnit = computed(() => projectExecutionUnitOptions.value.find(unit => unit.id === projectExecutionUnitFilter.value))
const projectPersonOptions = computed(() => [...new Set(rootProjects.value.flatMap(item => [item.primaryProjectManager, ...item.collaborativeProjectManagers, ...projectDesignLeads(item), ...item.designers]).filter((item): item is string => Boolean(item)))].sort((left, right) => displayUserName(left).localeCompare(displayUserName(right), 'zh-CN')))
function matchesProjectSearch(item: ProjectSummary, query: string) {
  return !query || [item.code, item.name, item.projectAlias, item.customerName, item.deviceModel, ...item.serialNumbers].some(value => value?.toLocaleLowerCase().includes(query))
}
function quickProjectSuggestions(query: string, callback: (items: Array<{ id: string; code: string; name: string; value: string }>) => void) {
  const normalized = query.trim().toLocaleLowerCase()
  callback(rootProjects.value.filter(item => projectCodePrefix(item.code) === 'P' && matchesProjectSearch(item, normalized))
    .sort((left, right) => right.code.localeCompare(left.code, 'zh-CN', { numeric: true, sensitivity: 'base' }))
    .slice(0, 20).map(item => ({ id: item.id, code: item.code, name: item.name, value: `${item.code} · ${item.name}` })))
}
function selectQuickProject(projectId: string) {
  confirmProjectSwitch(projectId)
  quickProjectQuery.value = ''
}
const filteredRootProjects = computed(() => {
  const query = normalizedProjectSearchQuery.value
  const customer = selectedCustomer.value
  const executionUnit = selectedExecutionUnit.value
  return rootProjects.value.filter(item => {
    const projectType = item.projectTypeCode?.trim().toUpperCase() || projectCodePrefix(item.code)
    if (projectTypeFilter.value.length && !projectTypeFilter.value.includes(projectType)) return false
    if (!matchesProjectSearch(item, query)) return false
    if (customer && (item.customerCode ? item.customerCode !== customer.code : item.customerName !== customer.name)) return false
    if (executionUnit && (item.executionUnitId ? item.executionUnitId !== executionUnit.id : item.executionUnitName !== executionUnit.name)) return false
    if (projectPersonFilter.value && ![item.primaryProjectManager, ...item.collaborativeProjectManagers, ...projectDesignLeads(item), ...item.designers].includes(projectPersonFilter.value)) return false
    return true
  }).sort((left, right) => right.code.localeCompare(left.code, 'zh-CN', { numeric: true, sensitivity: 'base' }))
})
const projectPageCount = computed(() => Math.max(1, Math.ceil(filteredRootProjects.value.length / projectPageSize.value)))
const pagedRootProjects = computed(() => {
  const start = (projectPage.value - 1) * projectPageSize.value
  return filteredRootProjects.value.slice(start, start + projectPageSize.value)
})

watch([projectSearchQuery, projectTypeFilter, projectCustomerFilter, projectExecutionUnitFilter, projectPersonFilter, projectPageSize], () => { projectPage.value = 1 })
watch(projectPageCount, pageCount => { if (projectPage.value > pageCount) projectPage.value = pageCount })
watch(() => props.project.id, () => { projectSettingsOpen.value = false })
watch(() => rootProject.value.id, () => { quickProjectQuery.value = '' })

function confirmProjectSwitch(projectId: string) {
  if (switchConfirmationPending.value || props.switchingProjectId || projectId === rootProject.value.id) return false
  const target = props.projects.find(item => item.id === projectId)
  if (!target) return false

  switchConfirmationPending.value = true
  try {
    emit('switch', projectId)
    return true
  } finally {
    switchConfirmationPending.value = false
  }
}

function openProjectBrowser() {
  projectSearchQuery.value = quickProjectQuery.value.trim()
  projectTypeFilter.value = projectSearchQuery.value ? [] : [...defaultProjectTypes.value]
  quickProjectQuery.value = ''
  projectBrowserOpen.value = true
}

function closeProjectBrowser() {
  if (switchConfirmationPending.value) return
  projectBrowserOpen.value = false
  projectSearchQuery.value = ''
  projectTypeFilter.value = []
  projectCustomerFilter.value = ''
  projectExecutionUnitFilter.value = ''
  projectPersonFilter.value = ''
  projectPage.value = 1
  projectPageSize.value = 20
}

function selectBrowsedProject(projectId: string) {
  if (confirmProjectSwitch(projectId)) closeProjectBrowser()
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
    : `${displayUserName(owner)}编辑中`
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
          <SearchField :compact-placeholder="false" class="pdm-project-switcher__display" :class="{ 'is-disabled': switchConfirmationPending || Boolean(switchingProjectId) }" @keydown.esc.capture="quickProjectQuery = ''">
            <ElAutocomplete v-model="quickProjectQuery" class="pdm-project-switcher__input" aria-label="快速搜索项目" :placeholder="`${rootProject.code} · ${rootProject.name}`" :title="`${rootProject.code} · ${rootProject.name}`"
              :fetch-suggestions="quickProjectSuggestions" :debounce="100" :highlight-first-item="true" popper-class="pdm-project-quick-search" :disabled="switchConfirmationPending || Boolean(switchingProjectId)"
              @select="item => selectQuickProject(item.id)">
              <template #default="{ item }"><strong :title="item.code">{{ item.code }}</strong><small :title="item.name">{{ item.name }}</small></template>
            </ElAutocomplete>
            <template #action><button type="button" class="pdm-project-switcher__search-icon" aria-label="浏览项目" aria-haspopup="dialog" :aria-expanded="projectBrowserOpen" :disabled="switchConfirmationPending || Boolean(switchingProjectId)" title="浏览项目" @click="openProjectBrowser">搜索</button></template>
          </SearchField>
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
          <div><dt>事业部</dt><dd :title="rootProject.executionUnitName"><DepartmentName :name="rootProject.executionUnitName" fallback="待分配" /></dd></div>
          <div><dt>项目经理</dt><dd :title="displayUserName(rootProject.primaryProjectManager, '—')"><PersonName :name="displayUserName(rootProject.primaryProjectManager, '—')" /></dd></div>
          <div><dt>主设计</dt><dd :title="projectDesignLeads(rootProject).map(item => displayUserName(item)).join('、') || '—'"><PersonName :name="projectDesignLeads(rootProject).map(item => displayUserName(item)).join('、') || '—'" /></dd></div>
          <div><dt>设计人员</dt><dd :title="executionEngineerNames"><PersonName :name="executionEngineerNames" /></dd></div>
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
            :class="{ 'is-active': item.id === project.id, 'is-release-attention': Boolean(listAttentionTone(item)) }"
            :data-release-attention="listAttentionTone(item) || undefined"
            :title="listAttentionTitle(item)"
            :aria-current="item.id === project.id ? 'page' : undefined"
            :aria-label="`选择项目号 ${item.code}`"
            :aria-busy="item.id === switchingProjectId"
            :disabled="switchConfirmationPending || Boolean(switchingProjectId)"
            @click="item.id !== project.id && emit('switch', item.id)"
          >
            <ReleaseAttentionBorder v-if="listAttentionTone(item)" :tone="listAttentionTone(item) as 'error' | 'new'" />
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
        <nav class="pdm-project-tabs pdm-tabs" aria-label="项目功能">
          <button v-for="tab in visibleTabs" :key="tab.key" type="button" :class="{ 'is-active': activeTab === tab.key, 'is-release-attention': Boolean(tabAttentionTone(tab.key)) }" :data-release-attention="tabAttentionTone(tab.key) || undefined" :title="updateTitle(tab.key, tab.label)" @click="emit('tab', tab.key, Boolean(tabUpdates[tab.key]))"><ReleaseAttentionBorder v-if="tabAttentionTone(tab.key)" :tone="tabAttentionTone(tab.key) as 'error' | 'new'" /><span>{{ tab.label }}</span><i v-if="tabUpdates[tab.key] && !tabAttentionTone(tab.key)" class="pdm-tab-update-dot" aria-hidden="true" /></button>
          <button type="button" v-if="projectAccess.write.value && projectAccess.canViewTab('information')" class="pdm-project-tabs__settings" aria-label="项目设置" @click="projectSettingsOpen = true"><span>设置</span></button>
        </nav>
      </header>
      <slot />
    </section>

    <Transition name="pdm-drawer" appear><div v-if="projectBrowserOpen" class="pdm-dialog-backdrop" @click.self="closeProjectBrowser" @keydown.esc="closeProjectBrowser">
      <RawDrawerPanel class="pdm-project-browser-dialog" role="dialog" aria-modal="true" aria-labelledby="pdm-project-browser-title">
        <template #header="{ setActionTarget }"><header><div class="pdm-panel-topbar"><div class="pdm-panel-topbar-title">
          <div><h3 id="pdm-project-browser-title">浏览项目</h3><p>{{ defaultProjectTypes.length ? '默认显示标准项目 P，' : '' }}可多选项目分类，清空分类则显示全部。</p></div>

        </div><div class="pdm-panel-header-actions" :ref="setActionTarget"></div></div><button type="button" class="pdm-icon-button" aria-label="关闭项目浏览" :disabled="switchConfirmationPending" @click="closeProjectBrowser">×</button></header></template>
        <div class="pdm-project-browser-search">
          <SearchField><input v-model="projectSearchQuery" type="search" aria-label="搜索项目" placeholder="搜索项目号、名称、客户、型号或序列号" autofocus></SearchField>
          <span>找到 {{ filteredRootProjects.length }} 个项目</span>
        </div>
        <div class="pdm-project-browser-filters" aria-label="项目筛选">
          <ElSelect v-model="projectTypeFilter" class="pdm-project-prefix-filter" aria-label="项目分类筛选" multiple clearable collapse-tags collapse-tags-tooltip placeholder="全部项目分类">
            <ElOption v-for="type in projectTypeOptions" :key="type.code" :value="type.code" :label="`${type.name} ${type.code}`" />
          </ElSelect>
          <CompanyNameField :name="selectedCustomer?.name"><select v-model="projectCustomerFilter" aria-label="客户筛选"><option value="">全部客户</option><option v-for="customer in projectCustomerOptions" :key="customer.id" :value="customer.code">{{ customer.name }}</option></select></CompanyNameField>
          <DepartmentSelectField :name="selectedExecutionUnit?.name"><select v-model="projectExecutionUnitFilter" aria-label="事业部筛选"><option value="">全部事业部</option><option v-for="unit in projectExecutionUnitOptions" :key="unit.id" :value="unit.id">{{ unit.name }}</option></select></DepartmentSelectField>
          <UserSelect v-model="projectPersonFilter" aria-label="人员筛选"><option value="">全部人员</option><option v-for="person in projectPersonOptions" :key="person" :value="person">{{ displayUserName(person) }}</option></UserSelect>
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
            <CompanyName :name="item.customerName || '—'" />
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
        <PanelActions><footer><button type="button" class="pdm-secondary-action" :disabled="switchConfirmationPending" @click="closeProjectBrowser">取消</button></footer></PanelActions>
      </RawDrawerPanel>
    </div></Transition>

    <ProjectSettingsDrawer
      v-if="projectSettingsOpen"
      v-model="projectSettingsOpen"
      :project="project"
      :projects="projects"
      :token="token ?? ''"
      :can-copy-content="Boolean(canCopyContent)"
      :can-reset-content="Boolean(canResetContent)"
      :pending="Boolean(pending)"
      :on-preview-project-copy="onPreviewProjectCopy"
      :on-copy-project-content="onCopyProjectContent"
      :on-content-reset-complete="onContentResetComplete ?? (async () => undefined)"
    />
  </div>
</template>

<style scoped>
.pdm-project-browser-filters { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.pdm-project-prefix-filter { min-width: 0; width: 100%; }
.pdm-project-browser-list > button, .pdm-project-browser-table-head { text-align: center; align-items: center; }
.pdm-project-browser-list .pdm-empty-info { text-align: center; }
@media (max-width: 600px) {
  .pdm-project-browser-filters { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .pdm-project-browser-pagination { flex-wrap: wrap; }
  .pdm-project-browser-pagination > span, .pdm-project-browser-pagination label, .pdm-project-browser-pagination button { white-space: nowrap; }
}
.pdm-project-tabs button.is-release-attention, .pdm-project-family__list button.is-release-attention { position: relative; }
/* From Uiverse.io by vinodjangid07，输入框宽度随项目侧栏收缩。 */
.pdm-project-sidebar__context { height: calc(var(--pdm-tab-primary) + 2 * var(--pdm-tabs-padding)); flex-basis: calc(var(--pdm-tab-primary) + 2 * var(--pdm-tabs-padding)); padding: var(--pdm-tabs-padding) var(--pdm-content-inset); border-color: transparent; }
.pdm-project-switcher__display { height: var(--pdm-tab-primary); gap: var(--pdm-control-gap); padding: 0; overflow: visible; border: 0; border-radius: 0; background: transparent; font-size: 16px; font-weight: 400; }
.pdm-project-switcher__display:not(.is-disabled):hover,
.pdm-project-switcher__display:not(.is-disabled):focus-within { transform: none; border-color: transparent; box-shadow: none; }
.pdm-project-switcher__display > :deep(.pdm-project-switcher__input) { width: 170px; flex: 1 1 170px; }
.pdm-project-switcher__display :deep(.pdm-project-switcher__input .el-input__wrapper) { height: var(--pdm-tab-primary); min-height: var(--pdm-tab-primary); padding: 0 0 0 15px; border: 1px solid #d0d7e1; border-radius: 6px; background: var(--pdm-surface); box-shadow: none !important; }
.pdm-project-switcher__display :deep(.pdm-project-switcher__input .el-input__wrapper.is-focus) { border-color: #292524; }
.pdm-project-switcher__display :deep(.pdm-project-switcher__input .el-input__inner) { height: calc(var(--pdm-tab-primary) - 2px); line-height: calc(var(--pdm-tab-primary) - 2px); font-size: .7em; color: #292524; }
.pdm-project-switcher__search-icon { width: var(--pdm-tab-primary); min-width: var(--pdm-tab-primary); height: var(--pdm-tab-primary); flex: 0 0 var(--pdm-tab-primary); padding: 0; border: none; border-radius: 6px; color: #fff; background: #292524; box-shadow: none; transition: all .3s; font-size: .7em; gap: 4px; }
.pdm-project-switcher__search-icon:hover:not(:disabled) { color: #fff; }
.pdm-project-switcher__search-icon:hover:not(:disabled) .arrow { margin-right: 0; animation: jello-vertical .9s both; transform-origin: left; }
.pdm-project-switcher__search-icon:active:not(:disabled) { transform: scale(.9); }
.pdm-project-switcher__search-icon:focus-visible { outline: 2px solid #292524; outline-offset: 2px; }
@keyframes jello-vertical {
  0%, 100% { transform: scale3d(1, 1, 1); }
  30% { transform: scale3d(.75, 1.25, 1); }
  40% { transform: scale3d(1.25, .75, 1); }
  50% { transform: scale3d(.85, 1.15, 1); }
  65% { transform: scale3d(1.05, .95, 1); }
  75% { transform: scale3d(.95, 1.05, 1); }
}
@media (prefers-reduced-motion: reduce) {
  .pdm-project-switcher__search-icon { transition: none; }
  .pdm-project-switcher__search-icon:hover:not(:disabled) .arrow { animation: none; }
  .pdm-project-switcher__search-icon:active:not(:disabled) { transform: none; }
}
</style>
