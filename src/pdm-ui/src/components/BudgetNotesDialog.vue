<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { addProjectBudgetNote } from '../api'
import type { BudgetNote, ProjectBudget } from '../projectBudget'
import { useUserDisplayName } from '../userDisplay'
import { ElMessage } from '../statusMessage'
const props = defineProps<{ modelValue: boolean; projectId: string; category: string; title: string; notes: BudgetNote[]; rowVersion: number; token?: string; busy?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; added: [result: ProjectBudget]; saving: [value: boolean] }>()
const displayUserName = useUserDisplayName()
const content = ref('')
const saving = ref(false)
const history = computed(() => props.notes.filter(note => note.category === props.category).slice().reverse())
watch(() => [props.modelValue, props.projectId, props.category], () => { content.value = '' })
async function add() {
  if (!props.token || saving.value || props.busy || !content.value.trim()) return
  const projectId = props.projectId
  saving.value = true; emit('saving', true)
  try {
    const result = await addProjectBudgetNote(projectId, { category: props.category, content: content.value, expectedRowVersion: props.rowVersion }, props.token)
    if (props.projectId === projectId) { emit('added', result); content.value = ''; ElMessage.success('备注已添加') }
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '备注添加失败') }
  finally { saving.value = false; emit('saving', false) }
}
</script>
<template>
  <el-dialog :model-value="modelValue" :title="title" width="600px" :close-on-click-modal="false" :close-on-press-escape="!saving" :show-close="!saving" @update:model-value="(value: boolean) => emit('update:modelValue', value)">
    <div class="pdm-budget__note-history">
      <article v-for="note in history" :key="note.id"><header><span>{{ displayUserName(note.createdBy, '历史备注') }}</span><time>{{ note.createdAt ? new Date(note.createdAt).toLocaleString('zh-CN') : '时间未记录' }}</time></header><p>{{ note.content }}</p></article>
      <p v-if="!history.length" class="pdm-budget__note-empty">暂无备注</p>
    </div>
    <el-input v-model="content" type="textarea" :rows="3" maxlength="500" show-word-limit :disabled="saving" placeholder="输入新备注" aria-label="新备注" />
    <template #footer><button class="pdm-secondary-action" :disabled="saving" @click="emit('update:modelValue', false)">关闭</button><button class="pdm-primary-action" :disabled="saving || busy || !content.trim()" @click="add">{{ saving ? '添加中…' : '添加备注' }}</button></template>
  </el-dialog>
</template>
