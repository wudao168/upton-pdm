<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { computed, reactive, ref } from 'vue'
import type { OrganizationDirectory, OrganizationUnit, PdmUser, ProjectOrganization, SaveOrganizationUnitInput, SaveProjectOrganizationInput } from '../types'

const props = defineProps<{
  directory: OrganizationDirectory
  pending: boolean
  onSaveOrganization: (input: SaveProjectOrganizationInput) => Promise<ProjectOrganization>
  onSaveUnit: (input: SaveOrganizationUnitInput) => Promise<OrganizationUnit>
  onUpdateMemberships: (username: string, unitIds: string[], primaryUnitId: string) => Promise<OrganizationDirectory>
  onUpdateManagers: (unitId: string, primaryManager: string, collaborativeManagers: string[]) => Promise<OrganizationDirectory>
}>()

const companyDialog = ref(false)
const unitDialog = ref(false)
const membershipDialog = ref(false)
const managerDialog = ref(false)
const selectedUser = ref<PdmUser | null>(null)
const selectedUnit = ref<OrganizationUnit | null>(null)
const companyForm = reactive<SaveProjectOrganizationInput>({ name: '', projectCompanyCode: '', modelCompanyCode: '', isActive: true })
const unitForm = reactive<SaveOrganizationUnitInput>({ organizationId: '', parentUnitId: undefined, code: '', name: '', kind: 'BusinessDivision', isActive: true, sortOrder: 0 })
const membershipUnits = ref<string[]>([])
const primaryUnit = ref('')
const primaryManager = ref('')
const collaborativeManagers = ref<string[]>([])

const activeUsers = computed(() => props.directory.users.filter(user => user.isActive))
const sortedUnits = computed(() => [...props.directory.units].sort((left, right) => left.organizationId.localeCompare(right.organizationId) || left.sortOrder - right.sortOrder || left.name.localeCompare(right.name, 'zh-CN')))
const businessDivisions = computed(() => sortedUnits.value.filter(unit => unit.kind === 'BusinessDivision' && unit.isActive))
const unitOptions = computed(() => sortedUnits.value.filter(unit => unit.isActive && (!unitForm.organizationId || unit.organizationId === unitForm.organizationId) && unit.id !== unitForm.id))

function companyName(id: string) { return props.directory.organizations.find(item => item.id === id)?.name ?? '未知公司' }
function unitName(id: string) { return props.directory.units.find(item => item.id === id)?.name ?? '未设置' }
function userName(username: string) { const user = props.directory.users.find(item => item.username === username); return user ? `${user.displayName}（${username}）` : username }
function kindName(kind: OrganizationUnit['kind']) { return ({ BusinessDivision: '事业部', Department: '部门', Team: '团队' })[kind] }

function openCompany(item?: ProjectOrganization) {
  Object.assign(companyForm, item ? { id: item.id, name: item.name, projectCompanyCode: item.projectCompanyCode, modelCompanyCode: item.modelCompanyCode, isActive: item.isActive !== false } : { id: undefined, name: '', projectCompanyCode: '', modelCompanyCode: '', isActive: true })
  companyDialog.value = true
}
function openUnit(item?: OrganizationUnit) {
  Object.assign(unitForm, item ? { ...item, parentUnitId: item.parentUnitId } : { id: undefined, organizationId: props.directory.organizations.find(company => company.isActive !== false)?.id ?? '', parentUnitId: undefined, code: '', name: '', kind: 'BusinessDivision', isActive: true, sortOrder: sortedUnits.value.length })
  unitDialog.value = true
}
function openMembership(user: PdmUser) {
  selectedUser.value = user
  const memberships = props.directory.memberships.filter(item => item.username === user.username)
  membershipUnits.value = memberships.map(item => item.unitId)
  primaryUnit.value = memberships.find(item => item.isPrimary)?.unitId ?? memberships[0]?.unitId ?? ''
  membershipDialog.value = true
}
function openManagers(unit: OrganizationUnit) {
  selectedUnit.value = unit
  const managers = props.directory.managers.find(item => item.unitId === unit.id)
  primaryManager.value = managers?.primaryManager ?? ''
  collaborativeManagers.value = [...(managers?.collaborativeManagers ?? [])]
  managerDialog.value = true
}
function isWithin(unitId: string, divisionId: string) {
  let current = props.directory.units.find(unit => unit.id === unitId)
  while (current) {
    if (current.id === divisionId) return true
    current = current.parentUnitId ? props.directory.units.find(unit => unit.id === current!.parentUnitId) : undefined
  }
  return false
}
function divisionCandidates(divisionId: string) {
  const usernames = new Set(props.directory.memberships.filter(item => isWithin(item.unitId, divisionId)).map(item => item.username))
  return activeUsers.value.filter(user => usernames.has(user.username))
}

async function saveCompany() {
  if (!companyForm.name.trim() || !companyForm.projectCompanyCode.trim() || !companyForm.modelCompanyCode.trim()) return ElMessage.warning('请完整填写公司名称和两类代码')
  try { await props.onSaveOrganization({ ...companyForm }); companyDialog.value = false; ElMessage.success('公司设置已保存') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '公司保存失败') }
}
async function saveUnit() {
  if (!unitForm.organizationId || !unitForm.code.trim() || !unitForm.name.trim()) return ElMessage.warning('请完整填写组织资料')
  if (unitForm.kind !== 'BusinessDivision' && !unitForm.parentUnitId) return ElMessage.warning('部门或团队必须选择上级组织')
  try { await props.onSaveUnit({ ...unitForm, parentUnitId: unitForm.kind === 'BusinessDivision' ? undefined : unitForm.parentUnitId }); unitDialog.value = false; ElMessage.success('组织单元已保存') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '组织保存失败') }
}
async function saveMemberships() {
  if (!selectedUser.value || !membershipUnits.value.length || !primaryUnit.value) return ElMessage.warning('请选择所属组织和主组织')
  try { await props.onUpdateMemberships(selectedUser.value.username, membershipUnits.value, primaryUnit.value); membershipDialog.value = false; ElMessage.success('人员归属已保存') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '人员归属保存失败') }
}
async function saveManagers() {
  if (!selectedUnit.value || !primaryManager.value) return ElMessage.warning('请选择事业部主负责人')
  try { await props.onUpdateManagers(selectedUnit.value.id, primaryManager.value, collaborativeManagers.value); managerDialog.value = false; ElMessage.success('事业部负责人已保存') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '负责人保存失败') }
}
</script>

<template>
  <section class="pdm-project-manager">
    <header class="pdm-pagebar"><div><div class="pdm-breadcrumb">系统管理 <span>/</span> 组织结构</div><h1>公司与组织结构</h1><p>公司维护项目号及设备型号代码；公司下设置事业部、部门、团队和人员归属。</p></div><div class="pdm-page-actions"><button class="pdm-secondary-action" type="button" @click="openUnit()">新增组织</button><button class="pdm-primary-action" type="button" @click="openCompany()">新增公司</button></div></header>

    <section class="pdm-panel org-section"><header class="pdm-panel-heading"><div><h2>公司设置</h2><small>项目号公司代码1位；设备型号公司代码1至8位</small></div></header>
      <table class="pdm-project-table"><thead><tr><th>公司</th><th>项目号代码</th><th>设备型号代码</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="company in directory.organizations" :key="company.id"><td>{{ company.name }}</td><td>{{ company.projectCompanyCode }}</td><td>{{ company.modelCompanyCode }}</td><td>{{ company.isActive === false ? '停用' : '启用' }}</td><td><button class="pdm-text-action" type="button" @click="openCompany(company)">编辑</button></td></tr></tbody></table>
    </section>

    <section class="pdm-panel org-section"><header class="pdm-panel-heading"><div><h2>组织单元</h2><small>事业部直接隶属公司，部门和团队可继续分层</small></div></header>
      <table class="pdm-project-table"><thead><tr><th>公司</th><th>编码</th><th>名称</th><th>类型</th><th>上级</th><th>负责人</th><th>操作</th></tr></thead><tbody><tr v-for="unit in sortedUnits" :key="unit.id"><td>{{ companyName(unit.organizationId) }}</td><td>{{ unit.code }}</td><td>{{ unit.name }}</td><td>{{ kindName(unit.kind) }}</td><td>{{ unit.parentUnitId ? unitName(unit.parentUnitId) : '公司' }}</td><td><template v-if="unit.kind === 'BusinessDivision'"><span>{{ userName(directory.managers.find(item => item.unitId === unit.id)?.primaryManager || '未设置') }}</span></template><template v-else>—</template></td><td><button class="pdm-text-action" type="button" @click="openUnit(unit)">编辑</button><button v-if="unit.kind === 'BusinessDivision'" class="pdm-text-action" type="button" @click="openManagers(unit)">负责人</button></td></tr></tbody></table>
      <p v-if="!sortedUnits.length" class="pdm-empty-info">尚未创建事业部。请先新增事业部，再设置人员归属。</p>
    </section>

    <section class="pdm-panel org-section"><header class="pdm-panel-heading"><div><h2>人员归属</h2><small>一个账号可属于多个组织，但主组织只能有一个，且必须在同一公司内</small></div></header>
      <table class="pdm-project-table"><thead><tr><th>账号</th><th>姓名</th><th>系统角色</th><th>主组织</th><th>其他组织</th><th>操作</th></tr></thead><tbody><tr v-for="user in directory.users" :key="user.username"><td>{{ user.username }}</td><td>{{ user.displayName }}</td><td>{{ user.role }}</td><td>{{ unitName(directory.memberships.find(item => item.username === user.username && item.isPrimary)?.unitId || '') }}</td><td>{{ directory.memberships.filter(item => item.username === user.username && !item.isPrimary).map(item => unitName(item.unitId)).join('、') || '—' }}</td><td><button class="pdm-text-action" type="button" :disabled="!user.isActive" @click="openMembership(user)">设置归属</button></td></tr></tbody></table>
    </section>

    <el-dialog v-model="companyDialog" :title="companyForm.id ? '编辑公司' : '新增公司'" width="520px"><div class="org-form"><label>公司名称<input v-model="companyForm.name" maxlength="200"></label><label>项目号公司代码<input v-model="companyForm.projectCompanyCode" maxlength="1" placeholder="如 7"></label><label>设备型号公司代码<input v-model="companyForm.modelCompanyCode" maxlength="8" placeholder="如 AK"></label><label><input v-model="companyForm.isActive" type="checkbox"> 启用</label></div><template #footer><button class="pdm-secondary-action" type="button" @click="companyDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveCompany">保存</button></template></el-dialog>
    <el-dialog v-model="unitDialog" :title="unitForm.id ? '编辑组织' : '新增组织'" width="560px"><div class="org-form"><label>所属公司<select v-model="unitForm.organizationId"><option v-for="company in directory.organizations.filter(item => item.isActive !== false)" :key="company.id" :value="company.id">{{ company.name }}</option></select></label><label>类型<select v-model="unitForm.kind"><option value="BusinessDivision">事业部</option><option value="Department">部门</option><option value="Team">团队</option></select></label><label v-if="unitForm.kind !== 'BusinessDivision'">上级组织<select v-model="unitForm.parentUnitId"><option value="" disabled>请选择</option><option v-for="unit in unitOptions" :key="unit.id" :value="unit.id">{{ unit.name }}</option></select></label><label>组织编码<input v-model="unitForm.code" maxlength="40"></label><label>组织名称<input v-model="unitForm.name" maxlength="160"></label><label>排序<input v-model.number="unitForm.sortOrder" type="number"></label><label><input v-model="unitForm.isActive" type="checkbox"> 启用</label></div><template #footer><button class="pdm-secondary-action" type="button" @click="unitDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveUnit">保存</button></template></el-dialog>
    <el-dialog v-model="membershipDialog" :title="`人员归属 · ${selectedUser?.displayName ?? ''}`" width="600px"><div class="org-form"><label>所属组织<el-select v-model="membershipUnits" multiple filterable style="width:100%"><el-option v-for="unit in directory.units.filter(item => item.isActive)" :key="unit.id" :label="`${companyName(unit.organizationId)} / ${unit.name}`" :value="unit.id" /></el-select></label><label>主组织<el-select v-model="primaryUnit" style="width:100%"><el-option v-for="unitId in membershipUnits" :key="unitId" :label="unitName(unitId)" :value="unitId" /></el-select></label></div><template #footer><button class="pdm-secondary-action" type="button" @click="membershipDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveMemberships">保存</button></template></el-dialog>
    <el-dialog v-model="managerDialog" :title="`事业部负责人 · ${selectedUnit?.name ?? ''}`" width="600px"><div class="org-form"><label>主负责人<el-select v-model="primaryManager" filterable style="width:100%"><el-option v-for="user in divisionCandidates(selectedUnit?.id ?? '')" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label><label>协同负责人<el-select v-model="collaborativeManagers" multiple filterable style="width:100%"><el-option v-for="user in divisionCandidates(selectedUnit?.id ?? '').filter(item => item.username !== primaryManager)" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label></div><template #footer><button class="pdm-secondary-action" type="button" @click="managerDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveManagers">保存</button></template></el-dialog>
  </section>
</template>

<style scoped>
.org-section { margin-bottom: 18px; overflow: hidden; }
.org-section table { width: 100%; }
.org-form { display: grid; gap: 14px; }
.org-form label { display: grid; gap: 7px; color: #435066; font-size: 13px; }
.org-form input:not([type='checkbox']), .org-form select { min-height: 38px; border: 1px solid #d7dee9; border-radius: 6px; padding: 0 10px; background: #fff; }
</style>
