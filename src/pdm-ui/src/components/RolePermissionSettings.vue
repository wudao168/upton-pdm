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
const copySources = computed(() => roles.value.filter(role => !role.isSystemAdministrator))
const deletable = computed(() => props.canEdit && selected.value && !selected.value.isSystem && selected.value.userCount === 0)

function selectRole(role: RolePermissionSettings) {
  selectedRole.value = role.role
  draft.value = [...role.permissions]
}

function openPermissions(role: RolePermissionSettings) {
  selectRole(role)
  permissionDialog.value = true
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

async function save() {
  if (!selected.value || !editable.value) return
  try {
    const directory = await props.onSave(selected.value.role, draft.value)
    const saved = directory.roles.find(role => role.role === selected.value?.role)
    if (saved) selectRole(saved)
    permissionDialog.value = false
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

    <el-dialog v-model="permissionDialog" :title="`${selected?.name ?? ''} · 权限设置`" width="1040px" class="pdm-role-permission-dialog">
      <template v-if="selected">
        <div class="pdm-role-dialog-summary"><div><strong>{{ selected.name }}</strong><span>{{ selected.role }}</span></div><p>{{ selected.description }}</p><small>{{ selected.isSystem ? '系统角色' : '自定义角色' }} · {{ selected.userCount }} 个用户</small></div>
        <p class="pdm-role-directory-note">权限模块由系统功能目录自动生成；当前共 {{ moduleCount }} 个模块、{{ permissionCount }} 项权限，后续新增模块会自动加入。</p>
        <div class="pdm-permission-groups">
          <section v-for="group in groups" :key="group.name" class="pdm-permission-group">
            <header><h3>{{ group.name }}</h3><label v-if="editable"><input type="checkbox" :checked="group.permissions.every(item => hasPermission(item.code))" @change="setGroup(group.permissions, ($event.target as HTMLInputElement).checked)">全选本组</label></header>
            <div class="pdm-permission-grid">
              <label v-for="permission in group.permissions" :key="permission.code" class="pdm-permission-card" :class="{ 'is-checked': hasPermission(permission.code), 'is-sensitive': permission.sensitive }">
                <input type="checkbox" :checked="hasPermission(permission.code)" :disabled="!editable" @change="setPermission(permission.code, ($event.target as HTMLInputElement).checked)">
                <span><strong>{{ permission.name }}<em v-if="permission.sensitive">敏感</em></strong><small>{{ permission.description || permission.code }}</small><code>{{ permission.code }}</code></span>
              </label>
            </div>
          </section>
          <p v-if="!groups.length" class="pdm-empty-info">当前分类暂无可配置权限。</p>
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
.pdm-role-dialog-summary { display: grid; grid-template-columns: minmax(180px, auto) minmax(0, 1fr) auto; align-items: center; gap: 16px; padding: 0 2px 14px; border-bottom: 1px solid var(--pdm-border); }
.pdm-role-dialog-summary > div { display: flex; align-items: baseline; gap: 10px; }.pdm-role-dialog-summary > div span, .pdm-role-dialog-summary small { color: var(--pdm-muted); }.pdm-role-dialog-summary p { margin: 0; color: var(--pdm-muted); }
.pdm-role-directory-note { margin: 12px 2px 4px; color: var(--pdm-muted); font-size: 12px; }
.pdm-role-permission-dialog :deep(.el-dialog__body) { max-height: 68vh; overflow: auto; }
.pdm-role-permission-dialog :deep(.el-dialog__footer) { display: flex; align-items: center; gap: 8px; }
.pdm-role-dialog-footer-spacer { flex: 1; }
.pdm-danger-action { display: inline-flex; min-height: 34px; align-items: center; gap: 5px; border: 1px solid #f2c5c0; border-radius: 5px; padding: 0 11px; background: #fff7f6; color: #c8473d; cursor: pointer; }.pdm-danger-action:disabled { cursor: not-allowed; opacity: .45; }
.pdm-role-create-form { display: grid; gap: 14px; }.pdm-role-create-form label { display: grid; gap: 7px; color: #435066; font-size: 13px; }.pdm-role-create-form input, .pdm-role-create-form select, .pdm-role-create-form textarea { border: 1px solid #d7dee9; border-radius: 6px; padding: 9px 10px; background: #fff; color: var(--pdm-text); }.pdm-role-create-form small { color: var(--pdm-muted); line-height: 1.6; }
@media (max-width: 900px) { .pdm-role-directory { padding: 12px; }.pdm-role-dialog-summary { grid-template-columns: 1fr; gap: 6px; }.pdm-role-permission-dialog :deep(.el-dialog) { width: calc(100vw - 24px) !important; } }
</style>
