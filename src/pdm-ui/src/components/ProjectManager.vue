<script setup lang="ts">
import DepartmentSelectField from './DepartmentSelectField.vue'
import DepartmentName from './DepartmentName.vue'
import CompanyName from './CompanyName.vue'
import CustomerSelect from './CustomerSelect.vue'
import CompanyNameField from './CompanyNameField.vue'
import PersonName from './PersonName.vue'
import ProjectStaffingDialog from './ProjectStaffingDialog.vue'
import SearchField from './SearchField.vue'
import UserSelect from './UserSelect'
import { PdmApiError, previewChildProjectCodes } from '../api'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, FolderKanban, FolderPlus, Search, X } from '@lucide/vue'
import { computed, nextTick, reactive, ref, watch } from 'vue'
import ProjectNameplateDialog from './ProjectNameplateDialog.vue'
import HistoricalProjectImportDialog from './HistoricalProjectImportDialog.vue'
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
  canImportHistory?: boolean
  activeCompanyId?: string
  activeCompanyName?: string
  canEdit: boolean
  canDelete: boolean
  canCreateSubproject: boolean
  pending: boolean
  onCreate: (input: CreateProjectInput) => Promise<ProjectSummary>
  onCreateSubproject: (parentProjectId: string, items: CreateSubprojectInput[]) => Promise<ProjectSummary[]>
  onUpdateProject: (projectId: string, input: UpdateProjectInput) => Promise<ProjectSummary>
  onDeleteProject: (projectId: string) => Promise<void>
  onUpdateExecutionUnit: (projectId: string, executionUnitId: string) => Promise<ProjectSummary>
  onUpdateMainStaffing: (projectId: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (projectId: string, designers: string[]) => Promise<ProjectSummary>
  onUpdateChildManager: (projectId: string, projectManager: string) => Promise<ProjectSummary>
}>()

const emit = defineEmits<{ open: [projectId: string]; imported: [] }>()
const historyImportOpen = ref(false)
type ProjectAction = 'edit' | 'delete' | 'create-child'
const staffingDialog = ref<InstanceType<typeof ProjectStaffingDialog>>()
const dialogOpen = ref(false)
const nameplateProject = ref<ProjectSummary | null>(null)
const nameplateOpen = ref(false)
const childParent = ref<ProjectSummary | null>(null)
const editingProject = ref<ProjectSummary | null>(null)
const displayedProject = computed(() => childParent.value ?? editingProject.value)
const existingChildProjects = computed(() => {
  const project = displayedProject.value
  if (!project) return []
  return (project.parentProjectId ? [project] : props.projects.filter(item => item.parentProjectId === project.id))
    .sort((left, right) => left.code.localeCompare(right.code, 'zh-CN', { numeric: true }))
})
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
const unassignedDivisionId = '__unassigned__'
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
const bomRootNodeTypes = [
  { code: '0301', label: '0301 · 产线' },
  { code: '0302', label: '0302 · 设备（可独立作为根节点）' },
] as const
const form = reactive<CreateProjectInput>({
  organizationId: '', projectTypeCode: '', equipmentTypeCode: 0, customerId: '', name: '', projectAlias: '', signedDate: '', quantity: 1, bomItemCategoryCode: bomRootNodeTypes[0].code,
})
const formOrganization = computed(() => {
  const id = displayedProject.value ? displayedProject.value.organizationId : form.organizationId
  return props.numberingOptions.organizations.find(company => company.id === id)
    ?? props.organizationDirectory.organizations.find(company => company.id === id)
})
const projectFieldText = computed(() => {
  const project = displayedProject.value
  if (!project) return null
  const organization = formOrganization.value
  const projectType = props.numberingOptions.projectTypes.find(item => item.code === project.projectTypeCode)
  const equipmentType = props.numberingOptions.equipmentTypes.find(item => item.code === project.equipmentTypeCode)
  const customer = props.customers.find(item => item.code === project.customerCode)
  const customerName = project.customerName || customer?.name
  return {
    organization: organization ? `${organization.name}（${organization.projectCompanyCode} / ${organization.modelCompanyCode}）` : project.organizationName || '—',
    projectType: projectType ? `${projectType.code} · ${projectType.name}` : project.projectTypeCode || '—',
    bomRootNode: bomRootNodeTypes.find(item => item.code === project.bomItemCategoryCode)?.label || '—',
    equipmentType: equipmentType ? `${equipmentType.code} · ${equipmentType.name}` : project.equipmentTypeCode ?? '—',
    customer: customerName && project.customerCode ? `${customerName}（${project.customerCode}）` : customerName || project.customerCode || '—',
  }
})
const projectDialogTitle = computed(() => editingProject.value ? `编辑项目 · ${editingProject.value.code}`
  : childParent.value ? `创建0302设备子项目 · ${childParent.value.code}` : '创建主项目')
const projectDialogAction = computed(() => editingProject.value ? (props.pending ? '正在保存…' : '保存')
  : props.pending ? '正在创建…' : childParent.value ? '创建子项目' : '创建并取号')
const projectFormElement = ref<HTMLFormElement>()
const maxProjectQuantity = 10_000
let nextChildRowId = 0
type ChildProjectDraft = { id: number; name: string; projectAlias: string; quantity: number | '' }
const childRows = ref<ChildProjectDraft[]>([])
const childCodes = ref<string[]>([])
const childCodePending = ref(false)
const childCodeError = ref('')
function blankChildRow(): ChildProjectDraft {
  return { id: ++nextChildRowId, name: '', projectAlias: '', quantity: 1 }
}

watch(
  () => [dialogOpen.value, props.token, props.canCreateSubproject, childParent.value?.id,
    form.organizationId, form.projectTypeCode, childRows.value.length] as const,
  ([open, token, canCreateChild, parentProjectId, organizationId, projectTypeCode, count], _, onCleanup) => {
    childCodes.value = []
    childCodeError.value = ''
    childCodePending.value = false
    if (!open || !token || !canCreateChild || !count || (!parentProjectId && (!organizationId || !projectTypeCode))) return
    const scope = parentProjectId ? { parentProjectId } : { organizationId, projectTypeCode }
    const controller = new AbortController()
    childCodePending.value = true
    const timer = window.setTimeout(async () => {
      try {
        const codes = await previewChildProjectCodes(scope, count, token, controller.signal)
        if (!controller.signal.aborted) childCodes.value = codes
      } catch (error) {
        if (!controller.signal.aborted)
          childCodeError.value = error instanceof PdmApiError && error.status === 400 ? error.message : '暂时无法预览子项目号'
      } finally {
        if (!controller.signal.aborted) childCodePending.value = false
      }
    }, 120)
    onCleanup(() => {
      window.clearTimeout(timer)
      controller.abort()
    })
  },
)

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
const standardProjectTypeCode = 'P'
const selectedProjectType = ref<string>()
const unclassifiedProjectType = '__unclassified__'
const projectTypeOf = (project: ProjectSummary) => project.projectTypeCode?.trim().toUpperCase() || unclassifiedProjectType
const projectTypeTabs = computed(() => {
  const usedTypes = new Set(rootProjects.value.map(projectTypeOf))
  const tabs = props.numberingOptions.projectTypes
    .filter(item => item.isActive !== false || usedTypes.has(item.code.trim().toUpperCase()))
    .map(item => ({ code: item.code.trim().toUpperCase(), name: item.name }))
  for (const code of usedTypes) {
    if (code !== unclassifiedProjectType && !tabs.some(item => item.code === code)) tabs.push({ code, name: code === standardProjectTypeCode ? '标准项目' : `${code} 类项目` })
  }
  if (usedTypes.has(unclassifiedProjectType)) tabs.push({ code: unclassifiedProjectType, name: '未分类' })
  return [{ code: '', name: '全部项目' }, ...tabs]
})
const projectTypeFilter = computed({
  get: () => selectedProjectType.value ?? (projectTypeTabs.value.some(item => item.code === standardProjectTypeCode) ? standardProjectTypeCode : ''),
  set: (code: string) => { selectedProjectType.value = code },
})
watch(projectTypeTabs, tabs => {
  if (selectedProjectType.value !== undefined && !tabs.some(item => item.code === selectedProjectType.value)) selectedProjectType.value = undefined
})
const activeCustomers = computed(() => props.customers.filter(item => item.isActive))
const responsibleOptions = computed(() => [...new Set(props.projects.flatMap(item => [item.primaryProjectManager, ...projectDesignLeads(item), ...item.collaborativeProjectManagers, ...item.designers]).filter((item): item is string => Boolean(item)))].sort((left, right) => userDisplayName(left).localeCompare(userDisplayName(right), 'zh-CN')))
const executionUnitOptions = computed(() => [...new Set(props.projects.map(item => item.executionUnitName).filter((item): item is string => Boolean(item)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const projectManagerOptions = computed(() => [...new Set(props.projects.flatMap(item => [item.primaryProjectManager, ...item.collaborativeProjectManagers]).filter((item): item is string => Boolean(item)))].sort((left, right) => userDisplayName(left).localeCompare(userDisplayName(right), 'zh-CN')))
const designOwnerOptions = computed(() => [...new Set(props.projects.flatMap(item => [...projectDesignLeads(item), ...item.designers]).filter((item): item is string => Boolean(item)))].sort((left, right) => userDisplayName(left).localeCompare(userDisplayName(right), 'zh-CN')))
const executionUnitCandidates = computed(() => props.organizationDirectory.units
  .filter(unit => unit.kind === 'BusinessDivision' && unit.isActive)
  .sort((left, right) => Number(!left.name.includes('事业部')) - Number(!right.name.includes('事业部'))
    || left.name.localeCompare(right.name, 'zh-CN', { numeric: true, sensitivity: 'base' })))
const executionCompanyUnits = computed(() => executionUnitCandidates.value
  .filter(unit => unit.organizationId === executionProject.value?.organizationId))
const assignableExecutionUnits = computed(() => executionCompanyUnits.value.filter(unit => unit.canManufacture === true))
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
  if (selectedDivisionId.value === unassignedDivisionId) {
    if (project.executionUnitId || project.executionUnitName?.trim()) return false
  } else if (selectedDivisionId.value) {
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
  if (projectTypeFilter.value && projectTypeOf(parent) !== projectTypeFilter.value) return false
  const hasMatchingChild = filteredChildrenByParent.value.has(parent.id)
  return matchesProject(parent) || hasMatchingChild
}))
const projectPage = ref(1), projectPageSize = ref(50)
const projectTableScroll = ref<HTMLElement>()
const pagedRootProjects = computed(() => visibleRootProjects.value.slice((projectPage.value - 1) * projectPageSize.value, projectPage.value * projectPageSize.value))
watch(projectPageSize, () => { projectPage.value = 1 })
watch(() => Math.max(1, Math.ceil(visibleRootProjects.value.length / projectPageSize.value)), count => { if (projectPage.value > count) projectPage.value = count })
watch([projectPage, projectPageSize], () => { void nextTick(() => { if (projectTableScroll.value) projectTableScroll.value.scrollTop = 0 }) })
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
watch([projectTypeFilter, organizationFilter, selectedDivisionId, projectQuery, responsibleFilter, executionUnitFilter, projectManagerFilter, designOwnerFilter], () => { expanded.value = new Set(); projectPage.value = 1 })
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
  pagedRootProjects.value.forEach(project => append(project, 0))
  return result
})
watch(() => props.projects.map(item => item.id).join(','), () => {
  const validIds = new Set(props.projects.map(item => item.id))
  expanded.value = new Set([...expanded.value].filter(id => validIds.has(id)))
}, { immediate: true })

function openCreateDialog() {
  editingProject.value = null
  childParent.value = null
  childRows.value = []
  form.organizationId = props.numberingOptions.organizations[0]?.id ?? ''
  form.projectTypeCode = props.numberingOptions.projectTypes.find(item => item.isActive !== false && item.code.trim().toUpperCase() === projectTypeFilter.value)?.code
    ?? props.numberingOptions.projectTypes.find(item => item.isActive !== false)?.code ?? ''
  form.equipmentTypeCode = props.numberingOptions.equipmentTypes[0]?.code ?? 0
  form.customerId = activeCustomers.value[0]?.id ?? ''
  form.name = ''
  form.projectAlias = ''
  form.signedDate = ''
  form.quantity = 1
  form.bomItemCategoryCode = bomRootNodeTypes[0].code
  dialogOpen.value = true
}

function openExecutionDialog(project: ProjectSummary) {
  executionProject.value = project
  executionUnitId.value = project.executionUnitId ?? assignableExecutionUnits.value[0]?.id ?? ''
  executionDialogOpen.value = true
}

async function saveExecutionUnit() {
  if (!executionProject.value || !executionUnitId.value) return ElMessage.warning('请选择执行事业部')
  if (!assignableExecutionUnits.value.some(unit => unit.id === executionUnitId.value)) return ElMessage.warning('请选择项目所属公司的可分配事业部')
  try {
    await props.onUpdateExecutionUnit(executionProject.value.id, executionUnitId.value)
    executionDialogOpen.value = false
    ElMessage.success('执行事业部已分配；原项目分工已清空')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '执行事业部分配失败') }
}

function fillProjectDetails(project: ProjectSummary) {
  form.name = project.name
  form.projectAlias = project.projectAlias ?? ''
  form.signedDate = project.signedDate?.slice(0, 10) ?? ''
  form.quantity = project.quantity
}

function openChildDialog(parent: ProjectSummary) {
  if (!canAppendChildProject(parent) || props.pending) return
  editingProject.value = null
  childParent.value = parent
  fillProjectDetails(parent)
  childRows.value = [blankChildRow()]
  dialogOpen.value = true
}

function canWriteProject(project: ProjectSummary) {
  return project.canReadContent && project.effectiveProjectPermissions?.includes('project.content.write') === true
}

function canEditProject(project: ProjectSummary) {
  return props.canEdit && canWriteProject(project) && project.effectiveProjectPermissions?.includes('project.edit') === true
}

function canAppendChildProject(project: ProjectSummary) {
  return props.canCreateSubproject && !project.parentProjectId && !!project.deviceModel && canWriteProject(project)
    && project.effectiveProjectPermissions?.includes('project.child.create') === true
}

function openEditDialog(project: ProjectSummary) {
  if (!canEditProject(project) || props.pending) return
  editingProject.value = project
  childParent.value = null
  childRows.value = []
  fillProjectDetails(project)
  dialogOpen.value = true
}

async function saveProjectDetails(project: ProjectSummary) {
  if (!canEditProject(project)) return ElMessage.warning('当前账号无权编辑该项目')
  const name = form.name.trim()
  if (!name) return ElMessage.warning('请填写项目名称')
  if (!validProjectQuantity(form.quantity)) return ElMessage.warning(`项目数量须为 1 至 ${maxProjectQuantity} 的整数`)
  const isMainProject = !project.parentProjectId
  if (isMainProject && (!project.organizationId || !project.projectTypeCode || project.equipmentTypeCode == null)) {
    return ElMessage.warning('项目缺少所属公司、项目类型或设备类型，请联系管理员核对项目编号资料')
  }
  try {
    await props.onUpdateProject(project.id, {
      ...(isMainProject ? {
        organizationId: project.organizationId,
        projectTypeCode: project.projectTypeCode,
        equipmentTypeCode: project.equipmentTypeCode,
      } : {}),
      name,
      projectAlias: form.projectAlias?.trim(),
      signedDate: isMainProject ? form.signedDate || null : null,
      quantity: form.quantity,
    })
    dialogOpen.value = false
    ElMessage.success('项目已保存', { scope: 'page' })
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '项目保存失败')
  }
}

async function addChildRow() {
  childRows.value.push(blankChildRow())
  await nextTick()
  projectFormElement.value?.querySelector<HTMLInputElement>('tbody tr:last-child input[name="childProjectName"]')?.focus()
}

function removeChildRow(rowId: number) {
  if (!childParent.value || childRows.value.length > 1) childRows.value = childRows.value.filter(row => row.id !== rowId)
}

function moveChildRow(index: number, direction: -1 | 1) {
  const firstIndex = Math.min(index, index + direction)
  if (firstIndex < 0 || firstIndex + 1 >= childRows.value.length) return
  const [first, second] = childRows.value.slice(firstIndex, firstIndex + 2)
  childRows.value.splice(firstIndex, 2, second, first)
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
  if (props.pending) return
  if (editingProject.value) return saveProjectDetails(editingProject.value)
  const parent = childParent.value
  if (!parent && (!form.organizationId || !form.projectTypeCode || !form.customerId || !form.name.trim())) {
    ElMessage.warning('请完整填写项目资料并选择客户')
    return
  }
  if (!parent && !validProjectQuantity(form.quantity)) {
    ElMessage.warning(`主项目数量须为 1 至 ${maxProjectQuantity} 的整数`)
    return
  }
  const items = childRows.value.map(row => ({
    name: row.name.trim(),
    projectAlias: row.projectAlias.trim(),
    quantity: Number(row.quantity),
  }))
  const missingName = items.findIndex(row => !row.name)
  if (missingName !== -1) return ElMessage.warning(`请填写第 ${missingName + 1} 行的子项目名称`)
  const invalidQuantity = items.findIndex(row => !validProjectQuantity(row.quantity))
  if (invalidQuantity !== -1) return ElMessage.warning(`第 ${invalidQuantity + 1} 行的数量须为 1 至 ${maxProjectQuantity} 的整数`)
  try {
    if (parent) {
      const created = await props.onCreateSubproject(parent.id, items)
      expanded.value = new Set([...expanded.value, parent.id])
      ElMessage.success(`已创建 ${created.length} 个子项目`)
    } else {
      const created = await props.onCreate({
        ...form, signedDate: form.signedDate || null, name: form.name.trim(), projectAlias: form.projectAlias?.trim(),
        ...(items.length ? { children: items } : {}),
      })
      if (items.length) expanded.value = new Set([...expanded.value, created.id])
      ElMessage.success(items.length ? `已创建主项目和 ${items.length} 个子项目` : '项目号、设备型号和序列号已自动生成')
    }
    dialogOpen.value = false
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '项目创建失败')
  }
}

function validProjectQuantity(quantity: number) {
  return Number.isInteger(quantity) && quantity >= 1 && quantity <= maxProjectQuantity
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
  return managers.length ? [...new Set(managers)].map(item => userDisplayName(item)).join('、') : '—'
}

function designOwnerText(project: ProjectSummary) {
  if (project.parentProjectId) return project.designers.length ? project.designers.map(item => userDisplayName(item)).join('、') : '—'
  const designLeads = projectDesignLeads(project)
  return designLeads.length ? designLeads.map(item => userDisplayName(item)).join('、') : '—'
}

function canAssignExecutionUnit(project: ProjectSummary) {
  return project.canAssignExecutionUnit
}

function handleProjectAction(project: ProjectSummary, action: ProjectAction) {
  if (action === 'delete') void deleteProject(project)
  else if (action === 'create-child') openChildDialog(project)
  else if (action === 'edit') openEditDialog(project)
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
              <button v-for="division in organization.divisions" :key="division.id" type="button" role="treeitem" class="pdm-project-classification__division" :aria-selected="selectedDivisionId === division.id" :class="{ 'is-active': selectedDivisionId === division.id }" @click="selectProjectOrganization(organization.id, division.id)"><DepartmentName :name="division.name" /></button>
              <button type="button" role="treeitem" class="pdm-project-classification__division pdm-project-classification__unassigned" :aria-selected="organizationFilter === organization.id && selectedDivisionId === unassignedDivisionId" :class="{ 'is-active': organizationFilter === organization.id && selectedDivisionId === unassignedDivisionId }" @click="selectProjectOrganization(organization.id, unassignedDivisionId)"><DepartmentName name="待分配" highlight /></button>
            </div>
          </div>
        </div>
      </aside>
      <section class="pdm-panel pdm-project-table-panel" aria-label="项目列表">
        <el-tabs v-model="projectTypeFilter" class="pdm-settings-tabs pdm-project-type-tabs" aria-label="项目类型">
          <el-tab-pane v-for="item in projectTypeTabs" :key="item.code" :name="item.code" :label="item.name" />
        </el-tabs>
        <div class="pdm-project-list-toolbar" aria-label="项目筛选">
          <button v-if="canCreate" type="button" class="pdm-primary-action pdm-project-create-action" @click="openCreateDialog"><FolderPlus :size="16" />创建主项目</button>
          <button v-if="canCreate && canImportHistory && activeCompanyId && token" type="button" class="pdm-secondary-action" @click="historyImportOpen = true">导入历史项目</button>
          <button type="button" class="pdm-secondary-action pdm-project-collapse-action" :aria-label="`${allProjectDetailsExpanded ? '折叠' : '展开'}全部项目明细`" :disabled="expandableProjectIds.length === 0" @click="toggleAllProjectDetails"><ChevronDown v-if="allProjectDetailsExpanded" :size="15" /><ChevronRight v-else :size="15" />{{ allProjectDetailsExpanded ? '折叠明细' : '展开明细' }}</button>
          <div class="pdm-project-filters">
            <label class="pdm-inline-search"><SearchField><input v-model="projectSearchDraft" @keyup.enter="projectQuery = projectSearchDraft" type="search" aria-label="搜索项目" placeholder="搜索项目号、名称、客户或项目人员"><template #action><button type="button" class="pdm-secondary-action" @click="projectQuery = projectSearchDraft">搜索</button></template></SearchField></label>

            <UserSelect v-model="responsibleFilter" aria-label="项目人员筛选"><option value="">全部项目人员</option><option v-for="username in responsibleOptions" :key="username" :value="username">{{ userDisplayName(username) }}</option></UserSelect>
            <DepartmentSelectField :name="executionUnitFilter"><select v-model="executionUnitFilter" aria-label="事业部筛选"><option value="">全部事业部</option><option v-for="unit in executionUnitOptions" :key="unit" :value="unit">{{ unit }}</option></select></DepartmentSelectField>
            <UserSelect v-model="projectManagerFilter" aria-label="项目经理筛选"><option value="">全部项目经理</option><option v-for="username in projectManagerOptions" :key="username" :value="username">{{ userDisplayName(username) }}</option></UserSelect>
            <UserSelect v-model="designOwnerFilter" aria-label="主设计或设计人员筛选"><option value="">全部主设计／设计人员</option><option v-for="username in designOwnerOptions" :key="username" :value="username">{{ userDisplayName(username) }}</option></UserSelect>
          </div>
        </div>
        <div v-if="visibleRootProjects.length" ref="projectTableScroll" class="pdm-table-scroll pdm-project-number-scroll">
        <table class="pdm-project-table pdm-project-number-table" data-pdm-min-width="1040">
          <colgroup><col v-for="(weight, index) in [2, 2, 2, 2, 2, 2, 1.75, 1, 1, 1, 1, 1, 1]" :key="index" :style="{ width: `${weight / 19.75 * 100}%` }"></colgroup>
          <thead><tr><th>项目号</th><th>项目名称</th><th>别名</th><th>型号</th><th>序列号</th><th>客户</th><th>事业部</th><th>项目经理</th><th>主设计</th><th>状态</th><th>订单日期</th><th>铭牌</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="row in visibleProjectRows" :key="row.project.id" :class="{ 'is-child': row.depth > 0 }">
              <td><div class="pdm-project-code-cell" :class="{ 'is-child-code': row.depth > 0 }" :style="{ transform: `translateX(${row.depth * 2}ch)` }"><button v-if="filteredChildrenByParent.has(row.project.id)" type="button" class="pdm-tree-toggle" :aria-label="`${isEffectivelyExpanded(row.project.id) ? '折叠' : '展开'}${row.project.code}的子项目`" @click="toggle(row.project.id)"><ChevronDown v-if="isEffectivelyExpanded(row.project.id)" :size="15" /><ChevronRight v-else :size="15" /></button><span v-else class="pdm-project-code-spacer"></span><button type="button" class="pdm-project-code-link" :aria-label="`进入项目 ${row.project.code}`" @click="emit('open', row.project.id)">{{ row.project.code }}</button><button v-if="canAppendChildProject(row.project)" type="button" class="pdm-project-add-child" :aria-label="`为${row.project.code}创建子项目`" title="创建子项目" @click.stop="openChildDialog(row.project)"><FolderPlus :size="14" /></button><span v-else class="pdm-project-code-spacer"></span></div></td>
              <td><span v-if="row.project.isSpecialInitiation" class="pdm-status is-warn" style="margin-right:4px">特殊立项</span>{{ row.project.name }}</td>
              <td>{{ row.project.projectAlias || '—' }}</td>
              <td>{{ row.project.deviceModel || '旧项目未编号' }}</td>
              <td class="pdm-project-serials"><span v-for="serial in row.project.serialNumbers" :key="serial" class="pdm-serial-line">{{ serial }}</span><span v-if="row.project.serialNumbers.length === 0">—</span></td>
              <td><CompanyName :name="row.project.customerName || '—'" /></td>
              <td><button v-if="row.depth === 0 && canAssignExecutionUnit(row.project)" type="button" class="pdm-project-assignment-button" :aria-label="`分配事业部 ${row.project.code}`" title="点击分配执行事业部" @click="openExecutionDialog(row.project)"><DepartmentName :name="row.project.executionUnitName" fallback="待分配" /></button><div v-else class="pdm-project-cell-text" :title="row.project.executionUnitName || '待分配'"><DepartmentName :name="row.project.executionUnitName" fallback="待分配" /></div></td>
              <td><button v-if="row.project.canManageMainStaffing" class="pdm-text-action" :aria-label="`分配项目经理 ${row.project.code}`" @click="staffingDialog?.configure(row.project)"><PersonName :name="projectManagerText(row.project)" /></button><div v-else class="pdm-project-cell-text" :title="projectManagerText(row.project)"><PersonName :name="projectManagerText(row.project)" /></div></td>
              <td><button v-if="row.project.canManageMainStaffing" class="pdm-text-action" :aria-label="`分配主设计 ${row.project.code}`" @click="staffingDialog?.configure(row.project)"><PersonName :name="designOwnerText(row.project)" /></button><div v-else class="pdm-project-cell-text" :title="designOwnerText(row.project)"><PersonName :name="designOwnerText(row.project)" /></div></td>
              <td><span class="pdm-status" :class="row.project.stage === '进行中' ? 'is-ok' : 'is-warn'">{{ row.project.stage }}</span></td>
              <td class="pdm-project-order-date">{{ row.project.signedDate || '—' }}</td>
              <td><button type="button" class="pdm-text-action" :disabled="!row.project.canReadContent || !token" :aria-label="`查看铭牌 ${row.project.code}`" @click="nameplateProject = row.project; nameplateOpen = true">查看铭牌</button></td>
              <td>
                <el-dropdown :aria-label="`操作项目${row.project.code}`" trigger="click" placement="bottom-end" popper-class="pdm-project-action-menu" @command="handleProjectAction(row.project, $event)">
                  <button type="button" class="pdm-project-action-trigger" :aria-label="`操作项目${row.project.code}`">操作<ChevronDown :size="13" /></button>
                  <template #dropdown>
                    <el-dropdown-menu>
                      <el-dropdown-item v-if="canEditProject(row.project)" command="edit">编辑</el-dropdown-item>
                      <el-dropdown-item v-if="canAppendChildProject(row.project)" command="create-child">创建子项目</el-dropdown-item>
                      <el-dropdown-item v-if="canDelete && row.project.effectiveProjectPermissions?.includes('project.delete')" command="delete" divided>删除项目</el-dropdown-item>
                    </el-dropdown-menu>
                  </template>
                </el-dropdown>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
        <div v-else-if="projects.length" class="pdm-project-empty"><Search :size="42" /><h2>未找到匹配项目</h2><p>请切换项目类型或调整搜索、筛选条件。</p><button type="button" class="pdm-secondary-action" @click="projectTypeFilter=''; projectQuery=''; projectSearchDraft=''; responsibleFilter=''; selectProjectOrganization()">清除筛选</button></div>
        <div v-else class="pdm-project-empty"><FolderKanban :size="42" /><h2>当前公司还没有项目</h2><p v-if="canCreate">创建主项目后，可继续分配事业部和配置项目岗位。</p><p v-else>请联系有创建权限的人员新建项目。</p></div>
        <el-pagination v-model:current-page="projectPage" v-model:page-size="projectPageSize" class="pdm-project-pagination" aria-label="项目列表分页" :page-sizes="[50, 100, 200]" :total="visibleRootProjects.length" layout="total, sizes, prev, pager, next" />
      </section>
    </section>


    <HistoricalProjectImportDialog v-if="canCreate && canImportHistory && activeCompanyId && token"
      v-model="historyImportOpen" :company-id="activeCompanyId" :company-name="activeCompanyName || '当前公司'"
      :token="token" @imported="emit('imported')" />
    <el-dialog v-model="dialogOpen" :title="projectDialogTitle" width="800px" :close-on-click-modal="!pending">
      <form id="pdm-project-creation-form" ref="projectFormElement" class="pdm-project-form" :aria-label="editingProject ? '编辑PLM项目' : childParent ? '创建PLM子项目' : '创建PLM项目'" novalidate @submit.prevent="submitProject">
        <label>所属公司
          <CompanyNameField :name="formOrganization?.name || displayedProject?.organizationName">
            <input v-if="projectFieldText" name="organizationId" :value="projectFieldText.organization" :title="projectFieldText.organization" readonly>
            <select v-else v-model="form.organizationId" name="organizationId"><option v-for="item in numberingOptions.organizations" :key="item.id" :value="item.id">{{ item.name }}（{{ item.projectCompanyCode }} / {{ item.modelCompanyCode }}）</option></select>
          </CompanyNameField>
        </label>
        <label>项目类型
          <input v-if="projectFieldText" name="projectTypeCode" :value="projectFieldText.projectType" readonly>
          <select v-else v-model="form.projectTypeCode" name="projectTypeCode"><option v-for="item in numberingOptions.projectTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select>
        </label>
        <label>BOM根节点类型
          <input v-if="projectFieldText" name="bomItemCategoryCode" :value="projectFieldText.bomRootNode" readonly>
          <select v-else v-model="form.bomItemCategoryCode" name="bomItemCategoryCode"><option v-for="item in bomRootNodeTypes" :key="item.code" :value="item.code">{{ item.label }}</option></select>
        </label>
        <label>设备类型
          <input v-if="projectFieldText" name="equipmentTypeCode" :value="projectFieldText.equipmentType" readonly>
          <select v-else v-model.number="form.equipmentTypeCode" name="equipmentTypeCode"><option v-for="item in numberingOptions.equipmentTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select>
        </label>
        <label class="is-wide">客户
          <input v-if="projectFieldText" name="customerId" :value="projectFieldText.customer" :title="projectFieldText.customer" readonly>
          <CustomerSelect v-else v-model="form.customerId" :customers="activeCustomers" class="pdm-project-customer-select" name="customerId" placeholder="输入客户名称或编码筛选" no-match-text="未找到匹配客户" style="width:100%" />
          <small v-if="!displayedProject && activeCustomers.length === 0">尚未同步U9C客户，请先到“系统管理 → U9C接口 → 客户查询”执行同步。</small>
        </label>
        <label>项目名称<input v-model="form.name" name="projectName" maxlength="200" :placeholder="childParent ? '—' : '人工录入'" :readonly="!!childParent"></label>
        <label>项目别名<input v-model="form.projectAlias" name="projectAlias" maxlength="200" :placeholder="childParent ? '—' : '人工录入，可选'" :readonly="!!childParent"></label>
        <label>订单日期（选填）<input v-model="form.signedDate" name="signedDate" type="date" :readonly="!!childParent || !!editingProject?.parentProjectId"></label>
        <label>数量<input v-model.number="form.quantity" name="quantity" type="number" min="1" :max="maxProjectQuantity" step="1" :readonly="!!childParent"></label>
        <template v-if="displayedProject?.historicalUpdatedDate || displayedProject?.historicalRemarks">
          <label>原系统更新时间<input :value="displayedProject.historicalUpdatedDate || '—'" readonly></label>
          <label>交货日期<input :value="displayedProject.deliveryDate || '—'" readonly></label>
          <label class="is-wide">原系统备注<textarea :value="displayedProject.historicalRemarks || '—'" rows="3" readonly></textarea></label>
        </template>
        <section v-if="displayedProject || canCreateSubproject" class="pdm-child-project-section is-wide is-embedded" aria-label="子项目">
          <h3>{{ displayedProject ? '子项目' : '子项目（选填）' }}</h3>
          <table v-if="existingChildProjects.length || (!editingProject && childRows.length)" class="pdm-child-project-list pdm-fit-columns" :aria-label="existingChildProjects.length ? '子项目列表' : '待创建子项目'">
            <colgroup><col class="pdm-child-project-code-column"><col><col><col class="pdm-child-project-quantity-column"><col class="pdm-child-project-actions-column"></colgroup>
            <thead>
              <tr><th scope="col" title="预览编号，最终以创建结果为准">子项目号</th><th scope="col">子项目名称</th><th scope="col">子项目别名</th><th scope="col">数量</th><th scope="col">操作</th></tr>
            </thead>
            <tbody>
              <tr v-for="row in existingChildProjects" :key="row.id">
                <td class="pdm-child-project-code">{{ row.code }}</td>
                <td>{{ row.name }}</td>
                <td>{{ row.projectAlias || '—' }}</td>
                <td>{{ row.quantity }}</td>
                <td>已创建</td>
              </tr>
              <tr v-for="(row, index) in editingProject ? [] : childRows" :key="row.id">
                <td class="pdm-child-project-code" :class="{ 'is-pending': !childCodes[index] }">{{ childCodes[index] || (childCodePending ? '…' : '—') }}</td>
                <td><input v-model="row.name" name="childProjectName" :aria-label="`第 ${index + 1} 行子项目名称`" maxlength="200" placeholder="必填" :disabled="pending"></td>
                <td><input v-model="row.projectAlias" name="childProjectAlias" :aria-label="`第 ${index + 1} 行子项目别名`" maxlength="200" placeholder="选填" :disabled="pending"></td>
                <td><input v-model.number="row.quantity" class="pdm-child-project-quantity" name="childQuantity" :aria-label="`第 ${index + 1} 行数量`" type="number" min="1" :max="maxProjectQuantity" step="1" :disabled="pending"></td>
                <td>
                  <div class="pdm-child-project-row-actions">
                    <button type="button" class="pdm-tree-toggle" :aria-label="`上移第 ${index + 1} 行`" :title="`上移第 ${index + 1} 行`" :disabled="pending || index === 0" @click="moveChildRow(index, -1)"><ArrowUp :size="14" aria-hidden="true" /></button>
                    <button type="button" class="pdm-tree-toggle" :aria-label="`下移第 ${index + 1} 行`" :title="`下移第 ${index + 1} 行`" :disabled="pending || index === childRows.length - 1" @click="moveChildRow(index, 1)"><ArrowDown :size="14" aria-hidden="true" /></button>
                    <button type="button" class="pdm-child-project-remove pdm-danger-action" :aria-label="`删除第 ${index + 1} 行`" :title="`删除第 ${index + 1} 行`" :disabled="pending || (!!childParent && childRows.length === 1)" @click="removeChildRow(row.id)"><X :size="14" aria-hidden="true" /></button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
          <p v-if="editingProject && !existingChildProjects.length" class="pdm-muted">暂无子项目</p>
          <div v-if="!editingProject" class="pdm-child-project-list-actions">
            <button type="button" class="pdm-primary-action" data-pdm-primary :disabled="pending" @click="addChildRow">新增行</button>
            <span v-if="childRows.length" aria-live="polite">{{ existingChildProjects.length ? '待创建' : '共' }} {{ childRows.length }} 个子项目</span>
          </div>
          <p v-if="childCodeError" class="pdm-child-project-code-error" role="status">{{ childCodeError }}</p>
        </section>
      </form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="dialogOpen=false">取消</button><button type="submit" form="pdm-project-creation-form" class="pdm-primary-action" :disabled="pending" @click.prevent="submitProject">{{ projectDialogAction }}</button></template>
    </el-dialog>

    <el-dialog v-model="executionDialogOpen" :title="`分配执行事业部 · ${executionProject?.code ?? ''}`" width="560px" :close-on-click-modal="!pending">
      <label class="pdm-dialog-field">执行事业部
        <el-select v-model="executionUnitId" aria-label="执行事业部" filterable :disabled="pending" style="width:100%">
          <template #label="{ label }"><DepartmentName :name="label" /></template>
          <el-option v-for="unit in executionCompanyUnits" :key="unit.id" :label="unit.name" :value="unit.id" :disabled="unit.canManufacture !== true"><DepartmentName :name="unit.name" /></el-option>
        </el-select>
      </label>
      <div v-if="assignableExecutionUnits.length" class="pdm-execution-unit-options" role="group" aria-label="可分配的事业部">
        <button v-for="unit in assignableExecutionUnits" :key="unit.id" type="button" class="pdm-secondary-action pdm-execution-unit-option"
          :aria-pressed="executionUnitId === unit.id" :disabled="pending" :title="unit.name" @click="executionUnitId = unit.id">
          <DepartmentName :name="unit.name" />
        </button>
      </div>
      <p v-else class="pdm-counter-note">当前公司暂无可分配的事业部。</p>
      <p class="pdm-counter-note">由系统管理员或拥有“分配执行事业部”权限的计划人员操作。更换事业部会清空项目经理、主设计和子项目设计人员。</p>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="executionDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveExecutionUnit">确认分配</button></template>
    </el-dialog>

    <ProjectNameplateDialog v-if="nameplateProject && token" v-model="nameplateOpen" :project="nameplateProject" :token="token" />
  </section>
  <ProjectStaffingDialog ref="staffingDialog" :directory="organizationDirectory" :current-username="currentUsername" :administrator="administrator" :on-save="onUpdateMainStaffing" />
</template>

<style scoped>
.pdm-project-pagination{flex:0 0 auto;justify-content:flex-end;padding-top:8px}
.pdm-execution-unit-options { display: flex; flex-wrap: wrap; gap: var(--pdm-control-gap, 8px); margin: 8px 0; }
.pdm-execution-unit-option[aria-pressed="true"] { outline: 1px solid var(--pdm-theme-accent); outline-offset: -2px; }
.pdm-child-project-section { display: grid; min-width: 0; gap: 10px; }
.pdm-child-project-section.is-embedded { padding-top: 12px; border-top: 1px solid var(--pdm-container-border, var(--pdm-border)); }
.pdm-child-project-section h3 { margin: 0; color: var(--pdm-text); font-size: 13px; }
.pdm-project-form input[readonly] { background: var(--pdm-surface-muted); }
.pdm-child-project-list { width: 100%; table-layout: fixed; border-collapse: collapse; }
.pdm-child-project-list :is(th, td) { padding: 4px 3px; text-align: center; vertical-align: middle; }
.pdm-child-project-list th { color: var(--pdm-muted); font-size: 12px; }
.pdm-child-project-list input { box-sizing: border-box; width: 100%; }
.pdm-child-project-code-column { width: 104px; }
.pdm-child-project-code { overflow-wrap: anywhere; font-variant-numeric: tabular-nums; }
.pdm-child-project-code.is-pending, .pdm-child-project-code-error { color: var(--pdm-muted); }
.pdm-child-project-quantity-column { width: 76px; }
.pdm-child-project-quantity { text-align: center; }
.pdm-child-project-actions-column { width: 84px; }
.pdm-child-project-row-actions { display: flex; align-items: center; justify-content: center; gap: 3px; }
.pdm-child-project-row-actions .pdm-tree-toggle { width: 22px; height: 24px; }
.pdm-child-project-row-actions .pdm-tree-toggle:disabled { opacity: .35; cursor: default; background: transparent; }
.pdm-child-project-remove { display: inline-grid; width: 24px; height: 24px; place-items: center; padding: 0; }
.pdm-child-project-list-actions { display: flex; align-items: center; gap: 10px; color: var(--pdm-muted); font-size: 12px; }
@media (max-width: 600px) {
  .pdm-child-project-code-column { width: 20%; }
  .pdm-child-project-quantity-column { width: 14%; }
}
</style>

<style scoped>
.pdm-project-type-tabs{flex:none;min-width:0;margin-bottom:8px}
</style>

<style scoped>
/* Keep the preferred desktop widths and wrap when the panel is narrower. */
@layer pdm-control-spacing {
  :root body .pdm-project-table-panel .pdm-project-list-toolbar {
    flex-wrap: wrap !important;
    gap: var(--pdm-control-gap) !important;
  }
  :root body .pdm-project-table-panel .pdm-project-list-toolbar > .pdm-project-filters {
    display: flex !important;
    flex-wrap: wrap !important;
    align-items: center;
    flex: 0 1 835px !important;
    width: 835px !important;
    min-width: 0 !important;
    max-width: 100% !important;
    gap: var(--pdm-control-gap) !important;
  }
  :root body .pdm-project-table-panel .pdm-project-list-toolbar > .pdm-project-filters > :is(.pdm-inline-search, .el-select, select) {
    flex: 0 1 120px !important;
    width: 120px !important;
    min-width: 0 !important;
    max-width: 100% !important;
    height: var(--pdm-control-height) !important;
    min-height: var(--pdm-control-height) !important;
    margin: 0 !important;
  }
  :root body .pdm-project-table-panel .pdm-project-list-toolbar > .pdm-project-filters > .pdm-inline-search {
    flex-basis: 335px !important;
    width: 335px !important;
  }
  :root body .pdm-project-filters :deep(.el-select__wrapper) {
    min-height: var(--pdm-control-height) !important;
    height: var(--pdm-control-height) !important;
  }
}
</style>
