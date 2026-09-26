<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { addProjectManagerNote } from '../api'
import { ElMessage } from '../statusMessage'
import type { AuditEntry, ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()
const props = withDefaults(defineProps<{ entries: AuditEntry[]; projects?: Array<Pick<ProjectSummary, 'id' | 'code' | 'name'>>; title?: string; description?: string; hideHeading?: boolean; projectId?: string; token?: string; canAddManagerNote?: boolean; projectRecordsOnly?: boolean }>(), {
  title: '审计查询',
  description: '查看、下载、恢复、存档、审批和发布操作均保留审计记录。',
  hideHeading: false,
})
const emit = defineEmits<{ refresh: [] }>()
const managerNote = ref('')
const savingManagerNote = ref(false)
const noteProjectId = ref(props.projectId ?? '')
const projectFilter = ref('')
const recordTypeFilter = ref('')
const query = ref('')
const projectById = computed(() => new Map((props.projects ?? []).map(project => [project.id, project])))
const noteProjects = computed(() => props.projects?.length ? props.projects : [])
const displayedEntries = computed(() => {
  const normalizedQuery = query.value.trim().toLocaleLowerCase()
  return (props.projectRecordsOnly ? props.entries.filter(isProjectRecord) : props.entries)
    .filter(entry => !projectFilter.value || entry.projectId === projectFilter.value)
    .filter(entry => !recordTypeFilter.value || projectRecordLabel(entry) === recordTypeFilter.value)
    .filter(entry => {
      if (!normalizedQuery) return true
      const project = projectById.value.get(entry.projectId ?? '')
      return [entry.detail, entry.actor, project?.code, project?.name]
        .some(value => value?.toLocaleLowerCase().includes(normalizedQuery))
    })
})

watch(() => [props.projectId, props.projects?.map(project => project.id).join(',')], () => {
  if (!noteProjects.value.some(project => project.id === noteProjectId.value)) noteProjectId.value = props.projectId ?? noteProjects.value[0]?.id ?? ''
}, { immediate: true })

function isProjectRecord(entry: AuditEntry) {
  return entry.action === 'project.manager-note'
    || entry.action === 'project.todo.create'
    || entry.action === 'release-package.publish'
    || entry.action === 'project-plan.bom-release'
    || (entry.action === 'project-plan.progress' && /；100%$/.test(entry.detail))
}

function projectRecordLabel(entry: AuditEntry) {
  if (entry.action === 'project.manager-note') return '项目经理备注'
  if (entry.action === 'project.todo.create') return '待办推送'
  if (entry.action === 'project-plan.progress') return '任务完成'
  return 'BOM发布'
}

function todoRecordDetail(entry: AuditEntry) {
  const prefix = '创建待办并推送给 '
  if (!entry.detail.startsWith(prefix)) return { recipients: '—', dueDate: '未设置', content: entry.detail }
  const [recipientAndDate, ...contentParts] = entry.detail.slice(prefix.length).split('：')
  const dueDate = /；截止 (\d{4}-\d{2}-\d{2})$/.exec(recipientAndDate)
  return {
    recipients: (dueDate ? recipientAndDate.slice(0, dueDate.index) : recipientAndDate) || '—',
    dueDate: dueDate?.[1] ?? '未设置',
    content: contentParts.join('：') || '—',
  }
}

async function saveManagerNote() {
  const content = managerNote.value.trim()
  if (!noteProjectId.value || !props.token || !content) return ElMessage.warning('请输入项目备注')
  savingManagerNote.value = true
  try {
    await addProjectManagerNote(noteProjectId.value, content, props.token)
    managerNote.value = ''
    ElMessage.success('项目经理备注已记录')
    emit('refresh')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '项目经理备注保存失败') }
  finally { savingManagerNote.value = false }
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="审计查询">
    <header v-if="!hideHeading" class="pdm-manager-heading"><div><h2>{{ title }}</h2><p>{{ description }}</p></div><button type="button" class="pdm-secondary-action" @click="$emit('refresh')">刷新</button></header>
    <form v-if="projectId && token && canAddManagerNote" class="pdm-audit-note-editor" @submit.prevent="saveManagerNote">
      <el-select v-if="noteProjects.length > 1" v-model="noteProjectId" aria-label="备注项目" placeholder="选择项目"><el-option v-for="project in noteProjects" :key="project.id" :label="`${project.code} · ${project.name}`" :value="project.id" /></el-select>
      <textarea v-model="managerNote" maxlength="1000" rows="3" placeholder="记录项目备注" /><button type="submit" class="pdm-primary-action" :disabled="savingManagerNote">{{ savingManagerNote ? '保存中…' : '记录备注' }}</button>
    </form>
    <div v-if="projectRecordsOnly" class="pdm-audit-record-filters" aria-label="项目记录筛选">
      <el-select v-model="projectFilter" clearable placeholder="全部项目" aria-label="筛选项目"><el-option v-for="project in noteProjects" :key="project.id" :label="`${project.code} · ${project.name}`" :value="project.id" /></el-select>
      <el-select v-model="recordTypeFilter" clearable placeholder="全部记录类型" aria-label="筛选记录类型"><el-option label="项目经理备注" value="项目经理备注" /><el-option label="待办推送" value="待办推送" /><el-option label="BOM发布" value="BOM发布" /><el-option label="任务完成" value="任务完成" /></el-select>
      <input v-model="query" type="search" placeholder="搜索内容、人员或项目" aria-label="搜索项目记录" />
    </div>
    <div class="pdm-table-scroll"><table class="pdm-edit-table"><thead><tr v-if="projectRecordsOnly"><th>时间</th><th>项目</th><th>人员</th><th>记录类型</th><th>内容</th></tr><tr v-else><th>时间</th><th>人员</th><th>操作</th><th>对象</th><th>详情</th></tr></thead><tbody><tr v-for="entry in displayedEntries" :key="entry.id"><template v-if="projectRecordsOnly"><td>{{ new Date(entry.occurredAt).toLocaleString() }}</td><td>{{ projectById.get(entry.projectId ?? '')?.code ?? '—' }}<small v-if="projectById.get(entry.projectId ?? '')">{{ projectById.get(entry.projectId ?? '')?.name }}</small></td><td>{{ displayUserName(entry.actor) }}</td><td>{{ projectRecordLabel(entry) }}</td><td v-if="entry.action === 'project.todo.create'">待办人：{{ todoRecordDetail(entry).recipients }} · 截止日期：{{ todoRecordDetail(entry).dueDate }}<small>{{ todoRecordDetail(entry).content }}</small></td><td v-else>{{ entry.detail }}</td></template><template v-else><td>{{ new Date(entry.occurredAt).toLocaleString() }}</td><td>{{ displayUserName(entry.actor) }}</td><td>{{ entry.action }}</td><td>{{ entry.entityType }} · {{ entry.entityId }}</td><td>{{ entry.detail }}</td></template></tr><tr v-if="displayedEntries.length === 0"><td :colspan="projectRecordsOnly ? 5 : 5" class="pdm-empty-info">暂无项目记录。</td></tr></tbody></table></div>
  </section>
</template>
