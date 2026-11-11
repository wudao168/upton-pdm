<script setup lang="ts">
import PersonName from './PersonName.vue'
import { useUserDisplayName as usePersonDisplayName } from '../userDisplay'
const displayUserName = usePersonDisplayName()
import { computed, watch } from 'vue'
import type { ApprovalNoteRevision, ApprovalParticipants } from '../approvalParticipants'
import ParallelApprovalStatus from './ParallelApprovalStatus.vue'

const props = defineProps<{
  identity: string
  label: string
  editable: boolean
  nodes: Array<{ noteHistory?: ApprovalNoteRevision[]; departments?: ApprovalParticipants['departments'] } | null | undefined>
}>()
const draft = defineModel<string>()
const history = computed(() => props.nodes.flatMap(node => node?.noteHistory ?? [])
  .sort((a, b) => Date.parse(a.at) - Date.parse(b.at)))
const latest = computed(() => history.value.at(-1))
watch(() => [props.identity, latest.value?.at, props.editable], () => { draft.value = undefined })
</script>

<template>
  <template v-for="(node, index) in nodes" :key="index"><ParallelApprovalStatus :departments="node?.departments" /></template>
  <div v-if="editable || latest" class="approval-note">
    <label v-if="editable">{{ label }}<textarea :value="draft ?? latest?.text ?? ''" rows="2" maxlength="500"
      @input="draft = ($event.target as HTMLTextAreaElement).value" /></label>
    <p v-else>{{ label }}：{{ latest?.text || '已清空' }}</p>
    <details v-if="history.length"><summary>修改记录（{{ history.length }}）</summary>
      <p v-for="(item, index) in history" :key="index"><PersonName :name="displayUserName(item.actor)" /> · {{ new Date(item.at).toLocaleString() }} · {{ item.text || '已清空' }}</p>
    </details>
  </div>
</template>

<style scoped>
.approval-note{min-width:0;flex-basis:100%;font-size:13px;color:var(--pdm-text)}
.approval-note label{display:grid;gap:4px}.approval-note textarea{box-sizing:border-box;width:100%;padding:6px 8px;font:inherit;color:inherit;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface)}
.approval-note p{margin:5px 0;overflow-wrap:anywhere}.approval-note details{color:var(--pdm-muted)}
</style>
