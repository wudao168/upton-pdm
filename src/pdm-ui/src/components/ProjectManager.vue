<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { ChevronDown, ChevronRight, FolderKanban, FolderPlus, Pencil, Search } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import type { CreateProjectInput, CreateSubprojectInput, MainProjectStaffingInput, OrganizationDirectory, PdmCustomer, PdmUser, ProjectNumberingOptions, ProjectSummary } from '../types'

const props = defineProps<{
  projects: ProjectSummary[]
  numberingOptions: ProjectNumberingOptions
  customers: PdmCustomer[]
  users: PdmUser[]
  organizationDirectory: OrganizationDirectory
  currentUsername: string
  administrator: boolean
  canCreate: boolean
  canCreateSubproject: boolean
  pending: boolean
  onCreate: (input: CreateProjectInput) => Promise<ProjectSummary>
  onCreateSubproject: (parentProjectId: string, input: CreateSubprojectInput) => Promise<ProjectSummary>
  onUpdateExecutionUnit: (projectId: string, executionUnitId: string) => Promise<ProjectSummary>
  onUpdateMainStaffing: (projectId: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (projectId: string, designers: string[]) => Promise<ProjectSummary>
}>()

const emit = defineEmits<{ open: [projectId: string] }>()
type ProjectAction = 'open' | 'assign-execution' | 'configure-staffing' | 'create-child' | 'assign-designers'
const dialogOpen = ref(false)
const childDialogOpen = ref(false)
const childParent = ref<ProjectSummary | null>(null)
const executionDialogOpen = ref(false)
const executionProject = ref<ProjectSummary | null>(null)
const executionUnitId = ref('')
const staffingDialogOpen = ref(false)
const staffingProject = ref<ProjectSummary | null>(null)
const staffingFocus = ref<'managers' | 'design'>('managers')
const staffingForm = reactive<MainProjectStaffingInput>({ primaryProjectManager: '', collaborativeProjectManagers: [], designLead: '' })
const designerDialogOpen = ref(false)
const designerProject = ref<ProjectSummary | null>(null)
const designerDraft = ref<string[]>([])
const expanded = ref(new Set<string>())
const projectQuery = ref('')
const hierarchyFilter = ref<'all' | 'parent' | 'child'>('all')
const stageFilter = ref('')
const responsibleFilter = ref('')
const executionUnitFilter = ref('')
const projectManagerFilter = ref('')
const designOwnerFilter = ref('')
const form = reactive<CreateProjectInput>({
  organizationId: '', projectTypeCode: '', equipmentTypeCode: 0, customerId: '', name: '', projectAlias: '', signedDate: '', quantity: 1,
})
const childForm = reactive<CreateSubprojectInput>({ name: '', projectAlias: '', quantity: 1 })

const rootProjects = computed(() => props.projects.filter(item => !item.parentProjectId))
const activeCustomers = computed(() => props.customers.filter(item => item.isActive))
const responsibleOptions = computed(() => [...new Set(props.projects.flatMap(item => [item.primaryProjectManager, item.designLead, ...item.collaborativeProjectManagers, ...item.designers]).filter((item): item is string => Boolean(item)))].sort())
const executionUnitOptions = computed(() => [...new Set(props.projects.map(item => item.executionUnitName).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const projectManagerOptions = computed(() => [...new Set(props.projects.flatMap(item => [item.primaryProjectManager, ...item.collaborativeProjectManagers]).filter((item): item is string => Boolean(item)))].sort())
const designOwnerOptions = computed(() => [...new Set(props.projects.flatMap(item => [item.designLead, ...item.designers]).filter((item): item is string => Boolean(item)))].sort())
const businessDivisions = computed(() => props.organizationDirectory.units.filter(unit => unit.kind === 'BusinessDivision' && unit.isActive))
const stageOptions = computed(() => [...new Set(props.projects.map(item => item.stage).filter(Boolean))].sort())
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
  if (stageFilter.value && project.stage !== stageFilter.value) return false
  if (responsibleFilter.value && ![project.primaryProjectManager, project.designLead, ...project.collaborativeProjectManagers, ...project.designers].includes(responsibleFilter.value)) return false
  if (executionUnitFilter.value && project.executionUnitName !== executionUnitFilter.value) return false
  if (projectManagerFilter.value && ![project.primaryProjectManager, ...project.collaborativeProjectManagers].includes(projectManagerFilter.value)) return false
  if (designOwnerFilter.value && ![project.designLead, ...project.designers].includes(designOwnerFilter.value)) return false
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
    project.designLead,
    ...project.collaborativeProjectManagers,
    ...project.designers,
  ].some(value => String(value ?? '').toLocaleLowerCase().includes(query))
}
const filteredChildrenByParent = computed(() => {
  const result = new Map<string, ProjectSummary[]>()
  if (hierarchyFilter.value === 'parent') return result
  for (const parent of rootProjects.value) {
    const children = (childrenByParent.value.get(parent.id) ?? []).filter(matchesProject)
    if (children.length > 0) result.set(parent.id, children)
  }
  return result
})
const visibleRootProjects = computed(() => rootProjects.value.filter(parent => {
  if (hierarchyFilter.value === 'parent') return matchesProject(parent)
  const hasMatchingChild = filteredChildrenByParent.value.has(parent.id)
  if (hierarchyFilter.value === 'child') return hasMatchingChild
  return matchesProject(parent) || hasMatchingChild
}))
const hasActiveProjectFilter = computed(() => Boolean(
  normalizedProjectQuery.value || stageFilter.value || responsibleFilter.value || executionUnitFilter.value || projectManagerFilter.value || designOwnerFilter.value,
))
function visibleChildren(parentId: string) {
  const children = filteredChildrenByParent.value.get(parentId) ?? []
  if (hasActiveProjectFilter.value || hierarchyFilter.value === 'child') return children
  return expanded.value.has(parentId) ? children : []
}
function isEffectivelyExpanded(parentId: string) {
  return filteredChildrenByParent.value.has(parentId)
    && (hasActiveProjectFilter.value || hierarchyFilter.value === 'child' || expanded.value.has(parentId))
}
watch(() => rootProjects.value.map(item => item.id).join(','), () => {
  const validIds = new Set(rootProjects.value.map(item => item.id))
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
  dialogOpen.value = true
}

function openExecutionDialog(project: ProjectSummary) {
  executionProject.value = project
  executionUnitId.value = project.executionUnitId ?? businessDivisions.value.find(unit => unit.organizationId === project.organizationId)?.id ?? ''
  executionDialogOpen.value = true
}

function isUnitWithin(unitId: string, divisionId: string) {
  let current = props.organizationDirectory.units.find(unit => unit.id === unitId)
  while (current) {
    if (current.id === divisionId) return true
    current = current.parentUnitId ? props.organizationDirectory.units.find(unit => unit.id === current!.parentUnitId) : undefined
  }
  return false
}

function divisionOfUser(username: string) {
  const memberships = props.organizationDirectory.memberships.filter(item => item.username === username).sort((left, right) => Number(right.isPrimary) - Number(left.isPrimary))
  for (const membership of memberships) {
    let current = props.organizationDirectory.units.find(unit => unit.id === membership.unitId)
    while (current) {
      if (current.kind === 'BusinessDivision') return current
      current = current.parentUnitId ? props.organizationDirectory.units.find(unit => unit.id === current!.parentUnitId) : undefined
    }
  }
}

function usersInDivision(divisionId: string) {
  const usernames = new Set(props.organizationDirectory.memberships.filter(item => isUnitWithin(item.unitId, divisionId)).map(item => item.username))
  return props.organizationDirectory.users.filter(user => user.isActive && usernames.has(user.username))
}

function openStaffingDialog(project: ProjectSummary, focus: 'managers' | 'design' = 'managers') {
  staffingProject.value = project
  staffingFocus.value = focus
  staffingForm.primaryProjectManager = project.primaryProjectManager ?? ''
  staffingForm.collaborativeProjectManagers = [...project.collaborativeProjectManagers]
  staffingForm.designLead = project.designLead ?? ''
  staffingDialogOpen.value = true
}

function openDesignerDialog(project: ProjectSummary) {
  designerProject.value = project
  designerDraft.value = [...project.designers]
  designerDialogOpen.value = true
}

const designerCandidates = computed(() => {
  const project = designerProject.value
  if (!project?.organizationId) return []
  const ownDivisionId = divisionOfUser(props.currentUsername)?.id
  const usernames = new Set(props.organizationDirectory.memberships.filter(item => props.organizationDirectory.units.some(unit => unit.id === item.unitId && unit.organizationId === project.organizationId && unit.isActive)).map(item => item.username))
  return props.organizationDirectory.users.filter(user => user.isActive && usernames.has(user.username)).map(user => {
    const division = divisionOfUser(user.username)
    return { ...user, divisionName: division?.name ?? '未归属事业部', ownDivision: division?.id === ownDivisionId }
  }).sort((left, right) => Number(right.ownDivision) - Number(left.ownDivision) || left.divisionName.localeCompare(right.divisionName, 'zh-CN') || left.displayName.localeCompare(right.displayName, 'zh-CN'))
})

const hasCrossDivisionSelection = computed(() => designerCandidates.value.some(user => designerDraft.value.includes(user.username) && !user.ownDivision))

async function saveExecutionUnit() {
  if (!executionProject.value || !executionUnitId.value) return ElMessage.warning('请选择执行事业部')
  try {
    await props.onUpdateExecutionUnit(executionProject.value.id, executionUnitId.value)
    executionDialogOpen.value = false
    ElMessage.success('执行事业部已分配；原项目分工已清空')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '执行事业部分配失败') }
}

async function saveMainStaffing() {
  if (!staffingProject.value || !staffingForm.primaryProjectManager || !staffingForm.designLead) return ElMessage.warning('请选择一名项目经理和一名设计负责人')
  try {
    await props.onUpdateMainStaffing(staffingProject.value.id, { ...staffingForm, collaborativeProjectManagers: [...staffingForm.collaborativeProjectManagers] })
    staffingDialogOpen.value = false
    ElMessage.success('主项目分工已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '主项目分工保存失败') }
}

async function saveDesigners() {
  if (!designerProject.value || !designerDraft.value.length) return ElMessage.warning('请至少选择一名设计人员')
  try {
    await props.onUpdateDesigners(designerProject.value.id, designerDraft.value)
    designerDialogOpen.value = false
    ElMessage.success('子项目设计人员已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '设计人员保存失败') }
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
    ElMessage.warning('请填写子项目名称和数量')
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

function projectManagerText(project: ProjectSummary) {
  const managers = [project.primaryProjectManager, ...project.collaborativeProjectManagers].filter((item): item is string => Boolean(item))
  return managers.length ? [...new Set(managers)].join('、') : '待分配'
}

function designOwnerText(project: ProjectSummary) {
  if (project.parentProjectId && project.designers.length) return project.designers.join('、')
  return project.designLead || '待分配'
}

function canAssignExecutionUnit(project: ProjectSummary) {
  return project.canAssignExecutionUnit || (props.administrator && !project.parentProjectId)
}

function canManageMainStaffing(project: ProjectSummary) {
  return project.canManageMainStaffing || (props.administrator && !project.parentProjectId && Boolean(project.executionUnitId))
}

function handleProjectAction(project: ProjectSummary, action: ProjectAction) {
  if (action === 'open') emit('open', project.id)
  else if (action === 'assign-execution') openExecutionDialog(project)
  else if (action === 'configure-staffing') openStaffingDialog(project)
  else if (action === 'create-child') openChildDialog(project)
  else if (action === 'assign-designers') openDesignerDialog(project)
}
</script>

<template>
  <section class="pdm-project-manager" aria-label="项目中心">
    <header class="pdm-pagebar">
      <div>
        <div class="pdm-breadcrumb">项目管理 <span>/</span> 项目中心</div>
        <h1>项目中心</h1>
      </div>
      <div class="pdm-page-actions">
        <button v-if="canCreate" type="button" class="pdm-primary-action" @click="openCreateDialog"><FolderPlus :size="16" />创建主项目</button>
        <small v-else class="pdm-project-permission-note">当前角色仅可查看负责的项目</small>
      </div>
    </header>

    <section class="pdm-panel pdm-project-list">
      <header class="pdm-panel-heading">
        <div><h2>项目列表</h2><small>子项目默认折叠；搜索时自动显示匹配的子项目</small></div>
        <div class="pdm-project-filters">
          <label class="pdm-inline-search"><Search :size="15" /><input v-model="projectQuery" type="search" aria-label="搜索项目" placeholder="搜索项目号、名称、客户或项目人员"></label>
          <select v-model="stageFilter" aria-label="项目状态筛选"><option value="">全部状态</option><option v-for="stage in stageOptions" :key="stage" :value="stage">{{ stage }}</option></select>
          <select v-model="responsibleFilter" aria-label="项目人员筛选"><option value="">全部项目人员</option><option v-for="username in responsibleOptions" :key="username" :value="username">{{ username }}</option></select>
          <select v-model="executionUnitFilter" aria-label="事业部筛选"><option value="">全部事业部</option><option v-for="unit in executionUnitOptions" :key="unit" :value="unit">{{ unit }}</option></select>
          <select v-model="projectManagerFilter" aria-label="项目经理筛选"><option value="">全部项目经理</option><option v-for="username in projectManagerOptions" :key="username" :value="username">{{ username }}</option></select>
          <select v-model="designOwnerFilter" aria-label="设计负责人筛选"><option value="">全部设计负责人</option><option v-for="username in designOwnerOptions" :key="username" :value="username">{{ username }}</option></select>
          <select v-model="hierarchyFilter" aria-label="项目层级筛选"><option value="all">全部项目</option><option value="parent">仅主项目</option><option value="child">仅子项目</option></select>
        </div>
      </header>
      <div v-if="visibleRootProjects.length" class="pdm-table-scroll pdm-project-number-scroll">
        <table class="pdm-project-table pdm-project-number-table">
          <thead><tr><th>项目号</th><th>项目名称</th><th>别名</th><th>型号</th><th>序列号</th><th>客户</th><th>事业部</th><th>项目经理</th><th>设计负责人</th><th>状态</th><th>操作</th></tr></thead>
          <tbody v-for="parent in visibleRootProjects" :key="parent.id">
            <tr v-if="hierarchyFilter !== 'child'">
              <td><div class="pdm-project-code-cell"><button v-if="filteredChildrenByParent.has(parent.id) && hierarchyFilter !== 'parent'" type="button" class="pdm-tree-toggle" :aria-label="`${isEffectivelyExpanded(parent.id) ? '折叠' : '展开'}${parent.code}的子项目`" @click="toggle(parent.id)"><ChevronDown v-if="isEffectivelyExpanded(parent.id)" :size="15" /><ChevronRight v-else :size="15" /></button><span v-else class="pdm-project-code-spacer"></span><button type="button" class="pdm-project-code-link" :aria-label="`进入项目 ${parent.code}`" @click="emit('open', parent.id)">{{ parent.code }}</button><span class="pdm-project-code-spacer"></span></div></td>
              <td>{{ parent.name }}</td>
              <td>{{ parent.projectAlias || '—' }}</td>
              <td>{{ parent.deviceModel || '旧项目未编号' }}</td>
              <td class="pdm-project-serials"><span v-for="serial in parent.serialNumbers" :key="serial" class="pdm-serial-line">{{ serial }}</span><span v-if="parent.serialNumbers.length === 0">—</span></td>
              <td>{{ parent.customerName || '—' }}</td>
              <td><button v-if="canAssignExecutionUnit(parent)" type="button" class="pdm-project-assignment-button" :aria-label="`分配事业部 ${parent.code}`" title="点击分配执行事业部" @click="openExecutionDialog(parent)"><span>{{ parent.executionUnitName || '待分配' }}</span><Pencil :size="12" aria-hidden="true" /></button><div v-else class="pdm-project-cell-text" :title="parent.executionUnitName || '待分配'">{{ parent.executionUnitName || '待分配' }}</div></td>
              <td><button v-if="canManageMainStaffing(parent)" type="button" class="pdm-project-assignment-button" :aria-label="`配置项目经理（含协同） ${parent.code}`" title="点击配置项目经理和协同项目经理" @click="openStaffingDialog(parent, 'managers')"><span>{{ projectManagerText(parent) }}</span><Pencil :size="12" aria-hidden="true" /></button><div v-else class="pdm-project-cell-text" :title="projectManagerText(parent)">{{ projectManagerText(parent) }}</div></td>
              <td><button v-if="canManageMainStaffing(parent)" type="button" class="pdm-project-assignment-button" :aria-label="`配置设计负责人 ${parent.code}`" title="点击配置设计负责人" @click="openStaffingDialog(parent, 'design')"><span>{{ designOwnerText(parent) }}</span><Pencil :size="12" aria-hidden="true" /></button><div v-else class="pdm-project-cell-text" :title="designOwnerText(parent)">{{ designOwnerText(parent) }}</div></td>
              <td><span class="pdm-status" :class="parent.stage === '进行中' ? 'is-ok' : 'is-warn'">{{ parent.stage }}</span></td>
              <td>
                <el-dropdown :aria-label="`操作项目${parent.code}`" trigger="click" placement="bottom-end" popper-class="pdm-project-action-menu" @command="handleProjectAction(parent, $event)">
                  <button type="button" class="pdm-project-action-trigger" :aria-label="`操作项目${parent.code}`">操作<ChevronDown :size="13" /></button>
                  <template #dropdown><el-dropdown-menu><el-dropdown-item command="open">进入项目</el-dropdown-item><el-dropdown-item v-if="canAssignExecutionUnit(parent)" command="assign-execution">分配事业部</el-dropdown-item><el-dropdown-item v-if="canManageMainStaffing(parent)" command="configure-staffing">配置分工</el-dropdown-item><el-dropdown-item v-if="canCreateSubproject && parent.deviceModel" command="create-child">创建子项目</el-dropdown-item></el-dropdown-menu></template>
                </el-dropdown>
              </td>
            </tr>
            <tr v-for="child in visibleChildren(parent.id)" :key="child.id" class="is-child">
              <td><div class="pdm-project-code-cell"><span class="pdm-project-code-spacer"></span><button type="button" class="pdm-project-code-link" :aria-label="`进入项目 ${child.code}`" @click="emit('open', child.id)">{{ child.code }}</button><span class="pdm-project-code-spacer"></span></div></td>
              <td>{{ child.name }}</td>
              <td>{{ child.projectAlias || '—' }}</td>
              <td>{{ child.deviceModel || '旧项目未编号' }}</td>
              <td class="pdm-project-serials"><span v-for="serial in child.serialNumbers" :key="serial" class="pdm-serial-line">{{ serial }}</span><span v-if="child.serialNumbers.length === 0">—</span></td>
              <td>{{ child.customerName || '—' }}</td>
              <td><div class="pdm-project-cell-text" :title="child.executionUnitName || '待分配'">{{ child.executionUnitName || '待分配' }}</div></td>
              <td><div class="pdm-project-cell-text" :title="projectManagerText(child)">{{ projectManagerText(child) }}</div></td>
              <td><div class="pdm-project-cell-text" :title="designOwnerText(child)">{{ designOwnerText(child) }}</div></td>
              <td><span class="pdm-status" :class="child.stage === '进行中' ? 'is-ok' : 'is-warn'">{{ child.stage }}</span></td>
              <td>
                <el-dropdown :aria-label="`操作项目${child.code}`" trigger="click" placement="bottom-end" popper-class="pdm-project-action-menu" @command="handleProjectAction(child, $event)">
                  <button type="button" class="pdm-project-action-trigger" :aria-label="`操作项目${child.code}`">操作<ChevronDown :size="13" /></button>
                  <template #dropdown><el-dropdown-menu><el-dropdown-item command="open">进入项目</el-dropdown-item><el-dropdown-item v-if="child.canAssignDesigners" command="assign-designers">分配设计人员</el-dropdown-item></el-dropdown-menu></template>
                </el-dropdown>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else-if="projects.length" class="pdm-project-empty"><Search :size="42" /><h2>未找到匹配项目</h2><p>请调整搜索内容或筛选条件。</p><button type="button" class="pdm-secondary-action" @click="projectQuery=''; stageFilter=''; responsibleFilter=''; hierarchyFilter='all'">清除筛选</button></div>
      <div v-else class="pdm-project-empty"><FolderKanban :size="42" /><h2>当前账号还没有分配到项目</h2><p v-if="canCreate">项目创建后由系统管理员或计划管理分配事业部，再由系统管理员或事业部负责人配置项目岗位。</p><p v-else>请联系系统管理员、计划管理或事业部负责人完成项目岗位分配。</p></div>
    </section>

    <el-dialog v-model="dialogOpen" title="创建主项目" width="680px" :close-on-click-modal="false">
      <form class="pdm-project-form" aria-label="创建PDM项目" @submit.prevent="submitProject">
        <label>所属公司<select v-model="form.organizationId" name="organizationId"><option v-for="item in numberingOptions.organizations" :key="item.id" :value="item.id">{{ item.name }}（{{ item.projectCompanyCode }} / {{ item.modelCompanyCode }}）</option></select></label>
        <label>项目类型<select v-model="form.projectTypeCode" name="projectTypeCode"><option v-for="item in numberingOptions.projectTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
        <label>设备类型<select v-model.number="form.equipmentTypeCode" name="equipmentTypeCode"><option v-for="item in numberingOptions.equipmentTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
        <label class="is-wide">客户<select v-model="form.customerId" name="customerId"><option value="" disabled>请选择客户</option><option v-for="item in activeCustomers" :key="item.id" :value="item.id">{{ item.name }}（{{ item.code }}）</option></select><small v-if="selectedCustomer">客户编码由CRM客户数据自动带出：{{ selectedCustomer.code }}</small><small v-else-if="activeCustomers.length === 0">尚未同步CRM客户，请先到“系统管理 → CRM客户”配置并同步。</small></label>
        <label>项目名称<input v-model="form.name" name="projectName" maxlength="200" placeholder="人工录入"></label>
        <label>项目别名<input v-model="form.projectAlias" name="projectAlias" maxlength="200" placeholder="人工录入，可选"></label>
        <label>签订日期<input v-model="form.signedDate" name="signedDate" type="date"></label>
        <label>数量<input v-model.number="form.quantity" name="quantity" type="number" min="1" max="10000"></label>
        <p class="is-wide">项目号、客户编码、客户项目流水号和序列号由服务器生成；存档目录按“系统设置根目录\项目号”自动创建。项目权限由后续事业部和项目岗位分配确定。</p>
      </form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="dialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submitProject">{{ pending ? '正在创建…' : '创建并取号' }}</button></template>
    </el-dialog>

    <el-dialog v-model="executionDialogOpen" :title="`分配执行事业部 · ${executionProject?.code ?? ''}`" width="560px" :close-on-click-modal="false">
      <label class="pdm-dialog-field">执行事业部<el-select v-model="executionUnitId" filterable style="width:100%"><el-option v-for="unit in businessDivisions.filter(item => item.organizationId === executionProject?.organizationId)" :key="unit.id" :label="unit.name" :value="unit.id" /></el-select></label>
      <p class="pdm-counter-note">由系统管理员或拥有“分配执行事业部”权限的计划人员操作。更换事业部会清空项目经理、设计负责人和子项目设计人员。</p>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="executionDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveExecutionUnit">确认分配</button></template>
    </el-dialog>

    <el-dialog v-model="staffingDialogOpen" :title="`配置主项目分工 · ${staffingProject?.code ?? ''}`" width="620px" :close-on-click-modal="false">
      <div class="pdm-project-form"><label class="is-wide" :class="{ 'is-staffing-target': staffingFocus === 'managers' }">项目经理（限1名）<el-select v-model="staffingForm.primaryProjectManager" filterable style="width:100%"><el-option v-for="user in usersInDivision(staffingProject?.executionUnitId ?? '')" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label><label class="is-wide" :class="{ 'is-staffing-target': staffingFocus === 'managers' }">协同项目经理（可多选）<el-select v-model="staffingForm.collaborativeProjectManagers" multiple filterable style="width:100%"><el-option v-for="user in usersInDivision(staffingProject?.executionUnitId ?? '').filter(item => item.username !== staffingForm.primaryProjectManager)" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label><label class="is-wide" :class="{ 'is-staffing-target': staffingFocus === 'design' }">设计负责人（限1名）<el-select v-model="staffingForm.designLead" filterable style="width:100%"><el-option v-for="user in usersInDivision(staffingProject?.executionUnitId ?? '')" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label></div>
      <p class="pdm-counter-note">由系统管理员或事业部负责人配置。项目经理查看项目状态；设计负责人查看设计内容并分配子项目。</p>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="staffingDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveMainStaffing">保存分工</button></template>
    </el-dialog>

    <el-dialog v-model="designerDialogOpen" :title="`分配子项目设计人员 · ${designerProject?.code ?? ''}`" width="640px" :close-on-click-modal="false">
      <label class="pdm-dialog-field">设计人员<el-select v-model="designerDraft" multiple filterable style="width:100%"><el-option-group label="本事业部（优先）"><el-option v-for="user in designerCandidates.filter(item => item.ownDivision)" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-option-group><el-option-group label="其他事业部"><el-option v-for="user in designerCandidates.filter(item => !item.ownDivision)" :key="user.username" :label="`${user.displayName}（${user.username}） · ${user.divisionName}`" :value="user.username" /></el-option-group></el-select></label>
      <p v-if="hasCrossDivisionSelection" class="pdm-counter-note is-warning">已选择其他事业部人员：其权限只覆盖当前子项目及主项目摘要，不会获得兄弟子项目权限，也不会改变执行事业部。</p><p v-else class="pdm-counter-note">默认优先显示设计负责人所在事业部人员；允许选择同一公司其他事业部人员。</p>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="designerDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveDesigners">保存设计人员</button></template>
    </el-dialog>

    <el-dialog v-model="childDialogOpen" :title="`创建子项目 · ${childParent?.code ?? ''}`" width="560px" :close-on-click-modal="false">
      <form class="pdm-project-form" aria-label="创建PDM子项目" @submit.prevent="submitSubproject">
        <label>子项目名称<input v-model="childForm.name" name="childProjectName" maxlength="200" placeholder="人工录入"></label>
        <label>子项目别名<input v-model="childForm.projectAlias" name="childProjectAlias" maxlength="200" placeholder="人工录入，可选"></label>
        <label>数量<input v-model.number="childForm.quantity" name="childQuantity" type="number" min="1" max="10000"></label>
        <p class="is-wide">子项目号按主项目号追加-1、-2…；一个子项目号只生成一个设备型号，数量仅分配对应数量的连续序列号。</p>
      </form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="childDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submitSubproject">{{ pending ? '正在创建…' : '创建子项目' }}</button></template>
    </el-dialog>

  </section>
</template>
