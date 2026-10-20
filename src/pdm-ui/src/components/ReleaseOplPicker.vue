<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { readProjectOpl } from '../api'
import type { OplView } from '../projectOpl'

const props = withDefaults(defineProps<{ projectId: string; token?: string; modelValue: string[]; disabled?: boolean; readonly?: boolean }>(), { token: '', disabled: false, readonly: false })
const emit = defineEmits<{ 'update:modelValue': [ids: string[]] }>()
const view = ref<OplView>({ rows: [], people: [] })
const query = ref(''), state = ref('未完成'), loading = ref(false), error = ref('')
let generation = 0
async function load() {
  const current = ++generation
  view.value = { rows: [], people: [] }; error.value = ''
  if (!props.projectId || !props.token) return
  loading.value = true
  try { const data = await readProjectOpl(props.projectId, props.token); if (generation === current) view.value = data }
  catch { if (generation === current) error.value = 'OPL问题加载失败，请重试；已选关联仍保留。' }
  finally { if (generation === current) loading.value = false }
}
watch([() => props.projectId, () => props.token], load, { immediate: true })
const candidates = computed(() => view.value.rows.filter(({ issue }) => (props.readonly || issue.status !== '已作废')
  && (!view.value.rootProject || view.value.rootProject.id === props.projectId || !issue.relatedProjectId || issue.relatedProjectId === props.projectId)))
const filtered = computed(() => candidates.value.filter(({ issue }) => {
  const selected = props.modelValue.includes(issue.id)
  if (props.readonly) return selected
  const text = `${issue.number} ${issue.description} ${person(issue.owner)} ${project(issue.relatedProjectId)}`.toLowerCase()
  return (!query.value.trim() || text.includes(query.value.trim().toLowerCase()))
    && (selected || !state.value || state.value === '未完成' && issue.status !== '已完成' || state.value === issue.status)
}))
const missing = computed(() => props.modelValue.filter(id => !candidates.value.some(row => row.issue.id === id)))
const person = (username: string) => view.value.people.find(item => item.username === username)?.displayName || username || '待分派'
const project = (id?: string | null) => view.value.subprojects?.find(item => item.id === id)?.code || (id ? '关联子项目' : '项目整体')
function toggle(id: string, checked: boolean) {
  emit('update:modelValue', checked ? [...new Set([...props.modelValue, id])] : props.modelValue.filter(item => item !== id))
}
</script>

<template>
  <details class="release-opl-picker" aria-label="关联OPL问题">
    <summary>关联 OPL 问题<span>已选 {{ modelValue.length }} 项{{ readonly ? '' : ' · 选填，可多选' }}</span></summary>
    <p class="release-opl-note">每条关联问题均记录本发布包的图纸总数；发布成功后保留历史，问题仍需人工确认完成。</p>
    <div v-if="!readonly" class="release-opl-filters">
      <input v-model="query" aria-label="搜索关联OPL问题" placeholder="搜索编号、问题描述、负责人或子项目">
      <select v-model="state" aria-label="关联OPL问题状态"><option value="未完成">未完成</option><option value="">全部有效状态</option><option value="已完成">已完成</option></select>
    </div>
    <p v-if="loading" role="status">正在加载OPL问题…</p>
    <p v-else-if="error" role="alert">{{ error }} <button type="button" class="pdm-text-action" @click="load">重试</button></p>
    <div v-else class="release-opl-rows">
      <label v-for="{ issue } in filtered" :key="issue.id" :title="issue.description">
        <input v-if="!readonly" type="checkbox" :checked="modelValue.includes(issue.id)" :disabled="disabled" :aria-label="`关联OPL ${issue.number}`" @change="toggle(issue.id, ($event.target as HTMLInputElement).checked)">
        <strong>#{{ issue.number }}</strong><span class="release-opl-description">{{ issue.description }}</span><small>{{ project(issue.relatedProjectId) }} · {{ person(issue.owner) }} · {{ issue.status }}</small>
      </label>
      <p v-if="!filtered.length && !missing.length">暂无符合条件的OPL问题。</p>
      <label v-for="id in missing" :key="id"><input v-if="!readonly" type="checkbox" checked :disabled="disabled" aria-label="移除不可用OPL关联" @change="toggle(id, false)"><span>已关联问题当前不可用，请刷新核对。</span></label>
    </div>
  </details>
</template>

<style scoped>
.release-opl-picker{min-width:0;border:1px solid var(--pdm-border);border-radius:6px;padding:8px;background:var(--pdm-surface)}
summary{cursor:pointer;font-weight:600}summary span{margin-left:12px;font-weight:400;color:var(--pdm-muted)}
.release-opl-note{margin:8px 0;color:var(--pdm-muted)}.release-opl-filters{display:flex;gap:8px;margin-bottom:8px}.release-opl-filters>input{flex:1;min-width:0}.release-opl-filters input,.release-opl-filters select{height:30px;padding:0 8px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text)}
.release-opl-rows{max-height:180px;overflow:auto}.release-opl-rows label{display:flex;align-items:center;gap:8px;padding:6px 0;border-bottom:1px solid var(--pdm-border)}.release-opl-rows input{width:auto;height:auto}.release-opl-description{flex:1;min-width:80px;white-space:pre-wrap;overflow-wrap:anywhere}.release-opl-rows small{color:var(--pdm-muted);max-width:40%;overflow-wrap:anywhere}
@media(max-width:600px){.release-opl-filters{flex-wrap:wrap}.release-opl-rows label{flex-wrap:wrap}.release-opl-rows small{max-width:100%;width:100%;padding-left:24px}}
</style>
