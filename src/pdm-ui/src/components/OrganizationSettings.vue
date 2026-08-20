<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { computed, reactive, ref, watch } from 'vue'
import type { OrganizationDirectory, OrganizationUnit, PdmUser, ProjectOrganization, SaveOrganizationUnitInput, SaveProjectOrganizationInput } from '../types'

interface UnitTreeNode extends OrganizationUnit {
  label: string
  children: UnitTreeNode[]
}

const props = defineProps<{
  directory: OrganizationDirectory
  embedded?: boolean
  mode?: 'organization' | 'companies'
  pending: boolean
  onSaveOrganization: (input: SaveProjectOrganizationInput) => Promise<ProjectOrganization>
  onSaveUnit: (input: SaveOrganizationUnitInput) => Promise<OrganizationUnit>
  onUpdateMemberships: (username: string, unitIds: string[], primaryUnitId: string) => Promise<OrganizationDirectory>
  onUpdateManagers: (unitId: string, primaryManager: string, collaborativeManagers: string[]) => Promise<OrganizationDirectory>
}>()

const activeView = ref<'organization' | 'companies'>(props.mode ?? 'organization')
const companyDialog = ref(false)
const unitDialog = ref(false)
const membershipDialog = ref(false)
const managerDialog = ref(false)
const currentCompanyId = ref(localStorage.getItem('pdm_active_organization') ?? '')
const selectedUnitId = ref('')
const showingUnassigned = ref(false)
const selectedUser = ref<PdmUser | null>(null)
const selectedUnit = ref<OrganizationUnit | null>(null)
const companyForm = reactive<SaveProjectOrganizationInput>({ name: '', projectCompanyCode: '', modelCompanyCode: '', isActive: true })
const unitForm = reactive<SaveOrganizationUnitInput>({ organizationId: '', parentUnitId: undefined, code: '', name: '', kind: 'BusinessDivision', isActive: true, sortOrder: 0 })
const membershipUnits = ref<string[]>([])
const primaryUnit = ref('')
const primaryManager = ref('')
const collaborativeManagers = ref<string[]>([])

const sortedCompanies = computed(() => [...props.directory.organizations].sort((left, right) => Number(right.isActive !== false) - Number(left.isActive !== false) || left.name.localeCompare(right.name, 'zh-CN')))
const selectableCompanies = computed(() => sortedCompanies.value.filter(company => company.isActive !== false))
const currentCompany = computed(() => props.directory.organizations.find(company => company.id === currentCompanyId.value))
const companyUnits = computed(() => props.directory.units
  .filter(unit => unit.organizationId === currentCompanyId.value)
  .sort((left, right) => left.sortOrder - right.sortOrder || left.name.localeCompare(right.name, 'zh-CN')))
const currentUnit = computed(() => companyUnits.value.find(unit => unit.id === selectedUnitId.value))
const activeUsers = computed(() => props.directory.users.filter(user => user.isActive))
const companyUnitIds = computed(() => new Set(companyUnits.value.map(unit => unit.id)))
const unassignedUsers = computed(() => props.directory.users.filter(user => !props.directory.memberships.some(item => item.username === user.username)))
const treeData = computed<UnitTreeNode[]>(() => {
  const nodes = new Map(companyUnits.value.map(unit => [unit.id, { ...unit, label: unit.name, children: [] as UnitTreeNode[] }]))
  const roots: UnitTreeNode[] = []
  for (const node of nodes.values()) {
    const parent = node.parentUnitId ? nodes.get(node.parentUnitId) : undefined
    if (parent) parent.children.push(node)
    else roots.push(node)
  }
  const sortNodes = (items: UnitTreeNode[]) => {
    items.sort((left, right) => left.sortOrder - right.sortOrder || left.name.localeCompare(right.name, 'zh-CN'))
    items.forEach(item => sortNodes(item.children))
  }
  sortNodes(roots)
  return roots
})
const selectedMembers = computed(() => {
  if (!currentUnit.value) return []
  const usernames = new Set(props.directory.memberships.filter(item => isWithin(item.unitId, currentUnit.value!.id)).map(item => item.username))
  return props.directory.users.filter(user => usernames.has(user.username))
})
const currentManagers = computed(() => currentUnit.value ? props.directory.managers.find(item => item.unitId === currentUnit.value!.id) : undefined)
const unitOptions = computed(() => companyUnits.value.filter(unit => unit.isActive && unit.id !== unitForm.id && (!unitForm.id || !isWithin(unit.id, unitForm.id))))

watch(sortedCompanies, companies => {
  if (companies.some(company => company.id === currentCompanyId.value && company.isActive !== false)) return
  currentCompanyId.value = companies.find(company => company.isActive !== false)?.id ?? companies[0]?.id ?? ''
}, { immediate: true })

watch(() => props.mode, mode => { if (mode) activeView.value = mode }, { immediate: true })

watch(currentCompanyId, companyId => {
  if (companyId) localStorage.setItem('pdm_active_organization', companyId)
  showingUnassigned.value = false
  if (!companyUnits.value.some(unit => unit.id === selectedUnitId.value)) selectedUnitId.value = companyUnits.value[0]?.id ?? ''
})

watch(companyUnits, units => {
  if (!showingUnassigned.value && !units.some(unit => unit.id === selectedUnitId.value)) selectedUnitId.value = units[0]?.id ?? ''
}, { immediate: true })

function companyName(id: string) { return props.directory.organizations.find(item => item.id === id)?.name ?? '未知公司' }
function unitName(id: string) { return props.directory.units.find(item => item.id === id)?.name ?? '未设置' }
function userName(username?: string) { const user = props.directory.users.find(item => item.username === username); return user ? `${user.displayName}（${username}）` : username || '未设置' }
function kindName(kind: OrganizationUnit['kind']) { return ({ BusinessDivision: '部门', Department: '下级部门', Team: '团队' })[kind] }
function unitPath(unitId: string) {
  const names: string[] = []
  let current = props.directory.units.find(unit => unit.id === unitId)
  while (current) {
    names.unshift(current.name)
    current = current.parentUnitId ? props.directory.units.find(unit => unit.id === current!.parentUnitId) : undefined
  }
  return names.join(' / ')
}
function isWithin(unitId: string, ancestorId: string) {
  let current = props.directory.units.find(unit => unit.id === unitId)
  while (current) {
    if (current.id === ancestorId) return true
    current = current.parentUnitId ? props.directory.units.find(unit => unit.id === current!.parentUnitId) : undefined
  }
  return false
}
function userMemberships(username: string) { return props.directory.memberships.filter(item => item.username === username && companyUnitIds.value.has(item.unitId)) }
function primaryUnitName(username: string) { return unitName(userMemberships(username).find(item => item.isPrimary)?.unitId ?? '') }
function otherUnitNames(username: string) { return userMemberships(username).filter(item => !item.isPrimary).map(item => unitName(item.unitId)).join('、') || '—' }
function divisionCandidates(divisionId: string) {
  const usernames = new Set(props.directory.memberships.filter(item => isWithin(item.unitId, divisionId)).map(item => item.username))
  return activeUsers.value.filter(user => usernames.has(user.username))
}
function selectCompany(companyId: string) {
  currentCompanyId.value = companyId
  activeView.value = 'organization'
}
function selectTreeNode(node: UnitTreeNode) {
  showingUnassigned.value = false
  selectedUnitId.value = node.id
}
function showUnassigned() {
  selectedUnitId.value = ''
  showingUnassigned.value = true
}

function openCompany(item?: ProjectOrganization) {
  Object.assign(companyForm, item ? { id: item.id, name: item.name, projectCompanyCode: item.projectCompanyCode, modelCompanyCode: item.modelCompanyCode, isActive: item.isActive !== false } : { id: undefined, name: '', projectCompanyCode: '', modelCompanyCode: '', isActive: true })
  companyDialog.value = true
}
function openUnit(item?: OrganizationUnit, parent?: OrganizationUnit) {
  const organizationId = item?.organizationId ?? currentCompanyId.value
  Object.assign(unitForm, item ? { ...item, parentUnitId: item.parentUnitId } : {
    id: undefined,
    organizationId,
    parentUnitId: parent?.id,
    code: '',
    name: '',
    kind: parent ? (parent.kind === 'BusinessDivision' ? 'Department' : 'Team') : 'BusinessDivision',
    isActive: true,
    sortOrder: companyUnits.value.length,
  })
  unitDialog.value = true
}
function openMembership(user: PdmUser) {
  selectedUser.value = user
  const memberships = userMemberships(user.username)
  membershipUnits.value = memberships.map(item => item.unitId)
  if (!membershipUnits.value.length && currentUnit.value?.isActive) membershipUnits.value = [currentUnit.value.id]
  primaryUnit.value = memberships.find(item => item.isPrimary)?.unitId ?? membershipUnits.value[0] ?? ''
  membershipDialog.value = true
}
function openManagers(unit: OrganizationUnit) {
  selectedUnit.value = unit
  const managers = props.directory.managers.find(item => item.unitId === unit.id)
  primaryManager.value = managers?.primaryManager ?? ''
  collaborativeManagers.value = [...(managers?.collaborativeManagers ?? [])]
  managerDialog.value = true
}

async function saveCompany() {
  if (!companyForm.name.trim() || !companyForm.projectCompanyCode.trim() || !companyForm.modelCompanyCode.trim()) return ElMessage.warning('请完整填写公司名称和两类代码')
  try {
    const saved = await props.onSaveOrganization({ ...companyForm })
    companyDialog.value = false
    if (saved.isActive !== false) currentCompanyId.value = saved.id
    ElMessage.success('公司设置已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '公司保存失败') }
}
async function saveUnit() {
  if (!unitForm.organizationId || !unitForm.code.trim() || !unitForm.name.trim()) return ElMessage.warning('请完整填写组织资料')
  if (unitForm.kind !== 'BusinessDivision' && !unitForm.parentUnitId) return ElMessage.warning('部门或团队必须选择上级组织')
  try {
    const saved = await props.onSaveUnit({ ...unitForm, parentUnitId: unitForm.kind === 'BusinessDivision' ? undefined : unitForm.parentUnitId })
    unitDialog.value = false
    currentCompanyId.value = saved.organizationId
    selectedUnitId.value = saved.id
    showingUnassigned.value = false
    ElMessage.success('组织单元已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '组织保存失败') }
}
async function saveMemberships() {
  if (!selectedUser.value || !membershipUnits.value.length || !primaryUnit.value || !membershipUnits.value.includes(primaryUnit.value)) return ElMessage.warning('请选择所属组织和主组织')
  try {
    await props.onUpdateMemberships(selectedUser.value.username, membershipUnits.value, primaryUnit.value)
    membershipDialog.value = false
    ElMessage.success('人员归属已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '人员归属保存失败') }
}
async function saveManagers() {
  if (!selectedUnit.value || !primaryManager.value) return ElMessage.warning('请选择部门主负责人')
  try {
    await props.onUpdateManagers(selectedUnit.value.id, primaryManager.value, collaborativeManagers.value)
    managerDialog.value = false
    ElMessage.success('部门负责人已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '负责人保存失败') }
}
</script>

<template>
  <section class="pdm-project-manager pdm-organization-settings">
    <header v-if="!embedded" class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">系统管理 <span>/</span> 组织结构</div><h1>公司与组织结构</h1><p>多个公司分别维护独立组织树；可承接项目的部门直属公司，下级部门和团队最多10级。</p></div>
      <div class="pdm-page-actions"><button v-if="activeView === 'organization'" class="pdm-primary-action" type="button" :disabled="!currentCompany || currentCompany.isActive === false" @click="openUnit()">新建部门</button><button v-else class="pdm-primary-action" type="button" @click="openCompany()">新增公司</button></div>
    </header>
    <div v-else class="org-embedded-heading"><div><strong>{{ activeView === 'organization' ? '组织关系' : '公司管理' }}</strong><small>{{ activeView === 'organization' ? '维护部门、人员归属及部门负责人' : '维护公司及项目号、设备型号代码' }}</small></div><div class="pdm-page-actions"><button v-if="activeView === 'organization'" class="pdm-primary-action" type="button" :disabled="!currentCompany || currentCompany.isActive === false" @click="openUnit()">新建部门</button><button v-else class="pdm-primary-action" type="button" @click="openCompany()">新增公司</button></div></div>

    <nav v-if="!embedded" class="org-view-tabs" aria-label="公司与组织管理"><button type="button" :class="{ 'is-active': activeView === 'organization' }" @click="activeView='organization'">组织架构</button><button type="button" :class="{ 'is-active': activeView === 'companies' }" @click="activeView='companies'">公司管理</button></nav>

    <template v-if="activeView === 'organization'">
      <section class="pdm-panel org-company-switcher" aria-label="当前公司">
        <div><strong>当前公司</strong><small>切换后仅显示该公司的组织与人员</small></div>
        <select v-model="currentCompanyId" aria-label="选择当前公司"><option v-for="company in selectableCompanies" :key="company.id" :value="company.id">{{ company.name }}</option></select>
        <div v-if="currentCompany" class="org-company-codes"><span>项目号代码 <b>{{ currentCompany.projectCompanyCode }}</b></span><span>设备型号代码 <b>{{ currentCompany.modelCompanyCode }}</b></span></div>
      </section>

      <div class="org-workspace">
        <aside class="pdm-panel org-tree-panel" aria-label="公司组织树">
          <header class="pdm-panel-heading"><div><h2>{{ currentCompany?.name || '请选择公司' }}</h2><small>{{ companyUnits.length }} 个组织单元</small></div></header>
          <div class="org-company-root"><span class="org-node-kind is-company">公司</span><strong>{{ currentCompany?.name || '—' }}</strong></div>
          <el-tree v-if="treeData.length" class="org-tree" :data="treeData" node-key="id" default-expand-all highlight-current :expand-on-click-node="false" :current-node-key="selectedUnitId" aria-label="组织架构树" @node-click="selectTreeNode">
            <template #default="{ data }"><span class="org-tree-node"><span class="org-node-kind" :class="`is-${data.kind}`">{{ kindName(data.kind) }}</span><span>{{ data.name }}</span><em v-if="data.isActive === false">停用</em></span></template>
          </el-tree>
          <p v-else class="pdm-empty-info">当前公司尚未创建部门。</p>
          <button type="button" class="org-unassigned-button" :class="{ 'is-active': showingUnassigned }" @click="showUnassigned"><span>未分配人员</span><b>{{ unassignedUsers.length }}</b></button>
        </aside>

        <section class="pdm-panel org-detail-panel" aria-label="组织详情">
          <template v-if="showingUnassigned">
            <header class="org-detail-heading"><div><div class="org-detail-eyebrow">人员归属</div><h2>未分配人员</h2><p>仅显示尚未加入任何公司的账号，可分配到当前公司。</p></div></header>
            <div class="org-table-scroll"><table class="pdm-project-table"><thead><tr><th>账号</th><th>姓名</th><th>系统角色</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="user in unassignedUsers" :key="user.username"><td>{{ user.username }}</td><td>{{ user.displayName }}</td><td>{{ user.role }}</td><td><span class="pdm-status" :class="user.isActive ? 'is-ok' : 'is-warn'">{{ user.isActive ? '启用' : '停用' }}</span></td><td><button class="pdm-text-action" type="button" :disabled="!user.isActive || !companyUnits.length" @click="openMembership(user)">设置归属</button></td></tr></tbody></table></div>
            <p v-if="!unassignedUsers.length" class="pdm-empty-info">所有账号均已分配组织。</p>
          </template>

          <template v-else-if="currentUnit">
            <header class="org-detail-heading"><div><div class="org-detail-eyebrow">{{ unitPath(currentUnit.id) }}</div><h2>{{ currentUnit.name }}</h2><p>{{ kindName(currentUnit.kind) }} · {{ currentUnit.code }}</p></div><div class="pdm-page-actions"><button v-if="currentUnit.kind === 'BusinessDivision'" type="button" class="pdm-secondary-action" @click="openManagers(currentUnit)">设置负责人</button><button type="button" class="pdm-secondary-action" @click="openUnit(currentUnit)">编辑</button><button type="button" class="pdm-primary-action" :disabled="currentUnit.isActive === false" @click="openUnit(undefined, currentUnit)">新增下级</button></div></header>
            <dl class="org-unit-summary"><div><dt>所属公司</dt><dd>{{ companyName(currentUnit.organizationId) }}</dd></div><div><dt>组织类型</dt><dd>{{ kindName(currentUnit.kind) }}</dd></div><div><dt>上级组织</dt><dd>{{ currentUnit.parentUnitId ? unitName(currentUnit.parentUnitId) : '公司直属' }}</dd></div><div><dt>状态</dt><dd><span class="pdm-status" :class="currentUnit.isActive ? 'is-ok' : 'is-warn'">{{ currentUnit.isActive ? '启用' : '停用' }}</span></dd></div></dl>
            <section v-if="currentUnit.kind === 'BusinessDivision'" class="org-manager-summary"><div><small>主负责人</small><strong>{{ userName(currentManagers?.primaryManager) }}</strong></div><div><small>协同负责人</small><strong>{{ currentManagers?.collaborativeManagers.map(userName).join('、') || '未设置' }}</strong></div></section>
            <header class="org-members-heading"><div><h3>本组织及下级人员</h3><small>{{ selectedMembers.length }} 人；一个账号可有多个归属，但仅有一个主组织</small></div></header>
            <div class="org-table-scroll"><table class="pdm-project-table"><thead><tr><th>账号</th><th>姓名</th><th>系统角色</th><th>主组织</th><th>其他组织</th><th>操作</th></tr></thead><tbody><tr v-for="user in selectedMembers" :key="user.username"><td>{{ user.username }}</td><td>{{ user.displayName }}</td><td>{{ user.role }}</td><td>{{ primaryUnitName(user.username) }}</td><td>{{ otherUnitNames(user.username) }}</td><td><button class="pdm-text-action" type="button" :disabled="!user.isActive" @click="openMembership(user)">设置归属</button></td></tr></tbody></table></div>
            <p v-if="!selectedMembers.length" class="pdm-empty-info">该组织及下级尚未分配人员。可从“未分配人员”开始设置。</p>
          </template>

          <div v-else class="org-detail-empty"><strong>请选择组织单元</strong><span>从左侧组织树选择部门或团队。</span></div>
        </section>
      </div>
    </template>

    <section v-else class="pdm-panel org-section" aria-label="公司管理">
      <header class="pdm-panel-heading"><div><h2>公司管理</h2><small>公司代码参与项目号和设备型号生成；已有业务数据的公司仅允许停用。</small></div></header>
      <div class="org-table-scroll"><table class="pdm-project-table"><thead><tr><th>公司</th><th>项目号代码</th><th>设备型号代码</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="company in sortedCompanies" :key="company.id"><td>{{ company.name }}</td><td>{{ company.projectCompanyCode }}</td><td>{{ company.modelCompanyCode }}</td><td><span class="pdm-status" :class="company.isActive === false ? 'is-warn' : 'is-ok'">{{ company.isActive === false ? '停用' : '启用' }}</span></td><td><button v-if="!embedded" class="pdm-text-action" type="button" @click="selectCompany(company.id)">组织架构</button><button class="pdm-text-action" type="button" @click="openCompany(company)">编辑</button></td></tr></tbody></table></div>
    </section>

    <el-dialog v-model="companyDialog" :title="companyForm.id ? '编辑公司' : '新增公司'" width="520px"><div class="org-form"><label>公司名称<input v-model="companyForm.name" maxlength="200"></label><label>项目号公司代码<input v-model="companyForm.projectCompanyCode" maxlength="1" placeholder="如 7"></label><label>设备型号公司代码<input v-model="companyForm.modelCompanyCode" maxlength="8" placeholder="如 AK"></label><label><input v-model="companyForm.isActive" type="checkbox"> 启用</label></div><template #footer><button class="pdm-secondary-action" type="button" @click="companyDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveCompany">保存</button></template></el-dialog>
    <el-dialog v-model="unitDialog" :title="unitForm.id ? '编辑组织' : unitForm.parentUnitId ? '新增下级组织' : '新建部门'" width="560px"><div class="org-form"><label>所属公司<input :value="companyName(unitForm.organizationId)" disabled></label><label>类型<select v-model="unitForm.kind" :disabled="!unitForm.parentUnitId"><option v-if="!unitForm.parentUnitId" value="BusinessDivision">部门（公司直属）</option><option value="Department">下级部门</option><option value="Team">团队</option></select></label><label v-if="unitForm.kind !== 'BusinessDivision'">上级组织<select v-model="unitForm.parentUnitId"><option value="" disabled>请选择</option><option v-for="unit in unitOptions" :key="unit.id" :value="unit.id">{{ unitPath(unit.id) }}</option></select></label><label>组织编码<input v-model="unitForm.code" maxlength="40"></label><label>组织名称<input v-model="unitForm.name" maxlength="160"></label><label>排序<input v-model.number="unitForm.sortOrder" type="number"></label><label><input v-model="unitForm.isActive" type="checkbox"> 启用</label><small class="org-form-note">可承接项目的部门必须直属公司；下级部门和团队可继续分层，最多10级。</small></div><template #footer><button class="pdm-secondary-action" type="button" @click="unitDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveUnit">保存</button></template></el-dialog>
    <el-dialog v-model="membershipDialog" :title="`人员归属 · ${selectedUser?.displayName ?? ''}`" width="600px"><div class="org-form"><label>所属公司<input :value="currentCompany?.name || ''" disabled></label><label>所属组织<el-select v-model="membershipUnits" multiple filterable style="width:100%"><el-option v-for="unit in companyUnits.filter(item => item.isActive)" :key="unit.id" :label="unitPath(unit.id)" :value="unit.id" /></el-select></label><label>主组织<el-select v-model="primaryUnit" style="width:100%"><el-option v-for="unitId in membershipUnits" :key="unitId" :label="unitPath(unitId)" :value="unitId" /></el-select></label><small class="org-form-note">人员只能归属同一公司；可加入多个组织，但必须指定一个主组织。</small></div><template #footer><button class="pdm-secondary-action" type="button" @click="membershipDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveMemberships">保存</button></template></el-dialog>
    <el-dialog v-model="managerDialog" :title="`部门负责人 · ${selectedUnit?.name ?? ''}`" width="600px"><div class="org-form"><label>主负责人<el-select v-model="primaryManager" filterable style="width:100%"><el-option v-for="user in divisionCandidates(selectedUnit?.id ?? '')" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label><label>协同负责人<el-select v-model="collaborativeManagers" multiple filterable style="width:100%"><el-option v-for="user in divisionCandidates(selectedUnit?.id ?? '').filter(item => item.username !== primaryManager)" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label><small class="org-form-note">部门负责人必须已经归属该部门或其下级组织。</small></div><template #footer><button class="pdm-secondary-action" type="button" @click="managerDialog=false">取消</button><button class="pdm-primary-action" type="button" :disabled="pending" @click="saveManagers">保存</button></template></el-dialog>
  </section>
</template>

<style scoped>
.pdm-organization-settings { min-height: 0; }
.org-embedded-heading { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 10px; }.org-embedded-heading > div:first-child { display: grid; gap: 3px; }.org-embedded-heading strong { font-size: 15px; }.org-embedded-heading small { color: var(--pdm-muted); }
.org-view-tabs { display: inline-flex; gap: 3px; margin-bottom: 12px; padding: 3px; border: 1px solid var(--pdm-border); border-radius: 7px; background: var(--pdm-surface-muted); }
.org-view-tabs button { min-width: 88px; min-height: 30px; border: 0; border-radius: 5px; background: transparent; color: var(--pdm-muted); cursor: pointer; }
.org-view-tabs button.is-active { background: var(--pdm-surface); color: var(--pdm-blue); box-shadow: 0 1px 4px rgba(31, 52, 82, .12); }
.org-company-switcher { display: grid; grid-template-columns: minmax(190px, auto) minmax(260px, 390px) minmax(260px, 1fr); align-items: center; gap: 18px; margin-bottom: 12px; padding: 13px 16px; }
.org-company-switcher > div:first-child { display: grid; gap: 3px; }.org-company-switcher small { color: var(--pdm-muted); }
.org-company-switcher select { min-height: 36px; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 0 10px; background: var(--pdm-surface); color: var(--pdm-text); }
.org-company-codes { display: flex; justify-content: flex-end; gap: 22px; color: var(--pdm-muted); font-size: 11px; }.org-company-codes b { margin-left: 5px; color: var(--pdm-text); font-size: 13px; }
.org-workspace { min-height: 480px; display: grid; grid-template-columns: 300px minmax(0, 1fr); gap: 12px; }
.org-tree-panel, .org-detail-panel { min-width: 0; padding: 15px; }
.org-tree-panel { display: flex; flex-direction: column; overflow: hidden; }.org-tree-panel .pdm-panel-heading { margin-bottom: 10px; }.org-tree-panel .pdm-panel-heading small { color: var(--pdm-muted); }
.org-company-root { display: flex; align-items: center; gap: 8px; padding: 10px; border: 1px solid var(--pdm-border); border-radius: 6px; background: var(--pdm-surface-muted); font-size: 12px; }
.org-tree { flex: 1 1 auto; min-height: 180px; margin: 7px 0; overflow: auto; background: transparent; --el-tree-node-hover-bg-color: var(--pdm-blue-soft); }
.org-tree-node { min-width: 0; display: inline-flex; align-items: center; gap: 7px; }.org-tree-node > span:nth-child(2) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }.org-tree-node em { color: var(--pdm-danger); font-size: 10px; font-style: normal; }
.org-node-kind { flex: 0 0 auto; min-width: 35px; padding: 2px 5px; border-radius: 4px; background: #eef3f9; color: #52647c; font-size: 9px; text-align: center; }.org-node-kind.is-company { background: #e7f1ff; color: var(--pdm-blue); }.org-node-kind.is-BusinessDivision { background: #e6f7f2; color: #11866d; }.org-node-kind.is-Team { background: #fff3dc; color: #aa6a00; }
.org-unassigned-button { min-height: 38px; display: flex; align-items: center; justify-content: space-between; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 0 11px; background: var(--pdm-surface); color: var(--pdm-text); cursor: pointer; }.org-unassigned-button:hover, .org-unassigned-button.is-active { border-color: var(--pdm-blue); background: var(--pdm-blue-soft); color: var(--pdm-blue); }.org-unassigned-button b { min-width: 22px; padding: 2px 6px; border-radius: 10px; background: var(--pdm-surface-muted); }
.org-detail-panel { overflow: hidden; }.org-detail-heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; padding-bottom: 14px; border-bottom: 1px solid var(--pdm-border); }.org-detail-heading h2 { margin: 3px 0; font-size: 18px; }.org-detail-heading p { margin: 0; color: var(--pdm-muted); font-size: 11px; }.org-detail-eyebrow { max-width: 620px; overflow: hidden; color: var(--pdm-blue); font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }
.org-unit-summary { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; margin: 14px 0; }.org-unit-summary div, .org-manager-summary div { min-width: 0; padding: 10px; border-radius: 6px; background: var(--pdm-surface-muted); }.org-unit-summary dt, .org-manager-summary small { display: block; margin-bottom: 4px; color: var(--pdm-muted); font-size: 10px; }.org-unit-summary dd { margin: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; }
.org-manager-summary { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin-bottom: 14px; }.org-manager-summary strong { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; font-weight: 500; }
.org-members-heading { display: flex; justify-content: space-between; margin: 4px 0 8px; }.org-members-heading h3 { margin: 0 0 3px; font-size: 13px; }.org-members-heading small { color: var(--pdm-muted); }
.org-table-scroll { width: 100%; overflow: auto; }.org-table-scroll table { width: 100%; min-width: 680px; }
.org-detail-empty { min-height: 360px; display: grid; place-content: center; gap: 6px; color: var(--pdm-muted); text-align: center; }.org-detail-empty strong { color: var(--pdm-text); }
.org-section { overflow: hidden; padding: 15px; }.org-section .pdm-panel-heading { margin-bottom: 10px; }.org-section .pdm-panel-heading small { color: var(--pdm-muted); }
.org-form { display: grid; gap: 14px; }.org-form label { display: grid; gap: 7px; color: #435066; font-size: 13px; }.org-form input:not([type='checkbox']), .org-form select { min-height: 38px; border: 1px solid #d7dee9; border-radius: 6px; padding: 0 10px; background: #fff; }.org-form input:disabled, .org-form select:disabled { background: #f4f6f9; color: #778399; }.org-form-note { color: var(--pdm-muted); line-height: 1.6; }
@media (max-width: 1050px) { .org-company-switcher { grid-template-columns: minmax(180px, 1fr) minmax(240px, 1fr); }.org-company-codes { grid-column: 1 / -1; justify-content: flex-start; }.org-workspace { grid-template-columns: 260px minmax(0, 1fr); }.org-unit-summary { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 760px) { .org-company-switcher, .org-workspace { grid-template-columns: 1fr; }.org-workspace { min-height: 0; }.org-tree-panel { min-height: 300px; }.org-detail-heading { flex-direction: column; }.org-manager-summary, .org-unit-summary { grid-template-columns: 1fr; } }
</style>
