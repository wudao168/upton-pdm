<script setup lang="ts">
import { Boxes, ContactRound, FolderTree, PackageCheck, UsersRound } from '@lucide/vue'
import { ElMessage } from 'element-plus'
import { computed, reactive, ref } from 'vue'
import type { DocumentNode, DrawingReviewPackage, DrawingReviewTarget, MainProjectStaffingInput, MaterialCodeApplication, OrganizationDirectory, PdmUser, ProjectSummary, ReleasePackageSummary, ReleaseScope } from '../types'

const props = defineProps<{
  project: ProjectSummary
  projects: ProjectSummary[]
  users: PdmUser[]
  selected: DocumentNode
  currentUsername?: string
  hasDocuments: boolean
  documentCount: number
  modelCount: number
  drawingCount: number
  warningCount: number
  standardCount: number
  nonStandardCount: number
  electricalCount: number
  standardItemIds: string[]
  nonStandardItemIds: string[]
  electricalItemIds: string[]
  drawingReviews: DrawingReviewPackage[]
  materialApplications: MaterialCodeApplication[]
  releasePackages: ReleasePackageSummary[]
  releasePackage: ReleasePackageSummary | null
  organizationDirectory: OrganizationDirectory
  pending: boolean
  onUpdateMainStaffing: (projectId: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (projectId: string, designers: string[]) => Promise<ProjectSummary>
}>()

const emit = defineEmits<{ documents: []; bom: [] }>()

const activeProject = computed(() => props.projects.find(item => item.id === props.project.id) ?? props.project)
const rootProject = computed(() => {
  if (!activeProject.value.parentProjectId) return activeProject.value
  return props.projects.find(item => item.id === activeProject.value.parentProjectId) ?? activeProject.value
})

const staffingDialogOpen = ref(false)
const designerDialogOpen = ref(false)
const staffingForm = reactive<MainProjectStaffingInput>({ primaryProjectManager: '', collaborativeProjectManagers: [], designLeads: [] })
const designerDraft = ref<string[]>([])

function projectDesignLeads(project: ProjectSummary) {
  return project.designLeads?.length ? project.designLeads : project.designLead ? [project.designLead] : []
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

function usersInExecutionUnit(role: string) {
  const executionUnitId = rootProject.value.executionUnitId
  if (!executionUnitId) return []
  const usernames = new Set(props.organizationDirectory.memberships.filter(item => isUnitWithin(item.unitId, executionUnitId)).map(item => item.username))
  return props.organizationDirectory.users
    .filter(user => user.isActive && usernames.has(user.username) && [user.role, ...(user.roles ?? [])].includes(role))
    .sort((left, right) => left.displayName.localeCompare(right.displayName, 'zh-CN'))
}

const projectManagerCandidates = computed(() => usersInExecutionUnit('ProjectManager'))
const designLeadCandidates = computed(() => usersInExecutionUnit('Engineer'))
const designerCandidates = computed(() => {
  const organizationId = props.project.organizationId ?? rootProject.value.organizationId
  if (!organizationId) return []
  const ownDivisionId = divisionOfUser(props.currentUsername ?? '')?.id
  const companyUnitIds = new Set(props.organizationDirectory.units.filter(unit => unit.organizationId === organizationId && unit.isActive).map(unit => unit.id))
  const usernames = new Set(props.organizationDirectory.memberships.filter(item => companyUnitIds.has(item.unitId)).map(item => item.username))
  const technicalRoles = new Set(['Engineer', 'ElectricalEngineer', 'CommissioningEngineer', 'HardwareEngineer', 'MechanicalManager', 'TechnicalAssistant', 'ProcessReviewer', 'Approver'])
  const isTechnicalUser = (user: PdmUser) => {
    const roles = [user.role, ...(user.roles ?? [])]
    if (!roles.some(role => technicalRoles.has(role))) return false
    return props.organizationDirectory.memberships.filter(item => item.username === user.username).some(item => {
      const unit = props.organizationDirectory.units.find(candidate => candidate.id === item.unitId)
      const name = unit?.name ?? ''
      return /机械|电气|硬件|标准化|技术|设计|研发/.test(name) || unit?.kind === 'BusinessDivision' && !/采购|供应链|生产|装配|机加|财务|行政|人事部|销售|计划|质量|仓储|物流/.test(name)
    })
  }
  return props.organizationDirectory.users
    .filter(user => user.isActive && usernames.has(user.username) && isTechnicalUser(user))
    .map(user => {
      const division = divisionOfUser(user.username)
      return { ...user, divisionName: division?.name ?? '未归属事业部', ownDivision: division?.id === ownDivisionId }
    })
    .sort((left, right) => Number(right.ownDivision) - Number(left.ownDivision) || left.divisionName.localeCompare(right.divisionName, 'zh-CN') || left.displayName.localeCompare(right.displayName, 'zh-CN'))
})
const hasCrossDivisionSelection = computed(() => designerCandidates.value.some(user => designerDraft.value.includes(user.username) && !user.ownDivision))

function openStaffingDialog() {
  staffingForm.primaryProjectManager = rootProject.value.primaryProjectManager ?? ''
  staffingForm.collaborativeProjectManagers = [...rootProject.value.collaborativeProjectManagers]
  staffingForm.designLeads = [...projectDesignLeads(rootProject.value)]
  staffingDialogOpen.value = true
}

function openDesignerDialog() {
  designerDraft.value = [...activeProject.value.designers]
  designerDialogOpen.value = true
}

async function saveMainStaffing() {
  if (!staffingForm.primaryProjectManager || !staffingForm.designLeads.length) return ElMessage.warning('请选择一名项目经理和至少一名主设')
  try {
    await props.onUpdateMainStaffing(rootProject.value.id, {
      primaryProjectManager: staffingForm.primaryProjectManager,
      collaborativeProjectManagers: [...staffingForm.collaborativeProjectManagers],
      designLeads: [...staffingForm.designLeads],
    })
    staffingDialogOpen.value = false
    ElMessage.success('主项目分工已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '主项目分工保存失败') }
}

async function saveDesigners() {
  try {
    await props.onUpdateDesigners(activeProject.value.id, [...designerDraft.value])
    designerDialogOpen.value = false
    ElMessage.success(designerDraft.value.length ? '执行工程师已保存' : '执行工程师已清空')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '执行工程师保存失败') }
}

function assignedPeople(usernames: Array<string | undefined>) {
  const uniqueUsernames = [...new Set(usernames.map(item => item?.trim()).filter((item): item is string => Boolean(item)))]
  return uniqueUsernames.map(username => ({
    username,
    name: props.users.find(user => user.username.localeCompare(username, undefined, { sensitivity: 'accent' }) === 0)?.displayName || username,
  }))
}

const staffingRows = computed(() => [
  { key: 'manager', stage: '项目管理', role: activeProject.value.parentProjectId ? '子项目负责人' : '项目经理', people: assignedPeople([activeProject.value.primaryProjectManager ?? rootProject.value.primaryProjectManager]) },
  { key: 'collaborative-managers', stage: '项目管理', role: '协同项目经理', people: assignedPeople(rootProject.value.collaborativeProjectManagers) },
  { key: 'design-lead', stage: '设计阶段', role: '主设', people: assignedPeople(projectDesignLeads(rootProject.value)) },
  { key: 'engineers', stage: '设计执行', role: '执行工程师', people: assignedPeople(activeProject.value.designers) },
  { key: 'downstream', stage: '后续阶段', role: '各部门负责人', people: [] },
])

const drawingReviewSummary = computed(() => {
  const review = [...props.drawingReviews].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
  if (!review) return '图纸审核：未发起'
  const stateLabels = {
    InReview: '审核中',
    ChangesRequested: '已退改',
    WritingProperties: '写入标记',
    Approved: '已完成',
    Stale: '版本冲突',
    Withdrawn: '已撤销',
  }
  const reviewed = review.items.reduce((count, item) => count
    + (['Approved', 'Marked'].includes(item.modelState) ? 1 : 0)
    + (['Approved', 'Marked'].includes(item.drawingState) ? 1 : 0), 0)
  return `图纸审核：${stateLabels[review.state]} · 双审 ${reviewed}/${review.items.length * 2}`
})

function drawingTargetSummary(target: DrawingReviewTarget) {
  const count = target === 'Model3D' ? props.modelCount : props.drawingCount
  if (count === 0) return '无图档'
  const review = [...props.drawingReviews].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
  if (!review) return '未发起'
  if (review.state === 'Stale') return '版本冲突'
  const states = review.items.map(item => target === 'Model3D' ? item.modelState : item.drawingState)
  const completed = states.filter(state => state === 'Approved' || state === 'Marked').length
  const changesRequested = states.filter(state => state === 'ChangesRequested').length
  if (states.length > 0 && completed === states.length) return '已完成'
  if (changesRequested > 0) return `已退改 ${changesRequested}`
  if (review.state === 'WritingProperties') return `写入标记 ${completed}/${states.length}`
  return `审核中 ${completed}/${states.length}`
}

function latestReleaseFor(scopes: ReleaseScope[]) {
  return [...props.releasePackages]
    .filter(item => scopes.includes(item.scope))
    .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? ''))[0]
}

function bomApprovalStatus(scopes: ReleaseScope[]) {
  const release = latestReleaseFor(scopes)
  if (!release) return '未发起'
  const currentStep = release.steps.find(step => step.status === 'current')
  return `${release.state}${currentStep ? ` · ${currentStep.stage}` : ''}`
}

function materialApplicationStatus(itemIds: string[]) {
  const ids = new Set(itemIds)
  const applications = props.materialApplications.filter(item => item.bomItemId && ids.has(item.bomItemId))
  if (!applications.length) return '暂无'
  const pending = applications.filter(item => item.status === 'Pending').length
  const approved = applications.filter(item => item.status === 'Approved').length
  const rejected = applications.filter(item => item.status === 'Rejected').length
  return [`待审${pending}`, `已批${approved}`, `驳回${rejected}`].filter((_, index) => [pending, approved, rejected][index] > 0).join(' · ')
}

const documentRows = computed(() => [
  { key: 'model', label: '3D图档', count: props.modelCount, approval: drawingTargetSummary('Model3D') },
  { key: 'drawing', label: '2D图纸', count: props.drawingCount, approval: drawingTargetSummary('Drawing2D') },
])

const bomRows = computed(() => [
  { key: 'standard', label: '标准件', count: props.standardCount, approval: bomApprovalStatus(['StandardLongLead', 'StandardFormal', 'StandardSupplement']), application: materialApplicationStatus(props.standardItemIds) },
  { key: 'non-standard', label: '非标件', count: props.nonStandardCount, approval: bomApprovalStatus(['NonStandardWithDrawing']), application: materialApplicationStatus(props.nonStandardItemIds) },
  { key: 'electrical', label: '电气', count: props.electricalCount, approval: bomApprovalStatus(['ElectricalFormal', 'ElectricalSupplement']), application: materialApplicationStatus(props.electricalItemIds) },
])

const bomApprovalSummary = computed(() => {
  if (!props.releasePackage) return 'BOM审批：未发起'
  const currentStep = props.releasePackage.steps.find(step => step.status === 'current')
  return `BOM审批：${props.releasePackage.state}${currentStep ? ` · ${currentStep.stage}` : ''}`
})

const materialApplicationSummary = computed(() => {
  if (!props.materialApplications.length) return '物料申请：暂无'
  const pending = props.materialApplications.filter(item => item.status === 'Pending').length
  const approved = props.materialApplications.filter(item => item.status === 'Approved').length
  const rejected = props.materialApplications.filter(item => item.status === 'Rejected').length
  return `物料申请：待审 ${pending} · 已批 ${approved} · 驳回 ${rejected}`
})
</script>

<template>
  <section class="pdm-workbench" aria-label="工作台主页面">
    <div class="pdm-workbench-grid">
      <button type="button" class="pdm-panel pdm-stat-card pdm-stat-card-action" aria-label="进入项目图档" @click="emit('documents')">
        <span class="is-blue"><FolderTree :size="19" /></span>
        <div><small>项目图档</small><strong>{{ documentCount }}</strong><em>{{ warningCount ? `${warningCount} 个异常引用` : '引用结构正常' }}</em><em class="pdm-stat-card__flow">{{ drawingReviewSummary }}</em><div class="pdm-stat-card__table is-document"><div class="is-heading"><span>类型</span><span>数量</span><span>审批情况</span></div><div v-for="row in documentRows" :key="row.key" :aria-label="`${row.label}统计`"><span>{{ row.label }}</span><b>{{ row.count }}</b><em>{{ row.approval }}</em></div></div></div>
      </button>
      <button type="button" class="pdm-panel pdm-stat-card pdm-stat-card-action" aria-label="进入BOM数据" @click="emit('bom')">
        <span class="is-green"><Boxes :size="19" /></span>
        <div><small>BOM数据</small><strong>{{ standardCount + nonStandardCount + electricalCount }}</strong><em class="pdm-stat-card__flow">{{ bomApprovalSummary }}</em><em class="pdm-stat-card__flow">{{ materialApplicationSummary }}</em><div class="pdm-stat-card__table is-bom"><div class="is-heading"><span>类别</span><span>零件</span><span>审批情况</span><span>物料申请</span></div><div v-for="row in bomRows" :key="row.key" :aria-label="`${row.label}BOM统计`"><span>{{ row.label }}</span><b>{{ row.count }}</b><em>{{ row.approval }}</em><em>{{ row.application }}</em></div></div></div>
      </button>
      <article class="pdm-panel pdm-stat-card">
        <span class="is-orange"><PackageCheck :size="19" /></span>
        <div><small>当前发布包</small><strong class="is-code">{{ releasePackage?.number || '暂无' }}</strong><em>{{ releasePackage?.state || '尚未创建发布包' }}</em></div>
      </article>

      <div class="pdm-workbench-primary">
        <article class="pdm-panel pdm-workbench-detail pdm-project-people" aria-label="人员组织结构">
          <header class="pdm-panel-heading">
            <h2>人员组织结构</h2>
            <small>按项目阶段展示当前负责人</small>
            <span class="pdm-project-people__actions">
              <button v-if="rootProject.canManageMainStaffing" type="button" class="pdm-text-action" @click="openStaffingDialog">配置主项目分工</button>
              <button v-if="activeProject.canAssignDesigners" type="button" class="pdm-text-action" @click="openDesignerDialog">配置当前项目执行工程师</button>
            </span>
          </header>
          <div class="pdm-project-people__layout">
            <section class="pdm-project-people__section" aria-label="项目阶段负责人">
              <header><span><UsersRound :size="17" /></span><div><strong>项目阶段负责人</strong><small>{{ rootProject.executionUnitName || '执行事业部待分配' }}</small></div></header>
              <div class="pdm-project-staffing-list">
                <div v-for="row in staffingRows" :key="row.key" class="pdm-project-staffing-row">
                  <span><small>{{ row.stage }}</small><strong>{{ row.role }}</strong></span>
                  <div v-if="row.people.length" class="pdm-project-person-list">
                    <span v-for="person in row.people" :key="person.username" :title="person.name">{{ person.name }}</span>
                  </div>
                  <em v-else>无</em>
                </div>
              </div>
            </section>

            <section class="pdm-project-people__section is-customer" aria-label="客户联络人">
              <header><span><ContactRound :size="17" /></span><div><strong>客户联络人</strong><small>项目外部沟通窗口</small></div></header>
              <dl>
                <div><dt>客户单位</dt><dd :title="rootProject.customerName">{{ rootProject.customerName || '待关联客户' }}</dd></div>
                <div><dt>联络人</dt><dd class="is-pending">待维护</dd></div>
              </dl>
            </section>
          </div>
        </article>
      </div>

      <article class="pdm-panel pdm-workbench-detail">
        <header class="pdm-panel-heading"><h2>项目存储位置</h2></header>
        <dl class="pdm-location-list">
          <div><dt>图档库</dt><dd :title="project.vaultLocation">{{ project.vaultLocation }}</dd></div>
          <div><dt>发包目录</dt><dd :title="project.releaseLocation">{{ project.releaseLocation }}</dd></div>
        </dl>
      </article>
    </div>

    <el-dialog v-model="staffingDialogOpen" :title="`配置主项目分工 · ${rootProject.code}`" width="500px" append-to-body>
      <div class="pdm-project-staffing-form">
        <label class="pdm-dialog-field">项目经理（限1名）<el-select v-model="staffingForm.primaryProjectManager" class="pdm-project-person-select" filterable placeholder="输入姓名筛选" style="width:100%"><el-option v-for="user in projectManagerCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <label class="pdm-dialog-field">协同项目经理（可多选）<el-select v-model="staffingForm.collaborativeProjectManagers" class="pdm-project-person-select" multiple filterable placeholder="输入姓名筛选" style="width:100%"><el-option v-for="user in projectManagerCandidates.filter(item => item.username !== staffingForm.primaryProjectManager)" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <label class="pdm-dialog-field">主设（可多选）<el-select v-model="staffingForm.designLeads" class="pdm-project-person-select" multiple filterable placeholder="输入姓名筛选" style="width:100%"><el-option v-for="user in designLeadCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
      </div>
      <template #footer><el-button @click="staffingDialogOpen=false">取消</el-button><el-button type="primary" :loading="pending" @click="saveMainStaffing">保存分工</el-button></template>
    </el-dialog>

    <el-dialog v-model="designerDialogOpen" :title="`配置执行工程师 · ${project.code}`" width="500px" append-to-body>
      <label class="pdm-dialog-field">工程师（可多选）<el-select v-model="designerDraft" class="pdm-project-person-select" multiple filterable placeholder="输入姓名筛选" style="width:100%"><el-option-group label="本事业部（优先）"><el-option v-for="user in designerCandidates.filter(item => item.ownDivision)" :key="user.username" :label="user.displayName" :value="user.username" /></el-option-group><el-option-group label="其他事业部"><el-option v-for="user in designerCandidates.filter(item => !item.ownDivision)" :key="user.username" :label="`${user.displayName} · ${user.divisionName}`" :value="user.username" /></el-option-group></el-select></label>
      <p v-if="hasCrossDivisionSelection" class="pdm-dialog-note is-warning">已选择其他事业部人员，请确认跨事业部协作安排。</p>
      <p class="pdm-dialog-note">工程师可保持为空；由具备“分配执行工程师”权限的事业部负责人、机械主管、当前项目经理或主设配置。</p>
      <template #footer><el-button @click="designerDialogOpen=false">取消</el-button><el-button type="primary" :loading="pending" @click="saveDesigners">保存工程师</el-button></template>
    </el-dialog>
  </section>
</template>
