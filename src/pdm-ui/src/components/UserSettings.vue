<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import OrganizationSettings from './OrganizationSettings.vue'
import RolePermissionSettings from './RolePermissionSettings.vue'
import type { CreateRoleInput, OrganizationDirectory, OrganizationUnit, PdmUser, RolePermissionDirectory, SaveOrganizationUnitInput, SavePdmUserInput, SaveProjectOrganizationInput, ProjectOrganization } from '../types'

const props = defineProps<{
  directory: OrganizationDirectory
  roleDirectory: RolePermissionDirectory
  activeOrganizationId: string
  permissions: string[]
  currentUsername: string
  platformAdministrator?: boolean
  pending: boolean
  onSaveUser: (input: SavePdmUserInput, creating: boolean) => Promise<PdmUser>
  onResetPassword: (username: string) => Promise<PdmUser>
  onSaveRolePermissions: (role: string, permissions: string[]) => Promise<RolePermissionDirectory>
  onCreateRole: (input: CreateRoleInput) => Promise<RolePermissionDirectory>
  onDeleteRole: (role: string) => Promise<RolePermissionDirectory>
  onSaveOrganization: (input: SaveProjectOrganizationInput) => Promise<ProjectOrganization>
  onSaveUnit: (input: SaveOrganizationUnitInput) => Promise<OrganizationUnit>
  onUpdateMemberships: (username: string, unitIds: string[], primaryUnitId: string) => Promise<OrganizationDirectory>
  onUpdateManagers: (unitId: string, primaryManager: string, collaborativeManagers: string[]) => Promise<OrganizationDirectory>
}>()
const emit = defineEmits<{ updateActiveOrganizationId: [organizationId: string] }>()

type Section = 'users' | 'roles' | 'organization' | 'companies'
const activeSection = ref<Section>('users')
const query = ref('')
const userDialog = ref(false)
const membershipDialog = ref(false)
const editingUsername = ref('')
const selectedUser = ref<PdmUser | null>(null)
const membershipUnits = ref<string[]>([])
const primaryUnitId = ref('')
const userForm = reactive<SavePdmUserInput>({ username: '', displayName: '', role: 'Engineer', isActive: true, companyId: '', crossCompanyView: false, accessibleCompanyIds: [], password: '11111111' })

const canManageOrganization = computed(() => props.permissions.includes('settings.organization.manage'))
const canViewRoles = computed(() => props.permissions.includes('system.role.view'))
const canEditRoles = computed(() => props.permissions.includes('system.role.edit'))
const canManageUsers = computed(() => canManageOrganization.value && canEditRoles.value)
const activeCompany = computed(() => props.directory.organizations.find(item => item.id === props.activeOrganizationId))
const activeCompanyUnits = computed(() => props.directory.units.filter(unit => unit.organizationId === props.activeOrganizationId))
const activeCompanyUnitIds = computed(() => new Set(activeCompanyUnits.value.map(unit => unit.id)))
const platformManagedRoles = new Set(['platform_admin', 'developer'])
const scopedUsers = computed(() => props.directory.users.filter(user => userCompanyId(user) === props.activeOrganizationId && (props.platformAdministrator || !platformManagedRoles.has(user.role))))
const activeCompanies = computed(() => props.directory.organizations.filter(item => item.isActive !== false))
const assignableRoles = computed(() => props.platformAdministrator ? props.roleDirectory.roles : props.roleDirectory.roles.filter(item => !platformManagedRoles.has(item.role)))
const accessibleCompanyOptions = computed(() => activeCompanies.value.filter(item => item.id !== userForm.companyId))
const availableSections = computed<Section[]>(() => [
  'users',
  canViewRoles.value && 'roles',
  canManageOrganization.value && 'organization',
  canManageOrganization.value && 'companies',
].filter((item): item is Section => Boolean(item)))
const filteredUsers = computed(() => {
  const normalized = query.value.trim().toLocaleLowerCase('zh-CN')
  if (!normalized) return scopedUsers.value
  return scopedUsers.value.filter(user => `${user.username} ${user.displayName} ${roleName(user.role)} ${primaryUnitName(user.username)}`.toLocaleLowerCase('zh-CN').includes(normalized))
})
const activeUnits = computed(() => activeCompanyUnits.value.filter(unit => unit.isActive))

watch(availableSections, sections => { if (!sections.includes(activeSection.value)) activeSection.value = sections[0] ?? 'users' }, { immediate: true })
watch(() => userForm.companyId, companyId => {
  userForm.accessibleCompanyIds = userForm.accessibleCompanyIds.filter(id => id !== companyId)
})

function roleName(role: string) { return props.roleDirectory.roles.find(item => item.role === role)?.name ?? role }
function unitById(id?: string) { return props.directory.units.find(item => item.id === id) }
function companyName(id?: string) { return props.directory.organizations.find(item => item.id === id)?.name ?? '—' }
function unitPath(id: string) {
  const names: string[] = []
  let current = unitById(id)
  while (current) { names.unshift(current.name); current = unitById(current.parentUnitId) }
  return names.join(' / ')
}
function memberships(username: string) { return props.directory.memberships.filter(item => item.username.toLocaleLowerCase('zh-CN') === username.toLocaleLowerCase('zh-CN') && activeCompanyUnitIds.value.has(item.unitId)) }
function primaryMembership(username: string) { return memberships(username).find(item => item.isPrimary) }
function userCompanyId(user: PdmUser) { return user.companyId ?? unitById(primaryMembership(user.username)?.unitId)?.organizationId }
function primaryUnitName(username: string) { const item = primaryMembership(username); return item ? unitPath(item.unitId) : '未分配' }
function otherUnitNames(username: string) { return memberships(username).filter(item => !item.isPrimary).map(item => unitPath(item.unitId)).join('、') || '—' }

function openUser(user?: PdmUser) {
  editingUsername.value = user?.username ?? ''
  Object.assign(userForm, user
    ? { username: user.username, displayName: user.displayName, role: user.role, isActive: user.isActive, companyId: user.companyId ?? props.activeOrganizationId, crossCompanyView: Boolean(user.crossCompanyView), accessibleCompanyIds: [...(user.accessibleCompanyIds ?? [])], password: '' }
    : { username: '', displayName: '', role: 'Engineer', isActive: true, companyId: props.activeOrganizationId, crossCompanyView: false, accessibleCompanyIds: [], password: '11111111' })
  userDialog.value = true
}

async function saveUser() {
  if (!userForm.username.trim() || !userForm.displayName.trim()) return ElMessage.warning('请填写账号和姓名')
  if (!userForm.companyId) return ElMessage.warning('请选择主公司')
  if (!editingUsername.value && (!userForm.password || userForm.password.length < 8)) return ElMessage.warning('初始密码至少需要8位')
  try {
    const accessibleCompanyIds = props.platformAdministrator && userForm.crossCompanyView
      ? [...new Set(userForm.accessibleCompanyIds.filter(id => id !== userForm.companyId))]
      : []
    await props.onSaveUser({ ...userForm, crossCompanyView: accessibleCompanyIds.length > 0, accessibleCompanyIds }, !editingUsername.value)
    userDialog.value = false
    ElMessage.success(editingUsername.value ? '用户资料已保存' : '用户已创建')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '用户保存失败') }
}

function openMembership(user: PdmUser) {
  selectedUser.value = user
  const current = memberships(user.username)
  membershipUnits.value = current.map(item => item.unitId)
  primaryUnitId.value = current.find(item => item.isPrimary)?.unitId ?? membershipUnits.value[0] ?? ''
  membershipDialog.value = true
}

async function saveMemberships() {
  if (!selectedUser.value || !membershipUnits.value.length || !primaryUnitId.value || !membershipUnits.value.includes(primaryUnitId.value)) return ElMessage.warning('请选择所属部门和主部门')
  const companies = new Set(membershipUnits.value.map(id => unitById(id)?.organizationId).filter(Boolean))
  if (companies.size > 1) return ElMessage.warning('一个用户的组织关系必须在同一公司内')
  try {
    await props.onUpdateMemberships(selectedUser.value.username, membershipUnits.value, primaryUnitId.value)
    membershipDialog.value = false
    ElMessage.success('组织关系已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '组织关系保存失败') }
}

async function resetPassword(user: PdmUser) {
  try {
    await ElMessageBox.confirm(`确认将“${user.displayName}”的密码重置为 11111111？该账号当前登录状态将立即失效。`, '重置密码', { type: 'warning', confirmButtonText: '确认重置', cancelButtonText: '取消' })
    await props.onResetPassword(user.username)
    ElMessage.success('密码已重置为 11111111')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '密码重置失败')
  }
}
</script>

<template>
  <section class="pdm-project-manager pdm-user-settings" aria-label="用户设置">
    <nav class="user-settings-tabs" aria-label="用户设置功能">
      <button type="button" :class="{ 'is-active': activeSection === 'users' }" @click="activeSection='users'">用户</button>
      <button v-if="availableSections.includes('roles')" type="button" :class="{ 'is-active': activeSection === 'roles' }" @click="activeSection='roles'">角色权限</button>
      <button v-if="availableSections.includes('organization')" type="button" :class="{ 'is-active': activeSection === 'organization' }" @click="activeSection='organization'">组织关系</button>
      <button v-if="availableSections.includes('companies')" type="button" :class="{ 'is-active': activeSection === 'companies' }" @click="activeSection='companies'">公司管理</button>
    </nav>

    <section v-if="activeSection !== 'roles'" class="pdm-panel user-company-context" aria-label="当前公司主体">
      <div><strong>当前公司主体</strong><small>用户以主公司归属，组织关系和负责人按当前公司隔离维护</small></div>
      <select :value="activeOrganizationId" aria-label="选择当前公司主体" @change="emit('updateActiveOrganizationId', ($event.target as HTMLSelectElement).value)">
        <option v-for="company in directory.organizations.filter(item => item.isActive !== false)" :key="company.id" :value="company.id">{{ company.name }}</option>
      </select>
      <span>{{ activeCompany?.name || '请选择公司' }}</span>
    </section>

    <section v-if="activeSection === 'users'" class="pdm-panel user-directory" aria-label="用户列表">
      <header class="user-directory-heading">
        <div><h2>用户</h2><p>仅显示主公司为当前公司的账号；平台级账号可授权用户访问其他公司。</p></div>
        <div class="user-directory-actions"><input v-model="query" type="search" placeholder="搜索账号、姓名、角色或部门" aria-label="搜索用户"><button v-if="canManageUsers" type="button" class="pdm-primary-action" @click="openUser()">新建用户</button></div>
      </header>
      <div class="user-table-scroll">
        <table class="pdm-project-table">
          <thead><tr><th>账号</th><th>姓名</th><th>角色</th><th>公司</th><th>主部门</th><th>兼任部门</th><th>状态</th><th>操作</th></tr></thead>
          <tbody><tr v-for="user in filteredUsers" :key="user.username"><td><strong>{{ user.username }}</strong></td><td>{{ user.displayName }}</td><td>{{ roleName(user.role) }}</td><td :title="companyName(user.companyId ?? undefined)">{{ companyName(user.companyId ?? undefined) }}</td><td :title="primaryUnitName(user.username)">{{ primaryUnitName(user.username) }}</td><td :title="otherUnitNames(user.username)">{{ otherUnitNames(user.username) }}</td><td><span class="pdm-status" :class="user.isActive ? 'is-ok' : 'is-warn'">{{ user.isActive ? '启用' : '停用' }}</span></td><td><div class="user-row-actions"><button v-if="canManageUsers" type="button" class="pdm-text-action" @click="openUser(user)">编辑</button><button v-if="canManageOrganization" type="button" class="pdm-text-action" :disabled="!user.isActive || !activeUnits.length" @click="openMembership(user)">组织关系</button><button v-if="canManageUsers && user.username.toLocaleLowerCase('zh-CN') !== currentUsername.toLocaleLowerCase('zh-CN')" type="button" class="pdm-text-action" @click="resetPassword(user)">重置密码</button></div></td></tr></tbody>
        </table>
      </div>
      <p v-if="!filteredUsers.length" class="pdm-empty-info">没有符合条件的用户。</p>
      <p v-if="!canManageUsers" class="user-permission-note">当前账号可查看用户；新建、编辑及角色分配需要同时拥有组织维护和角色修改权限。</p>
    </section>

    <RolePermissionSettings v-else-if="activeSection === 'roles'" embedded :directory="roleDirectory" :can-edit="canEditRoles" :pending="pending" :on-save="onSaveRolePermissions" :on-create="onCreateRole" :on-delete="onDeleteRole" />
    <OrganizationSettings v-else-if="activeSection === 'organization'" embedded mode="organization" :active-company-id="activeOrganizationId" :directory="directory" :pending="pending" :on-save-organization="onSaveOrganization" :on-save-unit="onSaveUnit" :on-update-memberships="onUpdateMemberships" :on-update-managers="onUpdateManagers" @update:active-company-id="emit('updateActiveOrganizationId', $event)" />
    <OrganizationSettings v-else embedded mode="companies" :active-company-id="activeOrganizationId" :directory="directory" :pending="pending" :on-save-organization="onSaveOrganization" :on-save-unit="onSaveUnit" :on-update-memberships="onUpdateMemberships" :on-update-managers="onUpdateManagers" @update:active-company-id="emit('updateActiveOrganizationId', $event)" />

    <el-dialog v-model="userDialog" :title="editingUsername ? '编辑用户' : '新建用户'" width="560px">
      <div class="user-form">
        <label>账号<input v-model="userForm.username" :disabled="Boolean(editingUsername)" maxlength="100" autocomplete="off"></label>
        <label>姓名<input v-model="userForm.displayName" maxlength="100"></label>
        <label>系统角色<select v-model="userForm.role"><option v-for="role in assignableRoles" :key="role.role" :value="role.role">{{ role.name }}</option></select></label>
        <label>主公司<select v-model="userForm.companyId" :disabled="!platformAdministrator"><option v-for="company in activeCompanies" :key="company.id" :value="company.id">{{ company.name }}</option></select></label>
        <template v-if="platformAdministrator">
          <label class="user-checkbox"><input v-model="userForm.crossCompanyView" type="checkbox"> 允许跨公司访问</label>
          <label v-if="userForm.crossCompanyView">可访问公司<el-select v-model="userForm.accessibleCompanyIds" multiple filterable style="width:100%"><el-option v-for="company in accessibleCompanyOptions" :key="company.id" :label="company.name" :value="company.id" /></el-select></label>
          <small v-if="userForm.crossCompanyView">主公司始终可访问；此处只授权额外公司。平台管理员和开发者可直接访问全部启用公司。</small>
        </template>
        <label v-if="!editingUsername">初始密码<input v-model="userForm.password" type="password" minlength="8" autocomplete="new-password"></label>
        <label class="user-checkbox"><input v-model="userForm.isActive" type="checkbox"> 启用账号</label>
        <small v-if="!editingUsername">建议保留默认初始密码 11111111，并通知用户首次登录后立即修改。</small>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="userDialog=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveUser">{{ pending ? '保存中…' : '保存' }}</button></template>
    </el-dialog>

    <el-dialog v-model="membershipDialog" :title="`组织关系 · ${selectedUser?.displayName ?? ''}`" width="620px">
      <div class="user-form"><label>所属部门<el-select v-model="membershipUnits" multiple filterable style="width:100%"><el-option v-for="unit in activeUnits" :key="unit.id" :label="`${companyName(unit.organizationId)} / ${unitPath(unit.id)}`" :value="unit.id" /></el-select></label><label>主部门<el-select v-model="primaryUnitId" style="width:100%"><el-option v-for="unitId in membershipUnits" :key="unitId" :label="unitPath(unitId)" :value="unitId" /></el-select></label><small>可加入同一公司的多个部门，但必须指定一个主部门。</small></div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="membershipDialog=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveMemberships">保存</button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.pdm-user-settings { min-height: 0; }
.user-settings-tabs { display: flex; gap: 28px; margin-bottom: 12px; padding: 0 16px; border: 1px solid var(--pdm-border); border-radius: 7px; background: var(--pdm-surface); }
.user-settings-tabs button { min-width: 74px; min-height: 42px; border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; color: var(--pdm-text); cursor: pointer; }
.user-settings-tabs button:hover { color: var(--pdm-blue); }.user-settings-tabs button:focus-visible { outline: 2px solid rgba(59, 130, 246, .28); outline-offset: -3px; }
.user-settings-tabs button.is-active { border-bottom-color: var(--pdm-blue); background: transparent; color: var(--pdm-blue); font-weight: 500; }
.user-company-context { display: grid; grid-template-columns: minmax(210px, auto) minmax(260px, 390px) minmax(180px, 1fr); align-items: center; gap: 18px; margin-bottom: 12px; padding: 12px 16px; }.user-company-context > div { display: grid; gap: 3px; }.user-company-context small { color: var(--pdm-muted); }.user-company-context select { min-height: 36px; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 0 10px; background: var(--pdm-surface); color: var(--pdm-text); }.user-company-context > span { overflow: hidden; color: var(--pdm-muted); text-align: right; text-overflow: ellipsis; white-space: nowrap; }
.user-directory { min-height: 0; display: flex; flex: 1 1 auto; flex-direction: column; padding: 16px; overflow: hidden; }
.user-directory-heading { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin-bottom: 12px; }.user-directory-heading h2 { margin: 0 0 4px; font-size: 18px; }.user-directory-heading p { margin: 0; color: var(--pdm-muted); font-size: 11px; }
.user-directory-actions { display: flex; align-items: center; gap: 8px; }.user-directory-actions input { width: 280px; min-height: 36px; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 0 11px; background: var(--pdm-surface); color: var(--pdm-text); }
.user-table-scroll { width: 100%; min-height: 0; flex: 1 1 auto; overflow: auto; }.user-table-scroll table { width: 100%; min-width: 1020px; table-layout: fixed; }.user-table-scroll th:nth-child(1) { width: 120px; }.user-table-scroll th:nth-child(2) { width: 110px; }.user-table-scroll th:nth-child(3) { width: 110px; }.user-table-scroll th:nth-child(4) { width: 170px; }.user-table-scroll th:nth-child(5), .user-table-scroll th:nth-child(6) { width: 160px; }.user-table-scroll th:nth-child(7) { width: 75px; }.user-table-scroll th:nth-child(8) { width: 205px; }.user-table-scroll td { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.user-row-actions { display: flex; align-items: center; gap: 8px; white-space: nowrap; }.user-permission-note { margin: 12px 0 0; color: var(--pdm-muted); font-size: 11px; }
.user-form { display: grid; gap: 14px; }.user-form label { display: grid; gap: 7px; color: #435066; font-size: 13px; }.user-form input:not([type='checkbox']), .user-form select { min-height: 38px; border: 1px solid #d7dee9; border-radius: 6px; padding: 0 10px; background: #fff; }.user-form input:disabled { background: #f4f6f9; color: #778399; }.user-form small { color: var(--pdm-muted); line-height: 1.6; }.user-form .user-checkbox { display: flex; align-items: center; gap: 8px; }
@media (max-width: 900px) { .user-company-context { grid-template-columns: 1fr; }.user-company-context > span { text-align: left; }.user-directory-heading { align-items: stretch; flex-direction: column; }.user-directory-actions input { width: 100%; }.user-directory-actions { align-items: stretch; }.user-settings-tabs button { min-width: 0; flex: 1; } }
</style>
