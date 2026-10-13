<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { ChevronDown, ChevronRight, FolderKanban, FolderPlus, Search } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import ProjectNameplateDialog from './ProjectNameplateDialog.vue'
import type { CreateProjectInput, CreateSubprojectInput, MainProjectStaffingInput, OrganizationDirectory, PdmCustomer, PdmUser, ProjectNumberingOptions, ProjectSummary, UpdateProjectInput } from '../types'

const props = defineProps<{
  projects: ProjectSummary[]
  token?: string
  numberingOptions: ProjectNumberingOptions
  customers: PdmCustomer[]
  users: PdmUser[]
  organizationDirectory: OrganizationDirectory
  currentUsername: string
  administrator: boolean
  canCreate: boolean
  canEdit: boolean
  canDelete: boolean
  canCreateSubproject: boolean
  pending: boolean
  onCreate: (input: CreateProjectInput) => Promise<ProjectSummary>
  onCreateSubproject: (parentProjectId: string, input: CreateSubprojectInput) => Promise<ProjectSummary>
  onUpdateProject: (projectId: string, input: UpdateProjectInput) => Promise<ProjectSummary>
  onDeleteProject: (projectId: string) => Promise<void>
  onUpdateExecutionUnit: (projectId: string, executionUnitId: string) => Promise<ProjectSummary>
  onUpdateMainStaffing: (projectId: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (projectId: string, designers: string[]) => Promise<ProjectSummary>
  onUpdateChildManager: (projectId: string, projectManager: string) => Promise<ProjectSummary>
}>()

const emit = defineEmits<{ open: [projectId: string] }>()
type ProjectAction = 'delete' | 'create-child'
const dialogOpen = ref(false)
const nameplateProject = ref<ProjectSummary | null>(null)
const nameplateOpen = ref(false)
const childDialogOpen = ref(false)
const childParent = ref<ProjectSummary | null>(null)
const executionDialogOpen = ref(false)
const executionProject = ref<ProjectSummary | null>(null)
const executionUnitId = ref('')
const expanded = ref(new Set<string>())
const projectQuery = ref('')
const projectSearchDraft = ref('')
const responsibleFilter = ref('')
const executionUnitFilter = ref('')
const organizationFilter = ref('')
const selectedDivisionId = ref('')
const collapsedOrganizations = ref(new Set<string>())
const projectOrganizationGroups = computed(() => props.numberingOptions.organizations.map(organization => ({
  ...organization,
  divisions: props.organizationDirectory.units.filter(unit => unit.organizationId === organization.id && unit.isActive && unit.canManufacture)
    .sort((left, right) => left.code.localeCompare(right.code, 'zh-CN')),
})))
function selectProjectOrganization(organizationId = '', divisionId = '') {
  organizationFilter.value = organizationId
  selectedDivisionId.value = divisionId
  executionUnitFilter.value = ''
}
function toggleOrganization(organizationId: string) {
  const next = new Set(collapsedOrganizations.value)
  if (next.has(organizationId)) next.delete(organizationId)
  else next.add(organizationId)
  collapsedOrganizations.value = next
}
const projectManagerFilter = ref('')
const designOwnerFilter = ref('')
const form = reactive<CreateProjectInput>({
  organizationId: '', projectTypeCode: '', equipmentTypeCode: 0, customerId: '', name: '', projectAlias: '', signedDate: '', quantity: 1, bomItemCategoryCode: '0301',
})
const childForm = reactive<CreateSubprojectInput>({ name: '', projectAlias: '', quantity: 1 })

function compareProjectCodeDescending(left: ProjectSummary, right: ProjectSummary) {
  return right.code.localeCompare(left.code, 'zh-CN', { numeric: true, sensitivity: 'base' })
}

function userDisplayName(username?: string | null, emptyText = '—') {
  const normalized = username?.trim()
  if (!normalized) return emptyText
  return props.organizationDirectory.users.find(user => user.username.localeCompare(normalized, undefined, { sensitivity: 'accent' }) === 0)?.displayName || normalized
}

function projectDesignLeads(project: ProjectSummary) {
  return project.designLeads?.length ? project.designLeads : project.designLead ? [project.designLead] : []
}

const rootProjects = computed(() => props.projects.filter(item => !item.parentProjectId).sort(compareProjectCodeDescending))
const activeCustomers = computed(() => props.customers.filter(item => item.isActive))
const responsibleOptions = computed(() => [...new Set(props.projects.flatMap(item => [item.primaryProjectManager, ...projectDesignLeads(item), ...item.collaborativeProjectManagers, ...item.designers]).filter((item): item is string => Boolean(item)))].sort((left, right) => userDisplayName(left).localeCompare(userDisplayName(right), 'zh-CN')))
const executionUnitOptions = computed(() => [...new Set(props.projects.map(item => item.executionUnitName).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const projectManagerOptions = computed(() => [...new Set(props.projects.flatMap(item => [item.primaryProjectManager, ...item.collaborativeProjectManagers]).filter((item): item is string => Boolean(item)))].sort((left, right) => userDisplayName(left).localeCompare(userDisplayName(right), 'zh-CN')))
const designOwnerOptions = computed(() => [...new Set(props.projects.flatMap(item => [...projectDesignLeads(item), ...item.designers]).filter((item): item is string => Boolean(item)))].sort((left, right) => userDisplayName(left).localeCompare(userDisplayName(right), 'zh-CN')))
const executionUnitCandidates = computed(() => props.organizationDirectory.units
  .filter(unit => unit.kind === 'BusinessDivision' && unit.isActive)
  .sort((left, right) => Number(!left.name.includes('事业部')) - Number(!right.name.includes('事业部'))
    || left.name.localeCompare(right.name, 'zh-CN', { numeric: true, sensitivity: 'base' })))
const selectedCustomer = computed(() => props.customers.find(item => item.id === form.customerId))
const childrenByParent = computed(() => {
  const result = new Map<string, ProjectSummary[]>()
  for (const project of props.projects.filter(item => item.parentProjectId)) {
    const children = result.get(project.parentProjectId!) ?? []
    children.push(project)
    result.set(project.parentProjectId!, children)
  }
  for (const children of result.values()) children.sort((left, right) => (left.childSequence ?? 0) - (right.childSequence ?? 0))
  return result
})
const normalizedProjectQuery = computed(() => projectQuery.value.trim().toLocaleLowerCase())
function matchesProject(project: ProjectSummary) {
  if (organizationFilter.value && project.organizationId !== organizationFilter.value) return false
  if (selectedDivisionId.value) {
    const division = props.organizationDirectory.units.find(unit => unit.id === selectedDivisionId.value)
    if (project.executionUnitId ? project.executionUnitId !== selectedDivisionId.value : project.executionUnitName !== division?.name) return false
  }
  if (responsibleFilter.value && ![project.primaryProjectManager, ...projectDesignLeads(project), ...project.collaborativeProjectManagers, ...project.designers].includes(responsibleFilter.value)) return false
  if (executionUnitFilter.value && project.executionUnitName !== executionUnitFilter.value) return false
  if (projectManagerFilter.value && ![project.primaryProjectManager, ...project.collaborativeProjectManagers].includes(projectManagerFilter.value)) return false
  if (designOwnerFilter.value && ![...projectDesignLeads(project), ...project.designers].includes(designOwnerFilter.value)) return false
  const query = normalizedProjectQuery.value
  if (!query) return true
  return [
    project.code,
    project.name,
    project.projectAlias,
    project.deviceModel,
    project.customerCode,
    project.customerName,
    project.stage,
    ...project.serialNumbers,
    project.executionUnitName,
    project.primaryProjectManager,
    ...projectDesignLeads(project),
    ...project.collaborativeProjectManagers,
    ...project.designers,
  ].some(value => String(value ?? '').toLocaleLowerCase().includes(query))
}
const filteredChildrenByParent = computed(() => {
  const result = new Map<string, ProjectSummary[]>()
  const hasMatchingDescendant = (projectId: string): boolean => (childrenByParent.value.get(projectId) ?? [])
    .some(child => matchesProject(child) || hasMatchingDescendant(child.id))
  for (const parent of props.projects) {
    const children = (childrenByParent.value.get(parent.id) ?? [])
      .filter(child => matchesProject(child) || hasMatchingDescendant(child.id))
    if (children.length > 0) result.set(parent.id, children)
  }
  return result
})
const visibleRootProjects = computed(() => rootProjects.value.filter(parent => {
  const hasMatchingChild = filteredChildrenByParent.value.has(parent.id)
  return matchesProject(parent) || hasMatchingChild
}))
const expandableProjectIds = computed(() => {
  const result: string[] = []
  const append = (project: ProjectSummary) => {
    const children = filteredChildrenByParent.value.get(project.id) ?? []
    if (children.length === 0) return
    result.push(project.id)
    children.forEach(append)
  }
  visibleRootProjects.value.forEach(append)
  return result
})
watch([organizationFilter, selectedDivisionId, projectQuery, responsibleFilter, executionUnitFilter, projectManagerFilter, designOwnerFilter], () => { expanded.value = new Set() })
function visibleChildren(parentId: string) {
  const children = filteredChildrenByParent.value.get(parentId) ?? []
  return expanded.value.has(parentId) ? children : []
}
function isEffectivelyExpanded(parentId: string) {
  return filteredChildrenByParent.value.has(parentId)
    && expanded.value.has(parentId)
}
const visibleProjectRows = computed(() => {
  const result: Array<{ project: ProjectSummary; depth: number }> = []
  const append = (project: ProjectSummary, depth: number) => {
    result.push({ project, depth })
    const children = filteredChildrenByParent.value.get(project.id) ?? []
    if (!expanded.value.has(project.id)) return
    children.forEach(child => append(child, depth + 1))
  }
  visibleRootProjects.value.forEach(project => append(project, 0))
  return result
})
watch(() => props.projects.map(item => item.id).join(','), () => {
  const validIds = new Set(props.projects.map(item => item.id))
  expanded.value = new Set([...expanded.value].filter(id => validIds.has(id)))
}, { immediate: true })

function localDate() {
  const date = new Date()
  const offset = date.getTimezoneOffset() * 60_000
  return new Date(date.getTime() - offset).toISOString().slice(0, 10)
}

function openCreateDialog() {
  form.organizationId = props.numberingOptions.organizations[0]?.id ?? ''
  form.projectTypeCode = props.numberingOptions.projectTypes[0]?.code ?? ''
  form.equipmentTypeCode = props.numberingOptions.equipmentTypes[0]?.code ?? 0
  form.customerId = activeCustomers.value[0]?.id ?? ''
  form.name = ''
  form.projectAlias = ''
  form.signedDate = localDate()
  form.quantity = 1
  form.bomItemCategoryCode = '0301'
  dialogOpen.value = true
}

function openExecutionDialog(project: ProjectSummary) {
  executionProject.value = project
  executionUnitId.value = project.executionUnitId ?? executionUnitCandidates.value.find(unit => unit.organizationId === project.organizationId && unit.canManufacture === true)?.id ?? ''
  executionDialogOpen.value = true
}

async function saveExecutionUnit() {
  if (!executionProject.value || !executionUnitId.value) return ElMessage.warning('请选择执行事业部')
  if (executionUnitCandidates.value.find(unit => unit.id === executionUnitId.value)?.canManufacture !== true) return ElMessage.warning('所选部门未设置为制造部门，不能承接项目')
  try {
    await props.onUpdateExecutionUnit(executionProject.value.id, executionUnitId.value)
    executionDialogOpen.value = false
    ElMessage.success('执行事业部已分配；原项目分工已清空')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '执行事业部分配失败') }
}

function openChildDialog(parent: ProjectSummary) {
  childParent.value = parent
  childForm.name = ''
  childForm.projectAlias = ''
  childForm.quantity = 1
  childDialogOpen.value = true
}

function toggle(projectId: string) {
  const next = new Set(expanded.value)
  if (next.has(projectId)) next.delete(projectId)
  else next.add(projectId)
  expanded.value = next
}

const allProjectDetailsExpanded = computed(() => expandableProjectIds.value.length > 0
  && expandableProjectIds.value.every(projectId => expanded.value.has(projectId)))

function toggleAllProjectDetails() {
  const next = new Set(expanded.value)
  if (allProjectDetailsExpanded.value) expandableProjectIds.value.forEach(projectId => next.delete(projectId))
  else expandableProjectIds.value.forEach(projectId => next.add(projectId))
  expanded.value = next
}

async function submitProject() {
  if (!form.organizationId || !form.projectTypeCode || !form.customerId || !form.name.trim() || !form.signedDate || form.quantity < 1) {
    ElMessage.warning('请完整填写项目资料并选择客户')
    return
  }
  try {
    await props.onCreate({ ...form, name: form.name.trim(), projectAlias: form.projectAlias?.trim() })
    dialogOpen.value = false
    ElMessage.success('项目号、设备型号和序列号已自动生成')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '项目创建失败')
  }
}

async function submitSubproject() {
  if (!childParent.value || !childForm.name.trim() || childForm.quantity < 1) {
    ElMessage.warning('请填写设备子项目名称和数量')
    return
  }
  try {
    const parentId = childParent.value.id
    await props.onCreateSubproject(parentId, { name: childForm.name.trim(), projectAlias: childForm.projectAlias?.trim(), quantity: childForm.quantity })
    expanded.value = new Set([...expanded.value, parentId])
    childDialogOpen.value = false
    ElMessage.success('子项目号、设备型号和序列号已自动生成')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '子项目创建失败')
  }
}

async function deleteProject(project: ProjectSummary) {
  try {
    await ElMessageBox.confirm(
      `确认删除项目“${project.code} · ${project.name}”吗？\n\n仅没有子项目、图档、BOM、结构快照和审批发布包的项目可以删除。删除成功后，对应项目号、型号流水和序列号将释放并供后续创建优先使用。`,
      '确认删除项目',
      { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' },
    )
    await props.onDeleteProject(project.id)
    ElMessage.success(`项目 ${project.code} 已删除，系统编号已释放`)
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '项目删除失败')
  }
}

function projectManagerText(project: ProjectSummary) {
  const managers = [project.primaryProjectManager, ...project.collaborativeProjectManagers].filter((item): item is string => Boolean(item))
  return managers.length ? [...new Set(managers)].map(item => userDisplayName(item)).join('、') : '待分配'
}

function designOwnerText(project: ProjectSummary) {
  if (project.parentProjectId) return project.designers.length ? project.designers.map(item => userDisplayName(item)).join('、') : '待分配'
  const designLeads = projectDesignLeads(project)
  return designLeads.length ? designLeads.map(item => userDisplayName(item)).join('、') : '待分配'
}

function canAssignExecutionUnit(project: ProjectSummary) {
  return project.canAssignExecutionUnit || (props.administrator && !project.parentProjectId)
}

function handleProjectAction(project: ProjectSummary, action: ProjectAction) {
  if (action === 'delete') void deleteProject(project)
  else if (action === 'create-child') openChildDialog(project)
}
</script>

<template>
  <section class="pdm-project-manager" aria-label="项目中心">
    <section class="pdm-project-list pdm-project-list-layout">
      <aside class="pdm-panel pdm-project-classification" aria-label="项目事业部分类">
        <div role="tree" aria-label="公司与事业部">
          <div v-for="organization in projectOrganizationGroups" :key="organization.id" role="none">
            <div class="pdm-project-classification__company">
              <button type="button" class="pdm-tree-toggle" :aria-label="`${collapsedOrganizations.has(organization.id) ? '展开' : '折叠'}${organization.name}的事业部`" @click="toggleOrganization(organization.id)"><span class="pdm-classification-caret" :class="{ 'is-expanded': !collapsedOrganizations.has(organization.id) }" aria-hidden="true" /></button>
              <button type="button" role="treeitem" :aria-expanded="!collapsedOrganizations.has(organization.id)" :aria-selected="organizationFilter === organization.id && !selectedDivisionId" :class="{ 'is-active': organizationFilter === organization.id && !selectedDivisionId }" :title="organization.name" @click="selectProjectOrganization(organization.id)">{{ organization.name }}</button>
            </div>
            <div v-show="!collapsedOrganizations.has(organization.id)" role="group">
              <button v-for="division in organization.divisions" :key="division.id" type="button" role="treeitem" class="pdm-project-classification__division" :aria-selected="selectedDivisionId === division.id" :class="{ 'is-active': selectedDivisionId === division.id }" @click="selectProjectOrganization(organization.id, division.id)">{{ division.name }}</button>
            </div>
          </div>
        </div>
      </aside>
      <section class="pdm-panel pdm-project-table-panel" aria-label="项目列表">
        <div class="pdm-project-list-toolbar" aria-label="项目筛选">
          <button v-if="canCreate" type="button" class="pdm-primary-action pdm-project-create-action" @click="openCreateDialog"><FolderPlus :size="16" />创建主项目</button>
          <button type="button" class="pdm-secondary-action pdm-project-collapse-action" :aria-label="`${allProjectDetailsExpanded ? '折叠' : '展开'}全部项目明细`" :disabled="expandableProjectIds.length === 0" @click="toggleAllProjectDetails"><ChevronDown v-if="allProjectDetailsExpanded" :size="15" /><ChevronRight v-else :size="15" />{{ allProjectDetailsExpanded ? '折叠明细' : '展开明细' }}</button>
          <div class="pdm-project-filters">
            <label class="pdm-inline-search"><Search :size="15" /><input v-model="projectSearchDraft" @keyup.enter="projectQuery = projectSearchDraft" type="search" aria-label="搜索项目" placeholder="搜索项目号、名称、客户或项目人员"></label>
            <button type="button" class="pdm-secondary-action" @click="projectQuery = projectSearchDraft">搜索</button>
            <select v-model="responsibleFilter" aria-label="项目人员筛选"><option value="">全部项目人员</option><option v-for="username in responsibleOptions" :key="username" :value="username">{{ userDisplayName(username) }}</option></select>
            <select v-model="executionUnitFilter" aria-label="事业部筛选"><option value="">全部事业部</option><option v-for="unit in executionUnitOptions" :key="unit" :value="unit">{{ unit }}</option></select>
            <select v-model="projectManagerFilter" aria-label="项目经理筛选"><option value="">全部项目经理</option><option v-for="username in projectManagerOptions" :key="username" :value="username">{{ userDisplayName(username) }}</option></select>
            <select v-model="designOwnerFilter" aria-label="主设工程师筛选"><option value="">全部主设／工程师</option><option v-for="username in designOwnerOptions" :key="username" :value="username">{{ userDisplayName(username) }}</option></select>
          </div>
        </div>
        <div v-if="visibleRootProjects.length" class="pdm-table-scroll pdm-project-number-scroll">
        <table class="pdm-project-table pdm-project-number-table">
          <colgroup><col v-for="(weight, index) in [2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1]" :key="index" :style="{ width: `${weight / 19 * 100}%` }"></colgroup>
          <thead><tr><th>项目号</th><th>项目名称</th><th>别名</th><th>型号</th><th>序列号</th><th>客户</th><th>事业部</th><th>项目经理</th><th>主设</th><th>状态</th><th>订单日期</th><th>铭牌</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="row in visibleProjectRows" :key="row.project.id" :class="{ 'is-child': row.depth > 0 }">
              <td><div class="pdm-project-code-cell" :class="{ 'is-child-code': row.depth > 0 }" :style="{ transform: `translateX(${row.depth * 2}ch)` }"><button v-if="filteredChildrenByParent.has(row.project.id)" type="button" class="pdm-tree-toggle" :aria-label="`${isEffectivelyExpanded(row.project.id) ? '折叠' : '展开'}${row.project.code}的子项目`" @click="toggle(row.project.id)"><ChevronDown v-if="isEffectivelyExpanded(row.project.id)" :size="15" /><ChevronRight v-else :size="15" /></button><span v-else class="pdm-project-code-spacer"></span><button type="button" class="pdm-project-code-link" :aria-label="`进入项目 ${row.project.code}`" @click="emit('open', row.project.id)">{{ row.project.code }}</button><button v-if="row.depth === 0 && canCreateSubproject && row.project.effectiveProjectPermissions?.includes('project.child.create') && row.project.deviceModel" type="button" class="pdm-project-add-child" :aria-label="`为${row.project.code}创建子项目`" title="创建子项目" @click.stop="openChildDialog(row.project)"><FolderPlus :size="14" /></button><span v-else class="pdm-project-code-spacer"></span></div></td>
              <td>{{ row.project.name }}</td>
              <td>{{ row.project.projectAlias || '—' }}</td>
              <td>{{ row.project.deviceModel || '旧项目未编号' }}</td>
              <td class="pdm-project-serials"><span v-for="serial in row.project.serialNumbers" :key="serial" class="pdm-serial-line">{{ serial }}</span><span v-if="row.project.serialNumbers.length === 0">—</span></td>
              <td>{{ row.project.customerName || '—' }}</td>
              <td><button v-if="row.depth === 0 && canAssignExecutionUnit(row.project)" type="button" class="pdm-project-assignment-button" :aria-label="`分配事业部 ${row.project.code}`" title="点击分配执行事业部" @click="openExecutionDialog(row.project)"><span>{{ row.project.executionUnitName || '待分配' }}</span></button><div v-else class="pdm-project-cell-text" :title="row.project.executionUnitName || '待分配'">{{ row.project.executionUnitName || '待分配' }}</div></td>
              <td><div class="pdm-project-cell-text" :title="projectManagerText(row.project)">{{ projectManagerText(row.project) }}</div></td>
              <td><div class="pdm-project-cell-text" :title="designOwnerText(row.project)">{{ designOwnerText(row.project) }}</div></td>
              <td><span class="pdm-status" :class="row.project.stage === '进行中' ? 'is-ok' : 'is-warn'">{{ row.project.stage }}</span></td>
              <td class="pdm-project-order-date">{{ row.project.signedDate || '—' }}</td>
              <td><button type="button" class="pdm-text-action" :disabled="!row.project.canReadContent || !token" :aria-label="`查看铭牌 ${row.project.code}`" @click="nameplateProject = row.project; nameplateOpen = true">查看铭牌</button></td>
              <td><el-dropdown :aria-label="`操作项目${row.project.code}`" trigger="click" placement="bottom-end" popper-class="pdm-project-action-menu" @command="handleProjectAction(row.project, $event)"><button type="button" class="pdm-project-action-trigger" :aria-label="`操作项目${row.project.code}`">操作<ChevronDown :size="13" /></button><template #dropdown><el-dropdown-menu><el-dropdown-item v-if="row.depth === 0 && canCreateSubproject && row.project.effectiveProjectPermissions?.includes('project.child.create') && row.project.deviceModel" command="create-child">创建子项目</el-dropdown-item><el-dropdown-item v-if="canDelete && row.project.effectiveProjectPermissions?.includes('project.delete')" command="delete" divided>删除项目</el-dropdown-item></el-dropdown-menu></template></el-dropdown></td>
            </tr>
          </tbody>
        </table>
      </div>
        <div v-else-if="projects.length" class="pdm-project-empty"><Search :size="42" /><h2>未找到匹配项目</h2><p>请调整搜索内容或筛选条件。</p><button type="button" class="pdm-secondary-action" @click="projectQuery=''; projectSearchDraft=''; responsibleFilter=''; selectProjectOrganization()">清除筛选</button></div>
        <div v-else class="pdm-project-empty"><FolderKanban :size="42" /><h2>当前公司还没有项目</h2><p v-if="canCreate">创建主项目后，可继续分配事业部和配置项目岗位。</p><p v-else>请联系有创建权限的人员新建项目。</p></div>
      </section>
    </section>


    <el-dialog v-model="dialogOpen" title="创建主项目" width="680px" :close-on-click-modal="false">
      <form class="pdm-project-form" aria-label="创建PLM项目" @submit.prevent="submitProject">
        <label>所属公司<select v-model="form.organizationId" name="organizationId"><option v-for="item in numberingOptions.organizations" :key="item.id" :value="item.id">{{ item.name }}（{{ item.projectCompanyCode }} / {{ item.modelCompanyCode }}）</option></select></label>
        <label>项目类型<select v-model="form.projectTypeCode" name="projectTypeCode"><option v-for="item in numberingOptions.projectTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
        <label>BOM根节点类型<select v-model="form.bomItemCategoryCode" name="bomItemCategoryCode"><option value="0301">0301 · 产线</option><option value="0302">0302 · 设备（可独立作为根节点）</option></select></label>
        <label>设备类型<select v-model.number="form.equipmentTypeCode" name="equipmentTypeCode"><option v-for="item in numberingOptions.equipmentTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
        <label class="is-wide">客户<el-select v-model="form.customerId" class="pdm-project-customer-select" name="customerId" filterable placeholder="输入客户名称或编码筛选" no-match-text="未找到匹配客户" style="width:100%"><el-option v-for="item in activeCustomers" :key="item.id" :label="`${item.name}（${item.code}）`" :value="item.id" /></el-select><small v-if="selectedCustomer">客户编码由U9C客户数据自动带出：{{ selectedCustomer.code }}</small><small v-else-if="activeCustomers.length === 0">尚未同步U9C客户，请先到“系统管理 → U9C接口 → 客户查询”执行同步。</small></label>
        <label>项目名称<input v-model="form.name" name="projectName" maxlength="200" placeholder="人工录入"></label>
        <label>项目别名<input v-model="form.projectAlias" name="projectAlias" maxlength="200" placeholder="人工录入，可选"></label>
        <label>签订日期<input v-model="form.signedDate" name="signedDate" type="date"></label>
        <label>数量<input v-model.number="form.quantity" name="quantity" type="number" min="1" max="10000"></label>
        <p class="is-wide">0301产线通常包含多台0302设备；0302也可独立作为根节点并继续嵌套0302设备。项目号、客户流水和序列号由服务器生成。</p>
      </form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="dialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submitProject">{{ pending ? '正在创建…' : '创建并取号' }}</button></template>
    </el-dialog>

    <el-dialog v-model="executionDialogOpen" :title="`分配执行事业部 · ${executionProject?.code ?? ''}`" width="560px" :close-on-click-modal="false">
      <label class="pdm-dialog-field">执行事业部<el-select v-model="executionUnitId" filterable style="width:100%"><el-option v-for="unit in executionUnitCandidates.filter(item => item.organizationId === executionProject?.organizationId)" :key="unit.id" :label="unit.name" :value="unit.id" :disabled="unit.canManufacture !== true" /></el-select></label>
      <p class="pdm-counter-note">由系统管理员或拥有“分配执行事业部”权限的计划人员操作。更换事业部会清空项目经理、主设和子项目工程师。</p>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="executionDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveExecutionUnit">确认分配</button></template>
    </el-dialog>




    <el-dialog v-model="childDialogOpen" :title="`创建0302设备子项目 · ${childParent?.code ?? ''}`" width="560px" :close-on-click-modal="false">
      <form class="pdm-project-form" aria-label="创建PLM子项目" @submit.prevent="submitSubproject">
        <label>子项目名称<input v-model="childForm.name" name="childProjectName" maxlength="200" placeholder="人工录入"></label>
        <label>子项目别名<input v-model="childForm.projectAlias" name="childProjectAlias" maxlength="200" placeholder="人工录入，可选"></label>
        <label>数量<input v-model.number="childForm.quantity" name="childQuantity" type="number" min="1" max="10000"></label>
        <p class="is-wide">每台设备建立为独立0302子项目；型号规则沿用上级项目，编号按上级追加-1、-2…，U9C BOM版本固定A1。</p>
      </form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="childDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submitSubproject">{{ pending ? '正在创建…' : '创建子项目' }}</button></template>
    </el-dialog>

    <ProjectNameplateDialog v-if="nameplateProject && token" v-model="nameplateOpen" :project="nameplateProject" :token="token" />
  </section>
</template>
