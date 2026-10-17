<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { ElMessage } from '../statusMessage'
import { getProjectPositionMembers, updateProjectPositionMembers } from '../api'
import type { MainProjectStaffingInput, OrganizationDirectory, ProjectPhaseOwners, ProjectPositionDefinition, ProjectSummary } from '../types'

const props = defineProps<{
  token?: string
  currentUsername: string; root: ProjectSummary; directory: OrganizationDirectory; canMaintainTeam: boolean
  commercialStaff: { salesPeople: string; presalesPeople: string }; canEditCommercial: boolean
  onSaveCommercial: (value: { salesPeople: string; presalesPeople: string }) => Promise<boolean | undefined>
  onUpdateMainStaffing: (id: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (id: string, users: string[]) => Promise<ProjectSummary>
  onUpdatePhaseOwners: (id: string, owners: ProjectPhaseOwners) => Promise<ProjectSummary>
}>()
const emit = defineEmits<{ positionsUpdated: [project: ProjectSummary] }>()
const open = ref(false)
const target = ref<ProjectSummary | null>(null)
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
const phases = reactive<Partial<Record<keyof ProjectPhaseOwners, string[]>>>({})
const definitions = [{ key: 'ElectricalHardware', label: '电气硬件' }, { key: 'StandardProcurement', label: '标准件采购' }, { key: 'NonStandardProcurement', label: '非标件采购' }, { key: 'NonStandardProduction', label: '非标件生产' }, { key: 'MechanicalAssembly', label: '机械装配' }, { key: 'ElectricalAssembly', label: '电气装配' }, { key: 'ElectricalCommissioning', label: '电气调试' }, { key: 'Acceptance', label: '验收' }] as const
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
const hardwareUsers = computed(() => companyUsers.value.filter(user => [user.role, ...(user.roles ?? [])].includes('HardwareEngineer')))
const engineerUsers = computed(() => companyUsers.value.filter(user => [user.role, ...(user.roles ?? [])].some(role => ['Engineer', 'ElectricalEngineer', 'CommissioningEngineer', 'HardwareEngineer', 'MechanicalManager', 'TechnicalAssistant', 'ProcessReviewer', 'Approver'].includes(role))))
async function configure(project?: ProjectSummary) {
  if (!project && !props.canMaintainTeam) return
  target.value = project ?? null
  error.value = ''
  staffing.primaryProjectManager = props.root.primaryProjectManager ?? ''
  staffing.collaborativeProjectManagers = [...props.root.collaborativeProjectManagers]
  staffing.designLeads = [...(props.root.designLeads?.length ? props.root.designLeads : props.root.designLead ? [props.root.designLead] : [])]
  for (const phase of definitions) phases[phase.key] = (props.root.phaseOwners?.[phase.key] ?? '').split('、').filter(Boolean)
  commercial.salesPeople = commercialUsers(props.commercialStaff.salesPeople)
  commercial.presalesPeople = commercialUsers(props.commercialStaff.presalesPeople)
  engineers.value = [...(project?.designers ?? [])]
  open.value = true
  const generation = ++positionGeneration
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
async function save() {
  if (saving.value || positionsLoading.value || (!target.value && !props.canMaintainTeam)) return
  if (!target.value && props.root.canManageMainStaffing && (!staffing.primaryProjectManager || !staffing.designLeads.length)) { error.value = '请选择项目经理和至少一名主设。'; return }
  saving.value = true
  error.value = ''
  try {
    if (target.value) await props.onUpdateDesigners(target.value.id, [...engineers.value])
    else {
      const value = { salesPeople: commercial.salesPeople.join('、'), presalesPeople: commercial.presalesPeople.join('、') }
      if (props.canEditCommercial && (value.salesPeople !== props.commercialStaff.salesPeople || value.presalesPeople !== props.commercialStaff.presalesPeople)) {
        if (!await props.onSaveCommercial(value)) { error.value = '销售、售前人员保存失败，请检查客户信息提示。'; return }
      }
      if (props.root.canManageMainStaffing) await props.onUpdateMainStaffing(props.root.id, { ...staffing, collaborativeProjectManagers: [...staffing.collaborativeProjectManagers], designLeads: [...staffing.designLeads] })
      if (props.root.canAssignDesigners) await props.onUpdatePhaseOwners(props.root.id, Object.fromEntries(definitions.map(phase => [phase.key, (phases[phase.key] ?? []).join('、')])))
    }
    if (props.token && originalMembers.value && JSON.stringify(customMembers.value) !== originalMembers.value)
      emit('positionsUpdated', await updateProjectPositionMembers(target.value?.id ?? props.root.id, customMembers.value, props.token))
    open.value = false
    ElMessage.success('人员分配已保存')
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '人员分配失败' }
  finally { saving.value = false }
}
defineExpose({ configure })
</script>

<template>
  <el-dialog v-model="open" :title="target ? `分配执行工程师 · ${target.code}` : '分配项目团队'" width="560px" append-to-body :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
    <div class="personnel-assignment">
      <template v-if="!target">

          <label>项目经理<el-select v-model="staffing.primaryProjectManager" aria-label="项目经理" filterable :disabled="saving || !root.canManageMainStaffing || currentUsername === root.primaryProjectManager"><el-option v-for="user in divisionUsers" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
          <label>协同项目经理<el-select v-model="staffing.collaborativeProjectManagers" aria-label="协同项目经理" multiple filterable :disabled="saving || !root.canManageMainStaffing"><el-option v-for="user in divisionUsers.filter(item => item.username !== staffing.primaryProjectManager)" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
          <label>主设<el-select v-model="staffing.designLeads" aria-label="主设" multiple filterable :disabled="saving || !root.canManageMainStaffing"><el-option v-for="user in divisionUsers" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>

        <label>电气硬件<el-select v-model="phases.ElectricalHardware" aria-label="电气硬件" multiple filterable clearable :disabled="saving || !root.canAssignDesigners"><el-option v-for="user in hardwareUsers" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <label>销售<el-select v-model="commercial.salesPeople" aria-label="团队销售人员" multiple filterable :disabled="saving || !canEditCommercial"><el-option v-for="user in companyUsers" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <label>售前<el-select v-model="commercial.presalesPeople" aria-label="团队售前人员" multiple filterable :disabled="saving || !canEditCommercial"><el-option v-for="user in companyUsers" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <label v-for="phase in definitions.filter(item => item.key !== 'ElectricalHardware')" :key="phase.key">{{ phase.label }}<el-select v-model="phases[phase.key]" :aria-label="phase.label" multiple filterable clearable :disabled="saving || !root.canAssignDesigners"><el-option v-for="user in companyUsers" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
      </template>
      <label v-else>执行工程师<el-select v-model="engineers" aria-label="执行工程师" multiple filterable :disabled="saving"><el-option v-for="user in engineerUsers" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
      <p v-if="positionsLoading" role="status">正在读取自定义岗位…</p>
      <template v-for="position in customPositions" :key="position.key">
        <label>{{ position.name }}{{ position.isActive ? '' : '（已停用）' }}<el-select v-model="customMembers[position.key]" :aria-label="`自定义岗位${position.name}`" multiple filterable clearable :disabled="saving"><el-option v-for="user in companyUsers" :disabled="!position.isActive && !customMembers[position.key]?.includes(user.username)" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <small>{{ position.scope === 'subtree' ? '适用于所分配项目及其子项目' : '仅适用于所分配项目' }}</small>
      </template>
      <p v-if="error" role="alert">{{ error }}</p>
    </div>
    <template #footer><el-button aria-label="取消人员分配" :disabled="saving" @click="open = false">取消</el-button><el-button aria-label="保存人员分配" type="primary" :loading="saving" @click="save">保存</el-button></template>
  </el-dialog>
</template>

<style scoped>
.personnel-assignment{display:grid;gap:12px}.personnel-assignment label{display:grid;grid-template-columns:110px minmax(0,1fr);align-items:center;gap:12px}.personnel-assignment p{color:var(--pdm-danger,#dc2626)}
</style>
