<script setup lang="ts">
import PersonName from './PersonName.vue'
import { projectPhasePersonnelRoles } from '../projectPersonnelRoles'
import UserSelect from './UserSelect'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { readProjectNameplate, saveApplicationProjectOrderDate, PdmApiError } from '../api'
import { ElMessage } from '../statusMessage'
import type { MainProjectStaffingInput, OrganizationDirectory, PdmCustomer, PdmUser, ProjectPhaseOwners, ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'
import ProjectNameplateDialog from './ProjectNameplateDialog.vue'
import ProjectContactInformation from './ProjectContactInformation.vue'
import ProjectPersonnelAssignment from './ProjectPersonnelAssignment.vue'
import ProjectStaffingDialog from './ProjectStaffingDialog.vue'
import ProjectAuthorization from './ProjectAuthorization.vue'
import { nameplateStatus, type ProjectNameplateView } from '../projectNameplate'

const props = defineProps<{ customers?: PdmCustomer[]; project: ProjectSummary; projects: ProjectSummary[]; token: string; users: PdmUser[]; currentUsername: string; directory: OrganizationDirectory; onUpdateMainStaffing: (id: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>; onUpdateDesigners: (id: string, users: string[]) => Promise<ProjectSummary>; onUpdatePhaseOwners: (id: string, owners: ProjectPhaseOwners) => Promise<ProjectSummary>; canEditApplicationOrderDate?: boolean; canViewApplications?: boolean; administrator?: boolean }>()
const emit = defineEmits<{ openApplication: [id: string] }>()
const staffAssignment = ref<'salesPeople' | 'presalesPeople' | null>(null)
watch(() => [props.project.id, props.token], () => { staffAssignment.value = null })
const assignment = ref<InstanceType<typeof ProjectPersonnelAssignment> | null>(null)
const staffingDialog = ref<InstanceType<typeof ProjectStaffingDialog> | null>(null)
const nameplateProject = ref<ProjectSummary | null>(null)
const nameplateOpen = ref(false)
const displayName = useUserDisplayName()
const familyProjects = computed(() => {
  const rootId = props.project.rootProjectId || props.project.parentProjectId || props.project.id
  const root = props.projects.find(project => project.id === rootId) ?? props.project
  const ids = new Set([root.id])
  let changed = true
  while (changed) {
    changed = false
    for (const project of props.projects) if (project.parentProjectId && ids.has(project.parentProjectId) && !ids.has(project.id)) { ids.add(project.id); changed = true }
  }
  return [root, ...props.projects.filter(project => project.id !== root.id && ids.has(project.id)).sort((a, b) => (a.childSequence ?? 0) - (b.childSequence ?? 0))]
})
const rootProject = computed(() => familyProjects.value[0]!)
const customer = computed(() => props.customers?.find(item => item.code === rootProject.value.customerCode))
const selectedProjectIds = ref<string[]>([])
const selectableProjects = computed(() => familyProjects.value.filter(item => item.canAssignDesigners))
const selectedProjects = computed(() => selectableProjects.value.filter(item => selectedProjectIds.value.includes(item.id)))
const allProjectsSelected = computed(() => selectableProjects.value.length > 0 && selectedProjects.value.length === selectableProjects.value.length)
watch([() => rootProject.value.id, () => props.token], () => { selectedProjectIds.value = [] })
watch(() => selectableProjects.value.map(item => item.id), ids => { selectedProjectIds.value = selectedProjectIds.value.filter(id => ids.includes(id)) })
function selectProject(id: string, checked: boolean) {
  selectedProjectIds.value = checked ? [...new Set([...selectedProjectIds.value, id])] : selectedProjectIds.value.filter(item => item !== id)
}
const orderDateOpen = ref(false), orderDatePending = ref(false), orderDate = ref('')
async function saveOrderDate() {
  if (orderDatePending.value) return
  orderDatePending.value = true
  try {
    const root = rootProject.value
    const saved = await saveApplicationProjectOrderDate(root.id, { orderDate: orderDate.value || null, expectedOrderDate: root.signedDate || null }, props.token)
    for (const item of familyProjects.value) item.signedDate = saved.signedDate || undefined
    if (familyProjects.value.some(x => x.id === props.project.id)) props.project.signedDate = saved.signedDate || undefined
    orderDateOpen.value = false
    ElMessage.success('订单日期已保存，原申请及审批记录完整保留。')
  } catch (cause) { ElMessage.error(cause instanceof PdmApiError ? cause.message : '订单日期保存失败，请刷新后重试。') }
  finally { orderDatePending.value = false }
}
const canMaintainTeam = computed(() => rootProject.value.canManageMainStaffing || rootProject.value.canAssignDesigners || rootProject.value.effectiveProjectPermissions?.includes("project.phase-owners.manage") === true)
const staffCandidates = computed(() => {
  const units = new Set(props.directory.units.filter(unit => unit.organizationId === rootProject.value.organizationId && unit.isActive).map(unit => unit.id))
  const members = new Set(props.directory.memberships.filter(item => units.has(item.unitId)).map(item => item.username))
  return props.users.filter(user => user.isActive && members.has(user.username))
})
function selectedPeople(value: string) { return value.split('、').filter(Boolean).map(name => props.users.find(user => user.username === name || user.displayName === name)?.username ?? name) }
const phaseRoles = projectPhasePersonnelRoles.filter(role => role.key !== 'ElectricalEngineer' && role.key !== 'ElectricalHardware')
function people(usernames: Array<string | undefined>) { return [...new Set(usernames.filter((user): user is string => Boolean(user)))].map(user => displayName(user)).join('、') || '—' }
function leads(project: ProjectSummary) { return project.designLeads?.length ? project.designLeads : [project.designLead] }
const assignmentColumns = [
  { key: 'ElectricalEngineer', label: '电气工程师' }, { key: 'ElectricalHardware', label: '硬件工程师' },
  { key: 'MechanicalAssembly', label: '机械装配' }, { key: 'ElectricalAssembly', label: '电气装配' },
] as const
const canMaintainOwners = computed(() => rootProject.value.effectiveProjectPermissions?.includes("project.phase-owners.manage") === true)
function phasePeople(project: ProjectSummary, phase: keyof ProjectPhaseOwners) { return (project.phaseOwners?.[phase] ?? '').split('、').filter(Boolean) }
function applyPhaseOwners(saved: ProjectSummary) {
  const item = props.projects.find(item => item.id === saved.id)
  if (item) item.phaseOwners = saved.phaseOwners
  if (props.project.id === saved.id) props.project.phaseOwners = saved.phaseOwners
}
function openNameplate(project: ProjectSummary) { nameplateProject.value = project; nameplateOpen.value = true }
type NameplateAccess = Pick<ProjectNameplateView, 'nameplate' | 'canEdit'>
const nameplateAccess = ref<Record<string, NameplateAccess>>({})
let nameplateLoadedAt = 0
let nameplateLoading = false
function nameplateHint(view: NameplateAccess) {
  const state = nameplateStatus(view.nameplate)
  return `设计人员：${state.executionComplete ? '已填写' : '待填写完整'}；硬件工程师：${state.hardwareComplete ? '已填写' : '待填写完整'}；${state.published ? '已发布，内容只读' : view.canEdit ? '可继续编辑' : '仅可查看'}`
}
let nameplateRequest = 0
async function loadNameplateAccess() {
  const request = ++nameplateRequest
  nameplateLoading = true
  const token = props.token
  nameplateAccess.value = {}
  const projectId = props.project.id
  const family = familyProjects.value.filter(item => item.canReadContent)
  try {
    const index = await readProjectNameplate(projectId, token, true)
    const results = Array.isArray(index)
      ? index.filter(item => family.some(project => project.id === item.projectId)).map(item => [item.projectId, item] as const)
      : (await Promise.allSettled(family.map(async item => [item.id, item.id === projectId ? index : await readProjectNameplate(item.id, token)] as const)))
        .flatMap(result => result.status === 'fulfilled' ? [result.value] : [])
    if (request === nameplateRequest) { nameplateAccess.value = Object.fromEntries(results); nameplateLoadedAt = Date.now() }
  } catch { if (request === nameplateRequest) ElMessage.error('铭牌状态读取失败，请刷新后重试。') }
  finally { if (request === nameplateRequest) nameplateLoading = false }
}
onBeforeUnmount(() => { nameplateRequest++ })
watch(() => JSON.stringify([props.token, props.currentUsername, ...familyProjects.value.map(item => `${item.id}:${item.canReadContent}:${item.designers.includes(props.currentUsername)}:${phasePeople(item, 'ElectricalHardware').includes(props.currentUsername)}`)]), () => { void loadNameplateAccess() }, { immediate: true })
watch(nameplateOpen, open => { if (!open) void loadNameplateAccess() })
defineExpose({ refresh: loadNameplateAccess, refreshIfStale: () => !nameplateLoading && Date.now() - nameplateLoadedAt > 30000 ? loadNameplateAccess() : Promise.resolve() })
</script>

<template>
  <section class="project-information" aria-label="项目信息">
    <section v-if="rootProject.sourceApplicationId" class="pdm-panel application-origin" aria-label="立项来源">
      <strong v-if="rootProject.isSpecialInitiation" class="pdm-status is-warn">特殊立项</strong>
      <span>来源申请：<button v-if="canViewApplications" class="pdm-text-action" @click="emit('openApplication', rootProject.sourceApplicationId!)">{{ rootProject.sourceApplicationNumber }}</button><span v-else>{{ rootProject.sourceApplicationNumber }}</span></span>
      <span>约定交货日期：{{ rootProject.deliveryDate || '—' }}</span><span>订单日期：{{ rootProject.signedDate || '未填写' }}</span>
      <button v-if="canEditApplicationOrderDate" class="pdm-text-action" @click="orderDate = rootProject.signedDate || ''; orderDateOpen = true">维护订单日期</button>
    </section>
    <el-dialog v-model="orderDateOpen" title="维护订单日期" width="440px" :close-on-click-modal="!orderDatePending" append-to-body><label class="pdm-dialog-field">订单日期（选填）<input v-model="orderDate" aria-label="主项目订单日期" type="date"></label><p>仅更新主项目及子项目的订单日期，原立项申请与审批记录完整保留。</p><template #footer><button class="pdm-secondary-action" :disabled="orderDatePending" @click="orderDateOpen = false">取消</button><button class="pdm-primary-action" :disabled="orderDatePending" @click="saveOrderDate">保存日期</button></template></el-dialog>
    <ProjectContactInformation :customer="customer" :customer-code="rootProject.customerCode" :customer-name="rootProject.customerName" :project-id="project.rootProjectId || project.parentProjectId || project.id" :token="token" v-slot="{ staff, canEdit, save }">
      <section class="pdm-panel project-information__staff" aria-label="项目人员信息列表">
        <header><h2>人员信息</h2><div class="project-information__staff-actions"><el-button v-if="canMaintainTeam && (rootProject.canManageMainStaffing || rootProject.canAssignDesigners)" aria-label="分配团队人员" @click="assignment?.configure()">分配</el-button><el-button v-if="canEdit" type="primary" aria-label="保存人员信息" @click="save">保存</el-button></div></header>
        <div class="project-information__table-wrap">
          <table class="project-information__table project-information__people">
            <thead><tr><th>项目经理</th><th>协同项目经理</th><th>主设计</th><th>设计人员</th><th>硬件工程师</th><th>销售经理</th><th>售前工程师</th><th v-for="phase in phaseRoles" :key="phase.key">{{ phase.label }}</th></tr></thead>
            <tbody><tr>
              <td><PersonName :name="people([rootProject.primaryProjectManager])" /></td>
              <td><button v-if="rootProject.canManageMainStaffing" type="button" class="pdm-text-action" aria-label="分配协同项目经理" title="分配协同项目经理" @click="staffingDialog?.configure(rootProject, true)"><PersonName :name="people(rootProject.collaborativeProjectManagers)" /></button><span v-else><PersonName :name="people(rootProject.collaborativeProjectManagers)" /></span></td>
              <td><PersonName :name="people(leads(rootProject))" /></td><td><PersonName :name="people(rootProject.designers)" /></td><td><PersonName :name="people((rootProject.phaseOwners?.ElectricalHardware ?? '').split('、'))" /></td><td><button v-if="canEdit" type="button" class="pdm-text-action" aria-label="分配销售经理" @click="staffAssignment = 'salesPeople'"><PersonName :name="people(selectedPeople(staff.salesPeople))" /></button><span v-else><PersonName :name="people(selectedPeople(staff.salesPeople))" /></span></td><td><button v-if="canEdit" type="button" class="pdm-text-action" aria-label="分配售前工程师" @click="staffAssignment = 'presalesPeople'"><PersonName :name="people(selectedPeople(staff.presalesPeople))" /></button><span v-else><PersonName :name="people(selectedPeople(staff.presalesPeople))" /></span></td><td v-for="phase in phaseRoles" :key="phase.key"><PersonName :name="people((rootProject.phaseOwners?.[phase.key] ?? '').split('、'))" /></td>
            </tr></tbody>
          </table>
        </div>
      </section>
      <el-dialog :model-value="Boolean(staffAssignment)" @update:model-value="staffAssignment = null" :title="staffAssignment === 'salesPeople' ? '分配销售经理' : '分配售前工程师'" width="480px" append-to-body>
        <UserSelect v-if="staffAssignment" :model-value="selectedPeople(staff[staffAssignment])" @update:model-value="staff[staffAssignment!] = $event.join('、')" aria-label="选择销售经理或售前工程师" multiple filterable placeholder="选择人员" style="width:100%"><el-option v-for="user in staffCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect>
        <template #footer><el-button aria-label="完成人员选择" @click="staffAssignment = null">完成选择</el-button></template>
      </el-dialog>
    <ProjectAuthorization v-if="project.effectiveProjectPermissions?.includes('project.templates.assign') && project.organizationId" :project-id="project.id" :company-id="project.organizationId" :token="token" />
    <ProjectPersonnelAssignment :administrator="administrator" ref="assignment" :token="token" @phase-owners-updated="applyPhaseOwners" @positions-updated="saved => Object.assign(projects.find(item => item.id === saved.id) ?? project, saved)" :current-username="currentUsername" :commercial-staff="staff" :can-edit-commercial="canEdit" :on-save-commercial="async value => { Object.assign(staff, value); return await save() }" :root="rootProject" :directory="directory" :can-maintain-team="canMaintainTeam" :on-update-main-staffing="onUpdateMainStaffing" :on-update-designers="onUpdateDesigners" :on-update-phase-owners="onUpdatePhaseOwners" />
    </ProjectContactInformation>
    <ProjectStaffingDialog :key="`${rootProject.id}:${token}`" ref="staffingDialog" :directory="directory" :current-username="currentUsername" :administrator="Boolean(administrator)" :on-save="onUpdateMainStaffing" />
    <section class="pdm-panel project-information__panel" aria-label="项目基本信息列表">
      <header><h2>基本信息</h2><span>共 {{ familyProjects.length }} 个主、子项目</span><div v-if="selectableProjects.length" class="project-information__batch-actions"><span>已选 {{ selectedProjects.length }} 项</span><el-button aria-label="批量分配人员" :disabled="!selectedProjects.length" @click="assignment?.configureMany(selectedProjects, canMaintainOwners)">批量分配人员</el-button></div></header>
      <div class="project-information__table-wrap">
        <table class="project-information__table">
          <colgroup><col style="width:36px"><col style="width:8%"><col style="width:16%"><col><col style="width:16%"><col span="11"></colgroup>
          <thead><tr><th><input type="checkbox" aria-label="全选可分配项目" :checked="allProjectsSelected" :indeterminate.prop="selectedProjects.length > 0 && !allProjectsSelected" :disabled="!selectableProjects.length" @change="selectedProjectIds = ($event.target as HTMLInputElement).checked ? selectableProjects.map(item => item.id) : []"></th><th>项目号</th><th>项目名称</th><th>别名</th><th>型号</th><th>序列号</th><th>事业部</th><th>设计人员</th><th class="project-information__assignment">电气工程师</th><th class="project-information__assignment">硬件工程师</th><th class="project-information__assignment">机械装配</th><th class="project-information__assignment">电气装配</th><th>数量</th><th>状态</th><th>订单日期</th><th>铭牌</th></tr></thead>
          <tbody><tr v-for="item in familyProjects" :key="item.id" :class="{ 'is-current': item.id === project.id }"><td><input type="checkbox" :aria-label="`选择项目 ${item.code}`" :checked="selectedProjectIds.includes(item.id)" :disabled="!item.canAssignDesigners" @change="selectProject(item.id, ($event.target as HTMLInputElement).checked)"></td><td>{{ item.code }}</td><td>{{ item.name }}</td><td>{{ item.projectAlias || '—' }}</td><td>{{ item.deviceModel || '—' }}</td><td>{{ item.serialNumbers.join('、') || '—' }}</td><td>{{ item.executionUnitName || '—' }}</td><td><button v-if="item.canAssignDesigners" type="button" class="pdm-text-action" :aria-label="`分配设计人员 ${item.code}`" @click="assignment?.configure(item)"><PersonName :name="people(item.designers)" /></button><span v-else><PersonName :name="people(item.designers)" /></span></td><td v-for="column in assignmentColumns" :key="column.key" class="project-information__assignment"><button v-if="item.canAssignDesigners && canMaintainOwners" type="button" class="pdm-text-action" :aria-label="`分配${column.label} ${item.code}`" @click="assignment?.configure(item, column.key)"><PersonName :name="people(phasePeople(item, column.key))" /></button><span v-else><PersonName :name="people(phasePeople(item, column.key))" /></span></td><td>{{ item.quantity }}</td><td>{{ item.businessStatus || item.stage }}</td><td>{{ item.signedDate || '—' }}</td><td><span v-if="nameplateAccess[item.id] && !nameplateAccess[item.id].nameplate.enabled && !nameplateAccess[item.id].canEdit">未启用</span><button v-else-if="nameplateAccess[item.id]" type="button" class="pdm-text-action" :class="{ 'is-nameplate-published': !!nameplateAccess[item.id].nameplate.releasePackageId }" :title="nameplateHint(nameplateAccess[item.id])" :aria-label="`${nameplateAccess[item.id].nameplate.enabled ? '查看铭牌' : '创建铭牌'} ${item.code}`" @click="openNameplate(item)">{{ nameplateAccess[item.id].nameplate.enabled ? nameplateStatus(nameplateAccess[item.id].nameplate).label : '创建' }}</button><span v-else>—</span></td></tr></tbody>
        </table>
      </div>
    </section>

    <ProjectNameplateDialog v-if="nameplateProject" v-model="nameplateOpen" :project="nameplateProject" :token="token" />
  </section>
</template>

<style scoped>
.application-origin{display:flex;gap:14px;align-items:center;flex-wrap:wrap;padding:10px 12px;font-size:12px;flex:none}.project-information{display:flex;flex-direction:column;gap:5px;flex:1;min-height:0;overflow:auto}
.project-information__panel{display:flex;flex-direction:column;flex:1;min-height:0;overflow:hidden}
.project-information__panel>header{display:flex;align-items:center;gap:12px;min-height:40px;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-information h2{margin:0;font-size:14px}.project-information header>span{font-size:11px;color:var(--pdm-muted)}
.project-information__table-wrap{flex:1;min-height:0;overflow:auto}
.project-information__table{width:100%;min-width:1100px;border-collapse:collapse;font-size:11px}
.project-information__panel .project-information__table{table-layout:fixed}
.project-information__table .project-information__assignment{padding-inline:4px;white-space:normal;overflow-wrap:anywhere}
.project-information__table th,.project-information__table td{height:32px;padding:0 8px;text-align:center;border-right:1px solid var(--pdm-border-soft);border-bottom:1px solid var(--pdm-border-soft);white-space:nowrap}
.project-information__table th{position:sticky;top:0;z-index:1;background:var(--pdm-panel-muted,var(--pdm-table-header));font-weight:500}
.project-information__table tbody tr:nth-child(even){background:var(--pdm-row-stripe,var(--pdm-surface-muted))}
.project-information__table .is-current{background:var(--pdm-blue-soft,var(--pdm-theme-accent-soft))}
.project-information__people{min-width:1200px;table-layout:fixed}.project-information__people th,.project-information__people td{padding:4px;white-space:normal;overflow-wrap:anywhere}.project-information__table button{font-size:11px}
.project-information__assignment .pdm-text-action{max-width:100%;white-space:normal;overflow-wrap:anywhere}
.project-information__staff{flex:none;overflow:hidden}.project-information__staff>header{display:flex;align-items:center;gap:12px;min-height:32px;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-information__staff-actions{display:flex;gap:5px;margin-left:auto}
.project-information__staff-actions :deep(.el-button){width:auto;min-width:60px;height:var(--pdm-button-compact);margin:0;padding:0 6px;font-size:11px}.project-information__staff .project-information__table-wrap{max-height:240px}
.project-information__batch-actions{display:flex;align-items:center;gap:12px;margin-left:auto;font-size:11px;color:var(--pdm-muted)}

.project-information__table .is-nameplate-published{color:var(--pdm-success,#16803c);font-weight:600}
.project-information__panel{flex:1 0 180px}
</style>
