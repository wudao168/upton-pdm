<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { getProjectPermissionSettings, updateProjectPermissionSettings } from '../api'
import type { ProjectPermissionSettings } from '../types'

const props = defineProps<{ token: string; canEdit: boolean }>()
const positions = [
  { key: 'MainManager', name: '主项目经理／协同项目经理', description: '负责的主项目及全部子项目' },
  { key: 'ChildManager', name: '子项目负责人', description: '仅负责的子项目' },
  { key: 'MainDesigner', name: '主设', description: '负责的主项目及全部子项目' },
  { key: 'Engineer', name: '执行工程师', description: '明确分配的项目' },
  { key: 'Other', name: '其他人员', description: '同公司其他项目' },
] as const
const groups = [
  { name: '预算', permissions: [{ code: 'project.budget.view', name: '查看项目预算' }] },
  { name: '项目管理', permissions: [
    { code: 'project.edit', name: '编辑项目信息' },
    { code: 'project.delete', name: '删除空项目' },
    { code: 'project.child.create', name: '创建子项目' },
    { code: 'project.designer.assign', name: '分配执行工程师' },
  ] },
  { name: 'BOM', permissions: [
    { code: 'bom.edit', name: 'BOM料号及关联配置' },
    { code: 'bom.mechanical.edit', name: '标准件／非标件BOM' },
    { code: 'bom.electrical.edit', name: '电气BOM' },
  ] },
  { name: '质量', permissions: [{ code: 'validation-plan.edit', name: '维护质量计划' }] },
  { name: '发布', permissions: [{ code: 'release.manage', name: '创建及管理发布包' }] },
] as const
const settings = ref<ProjectPermissionSettings | null>(null)
const selected = ref<string>('MainManager')
const activeGroup = ref('项目管理')
const dialogOpen = ref(false)
const loading = ref(false)
const saving = ref(false)
const draft = ref<string[]>([])
const currentPosition = computed(() => positions.find(item => item.key === selected.value) ?? positions[0])
const currentGroup = computed(() => groups.find(item => item.name === activeGroup.value) ?? groups[0])
const changed = computed(() => JSON.stringify([...draft.value].sort()) !== JSON.stringify([...(settings.value?.rules[selected.value] ?? [])].sort()))

async function load() {
  loading.value = true
  try { settings.value = await getProjectPermissionSettings(props.token) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '读取项目权限失败') }
  finally { loading.value = false }
}
function open(position: string) {
  selected.value = position
  activeGroup.value = groups[0].name
  draft.value = [...(settings.value?.rules[position] ?? [])]
  dialogOpen.value = true
}
function setPermission(code: string, checked: boolean) {
  draft.value = checked ? [...new Set([...draft.value, code])] : draft.value.filter(item => item !== code)
}
async function save() {
  if (!settings.value || !props.canEdit) return
  saving.value = true
  try {
    settings.value = await updateProjectPermissionSettings({ rules: { ...settings.value.rules, [selected.value]: draft.value } }, props.token)
    dialogOpen.value = false
    ElMessage.success('项目权限已保存并立即生效')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '保存项目权限失败') }
  finally { saving.value = false }
}
watch(() => props.token, token => { if (token) void load() }, { immediate: true })
</script>

<template>
  <section class="pdm-project-manager" aria-label="项目权限设置">
    <header class="pdm-pagebar"><div><div class="pdm-breadcrumb">系统管理 <span>/</span> 项目权限</div><h1>项目权限设置</h1><p>先由角色权限决定可执行的操作，再由项目岗位决定可操作的项目。具体人员在项目分工中指定。</p></div></header>
    <section class="pdm-panel pdm-role-directory" aria-label="项目岗位列表">
      <el-table :data="positions" stripe height="100%" class="pdm-role-table" :empty-text="loading ? '加载中…' : '暂无项目岗位'">
        <el-table-column prop="name" label="项目岗位" min-width="190" />
        <el-table-column prop="description" label="项目范围" min-width="250" />
        <el-table-column label="已授权操作" width="120"><template #default="{ row }">{{ settings?.rules[row.key]?.length ?? 0 }} 项</template></el-table-column>
        <el-table-column label="操作" width="120"><template #default="{ row }"><button type="button" class="pdm-text-action" :disabled="!settings" @click="open(row.key)">权限设置</button></template></el-table-column>
      </el-table>
    </section>
    <el-dialog v-model="dialogOpen" :title="`${currentPosition.name} · 项目权限`" width="min(1100px, 96vw)" class="pdm-role-permission-dialog" top="3vh">
      <p class="pdm-role-directory-note">{{ currentPosition.description }}。此处授权仍需同时满足角色权限；审批仅由实际分配的任务决定。</p>
      <div class="pdm-permission-layout">
        <nav class="pdm-permission-sidebar" aria-label="项目权限模块"><strong>权限分类</strong><button v-for="group in groups" :key="group.name" type="button" :class="{ 'is-active': activeGroup === group.name }" @click="activeGroup = group.name"><span>{{ group.name }}</span><small>{{ group.permissions.length }}</small></button></nav>
        <section class="pdm-permission-matrix" :aria-label="`${currentGroup.name}权限`"><header><h3>{{ currentGroup.name }}权限</h3></header><div class="pdm-permission-table-wrap"><table><thead><tr><th>操作</th><th>允许</th></tr></thead><tbody><tr v-for="permission in currentGroup.permissions" :key="permission.code"><td>{{ permission.name }} <code>{{ permission.code }}</code></td><td><label class="pdm-permission-cell"><input type="checkbox" :checked="draft.includes(permission.code)" :disabled="!canEdit" @change="setPermission(permission.code, ($event.target as HTMLInputElement).checked)"><span>{{ draft.includes(permission.code) ? '允许' : '只读' }}</span></label></td></tr></tbody></table></div></section>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="dialogOpen = false">关闭</button><button v-if="canEdit" type="button" class="pdm-primary-action" :disabled="saving || !changed" @click="save">{{ saving ? '保存中…' : '保存并立即生效' }}</button></template>
    </el-dialog>
  </section>
</template>
