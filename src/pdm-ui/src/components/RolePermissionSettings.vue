<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { LockKeyhole, ShieldCheck } from '@lucide/vue'
import type { PermissionDefinition, RolePermissionDirectory, RolePermissionSettings } from '../types'

const props = defineProps<{
  directory: RolePermissionDirectory
  canEdit: boolean
  pending: boolean
  onSave: (role: string, permissions: string[]) => Promise<RolePermissionDirectory>
}>()

const selectedRole = ref('')
const draft = ref<string[]>([])

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
const editable = computed(() => props.canEdit && !selected.value?.isSystemAdministrator)

function selectRole(role: RolePermissionSettings) {
  selectedRole.value = role.role
  draft.value = [...role.permissions]
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
  if (!checked && code === 'project.view') [...next].filter(item => item.startsWith('project.') || ['document.edit', 'bom.edit', 'release.manage', 'approval.decide'].includes(item)).forEach(item => next.delete(item))
  if (!checked && code === 'project.content.view') ['document.edit', 'bom.edit', 'release.manage', 'approval.decide'].forEach(item => next.delete(item))
  if (!checked && code === 'system.role.view') next.delete('system.role.edit')
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
    ElMessage.success('角色权限已保存并立即生效')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '角色权限保存失败')
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
    <header class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">系统管理 <span>/</span> 角色权限</div><h1>角色权限设置</h1><p>功能权限决定账号可以执行的操作，项目岗位与组织分配决定可操作的数据范围。</p></div>
    </header>
    <div class="pdm-role-settings">
      <aside class="pdm-panel pdm-role-list" aria-label="角色列表">
        <button v-for="role in roles" :key="role.role" type="button" :class="{ 'is-active': role.role === selectedRole }" @click="selectRole(role)">
          <span><ShieldCheck :size="17" /><strong>{{ role.name }}</strong></span><small>{{ role.permissions.length }} 项权限</small>
        </button>
      </aside>
      <section v-if="selected" class="pdm-panel pdm-role-permissions">
        <header class="pdm-manager-heading">
          <div><h2>{{ selected.name }}</h2><p>{{ selected.description }}</p></div>
          <button v-if="editable" type="button" class="pdm-primary-action" :disabled="pending" @click="save">{{ pending ? '保存中…' : '保存权限' }}</button>
          <span v-else class="pdm-role-lock"><LockKeyhole :size="15" />{{ selected.isSystemAdministrator ? '系统管理员固定拥有全部权限' : '当前账号只有查看权限' }}</span>
        </header>
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
        </div>
      </section>
      <section v-else class="pdm-panel pdm-workspace-state"><h2>暂无角色</h2><p>请确认角色权限目录已初始化。</p></section>
    </div>
  </section>
</template>
