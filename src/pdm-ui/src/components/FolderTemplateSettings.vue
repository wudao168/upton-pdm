<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import type { FolderPermissionRule, PdmUser, ProjectFolderTemplateNode } from '../types'

const props = defineProps<{ nodes: ProjectFolderTemplateNode[]; users: PdmUser[]; pending: boolean; onSave: (nodes: ProjectFolderTemplateNode[]) => Promise<ProjectFolderTemplateNode[]> }>()
const draft = ref<ProjectFolderTemplateNode[]>([])
const permissionOpen = ref(false)
const editingKey = ref('')
const permissionRows = ref<FolderPermissionRule[]>([])
const roles = ['Engineer', 'PlanningManager', 'ProcessReviewer', 'Approver', 'ProductionViewer', 'Administrator']
const accessOptions = [
  { value: 1, label: '查看' }, { value: 2, label: '下载' }, { value: 4, label: '上传' }, { value: 8, label: '编辑' },
  { value: 16, label: '删除' }, { value: 32, label: '管理权限' }, { value: 64, label: '发布' },
]
watch(() => props.nodes, value => { draft.value = value.map(item => ({ ...item, permissions: item.permissions.map(rule => ({ ...rule })) })) }, { immediate: true, deep: true })
const purposeLabel: Record<string, string> = { MechanicalRoot: '机械根目录', ElectricalRoot: '电气根目录', ProjectContainer: '项目目录', Release: '发布目录', Standard: '资料目录' }
async function save() { try { await props.onSave(draft.value); ElMessage.success('文件夹模板已保存') } catch (error) { ElMessage.error(error instanceof Error ? error.message : '模板保存失败') } }
function editPermissions(node: ProjectFolderTemplateNode) { editingKey.value = node.folderKey; permissionRows.value = node.permissions.map(rule => ({ ...rule })); permissionOpen.value = true }
function addPermission() { permissionRows.value.push({ principalType: 'Role', principalKey: 'Engineer', access: 3 }) }
function accessValues(rule: FolderPermissionRule) { return accessOptions.filter(item => (rule.access & item.value) === item.value).map(item => item.value) }
function setAccess(rule: FolderPermissionRule, values: number[]) { rule.access = values.reduce((mask, value) => mask | value, 0) }
function applyPermissions() { const node = draft.value.find(item => item.folderKey === editingKey.value); if (node) node.permissions = permissionRows.value.map(rule => ({ ...rule })); permissionOpen.value = false }
</script>
<template>
  <section class="pdm-project-manager pdm-template-settings">
    <header class="pdm-pagebar"><div><div class="pdm-breadcrumb">系统管理 <span>/</span> 文件夹模板</div><h1>项目文件夹模板</h1><p>新建项目自动应用；主项目目录固定为“项目号-0”，子项目目录固定为子项目号，机械和电气采用同一规则。</p></div><button type="button" class="pdm-primary-action" :disabled="pending" @click="save">保存模板</button></header>
    <section class="pdm-panel"><table class="pdm-data-table"><thead><tr><th>上级</th><th>目录名称</th><th>用途</th><th>顺序</th><th>继承上级权限</th><th>默认权限</th></tr></thead><tbody><tr v-for="node in draft" :key="node.folderKey"><td>{{ node.parentKey || '项目号' }}</td><td><el-input v-model="node.name" :disabled="node.purpose === 'ProjectContainer'" /></td><td>{{ purposeLabel[node.purpose] }}</td><td><el-input-number v-model="node.sortOrder" :min="0" :max="999" /></td><td><el-switch v-model="node.inheritPermissions" /></td><td><button type="button" class="pdm-secondary-action" @click="editPermissions(node)">{{ node.permissions.length ? `${node.permissions.length}条权限` : '设置权限' }}</button></td></tr></tbody></table></section>
  </section>
  <el-dialog v-model="permissionOpen" title="模板默认权限" width="760px">
    <p class="pdm-dialog-help">模板权限用于未单独设置权限的项目目录；项目内仍可逐个目录覆盖。</p>
    <div class="pdm-permission-list"><div v-for="(rule,index) in permissionRows" :key="rule.id || index" class="pdm-permission-row"><el-select v-model="rule.principalType" style="width:100px"><el-option label="角色" value="Role" /><el-option label="用户" value="User" /></el-select><el-select v-if="rule.principalType === 'Role'" v-model="rule.principalKey" style="width:165px"><el-option v-for="role in roles" :key="role" :label="role" :value="role" /></el-select><el-select v-else v-model="rule.principalKey" filterable style="width:165px"><el-option v-for="user in users" :key="user.username" :label="`${user.displayName} (${user.username})`" :value="user.username" /></el-select><el-checkbox-group :model-value="accessValues(rule)" @update:model-value="setAccess(rule, $event as number[])"><el-checkbox v-for="item in accessOptions" :key="item.value" :value="item.value">{{ item.label }}</el-checkbox></el-checkbox-group><button type="button" class="pdm-text-danger" @click="permissionRows.splice(index,1)">移除</button></div></div>
    <button type="button" class="pdm-secondary-action" @click="addPermission">添加权限主体</button>
    <template #footer><button type="button" class="pdm-secondary-action" @click="permissionOpen=false">取消</button><button type="button" class="pdm-primary-action" @click="applyPermissions">应用到模板草稿</button></template>
  </el-dialog>
</template>
