<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { addProjectManagerNote, createProjectTodo } from '../api'
import { ElMessage } from '../statusMessage'
import type { AuditEntry, PdmUser, ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()
const props = withDefaults(defineProps<{ entries: AuditEntry[]; projects?: Array<Pick<ProjectSummary, 'id' | 'code' | 'name'>>; users?: PdmUser[]; title?: string; description?: string; hideHeading?: boolean; projectId?: string; token?: string; canAddManagerNote?: boolean; projectRecordsOnly?: boolean }>(), {
  title: '审计查询',
  description: '查看、下载、恢复、存档、审批和发布操作均保留审计记录。',
  hideHeading: false,
})
const emit = defineEmits<{ refresh: [] }>()
const projectRecordDrawerOpen = ref(false)
const managerNote = ref('')
const savingManagerNote = ref(false)
const noteProjectId = ref(props.projectId ?? '')
const todoDueDate = ref<string>()
const todoRecipients = ref<string[]>([])
const projectFilter = ref('')
const recordTypeFilter = ref('')
const query = ref('')
const projectById = computed(() => new Map((props.projects ?? []).map(project => [project.id, project])))
const noteProjects = computed(() => props.projects?.length ? props.projects : [])
const todoRecipientCandidates = computed(() => (props.users ?? []).filter(user => user.isActive)
  .sort((left, right) => left.displayName.localeCompare(right.displayName, 'zh-CN')))
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

function openProjectRecordDrawer() {
  managerNote.value = ''
  todoDueDate.value = undefined
  todoRecipients.value = []
  projectRecordDrawerOpen.value = true
}

async function saveProjectRecord() {
  const content = managerNote.value.trim()
  if (!noteProjectId.value || !props.token || !content) return ElMessage.warning('请输入项目备注')
  if (todoDueDate.value && !todoRecipients.value.length) return ElMessage.warning('设置待办截止日期时请选择接收人')
  savingManagerNote.value = true
  try {
    await addProjectManagerNote(noteProjectId.value, content, props.token)
    if (todoRecipients.value.length) await createProjectTodo(noteProjectId.value, { content, dueDate: todoDueDate.value, recipientUsernames: todoRecipients.value }, props.token)
    managerNote.value = ''
    projectRecordDrawerOpen.value = false
    ElMessage.success(todoRecipients.value.length ? `项目记录已保存，待办已推送给 ${todoRecipients.value.length} 人` : '项目经理备注已记录')
    emit('refresh')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '项目经理备注保存失败') }
  finally { savingManagerNote.value = false }
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="审计查询">
    <header v-if="!hideHeading" class="pdm-manager-heading"><div><h2>{{ title }}</h2><p>{{ description }}</p></div><button type="button" class="pdm-secondary-action" @click="$emit('refresh')">刷新</button></header>
    <div v-if="projectRecordsOnly" class="pdm-audit-record-filters" aria-label="项目记录筛选">
      <button v-if="projectId && token && canAddManagerNote" type="button" class="pdm-primary-action" @click="openProjectRecordDrawer">新增记录</button>
      <el-select v-model="projectFilter" clearable placeholder="全部项目" aria-label="筛选项目"><el-option v-for="project in noteProjects" :key="project.id" :label="`${project.code} · ${project.name}`" :value="project.id" /></el-select>
      <el-select v-model="recordTypeFilter" clearable placeholder="全部记录类型" aria-label="筛选记录类型"><el-option label="项目经理备注" value="项目经理备注" /><el-option label="待办推送" value="待办推送" /><el-option label="BOM发布" value="BOM发布" /><el-option label="任务完成" value="任务完成" /></el-select>
      <input v-model="query" type="search" placeholder="搜索内容、人员或项目" aria-label="搜索项目记录" />
    </div>
    <div class="pdm-table-scroll"><table class="pdm-edit-table"><thead><tr v-if="projectRecordsOnly"><th>时间</th><th>项目</th><th>人员</th><th>记录类型</th><th>内容</th></tr><tr v-else><th>时间</th><th>人员</th><th>操作</th><th>对象</th><th>详情</th></tr></thead><tbody><tr v-for="entry in displayedEntries" :key="entry.id"><template v-if="projectRecordsOnly"><td>{{ new Date(entry.occurredAt).toLocaleString() }}</td><td>{{ projectById.get(entry.projectId ?? '')?.code ?? '—' }}<small v-if="projectById.get(entry.projectId ?? '')">{{ projectById.get(entry.projectId ?? '')?.name }}</small></td><td>{{ displayUserName(entry.actor) }}</td><td>{{ projectRecordLabel(entry) }}</td><td v-if="entry.action === 'project.todo.create'">待办人：{{ todoRecordDetail(entry).recipients }} · 截止日期：{{ todoRecordDetail(entry).dueDate }}<small>{{ todoRecordDetail(entry).content }}</small></td><td v-else>{{ entry.detail }}</td></template><template v-else><td>{{ new Date(entry.occurredAt).toLocaleString() }}</td><td>{{ displayUserName(entry.actor) }}</td><td>{{ entry.action }}</td><td>{{ entry.entityType }} · {{ entry.entityId }}</td><td>{{ entry.detail }}</td></template></tr><tr v-if="displayedEntries.length === 0"><td :colspan="projectRecordsOnly ? 5 : 5" class="pdm-empty-info">暂无项目记录。</td></tr></tbody></table></div>
    <el-drawer v-model="projectRecordDrawerOpen" class="pdm-project-record-drawer" title="新增项目记录" size="520px" append-to-body>
      <label v-if="noteProjects.length > 1" class="pdm-dialog-field">项目<el-select v-model="noteProjectId" aria-label="备注项目" placeholder="选择项目"><el-option v-for="project in noteProjects" :key="project.id" :label="`${project.code} · ${project.name}`" :value="project.id" /></el-select></label>
      <label class="pdm-dialog-field">备注内容<textarea v-model="managerNote" class="pdm-manager-note-dialog__input" maxlength="1000" rows="5" placeholder="记录进度、风险或需要跟进的事项" aria-label="项目备注内容" /></label>
      <section class="pdm-project-todo-settings" aria-label="待办设置">
        <header><strong>待办设置</strong><span>保存记录时将上述内容推送给指定人员</span></header>
        <div>
          <label class="pdm-dialog-field">截止日期（可选）<el-date-picker v-model="todoDueDate" type="date" value-format="YYYY-MM-DD" placeholder="不设截止日期" aria-label="待办截止日期" /></label>
          <label class="pdm-dialog-field">接收人<el-select v-model="todoRecipients" multiple filterable collapse-tags collapse-tags-tooltip placeholder="选择接收人" aria-label="待办接收人"><el-option v-for="user in todoRecipientCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        </div>
      </section>
      <template #footer><el-button @click="projectRecordDrawerOpen=false">取消</el-button><el-button type="primary" :loading="savingManagerNote" @click="saveProjectRecord">保存记录</el-button></template>
    </el-drawer>
  </section>
</template>
