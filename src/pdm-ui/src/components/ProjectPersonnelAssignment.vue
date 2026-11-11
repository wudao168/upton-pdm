<script setup lang="ts">
import { projectPhasePersonnelRoles } from '../projectPersonnelRoles'
import UserSelect from './UserSelect'
import { computed, nextTick, reactive, ref } from 'vue'
import { clearGlobalStatus, ElMessage } from '../statusMessage'
import { getProjectPositionMembers, updateProjectPositionMembers, PdmApiError } from '../api'
import type { MainProjectStaffingInput, OrganizationDirectory, ProjectPhaseOwners, ProjectPositionDefinition, ProjectSummary } from '../types'

const props = defineProps<{
  token?: string
  administrator?: boolean
  currentUsername: string; root: ProjectSummary; directory: OrganizationDirectory; canMaintainTeam: boolean
  commercialStaff: { salesPeople: string; presalesPeople: string }; canEditCommercial: boolean
  onSaveCommercial: (value: { salesPeople: string; presalesPeople: string }) => Promise<boolean | undefined>
  onUpdateMainStaffing: (id: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (id: string, users: string[]) => Promise<ProjectSummary>
  onUpdatePhaseOwners: (id: string, owners: ProjectPhaseOwners) => Promise<ProjectSummary>
}>()
const emit = defineEmits<{ positionsUpdated: [project: ProjectSummary]; phaseOwnersUpdated: [project: ProjectSummary] }>()
const open = ref(false)
const target = ref<ProjectSummary | null>(null)
const position = ref<keyof ProjectPhaseOwners | null>(null)
const batch = ref(false)
const batchProjects = ref<ProjectSummary[]>([])
const batchTotal = ref(0)
const batchAllowsOwners = ref(false)
const batchCompleted = computed(() => batchTotal.value - batchProjects.value.length)
const saving = ref(false)
const error = ref('')
const customPositions = ref<ProjectPositionDefinition[]>([])
const customMembers = ref<Record<string, string[]>>({})
const originalMembers = ref('')
const positionsLoading = ref(false)
let positionGeneration = 0
const staffing = reactive<MainProjectStaffingInput>({ primaryProjectManager: '', collaborativeProjectManagers: [], designLeads: [] })
const commercial = reactive({ salesPeople: [] as string[], presalesPeople: [] as string[] })
function commercialUsers(value: string) { return value.split('、').filter(Boolean).map(name => props.directory.users.find(user => user.username === name || user.displayName === name)?.username ?? name) }
const engineers = ref<string[]>([])
const originalEngineers = ref('[]')
const phases = reactive<Partial<Record<keyof ProjectPhaseOwners, string[]>>>({})
const definitions = projectPhasePersonnelRoles
function inDivision(unitId: string): boolean {
  const unit = props.directory.units.find(item => item.id === unitId)
  return unitId === props.root.executionUnitId || Boolean(unit?.parentUnitId && inDivision(unit.parentUnitId))
}
const divisionUsers = computed(() => {
  const members = new Set(props.directory.memberships.filter(item => inDivision(item.unitId)).map(item => item.username))
  return props.directory.users.filter(user => user.isActive && members.has(user.username))
})
const companyUsers = computed(() => {
  const units = new Set(props.directory.units.filter(unit => unit.organizationId === props.root.organizationId && unit.isActive).map(unit => unit.id))
  const members = new Set(props.directory.memberships.filter(item => units.has(item.unitId)).map(item => item.username))
  return props.directory.users.filter(user => user.isActive && members.has(user.username))
})
const hardwareUsers = computed(() => companyUsers.value)
const engineerUsers = computed(() => companyUsers.value.filter(user => user.canBeDesigner))
const electricalUsers = computed(() => companyUsers.value)
const positionCandidates = computed(() => {
  const candidates = position.value === 'ElectricalEngineer' ? electricalUsers.value : position.value === 'ElectricalHardware' ? hardwareUsers.value : companyUsers.value
  const selected = position.value ? phases[position.value] ?? [] : []
  const usernames = new Set([...candidates.map(user => user.username), ...selected])
  return props.directory.users.filter(user => usernames.has(user.username))
})
async function configure(project?: ProjectSummary, phase?: keyof ProjectPhaseOwners) {
  if (saving.value) return
  if (!project && !props.canMaintainTeam) return
  clearGlobalStatus()
  batch.value = false
  target.value = project ?? null
  position.value = phase ?? null
  error.value = ''
  staffing.primaryProjectManager = props.root.primaryProjectManager ?? ''
  staffing.collaborativeProjectManagers = [...props.root.collaborativeProjectManagers]
  staffing.designLeads = [...(props.root.designLeads?.length ? props.root.designLeads : props.root.designLead ? [props.root.designLead] : [])]
  for (const phase of definitions) phases[phase.key] = (props.root.phaseOwners?.[phase.key] ?? '').split('、').filter(Boolean)
  if (!phases.Acceptance?.length) {
    const qualityUnits = new Set(props.directory.units.filter(unit => unit.isActive && unit.organizationId === props.root.organizationId && /质量部|品质部|质检部/.test(unit.name)).map(unit => unit.id))
    const activeUsers = new Set(companyUsers.value.map(user => user.username))
    phases.Acceptance = [...new Set(props.directory.managers.filter(item => qualityUnits.has(item.unitId)).flatMap(item => [item.primaryManager, ...item.collaborativeManagers]).filter(user => activeUsers.has(user)))]
  }
  commercial.salesPeople = commercialUsers(props.commercialStaff.salesPeople)
  commercial.presalesPeople = commercialUsers(props.commercialStaff.presalesPeople)
  engineers.value = [...(project?.designers ?? props.root.designers)]
  originalEngineers.value = JSON.stringify(engineers.value)
  open.value = true
  const generation = ++positionGeneration
  positionsLoading.value = false
  if (project && phase) { phases[phase] = (project.phaseOwners?.[phase] ?? '').split('、').filter(Boolean); return }
  customPositions.value = []
  customMembers.value = {}
  originalMembers.value = ''
  if (!props.token) return
  positionsLoading.value = true
  try {
    const value = await getProjectPositionMembers(project?.id ?? props.root.id, props.token)
    if (generation !== positionGeneration) return
    customPositions.value = value.positions
    customMembers.value = Object.fromEntries(value.positions.map(position => [position.key, [...(value.members[position.key] ?? [])]]))
    originalMembers.value = JSON.stringify(customMembers.value)
  } catch (cause) { if (generation === positionGeneration) error.value = cause instanceof Error ? cause.message : '读取项目岗位失败，请关闭后重试' }
  finally { if (generation === positionGeneration) positionsLoading.value = false }
}
function configureMany(projects: ProjectSummary[], allowsOwners: boolean) {
  if (saving.value || !projects.length) return
  clearGlobalStatus()
  ++positionGeneration
  batch.value = true
  batchProjects.value = [...projects]
  batchTotal.value = projects.length
  batchAllowsOwners.value = allowsOwners
  target.value = projects[0]!
  position.value = null
  engineers.value = []
  for (const phase of definitions) phases[phase.key] = []
  customPositions.value = []
  customMembers.value = {}
  originalMembers.value = ''
  positionsLoading.value = false
  error.value = ''
  open.value = true
}
async function save() {
  if (saving.value || positionsLoading.value || (!target.value && !props.canMaintainTeam)) return
  if (batch.value && !(position.value ? phases[position.value]?.length : engineers.value.length)) { error.value = '请选择至少一名人员。'; return }
  if (!target.value && props.root.canManageMainStaffing && !staffing.primaryProjectManager) { error.value = '请选择项目经理，主设计可稍后分配。'; return }
  saving.value = true
  error.value = ''
  try {
    if (batch.value) {
      while (batchProjects.value.length) {
        const project = batchProjects.value[0]!
        const saved = position.value
          ? await props.onUpdatePhaseOwners(project.id, { ...project.phaseOwners, [position.value]: (phases[position.value] ?? []).join('、') })
          : await props.onUpdateDesigners(project.id, [...engineers.value])
        emit('positionsUpdated', saved)
        batchProjects.value.shift()
      }
    }
    else if (target.value && position.value) {
      const saved = await props.onUpdatePhaseOwners(target.value.id, { ...target.value.phaseOwners, [position.value]: (phases[position.value] ?? []).join('、') })
      emit('phaseOwnersUpdated', saved)
    }
    else if (target.value) await props.onUpdateDesigners(target.value.id, [...engineers.value])
    else {
      const value = { salesPeople: commercial.salesPeople.join('、'), presalesPeople: commercial.presalesPeople.join('、') }
      if (props.canEditCommercial && (value.salesPeople !== props.commercialStaff.salesPeople || value.presalesPeople !== props.commercialStaff.presalesPeople)) {
        if (!await props.onSaveCommercial(value)) { error.value = '销售经理、售前工程师保存失败，请检查客户信息提示。'; return }
      }
      if (props.root.canManageMainStaffing) await props.onUpdateMainStaffing(props.root.id, { ...staffing, collaborativeProjectManagers: [...staffing.collaborativeProjectManagers], designLeads: [...staffing.designLeads] })
      if (props.root.canAssignDesigners && JSON.stringify(engineers.value) !== originalEngineers.value) {
        const saved = await props.onUpdateDesigners(props.root.id, [...engineers.value])
        originalEngineers.value = JSON.stringify(engineers.value)
        emit('positionsUpdated', saved)
      }
      if (props.root.canAssignDesigners) await props.onUpdatePhaseOwners(props.root.id, { ...props.root.phaseOwners, ...Object.fromEntries(definitions.map(phase => [phase.key, (phases[phase.key] ?? []).join('、')])) })
    }
    if (!position.value && props.token && originalMembers.value && JSON.stringify(customMembers.value) !== originalMembers.value)
      emit('positionsUpdated', await updateProjectPositionMembers(target.value?.id ?? props.root.id, customMembers.value, props.token))
    open.value = false
    await nextTick()
    ElMessage.success(batch.value ? `已完成 ${batchTotal.value} 个项目的人员分配` : '人员分配已保存')
  } catch (cause) {
    const message = cause instanceof PdmApiError ? cause.message : '人员分配保存失败，请检查网络后重试。'
    error.value = batch.value ? `已保存 ${batchCompleted.value} 个项目，剩余 ${batchProjects.value.length} 个未保存（从 ${batchProjects.value[0]!.code} 开始）。${message}` : message
  }
  finally { saving.value = false }
}
defineExpose({ configure, configureMany })
</script>

<template>
  <el-dialog v-model="open" :title="batch ? `批量分配人员 · ${batchTotal} 个项目` : target ? `分配${position ? definitions.find(item => item.key === position)?.label : '设计人员'} · ${target.code}` : '分配项目团队'" width="560px" append-to-body :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
    <div class="personnel-assignment">
      <template v-if="batch">
        <div class="personnel-assignment__batch-summary"><p>将统一设置所选项目的该岗位人员，其他岗位保持不变。</p><p>待分配项目：{{ batchProjects.map(item => item.code).join('、') || '全部已完成' }}</p><p v-if="saving || batchCompleted" role="status">已保存 {{ batchCompleted }} / {{ batchTotal }} 个项目</p></div>
        <label>分配岗位<el-select :model-value="position || 'designers'" aria-label="批量分配岗位" :disabled="saving || batchCompleted > 0" @update:model-value="position = $event === 'designers' ? null : $event; error = ''"><el-option label="设计人员" value="designers" /><template v-if="batchAllowsOwners"><el-option v-for="phase in definitions.filter(item => ['ElectricalEngineer', 'ElectricalHardware', 'MechanicalAssembly', 'ElectricalAssembly'].includes(item.key))" :key="phase.key" :label="phase.label" :value="phase.key" /></template></el-select></label>
      </template>
      <label v-if="position">{{ definitions.find(item => item.key === position)?.label }}<UserSelect v-model="phases[position]" :aria-label="definitions.find(item => item.key === position)?.label" multiple filterable clearable :disabled="saving || (batch && batchCompleted > 0)"><el-option v-for="user in positionCandidates" :key="user.username" :label="user.displayName" :value="user.username" :disabled="!user.isActive" /></UserSelect></label>
      <template v-else-if="!target">

          <label>项目经理<UserSelect v-model="staffing.primaryProjectManager" aria-label="项目经理" filterable :disabled="saving || !root.canManageMainStaffing || (!root.effectiveProjectPermissions?.includes('project.staffing.replace-self') && currentUsername === root.primaryProjectManager)"><el-option v-for="user in divisionUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
          <label>协同项目经理<UserSelect v-model="staffing.collaborativeProjectManagers" aria-label="协同项目经理" multiple filterable :disabled="saving || !root.canManageMainStaffing"><el-option v-for="user in divisionUsers.filter(item => item.username !== staffing.primaryProjectManager)" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
          <label>主设计<UserSelect v-model="staffing.designLeads" aria-label="主设计" multiple filterable :disabled="saving || !root.canManageMainStaffing"><el-option v-for="user in divisionUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>

        <label>硬件工程师<UserSelect v-model="phases.ElectricalHardware" aria-label="硬件工程师" multiple filterable clearable :disabled="saving || !root.canAssignDesigners"><el-option v-for="user in hardwareUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
        <label>设计人员<UserSelect v-model="engineers" aria-label="设计人员" multiple filterable clearable :disabled="saving || !root.canAssignDesigners"><el-option v-for="user in engineerUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
        <label>销售经理<UserSelect v-model="commercial.salesPeople" aria-label="团队销售经理" multiple filterable :disabled="saving || !canEditCommercial"><el-option v-for="user in companyUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
        <label>售前工程师<UserSelect v-model="commercial.presalesPeople" aria-label="团队售前工程师" multiple filterable :disabled="saving || !canEditCommercial"><el-option v-for="user in companyUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
        <label v-for="phase in definitions.filter(item => item.key !== 'ElectricalHardware')" :key="phase.key">{{ phase.label }}<UserSelect v-model="phases[phase.key]" :aria-label="phase.label" multiple filterable clearable :disabled="saving || !root.canAssignDesigners"><el-option v-for="user in phase.key === 'ElectricalEngineer' ? electricalUsers : companyUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
      </template>
      <label v-else>设计人员<UserSelect v-model="engineers" aria-label="设计人员" multiple filterable :disabled="saving || (batch && batchCompleted > 0)"><el-option v-for="user in engineerUsers" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
      <p v-if="positionsLoading" role="status">正在读取自定义岗位…</p>
      <template v-for="customPosition in position ? [] : customPositions" :key="customPosition.key">
        <label>{{ customPosition.name }}{{ customPosition.isActive ? '' : '（已停用）' }}<UserSelect v-model="customMembers[customPosition.key]" :aria-label="`自定义岗位${customPosition.name}`" multiple filterable clearable :disabled="saving"><el-option v-for="user in companyUsers" :disabled="!customPosition.isActive && !customMembers[customPosition.key]?.includes(user.username)" :key="user.username" :label="user.displayName" :value="user.username" /></UserSelect></label>
        <small>{{ customPosition.scope === 'subtree' ? '适用于所分配项目及其子项目' : '仅适用于所分配项目' }}</small>
      </template>
      <p v-if="error" role="alert">{{ error }}</p>
    </div>
    <template #footer><el-button aria-label="取消人员分配" :disabled="saving" @click="open = false">取消</el-button><el-button aria-label="保存人员分配" type="primary" :loading="saving" @click="save">{{ batch && batchCompleted ? '重试未完成项目' : '保存' }}</el-button></template>
  </el-dialog>
</template>

<style scoped>
.personnel-assignment{display:grid;gap:12px}.personnel-assignment label{display:grid;grid-template-columns:110px minmax(0,1fr);align-items:center;gap:12px}.personnel-assignment p{color:var(--pdm-danger,var(--pdm-danger))}
.personnel-assignment__batch-summary p{margin:0 0 6px;color:var(--pdm-muted);overflow-wrap:anywhere}
</style>
