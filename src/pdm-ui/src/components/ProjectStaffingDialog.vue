<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import UserSelect from './UserSelect'
import { PdmApiError } from '../api'
import type { MainProjectStaffingInput, OrganizationDirectory, ProjectSummary } from '../types'
const props = defineProps<{ directory: OrganizationDirectory; currentUsername: string; administrator: boolean; onSave: (id: string, input: MainProjectStaffingInput) => Promise<ProjectSummary> }>()
const project = ref<ProjectSummary | null>(null)
const open = ref(false), saving = ref(false), error = ref('')
const collaborativeOnly = ref(false)
const form = reactive<MainProjectStaffingInput>({ primaryProjectManager: '', collaborativeProjectManagers: [], designLeads: [] })
const candidates = computed(() => {
  const belongs = (id: string) => {
    const visited = new Set<string>()
    while (!visited.has(id)) {
      visited.add(id)
      const unit = props.directory.units.find(item => item.id === id && item.isActive)
      if (!unit) return false
      if (id === project.value?.executionUnitId) return true
      if (!unit.parentUnitId) return false
      id = unit.parentUnitId
    }
    return false
  }
  const members = new Set(props.directory.memberships.filter(item => belongs(item.unitId)).map(item => item.username))
  return props.directory.users.filter(user => user.isActive && members.has(user.username))
})
const canChangeManager = computed(() => props.currentUsername !== project.value?.primaryProjectManager || project.value?.effectiveProjectPermissions?.includes("project.staffing.replace-self") === true)
function configure(value: ProjectSummary, editCollaborativeManagers = false) {
  if (saving.value || !value.canManageMainStaffing) return
  project.value = value
  collaborativeOnly.value = editCollaborativeManagers && Boolean(value.primaryProjectManager)
  const designLeads = value.designLeads?.length ? value.designLeads : value.designLead ? [value.designLead] : []
  Object.assign(form, { primaryProjectManager: value.primaryProjectManager ?? '', collaborativeProjectManagers: [...(value.collaborativeProjectManagers ?? [])], designLeads: [...designLeads] })
  error.value = ''; open.value = true
}
async function save() {
  if (!project.value || saving.value) return
  if (!form.primaryProjectManager) { error.value = '请选择项目经理，主设计可稍后分配。'; return }
  saving.value = true; error.value = ''
  try { await props.onSave(project.value.id, { ...form, collaborativeProjectManagers: [...form.collaborativeProjectManagers], designLeads: [...form.designLeads] }); open.value = false }
  catch (cause) { error.value = cause instanceof PdmApiError ? cause.message : '项目人员分配保存失败，请稍后重试。' }
  finally { saving.value = false }
}
defineExpose({ configure })
</script>
<template>
  <el-dialog v-model="open" :title="collaborativeOnly ? '分配协同项目经理' : '分配项目经理与主设计'" width="560px" append-to-body :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
    <p>{{ project?.code }} · {{ project?.name }}</p>
    <form class="pdm-project-form" @submit.prevent="save">
      <label v-if="!collaborativeOnly">项目经理<UserSelect v-model="form.primaryProjectManager" aria-label="项目经理" :disabled="saving || !canChangeManager"><el-option v-for="user in candidates" :key="user.username" :value="user.username" :label="user.displayName" /></UserSelect></label>
      <label :class="{ 'is-wide': collaborativeOnly }">协同项目经理<UserSelect v-model="form.collaborativeProjectManagers" aria-label="协同项目经理" multiple clearable collapse-tags collapse-tags-tooltip :disabled="saving"><el-option v-for="user in candidates.filter(item => item.username !== form.primaryProjectManager)" :key="user.username" :value="user.username" :label="user.displayName" /></UserSelect></label>
      <label v-if="!collaborativeOnly">主设计<UserSelect v-model="form.designLeads" aria-label="主设计" multiple :disabled="saving"><el-option v-for="user in candidates" :key="user.username" :value="user.username" :label="user.displayName" /></UserSelect></label>
      <p v-if="error" role="alert">{{ error }}</p>
    </form>
    <template #footer><el-button :disabled="saving" @click="open = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存</el-button></template>
  </el-dialog>
</template>
