<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { CopyPlus, LockKeyhole, Trash2 } from '@lucide/vue'
import type { CreateRoleInput, PermissionDefinition, RolePermissionDirectory, RolePermissionSettings } from '../types'

const props = defineProps<{
  directory: RolePermissionDirectory
  embedded?: boolean
  canEdit: boolean
  pending: boolean
  onSave: (role: string, permissions: string[]) => Promise<RolePermissionDirectory>
  onCreate: (input: CreateRoleInput) => Promise<RolePermissionDirectory>
  onDelete: (role: string) => Promise<RolePermissionDirectory>
}>()

const selectedRole = ref('')
const draft = ref<string[]>([])
const activeModule = ref('')
const permissionDialog = ref(false)
const createDialog = ref(false)
const createForm = reactive<CreateRoleInput>({ name: '', description: '', sourceRoleCode: '' })

const roles = computed(() => props.directory.roles)
const selected = computed(() => roles.value.find(role => role.role === selectedRole.value) ?? null)
const groups = computed(() => {
  const result = new Map<string, PermissionDefinition[]>()
  for (const permission of props.directory.permissions) {
    const items = result.get(permission.module) ?? []
    items.push(permission)
    result.set(permission.module, items)
  }
  return [...result.entries()].map(([name, permissions]) => ({ name, permissions }))
})
const moduleCount = computed(() => groups.value.length)
const permissionCount = computed(() => props.directory.permissions.length)
const editable = computed(() => props.canEdit && !selected.value?.isSystemAdministrator)
const selectedGroup = computed(() => groups.value.find(group => group.name === activeModule.value) ?? groups.value[0] ?? null)
const draftChanged = computed(() => {
  if (!selected.value || draft.value.length !== selected.value.permissions.length) return Boolean(selected.value)
  const original = new Set(selected.value.permissions)
  return draft.value.some(code => !original.has(code))
})
const copySources = computed(() => roles.value.filter(role => !role.isSystemAdministrator))
const deletable = computed(() => props.canEdit && selected.value && !selected.value.isSystem && selected.value.userCount === 0)
const permissionCategories = [
  { key: 'view', label: '查看' },
  { key: 'create', label: '新增' },
  { key: 'edit', label: '编辑' },
  { key: 'delete', label: '删除/停用' },
  { key: 'other', label: '其他操作' },
] as const
type PermissionCategory = typeof permissionCategories[number]['key']

function selectRole(role: RolePermissionSettings) {
  selectedRole.value = role.role
  draft.value = [...role.permissions]
}

function openPermissions(role: RolePermissionSettings) {
  selectRole(role)
  activeModule.value = groups.value[0]?.name ?? ''
  permissionDialog.value = true
}

async function switchRole(event: Event) {
  const control = event.target as HTMLSelectElement
  const role = roles.value.find(item => item.role === control.value)
  if (!role || role.role === selectedRole.value) return
  if (draftChanged.value) {
    try {
      await ElMessageBox.confirm('切换角色将放弃当前未保存的权限调整，是否继续？', '切换角色', { type: 'warning', confirmButtonText: '继续切换', cancelButtonText: '取消' })
    } catch {
      control.value = selectedRole.value
      return
    }
  }
  selectRole(role)
}

function hasPermission(code: string) {
  return draft.value.includes(code)
}

function setPermission(code: string, checked: boolean) {
  const next = new Set(draft.value)
  checked ? next.add(code) : next.delete(code)
  if (checked && (code.startsWith('project.') || ['document.edit', 'bom.edit', 'release.manage', 'approval.decide'].includes(code))) next.add('project.view')
  if (checked && ['document.edit', 'bom.edit', 'release.manage', 'approval.decide'].includes(code)) next.add('project.content.view')
  if (checked && code === 'system.role.edit') next.add('system.role.view')
  if (checked && code === 'material.manage') next.add('material.view')
  if (!checked && code === 'project.view') [...next].filter(item => item.startsWith('project.') || ['document.edit', 'bom.edit', 'release.manage', 'approval.decide'].includes(item)).forEach(item => next.delete(item))
  if (!checked && code === 'project.content.view') ['document.edit', 'bom.edit', 'release.manage', 'approval.decide'].forEach(item => next.delete(item))
  if (!checked && code === 'system.role.view') next.delete('system.role.edit')
  if (!checked && code === 'material.view') next.delete('material.manage')
  draft.value = [...next]
}

function setGroup(permissions: PermissionDefinition[], checked: boolean) {
  permissions.forEach(permission => setPermission(permission.code, checked))
}

function permissionCategory(permission: PermissionDefinition): PermissionCategory {
  const label = permission.name
  if (/查看|浏览|下载|查询/.test(label) || permission.code.endsWith('.view')) return 'view'
  if (/发布|批准|审批|审核|分配|同步|释放|导入|导出|恢复|催办|决定/.test(label) || /\.(publish|approve|decide|assign|sync|release|import|export)$/.test(permission.code)) return 'other'
  if (/新增|创建|发起|上传|提交/.test(label) || permission.code.endsWith('.create')) return 'create'
  if (/删除|停用|作废|回收/.test(label) || /\.(delete|recycle|archive|disable)$/.test(permission.code)) return 'delete'
  if (/编辑|维护|配置|登记|签出|存档/.test(label) || /\.(edit|manage|configure)$/.test(permission.code)) return 'edit'
  return 'other'
}

function categoryPermissions(category: PermissionCategory) {
  return selectedGroup.value?.permissions.filter(permission => permissionCategory(permission) === category) ?? []
}

async function save() {
  if (!selected.value || !editable.value) return
  try {
    const directory = await props.onSave(selected.value.role, draft.value)
    const saved = directory.roles.find(role => role.role === selected.value?.role)
    if (saved) selectRole(saved)
    ElMessage.success('角色权限已保存并立即生效')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '角色权限保存失败')
  }
}

function openCreate() {
  const source = selected.value && !selected.value.isSystemAdministrator ? selected.value : copySources.value[0]
  Object.assign(createForm, { name: '', description: '', sourceRoleCode: source?.role ?? '' })
  createDialog.value = true
}

async function createRole() {
  if (!createForm.name.trim() || !createForm.sourceRoleCode) return ElMessage.warning('请填写角色名称并选择复制来源')
  try {
    const directory = await props.onCreate({ ...createForm, name: createForm.name.trim(), description: createForm.description.trim() })
    const created = directory.roles.find(role => !role.isSystem && role.name === createForm.name.trim())
    if (created) selectRole(created)
    createDialog.value = false
    ElMessage.success('角色已创建，可继续调整权限')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '角色创建失败') }
}

async function deleteRole() {
  if (!selected.value || selected.value.isSystem) return
  try {
    await ElMessageBox.confirm(`确认删除自定义角色“${selected.value.name}”？删除后无法恢复。`, '删除角色', { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' })
    const deletedRole = selected.value.role
    const directory = await props.onDelete(deletedRole)
    const next = directory.roles[0]
    if (next) selectRole(next)
    ElMessage.success('角色已删除')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '角色删除失败')
  }
}

watch(roles, value => {
  const current = value.find(role => role.role === selectedRole.value) ?? value[0]
  if (current) selectRole(current)
  else { selectedRole.value = ''; draft.value = [] }
}, { immediate: true })
</script>

<template>
  <section class="pdm-project-manager" aria-label="角色权限设置">
    <header v-if="!embedded" class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">系统管理 <span>/</span> 角色权限</div><h1>角色权限设置</h1><p>功能权限决定账号可以执行的操作，项目岗位与组织分配决定可操作的数据范围。</p></div>
    </header>
    <section class="pdm-panel pdm-role-directory" aria-label="角色列表">
      <div v-if="canEdit" class="pdm-role-toolbar"><button type="button" class="pdm-primary-action" aria-label="复制角色新建" @click="openCreate"><CopyPlus :size="15" />复制角色新建</button></div>
      <el-table :data="roles" stripe height="100%" class="pdm-role-table" empty-text="暂无角色">
        <el-table-column prop="name" label="角色" min-width="150" />
        <el-table-column prop="role" label="编码" min-width="190" />
        <el-table-column prop="description" label="说明" min-width="300" show-overflow-tooltip />
        <el-table-column prop="userCount" label="用户数" width="90" />
        <el-table-column label="类型" width="90"><template #default="{ row }">{{ row.isSystem ? '系统' : '自定义' }}</template></el-table-column>
        <el-table-column label="操作" width="120" fixed="right"><template #default="{ row }"><div class="pdm-role-row-actions"><button type="button" class="pdm-text-action" @click="openPermissions(row)">权限设置</button></div></template></el-table-column>
      </el-table>
    </section>

    <el-dialog v-model="permissionDialog" title="角色权限" width="min(1360px, 96vw)" class="pdm-role-permission-dialog" top="2vh">
      <template v-if="selected">
        <div class="pdm-role-overview">
          <label><span>切换角色</span><select :value="selectedRole" @change="switchRole"><option v-for="role in roles" :key="role.role" :value="role.role">{{ role.name }}</option></select></label>
          <label><span>角色名称</span><input :value="selected.name" readonly></label>
          <label><span>说明</span><input :value="selected.description || '—'" readonly></label>
          <label><span>角色信息</span><input :value="`${selected.isSystem ? '系统角色' : '自定义角色'} · ${selected.userCount} 个用户 · ${selected.role}`" readonly></label>
        </div>
        <p class="pdm-role-directory-note">权限模块由系统功能目录自动生成；当前共 {{ moduleCount }} 个模块、{{ permissionCount }} 项权限，后续新增模块会自动加入。</p>
        <div class="pdm-permission-layout">
          <nav class="pdm-permission-sidebar" aria-label="权限模块">
            <strong>权限分类</strong>
            <button v-for="group in groups" :key="group.name" type="button" :class="{ 'is-active': selectedGroup?.name === group.name }" @click="activeModule = group.name">
              <span>{{ group.name }}</span><small>{{ group.permissions.length }}</small>
            </button>
          </nav>
          <section v-if="selectedGroup" class="pdm-permission-matrix" :aria-label="`${selectedGroup.name}权限`">
            <header><h3>{{ selectedGroup.name }}权限</h3><div v-if="editable"><button type="button" @click="setGroup(selectedGroup.permissions, true)">全选本组</button><button type="button" @click="setGroup(selectedGroup.permissions, false)">清空</button></div></header>
            <div class="pdm-permission-table-wrap">
              <table>
                <thead><tr><th class="pdm-permission-category-heading">分类</th><th v-for="category in permissionCategories" :key="category.key">{{ category.label }}</th></tr></thead>
                <tbody><tr>
                  <th class="pdm-permission-category-cell" scope="row">{{ selectedGroup.name }}</th>
                  <td v-for="category in permissionCategories" :key="category.key">
                    <label v-for="permission in categoryPermissions(category.key)" :key="permission.code" class="pdm-permission-cell" :class="{ 'is-checked': hasPermission(permission.code) }">
                      <input type="checkbox" :checked="hasPermission(permission.code)" :disabled="!editable" @change="setPermission(permission.code, ($event.target as HTMLInputElement).checked)">
                      <span><strong>{{ permission.name }}<em v-if="permission.sensitive">敏感</em></strong><small>{{ permission.description || permission.code }}</small><code>{{ permission.code }}</code></span>
                    </label>
                    <span v-if="!categoryPermissions(category.key).length" class="pdm-permission-empty">—</span>
                  </td>
                </tr></tbody>
              </table>
            </div>
          </section>
          <p v-else class="pdm-empty-info">当前没有可配置权限。</p>
        </div>
      </template>
      <template #footer><button v-if="selected && !selected.isSystem && canEdit" type="button" class="pdm-danger-action" :disabled="pending || !deletable" :title="selected.userCount ? '请先调整引用该角色的用户' : '删除自定义角色'" @click="deleteRole"><Trash2 :size="15" />删除角色</button><span class="pdm-role-dialog-footer-spacer"></span><span v-if="selected && !editable" class="pdm-role-lock"><LockKeyhole :size="15" />{{ selected.isSystemAdministrator ? '系统管理员固定拥有全部权限' : '当前账号只有查看权限' }}</span><button type="button" class="pdm-secondary-action" @click="permissionDialog=false">取消</button><button v-if="editable" type="button" class="pdm-primary-action" :disabled="pending" @click="save">{{ pending ? '保存中…' : '保存并立即生效' }}</button></template>
    </el-dialog>

    <el-dialog v-model="createDialog" title="复制角色新建" width="520px">
      <div class="pdm-role-create-form"><label>角色名称<input v-model="createForm.name" maxlength="80" placeholder="请输入角色名称"></label><label>复制来源<select v-model="createForm.sourceRoleCode"><option v-for="role in copySources" :key="role.role" :value="role.role">{{ role.name }}</option></select></label><label>角色说明<textarea v-model="createForm.description" maxlength="300" rows="3" placeholder="说明该角色的职责范围"></textarea></label><small>新角色会复制来源角色的基础业务身份和权限，创建后可单独调整；系统管理员不能作为复制来源。</small></div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="createDialog=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="createRole">{{ pending ? '创建中…' : '创建角色' }}</button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.pdm-role-directory { min-height: 0; display: flex; flex: 1 1 auto; flex-direction: column; padding: 16px; overflow: hidden; }
.pdm-role-toolbar { display: flex; flex: 0 0 auto; margin-bottom: 14px; }
.pdm-role-toolbar .pdm-primary-action { gap: 6px; }
.pdm-role-table { width: 100%; min-height: 0; flex: 1 1 auto; }
.pdm-role-table :deep(th.el-table__cell) { background: var(--pdm-surface-soft); color: var(--pdm-muted); font-weight: 600; }
.pdm-role-table :deep(.el-table__cell) { padding: 10px 0; }
.pdm-role-row-actions { display: flex; align-items: center; gap: 12px; white-space: nowrap; }
.pdm-role-overview { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px 20px; padding: 2px 0 16px; }
.pdm-role-overview label { display: grid; gap: 6px; color: var(--pdm-text-soft); font-size: 12px; }.pdm-role-overview input,.pdm-role-overview select { box-sizing: border-box; width: 100%; height: 38px; border: 1px solid #d7dee9; border-radius: 5px; padding: 0 11px; background: #fff; color: var(--pdm-text); font: inherit; }.pdm-role-overview input[readonly] { background: #fbfcfe; color: var(--pdm-text-soft); }
.pdm-role-directory-note { margin: 0; padding: 10px 0; border-top: 1px solid var(--pdm-border); color: var(--pdm-muted); font-size: 12px; }
.pdm-permission-layout { display: grid; min-height: 310px; grid-template-columns: 176px minmax(0, 1fr); border-top: 1px solid var(--pdm-border); }
.pdm-permission-sidebar { border-right: 1px solid var(--pdm-border); background: #f8fafc; }.pdm-permission-sidebar > strong { display: block; padding: 13px 14px 9px; color: var(--pdm-muted); font-size: 12px; font-weight: 600; }.pdm-permission-sidebar button { display: flex; width: 100%; min-height: 40px; align-items: center; justify-content: space-between; gap: 8px; border: 0; border-left: 3px solid transparent; padding: 8px 12px 8px 11px; background: transparent; color: var(--pdm-text-soft); cursor: pointer; font-size: 12px; text-align: left; }.pdm-permission-sidebar button:hover { background: #eef4fa; }.pdm-permission-sidebar button.is-active { border-left-color: var(--pdm-theme-accent); background: #eaf6f4; color: var(--pdm-theme-accent); font-weight: 600; }.pdm-permission-sidebar button small { min-width: 22px; border-radius: 10px; padding: 1px 6px; background: #e7edf4; color: var(--pdm-muted); font-size: 10px; text-align: center; }.pdm-permission-sidebar button.is-active small { background: #d4eee9; color: var(--pdm-green); }
.pdm-permission-matrix { min-width: 0; min-height: 230px; padding-left: 14px; }.pdm-permission-matrix > header { display: flex; align-items: center; justify-content: space-between; padding: 12px 10px 9px 0; }.pdm-permission-matrix h3 { margin: 0; font-size: 14px; }.pdm-permission-matrix header div { display: flex; gap: 18px; }.pdm-permission-matrix header button { border: 0; padding: 2px 0; background: transparent; color: var(--pdm-blue); cursor: pointer; font-size: 12px; }
.pdm-permission-table-wrap { overflow: auto; border: 1px solid var(--pdm-border); }.pdm-permission-table-wrap table { width: 100%; min-width: 920px; border-collapse: collapse; table-layout: fixed; }.pdm-permission-table-wrap th { height: 38px; border-right: 1px solid var(--pdm-border); background: #f4f7fb; color: var(--pdm-text-soft); font-size: 12px; font-weight: 600; text-align: left; }.pdm-permission-table-wrap th,.pdm-permission-table-wrap td { padding: 8px 10px; vertical-align: top; }.pdm-permission-table-wrap td,.pdm-permission-category-cell { height: 150px; border-top: 1px solid var(--pdm-border); border-right: 1px solid var(--pdm-border); }.pdm-permission-table-wrap th:last-child,.pdm-permission-table-wrap td:last-child { border-right: 0; }.pdm-permission-category-heading,.pdm-permission-category-cell { width: 92px; }.pdm-permission-category-cell { background: #fff; color: var(--pdm-text-soft); font-size: 12px; font-weight: 500; text-align: left; }
.pdm-permission-cell { display: flex; gap: 8px; margin: -1px -2px 7px; border: 1px solid transparent; border-radius: 5px; padding: 8px; background: #fff; cursor: pointer; }.pdm-permission-cell:hover { border-color: #cbd8e8; background: #f8fbff; }.pdm-permission-cell.is-checked { border-color: #a9c8ee; background: #f3f8ff; }.pdm-permission-cell > input { margin: 3px 0 0; }.pdm-permission-cell > span { display: grid; min-width: 0; gap: 3px; }.pdm-permission-cell strong { color: var(--pdm-text); font-size: 12px; line-height: 1.4; }.pdm-permission-cell strong em { margin-left: 5px; border-radius: 3px; padding: 1px 4px; background: #fff1e8; color: var(--pdm-orange); font-size: 10px; font-style: normal; font-weight: 500; }.pdm-permission-cell small { color: var(--pdm-muted); font-size: 11px; line-height: 1.4; }.pdm-permission-cell code { overflow: hidden; color: var(--pdm-text-soft); font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }.pdm-permission-empty { color: var(--pdm-muted); }
.pdm-role-permission-dialog :deep(.el-dialog__body) { max-height: 74vh; overflow: auto; padding-top: 12px; }
.pdm-role-permission-dialog :deep(.el-dialog__footer) { display: flex; align-items: center; gap: 8px; }
.pdm-role-dialog-footer-spacer { flex: 1; }
.pdm-danger-action { display: inline-flex; min-height: 34px; align-items: center; gap: 5px; border: 1px solid #f2c5c0; border-radius: 5px; padding: 0 11px; background: #fff7f6; color: var(--pdm-danger); cursor: pointer; }.pdm-danger-action:disabled { cursor: not-allowed; opacity: .45; }
.pdm-role-create-form { display: grid; gap: 14px; }.pdm-role-create-form label { display: grid; gap: 7px; color: var(--pdm-text-soft); font-size: 12px; }.pdm-role-create-form input, .pdm-role-create-form select, .pdm-role-create-form textarea { border: 1px solid #d7dee9; border-radius: 6px; padding: 9px 10px; background: #fff; color: var(--pdm-text); }.pdm-role-create-form small { color: var(--pdm-muted); line-height: 1.4; }
@media (max-width: 900px) { .pdm-role-directory { padding: 12px; }.pdm-role-overview { grid-template-columns: 1fr; }.pdm-role-permission-dialog :deep(.el-dialog) { width: calc(100vw - 24px) !important; }.pdm-permission-layout { grid-template-columns: 132px minmax(0, 1fr); }.pdm-permission-sidebar button { padding-right: 8px; font-size: 12px; }.pdm-permission-matrix { padding-left: 10px; } }
</style>
