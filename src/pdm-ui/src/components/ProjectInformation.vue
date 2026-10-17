<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { readProjectNameplate } from '../api'
import type { MainProjectStaffingInput, OrganizationDirectory, PdmUser, ProjectPhaseOwners, ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'
import ProjectNameplateDialog from './ProjectNameplateDialog.vue'
import ProjectContactInformation from './ProjectContactInformation.vue'
import ProjectPersonnelAssignment from './ProjectPersonnelAssignment.vue'

const props = defineProps<{ project: ProjectSummary; projects: ProjectSummary[]; token: string; users: PdmUser[]; currentUsername: string; directory: OrganizationDirectory; onUpdateMainStaffing: (id: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>; onUpdateDesigners: (id: string, users: string[]) => Promise<ProjectSummary>; onUpdatePhaseOwners: (id: string, owners: ProjectPhaseOwners) => Promise<ProjectSummary> }>()
const staffAssignment = ref<'salesPeople' | 'presalesPeople' | null>(null)
watch(() => [props.project.id, props.token], () => { staffAssignment.value = null })
const assignment = ref<InstanceType<typeof ProjectPersonnelAssignment> | null>(null)
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
const canMaintainTeam = computed(() => {
  if (rootProject.value.canAssignDesigners) return true
  const actor = props.currentUsername.toLowerCase()
  return rootProject.value.primaryProjectManager?.toLowerCase() === actor || props.directory.managers.some(item => item.unitId === rootProject.value.executionUnitId && [item.primaryManager, ...item.collaborativeManagers].some(user => user.toLowerCase() === actor))
})
const staffCandidates = computed(() => {
  const units = new Set(props.directory.units.filter(unit => unit.organizationId === rootProject.value.organizationId && unit.isActive).map(unit => unit.id))
  const members = new Set(props.directory.memberships.filter(item => units.has(item.unitId)).map(item => item.username))
  return props.users.filter(user => user.isActive && members.has(user.username))
})
function selectedPeople(value: string) { return value.split('、').filter(Boolean).map(name => props.users.find(user => user.username === name || user.displayName === name)?.username ?? name) }
const phaseRoles = [{ key: 'StandardProcurement', label: '标准件采购' }, { key: 'NonStandardProcurement', label: '非标件采购' }, { key: 'NonStandardProduction', label: '非标件生产' }, { key: 'MechanicalAssembly', label: '机械装配' }, { key: 'ElectricalAssembly', label: '电气装配' }, { key: 'ElectricalCommissioning', label: '电气调试' }, { key: 'Acceptance', label: '验收' }] as const
function people(usernames: Array<string | undefined>) { return [...new Set(usernames.filter((user): user is string => Boolean(user)))].map(user => displayName(user)).join('、') || '待分配' }
function leads(project: ProjectSummary) { return project.designLeads?.length ? project.designLeads : [project.designLead] }
function openNameplate(project: ProjectSummary) { nameplateProject.value = project; nameplateOpen.value = true }
const nameplateAccess = ref<Record<string, { enabled: boolean; canEdit: boolean }>>({})
let nameplateRequest = 0
async function loadNameplateAccess() {
  const request = ++nameplateRequest
  const token = props.token
  nameplateAccess.value = {}
  const results = await Promise.allSettled(familyProjects.value.filter(item => item.canReadContent).map(async item => {
    const result = await readProjectNameplate(item.id, token)
    return [item.id, { enabled: result.nameplate.enabled, canEdit: result.canEdit }] as const
  }))
  if (request === nameplateRequest) nameplateAccess.value = Object.fromEntries(results.flatMap(result => result.status === 'fulfilled' ? [result.value] : []))
}
watch(() => [props.token, ...familyProjects.value.map(item => `${item.id}:${item.canReadContent}`)], () => { void loadNameplateAccess() }, { immediate: true })
watch(nameplateOpen, open => { if (!open) void loadNameplateAccess() })
</script>

<template>
  <section class="project-information" aria-label="项目信息">
    <ProjectContactInformation :project-id="project.rootProjectId || project.parentProjectId || project.id" :token="token" v-slot="{ staff, canEdit, save }">
      <section class="pdm-panel project-information__staff" aria-label="项目人员信息列表">
        <header><h2>人员信息</h2><div class="project-information__staff-actions"><el-button v-if="canMaintainTeam && (rootProject.canManageMainStaffing || rootProject.canAssignDesigners)" aria-label="分配团队人员" @click="assignment?.configure()">分配</el-button><el-button v-if="canEdit" type="primary" aria-label="保存人员信息" @click="save">保存</el-button></div></header>
        <div class="project-information__table-wrap">
          <table class="project-information__table project-information__people">
            <thead><tr><th>项目经理</th><th>协同项目经理</th><th>主设</th><th>电气硬件</th><th>销售</th><th>售前</th><th v-for="phase in phaseRoles" :key="phase.key">{{ phase.label }}</th></tr></thead>
            <tbody><tr><td>{{ people([rootProject.primaryProjectManager]) }}</td><td>{{ people(rootProject.collaborativeProjectManagers) }}</td><td>{{ people(leads(rootProject)) }}</td><td>{{ people((rootProject.phaseOwners?.ElectricalHardware ?? '').split('、')) }}</td><td><button v-if="canEdit" type="button" class="pdm-text-action" aria-label="分配销售人员" @click="staffAssignment = 'salesPeople'">{{ people(selectedPeople(staff.salesPeople)) }}</button><span v-else>{{ people(selectedPeople(staff.salesPeople)) }}</span></td><td><button v-if="canEdit" type="button" class="pdm-text-action" aria-label="分配售前人员" @click="staffAssignment = 'presalesPeople'">{{ people(selectedPeople(staff.presalesPeople)) }}</button><span v-else>{{ people(selectedPeople(staff.presalesPeople)) }}</span></td><td v-for="phase in phaseRoles" :key="phase.key">{{ people((rootProject.phaseOwners?.[phase.key] ?? '').split('、')) }}</td></tr></tbody>
          </table>
        </div>
      </section>
      <el-dialog :model-value="Boolean(staffAssignment)" @update:model-value="staffAssignment = null" :title="staffAssignment === 'salesPeople' ? '分配销售人员' : '分配售前人员'" width="480px" append-to-body>
        <el-select v-if="staffAssignment" :model-value="selectedPeople(staff[staffAssignment])" @update:model-value="staff[staffAssignment!] = $event.join('、')" aria-label="选择销售售前人员" multiple filterable placeholder="选择人员" style="width:100%"><el-option v-for="user in staffCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></el-select>
        <template #footer><el-button aria-label="完成人员选择" @click="staffAssignment = null">完成选择</el-button></template>
      </el-dialog>
    <ProjectPersonnelAssignment ref="assignment" :token="token" @positions-updated="saved => Object.assign(projects.find(item => item.id === saved.id) ?? project, saved)" :current-username="currentUsername" :commercial-staff="staff" :can-edit-commercial="canEdit" :on-save-commercial="async value => { Object.assign(staff, value); return await save() }" :root="rootProject" :directory="directory" :can-maintain-team="canMaintainTeam" :on-update-main-staffing="onUpdateMainStaffing" :on-update-designers="onUpdateDesigners" :on-update-phase-owners="onUpdatePhaseOwners" />
    </ProjectContactInformation>
    <section class="pdm-panel project-information__panel" aria-label="项目基本信息列表">
      <header><h2>基本信息</h2><span>共 {{ familyProjects.length }} 个主、子项目</span></header>
      <div class="project-information__table-wrap">
        <table class="project-information__table">
          <colgroup><col style="width:8%"><col style="width:16%"><col><col style="width:16%"><col span="7"></colgroup>
          <thead><tr><th>项目号</th><th>项目名称</th><th>别名</th><th>型号</th><th>序列号</th><th>事业部</th><th>执行工程师</th><th>数量</th><th>状态</th><th>订单日期</th><th>铭牌</th></tr></thead>
          <tbody><tr v-for="item in familyProjects" :key="item.id" :class="{ 'is-current': item.id === project.id }"><td>{{ item.code }}</td><td>{{ item.name }}</td><td>{{ item.projectAlias || '—' }}</td><td>{{ item.deviceModel || '—' }}</td><td>{{ item.serialNumbers.join('、') || '—' }}</td><td>{{ item.executionUnitName || '—' }}</td><td><button v-if="item.canAssignDesigners" type="button" class="pdm-text-action" :aria-label="`分配执行工程师 ${item.code}`" @click="assignment?.configure(item)">{{ people(item.designers) }}</button><span v-else>{{ people(item.designers) }}</span></td><td>{{ item.quantity }}</td><td>{{ item.businessStatus || item.stage }}</td><td>{{ item.signedDate || '—' }}</td><td><span v-if="nameplateAccess[item.id] && !nameplateAccess[item.id].enabled && !nameplateAccess[item.id].canEdit">未启用</span><button v-else-if="nameplateAccess[item.id]" type="button" class="pdm-text-action" :aria-label="`${nameplateAccess[item.id]?.enabled ? '查看铭牌' : '创建铭牌'} ${item.code}`" @click="openNameplate(item)">{{ nameplateAccess[item.id]?.enabled ? '查看铭牌' : '创建' }}</button><span v-else>—</span></td></tr></tbody>
        </table>
      </div>
    </section>

    <ProjectNameplateDialog v-if="nameplateProject" v-model="nameplateOpen" :project="nameplateProject" :token="token" />
  </section>
</template>

<style scoped>
.project-information{display:flex;flex-direction:column;gap:5px;flex:1;min-height:0;overflow:auto}
.project-information__panel{display:flex;flex-direction:column;flex:1;min-height:0;overflow:hidden}
.project-information__panel>header{display:flex;align-items:center;gap:12px;min-height:40px;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-information h2{margin:0;font-size:14px}.project-information header>span{font-size:11px;color:var(--pdm-muted)}
.project-information__table-wrap{flex:1;min-height:0;overflow:auto}
.project-information__table{width:100%;min-width:1100px;border-collapse:collapse;font-size:11px}
.project-information__panel .project-information__table{table-layout:fixed}
.project-information__table th,.project-information__table td{height:32px;padding:0 8px;text-align:center;border-right:1px solid var(--pdm-border-soft);border-bottom:1px solid var(--pdm-border-soft);white-space:nowrap}
.project-information__table th{position:sticky;top:0;z-index:1;background:var(--pdm-panel-muted,var(--pdm-table-header));font-weight:500}
.project-information__table tbody tr:nth-child(even){background:var(--pdm-row-stripe,var(--pdm-surface-muted))}
.project-information__table .is-current{background:var(--pdm-blue-soft,var(--pdm-theme-accent-soft))}
.project-information__people{min-width:0;table-layout:fixed}.project-information__people th,.project-information__people td{padding:4px;white-space:normal;overflow-wrap:anywhere}.project-information__table button{font-size:11px}
.project-information__staff{flex:none;overflow:hidden}.project-information__staff>header{display:flex;align-items:center;gap:12px;min-height:32px;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-information__staff-actions{display:flex;gap:5px;margin-left:auto}
.project-information__staff-actions :deep(.el-button){width:auto;min-width:60px;height:var(--pdm-button-compact);margin:0;padding:0 6px;font-size:11px}.project-information__staff .project-information__table-wrap{max-height:240px}

.project-information__panel{flex:1 0 180px}
</style>
