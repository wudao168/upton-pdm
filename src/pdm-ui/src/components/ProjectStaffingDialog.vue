<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import UserSelect from './UserSelect'
import { PdmApiError } from '../api'
import { canChangeProjectManager, divisionStaffingUsers } from '../projectStaffing'
import type { MainProjectStaffingInput, OrganizationDirectory, ProjectSummary } from '../types'
const props = defineProps<{ directory: OrganizationDirectory; currentUsername: string; administrator: boolean; onSave: (id: string, input: MainProjectStaffingInput) => Promise<ProjectSummary> }>()
const project = ref<ProjectSummary | null>(null)
const open = ref(false), saving = ref(false), error = ref('')
const collaborativeOnly = ref(false)
const form = reactive<MainProjectStaffingInput>({ primaryProjectManager: '', collaborativeProjectManagers: [], designLeads: [] })
const candidates = computed(() => divisionStaffingUsers(props.directory, project.value?.executionUnitId))
const canChangeManager = computed(() => canChangeProjectManager(props.currentUsername, project.value))
const collaboratorCandidates = computed(() => candidates.value.filter(user => user.username.toLowerCase() !== form.primaryProjectManager.toLowerCase()))
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
  const activeAccounts = new Set(candidates.value.map(user => user.username.toLowerCase()))
  if (!activeAccounts.has(form.primaryProjectManager.toLowerCase())) {
    error.value = canChangeManager.value ? '现任项目经理已不在执行事业部，请重新选择项目经理。' : '现任项目经理已不在执行事业部。请由有权更换项目经理的人员处理。'
    return
  }
  if ([...form.collaborativeProjectManagers, ...form.designLeads].some(username => !activeAccounts.has(username.toLowerCase()))) {
    error.value = collaborativeOnly.value
      ? '部分已选人员已不在执行事业部，请打开完整的人员分配更新。'
      : '部分已选人员已不在执行事业部，请移除或重新选择。'
    return
  }
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
      <label v-if="!collaborativeOnly">项目经理<UserSelect v-model="form.primaryProjectManager" aria-label="项目经理" :disabled="saving || !canChangeManager"><el-option v-for="user in candidates" :key="user.username" :value="user.username" :label="user.displayName || '姓名待补充'" /></UserSelect></label>
      <label :class="{ 'is-wide': collaborativeOnly }">协同项目经理<UserSelect v-model="form.collaborativeProjectManagers" aria-label="协同项目经理" multiple clearable collapse-tags collapse-tags-tooltip :disabled="saving"><el-option v-for="user in collaboratorCandidates" :key="user.username" :value="user.username" :label="user.displayName || '姓名待补充'" /></UserSelect></label>
      <label v-if="!collaborativeOnly">主设计<UserSelect v-model="form.designLeads" aria-label="主设计" multiple clearable :disabled="saving"><el-option v-for="user in candidates" :key="user.username" :value="user.username" :label="user.displayName || '姓名待补充'" /></UserSelect></label>
      <p v-if="!candidates.length" class="is-wide">当前执行事业部暂无可分配的启用人员。</p>
      <p v-else-if="!collaborativeOnly && !canChangeManager" class="is-wide">当前账号无权更换自己的项目经理分配，仍可维护协同项目经理和主设计。</p>
      <p v-if="error" class="is-wide" role="alert">{{ error }}</p>
    </form>
    <template #footer><el-button :disabled="saving" @click="open = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存</el-button></template>
  </el-dialog>
</template>
